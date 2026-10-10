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

using System.Globalization;
using System.IO.Compression;
using System.Text;
using ProxiedHosts.Core.Configuration;
using ProxiedHosts.Core.Logging;

namespace ProxiedHosts.Tray;

internal sealed class FileProxyLogger : IProxyLogger
{
    private const long MaxLogSizeBytes = 10L * 1024 * 1024;
    private const int MaxArchives = 3;

    private static readonly TimeSpan RotationRetryDelay = TimeSpan.FromMinutes(5);

    private readonly Lock _gate = new();
    private readonly string _directory;

    private DateTimeOffset _retryRotationAfterUtc;

    public string FilePath { get; }

    public FileProxyLogger()
    {
        _directory = Path.GetDirectoryName(ProxyConfiguration.Instance.HostsFilePath)!;
        FilePath = Path.Combine(_directory, "proxiedhosts.log");

        try
        {
            PruneArchives();

            // Remove incomplete ZIP files left by an interrupted rotation.
            foreach (var path in Directory.EnumerateFiles(_directory, "proxiedhosts-*.zip.tmp"))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Archive maintenance must not prevent application startup.
        }
    }

    public void Information(string message) => Write("INFO", message);

    public void Warning(string message) => Write("WARN", message);

    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var entry = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {level} {message}{Environment.NewLine}";

        try
        {
            lock (_gate)
            {
                var currentSize = File.Exists(FilePath) ? new FileInfo(FilePath).Length : 0;
                var incomingSize = Encoding.UTF8.GetByteCount(entry);

                if (currentSize > 0 && currentSize + incomingSize >= MaxLogSizeBytes && DateTimeOffset.UtcNow >= _retryRotationAfterUtc)
                {
                    try
                    {
                        Rotate();
                        _retryRotationAfterUtc = default;
                    }
                    catch
                    {
                        // Preserve the existing log and retry later.
                        _retryRotationAfterUtc = DateTimeOffset.UtcNow.Add(RotationRetryDelay);
                    }
                }

                File.AppendAllText(FilePath, entry);
            }
        }
        catch
        {
            // Logging must never interfere with proxy operation.
        }
    }

    private void Rotate()
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff'Z'", CultureInfo.InvariantCulture);
        var archiveName = $"proxiedhosts-{timestamp}";
        var archivePath = Path.Combine(_directory, $"{archiveName}.zip");

        // Avoid overwriting an archive if timestamps collide.
        for (var suffix = 1; File.Exists(archivePath); suffix++)
        {
            archivePath = Path.Combine(_directory, $"{archiveName}-{suffix}.zip");
        }

        var temporaryPath = archivePath + ".tmp";

        try
        {
            // Create the archive under a temporary name first.
            using (var archive = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(FilePath, "proxiedhosts.log", CompressionLevel.Optimal);
            }

            // Publish only a completed ZIP.
            File.Move(temporaryPath, archivePath);
            // Delete the original only after ZIP creation succeeds.
            File.Delete(FilePath);

            PruneArchives();
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // Temporary-file cleanup is best-effort.
            }
        }
    }

    private void PruneArchives()
    {
        var oldArchives = Directory
            .EnumerateFiles(_directory, "proxiedhosts-*.zip")
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .Skip(MaxArchives);

        foreach (var archive in oldArchives)
        {
            File.Delete(archive);
        }
    }
}