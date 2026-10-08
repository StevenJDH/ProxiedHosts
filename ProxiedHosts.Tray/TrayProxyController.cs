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

namespace ProxiedHosts.Tray;

internal sealed class TrayProxyController
{
    private readonly IProxyLogger _logger;

    private CancellationTokenSource? _shutdown;
    private HostMappingProvider? _hostMappings;
    private ProxyState? _proxyState;
    private ProxyConnectionHandler? _connectionHandler;
    private ProxyServer? _proxyServer;
    private ProxyConfiguration? _configuration;
    private Task? _runTask;

    public event Action? Changed;

    public int Port { get; private set; }

    public int MappingCount => _hostMappings?.Count ?? 0;

    public bool IsActive => _proxyState?.IsActive ?? false;

    public string HostsFilePath => _configuration?.HostsFilePath ?? string.Empty;

    public string ProxyAddress => _configuration is null ? string.Empty : $"http://{_configuration.ListenAddress}:{Port}";

    public TrayProxyController(IProxyLogger logger)
    {
        _logger = logger;
    }

    public async Task StartAsync()
    {
        if (_shutdown is not null)
        {
            throw new InvalidOperationException("The proxy is already running.");
        }

        _shutdown = new CancellationTokenSource();
        _configuration = ProxyConfiguration.Instance;
        _hostMappings = new HostMappingProvider(_configuration.HostsFilePath, _logger);

        var portProvider = new PortProvider();

        Port = await portProvider.GetOrCreateAvailablePortAsync(_shutdown.Token);

        _proxyState = new ProxyState();
        _connectionHandler = new ProxyConnectionHandler(_hostMappings, _proxyState, _configuration.ConnectionLogMode, _logger);
        _proxyServer = new ProxyServer(_configuration.ListenAddress, Port, _connectionHandler);
        _hostMappings.MappingsReloaded += OnMappingsReloaded;
        _proxyState.Changed += OnProxyStateChanged;
        _runTask = _proxyServer.RunAsync(_shutdown.Token);

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

        _hostMappings!.MappingsReloaded -= OnMappingsReloaded;
        _proxyState!.Changed -= OnProxyStateChanged;
        _shutdown.Cancel();

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
        }

        _hostMappings.Dispose();
        _shutdown.Dispose();

        _runTask = null;
        _proxyServer = null;
        _connectionHandler = null;
        _proxyState = null;
        _hostMappings = null;
        _configuration = null;
        _shutdown = null;
    }

    private void OnMappingsReloaded()
    {
        _connectionHandler!.DisconnectChangedConnections();
        Changed?.Invoke();
    }

    private void OnProxyStateChanged(bool active)
    {
        _connectionHandler!.DisconnectChangedConnections();
        Changed?.Invoke();
    }
}