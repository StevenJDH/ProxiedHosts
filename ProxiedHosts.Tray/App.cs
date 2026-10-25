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
using ProxiedHosts.Core.Configuration;

namespace ProxiedHosts.Tray;

public sealed class App : Application
{
    private readonly FileProxyLogger _logger = new ();
    private TraySettings _settings = new();

    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private TrayProxyController? _controller;

    private const int MaxStartAttempts = 3;
    private static readonly TimeSpan StartRetryDelay = TimeSpan.FromSeconds(2);
    private readonly CancellationTokenSource _lifetimeCancellation = new();

    private TrayIcon? _trayIcon;
    private NativeMenuItem? _statusItem;
    private NativeMenuItem? _proxyItem;
    private NativeMenuItem? _startProxyItem;
    private NativeMenuItem? _mappingsItem;
    private NativeMenuItem? _startupItem;
    private NativeMenuItem? _checkUpdatesItem;
    private NativeMenuItem? _previewReleasesItem;
    private UpdateDialog? _updateDialog;
    private NativeMenuItem? _openHostsItem;
    private NativeMenuItem? _openStatusItem;
    private AboutDialog? _aboutDialog;

    private Task? _startTask;
    private bool _startingProxy;
    private int _startAttempts;
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

            ProxyStartupOptions startupOptions;

            try
            {
                startupOptions = ProxyStartupOptions.Parse(desktop.Args);
            }
            catch (ArgumentException ex)
            {
                _logger.Error($"Invalid command-line options: {ex.Message}");

                Dispatcher.UIThread.Post(() => desktop.Shutdown(1));
                base.OnFrameworkInitializationCompleted();
                return;
            }

            CreateTrayIcon();

            _controller = new TrayProxyController(_logger, startupOptions);
            _controller.Changed += OnControllerChanged;

            Dispatcher.UIThread.Post(() => BeginProxyStart());

            // Check once at startup without blocking application initialization.
            Dispatcher.UIThread.Post(() => _ = CheckForUpdatesAsync(automatic: true));
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void CreateTrayIcon()
    {
        var product = ApplicationMetadata.Instance.Product;

        _statusItem = new NativeMenuItem(" ◌ Status: Starting...")
        {
            IsEnabled = false
        };

        _proxyItem = new NativeMenuItem("↔ Proxy: Starting...")
        {
            IsEnabled = false
        };

        _startProxyItem = new NativeMenuItem("▶ Start proxy")
        {
            IsVisible = false
        };

        _startProxyItem.Click += (_, _) => BeginProxyStart(manual: true);

        var openProxySettingsItem = new NativeMenuItem("⚙ Open proxy settings...");

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

        _mappingsItem.Click += (_, _) => ToggleMappings();
        _startupItem = new NativeMenuItem(ToggleHeader("Start at login", StartupManager.IsEnabled()));
        _startupItem.Click += (_, _) => ToggleStartup();

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

        _openStatusItem = new NativeMenuItem("Open status dashboard")
        {
            IsEnabled = false
        };

        _openStatusItem.Click += (_, _) => OpenProxyStatus();

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

        var aboutItem = new NativeMenuItem($"About {product}");

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

        var quitItem = new NativeMenuItem($"Quit {product}");

        quitItem.Click += (_, _) =>
        {
            _ = ShutdownAsync();
        };

        var menu = new NativeMenu
        {
            _statusItem,
            _proxyItem,
            _startProxyItem,
            openProxySettingsItem,

            new NativeMenuItemSeparator(),

            _mappingsItem,
            _startupItem,

            new NativeMenuItemSeparator(),

            _openHostsItem,
            openLogItem,
            _openStatusItem,

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
            ToolTipText = $"{product} - Starting",
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

    private void ToggleMappings()
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
            _mappingsItem!.Header = ToggleHeader($"Enable mappings ({_controller.MappingCount})", _controller.IsActive);
        }
    }

