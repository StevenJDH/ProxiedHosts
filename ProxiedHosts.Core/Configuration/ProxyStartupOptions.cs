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
using System.Globalization;

namespace ProxiedHosts.Core.Configuration;

/// <summary>
/// Represents command-line overrides for a proxy session.
/// </summary>
public sealed record ProxyStartupOptions
{
    /// <summary>
    /// Overrides the configured connection logging mode when specified.
    /// </summary>
    public ConnectionLogMode? LogModeOverride { get; init; }

    /// <summary>
    /// Enables detailed connection diagnostics.
    /// </summary>
    public bool EnableDiagnostics { get; init; }

    /// <summary>
    /// Overrides the configured Via header behavior when specified.
    /// </summary>
    public bool? ViaHeaderOverride { get; init; }

    /// <summary>
    /// Overrides the configured concurrent connection limit when specified.
    /// </summary>
    public ConnectionLimitOverride? MaxConcurrentConnectionsOverride { get; init; }

    /// <summary>
    /// Parses supported proxy command-line options.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    /// <returns>The parsed startup options.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when the maximum connection limit option is invalid.
    /// </exception>
    public static ProxyStartupOptions Parse(string[]? args)
    {
        args ??= [];

        var diagnostics = args.Contains("--log-diagnostics", StringComparer.OrdinalIgnoreCase);
        var logAll = diagnostics || args.Contains("--log-all", StringComparer.OrdinalIgnoreCase);

        return new ProxyStartupOptions
        {
            LogModeOverride = logAll ? ConnectionLogMode.All : null,
            EnableDiagnostics = diagnostics,
            ViaHeaderOverride = args.Contains("--no-via", StringComparer.OrdinalIgnoreCase) ? false : null,
            MaxConcurrentConnectionsOverride = ParseConnectionLimit(args)
        };
    }

    private static ConnectionLimitOverride? ParseConnectionLimit(string[] args)
    {
        ConnectionLimitOverride? result = null;

        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].Equals("--max-connections", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (result is not null)
            {
                throw new ArgumentException("--max-connections may only be specified once.");
            }

            if (i + 1 >= args.Length)
            {
                throw new ArgumentException("--max-connections requires a value.");
            }

            var value = args[i + 1];

            if (value.Equals("unlimited", StringComparison.OrdinalIgnoreCase))
            {
                result = new ConnectionLimitOverride(null);
            }
            else if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) && limit > 0)
            {
                result = new ConnectionLimitOverride(limit);
            }
            else
            {
                throw new ArgumentException("--max-connections must be a positive integer or 'unlimited'.");
            }
        }

        return result;
    }
}

/// <summary>
/// Represents an explicit concurrent connection limit override.
/// </summary>
/// <param name="Value">
/// The maximum number of connections, or null for unlimited connections.
/// </param>
public readonly record struct ConnectionLimitOverride(int? Value);