
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
    private const string ViaPseudonym = "proxiedhosts";

    public static byte[] RewriteRequest(byte[] rawHeader, ProxyRequest request, bool enableViaHeader)
    {
        var (lines, bodyStart) = SplitHeader(rawHeader);

        if (request.AbsoluteUri is null || !Uri.TryCreate(request.AbsoluteUri, UriKind.Absolute, out var uri))
        {
            return Rewrite(rawHeader, lines, bodyStart, enableViaHeader);
        }

        var parts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 3)
        {
            var pathAndQuery = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
            lines[0] = $"{parts[0]} {pathAndQuery} {parts[2]}";
        }

        return Rewrite(rawHeader, lines, bodyStart, enableViaHeader, uri.Authority);
    }

    public static byte[] RewriteResponse(byte[] rawHeader, bool enableViaHeader, bool isInterim = false)
    {
        var (lines, bodyStart) = SplitHeader(rawHeader);

        return Rewrite(rawHeader, lines, bodyStart, enableViaHeader, isInterim: isInterim);
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

    private static byte[] Rewrite(byte[] rawHeader, string[] lines, int bodyStart, bool enableViaHeader, string? targetAuthority = null, bool isInterim = false)
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
        
        // RFC 9110, Section 7.6.3: Append this proxy's Via entry when enabled.
        if (enableViaHeader)
        {
            header.Append($"Via: {GetReceivedProtocol(lines[0])} {ViaPseudonym}\r\n");
        }
        
        if (!isInterim)
        {
            header.Append("Connection: close\r\n");
        }

        header.Append("\r\n");

        var rewrittenHeader = Encoding.Latin1.GetBytes(header.ToString());
        var result = new byte[rewrittenHeader.Length + rawHeader.Length - bodyStart];

        rewrittenHeader.CopyTo(result, 0);
        rawHeader.AsSpan(bodyStart).CopyTo(result.AsSpan(rewrittenHeader.Length));

        return result;
    }

    private static string GetReceivedProtocol(string startLine)
    {
        var parts = startLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var version = string.Empty;

        if (startLine.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
        {
            version = parts[0];
        }
        else if (parts.Length == 3)
        {
            version = parts[2];
        }

        if (!version.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase) || version.Length <= 5)
        {
            throw new IOException("Invalid HTTP protocol version.");
        }

        return version[5..];
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