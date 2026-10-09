# Home objectives, pickups and role placement (Phase 1.10)

Generated New Run homes (`RunLaunchRequest.GeneratedHome`, still off by default) now teach the game the way the golden hub does: the four typed
onboarding objectives, placed by room **role**, plus the two pickups the chain hands out, all on floor the survivor can walk to from the dock.

## What changed

- **`HomeObjectiveComposer`** (a post-pass on the generated documents, the pattern of `StartingHomeGuarantee` and `HomeDockPlanner`) replaces the generator's
  `salvage`/`interact` objectives with `recover_supplies`, `restore_systems` (a two-step `repair_junction`), `download_logs` and `stabilize_reactor`, in that order.
  Rooms come from the role table in `StationPlacer.PREFERRED_ROOM_ROLES` (`objective:<type>` keys), with a fallback to any usable room; unused rooms rank before
  reused ones, ties break on a seeded order. Cells are free interior loot slots (`FirstRunAwayGate.TryFindFreeSlot`, which now also blocks junction-step cells and
  `home_pickups`). It runs *before* the guarantee, so the three caches avoid its cells, and the result is checked again after the guarantee.
- **Pickups:** `home_pickups` in the slice names the room and floor cell of the portable oxygen pump and the junction calibrator. The session reads them for
  generated homes only; the golden hub keeps its original positions (`tool_storage_01`, then `maintenance_01`, else next to the survivor).
- **Role lookup:** `StationPlacer.RoomIdsByPreference(layout, kind)`. On the golden hub it returns the authored rooms the old literals named (`cargo_01`, `maintenance_01`,
  `medbay_01`, `reactor_01`); a test pins that. The web-mooring control uses it for generated homes (`home_mooring`); the golden hub still uses `cargo_01`.
- **No oxygen hazard at start:** the session used to synthesize a breach zone between the third and fourth objective when a layout had none. That zone is skipped for a
  generated home on the home ship (derelicts and the golden hub are unchanged), so an idle survivor keeps their air.
- **Gates:** `HomeObjectiveComposer.Validate` (types, order, rooms, slots, distinct cells, two distinct junction steps, mirrors) and `ReachabilityReason` (a cell-level
  standing path from the start/dock room to every objective, junction step, pickup and cache) run inside `BuildHomeStart`; failures reject the seed with a named
  reason (`too_few_rooms`, `no_room_for_objective`, `no_slot_for_pickup`, `unreachable_objective`) and the next seed is rolled. At least three usable rooms are required.

## Sweep (200 seeds, document level)

Seeds 1..200, `breach_field` / `standard`, Pristine, exterior dock, starting-home guarantee, onboarding composer.

| Measure | Result |
| --- | --- |
| Homes within 8 attempts | 200/200 (100.0%) |
| First attempt | 144 |
| Attempts histogram | attempt 1 x144; attempt 2 x41; attempt 3 x13; attempt 4 x1; attempt 5 x1 |
| Rejection reasons (all attempts) | no_free_slot x60; too_few_rooms x9; no exterior edge for the life boat x3; no_slot_for_pickup x2 |
| Usable rooms (min / median / max) | 3 / 5 / 7 |
| Objectives in a room whose role the type prefers | 522/800 (65.3%) |
| Wreck-mooring site on the west edge | 92/200 (46.0%) |
| Invariant problems | 0 |

## Objective placement by room role

recover_supplies@cargo x173; download_logs@bridge x100; restore_systems@engineering x72; stabilize_reactor@cargo x53; download_logs@medical x47; restore_systems@bridge x42; restore_systems@life_support x35; stabilize_reactor@reactor x31; download_logs@crew_quarters x26; stabilize_reactor@crew_quarters x26; stabilize_reactor@bridge x25; download_logs@cargo x17; restore_systems@cargo x15; stabilize_reactor@armory x14; stabilize_reactor@life_support x13; stabilize_reactor@engineering x12; stabilize_reactor@mess_hall x11; restore_systems@reactor x10; recover_supplies@storage x7; restore_systems@crew_quarters x7; restore_systems@medical x7; download_logs@hangar x6; restore_systems@hangar x6; stabilize_reactor@security x5; stabilize_reactor@storage x5; recover_supplies@armory x4; recover_supplies@engineering x4; restore_systems@mess_hall x4; stabilize_reactor@hangar x4; recover_supplies@bridge x3; recover_supplies@medical x3; recover_supplies@mess_hall x3; download_logs@armory x1; download_logs@life_support x1; download_logs@mess_hall x1; download_logs@storage x1; recover_supplies@crew_quarters x1; recover_supplies@hangar x1; recover_supplies@reactor x1; restore_systems@maintenance x1; restore_systems@security x1; stabilize_reactor@maintenance x1

