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

using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace ProxiedHosts;

internal static class Program
{
    private const string HostsFileName = "proxiedhosts.txt";

    public static async Task<int> Main()
    {
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
            var appDirectory = AppContext.BaseDirectory;
            var hostsPath = Path.Combine(appDirectory, HostsFileName);

            EnsureHostsFileExists(hostsPath);

            var hostMap = new HostMap(hostsPath);
            hostMap.Reload();
            using var watcher = new HostFileWatcher(hostMap);

            var portStore = new StablePortStore("ProxiedHosts");
            var port = await portStore.GetOrCreateAvailablePortAsync(shutdown.Token);

            var proxy = new ProxyServer(IPAddress.Loopback, port, hostMap);

            Console.WriteLine("ProxiedHosts");
            Console.WriteLine("------------------");
            Console.WriteLine($"Hosts file : {hostsPath}");
            Console.WriteLine($"Proxy      : http://127.0.0.1:{port}");
            Console.WriteLine($"Port       : {port} (stable and persisted)");
            Console.WriteLine($"Mappings   : {hostMap.Count}");
            Console.WriteLine();
            Console.WriteLine("Configure your application to use the proxy above for HTTP and HTTPS.");
            Console.WriteLine("HTTPS is tunneled with CONNECT; TLS is not decrypted or inspected.");
            Console.WriteLine("Press Ctrl+C to stop.");
            Console.WriteLine();

            await proxy.RunAsync(shutdown.Token);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal error: {ex.Message}");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void EnsureHostsFileExists(string hostsPath)
    {
        if (File.Exists(hostsPath))
        {
            return;
        }

        const string template = """
# proxiedhosts.txt
# Format: <ip-address> <hostname> [hostname2 ...]
# Example:
# 127.0.0.1 myapp.local
# 192.168.1.50 api.example.com
""";

        File.WriteAllText(hostsPath, template, Encoding.UTF8);
        Console.WriteLine($"Created {hostsPath}");
    }
}

internal sealed class HostMap
{
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, IPAddress> _entries = new(StringComparer.OrdinalIgnoreCase);

    public HostMap(string path) => _path = path;

    public string Path => _path;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public bool TryResolve(string host, out IPAddress? address)
    {
        host = NormalizeHost(host);
        lock (_gate)
        {
            return _entries.TryGetValue(host, out address);
        }
    }

    public void Reload()
    {
        var next = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
        var lines = File.ReadAllLines(_path);

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var commentIndex = raw.IndexOf('#');
            if (commentIndex >= 0)
            {
                raw = raw[..commentIndex];
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var parts = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
            {
                throw new FormatException($"Invalid hosts entry at line {i + 1}: expected '<ip> <hostname>'.");
            }

            if (!IPAddress.TryParse(parts[0], out var ip))
            {
                throw new FormatException($"Invalid IP address '{parts[0]}' at line {i + 1}.");
            }

            for (var p = 1; p < parts.Length; p++)
            {
                var host = NormalizeHost(parts[p]);
                if (host.Length == 0)
                {
                    throw new FormatException($"Invalid empty hostname at line {i + 1}.");
                }

                next[host] = ip;
            }
        }

        lock (_gate)
        {
            _entries = next;
        }
    }

    private static string NormalizeHost(string host) => host.Trim().TrimEnd('.');
}

internal sealed class HostFileWatcher : IDisposable
{
    private readonly HostMap _hostMap;
    private readonly FileSystemWatcher _watcher;
    private readonly object _gate = new();
    private Timer? _debounceTimer;

    public HostFileWatcher(HostMap hostMap)
    {
        _hostMap = hostMap;

        var directory = System.IO.Path.GetDirectoryName(hostMap.Path)
            ?? throw new InvalidOperationException("Unable to determine hosts file directory.");
        var fileName = System.IO.Path.GetFileName(hostMap.Path);

        _watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };

        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Renamed += OnChanged;
    }

    private void OnChanged(object? sender, FileSystemEventArgs e)
    {
        lock (_gate)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => ReloadSafely(), null, 250, Timeout.Infinite);
        }
    }

    private void ReloadSafely()
    {
        try
        {
            _hostMap.Reload();
            Console.WriteLine($"[{DateTimeOffset.Now:T}] Reloaded {_hostMap.Count} host mapping(s).");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{DateTimeOffset.Now:T}] Hosts reload failed: {ex.Message}");
            Console.Error.WriteLine("The previous valid mappings remain active.");
        }
    }

    public void Dispose()
    {
        _watcher.Dispose();
        lock (_gate)
        {
            _debounceTimer?.Dispose();
        }
    }
}

internal sealed class StablePortStore
{
    private const int MinPort = 20000;
    private const int MaxPort = 45000;
    private readonly string _portFile;

