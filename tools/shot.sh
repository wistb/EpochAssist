#!/usr/bin/env bash
# Drives a dev build in the running game through UserData/EpochAssist/dev/commands.txt and prints the results.
# Usage: tools/shot.sh [command...]   (no commands: "shot menu")
#   tools/shot.sh "menu open settings" "shot menu settings"
#   tools/shot.sh "menu open macros" "shot menu macros" "menu close" "hud panel open" "shot hud"
# Commands: menu open [feature id]|settings [tab], menu close, menu search [text], hud panel open|close,
#           set [setting id] [value], state, status, hotkeys, actions, run [action], facts, compat, move [x] [y] [held], stopmove,
#           probe [type] [filter], find [text], fonts, wait [frames|<n>ms],
#           see [game|ours|text|all] [filter] (numbered list of what is on screen: game windows, game controls and what covers
#           them, our menu, HUD and menu controls; text adds game text; game-window pixels),
#           shot view [name] (whole screen, numbered marks, 1600 px JPG; the marks become the see list),
#           shot full|menu|hud [name], shot rect <x> <y> <w> <h> [name], shot #n [name] (item n of the see list),
#           shot ui tooltip|hovered|<name or path end, quoted when it has spaces>[#n] [name];
#           shot options in any order: matte, jpg[quality], marks, fit<long edge>, x2..x8 (enlarge, square pixels),
#           theme list|set [name]|reload|problems|check, resources, toast [text], toasts, travel [scene], focus, draw test on|off,
#           map [icons [n]|volumes|step radius|maxdist|patch on|off],
#           planner [trees|nodes [id]|skills|selector open|close|open passive|skills|tree|picker|inventory|close|tab <n>|mastery|gear|idols|blessings|weaver|watch on|off|import [file]|check|resolve|show [stage]|paste [text]|host [url]|off],
#           patchcheck [game version] (saves dev/patch-check.json for tools/release.sh). Shots land in UserData/EpochAssist/dev/shots/.
# Input (offline, game may stay unfocused; x y are game-window pixels from the top left): reaches the mod's menu and HUD,
#           the game's panels and the game's key actions (movement, map, skills; aimed skills need a cursor step first).
#           key <Key> [ms], down <Key>, up <Key>, click <x> <y> [left|right|middle], press <x> <y> <button> <ms>,
#           (click, press, cursor, button and wheel take #n, item n of the last see list or shot marks, for x y),
#           button down|up <button> [x y], cursor <x> <y>, wheel <x> <y> <notches> (game panels only; the mod's menu
#           takes no wheel), type <text>, input, inputblock, wait <n>ms. move walks the character, it never moves the
#           pointer. A batch's end lets go of every key and button. Use tools/devloader/input.ps1 only to prove real OS input reaches the game.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
. "$root/build.local.env"
game_ml="$LE_ML"
command -v wslpath >/dev/null 2>&1 && game_ml="$(wslpath -u "$LE_ML")"
dev="$(dirname "$game_ml")/UserData/EpochAssist/dev"
[ -d "$dev" ] || { echo "No dev folder at $dev; run ./build.sh dev first" >&2; exit 1; }

[ $# -eq 0 ] && set -- "shot menu"
rm -f "$dev/result.txt"
printf '%s\n' "$@" > "$dev/commands.txt.tmp"
mv -f "$dev/commands.txt.tmp" "$dev/commands.txt"

# The game only runs commands while it updates; a paused or minimized game leaves them waiting.
for _ in $(seq 1 150); do
    if [ -f "$dev/result.txt" ] && grep -q '^done' "$dev/result.txt"; then
        while read -r kind rest; do
            case "$kind" in
                shot) echo "$dev/shots/${rest%% *}  (${rest#* })" ;;
                done) ;;
                *) echo "$kind $rest" ;;
            esac
        done < "$dev/result.txt"
        exit 0
    fi
    sleep 0.2
done
echo "No answer from the game after 30 s (is it running a dev build, and not paused?)" >&2
exit 1
