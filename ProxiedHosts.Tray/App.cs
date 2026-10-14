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
using ProxiedHosts.Tray.Configuration;
using ProxiedHosts.Tray.Dialogs;
using ProxiedHosts.Tray.Infrastructure;
using ProxiedHosts.Tray.Logging;
using ProxiedHosts.Tray.Proxy;
using System.Text.Json;

namespace ProxiedHosts.Tray;

public sealed class App : Application
{
    private readonly FileProxyLogger _logger = new ();
    private TraySettings _settings = new();

    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private TrayProxyController? _controller;

    private TrayIcon? _trayIcon;
    private NativeMenuItem? _statusItem;
    private NativeMenuItem? _proxyItem;
    private NativeMenuItem? _mappingsItem;
    private NativeMenuItem? _startupItem;
    private NativeMenuItem? _checkUpdatesItem;
    private NativeMenuItem? _previewReleasesItem;
    private UpdateDialog? _updateDialog;
    private NativeMenuItem? _openHostsItem;
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

        var openProxySettingsItem = new NativeMenuItem("Open proxy settings...");

        openProxySettingsItem.Click += (_, _) =>
        {
            try
            {
                FileLauncher.OpenSystemProxySettings();
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to open system proxy settings: {ex.Message}");
            }
        };

        _mappingsItem = new NativeMenuItem(ToggleHeader("Mappings active", _settings.MappingsActive))
        {
            IsEnabled = false
        };

        _mappingsItem.Click += (_, _) =>
        {
            if (_controller is null)
            {
                return;
            }

            try
            {
                _settings.SetMappingsActive(!_controller.IsActive);
                _controller.ToggleMappings();
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to change mappings state: {ex.Message}");
            }
            finally
            {
                _mappingsItem.Header = ToggleHeader($"Enable mappings ({_controller.MappingCount})", _controller.IsActive);
            }
        };

        _startupItem = new NativeMenuItem(ToggleHeader("Start at login", StartupManager.IsEnabled()));

        _startupItem.Click += (_, _) =>
        {
            try
            {
                StartupManager.SetEnabled(!StartupManager.IsEnabled());
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to update login startup: {ex.Message}");
            }
            finally
            {
                try
                {
                    _startupItem.Header = ToggleHeader("Start at login", StartupManager.IsEnabled());
                }
                catch (Exception ex)
                {
                    _logger.Error($"Failed to read login startup status: {ex.Message}");
                    _startupItem.Header = ToggleHeader("Start at login", false);
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

        var openLogItem = new NativeMenuItem("Open log");

        openLogItem.Click += (_, _) =>
        {
            OpenLogFile();
        };

        var donationUrl = ApplicationMetadata.Instance.FixedDonationUrl;

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
                _logger.Error($"Failed to open donation page: {ex.Message}");
            }
        };

        _checkUpdatesItem = new NativeMenuItem("Check for updates");

        _checkUpdatesItem.Click += (_, _) =>
        {
            _ = CheckForUpdatesAsync();
        };

        _previewReleasesItem = new NativeMenuItem(ToggleHeader("Include preview releases", _settings.IncludePreviewReleases));

        _previewReleasesItem.Click += (_, _) =>
        {
            try
            {
                _settings.SetIncludePreviewReleases(!_settings.IncludePreviewReleases);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to save update preferences: {ex.Message}");
            }
            finally
            {
                _previewReleasesItem.Header = ToggleHeader("Include preview releases", _settings.IncludePreviewReleases);
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

                _checkUpdatesItem,
                _previewReleasesItem,

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
            openProxySettingsItem,

            new NativeMenuItemSeparator(),

            _mappingsItem,
            _startupItem,

            new NativeMenuItemSeparator(),

            _openHostsItem,
            openLogItem,

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
            await _controller!.StartAsync(_settings.MappingsActive);

            UpdateTrayState();
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to start proxy: {ex}");

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

        if (_controller.HasFailed)
        {
            _statusItem!.Header = "Status: Failed";
            _proxyItem!.Header = "Proxy: Stopped";
            _proxyItem!.IsEnabled = false;
            _mappingsItem!.Header = ToggleHeader($"Enable mappings ({_controller.MappingCount})", false);
            _mappingsItem.IsEnabled = false;
            _openHostsItem!.IsEnabled = false;
            _trayIcon!.ToolTipText = "ProxiedHosts - Failed";

            return;
        }

        int mappingCount = _controller.MappingCount;
        string s = mappingCount > 1 ? "s" : "";

        _statusItem!.Header = _controller.IsActive ? "Status: Active" : "Status: Inactive";
        _proxyItem!.Header = $"Proxy: {_controller.ProxyAddress}";
        _mappingsItem!.Header = ToggleHeader($"Enable mappings ({mappingCount})", _controller.IsActive);
        _mappingsItem.IsEnabled = true;
        _openHostsItem!.IsEnabled = true;
        _trayIcon!.ToolTipText = _controller.IsActive ? $"ProxiedHosts - Active ({mappingCount} mapping{s})" : "ProxiedHosts - Inactive";
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
            var metadata = ApplicationMetadata.Instance;
            var currentVersion = metadata.Version;
            var repositoryUrl = metadata.RepositoryUrl;

            if (string.IsNullOrWhiteSpace(repositoryUrl))
            {
                throw new InvalidOperationException(
                    "Could not determine the repository URL.");
            }

            var update = await UpdateChecker.CheckAsync(currentVersion, repositoryUrl, _settings.IncludePreviewReleases);

            if (Volatile.Read(ref _shutdownStarted) != 0)
            {
                return;
            }

            HandleUpdateResult(update, currentVersion, automatic);
        }
        catch (Exception ex)
        {
            HandleUpdateCheckError(ex, automatic);
        }
        finally
        {
            _checkUpdatesItem.IsEnabled = true;
            _previewReleasesItem.IsEnabled = true;
        }
    }

    private void HandleUpdateResult(AvailableUpdate? update, Version currentVersion, bool automatic)
    {
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
            () => OpenReleasePage(update.Url));

        if (!automatic)
        {
            return;
        }

        // Remember that this release has already been announced,
        // regardless of whether the user chooses Yes or No.
        try
        {
            _settings.MarkNotified(update.Version, update.IsPreview);
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to save update notification state: {ex.Message}");
        }
    }
    
