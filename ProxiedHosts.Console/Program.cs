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
using ProxiedHosts.Core.Proxy;
using SystemConsole = System.Console;

namespace ProxiedHosts.Console;

internal static class Program
{
    public static async Task<int> Main()
    {
        using var shutdown = new CancellationTokenSource();

        try
        {
            var logger = new ConsoleProxyLogger();
            var configuration = ProxyConfiguration.Load();
            using var hostMappings = new HostMappingProvider(configuration.HostsFilePath, logger);
            var portProvider = new PortProvider(configuration.ApplicationName);
            var port = await portProvider.GetOrCreateAvailablePortAsync(shutdown.Token);
            var proxyState = new ProxyState();
            var connectionHandler = new ProxyConnectionHandler(hostMappings, proxyState, configuration.ConnectionLogMode, logger);
            var proxy = new ProxyServer(configuration.ListenAddress, port, connectionHandler);

            SystemConsole.CancelKeyPress += (_, e) =>
            {
                // Prevent the runtime from terminating immediately so
                // resources can shut down cleanly.
                e.Cancel = true;

                if (!shutdown.IsCancellationRequested)
                {
                    SystemConsole.WriteLine();
                    SystemConsole.WriteLine("Stopping proxy...");
                    shutdown.Cancel();
                }
            };

            proxyState.Changed += active =>
            {
                SystemConsole.WriteLine(active ? "Proxy mappings activated." : "Proxy mappings deactivated. Traffic will use normal DNS.");
            };

            SystemConsole.WriteLine();
            SystemConsole.WriteLine("ProxiedHosts");
            SystemConsole.WriteLine("------------------");
            SystemConsole.WriteLine($"Proxy      : http://127.0.0.1:{port}");
            SystemConsole.WriteLine($"Port       : {port} (stable and persisted)");
            SystemConsole.WriteLine($"Mappings   : {hostMappings.Count}");
            SystemConsole.WriteLine($"Status     : {(proxyState.IsActive ? "Active" : "Inactive")}");
            SystemConsole.WriteLine();
            SystemConsole.WriteLine("Configure your application to use the proxy above for HTTP and HTTPS.");
            SystemConsole.WriteLine("HTTPS is tunneled with CONNECT; TLS is not decrypted or inspected.");
            SystemConsole.WriteLine();
            SystemConsole.WriteLine("P = toggle proxy mappings");
            SystemConsole.WriteLine("Q / Ctrl+C = quit");
            SystemConsole.WriteLine();

            _ = Task.Run(() => RunInputLoop(proxyState, shutdown));
            await proxy.RunAsync(shutdown.Token);
            SystemConsole.WriteLine("Proxy stopped.");

            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            SystemConsole.Error.WriteLine($"Fatal error: {ex.Message}");
            SystemConsole.Error.WriteLine(ex);
            return 1;
        }

    }

    private static void RunInputLoop(ProxyState proxyState, CancellationTokenSource shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            var key = SystemConsole.ReadKey(intercept: true);

            switch (key.Key)
            {
                case ConsoleKey.P:
                    proxyState.Toggle();
                    break;

                case ConsoleKey.Q:
                    SystemConsole.WriteLine();
                    SystemConsole.WriteLine("Stopping proxy...");
                    shutdown.Cancel();
                    return;
            }
        }
    }
}