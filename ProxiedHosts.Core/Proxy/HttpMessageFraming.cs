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

namespace ProxiedHosts.Core.Proxy;

/// <summary>
/// Detects conflicting HTTP message framing headers to prevent
/// ambiguous message boundaries and potential request smuggling.
/// </summary>
internal static class HttpMessageFraming
{
    public static bool HasConflictingLengthHeaders(ReadOnlySpan<byte> rawMessage)
    {
        var headerEnd = rawMessage.IndexOf("\r\n\r\n"u8);

        if (headerEnd < 0)
        {
            throw new IOException("Incomplete HTTP headers.");
        }

        var lines = Encoding.Latin1
            .GetString(rawMessage[..headerEnd])
            .Split("\r\n", StringSplitOptions.None);

        var hasContentLength = false;
        var hasTransferEncoding = false;

        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');

            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator].Trim();

            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                hasContentLength = true;
            }
            else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                hasTransferEncoding = true;
            }

            if (hasContentLength && hasTransferEncoding)
            {
                return true;
            }
        }

        return false;
    }
}