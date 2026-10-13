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
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("A valid HTTPS URL is required.", nameof(url));
        }

        LaunchWithCooldown($"url:{uri.AbsoluteUri}", () => OpenUrlCore(uri));
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
}