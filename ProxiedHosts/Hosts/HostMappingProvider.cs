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

using System.Net;
using System.Text;

namespace ProxiedHosts.Hosts;

internal sealed class HostMappingProvider : IDisposable
{
    private readonly Lock _watcherGate = new();
    private readonly Lock _reloadGate = new();

    private readonly string _filePath;
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _reloadTimer;
    private bool _disposed;

    private volatile Dictionary<string, HostMapping> _mappings = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _mappings.Count;

    public HostMappingProvider(string filePath)
    {
        _filePath = filePath;

        EnsureFileExists();
        Reload();

        var directory = Path.GetDirectoryName(_filePath) ?? throw new InvalidOperationException("Could not determine the hosts file directory.");
        var fileName = Path.GetFileName(_filePath);

        _reloadTimer = new Timer(_ => Reload(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        _watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
        };

        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Renamed += OnFileChanged;

        _watcher.EnableRaisingEvents = true;
    }

    public bool TryResolve(string hostname, out HostMapping mapping)
    {
        return _mappings.TryGetValue(NormalizeHost(hostname), out mapping!);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        lock (_watcherGate)
        {
            if (_disposed)
            {
                return;
            }

            // File editors often generate several events for one save.
            // Reset the one-shot timer each time so Reload() occurs
            // 250 ms after the final event to debounce them into a single reload.
            _reloadTimer.Change(TimeSpan.FromMilliseconds(250), Timeout.InfiniteTimeSpan);
        }
    }

    private void Reload()
    {
        lock (_reloadGate)
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    Console.Error.WriteLine($"Hosts file not found: {_filePath}");

                    return;
                }

                var nextMappings = new Dictionary<string, HostMapping>(StringComparer.OrdinalIgnoreCase);
                var lines = File.ReadAllLines(_filePath);

                for (var i = 0; i < lines.Length; i++)
                {
                    var raw = lines[i];
                    var commentIndex = raw.IndexOf('#');

                    if (commentIndex >= 0)
                    {
                        raw = raw[..commentIndex];
                    }

                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        continue;
                    }

                    var parts = raw.Split((char[]?) null,
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                    if (parts.Length < 2)
                    {
                        Console.Error.WriteLine(
                            $"Ignoring invalid hosts entry on line {i + 1}: expected '<ip> <hostname>'.");
                        continue;
                    }

                    if (!IPAddress.TryParse(parts[0], out var ip))
                    {
                        Console.Error.WriteLine($"Ignoring invalid IP address '{parts[0]}' on line {i + 1}.");
                        continue;
                    }

                    for (var j = 1; j < parts.Length; j++)
                    {
                        var host = NormalizeHost(parts[j]);

                        if (host.Length == 0)
                        {
                            Console.Error.WriteLine($"Ignoring invalid empty hostname on line {i + 1}.");
                            continue;
                        }

                        nextMappings[host] = new HostMapping(host, ip);
                    }
                }

                // Atomic publication of the completely parsed snapshot.
                _mappings = nextMappings;

                Console.WriteLine($"[{DateTimeOffset.Now:T}] Loaded {_mappings.Count} host mapping(s).");
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"Could not reload hosts file: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.Error.WriteLine($"Could not access hosts file: {ex.Message}");
            }
        }
    }

    private static string NormalizeHost(string host) => host.Trim().TrimEnd('.');

    private void EnsureFileExists()
    {
        if (File.Exists(_filePath))
        {
            return;
        }

        const string template = """
                                # proxiedhosts.txt
                                # Format: <ip-address> <hostname> [hostname2 ...]
                                #
                                # Examples:
                                # 127.0.0.1 myapp.local
                                # 192.168.1.50 api.example.com
                                """;

        File.WriteAllText(_filePath, template, Encoding.UTF8);
        Console.WriteLine($"Created {_filePath}");
    }

    public void Dispose()
    {
        lock (_watcherGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _watcher.EnableRaisingEvents = false;
        }

        _watcher.Changed -= OnFileChanged;
        _watcher.Created -= OnFileChanged;
        _watcher.Renamed -= OnFileChanged;

        _watcher.Dispose();
        _reloadTimer.Dispose();
    }
}