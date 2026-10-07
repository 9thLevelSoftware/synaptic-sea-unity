#!/bin/bash
# Smoke-launches the built macOS player headless and fails on any exception or error in its log, or on the wrong
# build kind. macOS counterpart of tools/verify-headless.ps1. Build first with tools/mac/build.sh.
# Usage: tools/mac/smoke.sh [dev|demo|release] [max-seconds]   (defaults: dev, 30)
# The player has no -quit of its own: it is stopped 3 seconds after "[TitleScreen] ready" appears, or after max-seconds.

set -uo pipefail
# shellcheck source=env.sh
. "$(dirname "${BASH_SOURCE[0]}")/env.sh"

KIND="${1:-dev}"; MAX="${2:-30}"; SETTLE=3
case "$KIND" in dev|demo|release) ;; *) die "kind must be dev, demo or release" ;; esac

APP="$REPO/builds/StandaloneOSX/$KIND/TheSynapticSea.app"
BIN="$APP/Contents/MacOS/$(ls "$APP/Contents/MacOS" 2>/dev/null | head -1)"   # the executable is named "The Synaptic Sea"
LOG="$LOGS/player-headless-$KIND.log"
[ -x "$BIN" ] || { echo "VERIFY FAIL missing $BIN (run tools/mac/build.sh $KIND)"; exit 1; }
rm -f "$LOG"

"$BIN" -batchmode -nographics -logFile "$LOG" >/dev/null 2>&1 &
PID=$!
elapsed=0
while kill -0 "$PID" 2>/dev/null && [ "$elapsed" -lt "$MAX" ]; do
    if [ -f "$LOG" ] && grep -q '\[TitleScreen\] ready' "$LOG"; then sleep "$SETTLE"; break; fi
    sleep 1; elapsed=$((elapsed + 1))
done
if kill -0 "$PID" 2>/dev/null; then kill "$PID" 2>/dev/null; sleep 1; kill -9 "$PID" 2>/dev/null; fi
wait "$PID" 2>/dev/null
sleep 1

[ -f "$LOG" ] || { echo "VERIFY FAIL player wrote no log ($LOG)"; exit 1; }

problems=0
composed="$(grep -E '\[AppServices\] composed .*build=[A-Za-z]+' "$LOG" | head -1)"
if [ -z "$composed" ]; then
    echo "  no \"[AppServices] composed\" line: the player did not finish booting"; problems=$((problems + 1))
else
    reported="$(echo "$composed" | sed -E 's/.*build=([A-Za-z]+).*/\1/')"
    if [ "$reported" != "$KIND" ]; then
        echo "  build kind mismatch: player reports build=$reported, expected $KIND"; problems=$((problems + 1))
    fi
fi

hits="$(grep -nE 'Exception|\bError\b|The referenced script .* is missing|NullReference|Failed to load' "$LOG" || true)"
if [ -n "$hits" ]; then
    echo "$hits" | head -20 | sed 's/^/  /'
    problems=$((problems + $(echo "$hits" | wc -l)))
fi

if [ "$problems" -gt 0 ]; then echo "VERIFY FAIL kind=$KIND problems=$problems log=$LOG"; exit 1; fi
echo "VERIFY PASS kind=$KIND build=$reported log_lines=$(wc -l < "$LOG" | tr -d ' ') log=$LOG"
