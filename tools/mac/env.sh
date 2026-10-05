#!/bin/bash
# Shared environment for the macOS test/build scripts. Source it; do not run it.
# Resolves the Unity editor, the dotnet SDK and git-lfs, and provides helpers that keep Unity runs from
# leaving tracked files modified.

TOOLS_MAC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$TOOLS_MAC_DIR/../.." && pwd)"
PROJECT="$REPO/SynapticSea"
LOGS="$REPO/builds/logs"
mkdir -p "$LOGS"

MIGRATION_ROOT="${SYNAPTIC_MIGRATION_ROOT:-/Volumes/Untitled/SynapticSeaMigration}"
UNITY_VERSION="6000.6.0f1"

die() { echo "ERROR: $*" >&2; exit 1; }

resolve_unity() {
    if [ -n "${SYNAPTIC_UNITY:-}" ]; then
        [ -x "$SYNAPTIC_UNITY" ] || die "SYNAPTIC_UNITY is not executable: $SYNAPTIC_UNITY"
        echo "$SYNAPTIC_UNITY"; return
    fi
    local hub="$HOME/Library/Application Support/UnityHub/editors-v2.json" candidate
    if [ -f "$hub" ]; then
        candidate="$(python3 - "$hub" "$UNITY_VERSION" <<'PY'
import json, sys
data, want = json.load(open(sys.argv[1])), sys.argv[2]
for e in data.get("data", []):
    if e.get("version") == want and e.get("location"):
        print(e["location"][0] + "/Contents/MacOS/Unity"); break
PY
)"
        if [ -n "$candidate" ] && [ -x "$candidate" ]; then echo "$candidate"; return; fi
    fi
    for candidate in \
        "/Volumes/Untitled/Applications/Unity/Unity/Unity.app/Contents/MacOS/Unity" \
        "/Volumes/OS/Unity/$UNITY_VERSION/Editor/Unity.app/Contents/MacOS/Unity"; do
        if [ -x "$candidate" ]; then echo "$candidate"; return; fi
    done
    die "Unity $UNITY_VERSION not found. Mount the external volume ('Untitled'), or set SYNAPTIC_UNITY to the editor binary."
}

resolve_dotnet() {
    if [ -n "${SYNAPTIC_DOTNET:-}" ]; then echo "$SYNAPTIC_DOTNET"; return; fi
    if [ -x "$MIGRATION_ROOT/tools/dotnet-8.0.425/dotnet" ]; then echo "$MIGRATION_ROOT/tools/dotnet-8.0.425/dotnet"; return; fi
    command -v dotnet || die "dotnet SDK not found. Mount the external volume, or set SYNAPTIC_DOTNET."
}

# Called by scripts that need Unity; the dotnet-only path does not require the editor.
setup_unity() { UNITY="$(resolve_unity)"; }
setup_dotnet() {
    DOTNET="$(resolve_dotnet)"
    export DOTNET_CLI_TELEMETRY_OPTOUT=1
    if [ -z "${NUGET_PACKAGES:-}" ] && [ -d "$MIGRATION_ROOT/tools/nuget-packages" ]; then
        export NUGET_PACKAGES="$MIGRATION_ROOT/tools/nuget-packages"
    fi
}

LFS_DIR="$MIGRATION_ROOT/tools/git-lfs-3.8.0/git-lfs-3.8.0"
if [ -x "$LFS_DIR/git-lfs" ]; then PATH="$LFS_DIR:$PATH"; export PATH; fi

# Evidence directory read by PaidCraftNativeUtf8Tests; only default it when the caller has not set it.
if [ -z "${SYNAPTIC_NATIVE_UTF8_EVIDENCE_DIR:-}" ] && [ -d "$MIGRATION_ROOT/mac-baseline-evidence/native-utf8" ]; then
    export SYNAPTIC_NATIVE_UTF8_EVIDENCE_DIR="$MIGRATION_ROOT/mac-baseline-evidence/native-utf8"
fi

require_editor_closed() {
    if pgrep -f "projectPath $PROJECT" >/dev/null 2>&1 || pgrep -f "projectpath $PROJECT" >/dev/null 2>&1; then
        die "A Unity process already has $PROJECT open. Close it first."
    fi
}

# Tracked files that Unity rewrites on import/play (see mac-baseline-evidence/unity-*-generated-changes.patch).
GENERATED_FILES="
SynapticSea/ProjectSettings/EditorBuildSettings.asset
SynapticSea/ProjectSettings/ProjectSettings.asset
SynapticSea/ProjectSettings/UnityConnectSettings.asset
SynapticSea/Assets/Settings/UniversalRenderPipelineGlobalSettings.asset
SynapticSea/Assets/Content/VFX/Materials/VFX_beacon_blue_StandardMaterial3D_beacon_blue.mat
"
CLEAN_BEFORE=""

# Remember which generated files have no local modifications, so only those are restored afterwards.
snapshot_generated() {
    CLEAN_BEFORE=""
    local f
    for f in $GENERATED_FILES; do
        if git -C "$REPO" diff --quiet -- "$f" 2>/dev/null && git -C "$REPO" ls-files --error-unmatch "$f" >/dev/null 2>&1; then
            CLEAN_BEFORE="$CLEAN_BEFORE $f"
        fi
    done
}

# Save what Unity changed to $LOGS/unity-<label>-generated-changes.patch, then revert those files.
restore_generated() {
    local label="$1" patch="$LOGS/unity-$1-generated-changes.patch"
    [ -n "$CLEAN_BEFORE" ] || return 0
    # shellcheck disable=SC2086
    git -C "$REPO" diff -- $CLEAN_BEFORE > "$patch"
    if [ -s "$patch" ]; then
        # shellcheck disable=SC2086
        git -C "$REPO" checkout -- $CLEAN_BEFORE
        echo "Reverted Unity-generated changes (saved to $patch)"
    else
        rm -f "$patch"
    fi
}
