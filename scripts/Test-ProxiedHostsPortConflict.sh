#!/usr/bin/env bash

# Simulates a proxy startup failure by temporarily occupying its persisted TCP port.
# Fully exit proxiedhosts-tray before running this script.

set -euo pipefail

case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*)
        if [[ -n "${LOCALAPPDATA:-}" ]]; then
            data_directory=$(cygpath -u "$LOCALAPPDATA")
        else
            data_directory="$HOME/AppData/Local"
        fi
        ;;
    Darwin)
        data_directory="$HOME/Library/Application Support"
        ;;
    Linux)
        data_directory="${XDG_DATA_HOME:-$HOME/.local/share}"
        if [[ "$data_directory" != /* ]]; then
            data_directory="$HOME/.local/share"
        fi
        ;;
    *)
        printf 'Unsupported operating system.\n' >&2
        exit 1
        ;;
esac

port_file="$data_directory/ProxiedHosts/proxy.port"

if [[ ! -f "$port_file" ]]; then
    printf 'Port file not found: %s. Run proxiedhosts-tray once to create it.\n' "$port_file" >&2
    exit 1
fi

port_text=$(<"$port_file")
port_text="${port_text#"${port_text%%[![:space:]]*}"}"
port_text="${port_text%"${port_text##*[![:space:]]}"}"

port=""
if [[ "$port_text" =~ ^\+?[0-9]+$ ]]; then
    port="${port_text#+}"
    while [[ "${#port}" -gt 1 && "$port" == 0* ]]; do
        port="${port#0}"
    done
fi

if [[ ! "$port" =~ ^[0-9]{1,5}$ ]] || (( 10#$port < 1 || 10#$port > 65535 )); then
    printf "Invalid TCP port in '%s': '%s'.\n" "$port_file" "$port_text" >&2
    exit 1
fi

port=$((10#$port))

if ! command -v socat >/dev/null 2>&1; then
    printf 'socat is required to occupy a listening TCP port from Bash.\n' >&2
    exit 1
fi

if [[ ! -t 0 ]]; then
    printf 'Run this script in an interactive terminal.\n' >&2
    exit 1
fi

error_log=$(mktemp)
listener_pid=""
started=false

cleanup() {
    trap - EXIT INT TERM
    if [[ -n "$listener_pid" ]]; then
        kill "$listener_pid" 2>/dev/null || true
        wait "$listener_pid" 2>/dev/null || true
    fi
    rm -f "$error_log"

    if [[ "$started" == true ]]; then
        printf 'Released test port %s.\n' "$port"
    fi
}

trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

socat -d -d -u "TCP4-LISTEN:${port},bind=127.0.0.1,fork" /dev/null \
    2>"$error_log" &
listener_pid=$!

# Confirm that socat has opened the port before showing the test instructions.
ready=false
for ((attempt = 0; attempt < 30; attempt++)); do
    if ! kill -0 "$listener_pid" 2>/dev/null; then
        break
    fi
    if [[ "$(<"$error_log")" == *" listening on AF="* ]]; then
        ready=true
        break
    fi
    sleep 0.1
done

if [[ "$ready" != true ]] || ! kill -0 "$listener_pid" 2>/dev/null; then
    printf 'Unable to occupy 127.0.0.1:%s.\n' "$port" >&2
    if [[ -s "$error_log" ]]; then
        cat "$error_log" >&2
    fi
    exit 1
fi

started=true

printf 'Occupied 127.0.0.1:%s to simulate a proxy startup failure.\n' "$port"
printf '\n'
printf '%s\n' '1. Launch proxiedhosts-tray.'
printf '%s\n' '2. Wait for the initial attempt and two retries (about four seconds).'
printf '%s\n' '3. Confirm the tray shows Status: Failed and a Start proxy item.'
printf '%s\n' '4. Return here and press Enter to release the port.'
printf '%s\n' '5. Click Start proxy in the tray menu to verify recovery.'
printf '\n'
printf '%s' 'Press any key to release the test port...'

IFS= read -r -s -n 1 _ < /dev/tty
printf '\n'
