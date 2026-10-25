#!/usr/bin/env bash
# Cuts a release: checks, then sets the version and tested game version,
# dates the changelog entry, builds and checks the zip, commits, tags, pushes, creates the GitHub release, and waits for
# the Nexus workflow (.github/workflows/nexus.yml) that the release starts. A re-run finishes a release that stopped
# after its commit. This script never sees the Nexus key: it is a repository secret only the workflow reads.
# Usage: tools/release.sh <version> <game version> [--dry-run [--nexus-preview]]
#   <version>        major.minor.patch, e.g. 0.1.0
#   <game version>   the Last Epoch version tested on, as the game shows it, e.g. 1.5.3
#   --dry-run        check, build and preview; change nothing (Entry.cs and CHANGELOG.md are restored)
#   --nexus-preview  with --dry-run: also run the workflow's preview (reads Nexus, writes nothing)
# Exit codes: 0 done or previewed, 1 refused before any change, 2 failed after the commit, 130 interrupted.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
[ -f "$root/build.local.env" ] && . "$root/build.local.env"

usage() { echo "usage: $0 <version> <game version> [--dry-run [--nexus-preview]]" >&2; exit 1; }

version="" game="" dry=0 preview=0
while [ $# -gt 0 ]; do
    case "$1" in
        --dry-run) dry=1 ;;
        --nexus-preview) preview=1 ;;
        -*) usage ;;
        *) if [ -z "$version" ]; then version="$1"; elif [ -z "$game" ]; then game="$1"; else usage; fi ;;
    esac
    shift
done
[ -n "$version" ] && [ -n "$game" ] || usage
[ "$preview" = 0 ] || [ "$dry" = 1 ] || usage

tag="v$version"
zip="dist/EpochAssist-v$version.zip"
notes="dist/release-notes-v$version.md"
cliff=(npx --yes git-cliff@2.14.2)
steps=(checks prepare build commit tag push github nexus)
declare -A state=()
current=checks
restore=0     # set once the tree is known clean: from then on, Entry.cs and CHANGELOG.md are put back unless committed
committed=0
resume=0

say() { printf '  %-16s %s\n' "$1" "$2"; }
mark() { state[$1]="$2"; say "$1" "$2"; }
refuse() { echo "refused: $1" >&2; exit 1; }
warn() { echo "warning: $1" >&2; }

summary() {
    echo
    echo "$tag (Last Epoch $game)$([ "$dry" = 1 ] && echo ", dry run")$([ "$resume" = 1 ] && echo ", resumed")"
    for s in "${steps[@]}"; do say "$s" "${state[$s]:-left}"; done
}

on_exit() {
    local rc=$?
    trap - EXIT INT TERM
    if [ "$restore" = 1 ] && { [ "$dry" = 1 ] || [ "$committed" = 0 ]; }; then
        git checkout -- src/Core/Host/Entry.cs CHANGELOG.md
        [ "$dry" = 1 ] && [ -n "${state[prepare]:-}" ] && state[prepare]="${state[prepare]} (restored)"
    fi
    if [ "$rc" = 0 ]; then
        summary
        exit 0
    fi
    [ -z "${state[$current]:-}" ] && state[$current]="FAILED"
    if [ "$committed" = 1 ]; then
        summary
        echo "run again: tools/release.sh $version $game"
        [ "$rc" = 130 ] && exit 130
        exit 2
    fi
    [ "$rc" = 130 ] && echo "interrupted; nothing was changed" >&2
    [ "$rc" = 130 ] && exit 130
    exit 1
}
trap on_exit EXIT
# Bash holds Ctrl+C back while a foreground child runs, so the build runs in the background (run_build) and is stopped here.
child=""
on_signal() {
    if [ -n "$child" ]; then
        pkill -TERM -P "$child" 2>/dev/null || true
        kill "$child" 2>/dev/null || true
    fi
    exit 130
}
trap on_signal INT TERM
run_build() {
    ./build.sh release > "$1" 2>&1 &
    child=$!
    local rc=0
    wait "$child" || rc=$?
    child=""
    return "$rc"
}

