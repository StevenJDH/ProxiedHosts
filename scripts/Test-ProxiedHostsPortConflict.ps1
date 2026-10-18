
# Simulates a proxy startup failure by temporarily occupying its persisted TCP port.
# Fully exit proxiedhosts-tray before running this script.

$ErrorActionPreference = 'Stop'

$portFile = Join-Path (Join-Path $env:LOCALAPPDATA 'ProxiedHosts') 'proxy.port'
$listener = $null

try {
    if (-not (Test-Path -LiteralPath $portFile -PathType Leaf)) {
        throw "Port file not found: $portFile. Run proxiedhosts-tray once to create it."
    }

    $portText = (Get-Content -LiteralPath $portFile -Raw).Trim()
    $port = 0

    if (-not [int]::TryParse($portText, [ref]$port) -or $port -lt 1 -or $port -gt 65535) {
        throw "Invalid TCP port in '$portFile': '$portText'."
    }

    $listener = [System.Net.Sockets.TcpListener]::new(
        [System.Net.IPAddress]::Loopback,
        $port
    )

    $listener.Start()

    Write-Host "Occupied 127.0.0.1:$port to simulate a proxy startup failure."
    Write-Host ''
    Write-Host '1. Launch proxiedhosts-tray.'
    Write-Host '2. Wait for the initial attempt and two retries (about four seconds).'
    Write-Host '3. Confirm the tray shows Status: Failed and a Start proxy item.'
    Write-Host '4. Return here and press Enter to release the port.'
    Write-Host '5. Click Start proxy in the tray menu to verify recovery.'
    Write-Host ''
    Write-Host 'Press any key to release the test port...' -NoNewline

    [void][Console]::ReadKey($true)

    Write-Host ''
}
finally {
    if ($null -ne $listener) {
        $listener.Stop()
        Write-Host "Released test port $port."
    }
}