    private void OpenReleasePage(string url)
    {
        try
        {
            FileLauncher.OpenUrl(url);
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to open release page: {ex.Message}");

            ShowUpdateDialog($"Unable to open the GitHub release page.\n\n{ex.Message}");
        }
    }

    private void HandleUpdateCheckError(Exception ex, bool automatic)
    {
        string message;

        switch (ex)
        {
            case TaskCanceledException:
                _logger.Warning($"Update check timed out: {ex.Message}");
                message = "The update check timed out.\n\nCheck the internet connection and try again.";
                break;

            case HttpRequestException httpEx:
                _logger.Warning($"Update check failed: {httpEx.Message}");

                message = httpEx.StatusCode is { } status
                    ? $"GitHub returned HTTP {(int)status} ({status}).\n\nThe update check could not be completed. Please try again later."
                    : "Unable to connect to GitHub.\n\nCheck the internet connection and try again.";
                break;

            case JsonException:
                _logger.Warning($"Invalid GitHub response: {ex.Message}");
                message = "GitHub returned an invalid response.\n\nPlease try again later.";
                break;

            default:
                _logger.Error($"Update check failed: {ex.Message}");
                message = $"An unexpected error occurred while checking for updates.\n\n{ex.Message}";
                break;
        }

        if (!automatic)
        {
            ShowUpdateDialog(message);
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
            _logger.Error($"Failed to open hosts file: {ex.Message}");
        }
    }

    private void OpenLogFile()
    {
        try
        {
            FileLauncher.Open(_logger.FilePath);
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to open log file: {ex.Message}");
        }
    }

    private void ShowAboutDialog()
    {
        if (_aboutDialog is not null)
        {
            _aboutDialog.Activate();
            return;
        }

        var dialog = new AboutDialog(message => _logger.Error(message));

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

        try
        {
            if (_controller is not null)
            {
                _controller.Changed -= OnControllerChanged;

                await _controller.StopAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to stop proxy during shutdown: {ex}");
        }
        finally
        {
            try
            {
                _aboutDialog?.Close();
                _updateDialog?.Close();
                _trayIcon?.Dispose();
            }
            finally
            {
                _desktop?.Shutdown();
            }
        }
    }

    private static string ToggleHeader(string title, bool isChecked)
    {
        return $"{(isChecked ? '☑' : '☐')} {title}";
    }
}