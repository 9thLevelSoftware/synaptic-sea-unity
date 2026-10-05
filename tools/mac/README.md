# macOS test and build scripts

The Mac counterparts of `tools/test.ps1`, `tools/build.ps1` and `tools/verify-headless.ps1`. The PowerShell scripts still serve Windows.

| Script | What it does |
| --- | --- |
| `tools/mac/test.sh [dotnet\|editmode\|playmode\|all] [--filter X] [--playmode-target player\|editor] [--keep-going] [--skip-dotnet]` | Core suite under dotnet, then Unity EditMode and/or PlayMode. Exit 0 pass, 8 test failures, 1 infrastructure. |
| `tools/mac/build.sh [dev\|demo\|release]` | Builds `builds/StandaloneOSX/<kind>/TheSynapticSea.app` through `Builder.PerformBuild`. |
| `tools/mac/smoke.sh [kind] [max-seconds]` | Launches the built player headless, waits for `[TitleScreen] ready`, scans the log for errors. |

Results and logs go to `builds/logs/` (git-ignored).

## Prerequisites
- The external volume `Untitled` is mounted. It holds the Unity 6000.6.0f1 editor, the dotnet 8 SDK, the NuGet cache and git-lfs.
  `env.sh` finds them itself; override with `SYNAPTIC_UNITY`, `SYNAPTIC_DOTNET` or `SYNAPTIC_MIGRATION_ROOT`.
- The Unity Student license is valid until 2026-12-12. A successful editor launch confirms it; the Hub CLI reports licensing as unavailable, so don't rely on it.
- No other Unity editor has this project open.

## Typical durations
dotnet about 17 minutes (2,959 tests), Unity EditMode about 27 minutes (3,312 tests), Unity PlayMode as a player about 27 minutes (96 tests). The first PlayMode attempt can fail with "scripts are compiling"; `test.sh` retries once automatically.
`--playmode-target editor` has not been timed on this Mac yet.

## Generated files
Every Unity run rewrites a few tracked files (`ProjectSettings/*.asset`, the URP global settings, one VFX material). After each run the scripts
save that diff to `builds/logs/unity-<suite>-generated-changes.patch` and revert those files, but only if they had no local edits beforehand.

## Evidence directories
Some EditMode tests read directories that only exist outside the repo. `env.sh` sets `SYNAPTIC_NATIVE_UTF8_EVIDENCE_DIR` to the Mac evidence folder when it is
not already set. `SYNAPTIC_PAID_CHECKPOINT_DIAGNOSTIC_DIR` and `SYNAPTIC_CATALOG_EVIDENCE_DIR` are not set, so the tests that need them fail until Phase 0.3 removes them.
