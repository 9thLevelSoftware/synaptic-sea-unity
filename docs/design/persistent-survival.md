# Persistent survival and a growing home

The authoritative goal is long-haul survival in a persistent space-horror world: survey and reclaim derelict ships, salvage and repair them, secure habitable space, and connect vessels into a home that can survive ongoing pressure. Returning from a trip and finishing introductory tasks continue the same life. A possible eventual escape is separate from ordinary returns, salvage completion and onboarding. Death ends that survivor, not the world (D8, see `decisions.md`): the corpse, home and stashes stay in the persistent world and the player starts a new survivor there. Existing saves and useful systems must be preserved.

## Verified drift and first correction

The inherited `RunSession.Objectives.OnInteractableCompleted` final-home-objective branch set `SliceComplete`, stopped simulation, paid meta progression, deleted current/autosave slots and emitted terminal results. `ObjectiveChip` then advertised an extraction route. This was a demo completion contract, not an adequate survival design.

Home objective completion now advances the sequence and checkpoints the same world without terminal payout, deletion or results. `HomeObjectivesComplete` distinguishes onboarding progress from `SliceComplete` (terminal life state). Survival and saving stay active, and the HUD directs ongoing exploration/repair/survival. The old `complete`/`completion` terminal aliases are refused. Explicit legacy `EndRun("extraction")` remains an internal compatibility seam; there is no ordinary gameplay caller for it. This is not an implemented escape journey. Death and its existing frozen-save/results behavior remain until Phase 3.6 replaces them with "new survivor, same world" (D8).

Other inherited demo assumptions remain: `SeaGraph` creates a default extraction endpoint and `ChartPanel` advertises its route, without a real long-horizon escape journey. Demo build gates cap cargo at 6 kg, refuse saves after 20 minutes and restrict retained inactive world state; these gates must not be used as long-haul acceptance. The known delivered development build is not the demo variant. These are distinct from the objective hard stop and remain explicit audit work.

Supported seed/biome/difficulty onboarding guards and first-away progression stay in place. They are launch/introductory safety constraints, not the whole product design. Historical milestone documents and test descriptions that call final home tasks extraction are superseded by this direction; their useful physical traversal, repair, death and save assertions remain.

## Existing foundations and missing integration

| Area | Implemented foundation | Actual limitation |
| --- | --- | --- |
| Persistent world and ships | `WorldSnapshotAssembler`, `ShipInstance` summaries: exact blueprint/profile, visited ships, inventories/carts, systems, hull/web/fire, combat, module integrity and components | Not a validated long-term economy or assembled-home network |
| Ship connections | `DockingManager` alignment, parent/child graph, dock-edge persistence and transitive subtree transform carry | Temporary docking is not a welded structural connection; one canonical port can overlap a second attachment |
| Real traversal | `DockedShipGeometry` supports physical collision/NavMesh unions and whole-wall visual suppression | Host owns one active pair, not a complete home connection graph |
| Reclamation/work | Bridge/access state, tool/material/skill-gated timed work, structural cutting and `weld_patch` | Welding repairs an existing panel by 0.35; it does not create a join or home membership |
| Stored vessels | Hangar slot/size rules and bay dock/launch | Storage does not create habitable connected rooms or shared services |
| Survival | Oxygen, suit air, radiation, vitals, wounds, fire/arcs, hull and infestation | Several coordinator/away fallbacks remain scalar; attached ships do not form air/power networks |
| Threat pressure | Per-ship encounters/combat state and web coverage/hull damage | Colony harvesting, funded defenses and autonomous expansion are not implemented; creature approval does not implement those mechanics |
| Procedural ships | Versioned v3 partitions, physical routes, functional adjacency, work/loot clearance and exact regeneration | Rectangular single-deck envelopes and visual furniture are not complete repairable habitation/resource architecture |

`OccupancyEntries` recognizes only home/boat/piloted/current roots. `TickPresentShips` advances home and current; interaction/perception/structural work predominantly selects `CurrentShip`/`Loader`. Saved airlock edges contain host/mobile/type/slot, not stable connection-site IDs and exact local endpoints. `TravelHome` can free a leaving wreck scene. `RecomputeExpandedShipSystems` uses coordinator home models rather than connected-vessel reactors/air volumes/breaches. Each must be addressed before claiming a working welded home.

## Next bounded home-extension slice

Support **one durable traversable reclaimed wreck connected to the home**, retaining each vessel's exact identity and individual saved state. Reuse docking math, work channels, summaries and physical-union handling.

