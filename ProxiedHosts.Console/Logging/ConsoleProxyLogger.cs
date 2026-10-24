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

namespace ProxiedHosts.Console.Logging;

internal sealed class ConsoleProxyLogger : IProxyLogger
{
    public void Debug(string message) => Write("DEBUG", message);

    public void Information(string message) => Write("INFO", message);

    public void Warning(string message) => Write("WARN", message);

    public void Error(string message) => Write("ERROR", message);

    // Using human-readable timestamp based on ISO 8601, which uses a space instead of the 'T' separator like when setting the 's' format specifier.
    private static void Write(string level, string message) => System.Console.Error.WriteLine($"[{DateTimeOffset.Now:MM/dd/yyyy HH:mm:ss}] [{level,-5}] {message}");
}