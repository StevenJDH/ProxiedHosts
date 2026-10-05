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

namespace ProxiedHosts.Hosts;

internal sealed class HostMap
{
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, IPAddress> _entries = new(StringComparer.OrdinalIgnoreCase);

    public HostMap(string path) => _path = path;

    public string Path => _path;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public bool TryResolve(string host, out IPAddress? address)
    {
        host = NormalizeHost(host);
        lock (_gate)
        {
            return _entries.TryGetValue(host, out address);
        }
    }

    public void Reload()
    {
        var next = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
        var lines = File.ReadAllLines(_path);

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

            var parts = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
            {
                throw new FormatException($"Invalid hosts entry at line {i + 1}: expected '<ip> <hostname>'.");
            }

            if (!IPAddress.TryParse(parts[0], out var ip))
            {
                throw new FormatException($"Invalid IP address '{parts[0]}' at line {i + 1}.");
            }

            for (var p = 1; p < parts.Length; p++)
            {
                var host = NormalizeHost(parts[p]);
                if (host.Length == 0)
                {
                    throw new FormatException($"Invalid empty hostname at line {i + 1}.");
                }

                next[host] = ip;
            }
        }

        lock (_gate)
        {
            _entries = next;
        }
    }

    private static string NormalizeHost(string host) => host.Trim().TrimEnd('.');
}