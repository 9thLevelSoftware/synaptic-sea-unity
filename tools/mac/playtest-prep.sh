#!/bin/bash
# Prepares and collects a human playtest of the built macOS dev player.
#
# Usage: tools/mac/playtest-prep.sh [--clear] [--collect] [options]
#   (no flags)   DRY RUN. Prints the build's git SHA and whether it equals origin/main, the paths the game writes to, what would be
#                backed up and cleared, and the launch command. Changes nothing.
#   --clear      Back up the game's saves/settings/runs and Player.log to builds/playtest/backup-<time>/, verify the backup, then clear
#                them so the session starts from a fresh install. Nothing is deleted unless the backup verified.
#   --collect    After the session: copy Player.log, the saves and the build stamp into builds/playtest/session-<time>/, run the smoke
#                error check on the log, and print the boot seed(s) and any errors or exceptions. Writes collect-summary.txt there.
# Options:
#   --expect-sha SHA        compare the build with SHA instead of origin/main (also skips the git fetch)
#   --allow-sha-mismatch    carry on (with a warning) when the build is not the expected commit
#   --app PATH              the .app to inspect (default builds/StandaloneOSX/dev/TheSynapticSea.app)
#   --data-dir DIR          the game's persistentDataPath (default ~/Library/Application Support/9th Level Software/The Synaptic Sea)
#   --log-dir DIR           the folder holding Player.log (default ~/Library/Logs/9th Level Software/The Synaptic Sea)
#   --out-dir DIR           where backups and collected sessions go (default builds/playtest)
#   --skip-running-check    do not refuse when a game or Unity editor process is running (tests only)
#
# The data directory also holds Unity/ (analytics) and TestResults.xml (test output); both are left alone.

set -uo pipefail
# shellcheck source=env.sh
. "$(dirname "${BASH_SOURCE[0]}")/env.sh"

CLEAR=0; COLLECT=0; EXPECT_SHA=""; ALLOW_MISMATCH=0; SKIP_RUNNING=0
APP="$REPO/builds/StandaloneOSX/dev/TheSynapticSea.app"
DATA_DIR="$HOME/Library/Application Support/9th Level Software/The Synaptic Sea"
LOG_DIR="$HOME/Library/Logs/9th Level Software/The Synaptic Sea"
OUT_DIR="$REPO/builds/playtest"
while [ $# -gt 0 ]; do
    case "$1" in
        --clear) CLEAR=1 ;;
        --collect) COLLECT=1 ;;
        --expect-sha) shift; EXPECT_SHA="${1:-}"; [ -n "$EXPECT_SHA" ] || die "--expect-sha needs a value" ;;
        --allow-sha-mismatch) ALLOW_MISMATCH=1 ;;
        --app) shift; APP="${1:-}" ;;
        --data-dir) shift; DATA_DIR="${1:-}" ;;
        --log-dir) shift; LOG_DIR="${1:-}" ;;
        --out-dir) shift; OUT_DIR="${1:-}" ;;
        --skip-running-check) SKIP_RUNNING=1 ;;
        -h|--help) sed -n 2,22p "$0"; exit 0 ;;
        *) die "unknown argument: $1 (see --help)" ;;
    esac
    shift
done
[ "$CLEAR" -eq 1 ] && [ "$COLLECT" -eq 1 ] && die "use --clear before the session and --collect after it, not both at once"

STAMP="$APP/Contents/Resources/Data/StreamingAssets/build_stamp.json"
PLAYER_LOG="$LOG_DIR/Player.log"
TS="$(date +%Y%m%dT%H%M%S)"

# ---- refuse while the game or a Unity editor is running
if [ "$SKIP_RUNNING" -eq 0 ]; then
    if pgrep -f "$APP/Contents/MacOS" >/dev/null 2>&1 || pgrep -x "The Synaptic Sea" >/dev/null 2>&1; then
        die "The game is running. Quit it first."
    fi
    if pgrep -f "projectPath $PROJECT" >/dev/null 2>&1 || pgrep -f "projectpath $PROJECT" >/dev/null 2>&1; then
        die "A Unity editor has $PROJECT open. Close it first."
    fi
