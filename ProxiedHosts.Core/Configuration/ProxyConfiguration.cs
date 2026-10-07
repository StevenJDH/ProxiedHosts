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

using ProxiedHosts.Core.Proxy;
using System.Net;

namespace ProxiedHosts.Core.Configuration;

public sealed record ProxyConfiguration(string ApplicationName, IPAddress ListenAddress, string HostsFilePath, ConnectionLogMode ConnectionLogMode)
{
    public static ProxyConfiguration Load()
    {
        return new ProxyConfiguration(ApplicationName: "ProxiedHosts",
                                      ListenAddress: IPAddress.Loopback,
                                      HostsFilePath: Path.Combine(AppContext.BaseDirectory, "proxiedhosts.txt"),
                                      ConnectionLogMode: ConnectionLogMode.MappedOnly);
    }
}