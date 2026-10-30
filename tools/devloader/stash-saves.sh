#!/usr/bin/env bash
# Saves and restores the stash's save files (every folder, tab, item and the stash's gold) for development: buy and set up
# tabs, then put the stash back as it was. Only the stash files are copied; characters keep their progress.
# Usage: tools/devloader/stash-saves.sh save <name> | restore <name> | list
#   save     copies the stash files now (in game too; the game writes them after each change)
#   restore  copies them back; only at the main menu or character select, since a loaded character's stash is written
#            over the files. Load the character afterwards.
# Snapshots live in UserData/EpochAssist/dev/stash-saves/<name>/.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
. "$root/build.local.env"
game_ml="$LE_ML"
command -v wslpath >/dev/null 2>&1 && game_ml="$(wslpath -u "$LE_ML")"
dev="$(dirname "$game_ml")/UserData/EpochAssist/dev"
store="$dev/stash-saves"

profile="$(cmd.exe /c 'echo %USERPROFILE%' 2>/dev/null | tr -d '\r')"
saves="$(wslpath -u "$profile")/AppData/LocalLow/Eleventh Hour Games/Last Epoch/Saves"
[ -d "$saves" ] || { echo "No save files at $saves" >&2; exit 1; }

usage() { echo "usage: $0 save <name> | restore <name> | list" >&2; exit 2; }

# True while a character is loaded (the bridge's state says "in game True"); no answer counts as not loaded.
in_game() {
    local state
    state="$("$root/tools/shot.sh" state 2>/dev/null || true)"
    [[ "$state" == *"in game True"* ]]
}

case "${1:-}" in
    save)
        name="${2:-}"; [ -n "$name" ] || usage
        files=("$saves"/STASH_CYCLE_*)
        [ -e "${files[0]}" ] || { echo "No stash files in $saves" >&2; exit 1; }
        rm -rf "$store/$name"
        mkdir -p "$store/$name"
        cp -a "${files[@]}" "$store/$name/"
        echo "Saved ${#files[@]} stash files as $name"
        ;;
    restore)
        name="${2:-}"; [ -n "$name" ] || usage
        [ -d "$store/$name" ] || { echo "No stash snapshot $name (see: $0 list)" >&2; exit 1; }
        if in_game; then
            echo "A character is loaded: go to character select first, or the game writes its stash over the files" >&2
            exit 1
        fi
        rm -f "$saves"/STASH_CYCLE_*
        cp -a "$store/$name"/STASH_CYCLE_* "$saves/"
        echo "Restored stash $name; load the character"
        ;;
    list)
        [ -d "$store" ] && ls -1 "$store" || echo "No stash snapshots yet"
        ;;
    *) usage ;;
esac
