# Vessel habitats, structural topology and mobility

**Status:** proposed H01/H02/H03/M01/M02. Existing `ShipInstance`, `AssemblyMobility.Evaluate`, `HomeJoinPlanner.TryPlan/Validate`, `DockingManager`, `RunSession.HomeExtension` and `DockedShipGeometry` remain foundations.

## H01: per-vessel service ownership

Introduce proposed `Core/Systems/Travel/VesselServiceState.cs` with `GdDict GetSummary()` / `bool ApplySummary(GdDict)` and proposed `Core/Session/VesselServiceContext.cs` with `GdDict Resolve(string shipId)`. Own local craft/production station IDs, jobs/inventories, machinery references, power allocation and service revision per vessel. `RunSession.Crafting.BuildCraftingStations` / `BuildProductionStations` project the boarded owner's actual equipment rather than clear all services when `AwayFromStart`. Adapt `InteractionRegistry` home-only station scopes deliberately and update `docs/InteractionOrder.md` with its equality test.

A secured hull creates no equipment. Generator/installed-station manifests determine which stations physically exist; repaired local machinery supplies them. Station access follows ownership/access, local reach/LOS, station tier and power. Portable field craft/treatment remains available under its existing explicit capabilities. New service owners have no copied original-home crops/water/XP or `HomeSpawnSafety` immunity.

Legacy top-level hydroponics/water moves only to original-home station IDs under the migration rules. Collection/harvest must debit local production state once; outage resumes using the selected simulation policy. Reboarding a different vessel with equal local station IDs cannot replace the prior vessel's jobs. Failure to resolve a service owner returns `missing_service_owner`, without using home power as fallback.

Acceptance A14: normally claim/repair/equip a secondary shelter; collect/craft with its power and station state, interrupt by local outage, save/Continue inside, revisit and compare the original home. Actual machinery acquisition and placement are required for natural acceptance; provisioned station diagnostic is separate.

## H02: typed boundaries and edit reconciliation

`ModuleIntegrityMap`, `RunSession.Fire.BuildFireContext`, existing portals/room metadata and saved module modifications feed a proposed `Core/Systems/ShipSystems/BoundaryTopologyState.cs`. Exact public proposal: `GdDict EvaluateEdit(GdDict command)` / `GdDict CommitEdit(GdDict prepared)` / `GdDict GetSummary()` / `bool ApplySummary(GdDict)`. Records contain ship/boundary IDs, inside room A, inside room B or outside, kind (solid/internal door/exterior hatch/opening), seal/integrity, local geometry identity and revision.

Cutting an internal solid boundary creates an internal passage, not an outside atmospheric breach. Cutting an outside hull boundary creates outside exposure. Air behavior beyond typed distinction depends on OPEN-02. Cutting remains supported module/boundary operations; arbitrary mesh surgery is not required.

On a committed edit, publish one owner-qualified topology delta. Adapters reconcile colliders, portal/standing paths, NavMesh, AI room navigation, LOS/acoustics, atmosphere/fire context, interaction focus and camera occlusion. The geometry adapter reports completion/failure before passage is presented as usable. If instantiation fails, preserve a pending reconcile state and block unsafe passage; never advertise an open physical path that still has a wall collider. Reseal updates the same boundary identity and restores collision/navigation/perception consistently.

Acceptance A15 repeats the exact v4 seed17 dock/cargo internal edge and crew/outside exterior edge identified in `compiled_internal_exterior_cuts`, using actual timed work and physical player/AI checks after cut/save/reseal. A model graph result does not prove collider aperture.

## H03: service networks across joins

Keep mechanical join, conduit links and atmospheric openings separate. Proposed `Core/Systems/Travel/ServiceConnectionState.cs` exposes `GdDict Evaluate(GdDict request)` / `GdDict Commit(GdDict prepared)` / summaries. A closed connection door isolates air; absent conduit isolates power. The original join may remain mechanically secured while service links are broken. Each link names endpoint ship/station/boundary IDs and revision.

Choose OPEN-02 atmosphere fidelity before transfer math. A scalar fallback can implement typed exterior losses without claiming compartment pressure; a compartment model needs volumes, pressure and conservation plus leak/equalization rates. Never average unrelated percentage bars. Electrical supply must follow actual functioning conduit capacity/loss and local power demand; no remote engine power without a conduit.

Failure during conduit/seal work follows the work transaction policy. Occupied disconnect checks player path/landing and local safety before graph mutation. Broken power may pause production without destroying mechanical membership. A16 tests a closed join, deliberate conduit/door changes, local outage and save/revisit.

## M01: repair-dependent mobility

Preserve discrete sea-coordinate travel as the current compatibility model. OPEN choices may expand flight later. `AssemblyMobility.Evaluate(ShipInstance root, double playerCargo)` remains the capability calculation anchor: validated mechanical members counted once, dry mass and held/stowed payload counted once, eligible repaired enabled powered engines only. A stowed small craft adds load but not thrust. No forced static-home class.

Before departure validate piloted root, graph cycles/parent-child consistency, exact connection sites, occupied ports, machinery ownership, power, propulsion/navigation and load margin. Prepare new world location and relative poses; commit atomically. Preserve player-local location, door state and connection graph through transit/save/Continue. A secured member bridge normalizes to assembly control; independent shuttle control stays independent. Existing `CreateSpecification` / `ValidSpecification` legacy authority must not mint duplicate engines.

A17 combines a normally repaired hull, an earned installed engine on an assembly, insufficient-load rejection, A-B-C joins, independent shuttle detach/return, save at destination and nested stowed payload. A18 focuses occupied/nested detach: invalid landing/overlap/cycle rejection preserves all graph/state/geometry; a successful detach carries its descendants and exact saved local endpoints.

## M02: recovery logistics extension

Towing, connector structural loads and travel fuel/food consumption are OPEN-09/12 decisions. Proposed extension uses the same prepare/commit capability report, with owned available resources and persisted debt/cost once. Current `SeaGraph` charging helpers alone do not establish actual travel cost. Do not begin cost tuning until an exact resource ledger records the real `RunSession.Travel` transaction. A30 verifies actual debit, rollback and scout/home utility after that decision.
