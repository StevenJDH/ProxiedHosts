# ProxiedHosts

[![build](https://github.com/StevenJDH/ProxiedHosts/actions/workflows/dotnet-build-workflow.yml/badge.svg)](https://github.com/StevenJDH/ProxiedHosts/actions/workflows/dotnet-build-workflow.yml)
![GitHub release (latest by date including pre-releases)](https://img.shields.io/github/v/release/StevenJDH/ProxiedHosts?include_prereleases)
![Maintenance](https://img.shields.io/badge/yes-4FCA21?label=maintained&style=flat)
![GitHub](https://img.shields.io/github/license/StevenJDH/ProxiedHosts)

ProxiedHosts is a lightweight local HTTP and HTTPS proxy for applying custom hostname-to-IP mappings without modifying the system hosts file. Mappings are loaded from a simple `proxiedhosts.txt` file that can be changed without administrator privileges. HTTP traffic is forwarded using the configured hostname mappings, while HTTPS connections are tunneled using the standard CONNECT method without decrypting TLS traffic. A stable local proxy port is generated and persisted between application launches for predictable client configuration. The proxy is designed primarily for local development, testing, and environments where editing the operating system hosts file is inconvenient or restricted.

[![Buy me a coffee](https://img.shields.io/static/v1?label=Buy%20me%20a&message=coffee&color=important&style=flat&logo=buy-me-a-coffee&logoColor=white)](https://www.buymeacoffee.com/stevenjdh)

## Features

- Support Windows, Linux, and macOS (Intel/Apple Silicon) along with AOT compilation.
- Custom hostname-to-IP mappings using a familiar hosts-file-style format.
- Supports HTTP proxy requests.
- Supports HTTPS tunneling via `CONNECT` without decrypting TLS.
- No custom root certificate or TLS interception required.
- End-to-end TLS remains intact for HTTPS connections.
- Binds only to `127.0.0.1`.
- Generates a random port on first run and persists it under the user's local application-data directory.
- Reuses the same port on later runs.
- Hot-reloads `proxiedhosts.txt` when it changes.
- Keeps the previous valid mapping if a reload contains an error.
- Automatic fallback to normal DNS resolution for unmapped hosts.
- No administrator privileges required for normal operation.
- No modification of the system hosts file.

## Usage
The application prints the proxy URL, for example:

```text
Proxy      : http://127.0.0.1:28741
Port       : 28741 (stable and persisted)
```

Configure the client application to use that address for both HTTP and HTTPS proxying.

## `proxiedhosts.txt`

Format is similar to the operating-system hosts file:

```text
127.0.0.1 myapp.local
192.168.1.50 api.example.com
10.0.0.20 foo.example.com bar.example.com
```

Comments are supported:

```text
192.168.1.50 api.example.com # test environment
```

## Stable port behavior

On first run, the application selects an unused port in the range `20000-45000` and writes it to:

- Windows: `%LOCALAPPDATA%\ProxiedHostsProxy\proxy.port`
- Other platforms: the .NET local-application-data location for the current user

On later runs, the same port is reused. If another process is already using that port, the proxy exits with an error instead of silently changing ports. Delete `proxy.port` if you intentionally want a new stable port.

## Security notes

The listener binds to `127.0.0.1` only, so it is not exposed to other machines on the network.

HTTPS traffic is tunneled byte-for-byte. The proxy does not install a root certificate, inspect TLS traffic, or modify certificates.

## Limitations

This is intentionally a small development proxy, not a production-grade general-purpose HTTP proxy. In particular:

- It only affects applications that are explicitly configured to use it.
- It does not transparently intercept arbitrary TCP traffic.
- It does not implement proxy authentication.
- It does not decrypt HTTPS.
- Some applications may ignore system/application proxy settings.

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
