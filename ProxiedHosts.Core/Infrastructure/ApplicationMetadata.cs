
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

using System.Reflection;

namespace ProxiedHosts.Core.Infrastructure;

/// <summary>
/// Provides metadata from the application's entry assembly.
/// </summary>
public sealed class ApplicationMetadata
{
    public static ApplicationMetadata Instance { get; } = new();

    public string Product { get; }
    public Version Version { get; }
    public string Authors { get; }
    public string Copyright { get; }
    public string RepositoryUrl { get; }
    public string DonationUrl { get; }
    public string FixedDonationUrl { get; }

    private ApplicationMetadata()
    {
        var assembly = Assembly.GetEntryAssembly() ?? throw new InvalidOperationException("Could not determine the application's entry assembly.");

        Product = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "ProxiedHosts";
        Version = assembly.GetName().Version ?? throw new InvalidOperationException("Could not determine the installed version.");
        Copyright = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? string.Empty;

        var attributes = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToArray();

        Authors = GetMetadata(attributes, "Authors");
        RepositoryUrl = GetMetadata(attributes, "RepositoryUrl");
        DonationUrl = GetMetadata(attributes, "DonationUrl");
        FixedDonationUrl = GetMetadata(attributes, "FixedDonationUrl");
    }

    private static string GetMetadata(IEnumerable<AssemblyMetadataAttribute> attributes, string key)
    {
        return attributes.FirstOrDefault(attribute => attribute.Key == key)?.Value ?? string.Empty;
    }
}