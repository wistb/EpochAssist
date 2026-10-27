#!/usr/bin/env bash
# Usage: ./build.sh [build|deploy|dev|release|deploy-release|status]. Machine settings (LE_ML, DOTNET, NUGET_SOURCE) go in build.local.env.
#   build           debug build, nothing installed
#   deploy          debug build in Mods/ (no dev loader)
#   dev             hot-reload setup: dev loader in Mods/, builds in UserData/EpochAssist/dev
#   release         release package in dist/ (no dev code; checked)
#   deploy-release  release package, and its DLL in Mods/ in place of the dev setup (back with ./build.sh dev)
#   status          which setup the game has
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
[ -f "$root/build.local.env" ] && . "$root/build.local.env"

mode="${1:-build}"
case "$mode" in
    build|deploy|dev|status) config=Debug ;;
    release|deploy-release) config=Release ;;
    *) echo "usage: $0 [build|deploy|dev|release|deploy-release|status]" >&2; exit 2 ;;
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

game_ml="$LE_ML"
command -v wslpath >/dev/null 2>&1 && game_ml="$(wslpath -u "$LE_ML")"
game="$(dirname "$game_ml")"
mods="$game/Mods"

# Dev code is all in namespace EpochAssist.Dev; its name sits once, zero-terminated, in a DLL's metadata.
has_dev_code() { grep -qaP 'EpochAssist\.Dev\x00' "$1"; }

if [ "$mode" = status ]; then
    if [ -f "$mods/EpochAssist.DevLoader.dll" ]; then
        echo "dev: the dev loader is in Mods/ and loads UserData/EpochAssist/dev/EpochAssist.dll (hot reload)"
    elif [ -f "$mods/EpochAssist.dll" ]; then
        if has_dev_code "$mods/EpochAssist.dll"; then echo "deploy: a debug build is in Mods/ (dev code compiled in, not running)"
        else echo "release: a release build is in Mods/ (no dev code)"; fi
    else
        echo "none: Epoch Assist isn't installed in $mods"
    fi
    [ -f "$mods/EpochAssist.DevLoader.dll" ] && [ -f "$mods/EpochAssist.dll" ] && echo "warning: both the dev loader and EpochAssist.dll are in Mods/; everything would run twice" >&2
    exit 0
fi

build_project "$project"

out="$root/src/bin/Debug/net6.0/win-x64"

if [ "$mode" = deploy ]; then
    mkdir -p "$mods"
    [ -f "$mods/EpochAssist.DevLoader.dll" ] && wait_for rm -f "$mods/EpochAssist.DevLoader.dll"
    wait_for cp "$out/EpochAssist.dll" "$mods/EpochAssist.dll"
    echo "Deployed to $mods/EpochAssist.dll"
fi

if [ "$mode" = dev ]; then
    build_project "$root/tools/devloader/EpochAssist.DevLoader.csproj"
    loader="$root/tools/devloader/bin/Debug/net6.0/win-x64/EpochAssist.DevLoader.dll"
    mkdir -p "$mods"
    if [ ! -f "$mods/EpochAssist.DevLoader.dll" ]; then
        wait_for cp "$loader" "$mods/EpochAssist.DevLoader.dll"
        echo "Installed the dev loader; restart the game once to start using it"
    elif ! cmp -s "$loader" "$mods/EpochAssist.DevLoader.dll"; then
        # Rebuilds differ byte for byte even without changes; never block a hot reload on the running game.
        cp "$loader" "$mods/EpochAssist.DevLoader.dll" 2>/dev/null && echo "Updated the dev loader (takes effect on the next game start)" \
            || echo "Dev loader not updated while the game runs; the mod reload goes ahead"
    fi
    # The loader replaces the normal mod; both at once would run everything twice.
    [ -f "$mods/EpochAssist.dll" ] && wait_for rm -f "$mods/EpochAssist.dll"

    # Rename into place so the loader never reads a half-copied file.
    dev="$game/UserData/EpochAssist/dev"
    mkdir -p "$dev"
    cp "$out/EpochAssist.pdb" "$dev/EpochAssist.pdb.tmp" && mv -f "$dev/EpochAssist.pdb.tmp" "$dev/EpochAssist.pdb"
    cp "$out/EpochAssist.dll" "$dev/EpochAssist.dll.tmp" && mv -f "$dev/EpochAssist.dll.tmp" "$dev/EpochAssist.dll"
    # The planner fixtures are local only; the game reads them from the dev folder.
    if [ -d "$root/.reference/fixtures/planner" ]; then
        rm -rf "$dev/fixtures/planner" && mkdir -p "$dev/fixtures" && cp -r "$root/.reference/fixtures/planner" "$dev/fixtures/planner"
    fi
    echo "Dev build in $dev; the game reloads it within a second"
fi

if [ "$mode" = release ] || [ "$mode" = deploy-release ]; then
    version="$(sed -n 's/.*string Version = "\([^"]*\)".*/\1/p' "$root/src/Core/Host/Entry.cs")"
    zip="$root/dist/EpochAssist-v$version.zip"
    dll="$root/src/bin/Release/net6.0/win-x64/EpochAssist.dll"
    failed=0
    # Nothing of src/Dev/ may ship: name the source files that put the namespace back in.
    if has_dev_code "$dll"; then
        strays="$(grep -rlE '^namespace EpochAssist\.Dev(\.|;| |$)' "$root/src" --include='*.cs' --exclude-dir=Dev --exclude-dir=bin --exclude-dir=obj || true)"
        if [ -n "$strays" ]; then
            while read -r file; do echo "Release DLL contains dev-only code: namespace EpochAssist.Dev in ${file#"$root"/}" >&2; done <<< "$strays"
        else
            echo "Release DLL contains dev-only code (namespace EpochAssist.Dev); is src/Dev/ compiled in?" >&2
        fi
        failed=1
    fi
    expected=Mods/EpochAssist.dll
    actual="$(unzip -Z1 "$zip" | grep -v '/$' | sort)"
    while read -r entry; do [ -n "$entry" ] && echo "Release zip has unexpected entry $entry" >&2 && failed=1; done < <(comm -13 <(echo "$expected") <(echo "$actual"))
    while read -r entry; do [ -n "$entry" ] && echo "Release zip lacks $entry" >&2 && failed=1; done < <(comm -23 <(echo "$expected") <(echo "$actual"))
    if [ "$failed" = 1 ]; then
        rm -f "$zip"
        echo "Release package failed its checks; removed ${zip#"$root"/}" >&2
        exit 1
    fi
    echo "Release package checked: ${zip#"$root"/} (Mods/EpochAssist.dll only, no dev code)"
fi

if [ "$mode" = deploy-release ]; then
    mkdir -p "$mods"
    # The release replaces whatever setup was there; ./build.sh dev switches back.
    [ -f "$mods/EpochAssist.DevLoader.dll" ] && wait_for rm -f "$mods/EpochAssist.DevLoader.dll"
    wait_for unzip -o -q -j "$zip" Mods/EpochAssist.dll -d "$mods"
    echo "Release v$version in $mods/EpochAssist.dll (dev loader removed); start the game to try it, ./build.sh dev to go back"
fi
