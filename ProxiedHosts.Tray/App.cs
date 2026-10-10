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

using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using ProxiedHosts.Core.Infrastructure;
using ProxiedHosts.Tray.Configuration;
using ProxiedHosts.Tray.Dialogs;
using ProxiedHosts.Tray.Infrastructure;

namespace ProxiedHosts.Tray;

public sealed class App : Application
{
    private TraySettings _settings = new();

    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private TrayProxyController? _controller;
    private FileProxyLogger? _logger;

    private TrayIcon? _trayIcon;
    private NativeMenuItem? _statusItem;
    private NativeMenuItem? _proxyItem;
    private NativeMenuItem? _mappingsItem;
    private NativeMenuItem? _startupItem;
    private NativeMenuItem? _checkUpdatesItem;
    private NativeMenuItem? _previewReleasesItem;
    private UpdateDialog? _updateDialog;
    private NativeMenuItem? _openHostsItem;
    private NativeMenuItem? _openLogItem;
    private AboutDialog? _aboutDialog;

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

            try
            {
                _settings = TraySettings.Load();
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to load tray settings: {ex.Message}");
            }


            CreateTrayIcon();

            _controller = new TrayProxyController(_logger);
            _controller.Changed += OnControllerChanged;

            _ = StartProxyAsync();

            // Check once at startup without blocking application initialization.
            Dispatcher.UIThread.Post(() => _ = CheckForUpdatesAsync(automatic: true));
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

        var donationUrl = typeof(App).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "FixedDonationUrl")?
            .Value ?? string.Empty;

        var donateItem = new NativeMenuItem("Donate 5€ (PayPal)...")
        {
            IsEnabled = Uri.TryCreate(donationUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        };

        donateItem.Click += (_, _) =>
        {
            try
            {
                FileLauncher.OpenUrl(donationUrl);
            }
            catch (Exception ex)
            {
                _logger?.Error($"Failed to open donation page: {ex}");
            }
        };

        _checkUpdatesItem = new NativeMenuItem("Check for updates");

        _checkUpdatesItem.Click += (_, _) =>
        {
            _ = CheckForUpdatesAsync();
        };

        _previewReleasesItem = new NativeMenuItem("Include preview releases")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _settings.IncludePreviewReleases
        };

        _previewReleasesItem.Click += (_, _) =>
        {
            try
            {
                _settings.SetIncludePreviewReleases(!_settings.IncludePreviewReleases);
            }
            catch (Exception ex)
            {
                _logger?.Error($"Failed to save update preferences: {ex.Message}");
            }
            finally
            {
                _previewReleasesItem!.IsChecked = _settings.IncludePreviewReleases;
            }
        };

        var aboutItem = new NativeMenuItem("About ProxiedHosts");

        aboutItem.Click += (_, _) =>
        {
            ShowAboutDialog();
        };

        var helpItem = new NativeMenuItem("Help")
        {
            Menu = new NativeMenu
            {
                donateItem,

                new NativeMenuItemSeparator(),

                _checkUpdatesItem!,
                _previewReleasesItem!,

                new NativeMenuItemSeparator(),

                aboutItem
            }
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

            helpItem,

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

    private async Task CheckForUpdatesAsync(bool automatic = false)
    {
        if (_updateDialog is not null)
        {
            if (!automatic)
            {
                _updateDialog.Activate();
            }

            return;
        }

        if (_checkUpdatesItem is null || !_checkUpdatesItem.IsEnabled)
        {
            return;
        }

        _checkUpdatesItem.IsEnabled = false;
        _previewReleasesItem!.IsEnabled = false;

        try
        {
            var currentVersion = typeof(App).Assembly.GetName().Version ?? throw new InvalidOperationException("Could not determine the installed version.");
            var update = await UpdateChecker.CheckAsync(currentVersion, _settings.IncludePreviewReleases);

            if (Volatile.Read(ref _shutdownStarted) != 0)
            {
                return;
            }

            if (update is null)
            {
                if (!automatic)
                {
                    ShowUpdateDialog($"ProxiedHosts is up to date.\n\nInstalled version: {currentVersion}");
                }

                return;
            }

            // Automatic checks notify only once per release version.
            if (automatic && _settings.WasNotified(update.Version, update.IsPreview))
            {
                return;
            }

            // Avoid opening a second dialog if another is already visible.
            if (_updateDialog is not null)
            {
                return;
            }

            var releaseType = update.IsPreview ? "preview version" : "version";

            ShowUpdateDialog($"""
                A new {releaseType} of ProxiedHosts is available.
                         
                Installed version: {currentVersion}
                Available version: {update.Version}
                  
                "Would you like to open the GitHub release page?"
                """,
                () =>
                {
                    try
                    {
                        FileLauncher.OpenUrl(update.Url);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Error($"Failed to open release page: {ex}");

                        ShowUpdateDialog($"Unable to open the GitHub release page.\n\n{ex.Message}");
                    }
                });

            if (automatic)
            {
                // Remember that this release has already been announced,
                // regardless of whether the user chooses Yes or No.
                try
                {
                    _settings.MarkNotified(update.Version, update.IsPreview);
                }
                catch (Exception ex)
                {
                    _logger?.Warning($"Failed to save update notification state: {ex}");
                }
            }
        }
        catch (TaskCanceledException ex)
        {
            _logger?.Warning($"Update check timed out: {ex}");

            if (!automatic)
            {
                ShowUpdateDialog("The update check timed out.\n\nCheck the internet connection and try again.");
            }
        }
        catch (HttpRequestException ex)
        {
            _logger?.Warning($"Update check failed: {ex}");

            if (!automatic)
            {
                var message = ex.StatusCode is { } status
                    ? $"GitHub returned HTTP {(int)status} ({status}).\n\nThe update check could not be completed. Please try again later."
                    : "Unable to connect to GitHub.\n\nCheck the internet connection and try again.";

                ShowUpdateDialog(message);
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger?.Warning($"Invalid GitHub response: {ex}");

            if (!automatic)
            {
                ShowUpdateDialog("GitHub returned an invalid response.\n\nPlease try again later.");
            }
        }
        catch (Exception ex)
        {
            _logger?.Error($"Update check failed: {ex}");

            if (!automatic)
            {
                ShowUpdateDialog($"An unexpected error occurred while checking for updates.\n\n{ex.Message}");
            }
        }
        finally
        {
            _checkUpdatesItem.IsEnabled = true;
            _previewReleasesItem.IsEnabled = true;
        }
    }

    private void ShowUpdateDialog(string message, Action? onYes = null)
    {
        if (_updateDialog is not null)
        {
            _updateDialog.Activate();
            return;
        }

        var dialog = new UpdateDialog(message, onYes);

        dialog.Closed += (_, _) =>
        {
            _updateDialog = null;
        };

        _updateDialog = dialog;

        dialog.Show();
        dialog.Activate();
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

    private void ShowAboutDialog()
    {
        if (_aboutDialog is not null)
        {
            _aboutDialog.Activate();
            return;
        }

        var dialog = new AboutDialog(message => _logger?.Error(message));

        dialog.Closed += (_, _) =>
        {
            _aboutDialog = null;
        };

        _aboutDialog = dialog;

        dialog.Show();
        dialog.Activate();
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

        _aboutDialog?.Close();
        _updateDialog?.Close();
        _trayIcon?.Dispose();
        _desktop?.Shutdown();
    }
}