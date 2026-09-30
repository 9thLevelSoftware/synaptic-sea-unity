# Playability and asset integration audit

This slice starts from game main `8dcc95c10ab5e08658546319f51f49b4abd256fc` and companion main `01230d8`. The playable bootstrap, hub objectives, inventory, repairs, ship travel, saves, HUD and results screens exist. New Run intentionally uses the Milestone A hub; unsupported seed/biome/difficulty requests remain rejected. Completing all hub objectives ends extraction; first-away travel does not.

## Gameplay changes

* Weapon hits select the nearest living eligible target within the weapon's reach, facing cone, physical line of sight and room visibility. Explicit target IDs cannot bypass these rules. Misses consume ammunition and swing time; cooldown survives saves. Facing follows the last movement direction.
* Enemy damage requires current reach and visibility. Retreating during a telegraph cancels the attack, including anchored tendrils. Presentation fixtures now place targets in actual reach and retain their damage, death and animation assertions.
* Player room signals come from the current world-positioned navigation graph. Sealed hatches affect sight and acoustic attenuation; bypassing them opens the link. Restored ship combat rebuilds spatial perception alongside navigation.
* Hardsuit wear persists in equipment/save state. Hits consume the same profile's durability; depleted destructible armor stops providing positive resistance. Biological resistance with no durability budget remains valid. Threat armor consumes the resolver's returned profile.
* Focus filters skip searched loot, repaired points, opened barriers and the currently piloted bridge. Loot, repair, bridge and other common handlers choose the nearest eligible target within their category rather than insertion order. Authored door HUD/dispatch now share one target. Further source-anchor normalization is still needed beyond the docked repair/fire/seal subset.

## Existing custom asset pipeline

The original projects at `F:\synaptic-sea` and `F:\repos\synaptic-sea-unity` contain generated/custom GLBs under `Assets/Content/Structural/ship_structural_v0`, alongside an `ithappy` kit. Export tooling includes `tools/python/export_structural_glb.py`; wrappers, structural contracts and kit catalogs are separate data. Runtime builds registered module IDs through `KitCatalogResolver` and `ShipSceneBuilder`; it does not discover arbitrary meshes or asset archives.

The active default kit is `ship_structural_v0`. The old ithappy bake's 4×4 visuals for 8×4/4×8 contracts demonstrate missing validation, but are not established as the active runtime failure.

The active custom `corridor_floor_1x2` was a concrete mismatch: its wrapper selected `corridor_floor_1x2.glb`, containing a floor_1x1 collision mesh and roughly 4×4 visual geometry, for a 4×8 contract and collision deck. The existing `corridor_floor_1x2_textured.glb` has a 4×8 visual. A checked-in `corridor_floor_1x2.visual.json` now explicitly binds the intact state to that export and lowers it 0.125 m to align its visible deck with the authoritative collider. No stretching or opening edits are applied. The rebuilt prefab preserves sockets and gameplay collision.

The optional sidecar supports `variants` and `variant_offsets_m` per label (`Intact`, `Damaged`, `Breached`). Other modules and variant selections retain their existing behavior. Existing damaged/breached 1×2 exports contain 2.5 m side structures; they need visual/opening review before replacing the current bindings. Their original files are preserved. The builder now reports floor footprint discrepancies instead of merely recording bounds. The default bake produced 15 prefabs, zero errors and 25 existing proxy/placeholder warnings.

## Local purchased floor example

`tools/import_purchased_megakit.py` imports two allowlisted 4×4 glTF floor sources and their dependencies from the owner's local ZIP. It records SHA-256 provenance in an ignored artifact. `PurchasedMegaKitBuilder.BuildFromVisuals` also accepts local custom GLB/glTF/prefab tiles. It checks units, tiles larger footprints without stretching, aligns deck height, fills genuinely missing material slots, removes imported collision and retains original module sockets/colliders.

This is a **visual overlay for four existing floor module IDs**, not a new generation grammar or automatic adoption of a whole pack. Doors, ramps and other incompatible units/openings are not silently resized. Licensed files, generated local prefabs/materials and the override catalog are gitignored and remain local. Disable the overlay through **Synaptic Sea → Content → Disable Purchased MegaKit Floors (local)** to use the original catalog.

## Validation

