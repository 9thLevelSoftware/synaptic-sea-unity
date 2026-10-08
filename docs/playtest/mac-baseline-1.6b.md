# Mac baseline after Phase 1.6b (joined-home route follow-up)

Branch `phase1/1.6b-lifeboat-navigation`, cut from `main` at `ba4948c` (Phase 1.6 merged). macOS, Unity 6000.6.0f1, PlayMode on the editor target.

| Suite | After 1.6 (`ba4948c`) | After 1.6b |
| --- | --- | --- |
| dotnet | 1,876 passed, 0 failed, 1 skipped | 1,877 passed, 0 failed, 1 skipped |
| Unity EditMode | 2,196 passed, 0 failed, 25 skipped | 2,197 passed, 0 failed, 25 skipped |
| Unity PlayMode (editor target) | 84 of 86 passed, 1 failed, 1 skipped | 84 of 86 passed, **1 failed**, 1 skipped |
| `build.sh dev` | pass | pass (450.3 MB) |
| `smoke.sh dev` | pass | pass |

The PlayMode run was taken before the last EditMode-only test change; no PlayMode code changed after it. The one failure is still `JoinedHomeFlightRequiresEarnedPropulsionAndPreservesAssemblyAndShuttle`, which now fails at a different, later step (below).

## The Phase 1.6 "navigation gap" was not a navigation bug

Phase 1.6 reported that, after the test claimed the joined home's bridge, the lifeboat, its dock barrier and the wreck console went from PathComplete to PathPartial 8 physics steps later, and proposed a `ShipNavMesh.Rebuild()` union of roots. That diagnosis was wrong. Evidence from an instrumented run (temporary logs, since removed):

- The navigation roots did not change. At the press frame the log shows `ResetComposite` and `BuildAssembly host=GeneratedShipLoader members=GeneratedShip,LifeBoat`, and the `[Reach]` lines before and after both show the home assembly with two members (`assembly=2`). So the "docked lifeboat dropped from the roots" hypothesis is refuted.
- The home was already the piloted ship before the press. Every probe line, including the one right after the weld and Continue, reads `piloted=ship_start`. Welding a vessel to the home maps piloting onto the home. `RunSession.Interact.cs:22` makes a bridge terminal for the piloted ship non-interactable, so the press at the terminal was not a claim.
- The press fell through to the next handler in range: the home-join `connection_door` control about 1.1 m from the terminal. Pressing it toggles `connection_open` (`RunSession.HomeExtension.cs:144`), rebuilds the join controls and requests a save. The log shows `PLAYABLE SHIP SAVED` and the full assembly reconcile at that exact frame; `HomeAssemblyGeometry`'s identity string includes each edge's `connection_open`, so a toggle forces the reconcile.
- Afterwards the connection panel's `NavMeshObstacle` was enabled (closed) and the path to the wreck side ended just short of it (`join connection_door@(2.90) -> PathPartial ends@(1.57, 0.13, 0.00)`), which explains why everything beyond the seam became PathPartial.

**What is inferred, not directly instrumented:** the handler id and `DoorOpen` at that press were not logged, so "the press toggled the connection door" is the best-supported explanation, not a logged fact.

**Why it did not recur in 1.6b:** the test now presses only when the home is not yet piloted (`EnsureHomeAssemblyPiloted`). The home already is, so no press happens. The helper also asserts the connection doors are unchanged, so a press that toggled the seam would fail the test with the handler id.

**Could a real player hit it?** Yes, in the sense that pressing interact beside the bridge closes the seam (the control reads "Close home connection door"). They reopen it with the same control, which was reachable from the home side in the failing run (`join connection_door@(1.10, 0.40, 0.00) -> PathComplete`). No progression blocker is proven. A possible UX follow-up, not a bug claim: the control is about 1.1 m from the terminal, so a second press at the terminal silently closes the seam.

## Game fix (proven by before/after)

**Overlay entries changed the engineering salvage roll.** The Phase 0.3c book overlay added six books (total weight 3.0) to `salvage_engineering` (base total 18.8). Adding entries changes the total weight and so every fixed roll of that table. The route assertion "the existing deterministic engineering roll supplies thruster_nozzle without a fixture grant" failed because of it; after the books moved to `generic_crate` (four engineering-themed books) and `generic_locker` (astrogation and comms), the same route obtains the nozzle at marker -2:-2:2 (`[AssemblyFlight] engineering salvage marker=-2:-2:2 inventory={... "thruster_nozzle":1 ...}`).
- Guard test: `OverlaysNeverTouchTheProgressionSalvageTables` (compares every `salvage_*` table with and without overlays).
- `BookFoundInAnOrdinaryContainerCanBeStudiedForXp` now searches `generic_crate`, one container at a time. Its first version accumulated a heavy bag over up to 2,000 searches; the resulting encumbrance damage paused the study (`reason: damage`).

## Test-walker changes only (no game behavior)

- `EnsureHomeAssemblyPiloted`: press at the home bridge only when the home is not yet piloted, and assert no connection door changed.
- A repair point seeded on a container or objective starts work first. After the first press at the medical cache the walker waits for the work channel to finish, then presses again (up to three times). The same wait was added to the engineering-objective loop, but it did not fix that step (below).
- The medical-cache assertion now reports the handler, positions and the container list.

## Where `JoinedHomeFlight…` fails now

It passes the bridge step, the thruster nozzle excursion, and the medical cache at 0:2:1. It fails at the second excursion's engineering objective: "physically salvage the existing engineering objective; handler=repair_point; player=(64.00, 0.15, -36.08); objective=(-64.0, 0.12, -36.0)". The objective and the player are at the same place (Godot and Unity x signs differ), and a `repair_point` answers the press. The walker's repair-wait branch (taken only while a work channel is active) was not taken, which suggests the repair point could not start and consumed the press.

**Unproven.** The blocked reason was not logged. An attempted fix (a repair point that cannot start yields to an unsearched container in reach) was reverted because the route had not proven that case; it is the next candidate. See OPEN-3 in `docs/design/decisions.md`.

## Not done

No more diagnosis rounds were run after this one, by instruction. The standalone-player PlayMode target is unchanged (still unresolved; see `mac-baseline-1.6.md`).
