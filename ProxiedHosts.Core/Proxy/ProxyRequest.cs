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

internal sealed record ProxyRequest(string Method, string Host, int Port, bool IsConnect, string? AbsoluteUri)
{
    public static bool TryParse(byte[] rawHeader, out ProxyRequest request, out string? error)
    {
        request = default!;
        error = null;

        var headerText = Encoding.Latin1.GetString(rawHeader);
        var headerEnd = headerText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var headerOnly = headerEnd >= 0 ? headerText[..headerEnd] : headerText;
        var lines = headerOnly.Split("\r\n", StringSplitOptions.None);

        if (!TryParseRequestLine(lines, out var method, out var target, out error))
        {
            return false;
        }
        
        // RFC 9112, Section 6.3: Reject invalid or ambiguous HTTP message framing.
        if (!HttpMessageFraming.TryValidate(rawHeader, out error))
        {
            return false;
        }

        // RFC 9112, Section 3.2: Validate Host before routing.
        var requireHost = lines[0].EndsWith(" HTTP/1.1", StringComparison.Ordinal);

        if (!TryReadHostHeader(lines, requireHost, out var hostValue, out error))
        {
            return false;
        }

        if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseConnectRequest(method, target, out request, out error);
        }

        if (TryParseAbsoluteRequest(method, target, out request))
        {
            return true;
        }

        return TryParseHostHeaderRequest(hostValue, method, out request, out error);
    }

    private static bool TryParseRequestLine(string[] lines, out string method, out string target, out string? error)
    {
        method = string.Empty;
        target = string.Empty;
        error = null;

        if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0]))
        {
            error = "Missing request line.";
            return false;
        }

        var requestParts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);

        if (requestParts.Length != 3)
        {
            error = "Invalid request line.";
            return false;
        }

        method = requestParts[0];
        target = requestParts[1];

        return true;
    }

    private static bool TryReadHostHeader(string[] lines, bool required, out string? hostValue, out string? error)
    {
        hostValue = null;
        error = null;

        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');

            if (separator < 0)
            {
                continue;
            }

            var name = line[..separator];

            if (!name.TrimEnd(' ', '\t').Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (hostValue is not null)
            {
                error = "Multiple Host headers.";
                return false;
            }

            if (!name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                error = "Invalid Host header.";
                return false;
            }

            hostValue = line[(separator + 1)..].Trim(' ', '\t');
        }

        if (hostValue is null)
        {
            error = required ? "Missing Host header." : null;
            return !required;
        }

        if (!TryParseHostPort(hostValue, 80, out _, out _))
        {
            error = "Invalid Host header.";
            return false;
        }

        return true;
    }

    private static bool TryParseConnectRequest(string method, string target, out ProxyRequest request, out string? error)
    {
        request = default!;
        error = null;

        if (!TryParseConnectTarget(target, out var host, out var port))
        {
            error = "Invalid CONNECT target.";
            return false;
        }

        request = new ProxyRequest(method, host, port, true, null);

        return true;
    }

    /// <summary>
    /// Validates and parses an HTTP CONNECT target in accordance with
    /// RFC 9110, Section 9.3.6, which requires an explicit destination port.
    /// Unlike standard HTTP requests, CONNECT targets must not use a default port.
    /// </summary>
    /// <remarks>
    /// Ensures the target contains a valid port and authority without
    /// user information, paths, queries, or fragments, while supporting
    /// both hostnames and bracketed IPv6 addresses.
    /// See https://www.rfc-editor.org/rfc/rfc9110.html#section-9.3.6.
    /// </remarks>
    /// <param name="target">The CONNECT request target to validate.</param>
    /// <param name="host">The parsed destination hostname or IP address.</param>
    /// <param name="port">The explicitly specified destination port.</param>
    /// <returns>
    /// <see langword="true"/> if the target is valid; otherwise,
    /// <see langword="false"/>.
    /// </returns>
    private static bool TryParseConnectTarget(string target, out string host, out int port)
    {
        host = string.Empty;
        port = 0;

        var separator = target.LastIndexOf(':');

        if (separator <= 0 || !int.TryParse(target.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var explicitPort)
            || explicitPort is < 1 or > 65535)
        {
            return false;
        }

        if (!Uri.TryCreate($"tcp://{target}", UriKind.Absolute, out var uri) || uri.Port != explicitPort || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath is not ("" or "/") || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        host = uri.Host;
        port = explicitPort;

        return !string.IsNullOrWhiteSpace(host);
    }

    private static bool TryParseAbsoluteRequest(string method, string target, out ProxyRequest request)
    {
        request = default!;

        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!IsSupportedScheme(uri.Scheme))
        {
            return false;
        }

        var port = uri.IsDefaultPort ? GetDefaultPort(uri.Scheme) : uri.Port;

        request = new ProxyRequest(method, uri.Host, port, false, uri.AbsoluteUri);

        return true;
    }

    private static bool TryParseHostHeaderRequest(string? hostValue, string method, out ProxyRequest request, out string? error)
    {
        request = default!;
        error = null;

        if (hostValue is null)
        {
            error = "Missing Host header.";
            return false;
        }

        if (!TryParseHostPort(hostValue, 80, out var host, out var port))
        {
            error = "Invalid Host header.";
            return false;
        }

        request = new ProxyRequest(method, host, port, false, null);

        return true;
    }
    
    private static bool IsSupportedScheme(string scheme)
    {
        return scheme.Equals("http", StringComparison.OrdinalIgnoreCase) || scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetDefaultPort(string scheme)
    {
        return scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80;
    }

    public static byte[] RewriteForOriginServer(byte[] rawHeader, ProxyRequest request, bool enableViaHeader)
    {
        return HttpHeaderRewriter.RewriteRequest(rawHeader, request, enableViaHeader);
    }

    private static bool TryParseHostPort(string value, int defaultPort, out string host, out int port)
    {
        host = string.Empty;
        port = defaultPort;

        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace) || value.IndexOfAny(['/', '\\', '?', '#', '@']) >= 0)
        {
            return false;
        }

        if (!Uri.TryCreate($"tcp://{value}", UriKind.Absolute, out var uri))
        {
            return false;
        }

        host = uri.Host;
        port = uri.IsDefaultPort ? defaultPort : uri.Port;

        return !string.IsNullOrWhiteSpace(host) && port is > 0 and <= 65535;
    }
}