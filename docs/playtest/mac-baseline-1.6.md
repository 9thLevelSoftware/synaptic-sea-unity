# Mac baseline after Phase 1.6 (long routes)

> **Correction (Phase 1.6b):** the "game navigation gap" explanation and the `ShipNavMesh.Rebuild()` union hypothesis in this document were wrong. The navigation roots did not change; the test's press at the already-claimed home bridge closed the connection door. See `docs/playtest/mac-baseline-1.6b.md`.

Branch `phase1/1.6-long-routes`, cut from `main` at `dc85fa6` (Phase 1.5 merged). macOS, Unity 6000.6.0f1, PlayMode on the **editor target** (the supported path; see the player-target note below).

| Suite | After 1.5 (`dc85fa6`) | After 1.6 |
| --- | --- | --- |
| dotnet | 1,876 passed, 0 failed, 1 skipped | 1,876 passed, 0 failed, 1 skipped |
| Unity EditMode | 2,196 passed, 0 failed, 25 skipped | 2,196 passed, 0 failed, 25 skipped |
| Unity PlayMode (editor target) | 83 of 85 passed, 1 failed, 1 skipped | 84 of 86 passed, **1 failed**, 1 skipped |
| `build.sh dev` | pass | pass |
| `smoke.sh dev` | pass | pass |

PlayMode grew by one test (the retreat route below).

## Long routes

| Test | Result |
| --- | --- |
| `ReclaimWeldWalkSaveAndLeaveByIndependentCraftWithoutFixtureResources` | passes (514 s) |
| `WalkRepairTravelFightRetreatToTheLifeboatForAirThenSearchAndReturnWithoutFixtureResources` (new) | passes (175 s) |
| `JoinedHomeFlightRequiresEarnedPropulsionAndPreservesAssemblyAndShuttle` | **fails**, left failing on purpose (not weakened, not ignored) |

## Where `JoinedHomeFlight…` fails and why

It now gets much further than before. Passed in this run: the first wreck, the repairs, the reclaim of the second wreck, the weld, Continue, the sealed engineering hatch, the engineering salvage (`[AssemblyFlight] engineering salvage marker=-2:0:0`) and the initial "insufficient propulsion capacity" denial at the joined home's bridge.

It fails in `GatherMissingPropulsionSalvage`, at the first `WalkTo(lifeboat bridge)` after the survivor has claimed the joined home's bridge: "a reachable closed door or standing interaction approach exists for (9.5, 0.175, 16.0)". Godot coordinates; the lifeboat console is at Unity (-9.5, 0.18, 16.0).

**Evidence: reachability from the survivor, logged at four moments (Unity coordinates)**

| Moment | Survivor at | lifeboat console | dock barrier (-4, 0, 16) | wreck console (42, 0, 24) |
| --- | --- | --- | --- | --- |
| after the weld and Continue, before the engineering salvage | (2.71, 0.14, 0.00) on the wreck | PathComplete | PathComplete | PathComplete |
| before walking to the home bridge | (20.00, 0.14, 31.89) on the wreck | PathComplete | PathComplete | PathComplete |
| at the home bridge, before claiming it | (0.14, 0.14, 0.96) | PathComplete | PathComplete | PathComplete |
| **8 physics steps after claiming the home bridge, same spot** | (0.14, 0.14, 0.96) | **PathPartial, ends (-5.48, 0.13, 5.48)** | **PathPartial, ends (-4.00, 0.13, 5.48)** | **PathPartial, ends (1.52, 0.13, 5.48)** |

After the claim, the only walkable area left is the home's deck-0 strip (airlock, corridor, ramp): x from about -17.5 to 1.5, z up to 5.48. The wreck-side connection door becomes PathPartial too (its obstacle is closed, as expected). The probe is in the failure message (`reach=`) and in the `[Reach]` log lines.

Other facts from the same run:
- `current=ship_start piloted=ship_start home=ship_start boat=lifeboat`, dock barrier `opened=True`.
- `navmeshes=2` at every moment.
- Forcing a navigation rebuild (`ShipNavMesh.StructureCollisionChanged` on every surface, then 12 fixed updates) did **not** bring the route back ("route … is still missing after a navigation rebuild"). So the surface is not merely stale.
- Health lost by source over the run: atmosphere 136.7, combat 33.0, encumbrance 5.3, fire 22.2, wounds 8.5. The survivor is alive at the failure; this is not a survival problem.

