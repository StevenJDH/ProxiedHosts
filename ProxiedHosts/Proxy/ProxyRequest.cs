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

namespace ProxiedHosts.Proxy;

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
        if (lines.Length == 0)
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

        var method = requestParts[0];
        var target = requestParts[1];
        var isConnect = method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase);

        if (isConnect)
        {
            if (!TryParseHostPort(target, 443, out var connectHost, out var connectPort))
            {
                error = "Invalid CONNECT target.";
                return false;
            }

            request = new ProxyRequest(method, connectHost, connectPort, true, null);
            return true;
        }

        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) || uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
        {
            var port = uri.IsDefaultPort ? (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80) : uri.Port;
            request = new ProxyRequest(method, uri.Host, port, false, uri.AbsoluteUri);
            return true;
        }

        var hostHeader = lines
            .Skip(1)
            .FirstOrDefault(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase));

        if (hostHeader is null)
        {
            error = "Missing Host header.";
            return false;
        }

        var hostValue = hostHeader[5..].Trim();
        if (!TryParseHostPort(hostValue, 80, out var host, out var hostPort))
        {
            error = "Invalid Host header.";
            return false;
        }

        request = new ProxyRequest(method, host, hostPort, false, null);
        return true;
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