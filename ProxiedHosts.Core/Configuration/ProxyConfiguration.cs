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
using ProxiedHosts.Core.Infrastructure;

namespace ProxiedHosts.Core.Configuration;

public sealed record ProxyConfiguration(string ApplicationName, IPAddress ListenAddress, string HostsFilePath, string PortFilePath, ConnectionLogMode ConnectionLogMode)
{
    public static ProxyConfiguration Instance { get; } = Load();

    public int MaxHeaderBytes { get; init; } = 64 * 1024;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan HeaderReadTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Specifies the maximum period of inactivity allowed for HTTPS CONNECT tunnels.
    /// </summary>
    /// <remarks>
    /// By default, no application-enforced idle timeout is applied, allowing
    /// long-lived connections to remain open without requiring continuous traffic.
    /// A finite timeout can be configured to close tunnels after a period without
    /// successfully forwarded data in either direction, helping release inactive
    /// connection resources. However, this may interrupt otherwise healthy idle
    /// connections. Regardless of the configured timeout, tunnels remain open
    /// until an endpoint disconnects, an error occurs, or the proxy shuts down.
    /// </remarks>
    public TimeSpan TunnelIdleTimeout { get; init; } = Timeout.InfiniteTimeSpan;

    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan MappingReloadDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Controls whether the proxy adds its Via header to forwarded HTTP
    /// requests and responses, as required by RFC 9110, Section 7.6.3.
    /// </summary>
    /// <remarks>
    /// Enabled by default for standards compliance. Disabling it provides
    /// a compatibility workaround for servers that reject proxy-identifying headers.
    /// </remarks>
    public bool EnableViaHeader { get; init; } = true;

    private static ProxyConfiguration Load()
    {
        var directory = ApplicationPaths.GetDataDirectory();

        return new ProxyConfiguration(ApplicationName: ApplicationMetadata.Instance.Product,
                                      ListenAddress: IPAddress.Loopback,
                                      HostsFilePath: Path.Combine(directory, "proxiedhosts.txt"),
                                      PortFilePath: Path.Combine(directory, "proxy.port"),
                                      ConnectionLogMode: ConnectionLogMode.MappedOnly);
    }
}