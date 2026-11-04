#!/usr/bin/env -S dotnet --
#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0

// Simulates a proxy startup failure by temporarily occupying its persisted TCP port.
// Fully exit proxiedhosts-tray before running this script.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

if (args.Length != 0)
{
    bool windowsConsole = OperatingSystem.IsWindows() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MSYSTEM"));
    Console.Error.WriteLine($"Usage: {(windowsConsole ? "dotnet " : "./")}Test-ProxiedHostsPortConflict.cs");
    return 1;
}

bool gitBash = OperatingSystem.IsWindows() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MSYSTEM"));

if (Console.IsInputRedirected && !gitBash)
{
    Console.Error.WriteLine("Run this script in an interactive terminal.");
    return 1;
}

using var interrupted = new CancellationTokenSource();
ConsoleCancelEventHandler onCancel = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    interrupted.Cancel();
};
Console.CancelKeyPress += onCancel;

TcpListener? listener = null;
int port = 0;
bool started = false;

try
{
    var portFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProxiedHosts", "proxy.port");

    if (!File.Exists(portFile))
    {
        throw new InvalidOperationException($"Port file not found: {portFile}. Run proxiedhosts-tray once to create it.");
    }

    string portText = File.ReadAllText(portFile).Trim();

    if (!int.TryParse(portText, out port) || port is < 1 or > 65535)
    {
        throw new InvalidOperationException($"Invalid TCP port in '{portFile}': '{portText}'.");
    }

    listener = new TcpListener(IPAddress.Loopback, port);
    listener.Start();
    started = true;

    Console.WriteLine($"Occupied 127.0.0.1:{port} to simulate a proxy startup failure.");
    Console.WriteLine();
    Console.WriteLine("1. Launch proxiedhosts-tray.");
    Console.WriteLine("2. Wait for the initial attempt and two retries (about four seconds).");
    Console.WriteLine("3. Confirm the tray shows Status: Failed and a Start proxy item.");
    Console.WriteLine("4. Return here and press Enter to release the port.");
    Console.WriteLine("5. Click Start proxy in the tray menu to verify recovery.");
    Console.WriteLine();
    Console.Write("Press any key to release the test port...");

    if (gitBash)
    {
        await ReadGitBashKeyAsync(interrupted.Token);
    }
    else
    {
        await Task.Run(() => Console.ReadKey(intercept: true)).WaitAsync(interrupted.Token);
    }

    Console.WriteLine();
    return 0;
}
catch (OperationCanceledException) when (interrupted.IsCancellationRequested)
{
    Console.WriteLine();
    return 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
finally
{
    listener?.Stop();

    if (started)
    {
        Console.WriteLine($"Released test port {port}.");
    }

    Console.CancelKeyPress -= onCancel;
}

/// <summary>
/// Reads one key from Git Bash's terminal when MinTTY doesn't expose a Windows console.
/// </summary>
/// <param name="cancellationToken">Cancels the key wait when interrupted.</param>
static async Task ReadGitBashKeyAsync(CancellationToken cancellationToken)
{
    var startInfo = new ProcessStartInfo("bash.exe")
    {
        UseShellExecute = false
    };
    startInfo.ArgumentList.Add("-c");
    startInfo.ArgumentList.Add("IFS= read -r -s -n 1 _ < /dev/tty");

    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to read from the Git Bash terminal.");

    try
    {
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("Unable to read a key from the Git Bash terminal.");
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        if (!process.HasExited)
        {
            process.Kill();
            await process.WaitForExitAsync();
        }

        throw;
    }
}