field() { sed -n "s/.*public const string $1 = \"\\([^\"]*\\)\";.*/\\1/p" "${2:-src/Core/Host/Entry.cs}"; }
newer() { [ "$1" != "$2" ] && [ "$(printf '%s\n%s\n' "$1" "$2" | sort -V | tail -1)" = "$1" ]; }
# Leading and trailing blank lines dropped.
trim() { sed -e '/./,$!d' | sed -e ':a' -e '/^\n*$/{$d;N;ba' -e '}'; }

# --- checks ---------------------------------------------------------------------------------------------------------

# 1. Arguments.
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || refuse "version must look like 1.2.3"
[[ "$game" =~ ^[0-9]+(\.[0-9]+){1,3}$ ]] || refuse "game version must look like 1.5.3"

# 2. A clean tree.
[ -z "$(git status --porcelain)" ] || refuse "uncommitted changes (commit or stash them first)"
restore=1

# 3. On main, not behind origin/main.
[ "$(git symbolic-ref --short -q HEAD || true)" = main ] || refuse "not on main"
git fetch -q origin
[ "$(git rev-list --count HEAD..origin/main)" = 0 ] || refuse "main is behind origin/main (pull first)"

# Resume: a real run whose tag is already at HEAD, with the version committed there.
if [ "$dry" = 0 ] && git rev-parse -q --verify "refs/tags/$tag" >/dev/null \
    && [ "$(git rev-parse "$tag^{commit}")" = "$(git rev-parse HEAD)" ] && [ "$(field Version)" = "$version" ]; then
    resume=1
    committed=1
    restore=0
    echo "resuming $tag"
    [ "$(field TestedGameVersion)" = "$game" ] || refuse "$tag was released as tested with Last Epoch $(field TestedGameVersion), not $game"
fi

has_tags=0
[ -n "$(git tag -l 'v*')" ] && has_tags=1
patch_file=""
if [ "$resume" = 0 ]; then
    # 4. The version moves forward; the first release may keep the version it was developed under.
    current_version="$(field Version)"
    if ! newer "$version" "$current_version" && ! { [ "$has_tags" = 0 ] && [ "$version" = "$current_version" ]; }; then
        refuse "$version is not newer than $current_version"
    fi

    # 5. The tag is free here and on GitHub.
    if git rev-parse -q --verify "refs/tags/$tag" >/dev/null || [ -n "$(git ls-remote --tags origin "refs/tags/$tag")" ]; then
        refuse "tag $tag already exists"
    fi

    # 6. The changelog entry: hand-written for the first release, generated from the commits after that.
    if [ "$has_tags" = 0 ]; then
        body="$(awk -v h="## $version" '{ l = $0; sub(/[ \t]+$/, "", l) } !f && l == h { f = 1; next } f && /^## / { exit } f { print }' CHANGELOG.md | trim)"
        grep -q '^- ' <<< "$body" || refuse "write the first release's entry under the CHANGELOG.md header (## $version)"
    else
        last_tag="$(git describe --tags --abbrev=0 --match 'v*')"
        generated="$("${cliff[@]}" --unreleased --tag "$tag" --strip all 2>/dev/null)" || { echo "FAILED: git-cliff (${cliff[*]})" >&2; exit 1; }
        body="$(tail -n +2 <<< "$generated" | trim)"
        grep -q '^- ' <<< "$body" || refuse "nothing to release since $last_tag (no feat or fix commits that players see)"
    fi
    entry="$(printf '## %s - %s\n\nTested with Last Epoch %s.\n\n%s\n' "$version" "$(date +%F)" "$game" "$body")"

    # 7. A passed patch check on this game version (written by "patchcheck <game version>" in a dev build).
    patch_file="$(dirname "$(wslpath -u "${LE_ML:-/nonexistent}" 2>/dev/null || echo "${LE_ML:-/nonexistent}")")/UserData/EpochAssist/dev/patch-check.json"
    if [ -z "${LE_ML:-}" ] || [ ! -f "$patch_file" ] || [ "$(jq -r .game_version "$patch_file")" != "$game" ] || [ "$(jq -r .result "$patch_file")" != pass ]; then
        refuse "no passed patch check for Last Epoch $game; run \"patchcheck $game\" in a dev build"
    fi
fi

