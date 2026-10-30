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

using ProxiedHosts.Core.Infrastructure;
using System.Net;
using System.Text;

namespace ProxiedHosts.Core.Proxy;

/// <summary>
/// Generates consistent HTML error responses for failures detected by the proxy.
/// </summary>
internal static class ProxyErrorResponse
{
    /// <summary>
    /// Writes a branded HTTP error response and closes the connection logically
    /// through the Connection header.
    /// </summary>
    /// <param name="stream">The client stream receiving the response.</param>
    /// <param name="statusCode">The HTTP error status code.</param>
    /// <param name="reason">The HTTP reason phrase.</param>
    /// <param name="message">The error message displayed to the client.</param>
    /// <param name="cancellationToken">The cancellation token for the write operation.</param>
    public static async Task WriteAsync(Stream stream, int statusCode, string reason, string message, CancellationToken cancellationToken)
    {
        var appName = WebUtility.HtmlEncode(ApplicationMetadata.Instance.Product);
        var safeReason = WebUtility.HtmlEncode(reason);
        var safeMessage = WebUtility.HtmlEncode(message);

        var safeHint = WebUtility.HtmlEncode(statusCode == 503
            ? "The proxy is busy. Please try again shortly."
            : "Check the destination and your proxy settings, then try the request again.");

        var html = $$"""
                 <!DOCTYPE html>
                 <html lang="en">
                 <head>
                     <meta charset="utf-8">
                     <meta name="viewport" content="width=device-width, initial-scale=1">
                     <title>{{statusCode}} {{safeReason}} | {{appName}}</title>
                     <style>
                         * {
                             box-sizing: border-box;
                         }

                         body {
                             margin: 0;
                             min-height: 100vh;
                             display: grid;
                             place-items: center;
                             padding: 24px;
                             font-family: system-ui, -apple-system, "Segoe UI", sans-serif;
                             color: #e9f1fa;
                             background:
                                 radial-gradient(circle at 20% 20%, #204460 0%, transparent 45%),
                                 radial-gradient(circle at 85% 85%, #123c42 0%, transparent 40%),
                                 #0b1420;
                         }

                         .card {
                             width: min(100%, 620px);
                             padding: clamp(28px, 6vw, 48px);
                             background: #142131;
                             border: 1px solid #304459;
                             border-radius: 24px;
                             box-shadow: 0 24px 70px #0006;
                         }

                         .brand {
                             display: flex;
                             align-items: center;
                             flex-wrap: wrap;
                             gap: 12px;
                             font-size: 18px;
                             font-weight: 700;
                         }

                         .mark {
                             display: grid;
                             place-items: center;
                             width: 42px;
                             height: 42px;
                             border-radius: 12px;
                             background: linear-gradient(135deg, #49d6c4, #448ee5);
                             color: #091c29;
                             font-size: 23px;
                             font-weight: 800;
                         }

                         .tag {
                             margin-left: auto;
                             padding: 4px 10px;
                             border: 1px solid #3a5864;
                             border-radius: 20px;
                             color: #9cb9c4;
                             font-size: 10px;
                             font-weight: 600;
                             letter-spacing: 1px;
                         }

                         .eyebrow {
                             margin: 40px 0 4px;
                             color: #65d7c6;
                             font-size: 11px;
                             font-weight: 700;
                             letter-spacing: 2px;
                             text-transform: uppercase;
                         }

                         .code {
                             font-size: clamp(80px, 18vw, 120px);
                             font-weight: 800;
                             line-height: 1.1;
                             letter-spacing: -6px;
                             color: #68dbc9;
                         }

                         h1 {
                             margin: 8px 0 12px;
                             font-size: clamp(24px, 5vw, 32px);
                             font-weight: 650;
                         }

                         .message {
                             margin: 0;
                             color: #b0c1d1;
                             font-size: 16px;
                             line-height: 1.7;
                             overflow-wrap: anywhere;
                         }

                         .hint {
                             margin-top: 32px;
                             padding: 16px 18px;
                             border: 1px solid #2e4556;
                             border-left: 3px solid #65d7c6;
                             border-radius: 10px;
                             background: #1a2b3c;
                             color: #c4d3df;
                             font-size: 13px;
                             line-height: 1.6;
                         }

                         footer {
                             display: flex;
                             justify-content: space-between;
                             flex-wrap: wrap;
                             gap: 8px;
                             margin-top: 32px;
                             padding-top: 20px;
                             border-top: 1px solid #304459;
                             color: #7892a8;
                             font-size: 12px;
                         }
                     </style>
                 </head>
                 <body>
                     <main class="card">
                         <header class="brand">
                             <span class="mark">P</span>
                             <span>{{appName}}</span>
                             <span class="tag">LOCAL PROXY</span>
                         </header>

                         <p class="eyebrow">Connection error</p>
                         <div class="code">{{statusCode}}</div>
                         <h1>{{safeReason}}</h1>
                         <p class="message">{{safeMessage}}</p>

                         <div class="hint">
                             {{safeHint}}
                         </div>

                         <footer>
                             <span>Generated by {{appName}}</span>
                             <span>Connection closed</span>
                         </footer>
                     </main>
                 </body>
                 </html>
                 
                 """;

        var body = Encoding.UTF8.GetBytes(html);

        var headerBuilder = new StringBuilder()
            .Append($"HTTP/1.1 {statusCode} {reason}\r\n")
            .Append("Content-Type: text/html; charset=utf-8\r\n")
            .Append($"Content-Length: {body.Length}\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n")
            .Append("X-Frame-Options: DENY\r\n")
            .Append("Referrer-Policy: no-referrer\r\n")
            .Append("Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'\r\n")
            .Append("Connection: close\r\n\r\n");

        var header = Encoding.ASCII.GetBytes(headerBuilder.ToString());

        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }
}