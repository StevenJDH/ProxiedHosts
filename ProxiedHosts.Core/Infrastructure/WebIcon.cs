
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

namespace ProxiedHosts.Core.Infrastructure;

/// <summary>
/// Provides the embedded application icon for HTML pages.
/// </summary>
internal static class WebIcon
{
    public static readonly string DataUri = CreateDataUri();

    private static string CreateDataUri()
    {
        using var stream = typeof(WebIcon).Assembly.GetManifestResourceStream("ProxiedHosts.Core.WebIcon.png")
            ?? throw new InvalidOperationException("Could not load the embedded application icon.");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return $"data:image/png;base64,{Convert.ToBase64String(memory.ToArray())}";
    }
}