#!/usr/bin/env bash
# Fills one nexus/ page-text file for a release and prints it; tools/release.sh and .github/workflows/nexus.yml both use
# it, so the page they check is the page they push.
# Usage: tools/fill-nexus.sh <file> <version> <game version> <git ref>
#   {{version}}, {{game_version}}  replaced by the values given
#   {{media:<name>}}               on a line of its own: the image nexus/media/<name>.png from GitHub at <git ref>, collapsed
#                                  in a spoiler like the README's (consecutive lines share one), or nothing while that image
#                                  doesn't exist yet (nexus/media/README.md lists them)
# Any other {{...}} is left in place for the caller to refuse.
set -euo pipefail

[ $# -eq 4 ] || { echo "usage: $0 <file> <version> <game version> <git ref>" >&2; exit 1; }
file="$1" version="$2" game="$3" ref="$4"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
raw="https://raw.githubusercontent.com/wistb/EpochAssist/$ref/nexus/media"

# Images from consecutive media lines, written as one spoiler when the run ends.
images=""
flush() {
    [ -n "$images" ] && printf '[spoiler]%s[/spoiler]\n' "$images"
    images=""
}

while IFS= read -r line || [ -n "$line" ]; do
    if [[ "$line" =~ ^[[:space:]]*\{\{media:([a-z0-9-]+)\}\}[[:space:]]*$ ]]; then
        name="${BASH_REMATCH[1]}"
        [ -f "$root/nexus/media/$name.png" ] && images+="${images:+$'\n'}[img]$raw/$name.png[/img]"
        continue
    fi
    flush
    line="${line//\{\{version\}\}/$version}"
    printf '%s\n' "${line//\{\{game_version\}\}/$game}"
done < "$file"
flush
