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

namespace ProxiedHosts.Core.Proxy;

internal static class HttpHeaderReader
{
    public static async Task<byte[]?> ReadAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        var matched = 0;

        while (output.Length < maxBytes)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);

            if (read == 0)
            {
                return output.Length == 0 ? null : throw new IOException("Connection closed before the HTTP header was complete.");
            }

            for (var i = 0; i < read; i++)
            {
                var b = buffer[i];

                output.WriteByte(b);

                var expected = matched switch
                {
                    0 => (byte)'\r',
                    1 => (byte)'\n',
                    2 => (byte)'\r',
                    3 => (byte)'\n',
                    _ => throw new InvalidOperationException("Invalid header parser state.")
                };

                if (b == expected)
                {
                    matched++;

                    if (matched != 4)
                    {
                        continue;
                    }

                    if (i + 1 < read)
                    {
                        output.Write(buffer, i + 1, read - i - 1);
                    }

                    return output.ToArray();
                }

                matched = b == (byte)'\r' ? 1 : 0;
            }
        }

        throw new IOException($"HTTP header exceeded the {maxBytes}-byte limit.");
    }
}