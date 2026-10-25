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
using ProxiedHosts.Core.Proxy;
using System.Text;
using System.Text.Json;
using ProxiedHosts.Console.Infrastructure;
using ProxiedHosts.Console.Logging;
using ProxiedHosts.Core.Status;
using SystemConsole = System.Console;

namespace ProxiedHosts.Console;

internal static class Program
{
    private static int _updateCheckRunning;

    public static async Task<int> Main(string[] args)
    {
        SystemConsole.OutputEncoding = new UTF8Encoding(false);

        if (!SystemConsole.IsOutputRedirected)
        {
            SystemConsole.Title = $"{ApplicationMetadata.Instance.Product} - Console Proxy Manager";
        }

        using var shutdown = new CancellationTokenSource();
        var logger = new ConsoleProxyLogger();

        try
        {
            using var instance = SingleInstanceGuard.TryAcquire();
            var product = ApplicationMetadata.Instance.Product;

            if (instance is null)
            {
                logger.Error($"{product} is already running.");
                return 1;
            }

            var configuration = ProxyConfiguration.Instance;
            var startupOptions = ProxyStartupOptions.Parse(args);
            var currentVersion = ApplicationMetadata.Instance.Version;
            using var hostMappings = new HostMappingProvider(configuration.HostsFilePath, logger);
            var portProvider = new PortProvider();
            var port = await portProvider.GetOrCreateAvailablePortAsync(shutdown.Token);
            var proxyState = new ProxyState();
            var connectionHandler = new ProxyConnectionHandler(hostMappings, proxyState, logger, startupOptions);
            var proxy = new ProxyServer(configuration.ListenAddress, port, connectionHandler, startupOptions);

            hostMappings.MappingsReloaded += connectionHandler.DisconnectChangedConnections;

            using var cancelHandler = new ConsoleCancelHandler(shutdown, logger);
            await using var statusServer = new ProxyStatusServer(proxy, connectionHandler, proxyState, hostMappings, logger);

            statusServer.Start(shutdown.Token, excludedPort: port);

            proxyState.Changed += active =>
            {
                connectionHandler.DisconnectChangedConnections();
                logger.Information(active ? "Proxy mappings activated." : "Proxy mappings deactivated. Traffic will use normal DNS.");
            };

            SystemConsole.WriteLine();
            SystemConsole.WriteLine($"{product}");
            SystemConsole.WriteLine("------------------");
            SystemConsole.WriteLine($"Proxy      : http://127.0.0.1:{port}");
            SystemConsole.WriteLine($"Port       : {port} (stable and persisted)");
            SystemConsole.WriteLine($"Dashboard  : {statusServer.Url}");
            SystemConsole.WriteLine($"Mappings   : {hostMappings.Count}");
            SystemConsole.WriteLine($"Status     : {(proxyState.IsActive ? "Active" : "Inactive")}");
            SystemConsole.WriteLine();
            SystemConsole.WriteLine("Configure your application to use the proxy above for HTTP and HTTPS.");
            SystemConsole.WriteLine("HTTPS is tunneled with CONNECT; TLS is not decrypted or inspected.");
            SystemConsole.WriteLine();
            SystemConsole.WriteLine("P = toggle proxy mappings");
            SystemConsole.WriteLine("O = open proxy settings...");
            SystemConsole.WriteLine("E = edit proxiedhosts.txt");
            SystemConsole.WriteLine("S = open status dashboard");
            SystemConsole.WriteLine("U = check for updates");
            SystemConsole.WriteLine($"A = about {product}");
            SystemConsole.WriteLine("D = donate 5€ (PayPal)...");
            SystemConsole.WriteLine("Q / Ctrl+C = quit");
            SystemConsole.WriteLine();

            // Check for updates after displaying the startup information,
            // without delaying proxy startup or keyboard input.
            _ = CheckForUpdatesAsync(currentVersion, logger, shutdown.Token, automatic: true);

            // Start the proxy asynchronously.
            var proxyTask = proxy.RunAsync(shutdown.Token);

            try
            {
                if (!SystemConsole.IsInputRedirected)
                {
                    // Handle keyboard input without creating a detached background task.
                    RunInputLoop(proxyState, configuration.HostsFilePath, statusServer, currentVersion, logger, shutdown, proxyTask);
                }
                
                await proxyTask;
            }
            finally
            {
                if (!shutdown.IsCancellationRequested)
                {
                    await shutdown.CancelAsync();
                }
            }

            var totals = connectionHandler.Metrics.GetSnapshot();

            logger.Information($"Connections: accepted={totals.TotalAccepted}, peak={totals.PeakConnections}, rejected={totals.TotalRejected}.");
            logger.Information("Proxy stopped.");

            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            logger.Error($"Fatal error: {ex.Message}{Environment.NewLine}{ex}");
            return 1;
        }
    }

