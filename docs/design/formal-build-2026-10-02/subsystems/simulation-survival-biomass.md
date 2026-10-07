# Simulation, survival and bounded biomass

**Status:** proposed S01/S02/S03/S04. OPEN-01..05/09/10/11 remain explicit. Existing `ShipRuntime.CatchUp`, `RunSession.Tick`, `TickOrder`, `VitalsState`, `WoundState`, `SpoilageState`, `HydroponicsState`, `WaterRecyclerState`, `ThreatAIState`, `ShipNavGraph` and `SpatialPerceptionState` remain useful.

## S01: active world registry and scaling

Proposed `Core/Systems/Travel/ShipRegistry.cs`: `IReadOnlyList<ShipInstance> ActiveShips()` / `ShipInstance Resolve(string shipId)` / `GdDict GetPersistentReferences()`. Retain visited records separately from loaded/active roots; incrementally update active set at travel/connect/detach events. `AllKnownShips` remains a persistence query, not per-frame full-history traversal. Unload unneeded geometry while preserving all visited loot/modifications/encounters.

Per-owner dirty revisions identify snapshots needing serialization. Partitioned save writes must still commit one consistent generation; partition identity does not permit mixing versions. Snapshot caching invalidates on committed mutation and must not hide nested cargo/threat changes. Retained home assemblies have explicit root budgets and remain traversable.

A19 profiles 0/10/100/1000 visits with the same active working set. Record frame CPU/allocations, loaded roots/AI, memory, save bytes/latency and GPU separately. Current audit model medians are too small/noisy to nominate a bottleneck or hardware minimum. Performance budgets are OPEN-11; behavior and state conservation are hard requirements regardless of measured budget.

## S02: deliberate advancement policy

Proposed `Core/Systems/Travel/ShipSimulationPolicy.cs`: `GdDict Advance(GdDict state, double targetWorldTime, GdDict policy)` with nested `policy_id/version`, per-subsystem applied cursor and explicit pending elapsed interval. `ShipRuntime.CatchUp` adapts to it. World time is in-run monotonic simulation seconds unless OPEN-01 changes the mapping. Closed-game elapsed does not enter without OPEN-04.

Alternatives: analytic continuous outcomes for simple processes, deterministic bounded coarse stepping for nonlinear systems, or an explicit cap/freeze model. Define which hazards/jobs age, which freeze and why. Bounded numerical substeps remain; unprocessed time cannot be stamped complete. Repeated target time never double-ticks. Partitioned vs single absence must agree under the selected model within declared tolerances; if an approximation depends on visit cadence, document/test that behavior before acceptance rather than silently discard hours.

Fire, production, crops, wounds, food/cargo spoilage, web/hull and threats have separate cursors and policies. No proof that current `ShipRuntime` advances every one. Snapshot persists pending intervals so a quit during catch-up cannot apply or lose them twice. A20 repeats 300/1800/3600/86400-second absence against segmented/active paths and Save/Continue. Those durations are discriminators, not chosen campaign time scale.

## S03: recovery and resource loops

A safe shelter requires owner-local air, radiation, fire/electrical conditions and real machinery; hull ownership is not universal immunity. Stationary stamina recovery currently exists, bed/sleep command does not. Preserve portable wounds/consumables and make any future sleep/bed behavior a distinct reviewed action.

Extend proposed `Core/Systems/Food/ResourceFlowLedger.cs` with `void Record(GdDict committedFlow)` / `GdArray GetWindow(double fromTime, double toTime)`. Record actual accepted/debited quantities, container depletion, repair/craft/medicine costs, station outputs, effective XP and clamped health loss by cause. Distinguish transfer from consumption. This instrumentation may observe without changing survival rates.

Repeated-expedition design tests shelter -> rest/care -> finite preparation -> risk -> return -> repair/resupply. Finite medical/maintenance supplies cannot be assumed renewable; absent gauze/rad_patch routes must be sourced or truthfully deferred. A21 runs meaningful successive contacts with actually acquired care, food/water, repair and cargo handling. Balance passes require chosen duration/target and resource ledger, not a survival-rate reduction to pass a walking helper.

Spoilage currently tracks per item ID. Proposed future per-batch storage age records distinguish old/new supplies and cooling ownership; no fresh batch resetting old age, no ambiguity about carried/cargo storage through travel. Contamination/spoilage is a designed extension requiring exact rules under OPEN-09. A28 tests real prolonged cargo travel after those rules, while a fixture age probe remains diagnostic.

## S04: bounded biomass concept

This section proposes a local colony model; it is not an approved expansion requirement. Possible records: colony ID and ship/room territory, finite stored biomass, reachable resource nodes, expansion frontier, capped encounter budget, discrete response state and simulation cursor. A resource debit funds growth or creature spawn; killing/salvaging yields an authored bounded return. Removed territory and expenditures persist, so reboarding cannot respawn a free unlimited army.

Candidate behaviors: expand over connected accessible boundaries; consume authored matter/resources; defend active territory; choose one of a small authored response set to repeated noise/cuts. No ML, NPC labor, omniscient targeting, off-map infinite resources or whole-world autonomous takeover. Limit territory/spawn/step budgets per colony and provide readable environmental cues and containment actions. Resource amounts/rates and exact adaptation rules remain OPEN-10; authored horror pressure is an alternative.

`ThreatAIState` and `SpatialPerceptionState` continue authoritative combat/room sensing. Production creature geometry stays a view with existing hitbox/navigation authority. On topology updates, perception/nav/acoustics invalidate against the same boundary version. A22 checks enemy sensing after internal cut/door/reseal; A23, after OPEN-10, checks colony resource conservation, visible cues, finite expansion and exact saved territory/response restore.
