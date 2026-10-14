/*
 * This file is part of ProxiedHosts <https://github.com/StevenJDH/ProxiedHosts>.
 * Copyright (C) 2026 Steven Jenkins De Haro.
 *
 * ProxiedHosts is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * ProxiedHosts is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with ProxiedHosts.  If not, see <http://www.gnu.org/licenses/>.
 */

using ProxiedHosts.Core.Configuration;
using ProxiedHosts.Core.Hosts;
using ProxiedHosts.Core.Infrastructure;
using ProxiedHosts.Core.Logging;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ProxiedHosts.Core.Proxy;

public sealed class ProxyConnectionHandler
{
    private sealed record ConnectionRoute(
        bool IsMapped,
        IPAddress? DestinationAddress,
        int DestinationPort);

    private sealed record ActiveConnection(
        TcpClient Client,
        TcpClient Upstream,
        string Host,
        int Port,
        ConnectionRoute Route);

    private readonly HostMappingProvider _hostMappings;
    private readonly ProxyState _proxyState;
    private readonly ConnectionLogMode _logMode;
    private readonly IProxyLogger _logger;

    private readonly ConcurrentDictionary<TcpClient, ActiveConnection> _activeConnections = new();

    public ProxyConnectionHandler(HostMappingProvider hostMappings, ProxyState proxyState, ConnectionLogMode logMode = ConnectionLogMode.MappedOnly, IProxyLogger? logger = null)
    {
        _hostMappings = hostMappings;
        _proxyState = proxyState;
        _logMode = logMode;
        _logger = logger ?? NullProxyLogger.Instance;
    }

