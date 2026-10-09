#!/bin/bash
# Exercises tools/mac/playtest-prep.sh against a fake app, data directory and log directory (no real saves are touched).
# Usage: tools/mac/playtest-prep-selftest.sh
set -uo pipefail
# shellcheck source=env.sh
. "$(dirname "${BASH_SOURCE[0]}")/env.sh"
PREP="$TOOLS_MAC_DIR/playtest-prep.sh"
ROOT="$LOGS/playtest-prep-selftest"; rm -rf "$ROOT"
APP="$ROOT/app/TheSynapticSea.app"; DATA="$ROOT/data dir/The Synaptic Sea"; LOGD="$ROOT/log dir/The Synaptic Sea"; OUT="$ROOT/out"
STAMPDIR="$APP/Contents/Resources/Data/StreamingAssets"
mkdir -p "$STAMPDIR" "$DATA/saves" "$DATA/runs/r1" "$DATA/Unity" "$LOGD"
printf '{"build_kind":"dev","git_sha":"abc1234","built_utc":"2026-10-09T00:00:00Z","version":"0.1.0"}\n' > "$STAMPDIR/build_stamp.json"
echo '{"slot":1}' > "$DATA/saves/slot_01.json"; echo '{}' > "$DATA/settings.json"; echo '{}' > "$DATA/runs/r1/layout.json"
echo keep > "$DATA/Unity/analytics.json"; echo keep > "$DATA/TestResults.xml"
cat > "$LOGD/Player.log" <<LOG
[AppServices] composed headless=False build=dev version=0.1.0 store=direct
[TitleScreen] ready menu=main_menu continue=False
[PlayableBootstrap] booted NewRun layout=user://runs/x/layout.json seed=777 biome=breach_field difficulty=standard
LOG
echo old > "$LOGD/Player-prev.log"
fails=0
run() { "$PREP" --app "$APP" --data-dir "$DATA" --log-dir "$LOGD" --out-dir "$OUT" --skip-running-check "$@"; }
expect() { local name="$1" want="$2" got="$3"; if [ "$want" = "$got" ]; then echo "ok   $name"; else echo "BAD  $name (wanted $want, got $got)"; fails=$((fails + 1)); fi; }

run --expect-sha abc1234 >/dev/null 2>&1; expect "dry run succeeds" 0 $?
[ -f "$DATA/saves/slot_01.json" ] && [ -f "$LOGD/Player.log" ]; expect "dry run changes nothing" 0 $?
run --expect-sha ffff999 >/dev/null 2>&1; expect "sha mismatch stops" 1 $?
run --expect-sha ffff999 --allow-sha-mismatch >/dev/null 2>&1; expect "sha mismatch allowed with the flag" 0 $?
run --expect-sha abc1234 --clear --collect >/dev/null 2>&1; expect "--clear with --collect is refused" 1 $?

run --expect-sha abc1234 --clear >/dev/null 2>&1; expect "--clear succeeds" 0 $?
[ ! -e "$DATA/saves" ] && [ ! -e "$DATA/settings.json" ] && [ ! -e "$DATA/runs" ] && [ ! -f "$LOGD/Player.log" ] && [ ! -f "$LOGD/Player-prev.log" ]; expect "game data and logs cleared" 0 $?
[ -f "$DATA/Unity/analytics.json" ] && [ -f "$DATA/TestResults.xml" ]; expect "Unity/ and TestResults.xml untouched" 0 $?
B="$(ls -d "$OUT"/backup-* 2>/dev/null | head -1)"
[ -f "$B/data/saves/slot_01.json" ] && [ -f "$B/data/settings.json" ] && [ -f "$B/data/runs/r1/layout.json" ] && [ -f "$B/logs/Player.log" ]; expect "backup holds the cleared files" 0 $?

# a second session: play, then collect
mkdir -p "$DATA/saves"; echo '{"slot":2}' > "$DATA/saves/slot_02.json"
cat > "$LOGD/Player.log" <<LOG
[AppServices] composed headless=False build=dev version=0.1.0 store=direct
[PlayableBootstrap] booted NewRun layout=user://runs/y/layout.json seed=4711 biome=breach_field difficulty=standard
NullReferenceException: Object reference not set to an instance of an object
LOG
sleep 1
out="$(run --expect-sha abc1234 --collect 2>&1)"; expect "--collect succeeds" 0 $?
S="$(ls -d "$OUT"/session-* 2>/dev/null | head -1)"
[ -f "$S/Player.log" ] && [ -f "$S/build_stamp.json" ] && [ -f "$S/data/saves/slot_02.json" ] && [ -f "$S/collect-summary.txt" ]; expect "session folder has log, stamp, saves, summary" 0 $?
echo "$out" | grep -q "seed=4711"; expect "summary prints the boot seed" 0 $?
echo "$out" | grep -q "NullReferenceException"; expect "summary prints the exception" 0 $?
echo "$out" | grep -q "VERIFY FAIL"; expect "summary reports the smoke check failing on the exception" 0 $?
[ ! -e "$S/data/Unity" ]; expect "Unity/ not copied" 0 $?

if [ "$fails" -gt 0 ]; then echo "SELFTEST FAIL problems=$fails"; exit 1; fi
echo "SELFTEST PASS"
