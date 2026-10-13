# ProxiedHosts

[![build](https://github.com/StevenJDH/ProxiedHosts/actions/workflows/dotnet-build-workflow.yml/badge.svg)](https://github.com/StevenJDH/ProxiedHosts/actions/workflows/dotnet-build-workflow.yml)
![GitHub release (latest by date including pre-releases)](https://img.shields.io/github/v/release/StevenJDH/ProxiedHosts?include_prereleases)
![GitHub All Releases](https://img.shields.io/github/downloads/StevenJDH/ProxiedHosts/total)
[![Codacy Badge](https://app.codacy.com/project/badge/Grade/eba24660853646288346f73ea9cfe368)](https://app.codacy.com/gh/StevenJDH/ProxiedHosts/dashboard?utm_source=gh&utm_medium=referral&utm_content=&utm_campaign=Badge_grade)
[![Quality Gate Status](https://sonarcloud.io/api/project_badges/measure?project=StevenJDH_ProxiedHosts&metric=alert_status)](https://sonarcloud.io/summary/new_code?id=StevenJDH_ProxiedHosts)
[![Maintainability Rating](https://sonarcloud.io/api/project_badges/measure?project=StevenJDH_ProxiedHosts&metric=sqale_rating)](https://sonarcloud.io/summary/new_code?id=StevenJDH_ProxiedHosts)
[![Reliability Rating](https://sonarcloud.io/api/project_badges/measure?project=StevenJDH_ProxiedHosts&metric=reliability_rating)](https://sonarcloud.io/summary/new_code?id=StevenJDH_ProxiedHosts)
[![Technical Debt](https://sonarcloud.io/api/project_badges/measure?project=StevenJDH_ProxiedHosts&metric=sqale_index)](https://sonarcloud.io/summary/new_code?id=StevenJDH_ProxiedHosts)
[![Sonar Violations (long format)](https://img.shields.io/sonar/violations/StevenJDH_ProxiedHosts?format=long&server=https%3A%2F%2Fsonarcloud.io)](https://sonarcloud.io/summary/new_code?id=StevenJDH_ProxiedHosts)
[![Security Rating](https://sonarcloud.io/api/project_badges/measure?project=StevenJDH_ProxiedHosts&metric=security_rating)](https://sonarcloud.io/summary/new_code?id=StevenJDH_ProxiedHosts)
[![Vulnerabilities](https://sonarcloud.io/api/project_badges/measure?project=StevenJDH_ProxiedHosts&metric=vulnerabilities)](https://sonarcloud.io/summary/new_code?id=StevenJDH_ProxiedHosts)
[![Lines of Code](https://sonarcloud.io/api/project_badges/measure?project=StevenJDH_ProxiedHosts&metric=ncloc)](https://sonarcloud.io/summary/new_code?id=StevenJDH_ProxiedHosts)
![Maintenance](https://img.shields.io/badge/yes-4FCA21?label=maintained&style=flat)
![GitHub](https://img.shields.io/github/license/StevenJDH/ProxiedHosts)

ProxiedHosts is a lightweight local HTTP and HTTPS proxy for applying custom hostname-to-IP mappings without modifying the system hosts file. Mappings are loaded from a simple `proxiedhosts.txt` file that can be changed without administrator privileges. HTTP traffic is forwarded using the configured hostname mappings, while HTTPS connections are tunneled using the standard CONNECT method without decrypting TLS traffic. A stable local proxy port is generated and persisted between application launches for predictable client configuration. The proxy is designed primarily for local development, testing, and environments where editing the operating system hosts file is inconvenient or restricted.

[![Buy me a coffee](https://img.shields.io/static/v1?label=Buy%20me%20a&message=coffee&color=important&style=flat&logo=buy-me-a-coffee&logoColor=white)](https://www.buymeacoffee.com/stevenjdh)

## Features

* Supports Windows, Linux, and macOS (Intel/Apple Silicon) along with AOT compilation.
* Custom hostname-to-IP mappings using a familiar hosts-file-style format.
* Supports entries with ports to redirect requests.
* No administrator privileges required for normal operation.
* No modification of the system hosts file.
* No custom root certificate or TLS interception required.
* End-to-end TLS remains intact for HTTPS connections.
* Binds only to `127.0.0.1`.
* Generates a random port on first run and persists it for later runs.
* Hot-reloads `proxiedhosts.txt` when it changes.
* Automatically closes only affected connections when mappings change without disrupting unrelated traffic.
* Loads valid mappings, ignores invalid entries, and retains previous mappings if reloading fails.
* Automatic fallback to normal DNS resolution for unmapped hosts.
* Toggle proxy state to enable traffic passthrough without mapping requests.
* Optionally start tray application on system login.
* Automatic and manual update checking with option for preview releases.
* Supports HTTP proxy requests and HTTPS tunneling via `CONNECT` without decrypting TLS.
* Prevents incorrect HTTP connection reuse by enforcing `Connection: close` on requests and responses.
* Preserves tunnel data received alongside `CONNECT` headers to prevent data loss.
* Supports TCP half-close to allow remaining traffic to finish when one direction closes.
* Enforces timeouts for incoming HTTP headers and upstream connection attempts.
* Automatically closes idle connections after a set time without network activity.
* Allows active connections to finish gracefully during shutdown before forcing them to close.

## Usage
A console application and a system tray/menu bar variant of ProxiedHosts are provided as alternative options. For `proxiedhosts`, run the executable to see the printout of the proxy URL configuration, for example:

```text
Proxy      : http://127.0.0.1:12345
Port       : 12345 (stable and persisted)
```

For `proxiedhosts-tray`, run the executable and right-click the icon in the system tray (or menu bar on macOS) to see the same proxy URL configuration, for example:

```text
┌──────────────────────────────────────┐
│ Status: Active                       │
│ Proxy: http://127.0.0.1:12345        │
├──────────────────────────────────────┤
│ ☑ Enable mappings (5)               │
│ ☑ Start at login                    │
├──────────────────────────────────────┤
│ Open proxiedhosts.txt                │
│ Open log                             │
├──────────────────────────────────────┤
│ Help                               ▸ │   ┌──────────────────────────────────┐
├──────────────────────────────────────┤   │ Donate 5€ (PayPal)...            │
│ Quit ProxiedHosts                    │   ├──────────────────────────────────┤
└──────────────────────────────────────┘   │ Check for updates                │
                                           │ ☐ Include preview releases      │
                                           ├──────────────────────────────────┤
                                           │ About ProxiedHosts               │
                                           └──────────────────────────────────┘
```

With the proxy configuration in hand, configure the client application or system to use that address for both HTTP and HTTPS proxying.

> [!TIP]
> For Linux and macOS, run `chmod +x proxiedhosts` to set the execution bit so that `./proxiedhosts` works for running the application. Also, macOS users will likely need to run `xattr -d com.apple.quarantine proxiedhosts` to remove the quarantine attribute so that it doesn't get block by Gatekeeper. Alternatively, the execution can be approved by going to `System Settings > Privacy & Security`. The same applies for `proxiedhosts-tray`. For Windows, if there is a blue popup, click the `More info` text, and then click the `Run anyway` button. Subsequent runs will not require this.

### Proxy port generation
On first run, the application selects an unused port in the range `20000-45000` and writes it to:

* Windows: `%LOCALAPPDATA%\ProxiedHosts\proxy.port`.
* Other platforms: the .NET local-application-data location for the current user.

On later runs, the same port is reused. If another process is already using that port, the proxy exits with an error instead of silently changing ports. Delete `proxy.port` if a new port is needed.

### Technical test
To test the proxy for a couple of features, add `127.0.0.1 example.com` to the proxiedhosts.txt file. Then run the following command after updating `--proxy` with the actual proxy port:

```bash
# The empty '--noproxy' value guarantees curl won't bypass the proxy because of `NO_PROXY` and `no_proxy`environment variables.
curl http://example.com \
    --noproxy "" \
    --proxy http://127.0.0.1:12345  \
    -H "Connection: keep-alive" \
    -H "Proxy-Connection: keep-alive" \
    -v
```

🔳 **Output:**

```bash
*   Trying 127.0.0.1:12345...
* Established connection to 127.0.0.1 (127.0.0.1 port 12345) from 127.0.0.1 port 52780
* using HTTP/1.x
> GET http://example.com/ HTTP/1.1
> Host: example.com
> User-Agent: curl/8.21.0
> Accept: */*
> Proxy-Connection: keep-alive
> Connection: keep-alive
>
* Request completely sent off
< HTTP/1.1 502 Bad Gateway
< Content-Type: text/plain; charset=utf-8
< Content-Length: 37
< Connection: close
<
Could not connect to example.com:80.
* shutting down connection #0
```

The above output demonstrates two important features. One, the part that says `Could not connect to example.com:80` means that the entry provided early was used, in this case, to block traffic to `example.com`. And two, the part of the response that says `Connection: close` is a feature specific for HTTP traffic that removes headers like `Proxy-Connection: keep-alive` and `Connection: keep-alive` after being provided in the request to prevent connection reuse and ensure requests reach their intended destinations.

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

## Automatic startup
ProxiedHosts supports running at system login to maintain connectivity for applications using the proxy configuration. The below table shows where the startup configuration is set.

| Operating System | Location                                                                                                         |
|------------------|------------------------------------------------------------------------------------------------------------------|
| Windows          | HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run                                                  |
| macOS            | ~/Library/LaunchAgents/io.github.stevenjdh.proxiedhosts.plist                                                    |
| Linux            | ~/.config/autostart/proxiedhosts-tray.desktop (or the equivalent directory configured through `XDG_CONFIG_HOME`) |

> [!TIP]
> Move the proxiedhosts-tray application to a permanent location before enabling startup at login. Likewise, before removing the application or changing its location, ensure to disable the startup first.

## Limitations
Below is a list of intentional limitations that may or may not be needed in most or all cases.

* HTTP/1.1 pipelining is not supported because it is not worth the effort to identify individual HTTP request boundaries, including request bodies. It's also obsolete, and not used anymore by modern browsers.
* HTTP protocol upgrades are not supported, so unencrypted WebSocket (`ws://`) upgrades won't work, but encrypted WebSocket (`wss://`) connections will work through CONNECT tunneling.

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
