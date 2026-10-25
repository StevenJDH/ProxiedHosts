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

namespace ProxiedHosts.Core.Proxy;

/// <summary>
/// Represents a consistent snapshot of connection activity for one proxy session.
/// </summary>
public sealed record ProxyMetricsSnapshot(
    int ActiveConnections,
    int PeakConnections,
    int ActiveHttp,
    int ActiveConnect,
    int PendingClassification,
    long TotalAccepted,
    long TotalRejected);

/// <summary>
/// Tracks admitted and rejected client connections without retaining socket references.
/// </summary>
public sealed class ProxyConnectionMetrics
{
    private readonly Lock _gate = new();
    private int _active;
    private int _peak;
    private int _http;
    private int _connect;
    private int _pending;
    private long _accepted;
    private long _rejected;

    /// <summary>
    /// Begins tracking a newly admitted connection as unclassified.
    /// </summary>
    /// <returns>A scope that must be disposed when the connection finishes.</returns>
    public ConnectionActivity BeginConnection()
    {
        lock (_gate)
        {
            _active++;
            _pending++;
            _accepted++;
            _peak = Math.Max(_peak, _active);
        }

        return new ConnectionActivity(this);
    }

    /// <summary>
    /// Records a connection rejected because the normal connection limit was reached.
    /// </summary>
    public void RecordRejection()
    {
        lock (_gate)
        {
            _rejected++;
        }
    }

    /// <summary>
    /// Returns a synchronized snapshot of the current and lifetime counters.
    /// </summary>
    /// <returns>The current metrics snapshot.</returns>
    public ProxyMetricsSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return new ProxyMetricsSnapshot(_active, _peak, _http, _connect, _pending, _accepted, _rejected);
        }
    }

    private void Classify(bool isConnect)
    {
        lock (_gate)
        {
            _pending--;

            if (isConnect)
            {
                _connect++;
            }
            else
            {
                _http++;
            }
        }
    }

    private void Complete(bool? isConnect)
    {
        lock (_gate)
        {
            _active--;

            if (isConnect is null)
            {
                _pending--;
            }
            else if (isConnect.Value)
            {
                _connect--;
            }
            else
            {
                _http--;
            }
        }
    }

    /// <summary>
    /// Tracks the classification and lifetime of a single admitted connection.
    /// </summary>
    public sealed class ConnectionActivity : IDisposable
    {
        private readonly ProxyConnectionMetrics _owner;
        private bool? _isConnect;
        private int _disposed;

        internal ConnectionActivity(ProxyConnectionMetrics owner)
        {
            _owner = owner;
        }

        /// <summary>
        /// Classifies the connection after its HTTP request is successfully parsed.
        /// </summary>
        /// <param name="isConnect">Whether the request uses CONNECT.</param>
        public void Classify(bool isConnect)
        {
            if (_isConnect is not null)
            {
                return;
            }

            _owner.Classify(isConnect);
            _isConnect = isConnect;
        }

        /// <summary>
        /// Removes the connection from the active counts, at most once.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Complete(_isConnect);
            }
        }
    }
}