    public async Task HandleClientSafelyAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                await HandleClientAsync(client, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal shutdown.
            }
            catch (IOException)
            {
                // Normal client or upstream disconnect.
            }
            catch (SocketException)
            {
                // Normal client or upstream disconnect.
            }
            catch (ObjectDisposedException)
            {
                // Connection was closed while being processed.
            }
            catch (Exception ex)
            {
                _logger.Error($"Client error: {ex.Message}");
            }
            finally
            {
                _activeConnections.TryRemove(client, out _);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        client.NoDelay = true;

        await using var clientStream = client.GetStream();

        byte[]? requestHeader;

        try
        {
            using var headerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            headerCancellation.CancelAfter(ProxyConfiguration.Instance.HeaderReadTimeout);
            requestHeader = await HttpHeaderReader.ReadAsync(clientStream, ProxyConfiguration.Instance.MaxHeaderBytes, headerCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await WriteErrorAsync(clientStream, 408, "Request Timeout", "Timed out waiting for request headers.", cancellationToken);
            return;
        }

        if (requestHeader is null)
        {
            return;
        }

        if (!ProxyRequest.TryParse(requestHeader, out var request, out var parseError))
        {
            await WriteErrorAsync(clientStream, 400, "Bad Request", parseError ?? "Invalid proxy request.", cancellationToken);
            return;
        }

        var route = ResolveRoute(request.Host, request.Port);

        using var upstream = new TcpClient();
        upstream.NoDelay = true;

        _activeConnections[client] = new ActiveConnection(client, upstream, request.Host, request.Port, route);

        try
        {
            await ConnectUpstreamAsync(upstream, request, route, cancellationToken);
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException)
        {
            await WriteErrorAsync(clientStream, 502, "Bad Gateway",
                $"Could not connect to {request.Host}:{request.Port}.", cancellationToken);
            return;
        }

        await using var upstreamStream = upstream.GetStream();

        if (request.IsConnect)
        {
            var established = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\nProxy-Agent: ProxiedHosts\r\n\r\n");

            await clientStream.WriteAsync(established, cancellationToken);

            // Forward any tunnel data received alongside the CONNECT headers.
            var headerEnd = requestHeader.AsSpan().IndexOf("\r\n\r\n"u8) + 4;

            if (headerEnd < requestHeader.Length)
            {
                await upstreamStream.WriteAsync(requestHeader.AsMemory(headerEnd), cancellationToken);
            }
        }
        else
        {
            var rewrittenHeader = ProxyRequest.RewriteForOriginServer(requestHeader, request);

            await upstreamStream.WriteAsync(rewrittenHeader, cancellationToken);
        }

        await RelayBidirectionalAsync(client, upstream, clientStream, upstreamStream, !request.IsConnect, cancellationToken);
    }

    private async Task ConnectUpstreamAsync(TcpClient upstream, ProxyRequest request, ConnectionRoute route, CancellationToken cancellationToken)
    {
        using var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCancellation.CancelAfter(ProxyConfiguration.Instance.ConnectTimeout);

        try
        {
            if (route.IsMapped)
            {
                if (_logMode is ConnectionLogMode.MappedOnly or ConnectionLogMode.All)
                {
                    _logger.Information($"{request.Method} {request.Host}:{request.Port} -> {route.DestinationAddress}:{route.DestinationPort}");
                }

                await upstream.ConnectAsync(route.DestinationAddress!, route.DestinationPort, connectCancellation.Token);
                return;
            }

            if (_logMode == ConnectionLogMode.All)
            {
                _logger.Information($"{request.Method} {request.Host}:{request.Port} -> DNS");
            }

            await upstream.ConnectAsync(request.Host, request.Port, connectCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Upstream connection timed out.");
        }
    }

    private static async Task RelayBidirectionalAsync(TcpClient client, TcpClient upstream, NetworkStream clientStream, NetworkStream upstreamStream, bool rewriteResponse, CancellationToken cancellationToken)
    {
        using var idleCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idleCancellation.CancelAfter(ProxyConfiguration.Instance.IdleTimeout);

        var clientToUpstream = PumpAndHalfCloseAsync(clientStream, upstreamStream, upstream, idleCancellation);
        var upstreamToClient = PumpAndHalfCloseAsync(upstreamStream, clientStream, client, idleCancellation, rewriteResponse);
        var firstCompleted = await Task.WhenAny(clientToUpstream, upstreamToClient);

        if (!firstCompleted.IsCompletedSuccessfully)
        {
            // Unblock the other direction if a relay fails or is canceled.
            client.Close();
            upstream.Close();
        }

        try
        {
            await Task.WhenAll(clientToUpstream, upstreamToClient);
        }
        catch (OperationCanceledException) when (idleCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Connection was idle for too long.
        }
    }

    private static async Task PumpAndHalfCloseAsync(Stream source, Stream destination, TcpClient destinationClient, CancellationTokenSource idleCancellation, bool rewriteResponse = false)
    {
        if (rewriteResponse)
        {
            await ForwardHttpResponseAsync(source, destination, idleCancellation);
        }
        else
        {
            await PumpAsync(source, destination, idleCancellation);
        }

        try
        {
            // Signal EOF without closing the receiving direction.
            destinationClient.Client.Shutdown(SocketShutdown.Send);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // The destination may already be closed.
        }
    }
    
    private static async Task ForwardHttpResponseAsync(Stream source, Stream destination, CancellationTokenSource idleCancellation)
    {
        var buffered = Array.Empty<byte>();
        var buffer = new byte[4096];
        var cancellationToken = idleCancellation.Token;

        while (true)
        {
            var headerEnd = buffered.AsSpan().IndexOf("\r\n\r\n"u8);

            if (headerEnd >= 0)
            {
                var headerLength = headerEnd + 4;

                if (IsInterimResponse(buffered.AsSpan(0, headerLength)))
                {
                    // Forward 1xx responses unchanged and wait for the final response.
                    await destination.WriteAsync(buffered.AsMemory(0, headerLength), cancellationToken);
                    idleCancellation.CancelAfter(ProxyConfiguration.Instance.IdleTimeout);
                    buffered = buffered[headerLength..];
                    continue;
                }

                var rewritten = HttpHeaderRewriter.RewriteResponse(buffered);

                await destination.WriteAsync(rewritten, cancellationToken);
                idleCancellation.CancelAfter(ProxyConfiguration.Instance.IdleTimeout);

                // Forward the rest of the response body without modification.
                await PumpAsync(source, destination, idleCancellation);
                
                return;
            }

            var maxHeaderBytes = ProxyConfiguration.Instance.MaxHeaderBytes;

            if (buffered.Length >= maxHeaderBytes)
            {
                throw new IOException($"HTTP response header exceeded the {maxHeaderBytes}-byte limit.");
            }

            var read = await source.ReadAsync(buffer, cancellationToken);

            if (read == 0)
            {
                if (buffered.Length > 0)
                {
                    throw new IOException("Connection closed before the HTTP response headers were complete.");
                }

                return;
            }

            var combined = new byte[buffered.Length + read];

            buffered.CopyTo(combined, 0);
            buffer.AsSpan(0, read).CopyTo(combined.AsSpan(buffered.Length));

            buffered = combined;
            idleCancellation.CancelAfter(ProxyConfiguration.Instance.IdleTimeout);
        }
    }

    private static bool IsInterimResponse(ReadOnlySpan<byte> response)
    {
        var lineEnd = response.IndexOf("\r\n"u8);

        if (lineEnd < 0)
        {
            return false;
        }

        var statusLine = Encoding.ASCII.GetString(response[..lineEnd]);
        var parts = statusLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);

        return parts.Length >= 2 && int.TryParse(parts[1], out var statusCode) && statusCode is >= 100 and < 200 and not 101;
    }

    private static async Task PumpAsync(Stream source, Stream destination, CancellationTokenSource idleCancellation)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        var cancellationToken = idleCancellation.Token;

        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);

