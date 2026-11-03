#!/usr/bin/env bash
# Switches the game between the dev setup and a release build of the working tree, for A/B checks in game.
# Usage: tools/devloader/swap.sh dev|release [--no-start]
#   Closes the game (asks it to quit, as a normal close), builds and installs the setup, then starts the game again.
#   release keeps dist/EpochAssist-v<version>.zip as it was: a test build never replaces the published package there.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
mode="${1:-}"
start=1
[ "${2:-}" = "--no-start" ] && start=0
case "$mode" in
    dev|release) ;;
    *) echo "usage: $0 dev|release [--no-start]" >&2; exit 2 ;;
esac

running() { tasklist.exe /FI "IMAGENAME eq Last Epoch.exe" 2>/dev/null | grep -q "Last Epoch"; }

if running; then
    echo "Closing the game..."
    taskkill.exe /IM "Last Epoch.exe" >/dev/null 2>&1 || true
    for _ in $(seq 1 60); do running || break; sleep 1; done
    if running; then
        echo "The game is still running after 60 s; close it and run this again" >&2
        exit 1
    fi
fi

cd "$root"
if [ "$mode" = dev ]; then
    ./build.sh dev | tail -2
else
    version="$(sed -n 's/.*string Version = "\([^"]*\)".*/\1/p' src/Core/Host/Entry.cs)"
    zip="dist/EpochAssist-v$version.zip"
    backup=""
    if [ -f "$zip" ]; then
        backup="$(mktemp "${TMPDIR:-/tmp}/swap-release-XXXXXX.zip")"
        cp -p "$zip" "$backup"
    fi
    restore() { if [ -n "$backup" ]; then mv -f "$backup" "$zip"; fi; }
    trap restore EXIT
    ./build.sh deploy-release | tail -2
fi

if [ "$start" = 1 ]; then
    cmd.exe /c start steam://rungameid/899770 >/dev/null 2>&1
    echo "Game starting with the $mode setup"
fi