## Pickup placement by room role

portable_oxygen_pump@cargo x106; junction_calibrator@crew_quarters x48; junction_calibrator@bridge x44; junction_calibrator@cargo x38; junction_calibrator@mess_hall x24; portable_oxygen_pump@armory x19; portable_oxygen_pump@engineering x19; portable_oxygen_pump@crew_quarters x17; portable_oxygen_pump@bridge x13; junction_calibrator@armory x11; junction_calibrator@medical x11; portable_oxygen_pump@mess_hall x10; portable_oxygen_pump@storage x9; junction_calibrator@storage x7; junction_calibrator@life_support x5; junction_calibrator@reactor x4; junction_calibrator@security x4; junction_calibrator@hangar x3; portable_oxygen_pump@security x3; portable_oxygen_pump@medical x2; junction_calibrator@engineering x1; portable_oxygen_pump@maintenance x1; portable_oxygen_pump@reactor x1

## Seeds without a home


## Findings

- **Generation is reliable:** every seed produced a home inside the retry budget. The extra gates cost attempts, not seeds (the dominant rejection is still
  `no_free_slot`: the three caches, five objective cells and two pickups need free interior slots, and small homes run out).
- **Roles:** about two thirds of objectives land in a room whose role the type prefers. Generated homes have no `maintenance` room (the derelict archetype lacks it),
  so `restore_systems` lands in engineering, life support or the bridge, and `stabilize_reactor` falls back to other rooms when there is no reactor. That is the fallback working as designed.
- **Named limitation (LIM-1): wreck mooring.** `HomeJoinPlanner.Sites` only offers the **west** edge of a deck-0 hull, at least 4.05 m from the dock port. Because the exterior
  dock often sits on the west edge, fewer than half of generated homes have a west mooring site. The column above measures the host side only. Phase 5 must generalize the planner
  to any exposed edge before the joined-home flow works on every generated home. Not fixed here.
- **Verified by invariant, not by change:** the runtime's dock-overlap suppression keys on a room id starting with `airlock` or `dock`; the sweep asserts every generated home's
  dock room id has that prefix. The commissioning control and bridge terminal stand on hull floor cells (a session test asserts the controls sit over floor).
- **Not generated (deviation):** blocked links (`biomatter blockage`). The powered-gate machinery needs a blocked doorway in the compiled structural plan, and the calm-start rule
  rejects blocked links as hazards. Generated homes therefore have no powered gates; main power, navigation and the flight path still come online through the objectives' force-repairs.
- **Not done:** `manifestation_pool.json` keys one hallucination by room id `reactor_01`; it is a flavour entry, and the role keys are a later change.

## Test results (macOS, PlayMode on the editor target)

| Suite | Result |
| --- | --- |
| dotnet | 1,963 passed, 0 failed, 1 skipped |
| Unity EditMode | 2,289 passed, 0 failed, 25 skipped (2,314 total) |
| Unity PlayMode | 88 of 90 passed, 1 failed, 1 skipped |
| Dev build and smoke | pass |

The one PlayMode failure is the known `JoinedHomeFlight…` (OPEN-3, the second excursion's engineering objective); it is unchanged by this PR.

The booted-session seam (`GeneratedHomeSessionTests`) sweeps 100 seeds by default (`SYNAPTICSEA_SEED_SWEEP`): the four typed objectives in order, no oxygen hazard and an idle survivor keeping their air, finishing the chain repairs the flight path and rewards the survivor, and the pickups sit over the home's floor.
