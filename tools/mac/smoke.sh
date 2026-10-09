#!/bin/bash
# Smoke-launches the built macOS player headless and fails on any exception or error in its log, or on the wrong
# build kind. macOS counterpart of tools/verify-headless.ps1. Build first with tools/mac/build.sh.
#
# Usage: tools/mac/smoke.sh [dev|demo|release] [max-seconds] [--new-run [seed]]
#        tools/mac/smoke.sh --check-log FILE [dev|demo|release] [--new-run [seed]]
#   defaults: dev, 30 seconds (90 with --new-run), seed 4242.
#
# Default mode: the player has no -quit of its own, so it is stopped 3 seconds after "[TitleScreen] ready" appears, or after max-seconds.
#
# --new-run: also presses Title -> New Run (dev builds only; the player is started with -synaptic-smoke-new-run <seed>) and passes only
#   when the log shows the press, the real boot line for that seed with a generated home (a user://runs/ layout), the lifeboat docked to
#   the home, and none of the New Run failure messages. It exists so that a broken New Run is no longer invisible to the smoke test.
#
# --check-log FILE: evaluate an existing log with the same rules without launching the player (tools/mac/smoke-selftest.sh uses it
#   to show the checks fail on a broken log and pass on a good one).

set -uo pipefail
# shellcheck source=env.sh
. "$(dirname "${BASH_SOURCE[0]}")/env.sh"

KIND="dev"; MAX=""; SETTLE=3; NEW_RUN=0; SEED=4242; CHECK_LOG=""; NEW_RUN_SETTLE=5
POSITIONAL=0
while [ $# -gt 0 ]; do
    case "$1" in
        --new-run) NEW_RUN=1
            if [ $# -gt 1 ] && [[ "$2" =~ ^[0-9]+$ ]]; then SEED="$2"; shift; fi ;;
        --check-log) shift; [ $# -gt 0 ] || die "--check-log needs a file"; CHECK_LOG="$1" ;;
        dev|demo|release) KIND="$1" ;;
        -h|--help) sed -n 2,19p "$0"; exit 0 ;;
        *) if [[ "$1" =~ ^[0-9]+$ ]] && [ "$POSITIONAL" -eq 0 ]; then MAX="$1"; POSITIONAL=1
           else die "unknown argument: $1 (see --help)"; fi ;;
    esac
    shift
done
[ -n "$MAX" ] || { if [ "$NEW_RUN" -eq 1 ]; then MAX=90; else MAX=30; fi; }
if [ "$NEW_RUN" -eq 1 ] && [ "$KIND" != "dev" ]; then die "--new-run needs a dev build (the automation is inert in $KIND builds)"; fi

# Evaluates a log; prints each problem and sets PROBLEMS (the count) and REPORTED (the build kind the player reports).
evaluate_log() {
    local log="$1" composed hits failures booted
    PROBLEMS=0; REPORTED=""
    composed="$(grep -E '\[AppServices\] composed .*build=[A-Za-z]+' "$log" | head -1)"
    if [ -z "$composed" ]; then
        echo "  no \"[AppServices] composed\" line: the player did not finish booting"; PROBLEMS=$((PROBLEMS + 1))
    else
        REPORTED="$(echo "$composed" | sed -E 's/.*build=([A-Za-z]+).*/\1/')"
        if [ "$REPORTED" != "$KIND" ]; then
            echo "  build kind mismatch: player reports build=$REPORTED, expected $KIND"; PROBLEMS=$((PROBLEMS + 1))
        fi
    fi

    hits="$(grep -nE 'Exception|\bError\b|The referenced script .* is missing|NullReference|Failed to load' "$log" || true)"
    if [ -n "$hits" ]; then
        echo "$hits" | head -20 | sed 's/^/  /'
        PROBLEMS=$((PROBLEMS + $(echo "$hits" | wc -l)))
    fi

    if [ "$NEW_RUN" -eq 1 ]; then
        if ! grep -q "\[SmokeNewRun\] pressing New Run seed=$SEED" "$log"; then
            echo "  New Run was never pressed (no \"[SmokeNewRun] pressing New Run seed=$SEED\" line)"; PROBLEMS=$((PROBLEMS + 1))
        fi
        failures="$(grep -nE 'PLAYABLE SHIP FAIL|boot dock failed|gameplay boot failed|the run failed to start|No viable home|no viable ship|did not open' "$log" || true)"
        if [ -n "$failures" ]; then
            echo "$failures" | head -10 | sed 's/^/  New Run failure: /'
            PROBLEMS=$((PROBLEMS + $(echo "$failures" | wc -l)))
        fi
        booted="$(grep -E "\[PlayableBootstrap\] booted .* seed=$SEED( |\$)" "$log" | head -1)"
        if [ -z "$booted" ]; then
            echo "  no \"[PlayableBootstrap] booted ... seed=$SEED\" line: New Run did not reach a playable session"; PROBLEMS=$((PROBLEMS + 1))
        else
            BOOT_LINE="$booted"
            if ! echo "$booted" | grep -qE 'layout=[^ ]*runs/'; then
                echo "  the booted home is not a generated one (layout is not under runs/): $booted"; PROBLEMS=$((PROBLEMS + 1))
            fi
        fi
        if ! grep -q 'PlayableGeneratedShip: lifeboat docked to home' "$log"; then
            echo "  the lifeboat was not docked to the home (no \"lifeboat docked to home\" line)"; PROBLEMS=$((PROBLEMS + 1))
        fi
    fi
}

