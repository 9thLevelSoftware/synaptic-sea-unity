#!/bin/bash
# Runs the Synaptic Sea test suites on macOS: the engine-free Core suite under dotnet, then Unity EditMode and/or PlayMode.
# macOS counterpart of tools/test.ps1 (same exit codes).
#
# Usage: tools/mac/test.sh [dotnet|editmode|playmode|all] [--filter NAME] [--playmode-target player|editor] [--keep-going] [--skip-dotnet]
#   dotnet    Core + EditMode sources under dotnet test (no Unity).
#   editmode  dotnet, then Unity EditMode (default).
#   playmode  dotnet, then Unity PlayMode.
#   all       dotnet, EditMode, PlayMode.
#   --playmode-target player  PlayMode in a built standalone arm64 player (default; the path proven on this Mac).
#   --playmode-target editor  PlayMode inside the editor (faster; not yet proven on this Mac).
#   --keep-going              run every suite even after a failure (used for baselines).
#   --skip-dotnet             skip the dotnet suite (it takes about 17 minutes).
#
# Exit codes: 0 all passed; 8 tests ran and some failed; 1 infrastructure problem (build error, no result XML, ...).
# The Unity project must not be open in another editor. Unity-generated edits to tracked files are reverted afterwards
# and saved under builds/logs/unity-<suite>-generated-changes.patch.

set -uo pipefail
# shellcheck source=env.sh
. "$(dirname "${BASH_SOURCE[0]}")/env.sh"

MODE="editmode"; FILTER=""; PM_TARGET="player"; KEEP_GOING=0; SKIP_DOTNET=0
while [ $# -gt 0 ]; do
    case "$1" in
        dotnet|editmode|playmode|all) MODE="$1" ;;
        --filter) shift; FILTER="${1:-}"; [ -n "$FILTER" ] || die "--filter needs a value" ;;
        --playmode-target) shift; PM_TARGET="${1:-}"; case "$PM_TARGET" in player|editor) ;; *) die "--playmode-target must be player or editor" ;; esac ;;
        --keep-going) KEEP_GOING=1 ;;
        --skip-dotnet) SKIP_DOTNET=1 ;;
        -h|--help) sed -n '2,18p' "$0"; exit 0 ;;
        *) die "unknown argument: $1 (see --help)" ;;
    esac
    shift
done

WORST=0
note_status() { # keep the most severe status: infrastructure (1) beats test failure (8) beats pass (0)
    case "$1" in 1) WORST=1 ;; 8) [ "$WORST" -eq 1 ] || WORST=8 ;; esac
}
finish_suite() { # $1 = suite status; stops unless --keep-going
    note_status "$1"
    if [ "$1" -ne 0 ] && [ "$KEEP_GOING" -eq 0 ]; then
        [ "$1" -eq 8 ] && echo "TESTS FAIL suite=$2" || echo "TESTS INFRA FAIL suite=$2"
        exit "$1"
    fi
}

summarize() { # $1 = NUnit XML, $2 = label; exit 0 no failures, 8 failures, 1 unreadable
    python3 - "$1" "$2" <<'PY'
import sys, xml.etree.ElementTree as ET
path, label = sys.argv[1], sys.argv[2]
try:
    run = ET.parse(path).getroot()
    failed = int(run.get("failed", "0"))
except Exception as e:
    print(f"{label}: cannot read {path}: {e}"); sys.exit(1)
print("{}: total={} passed={} failed={} skipped={} duration={:.0f}s".format(
    label, run.get("total"), run.get("passed"), failed, run.get("skipped"), float(run.get("duration", "0"))))
for tc in run.iter("test-case"):
    if tc.get("result") == "Failed":
        msg = (tc.findtext("failure/message") or "").strip().splitlines()
        print("  FAIL {}: {}".format(tc.get("fullname"), msg[0] if msg else ""))
sys.exit(8 if failed else 0)
PY
}