# 8. GitHub and the Nexus workflow's settings; only warnings in a dry run.
gh_ok=1
warnings=0
gh_problem() {
    gh_ok=0
    warnings=$((warnings + 1))
    if [ "$dry" = 1 ]; then warn "$1"; else refuse "$1"; fi
}
if ! gh auth status >/dev/null 2>&1; then
    gh_problem "GitHub CLI not logged in"
else
    gh secret list --json name -q '.[].name' 2>/dev/null | grep -qx NEXUSMODS_API_KEY || gh_problem "repository secret NEXUSMODS_API_KEY is missing"
    for name in NEXUSMODS_FILE_ID NEXUSMODS_MOD_ID; do
        gh variable get "$name" >/dev/null 2>&1 || gh_problem "repository variable $name is missing"
    done
    git cat-file -e origin/main:.github/workflows/nexus.yml 2>/dev/null || gh_problem "the Nexus workflow is not on main"
fi
file_id="$(gh variable get NEXUSMODS_FILE_ID 2>/dev/null || echo '?')"

# 9. The page text fills cleanly: the workflow pushes it to Nexus.
# Images link to GitHub at the release tag (tools/fill-nexus.sh).
fill() { tools/fill-nexus.sh "nexus/$1" "$version" "$game" "$tag"; }
if [ "$resume" = 0 ]; then
    for file in summary.txt description.bbcode; do
        left="$(fill "$file" | grep -o '{{[^}]*}}' | head -1 || true)"
        [ -z "$left" ] || refuse "unfilled placeholder $left in nexus/$file"
    done
    length="$(fill summary.txt | tr -s '[:space:]' ' ' | sed -e 's/^ //' -e 's/ $//' | wc -m)"
    [ "$length" -le 350 ] || refuse "Nexus summary is $length characters (max 350)"
fi
mark checks "ok$([ "$warnings" = 0 ] || echo " ($warnings warning$([ "$warnings" = 1 ] || echo s), see above)")"

# --- prepare, build, commit, tag ------------------------------------------------------------------------------------

# The GitHub notes: the entry's items, the tested line and the patch check.
write_notes() {
    local check=""
    if [ -n "$patch_file" ] && [ -f "$patch_file" ] && [ "$(jq -r .game_version "$patch_file")" = "$game" ] && [ "$(jq -r .result "$patch_file")" = pass ]; then
        check="$(jq -r '"Patch-target check: pass, \(.targets | length) targets, \(.checked_at[0:10])."' "$patch_file")"
    fi
    mkdir -p dist
    { printf '%s\n\nTested with Last Epoch %s.\n' "$1" "$game"; [ -n "$check" ] && echo "$check"; } > "$notes"
}

if [ "$resume" = 0 ]; then
    current=prepare
    sed -i -E -e "s/(public const string Version = \")[^\"]*(\";)/\\1$version\\2/" \
        -e "s/(public const string TestedGameVersion = \")[^\"]*(\";)/\\1$game\\2/" src/Core/Host/Entry.cs
    grep -qF "public const string Version = \"$version\";" src/Core/Host/Entry.cs && grep -qF "public const string TestedGameVersion = \"$game\";" src/Core/Host/Entry.cs \
        || { echo "FAILED: could not set the version in src/Core/Host/Entry.cs" >&2; exit 1; }

    entry_file="$(mktemp)"
    printf '%s\n' "$entry" > "$entry_file"
    if [ "$has_tags" = 0 ]; then
        # The hand-written section is replaced by the dated one.
        awk -v h="## $version" -v e="$entry_file" '
            { l = $0; sub(/[ \t]+$/, "", l) }
            !done && l == h { while ((getline line < e) > 0) print line; skip = 1; done = 1; next }
            skip && /^## / { skip = 0; print "" }
            !skip { print }' CHANGELOG.md > CHANGELOG.md.tmp
    else
        # Newest first, right under the header.
        awk -v e="$entry_file" '
            !done && /^## / { while ((getline line < e) > 0) print line; print ""; done = 1 }
            { print }
            END { if (!done) { print ""; while ((getline line < e) > 0) print line } }' CHANGELOG.md > CHANGELOG.md.tmp
    fi
    mv CHANGELOG.md.tmp CHANGELOG.md
    rm -f "$entry_file"

    mkdir -p dist
    fill summary.txt > dist/nexus-summary.txt
    fill description.bbcode > dist/nexus-description.bbcode
    write_notes "$body"
    echo "changelog entry:"
    sed 's/^/    /' <<< "$entry"
    mark prepare ok

    current=build
    log="dist/release-build.log"
    if ! run_build "$log"; then
        tail -n 20 "$log" >&2
        echo "FAILED: ./build.sh release (full output in $log)" >&2
        exit 1
    fi
    unzip -Z1 "$zip" | sed 's/^/    /'
    sha="$(sha256sum "$zip" | cut -d' ' -f1)"
    mark build "ok  $zip  sha256 $sha"

    current=commit
    changed="$(git diff --name-only | sort | tr '\n' ' ')"
    [ "$changed" = "CHANGELOG.md src/Core/Host/Entry.cs " ] || { echo "FAILED: unexpected changes: $changed" >&2; exit 1; }
    commit_body="$(tail -n +3 <<< "$entry")"
    if [ "$dry" = 1 ]; then
        mark commit "would: chore(release): $tag"
        mark tag "would: $tag"
    else
        git commit -q -a -m "chore(release): $tag" -m "$commit_body"
        committed=1
        mark commit "ok  $(git rev-parse --short HEAD) chore(release): $tag"
        current=tag
        git tag -a "$tag" -m "Epoch Assist $version"
        mark tag "ok  $tag"
    fi
