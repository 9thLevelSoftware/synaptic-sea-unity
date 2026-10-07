# Mac baseline after Phase 0.4 (dead code removal)

`main-next` at `9a514f1`. Commits `0d5cd51` (work kernel), `219a26a` (Infra ledgers), `d9b7f4f` (graph-based procgen), `7865ebe` (Rust worldgen seam and fixtures), `9a514f1` (headers, dead members, docs, inventory). See `docs/design/decisions.md`, "Phase 0.4 outcome".

| Suite | After 0.3g (`619e041`) | After 0.4 |
| --- | --- | --- |
| dotnet | 1,936 passed, 0 failed, 1 skipped | 1,785 passed, 0 failed, 1 skipped |
| Unity EditMode | 2,258 passed, 0 failed, 25 skipped (2,283 total) | 2,103 passed, 0 failed, 25 skipped (2,128 total) |
| Unity PlayMode (editor target) | 84 of 88 passed, 2 failed, 2 skipped | 76 of 80 passed, 2 failed, 2 skipped |
| `build.sh dev` | pass | pass (450.3 MB, was larger before the 5.5 MB fixture dump left `StreamingAssets`) |
| `smoke.sh dev` | pass | pass |
| `build_system_inventory.py --check` | n/a | pass, 183 systems |

Per-step results (dotnet / EditMode passed, 0 failed each): a 1,820 / 2,142; b 1,814 / 2,136; c 1,809 / 2,131; d 1,785 / 2,103; e 1,785 / 2,103.

## PlayMode failures

The same two long-route tests fail as before (Phase 1.6 fixes them): `JoinedHomeFlightRequiresEarnedPropulsionAndPreservesAssemblyAndShuttle` ("survived ordinary local shelter recovery") and `ReclaimWeldWalkSaveAndLeaveByIndependentCraftWithoutFixtureResources` ("work completed without teleporting, granting resources or boosting skills"). The Joined-home message differs from both the Oct-5 baseline ("health 0") and the 0.3f run ("player movement stalled"), so the message is not stable between runs; the run uses the editor target, not the standalone player. The 8 fewer PlayMode tests are the deleted `FrozenProceduralPhysicalPlayModeTests` cases.

## Lines removed

About 1,350 (work kernel) + 1,040 (Infra) + 1,385 (procgen) lines of C# and tests, plus about 258,000 lines of JSON in the worldgen fixture dump. Net 0.4: well short of the master plan's 20k-line estimate for code, because most of the listed code was already gone or live.

## Notes

- `tools/mac/test.sh playmode` in the default standalone-player mode is still broken (no result XML); numbers above use `--playmode-target editor`.
- The Oct-2 formal-build validator now fails by design and is retired (see its README).
- `SynapticSea/Assets/_Project/Tests/builds.meta` reappears untracked after Unity runs; it is not ours to commit.
