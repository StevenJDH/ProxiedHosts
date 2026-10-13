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

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace ProxiedHosts.Core.Proxy;

public sealed class ProxyServer
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    private readonly TcpListener _listener;
    private readonly ProxyConnectionHandler _connectionHandler;

    public ProxyServer(IPAddress address, int port, ProxyConnectionHandler connectionHandler)
    {
        _listener = new TcpListener(address, port);
        _connectionHandler = connectionHandler;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var clientShutdown = new CancellationTokenSource();
        var activeClients = new ConcurrentDictionary<TcpClient, Task>();

        try
        {
            _listener.Start();

            // Explicitly stop the listener when cancellation is requested. This is
            // important because it guarantees that a pending AcceptTcpClientAsync
            // is unblocked immediately on Ctrl+C. Also, it is used for stopping to
            // accept new connections when shutdown is requested.
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

            await AcceptClientsAsync(cancellationToken, clientShutdown.Token, activeClients);
        }
        finally
        {
            _listener.Stop();

            // Allow active connections to finish before returning.
            await DrainClientsAsync(activeClients, clientShutdown);
        }
    }

    private async Task AcceptClientsAsync(CancellationToken cancellationToken, CancellationToken clientCancellationToken, ConcurrentDictionary<TcpClient, Task> activeClients)
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

            // Client connections have their own cancellation lifetime.
            var task = _connectionHandler.HandleClientSafelyAsync(client, clientCancellationToken);

            activeClients[client] = task;

            // Remove completed tasks so tracking does not grow indefinitely.
            _ = task.ContinueWith(completed =>
            {
                activeClients.TryRemove(client, out _);

                // Observe any unexpected exception from the task itself.
                _ = completed.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
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
            await allClients.WaitAsync(DrainTimeout, CancellationToken.None);
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