run_dotnet() {
    echo "== dotnet (Core + EditMode sources)"
    setup_dotnet
    local args=(test "$REPO/tools/dotnet/SynapticSea.Core.Tests" --nologo
        --logger "console;verbosity=minimal" --logger "trx;LogFileName=dotnet.trx" --results-directory "$LOGS")
    [ -n "$FILTER" ] && args+=(--filter "FullyQualifiedName~$FILTER")
    local log="$LOGS/dotnet-test.log" code
    "$DOTNET" "${args[@]}" 2>&1 | tee "$log"
    code=${PIPESTATUS[0]}
    if [ "$code" -eq 0 ]; then return 0; fi
    # dotnet test exits 1 for failed tests and for build errors alike; the summary line tells them apart.
    if grep -Eq '^[[:space:]]*Failed![[:space:]]+-[[:space:]]+Failed:[[:space:]]+[1-9]' "$log"; then return 8; fi
    echo "dotnet infrastructure failure (exit $code); see $log"
    return 1
}

run_unity_suite() { # $1 = label, rest = extra Unity arguments
    local label="$1"; shift
    local xml="$LOGS/unity-$label.xml" log="$LOGS/unity-$label.log" code=0 sum=0
    echo "== Unity $label"
    setup_unity
    require_editor_closed
    rm -f "$xml"
    snapshot_generated
    # The test runner touches ProjectSettings as it starts, which makes Unity recompile. A player build that starts during
    # that recompile fails with "Error building Player because scripts are compiling"; the failed attempt finishes the
    # compile, so retry once when that exact error is in the log.
    local attempt=1
    while :; do
        code=0; rm -f "$xml"
        "$UNITY" -batchmode -projectPath "$PROJECT" -runTests "$@" -testResults "$xml" -logFile "$log" || code=$?
        if [ ! -f "$xml" ] && [ "$attempt" -eq 1 ] && grep -q "Error building Player because scripts are compiling" "$log"; then
            echo "   player build raced a recompile; retrying once"; attempt=2; continue
        fi
        break
    done
    restore_generated "$label"
    if [ ! -f "$xml" ]; then echo "no result XML at $xml (Unity exit $code); see $log"; return 1; fi
    summarize "$xml" "Unity $label" || sum=$?
    [ "$sum" -ne 0 ] && return "$sum"
    if [ "$code" -ne 0 ]; then echo "Unity exited $code although the XML reports no failures; see $log"; return 1; fi
    return 0
}

run_editmode() {
    local extra=(-testPlatform EditMode)
    [ -n "$FILTER" ] && extra+=(-testFilter "$FILTER")
    run_unity_suite editmode "${extra[@]}"
}

run_playmode() {
    local extra
    if [ "$PM_TARGET" = "player" ]; then
        extra=(-testPlatform StandaloneOSX -assemblyNames SynapticSea.Tests.PlayMode
            -testSettingsFile "$TOOLS_MAC_DIR/player-test-settings.json"
            -buildPlayerPath "$REPO/builds/test-player/TheSynapticSeaTests.app")
        mkdir -p "$REPO/builds/test-player"
    else
        extra=(-testPlatform PlayMode -assemblyNames SynapticSea.Tests.PlayMode)
    fi
    [ -n "$FILTER" ] && extra+=(-testFilter "$FILTER")
    run_unity_suite playmode "${extra[@]}"
}

suites="dotnet"
case "$MODE" in
    editmode) suites="dotnet editmode" ;;
    playmode) suites="dotnet playmode" ;;
    all) suites="dotnet editmode playmode" ;;
esac
[ "$SKIP_DOTNET" -eq 1 ] && suites="${suites#dotnet}" && suites="${suites# }"

ran=""
for s in $suites; do
    status=0
    case "$s" in
        dotnet) run_dotnet || status=$? ;;
        editmode) run_editmode || status=$? ;;
        playmode) run_playmode || status=$? ;;
    esac
    ran="$ran,$s"
    finish_suite "$status" "$s"
done

if [ "$WORST" -eq 0 ]; then echo "TESTS PASS suites=${ran#,}"
elif [ "$WORST" -eq 8 ]; then echo "TESTS FAIL (some suites failed) suites=${ran#,}"
else echo "TESTS INFRA FAIL (some suites could not run) suites=${ran#,}"; fi
exit "$WORST"
