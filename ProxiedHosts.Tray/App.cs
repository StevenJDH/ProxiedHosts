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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using ProxiedHosts.Core.Infrastructure;
using ProxiedHosts.Tray.Infrastructure;

namespace ProxiedHosts.Tray;

public sealed class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private TrayProxyController? _controller;
    private FileProxyLogger? _logger;

    private TrayIcon? _trayIcon;
    private NativeMenuItem? _statusItem;
    private NativeMenuItem? _proxyItem;
    private NativeMenuItem? _mappingsItem;
    private NativeMenuItem? _startupItem;
    private NativeMenuItem? _openHostsItem;
    private NativeMenuItem? _openLogItem;

    private int _shutdownStarted;

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            _desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _logger = new FileProxyLogger();

            CreateTrayIcon();

            _controller = new TrayProxyController(_logger);
            _controller.Changed += OnControllerChanged;

            _ = StartProxyAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void CreateTrayIcon()
    {
        _statusItem = new NativeMenuItem("Status: Starting...")
        {
            IsEnabled = false
        };

        _proxyItem = new NativeMenuItem("Proxy: Starting...")
        {
            IsEnabled = false
        };

        _mappingsItem = new NativeMenuItem("Mappings active")
        {
            ToggleType = MenuItemToggleType.CheckBox,

            IsChecked = true,
            IsEnabled = false
        };

        _mappingsItem.Click += (_, _) =>
        {
            _controller?.ToggleMappings();
        };

        _startupItem = new NativeMenuItem("Start at login")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = StartupManager.IsEnabled()
        };

        _startupItem.Click += (_, _) =>
        {
            try
            {
                StartupManager.SetEnabled(!StartupManager.IsEnabled());
            }
            catch (Exception ex)
            {
                _logger?.Error($"Failed to update login startup: {ex}");
            }
            finally
            {
                try
                {
                    _startupItem!.IsChecked = StartupManager.IsEnabled();
                }
                catch (Exception ex)
                {
                    _logger?.Error($"Failed to read login startup status: {ex}");
                    _startupItem!.IsChecked = false;
                }
            }
        };

        _openHostsItem = new NativeMenuItem("Open proxiedhosts.txt")
        {
            IsEnabled = false
        };

        _openHostsItem.Click += (_, _) =>
        {
            OpenHostsFile();
        };

        _openLogItem = new NativeMenuItem("Open log");

        _openLogItem.Click += (_, _) =>
        {
            OpenLogFile();
        };

        var quitItem = new NativeMenuItem("Quit ProxiedHosts");

        quitItem.Click += (_, _) =>
        {
            _ = ShutdownAsync();
        };

        var menu = new NativeMenu
        {
            _statusItem,
            _proxyItem,

            new NativeMenuItemSeparator(),

            _mappingsItem,
            _startupItem,

            new NativeMenuItemSeparator(),

            _openHostsItem,
            _openLogItem,

            new NativeMenuItemSeparator(),

            quitItem
        };

        var iconFile = OperatingSystem.IsMacOS() ? "ProxiedHostsTemplate.png" : "ProxiedHostsTray.ico";
        using var iconStream = AssetLoader.Open(new Uri($"avares://proxiedhosts-tray/Assets/{iconFile}"));

        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(iconStream),
            ToolTipText = "ProxiedHosts - Starting",
            Menu = menu
        };

        if (OperatingSystem.IsMacOS())
        {
            MacOSProperties.SetIsTemplateIcon(_trayIcon, true);
        }

        TrayIcon.SetIcons(this, new TrayIcons
            {
                _trayIcon
            });
    }

    private async Task StartProxyAsync()
    {
        try
        {
            await _controller!.StartAsync();

            UpdateTrayState();
        }
        catch (Exception ex)
        {
            _logger?.Error($"Failed to start proxy: {ex}");

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _statusItem!.Header = "Status: Failed";
                _mappingsItem!.IsEnabled = false;
                _openHostsItem!.IsEnabled = false;
                _trayIcon!.ToolTipText = "ProxiedHosts - Failed";
            });
        }
    }

    private void OnControllerChanged()
    {
        Dispatcher.UIThread.Post(UpdateTrayState);
    }

    private void UpdateTrayState()
    {
        if (_controller is null)
        {
            return;
        }

        _statusItem!.Header = _controller.IsActive ? "Status: Active" : "Status: Inactive";
        _proxyItem!.Header = $"Proxy: {_controller.ProxyAddress}";
        _mappingsItem!.Header = $"Enable mappings ({_controller.MappingCount})";
        _mappingsItem.IsChecked = _controller.IsActive;
        _mappingsItem.IsEnabled = true;
        _openHostsItem!.IsEnabled = true;
        _trayIcon!.ToolTipText = _controller.IsActive ? $"ProxiedHosts - Active ({_controller.MappingCount} mappings)" : "ProxiedHosts - Inactive";
    }

    private void OpenHostsFile()
    {
        if (_controller is null)
        {
            return;
        }

        try
        {
            FileLauncher.Open(_controller.HostsFilePath);
        }
        catch (Exception ex)
        {
            _logger?.Error($"Failed to open hosts file: {ex.Message}");
        }
    }

    private void OpenLogFile()
    {
        if (_logger is null)
        {
            return;
        }

        try
        {
            FileLauncher.Open(_logger.FilePath);
        }
        catch
        {
            // Nothing else is available to log to.
        }
    }

    private async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            return;
        }

        if (_controller is not null)
        {
            _controller.Changed -= OnControllerChanged;

            await _controller.StopAsync();
        }

        _trayIcon?.Dispose();
        _desktop?.Shutdown();
    }
}