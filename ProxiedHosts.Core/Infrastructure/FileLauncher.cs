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
    public static void Open(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo
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

        Process.Start(startInfo);
    }

    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("A valid HTTPS URL is required.", nameof(url));
        }

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