**What I believe the cause is: a game navigation gap, not the test walker.** Claiming the home's bridge terminal (which makes the home the piloted ship) cuts the walkable connection between the home's deck 0 and the docked lifeboat and the joined wreck. A player who does this in the real game cannot walk back to the lifeboat.

**Hypothesis, not tested** (no fix was applied in this PR):
- The shared walkable surface is built in `ShipNavMesh.Rebuild()` (`Runtime/Session/ShipNavMesh.cs`, around line 184) from `_assemblyRoots ?? [_dockedRoot]`.
- `ShipNavMesh.BuildAssembly` (called from `HomeAssemblyGeometry.cs:51`) sets `_assemblyRoots` to the home and its joined members and suppresses each member's own surface. `BuildComposite` (called from `DockedShipGeometry.cs:97`) does the same for the docked lifeboat through `_dockedRoot`.
- If claiming the bridge triggers an assembly rebuild whose roots do not include the lifeboat, the `??` drops `_dockedRoot`. The lifeboat's own surface is already suppressed, so it ends up with no connected walkable surface.
- The obvious fix is to use the union of `_assemblyRoots` and `_dockedRoot` in `Rebuild()`. It is a navigation-code change, not a level change. It has not been tried because the owner has not decided; confirm the hypothesis first (log which roots each rebuild uses) before changing it.

## Test-walker problems found and fixed on the way

These were test-route problems and are fixed in `RunLifecyclePlayModeTests.cs`:
- **Hatch face search** required an existing route, so a closed door two units before the hatch made every face look unreachable. It now picks the face on the survivor's side of any closed door (`NearestHatchFaceBehindClosedDoors`) and lets `WalkTo` open the door.
- **Combat steering stall.** The walker stopped following its route to "fight" an enemy it could not reach (behind a door jamb) and stood still until the 25 s stall limit. It now disengages from an enemy it has not hurt for 6 s, and re-plans a path when it makes no progress for 1.5 s (up to four times). The stall was intermittent: it hit on most runs and not on others.
- **Death reporting.** The work-channel loop exited on death and then reported "work completed without teleporting". A death is now reported as a death, with a survival report (health, oxygen, sanity, radiation, ship, position, health lost by source). Failure messages for stalls and missing routes also carry the planned route, nearby threats, a position trail and a reachability probe.

## Game fixes found by the routes

- **Sanity shelter at any wreck.** A commissioned, powered lifeboat that the survivor is aboard (no fire) is now a sanity shelter at every wreck. Before, only wrecks using the constrained-expedition layout qualified, so at the first wreck a survivor who retreated to their own lifeboat recovered air but not sanity (sanity stayed at 14 after 45 s). Found by the new retreat-route test.
- **A bypassed hatch yields to a reachable objective.** Hatches are seeded on room positions, so one can sit exactly on a derelict objective (here the engineering objective at Godot (-20, 0.12, 32)). Once bypassed, `hatch_reseal` claimed every interact press ahead of `derelict_objective` and the objective could not be completed. A bypassed hatch now does not claim the press while an incomplete objective is in reach and sight. Reseal for fire containment works as before elsewhere. Documented in `docs/InteractionOrder.md`.

## Player-target PlayMode (`tools/mac/test.sh playmode` default)

**Unresolved.** Evidence:
- Two quick tests (`NonSliceNewRunFailsClosedAndReturnsToTitle`, `DirectOpenBootsTheMilestoneAHub`) passed in the default player target: build, launch and result file all fine (total 2, passed 2, exit 0), both on already-compiled scripts and right after a script change. So the recompile-race hypothesis from 0.3 ("Reloading assemblies after forced synchronous recompile") did **not** reproduce.
- A full-suite player-target run produced no result file again (the log is the 18-byte `== Unity playmode` header and nothing else, and no process was left running). That matches the original symptom on long runs.
- So the failure is tied to long runs, not to a pending recompile. The cause is not known. No change was made to `test.sh`.
- **Supported path: `--playmode-target editor`.** All numbers in this document use it. The two long-route failure messages could not be compared against the player target.

## Not run

Nothing in the player target beyond the above. The build size was not re-measured.
