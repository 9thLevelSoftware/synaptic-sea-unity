# Procedural expedition expansion: first service-loop profile

## Diagnosis and compatibility boundary

New Run intentionally opens the fixed `coherent_ship_001` training hub (port-status decisions 60 and 65). The small initial area is not a randomly generated full expedition. The guarded first-away seed/biome/difficulty path is also retained.

Subsequent travel already uses `ShipGenerator`, extended topology templates, functional room roles, biome variants, encounter injection, condition overlays and structural validation. However, the live `RoomAssigner` uses each template zone's count and does not consume `ShipBlueprint.RoomCountRange`. That range belongs to the deprecated `RoomGraphGenerator`, which is not the live geometry pipeline. Increasing the blueprint's nominal room budget therefore does not reliably enlarge a live expedition. Same-deck template links require real shared cell edges; a declared loop is not automatically routed when its endpoint rooms are placed apart. The layout pipeline also has a connectivity soft-fail path after four attempts. These are real limitations of the existing pipeline, not missing store-pack scanning.

This first change adds a bounded, explicit **`service_loop_v1`** profile rather than changing the output of every historical seed. It replaces room assignment/cell layout only for new larger wrecks after the first visit; the existing geometry serializer, doorway compiler, biome variants, encounters, condition damage and gameplay slice still run. Existing blueprint documents omit the new profile key and retain legacy geometry. New visited ships save the profile with size, condition and seed; slot restoration and regeneration preserve that identity. Unsupported nonempty profile versions fail closed instead of silently generating a different wreck. The optional native derelict source interface is unchanged; this profile is used by the current managed generator, where that source is absent.

## First implemented slice

| Size | Geometry budget | Player-visible result |
| --- | --- | --- |
| Size 0 small boat | Existing generator | Opening and small encounters remain compatible |
| Size 1 expedition | 3–4 functional sectors, two service paths, end connectors and two side pockets | Approximately 14–17 rooms, alternate entrances and optional exploration branches |
| Size 2 expedition | 5–6 functional sectors, two service paths, end connectors and three side pockets | Approximately 21–24 rooms, larger spaces and off-route supply/medical pockets |

Seeded widths, sector depth, pocket depth/side and quarter-turn orientation vary. The main service routes form a physical loop. Crew, bridge and engineering sectors are guaranteed; secondary rooms and pockets use existing cargo, medical, maintenance and other role vocabulary. These roles feed the existing room variant/dressing, encounter and loot systems. Purpose-specific furniture is still sparse: a role assignment is not a claim that every room now has a complete authored interior.

The current large seed-42 review has **24 rooms and 138 floor cells**, approximately 2,208 square metres of cell footprint. Every cell has a real reachable NavMesh path from the start in the pristine geometry fixture after doors are opened. That fixture opens doors only to isolate physical route/doorway alignment; it does not bypass locks in the playable game. Existing condition mutators and travel/repair/class gates remain active in play.

## Wall seams and reveal

The actual local v0 bake reports 3.5 m straight walls but 2 m corner/T-junction visuals. The previous vertex adapter allowed a short multi-wing junction to suppress neighboring full-height edge assemblies, leaving upper gaps. `StructuralLayoutBuilder.ResolveForKit` now checks actual visual heights: an incomplete junction set uses full-height straight walls at the canonical edge poses and keeps all required edge placements. Compatible full-height kits retain the existing vertex solution. Source meshes, damaged/breached variants, original prefab assets and structural plan documents are preserved. The golden loader assertions still compare exact keys/counts/colliders through their established explicit port-deviation adapter.

Cutaway bounds selection now collects every active renderer in the selected logical module, including sibling trims/panels outside the sample ray. Retained compatible materials fade fully away, removing the former 8% ghost fragments. The intentional thin wall footprint remains. Unsupported materials still use reversible hiding. This remains camera presentation: collision, navigation and enemy physical sight are independent.

## Verification

- Multi-seed Core coverage: seeds 17, 42, 777 and 999 at both larger sizes; no overlapping cells, guaranteed roles, complete dock connectivity, physical cycles, validated structural plans and exact blueprint/seed regeneration. Small boats and legacy blueprint serialization retain their original path.
- Full affected Core aggregate: **741 passes**, zero failures/skips (`expedition-final-core-v2.trx`). The existing manual-slot test additionally asserts the saved expedition profile and seed.
- Full Unity Edit Mode: **1,011 passes**, zero failures, **17 unchanged upstream skips** (`expedition-final-edit-v2.xml`), including the additional slot assertion.
- Full GPU Editor Play Mode: **55 passes**, zero failures/skips (`expedition-final-play.xml`), including the existing natural first-away/combat/return/save/lifecycle journeys and new real larger-layout scene/NavMesh/render coverage.
- Independent GPU Windows test player: **5 passes**, zero failures/skips (`expedition-windows-local.xml`): real larger layout, Title/New Run, camera/wall rendering, F5/F9 and retained shader support. The Editor's no-callback exit in the quiet build flow is not counted as a pass; results come from the actual independently launched player.

On Deviltop's Windows run, generation + scene construction + NavMesh build took **780 ms** for the 138-cell candidate. It created **6,370 renderers** from the detailed local modules. This is useful load-cost evidence, not a steady-state frame-rate guarantee. Batching, module renderer complexity, room-light cost and occlusion scanning need dedicated frame-time profiling before increasing area budgets further.

Actual 2048 x 1224 camera-only game renders are under `builds/StandaloneWindows64/artifacts/screenshots/`: `expedition-overview-seed42.png`, `expedition-dock-seed42.png` and the refreshed `camera-after.png` / `camera-upper-deck.png` wall reveal views. They are GPU captures of local assets, not generated illustrations, native desktop screenshots or HUD coverage. New captures have not been uploaded to Library.

## Playing and subsequent stages

Start `F:\tmp\synaptic-sea-playable\builds\StandaloneWindows64\procgen-expeditions-20260930\TheSynapticSea.exe` (built 2026-09-30T17:59:20Z). New Run retains the safe training hub. Complete the established first-away boarding/return progression, then select a previously unvisited size-1 or size-2 wreck from the scanner. Existing visited wrecks regenerate their saved profile and are not upgraded in place. WASD/E, wheel zoom and F5/F9 remain the existing controls. Do not change the locked New Run seed/biome/difficulty setup to try to expose this profile.

The next coherent stages are: multiple hull/topology families with routed loops and shortcuts; clearer purpose-specific furnished room composition and distributed points of interest; physically usable multi-deck transitions before exposing bigger stacked layouts; and measured runtime batching/lighting/occlusion budgets. This first single-deck profile does not complete those stages or the wider procedural-generation redesign. Colony economy, class balance and creature owner approvals are outside this change.
