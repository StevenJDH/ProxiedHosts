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

using ProxiedHosts.Configuration;
using ProxiedHosts.Hosts;
using ProxiedHosts.Infrastructure;
using ProxiedHosts.Proxy;

namespace ProxiedHosts;

internal static class Program
{
    public static async Task<int> Main()
    {
        using var shutdown = new CancellationTokenSource();

        try
        {
            var configuration = ProxyConfiguration.Load();
            using var hostMappings = new HostMappingProvider(configuration.HostsFilePath);
            var portProvider = new PortProvider(configuration.ApplicationName);
            var port = await portProvider.GetOrCreateAvailablePortAsync(shutdown.Token);
            var proxyState = new ProxyState();
            var connectionHandler = new ProxyConnectionHandler(hostMappings, proxyState);
            var proxy = new ProxyServer(configuration.ListenAddress, port, connectionHandler);

            Console.CancelKeyPress += (_, e) =>
            {
                // Prevent the runtime from terminating immediately so
                // resources can shut down cleanly.
                e.Cancel = true;

                if (!shutdown.IsCancellationRequested)
                {
                    Console.WriteLine();
                    Console.WriteLine("Stopping proxy...");
                    shutdown.Cancel();
                }
            };

            proxyState.Changed += active =>
            {
                Console.WriteLine(active ? "Proxy mappings activated." : "Proxy mappings deactivated. Traffic will use normal DNS.");
            };

            Console.WriteLine("ProxiedHosts");
            Console.WriteLine("------------------");
            Console.WriteLine($"Proxy      : http://127.0.0.1:{port}");
            Console.WriteLine($"Port       : {port} (stable and persisted)");
            Console.WriteLine($"Mappings   : {hostMappings.Count}");
            Console.WriteLine($"Status     : {(proxyState.IsActive ? "Active" : "Inactive")}");
            Console.WriteLine();
            Console.WriteLine("Configure your application to use the proxy above for HTTP and HTTPS.");
            Console.WriteLine("HTTPS is tunneled with CONNECT; TLS is not decrypted or inspected.");
            Console.WriteLine();
            Console.WriteLine("P = toggle proxy mappings");
            Console.WriteLine("Q / Ctrl+C = quit");
            Console.WriteLine();

            _ = Task.Run(() => RunInputLoop(proxyState, shutdown));
            await proxy.RunAsync(shutdown.Token);

            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal error: {ex.Message}");
            Console.Error.WriteLine(ex);
            return 1;
        }

    }

    private static void RunInputLoop(ProxyState proxyState, CancellationTokenSource shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            var key = Console.ReadKey(intercept: true);

            switch (key.Key)
            {
                case ConsoleKey.P:
                    proxyState.Toggle();
                    break;

                case ConsoleKey.Q:
                    Console.WriteLine();
                    Console.WriteLine("Stopping proxy...");
                    shutdown.Cancel();
                    return;
            }
        }
    }
}