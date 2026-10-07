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

using System.Net;
using System.Net.Sockets;

namespace ProxiedHosts.Core.Proxy;

public sealed class ProxyServer
{
    private readonly TcpListener _listener;
    private readonly ProxyConnectionHandler _connectionHandler;

    public ProxyServer(IPAddress address, int port, ProxyConnectionHandler connectionHandler)
    {
        _listener = new TcpListener(address, port);
        _connectionHandler = connectionHandler;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _listener.Start();

        // Explicitly stop the listener when cancellation is requested. This is
        // important because it guarantees that a pending AcceptTcpClientAsync
        // is unblocked immediately on Ctrl+C.
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                _listener.Stop();
            }
            catch
            {
                // The listener may already be stopped during shutdown.
            }
        });

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
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

                _ = _connectionHandler.HandleClientSafelyAsync(client, cancellationToken);
            }
        }
        finally
        {
            _listener.Stop();
        }
    }
}