* Final Core aggregate: **707 passed, zero failed**, `builds/logs/combat-audit-final.trx`. NuGet vulnerability metadata lookup was unavailable; cached dependencies compiled and the tests executed successfully.
* Unity 6000.6.0f1 Edit Mode aggregate: **915 passed, zero failed**, `builds/logs/unity-audit-editmode-aggregate.xml`. Includes bounds, deck height, non-null materials, preserved collision/sockets and generated seeds 17, 42 and 73.
* Companion contracts/recipes/locomotion selection: **106 passed**, `F:\tmp\critter-audit-results.xml`. This is not the companion's full suite.
* Final Unity GPU Play Mode aggregate: **48 passed, zero failed**, `builds/logs/unity-audit-playmode-aggregate.xml`. Covers movement, interactions, real repair channels, accepted first-away boarding, later biome travel/return, save/load, combat presentation and NavMesh movement. Fixture corrections retain these assertions while supplying real room/reach/cooldown setup and honoring guarded marker eligibility and production kit selection.
* Windows development build: **passed**, Mono, 486.5 MB, zero errors; `builds/logs/build-StandaloneWindows64-dev.log`. Output: `builds/StandaloneWindows64/dev/TheSynapticSea.exe`. The player was built; a manual standalone gameplay session was not run.
* Actual Play Mode gameplay-camera image: `artifacts/screenshots/playable-local-floors.png`. The local floor test verifies deck support, no imported visual colliders, non-null materials and a cap of four renderers per tile. Visual inspection found no obvious floor flicker or floating in the captured view; this is not a GPU performance benchmark or exhaustive visual review of all seeds. A separate generated seed-17 overview is at `artifacts/screenshots/purchased-megakit-seed17.png` (1,631 scene renderers, including existing structural geometry).

Unit and scene tests establish this bounded slice, not a complete game or a continuous natural journey through every terminal outcome. The earlier runs' fixture/material failures are retained in separate logs; the aggregate files above record the final passing runs.

## Run locally

Open `F:\tmp\synaptic-sea-playable\SynapticSea` with `F:\Unity\6000.6.0f1\Editor\Unity.exe`. Open the **Boot** scene and press Play, then select New Run. WASD moves; E interacts; F attacks along the last movement direction; R reloads. The existing HUD/hotbar/pause controls remain available. Use the supported Milestone A defaults.

Alternatively run `F:\tmp\synaptic-sea-playable\builds\StandaloneWindows64\dev\TheSynapticSea.exe`, select New Run, and use the same controls.

To reproduce the licensed overlay locally:

```powershell
& F:\Tools\Python312\python.exe tools/import_purchased_megakit.py --archive 'F:\assets\Modular SciFi MegaKit[Source].zip'
```

Then run **Synaptic Sea → Content → Enable Purchased MegaKit Floors (local)** in Unity. To rebuild custom structural prefabs, use the existing structural builder's content menu. Changes to a sidecar require rebaking its prefab. Preserve authored source exports and review variant openings before adding new bindings.

## Remaining high-impact work

1. Extend the combined natural journey through resource acquisition, first-away combat and return. Title/New Run, walking, bidirectional deck transfers, loot, save/Continue, objective extraction/results/restart and normal-damage death/results/restart are now covered; first-away repair/travel/return currently has separate fixture-driven coverage.
2. Review custom damaged/breached geometry, remaining contract/visual mismatches, connector openings and multi-deck transitions across more seeds. The reusable adapter deliberately accepts only compatible planar tiles; it is not a general mesh normalization solution.
3. Complete authoritative interaction selection across all categories and presentation prompts. Common stale/nearest-target defects are fixed, but the dispatch architecture still has category-specific handlers.
4. Integrate critter-crafter behind a safe placeholder fallback. The game currently instantiates primitive threat visuals and does not reference the companion package. Production generation requires approved skeletons/parts; draft assets are not autoapproved by this work.
5. Implement a designed colony biomass/harvest/defense progression slice. Current infestation is scalar coverage/hull pressure and threats are fixed archetypes. The companion provides compatibility/budget generation and gait/IK constraints, not learning, resource harvesting or physical evolutionary adaptation. Those systems remain absent.

The original checkouts and their local settings are preserved. No remote publication, merge or deployment is included.

## Verified docking and lifecycle continuation

The intended overlapping home-airlock/lifeboat connection in `docs/port-status.md` decision 55 remains. Duplicate lifeboat static walls and frame posts fully covered by host floor are suppressed on runtime instances, with supported-footprint sampling and deck-height checks. Only covered exterior host airlock walls yield to the mobile interior; host room/corridor boundaries, unsupported walls, other decks and locked/hatch blockers remain. Sources, prefab assets and docking transforms are unchanged. Instance colliders/renderers and adjusted interaction anchors restore on undock.

The host builds one navigation surface over the connected geometry; the mobile's separate surface is suppressed while docked and restored afterward, including protection against LateUpdate re-adding duplicate data. Door carving has no stationary delay. Repair/fire/seal anchors embedded in composite docking walls move to nearby supported, capsule-clear positions within their interaction radius. Their system identity, resource/channel behavior and authored source positions remain intact.

Authored home doors are usable. Closed doors precede nearby deck/station interactions; open doors yield to reachable ordinary targets, including deck transfers. The HUD and dispatcher use the same portal target and deck reach rules. Home open/unlocked door state now survives world and manual saves through an optional backward-compatible snapshot field and is restored on return.

Validation on this continuation:

* Core: **710 passed, zero failed**, `builds/logs/lifecycle-core.trx`.
* Unity Edit Mode: **919 passed, zero failed**, `builds/logs/lifecycle-editmode.xml`, including unsupported/deck/lock preservation, undock restoration and safe repair-anchor assertions.
* GPU Unity Play Mode: **50 passed, zero failed**, `builds/logs/lifecycle-playmode.xml`. The continuous walking test uses title/New Run, physical movement, real doors, lower/upper/lower/upper deck transfers, authored loot, save/Continue with opened-door assertions, objective interaction through extraction/results and fresh restart. It does not teleport between objectives or call EndRun/CompleteObjectiveSequence. The death test injects an encounter, then relies on ordinary enemy damage, results and restart; it does not force zero health or EndRun.
* Repair/travel/return and wound/chart persistence fixtures retain their channel completion, consumed resources and travel/save assertions. These are separate fixtures, not a claim of one continuous natural first-away expedition.
* Rebuilt Windows dev player: **passed**, Mono, 486.5 MB, zero errors, `builds/logs/build-StandaloneWindows64-dev.log`. Runtime gameplay is verified in GPU Play Mode; standalone verification is a boot-to-title smoke check, not a manual expedition.
* Actual gameplay-camera capture from the bidirectional traversal: `artifacts/screenshots/natural-hub-upper-deck.png`. Camera captures omit the UI; dim lighting, primitive markers and visual readability remain presentation work.

Intermediate failures uncovered real overlapping collision, missing home-door persistence and wall-embedded interaction anchors. The test steering was also corrected to follow intermediate corners and approach props within physical reach. Assertions were retained and expanded.

## Bounded next integration and mechanic scope

Companion revision `01230d84c895c13fdb744df98d5b39724c35ba44` exposes `ICreatureVisualFactory`, seeded pool generation and saved-recipe reconstruction. The next adapter should use production validation (`allowReview=false`), persist each organic threat's recipe, preserve gameplay collider/navigation authority and fall back safely when no approved skeleton/pool is available. Keep `drone_swarm` mechanical. No draft asset is approved or production generation enabled by this continuation; the game still uses its existing primitive threat factory. Package installation, adapter implementation and successful approved-creature rendering remain unfinished.

A subsequent small colony slice can build on the existing `biomatter_tangle` junk item (yielding `biomatter_residue` and `reactive_gel`), work channels, saved per-ship infestation state and deterministic threat spawning. Define explicit colony biomass sources, a persistent reserve and bounded defense spending/alert thresholds before adding gameplay. Preserve the Milestone A progression guards. This would be scripted resource/defense behavior; force/mass/energy-constrained adaptation and learning remain separate unimplemented systems. The ownership and depletion of colony biomass sources need to be settled in the mechanic design before that larger change.

## Third slice: live Critter production adapter

The live threat view now uses the companion's production validation and saved-
recipe factory interface for `biomatter_swarm`, `puppet_corpse` and `stalker`.
Mechanical `drone_swarm`, hallucinations and unmapped archetypes keep game visuals.
Optional threat summaries persist exact recipe JSON and a deterministic decimal-
string seed, with pool and library identity. Invalid, missing, unapproved or
unavailable recipes keep playable placeholders without load-time rerolls.
Game collision/navigation and direct `Mesh` feedback remain authoritative; visual
root motion and imported colliders do not move or block threats. Successful
creatures use the published per-build base speed and existing AI multipliers.

The embedded companion package is pinned to `01230d84c895c13fdb744df98d5b39724c35ba44`.
No owner asset approvals or real art imports were performed. A synthetic approved
skinned-mesh fixture verifies successful assembly and restoration. Actual local
draft reviews were found in `D:\critter-creator`; the minimum candidate review
and precise runtime setup are in [critter-runtime-integration.md](critter-runtime-integration.md).

Final adapter checks: Core **710 passed, 0 failed**; Unity Edit Mode **952 passed,
0 failed, 17 skipped** out of 969; GPU Play Mode **50 passed, 0 failed**. Nine
adapter regressions pass within the Edit Mode aggregate. The 17 skips are upstream
tests requiring a built owner library/export fixtures at the companion test paths:
15 LibraryTests and 2 PrototypeImportTests. They are not production rendering passes.
Evidence: `builds/logs/critter-core.trx`, `critter-editmode.xml`, `critter-playmode.xml`.
Windows development Mono rebuild passed: **487.6 MB, 0 errors**, build version
0.1.0 (`builds/logs/critter-build.log`). Production-owner rendering remains gated
by approval, independently of the successful build and synthetic fixture.
The rebuilt standalone reached the title menu successfully in a hidden headless
boot smoke check (`builds/logs/critter-player-smoke.log`); this is not a manual
standalone expedition or owner-creature rendering verification.

The next proposed finite-harvest/biomass-reserve/alert-defense slice is specified in
[colony-defense-proposal.md](colony-defense-proposal.md). It has not been implemented.