else
    state[prepare]="skip (already done)"
    state[commit]="skip (already done)"
    state[tag]="skip (already done)"
    current=build
    if [ -f "$zip" ]; then
        mark build "skip (already done)  $zip"
    elif gh release view "$tag" --json assets -q '.assets[].name' 2>/dev/null | grep -qx "EpochAssist-v$version.zip"; then
        gh release download "$tag" --pattern "EpochAssist-v$version.zip" --dir dist
        mark build "ok  $zip from the GitHub release"
    else
        log="dist/release-build.log"
        run_build "$log" || { tail -n 20 "$log" >&2; echo "FAILED: ./build.sh release (full output in $log)" >&2; exit 1; }
        mark build "ok  $zip rebuilt from $tag  sha256 $(sha256sum "$zip" | cut -d' ' -f1)"
    fi
    if [ ! -f "$notes" ]; then
        # The committed entry, without its heading and tested line.
        body="$(awk -v h="## $version - " 'index($0, h) == 1 { f = 1; next } f && /^## / { exit } f && !/^Tested with Last Epoch / { print }' CHANGELOG.md | trim)"
        patch_file="$(dirname "$(wslpath -u "${LE_ML:-/nonexistent}" 2>/dev/null || echo /nonexistent)")/UserData/EpochAssist/dev/patch-check.json"
        write_notes "$body"
    fi
fi

# --- push -----------------------------------------------------------------------------------------------------------

current=push
if [ "$dry" = 1 ]; then
    ahead="$(git rev-list --count origin/main..HEAD)"
    git log --oneline origin/main..HEAD | sed 's/^/    also pushes: /'
    mark push "would: main + $tag ($((ahead + 1)) commit$([ $((ahead + 1)) = 1 ] || echo s))  git push --atomic origin main refs/tags/$tag"
else
    remote="$(git ls-remote --tags origin "refs/tags/$tag^{}" | cut -f1)"
    if [ -z "$remote" ]; then
        git push -q --atomic origin main "refs/tags/$tag"
        mark push "ok  main + $tag"
    elif [ "$remote" = "$(git rev-parse HEAD)" ]; then
        mark push "skip (already done)"
    else
        echo "FAILED: the tag $tag on GitHub is on another commit ($remote)" >&2
        exit 2
    fi
fi

# --- GitHub release -------------------------------------------------------------------------------------------------

current=github
title="Epoch Assist $version"
created=0
if [ "$dry" = 1 ]; then
    echo "    title: $title"
    echo "    asset: $(basename "$zip")"
    echo "    notes:"
    sed 's/^/      /' "$notes"
    mark github "would: \"$title\" with $(basename "$zip")"
