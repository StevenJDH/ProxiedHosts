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

using ProxiedHosts.Core.Logging;
using SystemConsole = System.Console;

namespace ProxiedHosts.Console;

/// <summary>
/// Handles Ctrl+C and unregisters the console event during shutdown.
/// </summary>
internal sealed class ConsoleCancelHandler : IDisposable
{
    private readonly CancellationTokenSource _shutdown;
    private readonly IProxyLogger _logger;

    public ConsoleCancelHandler(CancellationTokenSource shutdown, IProxyLogger logger)
    {
        _shutdown = shutdown;
        _logger = logger;

        SystemConsole.CancelKeyPress += OnCancelKeyPress;
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        // Prevent the runtime from terminating immediately so
        // resources can shut down cleanly.
        e.Cancel = true;

        if (!_shutdown.IsCancellationRequested)
        {
            _logger.Information("Stopping proxy...");
            _shutdown.Cancel();
        }
    }

    public void Dispose()
    {
        SystemConsole.CancelKeyPress -= OnCancelKeyPress;
    }
}