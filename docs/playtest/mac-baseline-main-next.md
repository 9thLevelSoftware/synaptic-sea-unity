# Mac baseline: `main-next` at `af38ef3`

Recorded 2026-10-05 with `tools/mac/test.sh`, `build.sh` and `smoke.sh` on Apple silicon, Unity 6000.6.0f1. This is the starting line for Phase 0; nothing was fixed to produce it. Raw results are in `builds/logs/` (git-ignored).

| Check | Result |
| --- | --- |
| Core dotnet suite | 2,959 passed, 0 failed (17 min). The summary reports 0 skipped although `PreservedMonoPaidCheckpointRequiresComposedCatalogsBeforeAdmission` printed as skipped. |
| Unity compile | Clean, no `error CS` lines. |
| Unity EditMode | 3,312 total: 3,286 passed, **1 failed**, 25 skipped (27 min). |
| Unity PlayMode (standalone arm64 player) | 96 total: 85 passed, **9 failed**, 2 skipped (27 min). |
| Dev build (`build.sh dev`) | Passed. 460 MB, Mono, 0 errors, 299 s. |
| Headless smoke (`smoke.sh dev`) | Passed. The player boots to `[TitleScreen] ready` with no errors in its log. |
| `git status` after the Unity runs | Clean. Unity's edits to tracked files were reverted and saved as `builds/logs/unity-*-generated-changes.patch`. |

## Failures, grouped

### (a) Expected long-route deaths (2). Phase 1.6 fixes these.
Both match `mac-baseline-evidence/survival-route-diagnosis.md` from Oct 4: the player dies of attrition before the route completes.
- `RunLifecyclePlayModeTests.JoinedHomeFlightRequiresEarnedPropulsionAndPreservesAssemblyAndShuttle`: "the player survived exploration", health 0.
- `RunLifecyclePlayModeTests.ReclaimWeldWalkSaveAndLeaveByIndependentCraftWithoutFixtureResources`: same assertion, health 0.

### (b) Diagnostic first-away and auxiliary tests (8). Phase 0.3 decides their fate.
- Five need `SYNAPTIC_AUXILIARY_CHECKPOINT_DIR`, a checkpoint folder that exists only in Codex's evidence set:
  `ContinueAuxiliaryCheckpointAndProbeFirstOrdinaryChannel`, `ContinueAuxiliaryEarnedReadyCheckpointThroughReviewedFirstAwayAndReturn`, `ContinueAuxiliaryPostSealCheckpointThroughEarnedDepartureAndReturn`, `DiagnoseAuxiliaryFirstAwayContractFromImmutableCheckpoint`, and `ContinueAuxiliaryReturnedCheckpointAndObserveThreeSecondsOfActualBoatAir` (this one fails on `Expected: False`).
- `CookPhysicallyEarnsAuxiliaryUtilitiesAndFiniteRacks`: "normal interact begins a timed work channel" fails.
- `MedicPhysicallyEarnsAuxiliaryUtilitiesAndFiniteRacks`: hits the 180 s timeout.
- EditMode: `FirstAwaySalvageIntegrationTests.EntireFiniteGeometryHasAuthenticContractContentAndUnchangedStockDamageOutcomes` hits the 180 s timeout. The same code passes in the dotnet suite, so this may be a slow-machine timeout rather than a defect.

The Cook and Medic tests matter to 0.3. It activates auxiliary services by default, and these are the tests that exercise their real player-facing path. Don't delete them with the checkpoint-directory tests.

### (c) New failures
None found. Every failure falls into (a) or (b).

## Infrastructure findings
- The test runner touches `ProjectSettings` as it starts, which triggers a recompile. The player build can start before that finishes and fail with `Error building Player because scripts are compiling`. `test.sh` retries once on that exact error; the retry succeeded here. A separate warm-up compile did not help.
- The player executable inside the `.app` is named `The Synaptic Sea`, with spaces. `smoke.sh` looks it up instead of assuming a name.
- The 17-minute dotnet suite makes `--skip-dotnet` worth using for Unity-only reruns.
- An empty `SynapticSea/Assets/Scripts/` tree (folders and `.meta` files only) appeared in the working tree during these runs. It is not committed.
