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
    private static ReadOnlySpan<byte> HeaderTerminator => "\r\n\r\n"u8;

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
                return HandleConnectionClosed(output);
            }

            if (ProcessBuffer(buffer.AsSpan(0, read), output, ref matched))
            {
                return output.ToArray();
            }
        }

        throw new IOException($"HTTP header exceeded the {maxBytes}-byte limit.");
    }

    private static bool ProcessBuffer(ReadOnlySpan<byte> buffer, MemoryStream output, ref int matched)
    {
        for (var i = 0; i < buffer.Length; i++)
        {
            var value = buffer[i];

            output.WriteByte(value);

            if (value == HeaderTerminator[matched])
            {
                matched++;

                if (matched == HeaderTerminator.Length)
                {
                    output.Write(buffer[(i + 1)..]);
                    return true;
                }

                continue;
            }

            matched = value == (byte)'\r' ? 1 : 0;
        }

        return false;
    }

    private static byte[]? HandleConnectionClosed(MemoryStream output)
    {
        if (output.Length == 0)
        {
            return null;
        }

        throw new IOException("Connection closed before the HTTP header was complete.");
    }
}