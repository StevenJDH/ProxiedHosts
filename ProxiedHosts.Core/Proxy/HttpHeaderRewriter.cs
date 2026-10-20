
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

internal static class HttpHeaderRewriter
{
    public static byte[] RewriteRequest(byte[] rawHeader, ProxyRequest request)
    {
        var (lines, bodyStart) = SplitHeader(rawHeader);

        if (request.AbsoluteUri is null || !Uri.TryCreate(request.AbsoluteUri, UriKind.Absolute, out var uri))
        {
            return Rewrite(rawHeader, lines, bodyStart);
        }

        var parts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 3)
        {
            var pathAndQuery = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
            lines[0] = $"{parts[0]} {pathAndQuery} {parts[2]}";
        }

        return Rewrite(rawHeader, lines, bodyStart, uri.Authority);
    }

    public static byte[] RewriteResponse(byte[] rawHeader)
    {
        var (lines, bodyStart) = SplitHeader(rawHeader);

        return Rewrite(rawHeader, lines, bodyStart);
    }

    private static (string[] Lines, int BodyStart) SplitHeader(byte[] rawHeader)
    {
        var headerEnd = rawHeader.AsSpan().IndexOf("\r\n\r\n"u8);

        if (headerEnd < 0)
        {
            throw new IOException("Incomplete HTTP headers.");
        }

        var lines = Encoding.Latin1
            .GetString(rawHeader, 0, headerEnd)
            .Split("\r\n", StringSplitOptions.None);

        return (lines, headerEnd + 4);
    }

    private static byte[] Rewrite(byte[] rawHeader, string[] lines, int bodyStart, string? targetAuthority = null)
    {
        var removedHeaders = GetHopByHopHeaders(lines);

        if (targetAuthority is not null)
        {
            // RFC 9112, section 3.2.2. Replace the supplied Host with the request URI authority.
            removedHeaders.Add("Host");
        }

        var header = new StringBuilder();

        header.Append(lines[0]).Append("\r\n");

        if (targetAuthority is not null)
        {
            header.Append($"Host: {targetAuthority}\r\n");
        }

        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');

            if (separator > 0 && removedHeaders.Contains(line[..separator].Trim()))
            {
                continue;
            }

            header.Append(line).Append("\r\n");
        }

        header.Append("Connection: close\r\n\r\n");

        var rewrittenHeader = Encoding.Latin1.GetBytes(header.ToString());
        var result = new byte[rewrittenHeader.Length + rawHeader.Length - bodyStart];

        rewrittenHeader.CopyTo(result, 0);
        rawHeader.AsSpan(bodyStart).CopyTo(result.AsSpan(rewrittenHeader.Length));

        return result;
    }

    private static HashSet<string> GetHopByHopHeaders(string[] lines)
    {
        var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Connection",
            "Proxy-Connection",
            "Keep-Alive",
            "TE",
            "Upgrade",
            "Proxy-Authenticate",
            "Proxy-Authorization"
        };

        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');

            if (separator < 1)
            {
                continue;
            }

            var name = line[..separator].Trim();

            if (!name.Equals("Connection", StringComparison.OrdinalIgnoreCase) && !name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var option in line[(separator + 1)..].Split(','))
            {
                var headerName = option.Trim();

                if (headerName.Length > 0)
                {
                    removed.Add(headerName);
                }
            }
        }

        // Preserve message framing because bodies are forwarded unchanged.
        removed.Remove("Content-Length");
        removed.Remove("Transfer-Encoding");
        removed.Remove("Trailer");

        return removed;
    }
}