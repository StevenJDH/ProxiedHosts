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

namespace ProxiedHosts.Proxy;

/// <summary>
/// Tracks whether custom proxy host mappings are currently active.
/// Reads are thread-safe via <see cref="Volatile"/>.<c>Read</c>, and toggles are
/// applied atomically with <see cref="Interlocked.CompareExchange(ref int, int, int)"/>
/// so concurrent updates cannot overwrite each other.
/// The <see cref="Changed"/> event is raised only after a successful state change and
/// executes on the thread that performed the toggle.
/// </summary>
internal sealed class ProxyState
{
    private int _active = 1;
    public event Action<bool>? Changed;

    public bool IsActive => Volatile.Read(ref _active) == 1;

    public void Toggle()
    {
        while (true)
        {
            var current = Volatile.Read(ref _active);
            var next = current == 1 ? 0 : 1;

            if (Interlocked.CompareExchange(ref _active, next, current) == current)
            {
                Changed?.Invoke(next == 1);
                return;
            }
        }
    }
}