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
using System.Net.Sockets;
using System.Security.Cryptography;

namespace ProxiedHosts.Infrastructure;

internal sealed class StablePortStore
{
    private const int MinPort = 20000;
    private const int MaxPort = 45000;
    private readonly string _portFile;

    public StablePortStore(string appName)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (string.IsNullOrWhiteSpace(root))
        {
            root = AppContext.BaseDirectory;
        }

        var directory = System.IO.Path.Combine(root, appName);
        Directory.CreateDirectory(directory);
        _portFile = System.IO.Path.Combine(directory, "proxy.port");
    }

    public async Task<int> GetOrCreateAvailablePortAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_portFile))
        {
            var text = (await File.ReadAllTextAsync(_portFile, cancellationToken)).Trim();
            if (int.TryParse(text, out var persisted) && persisted is >= MinPort and <= MaxPort)
            {
                if (IsPortAvailable(persisted))
                {
                    return persisted;
                }

                throw new InvalidOperationException(
                    $"The persisted proxy port {persisted} is already in use. " +
                    $"Close the process using it, or delete '{_portFile}' to generate a new stable port.");
            }
        }

        for (var attempt = 0; attempt < 100; attempt++)
        {
            var candidate = RandomNumberGenerator.GetInt32(MinPort, MaxPort + 1);
            if (!IsPortAvailable(candidate))
            {
                continue;
            }

            await File.WriteAllTextAsync(_portFile, candidate.ToString(), cancellationToken);
            return candidate;
        }

        throw new InvalidOperationException("Unable to find an available proxy port after 100 attempts.");
    }

    private static bool IsPortAvailable(int port)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener?.Stop();
        }
    }
}