#!/bin/bash
# Builds a macOS (arm64) Synaptic Sea player through Builder.PerformBuild. macOS counterpart of tools/build.ps1.
# Usage: tools/mac/build.sh [dev|demo|release]   (default dev)
# Output: builds/StandaloneOSX/<kind>/TheSynapticSea.app, log: builds/logs/build-StandaloneOSX-<kind>.log

set -uo pipefail
# shellcheck source=env.sh
. "$(dirname "${BASH_SOURCE[0]}")/env.sh"

KIND="${1:-dev}"
case "$KIND" in dev|demo|release) ;; *) die "kind must be dev, demo or release" ;; esac

setup_unity
require_editor_closed
OUT="$REPO/builds/StandaloneOSX/$KIND"
LOG="$LOGS/build-StandaloneOSX-$KIND.log"

snapshot_generated
code=0
"$UNITY" -batchmode -projectPath "$PROJECT" -buildTarget StandaloneOSX \
    -executeMethod SynapticSea.EditorTools.Build.Builder.PerformBuild \
    -buildKind "$KIND" -outputPath "$OUT" -logFile "$LOG" || code=$?
restore_generated "build-$KIND"

grep -E '\[Builder\]|error CS' "$LOG" || true
if [ "$code" -ne 0 ]; then echo "BUILD FAIL exit=$code log=$LOG"; exit 1; fi
echo "BUILD PASS out=$OUT"
