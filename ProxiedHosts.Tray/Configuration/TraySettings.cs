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

using System.Text.Json;
using ProxiedHosts.Core.Infrastructure;

namespace ProxiedHosts.Tray.Configuration;

/// <summary>
/// Manages persistent settings and update notifications for the tray application.
/// </summary>
internal sealed class TraySettings
{
    public bool IncludePreviewReleases { get; private set; }

    public bool MappingsActive { get; private set; } = true;

    public Version? LastNotifiedStableVersion { get; private set; }

    public Version? LastNotifiedPreviewVersion { get; private set; }

    public static TraySettings Load()
    {
        var settings = new TraySettings();
        var path = SettingsPath;

        CleanupTemporarySettings(path);

        if (!File.Exists(path))
        {
            return settings;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return settings;
        }

        if (root.TryGetProperty("includePreviewReleases", out var preview) && preview.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            settings.IncludePreviewReleases = preview.GetBoolean();
        }

        if (root.TryGetProperty("mappingsActive", out var mappingsActive) && mappingsActive.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            settings.MappingsActive = mappingsActive.GetBoolean();
        }

        settings.LastNotifiedStableVersion = ReadVersion(root, "lastNotifiedStableVersion");
        settings.LastNotifiedPreviewVersion = ReadVersion(root, "lastNotifiedPreviewVersion");

        return settings;
    }

    private static string SettingsPath => Path.Combine(ApplicationPaths.GetDataDirectory(), "tray-settings.json");

    private static void CleanupTemporarySettings(string settingsPath)
    {
        var directory = Path.GetDirectoryName(settingsPath)!;
        var pattern = $"{Path.GetFileName(settingsPath)}.*.tmp";

        try
        {
            foreach (var temporaryPath in Directory.EnumerateFiles(directory, pattern))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Cleanup is best-effort. Continue with the remaining files.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temporary-file cleanup must not prevent settings from loading.
        }
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

    public void SetMappingsActive(bool active)
    {
        var previousValue = MappingsActive;
        MappingsActive = active;

        try
        {
            Save();
        }
        catch
        {
            MappingsActive = previousValue;
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

    /// <summary>
    /// Atomically saves tray settings by writing to a temporary file before
    /// replacing the existing file, preventing corruption from interrupted writes.
    /// </summary>
    private void Save()
    {
        var path = SettingsPath;
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            // Write the complete settings file before replacing the original. Keep scoped using for this.
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions {Indented = true});

                writer.WriteStartObject();
                writer.WriteBoolean("includePreviewReleases", IncludePreviewReleases);
                writer.WriteBoolean("mappingsActive", MappingsActive);

                if (LastNotifiedStableVersion is not null)
                {
                    writer.WriteString("lastNotifiedStableVersion", LastNotifiedStableVersion.ToString());
                }

                if (LastNotifiedPreviewVersion is not null)
                {
                    writer.WriteString("lastNotifiedPreviewVersion", LastNotifiedPreviewVersion.ToString());
                }

                writer.WriteEndObject();
                writer.Flush();

                // Request that buffered file data be flushed to storage.
                stream.Flush(flushToDisk: true);
            }

            // Replace the original only after the new file is complete.
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // Cleanup is best-effort. Preserves any original save exceptions.
            }
        }
    }
}