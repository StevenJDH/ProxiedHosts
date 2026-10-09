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

using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Xml.Linq;
using ProxiedHosts.Core.Configuration;

namespace ProxiedHosts.Tray.Infrastructure;

/// <summary>
/// Manages per-user login startup registration for the tray application
/// using the platform's supported startup configuration.
/// </summary>
internal static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string MacAgentName = "io.github.stevenjdh.proxiedhosts.plist";

    private static string ApplicationName => ProxyConfiguration.Instance.ApplicationName;

    public static bool IsEnabled()
    {
        if (OperatingSystem.IsWindows())
        {
            return IsEnabledWindows();
        }

        if (OperatingSystem.IsMacOS())
        {
            return File.Exists(GetMacAgentPath());
        }

        if (OperatingSystem.IsLinux())
        {
            return File.Exists(GetLinuxDesktopPath());
        }

        return false;
    }

    public static void SetEnabled(bool enabled)
    {
        if (OperatingSystem.IsWindows())
        {
            SetEnabledWindows(enabled);
        }
        else if (OperatingSystem.IsMacOS())
        {
            SetEnabledMacOS(enabled);
        }
        else if (OperatingSystem.IsLinux())
        {
            SetEnabledLinux(enabled);
        }
        else
        {
            throw new PlatformNotSupportedException("Login startup is not supported on this platform.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsEnabledWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);

        var registeredPath = key?.GetValue(ApplicationName) as string;
        var expectedPath = $"\"{GetExecutablePath()}\"";

        return string.Equals(registeredPath, expectedPath, StringComparison.OrdinalIgnoreCase);
    }

    [SupportedOSPlatform("windows")]
    private static void SetEnabledWindows(bool enabled)
    {
        if (enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey)
                ?? throw new InvalidOperationException("Could not open the Windows startup registry key.");

            key.SetValue(ApplicationName, $"\"{GetExecutablePath()}\"", RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);

            key?.DeleteValue(ApplicationName, throwOnMissingValue: false);
        }
    }

    private static string GetMacAgentPath()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", MacAgentName);
    }

    private static void SetEnabledMacOS(bool enabled)
    {
        var path = GetMacAgentPath();

        if (!enabled)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        // The application must be running from a proper .app bundle.
        var appPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));

        if (!appPath.EndsWith(".app", StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(appPath, "Contents", "Info.plist")))
        {
            throw new InvalidOperationException("Start at login requires a packaged macOS .app bundle.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var plist = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"),
                new XElement("dict",
                    new XElement("key", "Label"),
                    new XElement("string", "io.github.stevenjdh.proxiedhosts"),
                    new XElement("key", "ProgramArguments"),
                    new XElement("array",
                        new XElement("string", "/usr/bin/open"),
                        new XElement("string", "-a"),
                        new XElement("string", appPath)),
                    new XElement("key", "RunAtLoad"),
                    new XElement("true"),
                    new XElement("key", "LimitLoadToSessionType"),
                    new XElement("string", "Aqua"))));

        plist.Save(path);
    }

    private static string GetLinuxDesktopPath()
    {
        var configDirectory = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

        if (string.IsNullOrWhiteSpace(configDirectory) || !Path.IsPathFullyQualified(configDirectory))
        {
            configDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return Path.Combine(configDirectory, "autostart", "proxiedhosts-tray.desktop");
    }

    private static void SetEnabledLinux(bool enabled)
    {
        var path = GetLinuxDesktopPath();

        if (!enabled)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var executable = EscapeDesktopArgument(GetExecutablePath());

        var desktopEntry = $"""
            [Desktop Entry]
            Type=Application
            Name={ApplicationName}
            Comment=ProxiedHosts tray application.
            Exec={executable}
            Terminal=false
            X-GNOME-Autostart-enabled=true

            """;

        File.WriteAllText(path, desktopEntry);
    }

    private static string EscapeDesktopArgument(string value)
    {
        if (value.Contains('\n') || value.Contains('\r'))
        {
            throw new ArgumentException("The executable path contains invalid characters.");
        }

        return "\"" + value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("`", "\\`")
            .Replace("$", "\\$")
            .Replace("%", "%%") + "\"";
    }

    private static string GetExecutablePath()
    {
        return Environment.ProcessPath ?? throw new InvalidOperationException("Could not determine the executable path.");
    }
}