    private static void RunInputLoop(ProxyState proxyState, string hostsFilePath, ProxyStatusServer statusServer, Version currentVersion, IProxyLogger logger, CancellationTokenSource shutdown, Task proxyTask)
    {
        while (!shutdown.IsCancellationRequested && !proxyTask.IsCompleted)
        {
            if (!SystemConsole.KeyAvailable)
            {
                Thread.Sleep(50);
                continue;
            }

            var key = SystemConsole.ReadKey(intercept: true);

            switch (key.Key)
            {
                case ConsoleKey.P:
                    proxyState.Toggle();
                    break;

                case ConsoleKey.O:
                    TryOpen(FileLauncher.OpenSystemProxySettings, "system proxy settings", logger);
                    break;

                case ConsoleKey.E:
                    TryOpen(() => FileLauncher.Open(hostsFilePath), "hosts file", logger);
                    break;

                case ConsoleKey.S:
                {
                    // Allows to capture changes in the url rather than just the initially set one if passed as a string.
                    string url = statusServer.Url;

                    if (string.IsNullOrEmpty(url))
                    {
                        logger.Warning("Proxy status dashboard is unavailable.");
                    }
                    else
                    {
                        TryOpen(() => FileLauncher.OpenUrl(url), "proxy status dashboard", logger);
                    }

                    break;
                }
                case ConsoleKey.U:
                    _ = CheckForUpdatesAsync(currentVersion, logger, shutdown.Token);
                    break;

                case ConsoleKey.A:
                    ShowAboutInformation();
                    break;

                case ConsoleKey.D:
                    TryOpen(() => FileLauncher.OpenUrl(ApplicationMetadata.Instance.FixedDonationUrl), "donation page", logger);
                    break;

                case ConsoleKey.Q:
                    if (!shutdown.IsCancellationRequested)
                    {
                        logger.Information("Stopping proxy...");
                        shutdown.Cancel();
                    }
                    return;
            }
        }
    }

    private static void TryOpen(Action action, string description, IProxyLogger logger)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            logger.Error($"Failed to open {description}: {ex.Message}");
        }
    }

    private static async Task CheckForUpdatesAsync(Version currentVersion, IProxyLogger logger, CancellationToken cancellationToken, bool automatic = false)
    {
        if (Interlocked.CompareExchange(ref _updateCheckRunning, 1, 0) != 0)
        {
            if (!automatic)
            {
                logger.Warning("An update check is already in progress.");
            }
            return;
        }

        try
        {
            if (!automatic)
            {
                logger.Information("Checking for updates...");
            }

            var repositoryUrl = ApplicationMetadata.Instance.RepositoryUrl;

            if (string.IsNullOrWhiteSpace(repositoryUrl))
            {
                throw new InvalidOperationException("Could not determine the repository URL.");
            }

            // Console checks stable releases only.
            var update = await UpdateChecker.CheckAsync(currentVersion, repositoryUrl, includePreviewReleases: false, cancellationToken: cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (update is null)
            {
                if (!automatic)
                {
                    logger.Information($"{ApplicationMetadata.Instance.Product} is up to date (v{currentVersion}).");
                }

                return;
            }

            logger.Information($"Update available: v{currentVersion} -> v{update.Version} | {update.Url}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal application shutdown.
        }
        catch (OperationCanceledException)
        {
            logger.Warning("Update check timed out. Press U to retry.");
        }
        catch (HttpRequestException ex)
        {
            logger.Warning($"Unable to check for updates: {ex.Message}");
        }
        catch (JsonException ex)
        {
            logger.Warning($"Invalid GitHub response: {ex.Message}");
        }
        catch (Exception ex)
        {
            logger.Error($"Update check failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _updateCheckRunning, 0);
        }
    }

    private static void ShowAboutInformation()
    {
        var metadata = ApplicationMetadata.Instance;
        var product = metadata.Product;
        var version = metadata.Version;
        var authors = metadata.Authors;
        var copyright = metadata.Copyright;
        var repositoryUrl = metadata.RepositoryUrl;

        SystemConsole.WriteLine($"""

                                 About {product}
                                 ------------------
                                 Version   : {version}
                                 Author    : {authors}
                                 Copyright : {copyright} {authors}
                                 License   : GNU General Public License v3 or later.
                                 Source    : {repositoryUrl}

                                 {product} is free software: you can redistribute it and/or modify it
                                 under the terms of the GNU General Public License as published by the
                                 Free Software Foundation, either version 3 of the License, or (at your
                                 option) any later version.

                                 """);
    }
}