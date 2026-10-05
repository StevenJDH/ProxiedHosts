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

namespace ProxiedHosts.Hosts;

internal sealed class HostFileWatcher : IDisposable
{
    private readonly HostMap _hostMap;
    private readonly FileSystemWatcher _watcher;
    private readonly object _gate = new();
    private Timer? _debounceTimer;

    public HostFileWatcher(HostMap hostMap)
    {
        _hostMap = hostMap;

        var directory = System.IO.Path.GetDirectoryName(hostMap.Path)
                        ?? throw new InvalidOperationException("Unable to determine hosts file directory.");
        var fileName = System.IO.Path.GetFileName(hostMap.Path);

        _watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };

        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Renamed += OnChanged;
    }

    private void OnChanged(object? sender, FileSystemEventArgs e)
    {
        lock (_gate)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => ReloadSafely(), null, 250, Timeout.Infinite);
        }
    }

    private void ReloadSafely()
    {
        try
        {
            _hostMap.Reload();
            Console.WriteLine($"[{DateTimeOffset.Now:T}] Reloaded {_hostMap.Count} host mapping(s).");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{DateTimeOffset.Now:T}] Hosts reload failed: {ex.Message}");
            Console.Error.WriteLine("The previous valid mappings remain active.");
        }
    }

    public void Dispose()
    {
        _watcher.Dispose();
        lock (_gate)
        {
            _debounceTimer?.Dispose();
        }
    }
}