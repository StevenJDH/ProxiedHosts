
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

using System.Diagnostics;

namespace ProxiedHosts.Core.Proxy;


/// <summary>
/// Tracks data transfer activity and byte counts in both directions
/// of an HTTPS tunnel to provide additional connection diagnostics.
/// </summary>
internal sealed class TunnelDiagnostics
{
    private readonly string _upstreamEndpoint;
    private long _clientToUpstreamBytes;
    private long _upstreamToClientBytes;
    private long _lastForwardedTimestamp;

    public TunnelDiagnostics(string upstreamEndpoint)
    {
        _upstreamEndpoint = upstreamEndpoint;
    }

    public void RecordClientToUpstream(int count) => Record(ref _clientToUpstreamBytes, count);

    public void RecordUpstreamToClient(int count) => Record(ref _upstreamToClientBytes, count);

    public string GetSummary()
    {
        var last = Interlocked.Read(ref _lastForwardedTimestamp);
        var sinceLastWrite = last == 0 ? "none (no relay payload forwarded)" : Stopwatch.GetElapsedTime(last).ToString("c");

        return $"upstream={_upstreamEndpoint}; relay bytes client->upstream={Interlocked.Read(ref _clientToUpstreamBytes)}, upstream->client={Interlocked.Read(ref _upstreamToClientBytes)}; time since last relay write={sinceLastWrite}";
    }

    private void Record(ref long total, int count)
    {
        Interlocked.Add(ref total, count);
        Interlocked.Exchange(ref _lastForwardedTimestamp, Stopwatch.GetTimestamp());
    }
}