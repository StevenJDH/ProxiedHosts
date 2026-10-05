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

using System.Text;

namespace ProxiedHosts.Hosts;

internal static class HostsFile
{
    public static void EnsureExists(string hostsPath)
    {
        if (File.Exists(hostsPath))
        {
            return;
        }

        const string template = """
                                # proxiedhosts.txt
                                # Format: <ip-address> <hostname> [hostname2 ...]
                                # Example:
                                # 127.0.0.1 myapp.local
                                # 192.168.1.50 api.example.com
                                """;

        File.WriteAllText(hostsPath, template, Encoding.UTF8);
        Console.WriteLine($"Created {hostsPath}");
    }
}