    private void ToggleStartup()
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
                _startupItem!.Header = ToggleHeader("Start at login", StartupManager.IsEnabled());
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to read login startup status: {ex.Message}");
                _startupItem!.Header = ToggleHeader("Start at login", false);
            }
        }
    }

    private void BeginProxyStart(bool manual = false)
    {
        if (_startingProxy || Volatile.Read(ref _shutdownStarted) != 0)
        {
            return;
        }

        if (manual)
        {
            _startAttempts = 0;
        }

        if (_startAttempts >= MaxStartAttempts)
        {
            UpdateTrayState();
            return;
        }

        _startTask = StartProxyAsync();
    }
    
    private async Task StartProxyAsync()
    {
        _startingProxy = true;

        try
        {
            await Dispatcher.UIThread.InvokeAsync(UpdateTrayState);

            while (_startAttempts < MaxStartAttempts)
            {
                _lifetimeCancellation.Token.ThrowIfCancellationRequested();

                var attempt = ++_startAttempts;

                if (await TryStartProxyAsync(attempt))
                {
                    return;
                }

                if (_startAttempts < MaxStartAttempts)
                {
                    await Task.Delay(StartRetryDelay, _lifetimeCancellation.Token);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Application is shutting down.
        }
        finally
        {
            _startingProxy = false;

            if (Volatile.Read(ref _shutdownStarted) == 0)
            {
                await Dispatcher.UIThread.InvokeAsync(UpdateTrayState);
            }
        }
    }

    private async Task<bool> TryStartProxyAsync(int attempt)
    {
        try
        {
            if (attempt > 1)
            {
                _logger.Warning($"Retrying proxy startup (attempt {attempt}/{MaxStartAttempts})...");
            }

            await _controller!.StopAsync();

            _lifetimeCancellation.Token.ThrowIfCancellationRequested();

            await _controller.StartAsync(_settings.MappingsActive);

            if (_controller.HasFailed)
            {
                _logger.Warning("Proxy stopped during startup.");
                return false;
            }

            return true;
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"Proxy startup attempt {attempt}/{MaxStartAttempts} failed: {ex.Message}");

            return false;
        }
    }
    
    private void OnControllerChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (Volatile.Read(ref _shutdownStarted) != 0)
            {
                return;
            }

            if (_controller?.HasFailed == true && !_startingProxy && _startAttempts < MaxStartAttempts)
            {
                BeginProxyStart();
                return;
            }

            UpdateTrayState();
        });
    }

    private void UpdateTrayState()
    {
        var product = ApplicationMetadata.Instance.Product;

        if (_controller is null)
        {
            return;
        }

        if (_startingProxy)
        {
            _statusItem!.Header = " ◌ Status: Starting...";
            _proxyItem!.Header = "↔ Proxy: Starting...";
            _startProxyItem!.IsVisible = false;
            _mappingsItem!.IsEnabled = false;
            _openHostsItem!.IsEnabled = false;
            _openStatusItem!.IsEnabled = false;
            _trayIcon!.ToolTipText = $"{product} - Starting";

            return;
        }

        if (_controller.HasFailed)
        {
            _statusItem!.Header = "⚠Status: Failed";
            _proxyItem!.Header = "↔ Proxy: Stopped";
            _startProxyItem!.IsVisible = _startAttempts >= MaxStartAttempts;
            _mappingsItem!.Header = ToggleHeader($"Enable mappings ({_controller.MappingCount})", _settings.MappingsActive);
            _mappingsItem.IsEnabled = false;
            _openHostsItem!.IsEnabled = false;
            _openStatusItem!.IsEnabled = !string.IsNullOrEmpty(_controller.StatusUrl);
            _trayIcon!.ToolTipText = $"{product} - Failed";

            return;
        }

        var mappingCount = _controller.MappingCount;
        var s = mappingCount > 1 ? "s" : "";

        _statusItem!.Header = _controller.IsActive ? " ● Status: Active" : " ○ Status: Inactive";
        _proxyItem!.Header = $"↔ Proxy: {_controller.ProxyAddress}";
        _startProxyItem!.IsVisible = false;
        _mappingsItem!.Header = ToggleHeader($"Enable mappings ({mappingCount})", _controller.IsActive);
        _mappingsItem.IsEnabled = true;
        _openHostsItem!.IsEnabled = true;
        _openStatusItem!.IsEnabled = !string.IsNullOrEmpty(_controller.StatusUrl);
        _trayIcon!.ToolTipText = _controller.IsActive ? $"{product} - Active ({mappingCount} mapping{s})" : $"{product} - Inactive";
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
                throw new InvalidOperationException("Could not determine the repository URL.");
            }

            var update = await UpdateChecker.CheckAsync(currentVersion, repositoryUrl, _settings.IncludePreviewReleases, _lifetimeCancellation.Token);

            if (Volatile.Read(ref _shutdownStarted) != 0)
            {
                return;
            }

            HandleUpdateResult(update, currentVersion, automatic);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Application is shutting down.
        }
        catch (Exception ex)
        {
            HandleUpdateCheckError(ex, automatic);
        }
        finally
        {
            if (Volatile.Read(ref _shutdownStarted) == 0)
            {
                _checkUpdatesItem.IsEnabled = true;
                _previewReleasesItem.IsEnabled = true;
            }
        }
    }

    private void HandleUpdateResult(AvailableUpdate? update, Version currentVersion, bool automatic)
    {
        var product = ApplicationMetadata.Instance.Product;

        if (update is null)
        {
            if (!automatic)
            {
                ShowUpdateDialog($"{product} is up to date.\n\nInstalled version: {currentVersion}");
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
                          A new {releaseType} of {product} is available.
                                   
                          Installed version: {currentVersion}
                          Available version: {update.Version}
                            
                          Would you like to open the GitHub release page?
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

    private void OpenProxyStatus()
    {
        var url = _controller?.StatusUrl;

        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        try
        {
            FileLauncher.OpenUrl(url);
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to open proxy status dashboard: {ex.Message}");
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
            await _lifetimeCancellation.CancelAsync();

            if (_startTask is not null)
            {
                await _startTask;
            }

            if (_controller is not null)
            {
                _controller.Changed -= OnControllerChanged;

                await _controller.StopAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to stop proxy during shutdown: {ex.Message}");
        }
        finally
        {
            try
            {
                _lifetimeCancellation.Dispose();
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