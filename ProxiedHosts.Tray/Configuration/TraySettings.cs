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
using System.Text.Json;

namespace ProxiedHosts.Tray.Configuration;

/// <summary>
/// Manages persistent settings and update notifications for the tray application.
/// </summary>
internal sealed class TraySettings
{
    private static string SettingsPath
    {
        get
        {
            var directory = Path.GetDirectoryName(ProxyConfiguration.Instance.HostsFilePath)
                ?? throw new InvalidOperationException("Could not determine the application data directory.");

            return Path.Combine(directory, "tray-settings.json");
        }
    }

    public bool IncludePreviewReleases { get; private set; }

    public Version? LastNotifiedStableVersion { get; private set; }

    public Version? LastNotifiedPreviewVersion { get; private set; }

    public static TraySettings Load()
    {
        var settings = new TraySettings();

        if (!File.Exists(SettingsPath))
        {
            return settings;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return settings;
        }

        if (root.TryGetProperty("includePreviewReleases", out var preview) && preview.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            settings.IncludePreviewReleases = preview.GetBoolean();
        }

        settings.LastNotifiedStableVersion = ReadVersion(root, "lastNotifiedStableVersion");
        settings.LastNotifiedPreviewVersion = ReadVersion(root, "lastNotifiedPreviewVersion");

        return settings;
    }

    public bool WasNotified(Version version, bool isPreview)
    {
        var previousVersion = isPreview ? LastNotifiedPreviewVersion : LastNotifiedStableVersion;

        return previousVersion is not null && version <= previousVersion;
    }

    public void MarkNotified(Version version, bool isPreview)
    {
        var previousStable = LastNotifiedStableVersion;
        var previousPreview = LastNotifiedPreviewVersion;

        if (isPreview)
        {
            LastNotifiedPreviewVersion = version;
        }
        else
        {
            LastNotifiedStableVersion = version;
        }

        try
        {
            Save();
        }
        catch
        {
            LastNotifiedStableVersion = previousStable;
            LastNotifiedPreviewVersion = previousPreview;
            throw;
        }
    }

    public void SetIncludePreviewReleases(bool enabled)
    {
        var previousValue = IncludePreviewReleases;
        IncludePreviewReleases = enabled;

        try
        {
            Save();
        }
        catch
        {
            IncludePreviewReleases = previousValue;
            throw;
        }
    }

    private static Version? ReadVersion(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && Version.TryParse(value.GetString(), out var version))
        {
            return version;
        }

        return null;
    }

    private void Save()
    {
        var path = SettingsPath;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        writer.WriteBoolean("includePreviewReleases", IncludePreviewReleases);

        if (LastNotifiedStableVersion is not null)
        {
            writer.WriteString("lastNotifiedStableVersion", LastNotifiedStableVersion.ToString());
        }

        if (LastNotifiedPreviewVersion is not null)
        {
            writer.WriteString("lastNotifiedPreviewVersion", LastNotifiedPreviewVersion.ToString());
        }

        writer.WriteEndObject();
    }
}