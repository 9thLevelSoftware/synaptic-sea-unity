# macOS test and build scripts

The Mac counterparts of `tools/test.ps1`, `tools/build.ps1` and `tools/verify-headless.ps1`. The PowerShell scripts still serve Windows.

| Script | What it does |
| --- | --- |
| `tools/mac/test.sh [dotnet\|editmode\|playmode\|all] [--filter X] [--playmode-target player\|editor] [--keep-going] [--skip-dotnet]` | Core suite under dotnet, then Unity EditMode and/or PlayMode. Exit 0 pass, 8 test failures, 1 infrastructure. |
| `tools/mac/build.sh [dev\|demo\|release]` | Builds `builds/StandaloneOSX/<kind>/TheSynapticSea.app` through `Builder.PerformBuild`. |
| `tools/mac/smoke.sh [kind] [max-seconds] [--new-run [seed]]` | Launches the built player headless, waits for `[TitleScreen] ready`, scans the log for errors. With `--new-run` (dev builds) it also presses Title -> New Run with the seed (default 4242) and passes only when the log shows the press, the real `[PlayableBootstrap] booted ... seed=` line for a generated home (a `user://runs/` layout), the lifeboat docked to the home, and no New Run failure message. `--check-log FILE` evaluates an existing log with the same rules. |
| `tools/mac/smoke-selftest.sh` | Proves the New Run checks catch a broken New Run: evaluates synthetic logs (one good, eleven broken) through `smoke.sh --check-log` and requires the good one to pass and every broken one to fail. No player or editor needed. |
| `tools/mac/playtest-prep.sh [--clear] [--collect]` | Playtest kit. Dry run by default: prints the build's git SHA and whether it equals `origin/main`, the save/log paths, and the launch command. `--clear` backs up (and verifies) then clears saves, settings and logs; `--collect` gathers the log, saves and build stamp into `builds/playtest/session-<time>/` and prints the boot seed and errors. Refuses while the game or a Unity editor is running. See `docs/playtest/playtest-1-guide.md`. |
| `tools/mac/playtest-prep-selftest.sh` | Exercises `playtest-prep.sh` against a fake app, data directory and log directory (nothing real is touched). |

Results and logs go to `builds/logs/` (git-ignored); playtest backups and collected sessions go to `builds/playtest/`.

## Verifying a change end to end
`tools/mac/test.sh dotnet`, then `tools/mac/test.sh editmode --skip-dotnet`; for anything that touches the New Run path also `tools/mac/build.sh dev`,
`tools/mac/smoke.sh dev` and `tools/mac/smoke.sh dev --new-run`. The `--new-run` smoke is test tooling: the player reads the dev-only command-line
argument `-synaptic-smoke-new-run <seed>` and presses New Run through the same setup panel the player uses; it is inert in demo and release builds and
without the argument, and it is not a game option.

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
Some EditMode tests read directories that only exist outside the repo. `SYNAPTIC_PAID_CHECKPOINT_DIAGNOSTIC_DIR` and `SYNAPTIC_CATALOG_EVIDENCE_DIR` are not set by `env.sh`, so the tests that need them fail until they are removed.
