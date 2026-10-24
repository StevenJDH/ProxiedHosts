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
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace ProxiedHosts.Core.Proxy;

public sealed class ProxyServer
{
    private const int MaxConcurrentRejections = 8;

    private readonly ProxyStartupOptions _startupOptions;
    private readonly TcpListener _listener;
    private readonly ProxyConnectionHandler _connectionHandler;

    public ProxyServer(IPAddress address, int port, ProxyConnectionHandler connectionHandler, ProxyStartupOptions? startupOptions = null)
    {
        _listener = new TcpListener(address, port);
        _connectionHandler = connectionHandler;
        _startupOptions = startupOptions ?? new ProxyStartupOptions();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var maxConnections = _startupOptions.MaxConcurrentConnectionsOverride is { } limitOverride
            ? limitOverride.Value
            : ProxyConfiguration.Instance.MaxConcurrentConnections;

        if (maxConnections is <= 0)
        {
            throw new InvalidOperationException($"{nameof(ProxyConfiguration.MaxConcurrentConnections)} must be positive or null.");
        }

        using var connectionSlots = maxConnections is not null
            ? new SemaphoreSlim(maxConnections.Value, maxConnections.Value)
            : null;

        using var rejectionSlots = new SemaphoreSlim(MaxConcurrentRejections, MaxConcurrentRejections);

        using var clientShutdown = new CancellationTokenSource();
        var activeClients = new ConcurrentDictionary<TcpClient, Task>();

        try
        {
            _listener.Start();

            // Explicitly stop the listener when cancellation is requested. This is
            // important because it guarantees that a pending AcceptTcpClientAsync
            // is unblocked immediately. Also, it is used for stopping to accept new
            // connections when shutdown is requested.
            await using var cancellationRegistration = cancellationToken.Register(() =>
            {
                try
                {
                    _listener.Stop();
                }
                catch
                {
                    // The listener may already be stopped.
                }
            });

            // ReSharper disable once PossiblyMistakenUseOfCancellationToken
            await AcceptClientsAsync(activeClients, connectionSlots, rejectionSlots, clientShutdown, cancellationToken);
        }
        finally
        {
            _listener.Stop();

            // Drain both admitted connections and pending rejection responses.
            await DrainClientsAsync(activeClients, clientShutdown);
        }
    }
    
    private async Task AcceptClientsAsync(ConcurrentDictionary<TcpClient, Task> activeClients, SemaphoreSlim? connectionSlots,
        SemaphoreSlim rejectionSlots, CancellationTokenSource clientShutdown, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                // ReSharper disable once PossiblyMistakenUseOfCancellationToken
                client = await _listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            bool slotAcquired;

            try
            {
                slotAcquired = connectionSlots is null || await connectionSlots.WaitAsync(0, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                client.Dispose();
                break;
            }

            // Reject excess connections without waiting for normal capacity.
            if (!slotAcquired)
            {
                await ScheduleRejectionAsync(activeClients, client, rejectionSlots, clientShutdown.Token);
                continue;
            }

            // Client connections have their own cancellation lifetime.
            var task = connectionSlots is null
                ? _connectionHandler.HandleClientSafelyAsync(client, clientShutdown.Token)
                : HandleClientWithSlotAsync(client, connectionSlots, clientShutdown.Token);

            TrackClientTask(activeClients, client, task);
        }
    }
    