1. Add a versioned attachment record: ship IDs, stable site/port IDs, local endpoints, connection state and home membership. Restore exact sites, not a newly guessed canonical port.
2. Qualify/claim a candidate through existing access/repair rules. Reserve a free supported home site; reject occupied connectors and whole-hull overlaps before changing state.
3. Secure the join through a real interruptible tool/material/skill work channel. Cancellation/failure must not consume a completed edge or leave partial membership.
4. Generalize occupancy, loaded-root lifetime, interaction target ownership, geometry reconciliation and exactly-once simulation to the home-connected graph. Work on one vessel must not cross-write another's equal module IDs.
5. Save/Continue while standing in the extension; leave and return with its doors, loot, hull repairs, infestation and cargo intact.
6. Initially keep service networks isolated behind the connection door. Implement deliberate sealing/conduit work and per-compartment transfer before sharing oxygen/power; summing scalar percentages would be incorrect.

Acceptance: both-way physical walking; closed-door isolation; collision/overlap rejection; cancelled joins; correct per-ship work and encounters; save/Continue from the extension; expedition departure/return with home membership intact; no automatic victory or save deletion.

The owner clarified mobility: **both stationary use and travel are supported**. Any vessel or connected assembly can move if repaired propulsion and power support its combined load; larger assemblies need more propulsion. Smaller detachable vessels remain useful for reconnaissance, scavenging and escape from immediate danger. Do not impose a static-home ship class or grant free assembly thrust. Traverse/count the connected graph once and diagnose missing repairs, power and thrust. This is a simulation capability model, not a promised rigid-body flight simulator.

Reclamation/towing logistics, removable secured joins, service-sharing work and infestation containment still need concrete implementation contracts. They do not block onboarding continuation. Initial attachment mechanics must preserve independent shuttle detach/return, reject invalid/overlapping connections and persist exact component/site identity.

## Long-horizon pacing assessment

The sequential three-trip test ended in passive attrition during the third trip, not a proven direct attack kill. Earlier damage/resource use cannot be reconstructed from its logs. The inherited source-free radiation fallback affects the entire away destination, including the boat, whereas oxygen treats boat occupancy as shelter. Radiation grows at 2/sec, harms health at 50, drains 1 HP/sec and decays outside exposure at 0.5/sec. Recovering from 100 takes about 100 seconds and can cost about 50 HP before harm stops. Default passive healing is zero. `rad_patch` has a medicine definition but no current loot/recipe entries.

This is an identified potential economy/recovery trap, not proof that every death is unavoidable. Existing ordinary medkits/rations are not evidence of adequate long-haul supply. Add a bounded diagnostic ledger of occupancy, radiation/O2/vitals, damage sources, consumption and medicine use, then test sustained shelter/recovery and finite repair/salvage costs. Do not disable survival or grant test supplies to conceal the gap. No survival rates or class balance changed in this correction.

Generator priorities now include usable connection sites, functional service compartments, repairable boundaries, accessible equipment/salvage anchors, containment doors and durable state. V3 is preserved as a room/route foundation; subsequent immutable profiles should add habitation and expansion constraints rather than more short clear-and-return objective chains.

## Mobility implementation audit

Travel currently checks one piloted systems manager and the session-wide `PropulsionState` percentage (operational and at least 50 percent). Hull size is a generation category, not mass. Cargo and installed-component weights exist, but dry hull mass and rated engine force do not. `thruster_control` is a control component, not an engine force specification. Home and lifeboat share the onboarding systems manager; summing their manager outputs would duplicate propulsion and power. The expanded power coordinator is currently the home grid. Live travel does not call the `SeaGraph` fuel/food charging helpers.

Implement capability from a versioned authored hull/engine specification, with explicit propulsion/power ownership and departure scope. Independent craft detach with their carried descendants. Connected-assembly departure counts validated mechanical members and payload once, includes only owned enabled repaired powered propulsion, preserves relative transforms and player-local position, and reports the resulting load/capability margin. Stowed engines do not contribute thrust. Unknown or inconsistent ownership must not mint an engine during save migration. Initially keep electrical networks local; shared supply needs an actual functioning conduit.

Reject ancestor cycles, inconsistent parent/child links, invalid/occupied connection sites and unsupported floor overlap before mutating a connection. Persist stable connection-site IDs and exact local endpoints, preserving legacy save fallback. The current dock graph has only self-docking rejection and saved airlock edges reconstruct from a canonical port; neither is enough for a durable multi-vessel home.

A remote wreck can be repaired and flown under its own capability. Towing/recovery logistics, connector load limits and travel-fuel consumption need explicit later contracts. No aggregate thrust, welded-home claim, balance retuning or rigid-body flight is implemented by the onboarding correction.