    public StablePortStore(string appName)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = AppContext.BaseDirectory;
        }

        var directory = System.IO.Path.Combine(root, appName);
        Directory.CreateDirectory(directory);
        _portFile = System.IO.Path.Combine(directory, "proxy.port");
    }

    public async Task<int> GetOrCreateAvailablePortAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_portFile))
        {
            var text = (await File.ReadAllTextAsync(_portFile, cancellationToken)).Trim();
            if (int.TryParse(text, out var persisted) && persisted is >= MinPort and <= MaxPort)
            {
                if (IsPortAvailable(persisted))
                {
                    return persisted;
                }

                throw new InvalidOperationException(
                    $"The persisted proxy port {persisted} is already in use. " +
                    $"Close the process using it, or delete '{_portFile}' to generate a new stable port.");
            }
        }

        for (var attempt = 0; attempt < 100; attempt++)
        {
            var candidate = RandomNumberGenerator.GetInt32(MinPort, MaxPort + 1);
            if (!IsPortAvailable(candidate))
            {
                continue;
            }

            await File.WriteAllTextAsync(_portFile, candidate.ToString(), cancellationToken);
            return candidate;
        }

        throw new InvalidOperationException("Unable to find an available proxy port after 100 attempts.");
    }

    private static bool IsPortAvailable(int port)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener?.Stop();
        }
    }
}

internal sealed class ProxyServer
{
    private const int MaxHeaderBytes = 64 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private readonly TcpListener _listener;
    private readonly HostMap _hostMap;

    public ProxyServer(IPAddress address, int port, HostMap hostMap)
    {
        _listener = new TcpListener(address, port);
        _hostMap = hostMap;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _listener.Start();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                _ = HandleClientSafelyAsync(client, cancellationToken);
            }
        }
        finally
        {
            _listener.Stop();
        }
    }

    private async Task HandleClientSafelyAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                await HandleClientAsync(client, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal shutdown.
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{DateTimeOffset.Now:T}] Client error: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        client.NoDelay = true;
        await using var clientStream = client.GetStream();

        var requestHeader = await HttpHeaderReader.ReadAsync(clientStream, MaxHeaderBytes, cancellationToken);
        if (requestHeader is null)
        {
            return;
        }

        if (!ProxyRequest.TryParse(requestHeader, out var request, out var parseError))
        {
            await WriteErrorAsync(clientStream, 400, "Bad Request", parseError ?? "Invalid proxy request.", cancellationToken);
            return;
        }

        var destinationAddress = _hostMap.TryResolve(request.Host, out var mapped)
            ? mapped
            : null;

        using var upstream = new TcpClient();
        upstream.NoDelay = true;

        try
        {
            if (destinationAddress is not null)
            {
                await upstream.ConnectAsync(destinationAddress, request.Port, cancellationToken)
                    .AsTask().WaitAsync(ConnectTimeout, cancellationToken);
                Console.WriteLine($"[{DateTimeOffset.Now:T}] {request.Method} {request.Host}:{request.Port} -> {destinationAddress}:{request.Port}");
            }
            else
            {
                await upstream.ConnectAsync(request.Host, request.Port, cancellationToken)
                    .AsTask().WaitAsync(ConnectTimeout, cancellationToken);
                Console.WriteLine($"[{DateTimeOffset.Now:T}] {request.Method} {request.Host}:{request.Port} -> DNS");
            }
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException)
        {
            await WriteErrorAsync(clientStream, 502, "Bad Gateway", $"Could not connect to {request.Host}:{request.Port}.", cancellationToken);
            return;
        }

        await using var upstreamStream = upstream.GetStream();

        if (request.IsConnect)
        {
            var established = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\nProxy-Agent: ProxiedHosts\r\n\r\n");
            await clientStream.WriteAsync(established, cancellationToken);
        }
        else
        {
            var rewrittenHeader = ProxyRequest.RewriteForOriginServer(requestHeader, request);
            await upstreamStream.WriteAsync(rewrittenHeader, cancellationToken);
        }

        await RelayBidirectionalAsync(clientStream, upstreamStream, cancellationToken);
    }

    private static async Task RelayBidirectionalAsync(
        NetworkStream client,
        NetworkStream upstream,
        CancellationToken cancellationToken)
    {
        var clientToUpstream = PumpAsync(client, upstream, cancellationToken);
        var upstreamToClient = PumpAsync(upstream, client, cancellationToken);

        await Task.WhenAny(clientToUpstream, upstreamToClient);

        try { client.Close(); } catch { }
        try { upstream.Close(); } catch { }

        try { await Task.WhenAll(clientToUpstream, upstreamToClient); }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // Expected when either side closes first.
        }
    }

    private static async Task PumpAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task WriteErrorAsync(Stream stream, int statusCode, string reason, string message, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(message + "\n");
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusCode} {reason}\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n");

        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }
}

internal static class HttpHeaderReader
{
    public static async Task<byte[]?> ReadAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        var matched = 0;

        while (output.Length < maxBytes)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return output.Length == 0 ? null : throw new IOException("Connection closed before the HTTP header was complete.");
            }

            for (var i = 0; i < read; i++)
            {
                var b = buffer[i];
                output.WriteByte(b);

                var expected = matched switch
                {
                    0 => (byte)'\r',
                    1 => (byte)'\n',
                    2 => (byte)'\r',
                    3 => (byte)'\n',
                    _ => throw new InvalidOperationException("Invalid header parser state.")
                };

                if (b == expected)
                {
                    matched++;
                    if (matched == 4)
                    {
                        if (i + 1 < read)
                        {
                            output.Write(buffer, i + 1, read - i - 1);
                        }
                        return output.ToArray();
                    }
                }
                else
                {
                    matched = b == (byte)'\r' ? 1 : 0;
                }
            }
        }

        throw new IOException($"HTTP header exceeded the {maxBytes}-byte limit.");
    }
}

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

        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
            (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) || uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
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