fi

# game data = everything in the data dir except Unity/ and TestResults.xml
game_data_entries() {
    [ -d "$DATA_DIR" ] || return 0
    local e
    for e in "$DATA_DIR"/* "$DATA_DIR"/.[!.]*; do
        [ -e "$e" ] || continue
        case "$(basename "$e")" in Unity|TestResults.xml) continue ;; esac
        echo "$e"
    done
}

# ---- the build
[ -f "$STAMP" ] || die "no build stamp at $STAMP (build first: tools/mac/build.sh dev)"
read_stamp() { python3 -I - "$STAMP" "$1" <<'PY'
import json, sys
print(json.load(open(sys.argv[1])).get(sys.argv[2], ""))
PY
}
B_KIND="$(read_stamp build_kind)"; B_SHA="$(read_stamp git_sha)"; B_WHEN="$(read_stamp built_utc)"; B_VER="$(read_stamp version)"
echo "Build:    kind=$B_KIND version=$B_VER git_sha=$B_SHA built_utc=$B_WHEN"
echo "App:      $APP"

if [ -n "$EXPECT_SHA" ]; then WANT="$EXPECT_SHA"; WANT_LABEL="--expect-sha"
else
    git -C "$REPO" fetch origin main:refs/remotes/origin/main >/dev/null 2>&1 || echo "Warning: could not fetch origin/main; comparing with the local copy."
    WANT="$(git -C "$REPO" rev-parse --short=7 origin/main 2>/dev/null)"; WANT_LABEL="origin/main"
fi
if [ -z "$B_SHA" ] || [ -z "$WANT" ]; then
    echo "SHA check: cannot compare (build sha='$B_SHA', $WANT_LABEL='$WANT')"; SHA_OK=0
elif [ "${B_SHA:0:7}" = "${WANT:0:7}" ]; then
    echo "SHA check: OK, the build is $WANT_LABEL ($WANT)"; SHA_OK=1
else
    echo "SHA check: MISMATCH, the build is $B_SHA but $WANT_LABEL is $WANT. Rebuild with tools/mac/build.sh dev from the current main."; SHA_OK=0
fi
[ "$B_KIND" = "dev" ] || { echo "Warning: this is a '$B_KIND' build; the playtest kit expects a dev build (F5/F6/F9 save keys are dev-only)."; }
if [ "$SHA_OK" -eq 0 ] && [ "$ALLOW_MISMATCH" -eq 0 ]; then
    echo "Stopping: the build is not the expected commit. Fix it, or pass --allow-sha-mismatch."; exit 1
fi

echo "Data dir: $DATA_DIR"
echo "Log:      $PLAYER_LOG"

# ---- collect
if [ "$COLLECT" -eq 1 ]; then
    SESSION="$OUT_DIR/session-$TS"; mkdir -p "$SESSION/data"
    cp "$STAMP" "$SESSION/build_stamp.json"
    [ -f "$PLAYER_LOG" ] && cp "$PLAYER_LOG" "$SESSION/Player.log" || echo "Warning: no Player.log at $PLAYER_LOG"
    [ -f "$LOG_DIR/Player-prev.log" ] && cp "$LOG_DIR/Player-prev.log" "$SESSION/Player-prev.log"
    while IFS= read -r entry; do [ -n "$entry" ] && cp -R "$entry" "$SESSION/data/"; done < <(game_data_entries)
    SUMMARY="$SESSION/collect-summary.txt"
    {
        echo "Playtest session collected $TS"
        echo "Build: kind=$B_KIND version=$B_VER git_sha=$B_SHA built_utc=$B_WHEN"
        if [ -f "$SESSION/Player.log" ]; then
            echo "Log lines: $(wc -l < "$SESSION/Player.log" | tr -d ' ')"
            echo "--- Boot lines (seed, layout):"
            grep -E '\[PlayableBootstrap\] booted' "$SESSION/Player.log" | sed 's/^/  /' || true
            [ -n "$(grep -E '\[PlayableBootstrap\] booted' "$SESSION/Player.log")" ] || echo "  (none: the session never reached a playable run)"
            echo "--- Smoke error check (same rules as tools/mac/smoke.sh):"
            "$TOOLS_MAC_DIR/smoke.sh" --check-log "$SESSION/Player.log" "${B_KIND:-dev}" 2>&1 | sed 's/^/  /'
        else
            echo "No Player.log was collected."
        fi
        echo "--- Saved data copied: $(find "$SESSION/data" -type f 2>/dev/null | wc -l | tr -d ' ') file(s)"
    } | tee "$SUMMARY"
    echo "Collected into: $SESSION"
    exit 0
fi

# ---- plan / clear
ENTRIES=(); while IFS= read -r entry; do [ -n "$entry" ] && ENTRIES+=("$entry"); done < <(game_data_entries)
LOGS_TO_CLEAR=(); for f in "$PLAYER_LOG" "$LOG_DIR/Player-prev.log"; do [ -f "$f" ] && LOGS_TO_CLEAR+=("$f"); done
echo
echo "Would back up and clear:"
if [ "${#ENTRIES[@]}" -eq 0 ] && [ "${#LOGS_TO_CLEAR[@]}" -eq 0 ]; then echo "  (nothing: no saves, settings or logs exist)"; fi
for e in "${ENTRIES[@]:-}"; do [ -n "$e" ] && echo "  data: $e"; done
for f in "${LOGS_TO_CLEAR[@]:-}"; do [ -n "$f" ] && echo "  log:  $f"; done

if [ "$CLEAR" -eq 0 ]; then
    echo
    echo "DRY RUN: nothing was changed. Pass --clear to back up and clear them for a fresh install."
else
    BACKUP="$OUT_DIR/backup-$TS"; mkdir -p "$BACKUP/data" "$BACKUP/logs"
    for e in "${ENTRIES[@]:-}"; do [ -n "$e" ] && cp -R "$e" "$BACKUP/data/"; done
    for f in "${LOGS_TO_CLEAR[@]:-}"; do [ -n "$f" ] && cp "$f" "$BACKUP/logs/"; done
    # verify the backup before deleting anything: same number of files and the same total size per source
    src_files=0; for e in "${ENTRIES[@]:-}"; do [ -n "$e" ] && src_files=$((src_files + $(find "$e" -type f | wc -l))); done
    bak_files="$(find "$BACKUP/data" -type f | wc -l | tr -d ' ')"
    src_logs="${#LOGS_TO_CLEAR[@]}"; bak_logs="$(find "$BACKUP/logs" -type f | wc -l | tr -d ' ')"
    src_files="$(echo "$src_files" | tr -d ' ')"
    if [ "$src_files" != "$bak_files" ] || [ "$src_logs" != "$bak_logs" ]; then
        die "Backup check failed (data files $src_files vs $bak_files, logs $src_logs vs $bak_logs). Nothing was deleted. Backup left at $BACKUP"
    fi
    for e in "${ENTRIES[@]:-}"; do
        [ -n "$e" ] || continue
        diff -rq "$e" "$BACKUP/data/$(basename "$e")" >/dev/null 2>&1 || die "Backup differs from the original for $e. Nothing was deleted. Backup left at $BACKUP"
    done
    for e in "${ENTRIES[@]:-}"; do [ -n "$e" ] && rm -rf "$e"; done
    for f in "${LOGS_TO_CLEAR[@]:-}"; do [ -n "$f" ] && rm -f "$f"; done
    echo
    echo "Backed up ($src_files data file(s), $src_logs log(s)) to: $BACKUP"
    echo "Cleared. The next launch is a fresh install."
fi

echo
echo "Launch the game with:"
echo "  open \"$APP\""
echo "Quit the game when the session ends, then run:"
echo "  tools/mac/playtest-prep.sh --collect"
