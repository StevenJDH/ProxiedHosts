# ProxiedHosts

[![build](https://github.com/StevenJDH/ProxiedHosts/actions/workflows/dotnet-build-workflow.yml/badge.svg)](https://github.com/StevenJDH/ProxiedHosts/actions/workflows/dotnet-build-workflow.yml)
![GitHub release (latest by date including pre-releases)](https://img.shields.io/github/v/release/StevenJDH/ProxiedHosts?include_prereleases)
![GitHub All Releases](https://img.shields.io/github/downloads/StevenJDH/ProxiedHosts/total)
![Maintenance](https://img.shields.io/badge/yes-4FCA21?label=maintained&style=flat)
![GitHub](https://img.shields.io/github/license/StevenJDH/ProxiedHosts)

ProxiedHosts is a lightweight local HTTP and HTTPS proxy for applying custom hostname-to-IP mappings without modifying the system hosts file. Mappings are loaded from a simple `proxiedhosts.txt` file that can be changed without administrator privileges. HTTP traffic is forwarded using the configured hostname mappings, while HTTPS connections are tunneled using the standard CONNECT method without decrypting TLS traffic. A stable local proxy port is generated and persisted between application launches for predictable client configuration. The proxy is designed primarily for local development, testing, and environments where editing the operating system hosts file is inconvenient or restricted.

[![Buy me a coffee](https://img.shields.io/static/v1?label=Buy%20me%20a&message=coffee&color=important&style=flat&logo=buy-me-a-coffee&logoColor=white)](https://www.buymeacoffee.com/stevenjdh)

## Features

- Support Windows, Linux, and macOS (Intel/Apple Silicon) along with AOT compilation.
- Custom hostname-to-IP mappings using a familiar hosts-file-style format.
- Supports entries with ports to redirect requests.
- Supports HTTP proxy requests.
- Supports HTTPS tunneling via `CONNECT` without decrypting TLS.
- No custom root certificate or TLS interception required.
- End-to-end TLS remains intact for HTTPS connections.
- Binds only to `127.0.0.1`.
- Generates a random port on first run and persists it for later runs.
- Hot-reloads `proxiedhosts.txt` when it changes.
- Automatically closes only affected connections when mappings change without disrupting unrelated traffic.
- Keeps the previous valid mappings and ignores the invalid ones when reloading.
- Automatic fallback to normal DNS resolution for unmapped hosts.
- No administrator privileges required for normal operation.
- No modification of the system hosts file.
- Toggle proxy state to enable traffic passthrough without mapping requests.

## Usage
A console application and a system tray/menu bar variant of ProxiedHosts are provided as alternative options. For `proxiedhosts`, run the executable to see the printout of the proxy URL configuration, for example:

```text
Proxy      : http://127.0.0.1:28741
Port       : 28741 (stable and persisted)
```

For `proxiedhosts-tray`, run the executable and right-click the icon in the system tray (or menu bar on macOS) to see the same proxy URL configuration, for example:

```text
────────────────────────────────
| Status: Active                |
| Proxy: http://127.0.0.1:12345 |
| ───────────────────────────── |
| ☑ Enable mappings (5)        |
| ───────────────────────────── |
| Open proxiedhosts.txt         |
| Open log                      |
| ───────────────────────────── |
| Quit ProxiedHosts             |
────────────────────────────────
```

With the proxy configuration in hand, configure the client application or system to use that address for both HTTP and HTTPS proxying.

> [!TIP]
> For Linux and macOS, run `chmod +x proxiedhosts` to set the execution bit so that `./proxiedhosts` works for running the application. Also, macOS users will likely need to run `xattr -d com.apple.quarantine proxiedhosts` to remove the quarantine attribute so that it doesn't get block by Gatekeeper. Alternatively, the execution can be approved by going to `System Settings > Privacy & Security`. The same applies for `proxiedhosts-tray`.

### Proxy port generation
On first run, the application selects an unused port in the range `20000-45000` and writes it to:

- Windows: `%LOCALAPPDATA%\ProxiedHosts\proxy.port`.
- Other platforms: the .NET local-application-data location for the current user.

On later runs, the same port is reused. If another process is already using that port, the proxy exits with an error instead of silently changing ports. Delete `proxy.port` if a new port is needed.

## Mapping file syntax

**Format:** `<ip-address>[:destination-port] <hostname>[:match-port] [hostname2[:match-port] ...]`

The syntax is similar to the operating-system hosts file:

```text
127.0.0.1 myapp.local
192.168.1.50 api.example.com
10.0.0.20 foo.example.com bar.example.com
```

Comments are supported:

```text
192.168.1.50 api.example.com # test environment
```

Additionally, entries with ports are supported, which isn´t possible with the system's host file:

```text
127.0.0.1:8080 myapp.local
192.168.1.50:8443 api.example.com
192.168.1.50:8443 api.example.com:443
127.0.0.1:5000 app.local:80 api.local:8080
[::1]:8080 ipv6.example.com
```

> [!IMPORTANT]  
> Port numbers must be in the range of 1-65535 inclusively to be valid.

### File location
The application has options to open the `proxiedhosts.txt` file directly, but below are the locations where the file is generated on first run:

| Operating System | Location                                                                             |
|------------------|--------------------------------------------------------------------------------------|
| Windows          | %LOCALAPPDATA%\ProxiedHosts\proxiedhosts.txt                                         |
| macOS            | ~/Library/Application Support/ProxiedHosts/proxiedhosts.txt                          |
| Linux            | ~/.local/share/ProxiedHosts/proxiedhosts.txt (or the configured user data directory) |

## Disclaimer
ProxiedHosts is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.

## Contributing
Thanks for your interest in contributing! There are many ways to contribute to this project. Get started [here](https://github.com/StevenJDH/.github/blob/main/docs/CONTRIBUTING.md).

## Do you have any questions?
Many commonly asked questions are answered in the FAQ:
[https://github.com/StevenJDH/ProxiedHosts/wiki/FAQ](https://github.com/StevenJDH/ProxiedHosts/wiki/FAQ)

## Want to show your support?

|Method          | Address                                                                                   |
|---------------:|:------------------------------------------------------------------------------------------|
|PayPal:         | [https://www.paypal.me/stevenjdh](https://www.paypal.me/stevenjdh "Steven's Paypal Page") |
|Cryptocurrency: | [Supported options](https://github.com/StevenJDH/StevenJDH/wiki/Donate-Cryptocurrency)    |


// Steven Jenkins De Haro ("StevenJDH" on GitHub)
