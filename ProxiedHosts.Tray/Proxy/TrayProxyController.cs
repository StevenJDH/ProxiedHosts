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

namespace ProxiedHosts.Tray.Proxy;

internal sealed class TrayProxyController
{
    private readonly ConnectionLogMode _logMode;
    private readonly IProxyLogger _logger;

    private CancellationTokenSource? _shutdown;
    private HostMappingProvider? _hostMappings;
    private ProxyState? _proxyState;
    private ProxyConnectionHandler? _connectionHandler;
    private ProxyConfiguration? _configuration;
    private Task? _runTask;

    public event Action? Changed;

    public int Port { get; private set; }

    public int MappingCount => _hostMappings?.Count ?? 0;

    public bool IsActive => _proxyState?.IsActive ?? false;

    public bool HasFailed { get; private set; }

    public string HostsFilePath => _configuration?.HostsFilePath ?? string.Empty;

    public string ProxyAddress => _configuration is null ? string.Empty : $"http://{_configuration.ListenAddress}:{Port}";

    public TrayProxyController(ConnectionLogMode logMode, IProxyLogger logger)
    {
        _logMode = logMode;
        _logger = logger;
    }

    public async Task StartAsync(bool mappingsActive = true)
    {
        if (_shutdown is not null)
        {
            throw new InvalidOperationException("The proxy is already running.");
        }

        HasFailed = false;

        try
        {
            _shutdown = new CancellationTokenSource();
            _configuration = ProxyConfiguration.Instance;
            _hostMappings = new HostMappingProvider(_configuration.HostsFilePath, _logger);

            var portProvider = new PortProvider();

            Port = await portProvider.GetOrCreateAvailablePortAsync(_shutdown.Token);

            _proxyState = new ProxyState();

            if (!mappingsActive)
            {
                _proxyState.Toggle();
            }

            _connectionHandler = new ProxyConnectionHandler(_hostMappings, _proxyState, _logMode, _logger);
            _hostMappings.MappingsReloaded += OnMappingsReloaded;
            _proxyState.Changed += OnProxyStateChanged;

            var proxyServer = new ProxyServer(_configuration.ListenAddress, Port, _connectionHandler);

            _runTask = proxyServer.RunAsync(_shutdown.Token);

            // Detect an immediate startup failure, such as a port binding error.
            if (_runTask.IsCompleted)
            {
                await _runTask;
            }

            Changed?.Invoke();

            // Observe failures that occur after startup.
            _runTask = MonitorProxyAsync(_runTask, _shutdown.Token);
        }
        catch
        {
            try
            {
                await StopAsync();
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to clean up after proxy startup error: {ex}");
            }

            throw;
        }
    }

    private async Task MonitorProxyAsync(Task proxyTask, CancellationToken cancellationToken)
    {
        try
        {
            await proxyTask;

            if (!cancellationToken.IsCancellationRequested)
            {
                _logger.Error("Proxy server stopped unexpectedly.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
            return;
        }
        catch (Exception ex)
        {
            _logger.Error($"Proxy task failed: {ex}");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        HasFailed = true;
        Changed?.Invoke();
    }

    public void ToggleMappings()
    {
        _proxyState?.Toggle();
    }

    public async Task StopAsync()
    {
        if (_shutdown is null)
        {
            return;
        }

        var shutdown = _shutdown;

        try
        {
            _hostMappings?.MappingsReloaded -= OnMappingsReloaded;
            _proxyState?.Changed -= OnProxyStateChanged;
            await shutdown.CancelAsync();

            if (_runTask is not null)
            {
                try
                {
                    await _runTask;
                }
                catch (OperationCanceledException)
                {
                    // Normal shutdown.
                }
                catch (Exception) when (_runTask.IsFaulted)
                {
                    // A faulted proxy task must not prevent resource cleanup.
                }
            }
        }
        finally
        {
            try
            {
                _hostMappings?.Dispose();
            }
            finally
            {
                shutdown.Dispose();

                _runTask = null;
                _connectionHandler = null;
                _proxyState = null;
                _hostMappings = null;
                _configuration = null;
                _shutdown = null;
                Port = 0;
            }
        }
    }

    private void OnMappingsReloaded()
    {
        _connectionHandler!.DisconnectChangedConnections();
        Changed?.Invoke();
    }

    private void OnProxyStateChanged(bool active)
    {
        _connectionHandler!.DisconnectChangedConnections();
        _logger.Information(active ? "Proxy mappings activated." : "Proxy mappings deactivated. Traffic will use normal DNS.");
        Changed?.Invoke();
    }
}