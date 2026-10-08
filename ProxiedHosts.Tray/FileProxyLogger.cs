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

namespace ProxiedHosts.Tray;

internal sealed class FileProxyLogger : IProxyLogger
{
    private readonly Lock _gate = new();

    public string FilePath { get; }

    public FileProxyLogger()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProxiedHosts");

        Directory.CreateDirectory(directory);

        FilePath = Path.Combine(directory, "proxiedhosts.log");
    }

    public void Information(string message)
    {
        Write("INFO", message);
    }

    public void Warning(string message)
    {
        Write("WARN", message);
    }

    public void Error(string message)
    {
        Write("ERROR", message);
    }

    private void Write(string level, string message)
    {
        try
        {
            lock (_gate)
            {
                File.AppendAllText(FilePath, $"{level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never interfere with proxy operation.
        }
    }
}