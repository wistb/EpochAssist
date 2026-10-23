#!/usr/bin/env bash
# Usage: ./build.sh [build|deploy|release]. Machine settings (LE_ML, DOTNET, NUGET_SOURCE) go in build.local.env.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
[ -f "$root/build.local.env" ] && . "$root/build.local.env"

mode="${1:-build}"
case "$mode" in
    build|deploy) config=Debug ;;
    release) config=Release ;;
    *) echo "usage: $0 [build|deploy|release]" >&2; exit 2 ;;
esac

if [ -z "${LE_ML:-}" ]; then
    echo "Set LE_ML to the game's MelonLoader folder in build.local.env" >&2
    exit 1
fi

if [ -z "${DOTNET:-}" ]; then
    windows_dotnet="/mnt/c/Program Files/dotnet/dotnet.exe"
    if [ -x "$windows_dotnet" ]; then
        DOTNET="$windows_dotnet"
    else
        DOTNET=dotnet
    fi
fi

project="$root/src/EpochAssist.csproj"
case "$DOTNET" in *.exe) project="$(wslpath -w "$project")" ;; esac

args=(build "$project" -c "$config" "-p:LE_ML=$LE_ML")
[ -n "${NUGET_SOURCE:-}" ] && args+=(--source "$NUGET_SOURCE")
"$DOTNET" "${args[@]}"

if [ "$mode" = deploy ]; then
    dll="$root/src/bin/Debug/net6.0/win-x64/EpochAssist.dll"
    game_ml="$LE_ML"
    command -v wslpath >/dev/null 2>&1 && game_ml="$(wslpath -u "$LE_ML")"
    mods="$(dirname "$game_ml")/Mods"
    mkdir -p "$mods"

    # The game locks the DLL while it runs.
    waiting=0
    until cp "$dll" "$mods/EpochAssist.dll" 2>/dev/null; do
        [ "$waiting" = 0 ] && echo "Waiting for the game to close before deploying..." && waiting=1
        sleep 2
    done
    echo "Deployed to $mods/EpochAssist.dll"
fi