BOOT_LINE=""
if [ -n "$CHECK_LOG" ]; then
    [ -f "$CHECK_LOG" ] || { echo "VERIFY FAIL no such log: $CHECK_LOG"; exit 1; }
    evaluate_log "$CHECK_LOG"
    if [ "$PROBLEMS" -gt 0 ]; then echo "VERIFY FAIL kind=$KIND problems=$PROBLEMS log=$CHECK_LOG"; exit 1; fi
    NR_NOTE=""; [ "$NEW_RUN" -eq 1 ] && NR_NOTE=" new_run seed=$SEED"
    echo "VERIFY PASS kind=$KIND build=$REPORTED$NR_NOTE log_lines=$(wc -l < "$CHECK_LOG" | tr -d ' ') log=$CHECK_LOG"
    exit 0
fi

APP="$REPO/builds/StandaloneOSX/$KIND/TheSynapticSea.app"
BIN="$APP/Contents/MacOS/$(ls "$APP/Contents/MacOS" 2>/dev/null | head -1)"   # the executable is named "The Synaptic Sea"
SUFFIX=""; [ "$NEW_RUN" -eq 1 ] && SUFFIX="-new-run"
LOG="$LOGS/player-headless-$KIND$SUFFIX.log"
[ -x "$BIN" ] || { echo "VERIFY FAIL missing $BIN (run tools/mac/build.sh $KIND)"; exit 1; }
rm -f "$LOG"

ARGS=(-batchmode -nographics -logFile "$LOG")
[ "$NEW_RUN" -eq 1 ] && ARGS+=(-synaptic-smoke-new-run "$SEED")
"$BIN" "${ARGS[@]}" >/dev/null 2>&1 &
PID=$!
elapsed=0
while kill -0 "$PID" 2>/dev/null && [ "$elapsed" -lt "$MAX" ]; do
    if [ "$NEW_RUN" -eq 1 ]; then
        # New Run: done once the real boot line (or a failure line) appears, then let the session run a few seconds so late errors land in the log.
        if [ -f "$LOG" ] && grep -qE '\[PlayableBootstrap\] booted|PLAYABLE SHIP FAIL|gameplay boot failed|the run failed to start' "$LOG"; then sleep "$NEW_RUN_SETTLE"; break; fi
    else
        if [ -f "$LOG" ] && grep -q '\[TitleScreen\] ready' "$LOG"; then sleep "$SETTLE"; break; fi
    fi
    sleep 1; elapsed=$((elapsed + 1))
done
if kill -0 "$PID" 2>/dev/null; then kill "$PID" 2>/dev/null; sleep 1; kill -9 "$PID" 2>/dev/null; fi
wait "$PID" 2>/dev/null
sleep 1

[ -f "$LOG" ] || { echo "VERIFY FAIL player wrote no log ($LOG)"; exit 1; }

evaluate_log "$LOG"

# New Run writes the generated home under the game's data directory (user://runs/<id>/). Remove exactly the one this smoke run created, named in
# its own boot line, so the smoke test leaves no run directory behind in the real data directory. Anything else there is left alone.
if [ "$NEW_RUN" -eq 1 ]; then
    DATA_DIR="${SYNAPTIC_DATA_DIR:-$HOME/Library/Application Support/9th Level Software/The Synaptic Sea}"
    RUN_ID="$(grep -oE 'user://runs/[0-9]{8}T[0-9]{6}-s[0-9]+-[0-9a-f]+/' "$LOG" | head -1 | sed -E 's#user://runs/([^/]+)/#\1#')"
    if [ -n "$RUN_ID" ] && [ -d "$DATA_DIR/runs/$RUN_ID" ]; then rm -rf "$DATA_DIR/runs/$RUN_ID"; rmdir "$DATA_DIR/runs" 2>/dev/null || true; fi
fi

if [ "$PROBLEMS" -gt 0 ]; then echo "VERIFY FAIL kind=$KIND problems=$PROBLEMS log=$LOG"; exit 1; fi
if [ "$NEW_RUN" -eq 1 ]; then
    echo "VERIFY PASS kind=$KIND build=$REPORTED new_run seed=$SEED log_lines=$(wc -l < "$LOG" | tr -d ' ') log=$LOG"
    echo "  $BOOT_LINE"
else
    echo "VERIFY PASS kind=$KIND build=$REPORTED log_lines=$(wc -l < "$LOG" | tr -d ' ') log=$LOG"
fi
