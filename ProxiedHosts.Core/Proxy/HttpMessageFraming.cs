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

using System.Globalization;
using System.Text;

namespace ProxiedHosts.Core.Proxy;

/// <summary>
/// Validates HTTP message framing headers to prevent ambiguous message
/// boundaries and potential request smuggling or response splitting.
/// </summary>
internal static class HttpMessageFraming
{
    /// <summary>
    /// Validates HTTP message framing in accordance with RFC 9112, Section 6.3.
    /// </summary>
    /// <param name="rawMessage">The HTTP message containing complete headers.</param>
    /// <param name="error">The validation error, if any.</param>
    /// <returns>
    /// <see langword="true"/> if framing is valid; otherwise,
    /// <see langword="false"/>.
    /// </returns>
    public static bool TryValidate(ReadOnlySpan<byte> rawMessage, out string? error)
    {
        var headerEnd = rawMessage.IndexOf("\r\n\r\n"u8);

        if (headerEnd < 0)
        {
            throw new IOException("Incomplete HTTP headers.");
        }

        var lines = Encoding.Latin1
            .GetString(rawMessage[..headerEnd])
            .Split("\r\n", StringSplitOptions.None);

        if (!TryValidateConflictingLengthHeaders(lines, out error))
        {
            return false;
        }

        return TryValidateContentLength(lines, out error);
    }

    /// <summary>
    /// Rejects messages containing both Content-Length and Transfer-Encoding
    /// headers to prevent ambiguous HTTP message framing.
    /// </summary>
    /// <param name="lines">The HTTP header lines.</param>
    /// <param name="error">The validation error, if any.</param>
    /// <returns>
    /// <see langword="true"/> if no conflict exists; otherwise,
    /// <see langword="false"/>.
    /// </returns>
    private static bool TryValidateConflictingLengthHeaders(string[] lines, out string? error)
    {
        error = null;
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
                error = "Conflicting Content-Length and Transfer-Encoding headers.";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Rejects invalid, duplicate, or overflowing Content-Length headers.
    /// </summary>
    /// <param name="lines">The HTTP header lines.</param>
    /// <param name="error">The validation error, if any.</param>
    /// <returns>
    /// <see langword="true"/> if Content-Length is valid or absent;
    /// otherwise, <see langword="false"/>.
    /// </returns>
    private static bool TryValidateContentLength(string[] lines, out string? error)
    {
        error = null;
        var hasContentLength = false;

        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');

            if (separator <= 0)
            {
                continue;
            }

            var originalName = line[..separator];
            var name = originalName.Trim(' ', '\t');

            if (!name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (hasContentLength || !IsValidContentLength(originalName, line[(separator + 1)..]))
            {
                error = "Invalid or duplicate Content-Length header.";
                return false;
            }

            hasContentLength = true;
        }

        return true;
    }

    /// <summary>
    /// Validates that Content-Length contains a non-negative decimal integer
    /// representable as a 64-bit signed value.
    /// </summary>
    /// <param name="name">The original header field name.</param>
    /// <param name="value">The header field value.</param>
    /// <returns>
    /// <see langword="true"/> if the header is valid; otherwise,
    /// <see langword="false"/>.
    /// </returns>
    private static bool IsValidContentLength(string name, string value)
    {
        return name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(value.Trim(' ', '\t'), NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }
}