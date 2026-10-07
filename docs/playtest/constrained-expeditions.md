# Constrained expedition composition

The new `constrained_expedition_v3` profile replaces fixed room recipes for new later size-1/size-2 expeditions with seeded binary space partitioning and physical route selection. It does not replace existing saved v1/v2 layouts or expand the starter hub/first-away. See [design and publication invariants](../design/constrained-expeditions.md).

Size 1 uses a 10–12 by 8–10-cell hull with 9–12 interior partitions plus an external dock; size 2 uses 12–15 by 10–12 cells with 12–16 partitions plus dock. Each cell remains 4 m and each room has at least two cells in both dimensions. The hull envelope remains rectangular: free-form silhouettes and multi-deck circulation are future work. Partition sizes, functional room arrangement, spanning routes, additional physical loops and branch placement vary; rotation alone does not count as variation.

The existing structural compiler builds walls/doors from real selected shared boundaries. Two functional pairs have direct connections: medical/living and engineering/maintenance. Every output is checked for unique floor ownership, valid portals, connectivity, required roles, two or more cycles and a non-dock side branch. Boarding starts at the dock, and a far reachable room becomes the goal before condition overlays protect its critical route. Damaged/wrecked overlays can restrict optional routes; structural cycles are not a claim that every locked door is immediately usable.

After four failed attempts, deterministic v2 geometry is used with `composition_diagnostics.status = fallback`, the attempt errors and fallback recipe recorded. The structural pipeline still validates that fallback. Unsupported profiles/sizes and final disconnected v3 layouts fail closed. Ordinary production settings never use the test-only attempt-budget override.

Perimeter room furnishing, work/loot approach reservations, whole-wall cutaway, physical docking admission and saved recipe identity continue through the established implementation. Furniture is visual-only; this slice does not add resources or change combat/survival balance. Prototype props and unfinished art remain.

## Normal access

Complete the normal first-away visit and return, board the repaired lifeboat and press Tab near its bridge. Choose a new unvisited size-1/size-2 contact and Travel. The supported seed-17 world offers `-1:0:0` and `-1:-1:1` as second-trip choices; they now compose different seeded v3 layouts instead of selecting fixed service/cargo recipes. If already visited, those IDs retain their saved old profile: select another eligible unvisited contact. No new run is required for an existing save with a prior expedition.

## Read-only repeated-expedition pacing assessment

The earlier sequential three-trip soak died during the third outbound trip, after first-away and service, while exploring cargo. Its 191.245-second test failure and stack (`TickSurvivalAttrition → CheckVitalsDeath`) prove the final loss occurred during passive attrition. Logs lack a per-tick vitals/damage/consumption ledger, so exact radiation, oxygen and hostile contribution cannot be reconstructed.

The current inherited fallback irradiates an entire away destination when no authored radiation sources exist, including the docked lifeboat. Oxygen separately treats lifeboat occupancy as shelter. Radiation grows at 2 units/sec, becomes harmful at 50 and drains 1 HP/sec. Outside exposure it decays at 0.5/sec; recovering from 100 to below the harmful threshold takes about 100 seconds and can consume about 50 HP, with no default passive healing. `rad_patch` exists as a medicine definition but has no current loot/recipe entry. This is a concrete recovery/pacing gap, not proof of an unavoidable progression lock or an authorized balance change. Rates and item availability remain unchanged here.

Next pacing work should first record exposure, occupancy, incoming damage, vitals and medicine consumption in a diagnostic journey. Spatial radiation zones, shelter behavior and reachable recovery supplies require explicit values and acceptance limits; they should not be smuggled into room furnishing to make long tests pass.

## Verification

Core: **758 passed** (`builds/logs/constrained-v3-final-core.trx`). Edit Mode: **1,035 passed, 17 existing upstream skips** (`constrained-v3-final-edit.xml`). Windows GPU player: **three passed, zero failures** (`constrained-v3-windows-local.xml`), including the three-seed actual mesh/NavMesh/anchor fixture and two complete ordinary second-trip journeys with combat, role tours, save/Continue and return. The 48-layout pristine matrix plus six damaged/wrecked cases check varied graph/room areas, physical edges, critical routes and saved identity.

The Windows run finished 2026-10-01T03:19:09Z. Runtime test assembly SHA-256: `43B83B377558D6B4F03A9357BF707D947F5E1C7F4EBA85F8507622EFFCE1EAB1`. Live stationary bridge samples at 2048 x 1224 on RTX 4070 Laptop GPU measured frame-interval median/p95 **19.63/26.58 ms** (12 rooms, four threats) and **14.20/20.67 ms** (11 rooms, two threats). These are bounded offscreen live-host/player/AI/survival/HUD samples, not native-present certification or draw-call measurements.

Actual full-HUD captures remain local under `builds/StandaloneWindows64/artifacts/ramp-review/constrained-*`; Windows three-seed overview/role renders are under `artifacts/screenshots/constrained-*` alongside the test players. They were visually inspected. Prototype semantic props/warnings and unfinished art remain. The existing NavMesh agent-creation warning also appears in the prior build; this slice does not claim to resolve it.

The subsequent [persistent-survival correction and final acceptance](persistent-survival.md) closes the onboarding hard stop and verifies 759 Core passes, 1,036 Edit Mode passes (17 existing skips), 61 Play Mode passes and four fresh Windows journey passes. Completing onboarding continues the life and save/travel, rather than terminating it. See that page for the separately stamped development build and normal access instructions. Local captures are actual camera/HUD output, not generated imagery or native OS-input proof; they remain local.