    /// <summary>
    /// Schedules a bounded HTTP 503 response or closes the connection
    /// immediately when all rejection slots are occupied.
    /// </summary>
    /// <param name="activeClients">The dictionary tracking active connection tasks.</param>
    /// <param name="client">The connection being rejected.</param>
    /// <param name="rejectionSlots">The semaphore limiting concurrent rejections.</param>
    /// <param name="cancellationToken">The cancellation token for the rejection.</param>
    private static async Task ScheduleRejectionAsync(ConcurrentDictionary<TcpClient, Task> activeClients, TcpClient client,
        SemaphoreSlim rejectionSlots, CancellationToken cancellationToken)
    {
        try
        {
            if (!await rejectionSlots.WaitAsync(0, cancellationToken))
            {
                client.Dispose();
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            return;
        }

        var task = RejectWithSlotAsync(client, rejectionSlots, cancellationToken);
        TrackClientTask(activeClients, client, task);
    }

    /// <summary>
    /// Sends a rejection response and releases its concurrency slot
    /// when processing finishes.
    /// </summary>
    /// <param name="client">The connection being rejected.</param>
    /// <param name="rejectionSlots">The semaphore controlling rejection concurrency.</param>
    /// <param name="cancellationToken">The cancellation token for the operation.</param>
    private static async Task RejectWithSlotAsync(TcpClient client, SemaphoreSlim rejectionSlots, CancellationToken cancellationToken)
    {
        try
        {
            await RejectExcessConnectionAsync(client, cancellationToken);
        }
        finally
        {
            rejectionSlots.Release();
        }
    }

    /// <summary>
    /// Tracks a connection task and removes it when processing completes.
    /// </summary>
    /// <param name="activeClients">The dictionary tracking connection tasks.</param>
    /// <param name="client">The client associated with the task.</param>
    /// <param name="task">The connection processing task.</param>
    private static void TrackClientTask(ConcurrentDictionary<TcpClient, Task> activeClients, TcpClient client, Task task)
    {
        activeClients[client] = task;

        _ = task.ContinueWith(completed =>
            {
                activeClients.TryRemove(client, out _);

                // Observe any unexpected exception from the task itself.
                _ = completed.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Processes an admitted client connection and releases its concurrency
    /// slot when processing finishes, including when an error occurs.
    /// </summary>
    /// <param name="client">The accepted client connection.</param>
    /// <param name="connectionSlots">The semaphore controlling concurrency.</param>
    /// <param name="cancellationToken">The cancellation token for the connection.</param>
    private async Task HandleClientWithSlotAsync(TcpClient client, SemaphoreSlim connectionSlots, CancellationToken cancellationToken)
    {
        try
        {
            await _connectionHandler.HandleClientSafelyAsync(client, cancellationToken);
        }
        finally
        {
            connectionSlots.Release();
        }
    }

    /// <summary>
    /// Rejects a connection when the concurrency limit has been reached as per RFC 9110 §15.6.4,
    /// attempting to return HTTP 503 before closing the socket.
    /// </summary>
    /// <param name="client">The connection to reject.</param>
    /// <param name="cancellationToken">The cancellation token for the operation.</param>
    private static async Task RejectExcessConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            using var rejectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            rejectionCancellation.CancelAfter(TimeSpan.FromMilliseconds(250));

            try
            {
                await ProxyErrorResponse.WriteAsync(client.GetStream(),
                    503,
                    "Service Unavailable",
                    "The proxy has reached its maximum number of concurrent connections.",
                    rejectionCancellation.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // Best-effort error response. The connection is closed regardless.
            }
        }
    }
    
    private static async Task DrainClientsAsync(ConcurrentDictionary<TcpClient, Task> activeClients, CancellationTokenSource clientShutdown)
    {
        // No new clients can be accepted. Allow current ones to finish.
        var tasks = activeClients.Values.ToArray();

        if (tasks.Length == 0)
        {
            return;
        }

        var allClients = Task.WhenAll(tasks);

        try
        {
            await allClients.WaitAsync(ProxyConfiguration.Instance.DrainTimeout, CancellationToken.None);
        }
        catch (TimeoutException) when (!allClients.IsCompleted)
        {
            // Grace period expired. Force remaining clients to stop.
            await clientShutdown.CancelAsync();

            foreach (var client in activeClients.Keys)
            {
                try
                {
                    client.Close();
                }
                catch
                {
                    // Closing connections during shutdown is best-effort.
                }
            }

            // Observe completion and finish cleanup before returning.
            await allClients;
        }
    }
}