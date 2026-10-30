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
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ProxiedHosts.Core.Infrastructure;

namespace ProxiedHosts.Core.Status;

/// <summary>
/// Writes a read-only HTML dashboard using the visual palette of proxy error pages.
/// </summary>
internal static class ProxyStatusPage
{
    private const string AutoRefreshScript = "setTimeout(() => location.reload(), 10000);";
    private static readonly string AutoRefreshScriptHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(AutoRefreshScript)));

    /// <summary>
    /// Writes one HTTP 200 dashboard response from a consistent metrics snapshot.
    /// </summary>
    /// <param name="stream">The browser's connection stream.</param>
    /// <param name="snapshot">The values displayed by the dashboard.</param>
    /// <param name="autoRefresh">Whether automatic dashboard refresh is enabled.</param>
    /// <param name="cancellationToken">Cancels slow reads or writes.</param>
    public static async Task WriteAsync(Stream stream, ProxyStatusSnapshot snapshot, bool autoRefresh, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(RenderHtml(snapshot, autoRefresh));

        var headerBuilder = new StringBuilder()
            .Append("HTTP/1.1 200 OK\r\n")
            .Append("Content-Type: text/html; charset=utf-8\r\n")
            .Append($"Content-Length: {body.Length}\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n")
            .Append("X-Frame-Options: DENY\r\n")
            .Append("Referrer-Policy: no-referrer\r\n")
            .Append("Cross-Origin-Resource-Policy: same-origin\r\n")
            .Append($"Content-Security-Policy: default-src 'none'; script-src 'sha256-{AutoRefreshScriptHash}'; style-src 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'\r\n")
            .Append("Connection: close\r\n\r\n");

        var headers = Encoding.ASCII.GetBytes(headerBuilder.ToString());

        await stream.WriteAsync(headers, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }

    private static string RenderHtml(ProxyStatusSnapshot snapshot, bool autoRefresh)
    {
        var metrics = snapshot.Connections;
        var metadata = ApplicationMetadata.Instance;
        var appName = WebUtility.HtmlEncode(metadata.Product);
        var version = WebUtility.HtmlEncode(metadata.Version.ToString(3));
        var authors = WebUtility.HtmlEncode(metadata.Authors);
        var copyright = WebUtility.HtmlEncode(metadata.Copyright);

        var repositoryLink = Uri.TryCreate(metadata.RepositoryUrl, UriKind.Absolute, out var repositoryUri) && repositoryUri.Scheme == Uri.UriSchemeHttps
            ? $"""
               <a class="repo-link" href="{WebUtility.HtmlEncode(repositoryUri.AbsoluteUri)}" target="_blank" rel="noopener noreferrer">
                   <svg viewBox="0 0 16 16" fill="currentColor" aria-hidden="true" focusable="false">
                       <path d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82a7.65 7.65 0 0 1 4 0c1.53-1.03 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.013 8.013 0 0 0 16 8c0-4.42-3.58-8-8-8Z"/>
                   </svg>
                   <span class="repo-text">GitHub repository</span>
                   <span aria-hidden="true">↗</span>
               </a>
               """
            : string.Empty;

        var listening = snapshot.IsListening ? "Listening" : "Stopped";
        var stateClass = snapshot.IsListening ? "ok" : "warn";
        var mappings = snapshot.MappingsEnabled ? "Enabled" : "Disabled";
        var limit = snapshot.MaxConcurrentConnections?.ToString("N0", CultureInfo.InvariantCulture) ?? "Unlimited";
        var timestamp = snapshot.CapturedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

        // Using script approach instead of refresh directive for better compatibility with browsers like Firefox.
        var refreshScript = autoRefresh ? $"<script>{AutoRefreshScript}</script>" : string.Empty;
        var autoRefreshUrl = autoRefresh ? "/status" : "/status?refresh=10";
        var manualRefreshUrl = autoRefresh ? "/status?refresh=10" : "/status";
        var autoRefreshLabel = autoRefresh ? "Auto-refresh: On" : "Auto-refresh: Off";
        
        return $$"""
                  <!doctype html>
                  <html lang="en">
                  <head>
                      <meta charset="utf-8">
                      <meta name="viewport" content="width=device-width, initial-scale=1">
                      <meta name="color-scheme" content="dark">
                      <title>Proxy Status | {{appName}}</title>
                      {{refreshScript}}
                      <style>
                          :root {
                              color-scheme: dark;
                              --background: #0b1420;
                              --surface: #142131;
                              --border: #304459;
                              --text: #e9f1fa;
                              --muted: #9cb9c4;
                              --subtle: #7892a8;
                              --teal: #68dbc9;
                              --amber: #f9bb7c;
                          }
                          * { box-sizing: border-box; }
                          body {
                              margin: 0;
                              min-height: 100vh;
                              padding: 38px 20px;
                              color: var(--text);
                              font-family: system-ui, -apple-system, "Segoe UI", sans-serif;
                              background:
                                  radial-gradient(circle at 14% 8%, #204460 0%, transparent 42%),
                                  radial-gradient(circle at 87% 94%, #123c42 0%, transparent 35%),
                                  var(--background);
                          }
                          main { width: min(100%, 960px); margin: 0 auto; }
                          .header { display: flex; flex-wrap: wrap; justify-content: space-between; align-items: center; gap: 22px; }
                          .brand { display: flex; gap: 15px; align-items: center; }
                          .mark {
                              width: 50px; height: 50px; display: grid; place-items: center;
                              border-radius: 14px; font-weight: 850; font-size: 27px;
                              background: linear-gradient(135deg, #49d6c4, #448ee5); color: #091c29;
                          }
                          .name { display: flex; align-items: center; flex-wrap: wrap; gap: 10px; font-size: 25px; font-weight: 750; letter-spacing: -.5px; }
                          .version { padding: 4px 9px; border-radius: 20px; background: #203c45; color: var(--teal); font-size: 11px; font-weight: 700; letter-spacing: 0; }
                          .eyebrow { color: var(--muted); font-size: 11px; font-weight: 700; letter-spacing: 2px; }
                          .status {
                              padding: 9px 14px; border-radius: 100px; font-size: 12px;
                              border: 1px solid #2e655c; background: #193b39; color: #65d7c6;
                          }
                          .status.warn { border-color: #70533a; background: #392c22; color: var(--amber); }
                          .divider { border: 0; border-top: 1px solid var(--border); margin: 27px 0 32px; }
                          .section-title { margin: 0 0 15px; font-size: 12px; font-weight: 750; letter-spacing: 1.5px; color: var(--muted); }
                          .stats { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 14px; }
                          .card { padding: 23px; background: var(--surface); border: 1px solid var(--border); border-radius: 18px; }
                          .card .label { color: var(--muted); font-size: 13px; }
                          .value { margin: 11px 0 5px; color: var(--teal); font-size: clamp(37px, 5vw, 52px); line-height: 1.05; font-weight: 780; letter-spacing: -1.5px; }
                          .value.amber { color: var(--amber); }
                          .description { color: var(--subtle); font-size: 12px; }
                          .information { margin-top: 34px; }
                          .details { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 16px 32px; }
                          .detail { display: flex; justify-content: space-between; align-items: center; gap: 12px; padding: 10px 0; border-bottom: 1px solid var(--border); }
                          .detail span:first-child { color: var(--muted); font-size: 14px; }
                          .detail span:last-child { font-weight: 700; font-size: 14px; }
                          .refresh, .auto-refresh { display: inline-flex; align-items: center; justify-content: center; width: 170px; min-height: 40px; padding: 10px 14px; border: 1px solid transparent; border-radius: 10px; font-size: 12px; font-weight: 400; line-height: 18px; text-decoration: none; white-space: nowrap; }
                          .refresh { gap: 7px; background: var(--teal); color: #091c29; }
                          .refresh:hover, .refresh:focus-visible { background: #91eddf; }
                          .refresh svg, .repo-link svg { width: 15px; height: 15px; flex-shrink: 0; }
                          .actions { display: flex; justify-content: flex-end; flex-wrap: wrap; gap: 10px; margin-top: 20px; }
                          .auto-refresh { background: var(--surface); color: var(--muted); border-color: var(--border); }
                          .auto-refresh:hover, .auto-refresh:focus-visible { border-color: var(--teal); color: var(--teal); }
                          .product-footer { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 12px; margin-top: 30px; padding-top: 20px; border-top: 1px solid var(--border); color: var(--subtle); font-size: 12px; }
                          .repo-link { display: inline-flex; align-items: center; gap: 7px; color: var(--teal); font-size: 12px; font-weight: 400; text-decoration: none; }
                          .repo-link:hover, .repo-link:focus-visible { color: #91eddf; }
                          .repo-link:hover .repo-text, .repo-link:focus-visible .repo-text { text-decoration: underline; }
                          @media (max-width: 650px) {
                              .stats { grid-template-columns: repeat(2, minmax(0, 1fr)); }
                              .details { grid-template-columns: 1fr; gap: 0; }
                              .card { padding: 16px; }
                          }
                          @media (max-width: 380px) { .stats { grid-template-columns: 1fr; } }
                      </style>
                  </head>
                  <body>
                  <main>
                      <header class="header">
                          <div class="brand">
                              <div class="mark" aria-hidden="true">P</div>
                              <div>
                                  <div class="name">{{appName}} <span class="version">v{{version}}</span></div>
                                  <div class="eyebrow">LOCAL PROXY STATUS</div>
                              </div>
                          </div>
                          <span class="status {{stateClass}}">● Proxy {{listening}}</span>
                      </header>
                      <hr class="divider">
                      <section aria-labelledby="activity">
                          <h2 id="activity" class="section-title">CONNECTION ACTIVITY</h2>
                          <div class="stats">
                              <article class="card"><div class="label">Active now</div><div class="value">{{metrics.ActiveConnections.ToString("N0", CultureInfo.InvariantCulture)}}</div><div class="description">Current admitted connections</div></article>
                              <article class="card"><div class="label">Peak concurrent</div><div class="value">{{metrics.PeakConnections.ToString("N0", CultureInfo.InvariantCulture)}}</div><div class="description">Since proxy session started</div></article>
                              <article class="card"><div class="label">HTTP</div><div class="value">{{metrics.ActiveHttp.ToString("N0", CultureInfo.InvariantCulture)}}</div><div class="description">Active HTTP requests</div></article>
                              <article class="card"><div class="label">HTTPS CONNECT</div><div class="value">{{metrics.ActiveConnect.ToString("N0", CultureInfo.InvariantCulture)}}</div><div class="description">Parsed CONNECT connections</div></article>
                              <article class="card"><div class="label">Pending</div><div class="value">{{metrics.PendingClassification.ToString("N0", CultureInfo.InvariantCulture)}}</div><div class="description">Not yet classified</div></article>
                              <article class="card"><div class="label">Rejected</div><div class="value amber">{{metrics.TotalRejected.ToString("N0", CultureInfo.InvariantCulture)}}</div><div class="description">Capacity rejections since start</div></article>
                          </div>
                      </section>
                      <section class="information" aria-labelledby="information">
                          <h2 id="information" class="section-title">PROXY INFORMATION</h2>
                          <div class="card details">
                              <div class="detail"><span>Proxy listener</span><span>{{listening}}</span></div>
                              <div class="detail"><span>Host mappings</span><span>{{mappings}}</span></div>
                              <div class="detail"><span>Loaded mappings</span><span>{{snapshot.MappingCount.ToString("N0", CultureInfo.InvariantCulture)}}</span></div>
                              <div class="detail"><span>Connection limit</span><span>{{limit}}</span></div>
                              <div class="detail"><span>Total accepted</span><span>{{metrics.TotalAccepted.ToString("N0", CultureInfo.InvariantCulture)}}</span></div>
                              <div class="detail"><span>Last updated</span><span>{{timestamp}}</span></div>
                          </div>
                      </section>
                      <div class="actions">
                          <a class="auto-refresh" href="{{autoRefreshUrl}}">{{autoRefreshLabel}}</a>
                          <a class="refresh" href="{{manualRefreshUrl}}">
                              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                                  <path d="M3 12a9 9 0 0 1 15.36-6.36L21 8M21 3v5h-5"/>
                                  <path d="M21 12a9 9 0 0 1-15.36 6.36L3 16M3 21v-5h5"/>
                              </svg>
                              Refresh snapshot
                          </a>
                      </div>
                      <footer class="product-footer">
                          <span>{{copyright}} {{authors}}</span>
                          {{repositoryLink}}
                      </footer>
                  </main>
                  </body>
                  </html>
                  
                  """;
    }
}