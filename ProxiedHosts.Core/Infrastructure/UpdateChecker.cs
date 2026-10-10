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

using System.Net.Http.Headers;
using System.Text.Json;

namespace ProxiedHosts.Core.Infrastructure;

public sealed record AvailableUpdate(Version Version, string Url, bool IsPreview);

/// <summary>
/// Checks published GitHub releases for versions newer than the
/// specified application version, optionally including prereleases.
/// </summary>
public static class UpdateChecker
{
    private const string ReleasesUrl = "https://api.github.com/repos/StevenJDH/ProxiedHosts/releases?per_page=100";

    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    public static async Task<AvailableUpdate?> CheckAsync(Version currentVersion, bool includePreviewReleases, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesUrl);

        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ProxiedHosts", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        AvailableUpdate? latest = null;

        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean())
            {
                continue;
            }

            var isPreview = release.GetProperty("prerelease").GetBoolean();

            if (isPreview && !includePreviewReleases)
            {
                continue;
            }

            var tag = release.GetProperty("tag_name").GetString();

            if (string.IsNullOrWhiteSpace(tag) || !Version.TryParse(tag.TrimStart('v', 'V'), out var version) || version <= currentVersion)
            {
                continue;
            }

            var url = release.GetProperty("html_url").GetString();

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (latest is null || version > latest.Version)
            {
                latest = new AvailableUpdate(version, uri.AbsoluteUri, isPreview);
            }
        }

        return latest;
    }
}