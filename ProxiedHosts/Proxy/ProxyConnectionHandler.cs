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

using ProxiedHosts.Hosts;
using System.Buffers;
using System.Net.Sockets;
using System.Text;

namespace ProxiedHosts.Proxy;

internal sealed class ProxyConnectionHandler
{
    private const int MaxHeaderBytes = 64 * 1024;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private readonly HostMappingProvider _hostMappings;

    public ProxyConnectionHandler(HostMappingProvider hostMappings)
    {
        _hostMappings = hostMappings;
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
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{DateTimeOffset.Now:T}] Client error: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        client.NoDelay = true;
        await using var clientStream = client.GetStream();

        var requestHeader = await HttpHeaderReader.ReadAsync(clientStream, MaxHeaderBytes, cancellationToken);
        if (requestHeader is null)
        {
            return;
        }

        if (!ProxyRequest.TryParse(requestHeader, out var request, out var parseError))
        {
            await WriteErrorAsync(clientStream, 400, "Bad Request", parseError ?? "Invalid proxy request.",
                cancellationToken);
            return;
        }

        var mapping = _hostMappings.TryResolve(request.Host, out var mapped) ? mapped : null;
        var destinationAddress = mapping?.Address;

        using var upstream = new TcpClient
        {
            NoDelay = true
        };

        try
        {
            if (destinationAddress is not null)
            {
                await upstream.ConnectAsync(destinationAddress, request.Port, cancellationToken)
                    .AsTask().WaitAsync(ConnectTimeout, cancellationToken);
                Console.WriteLine(
                    $"[{DateTimeOffset.Now:T}] {request.Method} {request.Host}:{request.Port} -> {destinationAddress}:{request.Port}");
            }
            else
            {
                await upstream.ConnectAsync(request.Host, request.Port, cancellationToken)
                    .AsTask().WaitAsync(ConnectTimeout, cancellationToken);
                Console.WriteLine(
                    $"[{DateTimeOffset.Now:T}] {request.Method} {request.Host}:{request.Port} -> DNS");
            }
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
            var established =
                Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\nProxy-Agent: ProxiedHosts\r\n\r\n");
            await clientStream.WriteAsync(established, cancellationToken);
        }
        else
        {
            var rewrittenHeader = ProxyRequest.RewriteForOriginServer(requestHeader, request);
            await upstreamStream.WriteAsync(rewrittenHeader, cancellationToken);
        }

        await RelayBidirectionalAsync(clientStream, upstreamStream, cancellationToken);
    }

    private static async Task RelayBidirectionalAsync(NetworkStream client, NetworkStream upstream, CancellationToken cancellationToken)
    {
        var clientToUpstream = PumpAsync(client, upstream, cancellationToken);
        var upstreamToClient = PumpAsync(upstream, client, cancellationToken);

        await Task.WhenAny(clientToUpstream, upstreamToClient);

        try
        {
            client.Close();
        }
        catch
        {
            // Connection may already be closed.
        }

        try
        {
            upstream.Close();
        }
        catch
        {
            // Connection may already be closed.
        }

        try
        {
            await Task.WhenAll(clientToUpstream, upstreamToClient);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // Expected when either side closes first.
        }
    }

    private static async Task PumpAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);

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
}