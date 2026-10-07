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

        if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseConnectRequest(method, target, out request, out error);
        }

        if (TryParseAbsoluteRequest(method, target, out request))
        {
            return true;
        }

        return TryParseHostHeaderRequest(lines, method, out request, out error);
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

    private static bool TryParseConnectRequest(string method, string target, out ProxyRequest request, out string? error)
    {
        request = default!;
        error = null;

        if (!TryParseHostPort(target, 443, out var host, out var port))
        {
            error = "Invalid CONNECT target.";
            return false;
        }

        request = new ProxyRequest(method, host, port, true, null);

        return true;
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

    private static bool TryParseHostHeaderRequest(string[] lines, string method, out ProxyRequest request, out string? error)
    {
        request = default!;
        error = null;

        var hostHeader = lines
            .Skip(1)
            .FirstOrDefault(line => line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase));

        if (hostHeader is null)
        {
            error = "Missing Host header.";
            return false;
        }

        var hostValue = hostHeader[5..].Trim();

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

    public static byte[] RewriteForOriginServer(byte[] rawHeader, ProxyRequest request)
    {
        if (request.AbsoluteUri is null || !Uri.TryCreate(request.AbsoluteUri, UriKind.Absolute, out var uri))
        {
            return rawHeader;
        }

        var headerText = Encoding.Latin1.GetString(rawHeader);
        var split = headerText.IndexOf("\r\n", StringComparison.Ordinal);

        if (split < 0)
        {
            return rawHeader;
        }

        var requestLine = headerText[..split];
        var parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 3)
        {
            return rawHeader;
        }

        var pathAndQuery = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
        var newRequestLine = $"{parts[0]} {pathAndQuery} {parts[2]}";
        var rewritten = newRequestLine + headerText[split..];

        return Encoding.Latin1.GetBytes(rewritten);
    }

    private static bool TryParseHostPort(string value, int defaultPort, out string host, out int port)
    {
        host = string.Empty;
        port = defaultPort;

        if (Uri.TryCreate($"tcp://{value}", UriKind.Absolute, out var uri))
        {
            host = uri.Host;
            port = uri.IsDefaultPort ? defaultPort : uri.Port;
            return !string.IsNullOrWhiteSpace(host) && port is > 0 and <= 65535;
        }

        return false;
    }
}