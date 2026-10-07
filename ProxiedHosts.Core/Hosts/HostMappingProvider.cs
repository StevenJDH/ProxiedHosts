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

using ProxiedHosts.Core.Logging;
using System.Net;
using System.Text;

namespace ProxiedHosts.Core.Hosts;

public sealed class HostMappingProvider : IDisposable
{
    private readonly Lock _watcherGate = new();
    private readonly Lock _reloadGate = new();

    private readonly string _filePath;
    private readonly IProxyLogger _logger;
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _reloadTimer;
    private bool _disposed;

    private volatile Dictionary<string, HostMapping> _mappings = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _mappings.Count;

    public HostMappingProvider(string filePath, IProxyLogger? logger = null)
    {
        _filePath = filePath;
        _logger = logger ?? NullProxyLogger.Instance;

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

    public bool TryResolve(string hostname, int port, out HostMapping mapping)
    {
        var host = NormalizeHost(hostname);

        // Port-specific mapping has priority.
        if (_mappings.TryGetValue(GetMappingKey(host, port), out mapping!))
        {
            return true;
        }

        // Fall back to a hostname-only mapping.
        return _mappings.TryGetValue(host, out mapping!);
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
                    _logger.Error($"Hosts file not found: {_filePath}");

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
                        _logger.Warning($"Ignoring invalid hosts entry on line {i + 1}: expected '<ip> <hostname>'.");
                        continue;
                    }

                    if (!TryParseEndpoint(parts[0], out var ip, out var port))
                    {
                        _logger.Warning($"Ignoring invalid IP address or port '{parts[0]}' on line {i + 1}.");
                        continue;
                    }

                    for (var j = 1; j < parts.Length; j++)
                    {
                        if (!TryParseHost(parts[j], out var host, out var matchPort))
                        {
                            _logger.Warning($"Ignoring invalid hostname or port '{parts[j]}' on line {i + 1}.");

                            continue;
                        }

                        nextMappings[GetMappingKey(host, matchPort)] = new HostMapping(host, ip!, port, matchPort);
                    }
                }

                // Atomic publication of the completely parsed snapshot.
                _mappings = nextMappings;

                _logger.Information($"[{DateTimeOffset.Now:T}] Loaded {_mappings.Count} host mapping(s).");
            }
            catch (IOException ex)
            {
                _logger.Error($"Could not reload hosts file: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.Error($"Could not access hosts file: {ex.Message}");
            }
        }
    }

    private static bool TryParseHost(string value, out string host, out int? port)
    {
        host = string.Empty;
        port = null;

        // Bracketed IPv6, optionally with a port:
        // [::1]
        // [::1]:443
        if (value.StartsWith('['))
        {
            var closingBracket = value.IndexOf(']');

            if (closingBracket <= 1)
            {
                return false;
            }

            host = NormalizeHost(value[1..closingBracket]);

            if (closingBracket == value.Length - 1)
            {
                return host.Length > 0;
            }

            if (value[closingBracket + 1] != ':')
            {
                return false;
            }

            return TryParsePort(value[(closingBracket + 2)..], out port);
        }

        var firstColon = value.IndexOf(':');
        var lastColon = value.LastIndexOf(':');

        // Exactly one colon means hostname:port.
        if (firstColon > 0 && firstColon == lastColon)
        {
            host = NormalizeHost(value[..firstColon]);

            return host.Length > 0 && TryParsePort(value[(firstColon + 1)..], out port);
        }

        // No colon, or an unbracketed IPv6 literal without a port.
        host = NormalizeHost(value);
        return host.Length > 0;
    }

    private static bool TryParseEndpoint(string value, out IPAddress? address, out int? port)
    {
        address = null;
        port = null;

        // IPv6 with port: [::1]:8080
        if (value.StartsWith('['))
        {
            var closingBracket = value.IndexOf(']');

            if (closingBracket <= 1)
            {
                return false;
            }

            var addressText = value[1..closingBracket];

            if (!IPAddress.TryParse(addressText, out address))
            {
                return false;
            }

            if (closingBracket == value.Length - 1)
            {
                return true;
            }

            if (value[closingBracket + 1] != ':')
            {
                return false;
            }

            return TryParsePort(value[(closingBracket + 2)..], out port);
        }

        // Plain IP address, including IPv6 without a port.
        if (IPAddress.TryParse(value, out address))
        {
            return true;
        }

        // IPv4 with port: 127.0.0.1:8080
        var colonIndex = value.LastIndexOf(':');

        if (colonIndex <= 0)
        {
            return false;
        }

        var addressPart = value[..colonIndex];
        var portPart = value[(colonIndex + 1)..];

        return IPAddress.TryParse(addressPart, out address) && TryParsePort(portPart, out port);
    }


    private static bool TryParsePort(string value, out int? port)
    {
        port = null;

        if (!int.TryParse(value, out var parsedPort) || parsedPort is < 1 or > 65535)
        {
            return false;
        }

        port = parsedPort;
        return true;
    }

    private static string GetMappingKey(string hostname, int? port)
    {
        var host = NormalizeHost(hostname);

        return port.HasValue
            ? $"{host}|{port.Value}"
            : host;
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
                                #
                                # Format:
                                # <ip-address>[:destination-port] <hostname>[:match-port] [hostname2[:match-port] ...]
                                #
                                # Examples:
                                #
                                # Map all ports and preserve the requested port:
                                # 127.0.0.1 myapp.local
                                #
                                # Map all ports to a fixed destination port:
                                # 127.0.0.1:8080 myapp.local
                                #
                                # Match only requests for port 443 and preserve port 443:
                                # 192.168.1.50 api.example.com:443
                                #
                                # Match only requests for port 443 and redirect them to port 8443:
                                # 192.168.1.50:8443 api.example.com:443
                                #
                                # Multiple hostnames may be listed on one line:
                                # 127.0.0.1:5000 app.local:80 api.local:8080
                                #
                                # IPv6 addresses with destination ports must use brackets:
                                # [::1]:8080 ipv6.example.com
                                """;

        File.WriteAllText(_filePath, template, Encoding.UTF8);
        _logger.Information($"Created {_filePath}");
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