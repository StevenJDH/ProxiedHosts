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
using System.Net;
using ProxiedHosts.Infrastructure;
using ProxiedHosts.Proxy;

namespace ProxiedHosts;

internal static class Program
{
    private const string HostsFileName = "proxiedhosts.txt";

    public static async Task<int> Main()
    {
        using var shutdown = new CancellationTokenSource();

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

        try
        {
            var appDirectory = AppContext.BaseDirectory;
            var hostsPath = Path.Combine(appDirectory, HostsFileName);

            HostsFile.EnsureExists(hostsPath);

            var hostMap = new HostMap(hostsPath);
            hostMap.Reload();
            using var watcher = new HostFileWatcher(hostMap);

            var portStore = new StablePortStore("ProxiedHosts");
            var port = await portStore.GetOrCreateAvailablePortAsync(shutdown.Token);
            var connectionHandler = new ProxyConnectionHandler(hostMap);
            var proxy = new ProxyServer(IPAddress.Loopback, port, connectionHandler);

            Console.WriteLine("ProxiedHosts");
            Console.WriteLine("------------------");
            Console.WriteLine($"Proxy      : http://127.0.0.1:{port}");
            Console.WriteLine($"Port       : {port} (stable and persisted)");
            Console.WriteLine($"Mappings   : {hostMap.Count}");
            Console.WriteLine();
            Console.WriteLine("Configure your application to use the proxy above for HTTP and HTTPS.");
            Console.WriteLine("HTTPS is tunneled with CONNECT; TLS is not decrypted or inspected.");
            Console.WriteLine("Press Ctrl+C to stop.");
            Console.WriteLine();

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

}