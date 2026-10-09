#!/bin/bash
# Proves the New Run smoke checks (tools/mac/smoke.sh --new-run) catch a broken New Run: it evaluates synthetic player logs with
# `smoke.sh --check-log` and requires the good log to pass and every broken variant to fail. No player or Unity editor is launched.
# Usage: tools/mac/smoke-selftest.sh

set -uo pipefail
# shellcheck source=env.sh
. "$(dirname "${BASH_SOURCE[0]}")/env.sh"

SMOKE="$TOOLS_MAC_DIR/smoke.sh"
DIR="$LOGS/smoke-selftest"; rm -rf "$DIR"; mkdir -p "$DIR"
SEED=4242
fails=0

good_log() {
    cat <<EOF
[AppServices] composed headless=True build=dev version=v0.0.0 store=direct
[TitleScreen] ready menu=main_menu continue=False
[SmokeNewRun] pressing New Run seed=$SEED
PlayableGeneratedShip: lifeboat docked to home
PLAYABLE SHIP READY player_spawned=true camera_spawned=true
[PlayableBootstrap] booted NewRun layout=user://runs/20261010T101010-s$SEED-abc123/layout.json seed=$SEED biome=breach_field difficulty=standard
EOF
}

# name | expected (pass|fail) | sed expression applied to the good log ("" = unchanged)
check() {
    local name="$1" expect="$2" edit="$3" file="$DIR/$1.log" out rc
    if [ -n "$edit" ]; then good_log | sed -E "$edit" > "$file"; else good_log > "$file"; fi
    out="$("$SMOKE" --check-log "$file" dev --new-run "$SEED" 2>&1)"; rc=$?
    if [ "$expect" = "pass" ] && [ "$rc" -eq 0 ]; then echo "ok   $name -> pass"
    elif [ "$expect" = "fail" ] && [ "$rc" -ne 0 ]; then echo "ok   $name -> fail ($(echo "$out" | grep -c .) line(s) of output)"
    else echo "BAD  $name expected $expect but exit=$rc"; echo "$out" | sed 's/^/       /'; fails=$((fails + 1)); fi
}

check good                       pass ""
check never-pressed              fail '/SmokeNewRun/d'
check no-boot-line               fail '/PlayableBootstrap\] booted/d'
check wrong-seed                 fail "s/seed=$SEED biome/seed=99 biome/"
check authored-hub-not-generated fail 's#user://runs/[^ ]*#res://data/procgen/golden/coherent_ship_001/layout.json#'
check lifeboat-not-docked        fail '/lifeboat docked to home/d'
check boot-dock-failed           fail 's/lifeboat docked to home/boot dock failed — reason=dock_incompatible/'
check playable-ship-failed       fail 's/PLAYABLE SHIP READY/PLAYABLE SHIP FAIL reason=x/'
check exception-in-log           fail '$a\
NullReferenceException: Object reference not set to an instance of an object'
check wrong-build-kind           fail 's/build=dev/build=release/'
check no-composed-line           fail '/AppServices/d'
check no-viable-home             fail '$a\
No viable home could be generated from seed 4242 (8 attempts). Choose Randomize seed and start again.'

# the default-mode (no --new-run) check must still pass on a log without any New Run lines
printf '%s\n' '[AppServices] composed headless=True build=dev version=v0.0.0 store=direct' '[TitleScreen] ready menu=main_menu continue=False' > "$DIR/title-only.log"
if "$SMOKE" --check-log "$DIR/title-only.log" dev >/dev/null 2>&1; then echo "ok   title-only (default mode) -> pass"
else echo "BAD  title-only (default mode) should pass"; fails=$((fails + 1)); fi

if [ "$fails" -gt 0 ]; then echo "SELFTEST FAIL problems=$fails"; exit 1; fi
echo "SELFTEST PASS"
