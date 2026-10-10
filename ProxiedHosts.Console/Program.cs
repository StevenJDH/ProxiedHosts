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
using System.Reflection;
using System.Text;
using System.Text.Json;
using SystemConsole = System.Console;

namespace ProxiedHosts.Console;

internal static class Program
{
    private static int _updateCheckRunning;

    public static async Task<int> Main()
    {
        SystemConsole.OutputEncoding = new UTF8Encoding(false);

        using var shutdown = new CancellationTokenSource();
        var logger = new ConsoleProxyLogger();

        try
        {
            using var instance = SingleInstanceGuard.TryAcquire();

            if (instance is null)
            {
                logger.Error("ProxiedHosts is already running.");
                return 1;
            }

            var configuration = ProxyConfiguration.Instance;
            var currentVersion = typeof(Program).Assembly.GetName().Version ?? throw new InvalidOperationException("Could not determine the installed version.");
            using var hostMappings = new HostMappingProvider(configuration.HostsFilePath, logger);
            var portProvider = new PortProvider();
            var port = await portProvider.GetOrCreateAvailablePortAsync(shutdown.Token);
            var proxyState = new ProxyState();
            var connectionHandler = new ProxyConnectionHandler(hostMappings, proxyState, configuration.ConnectionLogMode, logger);
            var proxy = new ProxyServer(configuration.ListenAddress, port, connectionHandler);

            hostMappings.MappingsReloaded += connectionHandler.DisconnectChangedConnections;

            using var cancelHandler = new ConsoleCancelHandler(shutdown, logger);

            proxyState.Changed += active =>
            {
                connectionHandler.DisconnectChangedConnections();
                logger.Information(active ? "Proxy mappings activated." : "Proxy mappings deactivated. Traffic will use normal DNS.");
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
            SystemConsole.WriteLine("E = edit proxiedhosts.txt");
            SystemConsole.WriteLine("U = check for updates");
            SystemConsole.WriteLine("A = about ProxiedHosts");
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
                // Handle keyboard input without creating a detached background task.
                RunInputLoop(proxyState, configuration.HostsFilePath, currentVersion, logger, shutdown, proxyTask);
                await proxyTask;
            }
            finally
            {
                if (!shutdown.IsCancellationRequested)
                {
                    await shutdown.CancelAsync();
                }
            }

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

    private static void RunInputLoop(ProxyState proxyState, string hostsFilePath, Version currentVersion, IProxyLogger logger, CancellationTokenSource shutdown, Task proxyTask)
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

                case ConsoleKey.E:
                    try
                    {
                        FileLauncher.Open(hostsFilePath);
                    }
                    catch (Exception ex)
                    {
                        logger.Error($"Failed to open hosts file: {ex.Message}");
                    }
                    break;

                case ConsoleKey.U:
                    _ = CheckForUpdatesAsync(currentVersion, logger, shutdown.Token);
                    break;

                case ConsoleKey.A:
                    ShowAboutInformation();
                    break;

                case ConsoleKey.D:
                    try
                    {
                        var donationUrl = typeof(Program).Assembly
                            .GetCustomAttributes<AssemblyMetadataAttribute>()
                            .FirstOrDefault(attribute => attribute.Key == "FixedDonationUrl")?
                            .Value ?? string.Empty;

                        FileLauncher.OpenUrl(donationUrl);
                    }
                    catch (Exception ex)
                    {
                        logger.Error($"Failed to open donation page: {ex.Message}");
                    }
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

            // Console checks stable releases only.
            var update = await UpdateChecker.CheckAsync(currentVersion, includePreviewReleases: false, cancellationToken: cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (update is null)
            {
                if (!automatic)
                {
                    logger.Information($"ProxiedHosts is up to date (v{currentVersion}).");
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
        var assembly = typeof(Program).Assembly;
        var product = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "ProxiedHosts";
        var version = assembly.GetName().Version?.ToString() ?? "Unknown";
        var copyright = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? string.Empty;
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>();

        var authors = metadata
            .FirstOrDefault(attribute => attribute.Key == "Authors")?
            .Value ?? string.Empty;

        var repositoryUrl = metadata
            .FirstOrDefault(attribute => attribute.Key == "RepositoryUrl")?
            .Value ?? string.Empty;

        SystemConsole.WriteLine();
        SystemConsole.WriteLine($"About {product}");
        SystemConsole.WriteLine("------------------");
        SystemConsole.WriteLine($"Version   : {version}");
        SystemConsole.WriteLine($"Author    : {authors}");
        SystemConsole.WriteLine($"Copyright : {copyright} {authors}");
        SystemConsole.WriteLine("License   : GNU General Public License v3 or later");
        SystemConsole.WriteLine($"Source    : {repositoryUrl}");
        SystemConsole.WriteLine();
        SystemConsole.WriteLine($"{product} is free software: you can redistribute it and/or modify it");
        SystemConsole.WriteLine("under the terms of the GNU General Public License as published by the");
        SystemConsole.WriteLine("Free Software Foundation, either version 3 of the License, or (at your");
        SystemConsole.WriteLine("option) any later version.");
        SystemConsole.WriteLine();
    }
}