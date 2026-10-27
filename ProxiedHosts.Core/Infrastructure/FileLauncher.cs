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

using System.Diagnostics;

namespace ProxiedHosts.Core.Infrastructure;

public static class FileLauncher
{
    private static readonly TimeSpan LaunchCooldown = TimeSpan.FromSeconds(2);
    private static readonly Lock LaunchGate = new();
    private static readonly Dictionary<string, long> LastLaunches = new(StringComparer.Ordinal);

    public static void Open(string path)
    {
        LaunchWithCooldown($"file:{Path.GetFullPath(path)}", () => OpenFile(path));
    }
    
    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("A valid URL is required.", nameof(url));
        }

        var isHttps = uri.Scheme == Uri.UriSchemeHttps;
        var isLocalHttp = uri.Scheme == Uri.UriSchemeHttp && uri.Host.Equals("127.0.0.1", StringComparison.Ordinal) && string.IsNullOrEmpty(uri.UserInfo);

        if (!isHttps && !isLocalHttp)
        {
            throw new ArgumentException("A valid HTTPS URL or loopback HTTP URL is required.", nameof(url));
        }

        LaunchWithCooldown($"url:{uri.AbsoluteUri}", () => OpenUrlCore(uri));
    }
    
    public static void OpenSystemProxySettings()
    {
        LaunchWithCooldown("settings:proxy", OpenSystemProxySettingsCore);
    }

    private static void LaunchWithCooldown(string key, Action launch)
    {
        lock (LaunchGate)
        {
            // Using monotonic timer so not affected by system clock.
            var now = Stopwatch.GetTimestamp();

            if (LastLaunches.TryGetValue(key, out var lastLaunchedAt) && Stopwatch.GetElapsedTime(lastLaunchedAt, now) < LaunchCooldown)
            {
                return;
            }

            // Record only successful launches, allowing immediate retries on failure.
            launch();
            LastLaunches[key] = Stopwatch.GetTimestamp();
        }
    }

    private static void OpenFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });

            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsMacOS() ? "open" : "xdg-open",
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add(path);

        using var launchedProcess = Process.Start(startInfo);
    }

    private static void OpenUrlCore(Uri uri)
    {
        ProcessStartInfo startInfo;

        if (OperatingSystem.IsWindows())
        {
            startInfo = new ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true
            };
        }
        else if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            startInfo = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open")
            {
                UseShellExecute = false
            };

            startInfo.ArgumentList.Add(uri.AbsoluteUri);
        }
        else
        {
            throw new PlatformNotSupportedException("Opening URLs is not supported on this platform.");
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Unable to open {uri.AbsoluteUri}.");
    }

    private static void OpenSystemProxySettingsCore()
    {
        ProcessStartInfo startInfo;

        if (OperatingSystem.IsWindows())
        {
            startInfo = new ProcessStartInfo("ms-settings:network-proxy")
            {
                UseShellExecute = true
            };
        }
        else if (OperatingSystem.IsMacOS())
        {
            startInfo = new ProcessStartInfo("/usr/bin/open")
            {
                UseShellExecute = false
            };

            var settingsUrl = OperatingSystem.IsMacOSVersionAtLeast(13)
                ? "x-apple.systempreferences:com.apple.Network-Settings.extension?Proxies"
                : "x-apple.systempreferences:com.apple.preference.network";

            startInfo.ArgumentList.Add(settingsUrl);
        }
        else if (OperatingSystem.IsLinux())
        {
            var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty;

            if (desktop.Contains("KDE", StringComparison.OrdinalIgnoreCase))
            {
                startInfo = new ProcessStartInfo("/usr/bin/systemsettings")
                {
                    UseShellExecute = false
                };

                startInfo.ArgumentList.Add("kcm_proxy");
            }
            else if (desktop.Contains("GNOME", StringComparison.OrdinalIgnoreCase))
            {
                startInfo = new ProcessStartInfo("/usr/bin/gnome-control-center")
                {
                    UseShellExecute = false
                };

                startInfo.ArgumentList.Add("network");
            }
            else
            {
                throw new PlatformNotSupportedException($"Opening proxy settings is not supported for desktop '{desktop}'.");
            }

            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;

            var linuxProcess = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to open system proxy settings.");

            _ = DrainDesktopOutputAsync(linuxProcess);

            return;
        }
        else
        {
            throw new PlatformNotSupportedException("Opening system proxy settings is not supported on this platform.");
        }

        using var process = Process.Start(startInfo);

        if (process is null && !startInfo.UseShellExecute)
        {
            throw new InvalidOperationException("Unable to open system proxy settings.");
        }
    }

    /// <summary>
    /// Asynchronously drains and discards a desktop process's standard output
    /// and error streams, suppressing diagnostic messages in the console.
    /// </summary>
    /// <remarks>
    /// Both redirected streams must be consumed concurrently to prevent the
    /// child process from blocking when either output buffer becomes full.
    /// Simply redirecting a stream without reading it can cause a deadlock.
    /// See <see href="https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.redirectstandarderror?view=net-10.0">
    /// Microsoft's redirected process output documentation</see>.
    /// The process is disposed after its output has been drained.
    /// </remarks>
    /// <param name="process">The launched desktop process to drain and dispose.</param>
    private static async Task DrainDesktopOutputAsync(Process process)
    {
        using (process)
        {
            try
            {
                await Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(Stream.Null), process.StandardError.BaseStream.CopyToAsync(Stream.Null));
                await process.WaitForExitAsync();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                // The settings process may exit while its output is being drained.
            }
        }
    }
}