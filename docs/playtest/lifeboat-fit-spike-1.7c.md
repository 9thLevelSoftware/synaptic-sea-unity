# Phase 1.7c spike: can generated homes fit the always-attached lifeboat?

Question from the 1.7b sweep (`home-generation-sweep.md`): only 7% of generated homes let the lifeboat overlap "no worse than the golden hub", so seed retries cannot fix it (8 attempts at 7% is about 44%). Is there a cheap, local change that gets the pass rate to 95% or better?

**Verdict: FIXABLE CHEAPLY, on geometry.** Docking the boat on the outward edge of the home's own dock/airlock room, instead of at the room's floor centroid facing +X, raises the strict pass rate (zero cells of other rooms overlapped) from **0% to 98.5%** per attempt. With the existing 8 seeded retries that is **200 of 200 seeds** producing a home whose boat fits. The remaining 1.5% (3 of 200 seeds, all `ring` templates whose dock room has no outside edge) re-roll.

Confidence is high for the geometry and medium for the game. The geometry uses the game's own docking transform. Nothing here was run in a rendered scene, and 93% of fitted homes rotate the boat away from today's +X orientation. See "Verified versus inferred".

## Setup

- 200 seeds (1 to 200), `breach_field` / `standard`, Medium, **Pristine** layout condition (the layout condition the 1.7b report recommends), `BuildHomeStart` first attempt.
- Test: `Tests/EditMode/Procgen/LifeboatFitSpikeTests.cs` (measurement; set `SYNAPTICSEA_SPIKE_REPORT` to write the tables) and `HomeDockPlannerTests.cs` (the candidate, asserted).
- The boat's three cells are taken from the real lifeboat layout (`LifeBoatBuilder.BuildLayout`, port from `DockPorts.ForLifeboat`) through `DockingManager.ComputeMobileTransform`, exactly as `RunSession.DockPilotedTo` does. Overlap counts distinct home cells whose 4 m square a boat cell overlaps by more than 0.5 m on both axes and that belong to a room other than the dock/airlock room.

## The 1.7b model was right

| Measure | Result |
| --- | --- |
| Analytic strip model and real-transform overlap counts agree | 200 / 200 (100%) |
| Golden hub through the real transform | other-room overlap 1, dock-room overlap 4 |
| Golden boat cell centres (home coordinates) | (0,2), (4,2), (8,2); dock port (2, 0, 2) facing +X |

So the 7% figure was not a modelling artefact. The current port is the room's floor centroid with the boat laid along +X, which overlaps neighbouring rooms in almost every hull.

## Candidates

Per-attempt pass rate over 200 homes; P(8) is the chance that at least one of 8 independent seeded attempts passes.

| Candidate | Strict (0 other-room cells) | P(8) strict | No worse than golden (1 cell) | P(8) golden-calibrated |
| --- | --- | --- | --- | --- |
| Current port (room centroid, +X), reject by fit | 0 (0.0%) | 0.0% | 14 (7.0%) | 44.0% |
| **A. Dock room's exterior edge, best direction** | **197 (98.5%)** | **100.0%** | **197 (98.5%)** | **100.0%** |
| D. Best exterior edge of any room (upper bound) | 200 (100.0%) | 100.0% | - | - |
| C. Template whitelist (`spine`, `bifurcated`, `stacked`), current port | 0 strict, 14 golden-calibrated of 44 whitelisted homes | - | - | gate passes 7.0% of seeds |

Why the other candidates lose:

- **Rejecting by fit alone (candidate 2) cannot work** with the current port: the strict pass rate is 0%, and the golden-calibrated rate is 7%. The geometry has to change, not only the gate.
- **A template whitelist (candidate 3)** keeps only 22% of seeds, and the current port still fails strict in all of them.
- **Any-room dock (candidate 4/D)** is the upper bound (100%) but moves the dock room away from the start/boarding cell, which is a much larger change. Candidate A already reaches 98.5% without it.

## Candidate A in detail

