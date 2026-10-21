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
using System.Diagnostics;
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
    private readonly bool _enableDiagnostics;
    private long _nextConnectionId;

    private readonly ConcurrentDictionary<TcpClient, ActiveConnection> _activeConnections = new();

    public ProxyConnectionHandler(HostMappingProvider hostMappings, ProxyState proxyState, ConnectionLogMode logMode = ConnectionLogMode.MappedOnly, IProxyLogger? logger = null, bool enableDiagnostics = false)
    {
        _hostMappings = hostMappings;
        _proxyState = proxyState;
        _logMode = logMode;
        _logger = logger ?? NullProxyLogger.Instance;
        _enableDiagnostics = enableDiagnostics;
    }

    public async Task HandleClientSafelyAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            var connectionId = _enableDiagnostics ? Interlocked.Increment(ref _nextConnectionId) : 0;

            try
            {
                await HandleClientAsync(client, connectionId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal shutdown.
            }
            catch (IOException ex)
            {
                // Normal client or upstream disconnect.
                LogExpectedDisconnect(connectionId, ex);
            }
            catch (SocketException ex)
            {
                // Normal client or upstream disconnect.
                LogExpectedDisconnect(connectionId, ex);
            }
            catch (ObjectDisposedException ex)
            {
                // Connection was closed while being processed.
                LogExpectedDisconnect(connectionId, ex);
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

    private void LogExpectedDisconnect(long connectionId, Exception ex)
    {
        if (_enableDiagnostics)
        {
            _logger.Debug($"Connection #{connectionId} ended with {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task HandleClientAsync(TcpClient client, long connectionId, CancellationToken cancellationToken)
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

        if (_enableDiagnostics && request.IsConnect)
        {
            _logger.Debug($"CONNECT #{connectionId} {request.Host}:{request.Port}: requested ({(route.IsMapped ? "mapped" : "DNS")}).");
        }

        using var upstream = new TcpClient();
        upstream.NoDelay = true;

        _activeConnections[client] = new ActiveConnection(client, upstream, request.Host, request.Port, route);

        try
        {
            await ConnectUpstreamAsync(upstream, request, route, cancellationToken);
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException)
        {
            if (_enableDiagnostics && request.IsConnect)
            {
                _logger.Error($"CONNECT #{connectionId} {request.Host}:{request.Port}: upstream connection failed {DescribeException(ex)}");
            }

            await WriteErrorAsync(clientStream, 502, "Bad Gateway", $"Could not connect to {request.Host}:{request.Port}.", cancellationToken);

            return;
        }

        await using var upstreamStream = upstream.GetStream();
        await InitializeRelayAsync(clientStream, upstreamStream, requestHeader, request, connectionId, cancellationToken);
        await RelayBidirectionalAsync(client, upstream, clientStream, upstreamStream, request, connectionId, cancellationToken);
    }
    
    private async Task InitializeRelayAsync(NetworkStream clientStream, NetworkStream upstreamStream, byte[] requestHeader, ProxyRequest request, long connectionId, CancellationToken cancellationToken)
    {
        if (request.IsConnect)
        {
            var product = ApplicationMetadata.Instance.Product.Replace(' ', '-');
            var established = Encoding.ASCII.GetBytes($"HTTP/1.1 200 Connection Established\r\nProxy-Agent: {product}\r\n\r\n");

            await clientStream.WriteAsync(established, cancellationToken);

            // Forward any tunnel data received alongside the CONNECT headers.
            var headerEnd = requestHeader.AsSpan().IndexOf("\r\n\r\n"u8) + 4;

            if (headerEnd < requestHeader.Length)
            {
                await upstreamStream.WriteAsync(requestHeader.AsMemory(headerEnd), cancellationToken);
            }

            if (_enableDiagnostics)
            {
                _logger.Information($"CONNECT #{connectionId} {request.Host}:{request.Port}: tunnel established.");
            }
        }
        else
        {
            var rewrittenHeader = ProxyRequest.RewriteForOriginServer(requestHeader, request);

            await upstreamStream.WriteAsync(rewrittenHeader, cancellationToken);
        }
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

    private async Task RelayBidirectionalAsync(TcpClient client, TcpClient upstream, NetworkStream clientStream, NetworkStream upstreamStream,
        ProxyRequest request, long connectionId, CancellationToken cancellationToken)
    {
        var isConnect = request.IsConnect;
        var idleTimeout = isConnect ? ProxyConfiguration.Instance.TunnelIdleTimeout : ProxyConfiguration.Instance.IdleTimeout;
        using var idleCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        idleCancellation.CancelAfter(idleTimeout);

        var startedAt = Stopwatch.GetTimestamp();
        var destination = $"{request.Host}:{request.Port}";

        var diagnostics = _enableDiagnostics && isConnect ? new TunnelDiagnostics(GetUpstreamEndpoint(upstream.Client)) : null;
        Action<int>? recordClientBytes = diagnostics is null ? null : diagnostics.RecordClientToUpstream;
        Action<int>? recordUpstreamBytes = diagnostics is null ? null : diagnostics.RecordUpstreamToClient;

        // Capture socket references before either relay can close a TcpClient. Maybe by nullable even if defined as non-nullable. See PumpAndHalfCloseAsync() for more details.
        var clientSocket = client.Client;
        var upstreamSocket = upstream.Client;

        var clientToUpstream = PumpAndHalfCloseAsync(clientStream, upstreamStream, upstreamSocket, idleCancellation, idleTimeout, recordClientBytes);
        var upstreamToClient = PumpAndHalfCloseAsync(upstreamStream, clientStream, clientSocket, idleCancellation, idleTimeout, recordUpstreamBytes, !isConnect);

        var firstCompleted = await Task.WhenAny(clientToUpstream, upstreamToClient);
        var firstFromClient = ReferenceEquals(firstCompleted, clientToUpstream);

        if (diagnostics is not null)
        {
            var firstDirection = firstFromClient ? "client -> upstream" : "upstream -> client";

            LogRelayOutcome(firstCompleted, $"{firstDirection} [first observed]", connectionId, destination, startedAt);
        }

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
        finally
        {
            LogTunnelCompletion(diagnostics, clientToUpstream, upstreamToClient, firstFromClient, connectionId, destination, startedAt);
        }
    }
    
    private void LogTunnelCompletion(TunnelDiagnostics? diagnostics, Task clientToUpstream, Task upstreamToClient, bool firstFromClient, long connectionId, string destination, long startedAt)
    {
        if (diagnostics is null)
        {
            return;
        }

        var secondTask = firstFromClient ? upstreamToClient : clientToUpstream;
        var secondDirection = firstFromClient ? "upstream -> client" : "client -> upstream";

        LogRelayOutcome(secondTask, $"{secondDirection} [second]", connectionId, destination, startedAt);
        _logger.Information($"CONNECT #{connectionId} {destination}: tunnel closed after {Stopwatch.GetElapsedTime(startedAt)}; {diagnostics.GetSummary()}");
    }

    private void LogRelayOutcome(Task task, string direction, long connectionId, string destination, long startedAt)
    {
        string outcome;

        if (task.IsCompletedSuccessfully)
        {
            outcome = "EOF";
        }
        else if (task.IsCanceled)
        {
            outcome = "Canceled";
        }
        else if (task.Exception is { } exception)
        {
            outcome = $"Error {DescribeException(exception)}";
        }
        else
        {
            outcome = "Incomplete";
        }

        _logger.Information($"CONNECT #{connectionId} {destination} {direction}: {outcome} after {Stopwatch.GetElapsedTime(startedAt)}.");
    }
    
    private static string GetUpstreamEndpoint(Socket? socket)
    {
        try
        {
            return socket?.RemoteEndPoint?.ToString() ?? "unavailable";
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return "unavailable";
        }
    }

    private static string DescribeException(Exception exception)
    {
        var cause = exception.GetBaseException();

        var details = cause is SocketException socketException
            ? $"socket={socketException.SocketErrorCode}, native={socketException.NativeErrorCode}"
            : $"socket=unavailable, hresult=0x{cause.HResult:X8}";

        return $"({cause.GetType().Name}, {details}): {cause.Message}";
    }

    /// <summary>
    /// Relays data in one direction and half-closes the destination socket
    /// when forwarding completes.
    /// </summary>
    /// <remarks>
    /// The destination socket is captured before relay tasks begin to avoid
    /// accessing <see cref="TcpClient.Client"/> during concurrent shutdown.
    /// The socket may be null if the client was already disposed, so null
    /// and disposed sockets are safely handled during half-close.
    /// See <see href="https://github.com/dotnet/runtime/issues/97050">
    /// .NET runtime issue #97050</see> for details.
    /// </remarks>
    private static async Task PumpAndHalfCloseAsync(Stream source, Stream destination, Socket? destinationSocket, CancellationTokenSource idleCancellation,
        TimeSpan idleTimeout, Action<int>? recordForwardedBytes = null, bool rewriteResponse = false)
    {
        if (rewriteResponse)
        {
            await ForwardHttpResponseAsync(source, destination, idleCancellation, idleTimeout);
        }
        else
        {
            await PumpAsync(source, destination, idleCancellation, idleTimeout, recordForwardedBytes);
        }

        try
        {
            // Signal EOF without closing the receiving direction.
            destinationSocket?.Shutdown(SocketShutdown.Send);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // The destination may already be closed.
        }
    }

    private static async Task ForwardHttpResponseAsync(Stream source, Stream destination, CancellationTokenSource idleCancellation, TimeSpan idleTimeout)
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

                // Reject complete headers whose terminator appears beyond the limit.
                ValidateResponseHeaderLength(headerLength);

                // Reject ambiguous upstream framing before forwarding the response.
                await ValidateResponseFramingAsync(buffered.AsMemory(0, headerLength), destination, cancellationToken);

                if (IsInterimResponse(buffered.AsSpan(0, headerLength)))
                {
                    // Add Via to 1xx informational responses without closing the connection,
                    // then continue waiting for the final response.
                    var interimHeader = buffered[..headerLength];
                    var rewrittenInterim = HttpHeaderRewriter.RewriteResponse(interimHeader, isInterim: true);

                    await destination.WriteAsync(rewrittenInterim, cancellationToken);
                    idleCancellation.CancelAfter(idleTimeout);
                    buffered = buffered[headerLength..];
                    continue;
                }

                var rewritten = HttpHeaderRewriter.RewriteResponse(buffered);

                await destination.WriteAsync(rewritten, cancellationToken);
                idleCancellation.CancelAfter(idleTimeout);

                // Forward the rest of the response body without modification.
                await PumpAsync(source, destination, idleCancellation, idleTimeout);
                
                return;
            }

            var maxHeaderBytes = ProxyConfiguration.Instance.MaxHeaderBytes;

            // Reject headers that reach the limit without a terminator.
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
            idleCancellation.CancelAfter(idleTimeout);
        }
    }

    /// <summary>
    /// Validates that a completed HTTP response header does not exceed the
    /// configured size limit, excluding any response body data.
    /// </summary>
    /// <param name="headerLength">
    /// The total header length in bytes, including the terminating CRLF sequence.
    /// </param>
    /// <exception cref="IOException">
    /// Thrown when the header length exceeds the configured maximum.
    /// </exception>
    private static void ValidateResponseHeaderLength(int headerLength)
    {
        var maxHeaderBytes = ProxyConfiguration.Instance.MaxHeaderBytes;

        if (headerLength > maxHeaderBytes)
        {
            throw new IOException($"HTTP response header exceeded the {maxHeaderBytes}-byte limit.");
        }
    }

    /// <summary>
    /// Validates HTTP response framing to prevent ambiguous message boundaries
    /// caused by conflicting Content-Length and Transfer-Encoding headers.
    /// </summary>
    /// <remarks>
    /// Rejects conflicting upstream responses with HTTP 502 before forwarding
    /// their headers, in accordance with RFC 9112, Section 6.3.
    /// </remarks>
    /// <param name="header">The complete HTTP response header to validate.</param>
    /// <param name="destination">The client stream receiving the error response.</param>
    /// <param name="cancellationToken">The cancellation token for the operation.</param>
    /// <exception cref="IOException">
    /// Thrown when conflicting framing headers are detected.
    /// </exception>
    private static async Task ValidateResponseFramingAsync(ReadOnlyMemory<byte> header, Stream destination, CancellationToken cancellationToken)
    {
        if (!HttpMessageFraming.HasConflictingLengthHeaders(header.Span))
        {
            return;
        }

        await WriteErrorAsync(destination, 502, "Bad Gateway", "The upstream server sent conflicting HTTP framing headers.", cancellationToken);

        throw new IOException("Upstream response contains both Content-Length and Transfer-Encoding.");
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

    private static async Task PumpAsync(Stream source, Stream destination, CancellationTokenSource idleCancellation, TimeSpan idleTimeout, Action<int>? recordForwardedBytes = null)
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

                recordForwardedBytes?.Invoke(read);

                // Activity in either direction resets the shared timeout.
                idleCancellation.CancelAfter(idleTimeout);
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