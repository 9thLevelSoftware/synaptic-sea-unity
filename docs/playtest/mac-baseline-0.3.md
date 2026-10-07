# Mac baseline: `main-next` after Phase 0.3 (through 0.3f)

Recorded 2026-10-06 with `tools/mac/test.sh`, `build.sh` and `smoke.sh` on Apple silicon, Unity 6000.6.0f1, at commit `a01f339`. Compare with `mac-baseline-main-next.md` (the `af38ef3` starting line). Nothing is pushed.

| Check | `af38ef3` | After 0.3f |
| --- | --- | --- |
| Core dotnet suite | 2,959 passed, 0 failed | 1,936 passed, 0 failed, 1 skipped (2.5 min) |
| Unity EditMode | 3,312 total: 3,286 passed, 1 failed, 25 skipped | 2,283 total: 2,258 passed, **0 failed**, 25 skipped |
| Unity PlayMode | 96 total: 85 passed, 9 failed, 2 skipped (player) | 88 total: 84 passed, **2 failed**, 2 skipped (editor target, see below) |
| Dev build | 460 MB, 0 errors | 456 MB, 0 errors, data manifest valid (158 files) |
| Headless smoke | Passed | Passed |

Test counts fell because the paid-crafting, auxiliary-service and reviewed-first-away tests were deleted along with the code they covered (about 950 EditMode cases in 0.3f alone).

## Remaining PlayMode failures (2)
`RunLifecyclePlayModeTests.JoinedHomeFlightRequiresEarnedPropulsionAndPreservesAssemblyAndShuttle` and `ReclaimWeldWalkSaveAndLeaveByIndependentCraftWithoutFixtureResources`. These are the two long-route deaths Phase 1.6 fixes. Their failure messages differ from the baseline ("player movement stalled at ..." and "work completed without teleporting, granting resources or boosting skills", versus "the player survived exploration", health 0). The run used the editor target, not the standalone player, so a difference in physics or timing is possible; rerun in the player once the player path works (see below) before reading anything into the new wording.

The Cook and Medic finite-kit tests (`CookWalksFiniteKitRetainedStudyAndLockpick`, `MedicWalksFiniteKitRetainedStudyAndLockpick`) and `WalkToDiagnosticFiniteKitCandidateWithoutPositioningPlayer` pass. They now run on the ordinary (non-generation) save path.

## Infrastructure findings
- `test.sh playmode` in the default standalone-player mode produced no result XML twice in a row on this commit. The player build succeeds, then Unity recompiles scripts ("Reloading assemblies after forced synchronous recompile") and the editor loses the player connection. `test.sh` only retries on `Error building Player because scripts are compiling`, which did not appear. `--playmode-target editor` worked and is what the numbers above use.
- `git-lfs` is not on `PATH` by default. Source `tools/mac/env.sh` before git commands, or the post-checkout hook fails and breaks `&&` chains.
- The empty `SynapticSea/Assets/Scripts/` tree, `Assets/InitTestScene*.unity` and `Assets/_Project/Tests/builds.meta` still appear untracked after Unity runs. They are not committed.

## Verification of the plan's acceptance items
- Ordinary saves: `SessionSaveParityTests` and the `SaveMigrationService` tests load the `fixtures/godot/save*` fixtures through `SaveLoadService` and pass in both suites.
- D9: `MilestoneALaunchContractTests` passes (9 of 9 cells for seeds 42 and 777).
- Study: `ManualStudySessionTests` covers finding a book in an ordinary container and studying it; the Cook and Medic PlayMode tests study through the inventory UI and see the XP.

## Known gaps found in 0.3f
- **Component-mode Continue does not restore finite loot.** `FiniteLootContinueTests.PartialKitSurvivesCompleteGenerationFreshContinueAndCannotRefillAfterDepletion` is ignored with this reason: after a component-integration Continue the finite kit's remaining stock is back to full. Only the removed paid restore path restored it. Revisit when component integration is activated (Phase 5.5).
- **Finite loot on the ordinary save path needed a fix.** Godot-style JSON parsing turns integers into doubles, and `FiniteLootState.Validate` requires exact `long`s, so any save with finite stock failed to load. `FiniteLootState.Normalize` now restores integral doubles before the strict validation (`RunSnapshot.FromDict`, `ShipInstance.ApplySummary`). `FiniteLootContinueTests.PartialKitSurvivesOrdinaryContinueAndCannotRefillAfterDepletion` covers it.
- **Saves with component integration and paid crafting both on** (only tests could create them) now fail to load as `corrupt_generation` and drop out of the slot list.