                if (read == 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

                // Activity in either direction resets the shared timeout.
                idleCancellation.CancelAfter(ProxyConfiguration.Instance.IdleTimeout);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task WriteErrorAsync(Stream stream, int statusCode, string reason, string message, CancellationToken cancellationToken)
    {
        var appName = WebUtility.HtmlEncode(ApplicationMetadata.Instance.Product);
        var safeReason = WebUtility.HtmlEncode(reason);
        var safeMessage = WebUtility.HtmlEncode(message);

        var html = $$"""
                 <!DOCTYPE html>
                 <html lang="en">
                 <head>
                     <meta charset="utf-8">
                     <meta name="viewport" content="width=device-width, initial-scale=1">
                     <title>{{statusCode}} {{safeReason}} | {{appName}}</title>
                     <style>
                         * {
                             box-sizing: border-box;
                         }

                         body {
                             margin: 0;
                             min-height: 100vh;
                             display: grid;
                             place-items: center;
                             padding: 24px;
                             font-family: system-ui, -apple-system, "Segoe UI", sans-serif;
                             color: #e9f1fa;
                             background:
                                 radial-gradient(circle at 20% 20%, #204460 0%, transparent 45%),
                                 radial-gradient(circle at 85% 85%, #123c42 0%, transparent 40%),
                                 #0b1420;
                         }

                         .card {
                             width: min(100%, 620px);
                             padding: clamp(28px, 6vw, 48px);
                             background: #142131;
                             border: 1px solid #304459;
                             border-radius: 24px;
                             box-shadow: 0 24px 70px #0006;
                         }

                         .brand {
                             display: flex;
                             align-items: center;
                             flex-wrap: wrap;
                             gap: 12px;
                             font-size: 18px;
                             font-weight: 700;
                         }

                         .mark {
                             display: grid;
                             place-items: center;
                             width: 42px;
                             height: 42px;
                             border-radius: 12px;
                             background: linear-gradient(135deg, #49d6c4, #448ee5);
                             color: #091c29;
                             font-size: 23px;
                             font-weight: 800;
                         }

                         .tag {
                             margin-left: auto;
                             padding: 4px 10px;
                             border: 1px solid #3a5864;
                             border-radius: 20px;
                             color: #9cb9c4;
                             font-size: 10px;
                             font-weight: 600;
                             letter-spacing: 1px;
                         }

                         .eyebrow {
                             margin: 40px 0 4px;
                             color: #65d7c6;
                             font-size: 11px;
                             font-weight: 700;
                             letter-spacing: 2px;
                             text-transform: uppercase;
                         }

                         .code {
                             font-size: clamp(80px, 18vw, 120px);
                             font-weight: 800;
                             line-height: 1.1;
                             letter-spacing: -6px;
                             color: #68dbc9;
                         }

                         h1 {
                             margin: 8px 0 12px;
                             font-size: clamp(24px, 5vw, 32px);
                             font-weight: 650;
                         }

                         .message {
                             margin: 0;
                             color: #b0c1d1;
                             font-size: 16px;
                             line-height: 1.7;
                             overflow-wrap: anywhere;
                         }

                         .hint {
                             margin-top: 32px;
                             padding: 16px 18px;
                             border: 1px solid #2e4556;
                             border-left: 3px solid #65d7c6;
                             border-radius: 10px;
                             background: #1a2b3c;
                             color: #c4d3df;
                             font-size: 13px;
                             line-height: 1.6;
                         }

                         footer {
                             display: flex;
                             justify-content: space-between;
                             flex-wrap: wrap;
                             gap: 8px;
                             margin-top: 32px;
                             padding-top: 20px;
                             border-top: 1px solid #304459;
                             color: #7892a8;
                             font-size: 12px;
                         }
                     </style>
                 </head>
                 <body>
                     <main class="card">
                         <header class="brand">
                             <span class="mark">P</span>
                             <span>{{appName}}</span>
                             <span class="tag">LOCAL PROXY</span>
                         </header>

                         <p class="eyebrow">Connection error</p>
                         <div class="code">{{statusCode}}</div>
                         <h1>{{safeReason}}</h1>
                         <p class="message">{{safeMessage}}</p>

                         <div class="hint">
                             Check the destination and your proxy settings,
                             then try the request again.
                         </div>

                         <footer>
                             <span>Generated by {{appName}}</span>
                             <span>Connection closed</span>
                         </footer>
                     </main>
                 </body>
                 </html>
                 """;

        var body = Encoding.UTF8.GetBytes(html);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusCode} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n");

        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }

    private ConnectionRoute ResolveRoute(string host, int port)
    {
        if (_proxyState.IsActive && _hostMappings.TryResolve(host, port, out var mapping))
        {
            return new ConnectionRoute(true, mapping.Address, mapping.Port ?? port);
        }

        return new ConnectionRoute(false, null, port);
    }

    private bool HasRouteChanged(ActiveConnection connection)
    {
        var currentRoute = ResolveRoute(connection.Host, connection.Port);

        return currentRoute != connection.Route;
    }

    public void DisconnectChangedConnections()
    {
        foreach (var connection in _activeConnections.Values)
        {
            if (!HasRouteChanged(connection))
            {
                continue;
            }

            try
            {
                connection.Client.Close();
            }
            catch
            {
                // Connection may already be closed.
            }

            try
            {
                connection.Upstream.Close();
            }
            catch
            {
                // Connection may already be closed.
            }
        }
    }
}