elif assets="$(gh release view "$tag" --json assets -q '.assets[].name' 2>/dev/null)"; then
    if grep -qx "EpochAssist-v$version.zip" <<< "$assets"; then
        mark github "skip (already done)"
    elif [ -n "$assets" ]; then
        echo "FAILED: the GitHub release $tag has other assets ($(tr '\n' ' ' <<< "$assets")) but not EpochAssist-v$version.zip" >&2
        exit 2
    else
        gh release upload "$tag" "$zip"
        mark github "ok  uploaded $(basename "$zip") to the existing release"
    fi
else
    gh release create "$tag" "$zip" --verify-tag --title "$title" --notes-file "$notes" >/dev/null
    created=1
    mark github "ok  $(gh release view "$tag" --json url -q .url)"
fi

# --- Nexus (the workflow) -------------------------------------------------------------------------------------------

current=nexus
# The newest nexus.yml run of an event started since $2 (and, for a release, for this tag); polled for up to 2 minutes.
find_run() {
    local event="$1" since="$2" id=""
    for _ in $(seq 1 24); do
        id="$(gh run list --workflow nexus.yml --event "$event" --limit 10 --json databaseId,headBranch,createdAt \
            -q "[.[] | select(.createdAt >= \"$since\") | select(\"$event\" != \"release\" or .headBranch == \"$tag\")][0].databaseId // empty")"
        [ -n "$id" ] && break
        sleep 5
    done
    echo "$id"
}

if [ "$dry" = 1 ]; then
    items="$(grep -c '^- ' "$notes" || true)"
    if [ "$preview" = 1 ] && [ "$gh_ok" = 1 ]; then
        since="$(date -u -d '-30 sec' +%FT%TZ)"
        gh workflow run nexus.yml -f dry_run=true -f ref=main -f version="$version" -f game_version="$game"
        run="$(find_run workflow_dispatch "$since")"
        [ -n "$run" ] || { echo "FAILED: the preview run didn't appear within 2 minutes" >&2; exit 1; }
        echo "    preview run: $(gh run view "$run" --json url -q .url)"
        if ! gh run watch "$run" --exit-status --interval 10 >/dev/null 2>&1; then
            echo "FAILED: the preview run failed: gh run view $run --log-failed" >&2
            exit 1
        fi
        rm -rf dist/nexus-preview
        gh run download "$run" -n nexus-preview -D dist/nexus-preview
        echo "    report:"
        sed 's/^/      /' dist/nexus-preview/report.txt
        echo "    summary ($(tr -s '[:space:]' ' ' < dist/nexus-preview/summary.txt | sed -e 's/^ //' -e 's/ $//' | wc -m) characters):"
        sed 's/^/      /' dist/nexus-preview/summary.txt
        echo "    description diff: $(grep -c '^+[^+]' dist/nexus-preview/description.diff || true) lines added, $(grep -c '^-[^-]' dist/nexus-preview/description.diff || true) removed (dist/nexus-preview/description.diff)"
        mark nexus "ok (preview run $run, nothing written)"
    else
        [ "$preview" = 1 ] && warn "Nexus preview skipped: the GitHub settings above aren't in place"
        mark nexus "would: workflow nexus.yml on release (version $version of file $file_id, changelog $items lines, page text)"
    fi
else
    since="$(date -u -d '-10 min' +%FT%TZ)"
    if [ "$created" = 1 ]; then
        run="$(find_run release "$since")"
    else
        # The release was there already: start the workflow for the tag; it skips a version Nexus already has.
        since="$(date -u -d '-30 sec' +%FT%TZ)"
        gh workflow run nexus.yml -f tag="$tag"
        run="$(find_run workflow_dispatch "$since")"
    fi
    if [ -z "$run" ]; then
        echo "The Nexus workflow didn't start within 2 minutes; finish it with: gh workflow run nexus.yml -f tag=$tag" >&2
        state[nexus]="left: no workflow run"
        exit 2
    fi
    echo "    workflow run: $(gh run view "$run" --json url -q .url)"
    if gh run watch "$run" --exit-status --interval 10 >/dev/null 2>&1; then
        mark nexus "ok  workflow run $run"
    else
        echo "The Nexus workflow failed (gh run view $run --log-failed); finish it with: gh workflow run nexus.yml -f tag=$tag" >&2
        state[nexus]="left: workflow run $run failed"
        exit 2
    fi
fi
