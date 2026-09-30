# Playability and asset integration audit

This slice starts from game main `8dcc95c10ab5e08658546319f51f49b4abd256fc` and companion main `01230d8`. The playable bootstrap, hub objectives, inventory, repairs, ship travel, saves, HUD and results screens exist. New Run intentionally uses the Milestone A hub; unsupported seed/biome/difficulty requests remain rejected. Completing all hub objectives ends extraction; first-away travel does not.

## Gameplay changes

* Weapon hits select the nearest living eligible target within the weapon's reach, facing cone, physical line of sight and room visibility. Explicit target IDs cannot bypass these rules. Misses consume ammunition and swing time; cooldown survives saves. Facing follows the last movement direction.
* Enemy damage requires current reach and visibility. Retreating during a telegraph cancels the attack, including anchored tendrils. Presentation fixtures now place targets in actual reach and retain their damage, death and animation assertions.
* Player room signals come from the current world-positioned navigation graph. Sealed hatches affect sight and acoustic attenuation; bypassing them opens the link. Restored ship combat rebuilds spatial perception alongside navigation.
* Hardsuit wear persists in equipment/save state. Hits consume the same profile's durability; depleted destructible armor stops providing positive resistance. Biological resistance with no durability budget remains valid. Threat armor consumes the resolver's returned profile.
* Focus filters skip searched loot, repaired points, opened barriers and the currently piloted bridge. Loot, repair, bridge and other common handlers choose the nearest eligible target within their category rather than insertion order. Miscellaneous interaction categories still need a complete shared selection contract.

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

1. Extend natural lifecycle acceptance coverage through title, objective interactions, first-away combat, return, save/continue, natural death/extraction and restart. Existing lifecycle tests also use direct terminal-state setup, which cannot prove the entire natural journey.
2. Review custom damaged/breached geometry, remaining contract/visual mismatches, connector openings and multi-deck transitions across more seeds. The reusable adapter deliberately accepts only compatible planar tiles; it is not a general mesh normalization solution.
3. Complete authoritative interaction selection across all categories and presentation prompts. Common stale/nearest-target defects are fixed, but the dispatch architecture still has category-specific handlers.
4. Integrate critter-crafter behind a safe placeholder fallback. The game currently instantiates primitive threat visuals and does not reference the companion package. Production generation requires approved skeletons/parts; draft assets are not autoapproved by this work.
5. Implement a designed colony biomass/harvest/defense progression slice. Current infestation is scalar coverage/hull pressure and threats are fixed archetypes. The companion provides compatibility/budget generation and gait/IK constraints, not learning, resource harvesting or physical evolutionary adaptation. Those systems remain absent.

The original checkouts and their local settings are preserved. No remote publication, merge or deployment is included.
