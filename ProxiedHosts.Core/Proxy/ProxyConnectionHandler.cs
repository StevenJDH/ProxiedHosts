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

using ProxiedHosts.Core.Hosts;
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

    private const int MaxHeaderBytes = 64 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HeaderReadTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

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

            headerCancellation.CancelAfter(HeaderReadTimeout);
            requestHeader = await HttpHeaderReader.ReadAsync(clientStream, MaxHeaderBytes, headerCancellation.Token);
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

        await RelayBidirectionalAsync(client, upstream, clientStream, upstreamStream, cancellationToken);
    }

    private async Task ConnectUpstreamAsync(TcpClient upstream, ProxyRequest request, ConnectionRoute route, CancellationToken cancellationToken)
    {
        using var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCancellation.CancelAfter(ConnectTimeout);

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

    private static async Task RelayBidirectionalAsync(TcpClient client, TcpClient upstream, NetworkStream clientStream, NetworkStream upstreamStream, CancellationToken cancellationToken)
    {
        using var idleCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idleCancellation.CancelAfter(IdleTimeout);

        var clientToUpstream = PumpAndHalfCloseAsync(clientStream, upstreamStream, upstream, idleCancellation);
        var upstreamToClient = PumpAndHalfCloseAsync(upstreamStream, clientStream, client, idleCancellation);
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
    
    private static async Task PumpAndHalfCloseAsync(Stream source, Stream destination, TcpClient destinationClient, CancellationTokenSource idleCancellation)
    {
        await PumpAsync(source, destination, idleCancellation);

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
                idleCancellation.CancelAfter(IdleTimeout);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task WriteErrorAsync(Stream stream, int statusCode, string reason, string message, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(message + "\n");
        var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {statusCode} {reason}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");

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