- The port is at the outward edge of one of the dock (else airlock) room's own exterior-facing cells, facing outward. The boat's engine bay then coincides with that cell, and its airlock and cockpit sit outside the hull: for all 197 fitted homes exactly 2 of the 3 boat cells overlap no home cell.
- Direction chosen (ties broken +X, -X, +Z, -Z, then lowest cell): `-X` 175, `+X` 14, `+Z` 6, `-Z` 2. So 89% of fitted homes dock on the west side with the boat rotated 180 degrees, and 4% rotate 90 degrees.
- Re-rolled seeds (dock room has no outside edge): 15, 80, 122, all `ring` templates.
- Result by template: every template is 100% except `ring` (13 of 16).
- Deterministic: the same seed gives byte-identical layout JSON (asserted for seeds 1 to 8, and the first seed twice in the spike).

### Design as implemented (behind default-preserving seams)

1. `Core/Procgen/HomeDockPlanner.cs` (new, about 190 lines, pure): `TryPlan` picks the best exterior-edge port for the dock/airlock room using the real docking transform; `Apply` stamps a `docking_port` contract (version 1, position, facing, source) on `Layout`, `SourceLayout` and `LayoutJson`, or returns a rejection reason.
2. `DockPorts.ForDerelict` (3-line change): a layout that carries a `docking_port` contract uses it, whatever its generation profile. No layout in the shipped data or fixtures carries one outside the expedition profiles, which already required it, so nothing existing changes.
3. `StartSceneBuilder.BuildHomeStart(..., bool exteriorDock = false)` (6 lines): when true, runs `HomeDockPlanner.Apply` after `ValidateHomeStart` and re-rolls the seed on rejection. Default false. **The generated-home boot is still not wired into New Run.**

## Cost and risk

| Item | Size | Risk |
| --- | --- | --- |
| Planner | about 190 lines, one new file | Low: pure, deterministic, tested over 200 seeds |
| `DockPorts` contract seam | 3 lines | Low: only layouts that already carry a contract are affected |
| `BuildHomeStart` flag | 6 lines | Low: default off |
| Tests | `HomeDockPlannerTests` (4) and `LifeboatFitSpikeTests` (1) | - |
| Rotated boats | not a code change | **Medium**: nothing in a rendered scene has docked a boat rotated 90 or 180 degrees to a generated home |

## Verified versus inferred

**Verified (EditMode, real code paths):**

- The 1.7b strip model equals the real docking transform on all 200 homes.
- With candidate A, the boat overlaps zero cells of other rooms in 197 of 200 first attempts and in all 200 within 8 attempts.
- `DockPorts.ForDerelict` returns the stamped port, and the three mirrors of the layout agree.
- The golden hub's port, and the default `BuildHomeStart` output, are unchanged.

**Inferred, not run:**

- That the walls at the dock room's outward edge open into the boat. `DockedShipGeometry.SuppressCoveredEdges` suppresses host walls owned by an `airlock`/`dock` room where the boat's floors cover them, and the exterior-edge wall sits between the boat's engine-bay and airlock floors, so it should be suppressed. A rendered check is needed.
- That nothing else assumes the boat points toward +X (camera framing, `SpawnClearance`, `HomeJoinPlanner` mooring sites, which are measured from the canonical dock port). The boat's own repair points and controls are parented in the boat's local space and rotate with it.
- That a single-cell dock room whose engine bay now coincides with the cell is as walkable as the golden hub's 2x2 room (which is cell-aligned for a different reason: its centroid falls between cells).

A rendered check would build a generated home through the scene host and dock the boat in an EditModeUnity or PlayMode test, then assert there are no overlapping colliders and that the nav mesh connects the home's start cell to the boat's cockpit. That needs the Unity editor and was not cheap enough for this spike.

## What this does not fix

The other extensions from the 1.7b report are untouched: the hazard strip (room variants, encounters, arc zones), a minimum of three non-connective rooms, reachability to the cache rooms, and the guaranteed repair kit. This spike removes the lifeboat-fit blocker only.

## Recommendation

Proceed with the generated-home plan using candidate A. Before flipping New Run: add the rendered dock check above (including a 180-degree and a 90-degree case), keep `exteriorDock` on for generated homes, and rerun the 1.7b sweep with `exteriorDock: true` to confirm the combined bar. If the rendered check finds a rotated-boat problem, the fallback is to restrict candidate A to `+X` and re-roll the other seeds, at a lower pass rate (to be measured), or to fall back to a few authored homes.
