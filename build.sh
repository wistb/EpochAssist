#!/usr/bin/env bash
# Usage: ./build.sh [build|deploy|dev|release]. dev: hot-reload setup (dev loader in Mods/, build in UserData/EpochAssist/dev). Machine settings (LE_ML, DOTNET, NUGET_SOURCE) go in build.local.env.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
[ -f "$root/build.local.env" ] && . "$root/build.local.env"

mode="${1:-build}"
case "$mode" in
    build|deploy|dev) config=Debug ;;
    release) config=Release ;;
    *) echo "usage: $0 [build|deploy|dev|release]" >&2; exit 2 ;;
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
build_project() {
    local path="$1"
    case "$DOTNET" in *.exe) path="$(wslpath -w "$path")" ;; esac
    local args=(build "$path" -c "$config" "-p:LE_ML=$LE_ML")
    [ -n "${NUGET_SOURCE:-}" ] && args+=(--source "$NUGET_SOURCE")
    "$DOTNET" "${args[@]}"
}

# The game locks mod DLLs while it runs; retry until it lets go.
wait_for() {
    local waiting=0
    until "$@" 2>/dev/null; do
        [ "$waiting" = 0 ] && echo "Waiting for the game to close..." && waiting=1
        sleep 2
    done
}

build_project "$project"

out="$root/src/bin/Debug/net6.0/win-x64"
game_ml="$LE_ML"
command -v wslpath >/dev/null 2>&1 && game_ml="$(wslpath -u "$LE_ML")"
game="$(dirname "$game_ml")"
mods="$game/Mods"

if [ "$mode" = deploy ]; then
    mkdir -p "$mods"
    [ -f "$mods/EpochAssist.DevLoader.dll" ] && wait_for rm -f "$mods/EpochAssist.DevLoader.dll"
    wait_for cp "$out/EpochAssist.dll" "$mods/EpochAssist.dll"
    echo "Deployed to $mods/EpochAssist.dll"
fi

if [ "$mode" = dev ]; then
    build_project "$root/devloader/EpochAssist.DevLoader.csproj"
    loader="$root/devloader/bin/Debug/net6.0/win-x64/EpochAssist.DevLoader.dll"
    mkdir -p "$mods"
    if ! cmp -s "$loader" "$mods/EpochAssist.DevLoader.dll"; then
        wait_for cp "$loader" "$mods/EpochAssist.DevLoader.dll"
        echo "Installed the dev loader; restart the game once to start using it"
    fi
    # The loader replaces the normal mod; both at once would run everything twice.
    [ -f "$mods/EpochAssist.dll" ] && wait_for rm -f "$mods/EpochAssist.dll"

    # Rename into place so the loader never reads a half-copied file.
    dev="$game/UserData/EpochAssist/dev"
    mkdir -p "$dev"
    cp "$out/EpochAssist.pdb" "$dev/EpochAssist.pdb.tmp" && mv -f "$dev/EpochAssist.pdb.tmp" "$dev/EpochAssist.pdb"
    cp "$out/EpochAssist.dll" "$dev/EpochAssist.dll.tmp" && mv -f "$dev/EpochAssist.dll.tmp" "$dev/EpochAssist.dll"
    echo "Dev build in $dev; the game reloads it within a second"
fi
