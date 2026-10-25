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
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ProxiedHosts.Core.Proxy;

/// <summary>
/// Hosts the read-only proxy status page on a separate IPv4 loopback listener.
/// </summary>
public sealed class ProxyStatusServer : IAsyncDisposable
{
    private const int MaxConcurrentRequests = 4;
    private const int MaxRequestHeaderBytes = 4096;

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly SemaphoreSlim _requestSlots = new(MaxConcurrentRequests, MaxConcurrentRequests);
    private readonly ConcurrentDictionary<TcpClient, Task> _clients = new();
    private readonly ProxyServer _proxy;
    private readonly ProxyConnectionHandler _handler;
    private readonly ProxyState _proxyState;
    private readonly HostMappingProvider _hostMappings;
    private CancellationTokenSource? _shutdown;
    private Task? _runTask;

    /// <summary>
    /// Gets the bound TCP port after <see cref="Start"/> succeeds.
    /// </summary>
    public int Port { get; private set; }

    /// <summary>
    /// Gets the complete IPv4 loopback URL for the status dashboard.
    /// </summary>
    public string Url => Port == 0 ? string.Empty : $"http://127.0.0.1:{Port}/status";

    /// <summary>
    /// Initializes the status listener with the services whose state is displayed.
    /// </summary>
    /// <param name="proxy">The normal proxy server.</param>
    /// <param name="handler">The proxy's connection handler and metrics owner.</param>
    /// <param name="proxyState">The host-mappings activation state.</param>
    /// <param name="hostMappings">The loaded host mappings.</param>
    public ProxyStatusServer(ProxyServer proxy, ProxyConnectionHandler handler, ProxyState proxyState, HostMappingProvider hostMappings)
    {
        _proxy = proxy;
        _handler = handler;
        _proxyState = proxyState;
        _hostMappings = hostMappings;
    }

    /// <summary>
    /// Starts the status listener on an automatically assigned loopback port.
    /// </summary>
    /// <param name="lifetimeToken">The application or proxy-session shutdown token.</param>
    public void Start(CancellationToken lifetimeToken)
    {
        if (_shutdown is not null)
        {
            throw new InvalidOperationException("The proxy status listener is already running.");
        }

        _listener.Start(backlog: 16);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _shutdown = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        _runTask = AcceptLoopAsync(_shutdown.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await AcceptClientAsync(cancellationToken);

                if (client is null)
                {
                    break;
                }

                if (!await TryAdmitAsync(client, cancellationToken))
                {
                    continue;
                }

                TrackClient(client, ServeWithSlotAsync(client, cancellationToken));
            }
        }
        finally
        {
            _listener.Stop();
            await Task.WhenAll(_clients.Values);
        }
    }

    private async Task<TcpClient?> AcceptClientAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _listener.AcceptTcpClientAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<bool> TryAdmitAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            if (await _requestSlots.WaitAsync(0, cancellationToken))
            {
                return true;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The accepted client still needs to be disposed during shutdown.
        }

        client.Dispose();
        return false;
    }

    private void TrackClient(TcpClient client, Task task)
    {
        _clients[client] = task;

        _ = task.ContinueWith(completed =>
        {
            _clients.TryRemove(client, out _);
            _ = completed.Exception;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task ServeWithSlotAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                await ServeClientAsync(client, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // Browsers may disconnect or time out while loading the status page.
            }
            finally
            {
                _requestSlots.Release();
            }
        }
    }

    private async Task ServeClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestCancellation.CancelAfter(TimeSpan.FromSeconds(5));

        var stream = client.GetStream();
        var header = await HttpHeaderReader.ReadAsync(stream, MaxRequestHeaderBytes, requestCancellation.Token);

        if (header is null)
        {
            return;
        }

        if (!IsAllowedRequest(header))
        {
            await WriteNotFoundAsync(stream, requestCancellation.Token);
            return;
        }

        var snapshot = new ProxyStatusSnapshot(
            _handler.Metrics.GetSnapshot(),
            _proxy.IsListening,
            _proxyState.IsActive,
            _hostMappings.Count,
            _proxy.EffectiveMaxConcurrentConnections,
            DateTimeOffset.UtcNow);

        await ProxyStatusPage.WriteAsync(stream, snapshot, requestCancellation.Token);
    }

    private bool IsAllowedRequest(byte[] header)
    {
        var lines = Encoding.ASCII.GetString(header).Split("\r\n", StringSplitOptions.None);

        if (!lines[0].Equals("GET /status HTTP/1.1", StringComparison.Ordinal))
        {
            return false;
        }

        var hostHeaders = lines.Where(line => line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)).ToArray();
        var validHost = hostHeaders.Length == 1 && hostHeaders[0][5..].Trim()
            .Equals($"127.0.0.1:{Port}", StringComparison.OrdinalIgnoreCase);

        return validHost && !lines.Any(IsCrossOriginRequest);
    }

    private static bool IsCrossOriginRequest(string line)
    {
        if (line.StartsWith("Origin:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return line.StartsWith("Sec-Fetch-Site:", StringComparison.OrdinalIgnoreCase) && line[15..].Trim().Equals("cross-site", StringComparison.OrdinalIgnoreCase);
    }

    private static Task WriteNotFoundAsync(Stream stream, CancellationToken cancellationToken)
    {
        return ProxyErrorResponse.WriteAsync(stream,
            404,
            "Not Found",
            "The requested dashboard page was not found. Open /status to view the dashboard.",
            cancellationToken);
    }

    /// <summary>
    /// Stops the listener, closes pending dashboard connections, and drains its tasks.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_shutdown is null)
        {
            return;
        }

        try
        {
            await _shutdown.CancelAsync();
            _listener.Stop();

            foreach (var client in _clients.Keys)
            {
                client.Dispose();
            }

            if (_runTask is not null)
            {
                await _runTask;
            }
        }
        finally
        {
            _shutdown.Dispose();
            _shutdown = null;
            _requestSlots.Dispose();
            Port = 0;
        }
    }
}