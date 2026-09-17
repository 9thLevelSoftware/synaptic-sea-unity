# Port "The Synaptic Sea" from Godot 4.7 to Unity 6 — End-to-End Plan

## Context

The Synaptic Sea (repo `D:\the-synaptic-sea`, GitHub `9thLevelSoftware/the-synaptic-sea`, `main` @ `96ecb2b0`) is a locked-isometric 3D space-horror survival sim. It is pre-alpha with 191 systems across 15 domains, about 65k lines of non-test GDScript, 826 smoke scripts, and roughly 3 MB of runtime JSON data. The user feels Godot is limiting them in four areas: rendering quality, the GLB/Meshy asset pipeline, GDScript tooling, and platform ecosystem (Steam etc.). This plan ports the game to **Unity 6 (6000.6.0f1), URP, C#** in a new repo at `F:\synaptic-sea`.

**Decisions the user fixed:**
- URP render pipeline.
- **Full feature parity** is reached domain by domain before the first playable milestone.
- **New minimal test suite.** The 826 Godot smokes are not translated. Cheap parity fixtures captured once from Godot are allowed.
- Every pain point is in scope: rendering, asset pipeline, C#, and platform.

**Why the port is tractable:** the codebase already separates pure data from engine code. 140 of 143 `scripts/systems/*.gd` files are `RefCounted` pure models with `configure` / `tick` / `get_summary` / `apply_summary` contracts. Nearly all UI and scene construction is code-built, with no shaders, threads, Tweens, or autoloads. The live asset set is small: 46 structural GLBs, 26 prop GLBs, 15 WAVs, and 16 placeholder PNGs. The hard parts are concentrated in a few places:
- The 12,267-line coordinator `scripts/procgen/playable_generated_ship.gd`.
- The 2,202-line loader `scripts/procgen/generated_ship_loader.gd`.
- The 29 UI panels.
- Deterministic RNG and hash parity for procgen and loot.

## Environment facts (verified this session)

| Item | State |
|---|---|
| Unity Editor | `F:\Unity\6000.6.0f1\Editor\Unity.exe`. Modules installed: Web + docs only. |
| Unity CLI | `C:\Users\dasbl\AppData\Local\Unity\bin\unity.exe` v1.0.0-beta.8, works. Templates: `com.unity.template.urp-blank` 17.2.1. |
| Unity Hub | `F:\Unity\Unity Hub\` |
| Claude **Unity plugin** | Listed in the marketplace, **not installed**. It ships 31 skills only, with **no MCP server or editor bridge**. Editor automation runs through the Unity CLI and batch `-executeMethod`. |
| IDE / SDK | VS Code is present. No .NET SDK, and no Visual Studio or C++ Build Tools, which IL2CPP requires. |
| Godot | Only `C:/Users/dasbl/Documents/Godot/Godot_v4.6.2-stable_win64_console.exe`. **`project.godot` declares 4.7**, and the paused branch uses 4.7.2 Mono. |
| Python | Not installed. Only the Windows Store stubs exist. Blender 5.2 ships its own Python. |
| git / LFS | git 2.55, git-lfs 3.7.1. The Godot repo stores 1.9 GB in plain git without LFS. |
| Disk | F: has 935 GB free. |

**Source-state caveat:** active gameplay work (crafting/derelict restoration and the world v6→v7 save migration) sits **unmerged and paused** on `codex/feature-completion-resume` in `D:\the-synaptic-sea-feature-completion`, 26 commits ahead. **Assumption: port from `main` @ `96ecb2b0`.** That branch's work is ported later as a delta after it lands in Godot, or is finished directly in Unity. Phase 0 records this decision.

**Native generator caveat:** `addons/derelict` is a Rust GDExtension (`DerelictGenerator`) that ships only as prebuilt DLL/dylib binaries, with **no source anywhere on disk**. The GDScript `ShipLayoutGenerator` fallback becomes the canonical generator. As a result, Windows-Godot derelict layouts for a given seed will differ from Unity's.

---

## Target repo layout

```
F:\synaptic-sea\                 git root (LFS for *.glb *.png *.wav *.ttf *.exr)
  SynapticSea\                   Unity project (Assets/ Packages/ ProjectSettings/)
    Assets\
      _Project\
        Core\       SynapticSea.Core.asmdef      noEngineReferences=true (+Newtonsoft)
          Contracts\ Variant\ Json\ Math\ Random\ Time\ Storage\ Data\ Systems\<domain>\ Session\
        Procgen\    SynapticSea.Procgen.asmdef   noEngineReferences=true, refs Core
        Runtime\    SynapticSea.Runtime.asmdef   MonoBehaviours: Bootstrap Adapters Session Ship Player
                    Interaction Spawners Zones Threats Audio Travel Save
        UI\         SynapticSea.UI.asmdef        UI Toolkit presenters, MenuCoordinator, ModalStack
        Editor\     SynapticSea.Editor.asmdef    prefab builders, DataSync, Builder, validators
        Tests\EditMode\ (refs Core+Procgen only)   Tests\PlayMode\ (refs all)
      Content\  Structural\ Props\ Audio\ UI\{Fonts,Icons,Theme,Screens}\ Materials\ Shaders\ VFX\ Input\ Prefabs\
      Resources\Catalogs\  KitCatalog, PropCatalog, AudioCatalog, RuntimeVisualCatalog (.asset)
      StreamingAssets\data\**    verbatim copy of Godot data/ JSON (res:// strings kept)
      Scenes\ Boot.unity Title.unity Playable.unity
      Settings\ URP assets, Volumes, SynapticSea.inputactions, SynapticSea.mixer, PanelSettings
  fixtures\   godot\{kernel,procgen,loot,models,save,meta.json}  godot_wrappers\*.tscn  golden\
  tools\      build.ps1 test.ps1 verify-headless.ps1 publish-itch.ps1 blender\ python\ dotnet\
  docs\       ported ADRs, this plan, TickOrder.md, InteractionOrder.md
  art-library\ (not imported by Unity, local only) ithappy 1.77 GB pack
```
Dependencies flow Core ← Procgen ← Runtime ← UI. Core and Procgen stay free of UnityEngine for three reasons. EditMode tests run in milliseconds. `tools/dotnet` can run them under plain `dotnet test`. And the `RunSession` can own every model without any MonoBehaviour.

---

## Phase 0 — Prerequisites, bootstrap, fixture capture

1. **Install the Unity plugin.** Run `/plugin marketplace add Unity-Technologies/unity-agent-plugin`, then `/plugin install unity@unity-agent-plugin`. The skills used throughout are `new-unity-project`, `unity-cli`, `unity-package-management`, `ui-uitk`, `audio-setup-mixers`, `urp-postprocessing`, `shader-graph-create-custom-node`, `physics-3d-collision`, and `build-live-game`.
2. **Install the remaining tooling:**
   - Run `winget install Microsoft.DotNet.SDK.8`, then add the VS Code extensions C# Dev Kit and Unity (vstuc).
   - Run `unity install-modules --editor-version 6000.6.0f1 windows-il2cpp mac-mono`. Add Linux later if wanted.
   - Install VS 2022 Build Tools with Desktop C++ and a Windows SDK. IL2CPP release builds need them. Mono works until then.
   - Run `winget install Python.Python.3.12` for the Meshy and sidecar tools that are kept.
3. **Create the project** with the `new-unity-project` skill. Check the flag names first with `unity projects create --help`, then create from `com.unity.template.urp-blank` into `F:\synaptic-sea\SynapticSea`. Run `git init` and `git lfs install`, and add the standard Unity `.gitignore` and the `.gitattributes` shown above. Per the skill, pass `--no-initial-commit`. Open the project once to generate `.meta` files, then make the first commit.
4. **Set Project Settings.** Use Force Text serialization, visible meta files, and Linear color space. Set Active Input Handling to Input System, the company to "9th Level Software", and the bundle id to `com.9thlevelsoftware.the-synaptic-sea`. Leave Unity Version Control off and remove `com.unity.collab-proxy`.
5. **Install packages** through the `unity-package-management` skill, using the PackageManager Client API and never editing `manifest.json` by hand:
   - `com.unity.inputsystem`
   - `com.unity.cloud.gltfast`
   - `com.unity.nuget.newtonsoft-json`
   - `com.unity.test-framework`
   - `com.unity.ide.visualstudio`, which also generates VS Code csproj files
   - uGUI and TextMeshPro are built in. Use TMP only for world labels.
   - **Deferred:** Addressables, Localization and Cinemachine. The hand-rolled replacements are explained below. (AI Navigation was deferred too, and is no longer: the threats walk on a real NavMesh, port-status decision 59.)
6. **Set physics layers.** Use 6 Player, 7 Structure, 8 ZoneBlocker, 9 Sensor, 10 Threat, 11 Prop, 12 Portal, 13 Ceiling, 14 Hallucination, and 15 Walkable (reserved). The collision matrix is:
   - Player collides with Structure, ZoneBlocker, Portal, and Threat.
   - Sensor collides with Player only.
   - Threat collides with Structure.
   - Prop, Ceiling, and Hallucination collide with nothing.
7. **Capture the Godot parity fixtures** once, before any port code, into `F:\synaptic-sea\fixtures\godot\`:
   - Get a **Godot 4.7.x console binary** that matches `project.godot`. Record its version, the source git SHA, and the date in `meta.json`.
   - Add `scripts/validation/export_parity_fixtures.gd` (extends SceneTree) to the Godot repo on a scratch branch. It emits the fixtures below.
     - `kernel/`: `rng_fixture.json`, the first 64 `randi`/`randf`/`randi_range` results for seeds 1, 17, 42, 777, 999, 7777, and 0x7FFFFFFF. Also `string_hash_fixture.json`, `float_format_fixture.json`, and `fnv1a_fixture.json`.
     - `procgen/`: layout.json and gameplay_slice for seed 17 MEDIUM/PRISTINE, and for seeds 42/777/999/7777 SMALL/WRECKED extended. Also one layout per biome/difficulty combo. Generate these **directly via `ShipLayoutGenerator.generate_with_options`**, never `ShipGenerator.generate_from_seed`, which routes to the Rust DLL. Canonicalize Vector2i values with `procgen_structural_debug_export._canonical_value`.
     - `loot/loot_rolls_fixture.json`: every table id, times 5 seed sources, times 2 contexts.
     - `models/<model>_tick_fixture.json` for oxygen, fire, electrical_arc, radiation, vitals, sanity, body_temperature, spoilage, hydroponics, web_infestation, and ship_systems_manager. Each records the config, fixed-delta steps, and the summary after each step.
     - `save/`: `current_run.json`, `world.json`, and `index.json`. Produce them by running the save/load and world-snapshot smokes, then copying from `%APPDATA%\Godot\app_userdata\The Synaptic Sea\saves\`.
   - Copy `data/procgen/golden/**`, `data/procgen/smoke/seed_000017/**`, and the 15 `scenes/wrappers/structural/ship_structural_v0/*.tscn` files into `fixtures/`.

**Done when:** the project opens headless with zero compile errors and `unity pipeline list` shows no Safe Mode. `git lfs ls-files` lists every binary. The fixtures and `meta.json` are committed.

---

## Phase 1 — Kernel (Core foundations)

Build the engine-free primitives everything else depends on.

- **Variant layer.** `JsonObject` is an insertion-ordered string-keyed map with `DeepCopy`, which replaces `duplicate(true)`, and `ShallowCopy`. `JsonArray` wraps `List<object>`. `V` holds the GDScript coercions: `Str`, `F64`, `I64`, `Bool`, `Obj`, `Arr`, and `IsNumeric`. Leaf values are limited to long, double, string, bool, null, JsonObject, and JsonArray. **Decision: keep dictionary-shaped contracts at every boundary rather than typed DTOs.** `Configure`, `GetSummary`, `ApplySummary`, and the layout/save documents all stay dictionaries. That preserves `apply_summary` semantics and `SaveMigrationService` dictionary surgery, and it allows tree diffs against Godot output.
- **JSON.** `GodotJson.Parse` uses the Newtonsoft `JsonTextReader` as a tokenizer only. Integer literals become long and float literals become double. `GodotJsonWriter` mirrors Godot's `JSON.stringify(x, indent, sort_keys=true)`: recursive ordinal key sort, the indent passed through, Godot's float formatting (`4.0`, 14 significant digits, trimmed), and minimal escaping. Semantic tree equality is the gate. Byte equality is advisory.
- **Math.** `double` holds all scalar model state, because GDScript floats are 64-bit. Do **not** use `Mathf`. `Vec3` is float32, `Vec2i` holds cells, and `GdMath.Round` uses **MidpointRounding.AwayFromZero**, since C# defaults to banker's rounding. `GdMath` also provides Posmod, IsEqualApprox, Clampf, Lerpf, and MoveToward.
- **Random and hashing, both parity-critical.**
  - `GodotRandom` reimplements Godot's PCG32: the seed/increment semantics, `Randi`, and **`Randf`, which consumes two `rand()` calls**. It also covers bounded `RandiRange` with threshold rejection, `RandfRange`, and `Randfn`. Verify against Godot 4.7 `core/math/random_pcg.*` and lock it with the fixture.
  - `GodotHash.StringHash` is djb2 over UTF-32 code points, returning a non-negative int64. `Fnv1a64` covers the rest.
- **Services.** `IClock` replaces `Time.*`. `IStorage` maps `user://` to `Application.persistentDataPath` or memory. `IResourceReader` plus `ResPath` map `res://data/...` to `StreamingAssets/data/...`, so the JSON is never rewritten. `CatalogRegistry` caches parsed catalogs, replacing each model's `static load_*()`. The `ILog` interface maps to `Debug.Log*` in Runtime and to a collecting logger in tests. `IEngineInfo` keeps the `godot_version` key in the save schema.
- **Contracts.** The interfaces are `ISimModel` {Configure, GetSummary, ApplySummary}, `ITickable`, `IAdvanceable`, `IHazardState` (with HazardKind, IsPassabilityBlocked, and GetStatusLines), `IStatusLineProvider`, `IHull`, `IWeb`, `IShipSystemHost`, `ISkillSource`, `IUniqueClaimSource`, `ISnapshotable`, and `IDiskPersisted`.
- **DataSync editor menu.** It copies runtime `data/**` from `D:\the-synaptic-sea` into `StreamingAssets/data`, excluding `training/`, `asset_generation/`, `comfyui/`, and `*.tres`. `DataManifestValidator` parses every JSON file and resolves every `res://` reference. Known-missing icon paths are snapshotted in `fixtures/known_missing_assets.json`.

**Done when:** EditMode tests pass for the kernel fixtures (RNG, hash, float format, FNV), for the JSON round-trip of seed 17 and golden 001–003, and for the data manifest. The same tests also pass under `dotnet test tools/dotnet`.

---

## Phase 2 — Translation mechanics (applies to Phases 3–6)

- **One `.gd` becomes one `.cs`.** Keep the same base name in PascalCase, and use the domain namespace (`SynapticSea.Core.Systems.Survival`). Public funcs become PascalCase with the same words. Carry doc comments across verbatim. Line 1 reads `// Ported from scripts/systems/<file>.gd @ 96ecb2b0`. Summary and config keys stay snake_case string literals.
- **Per-file checklist** (use it as the PR template):
  1. `float` becomes double. Seeds, hashes, epochs, and bit-ops become long.
  2. `Dictionary` and `Array` become `JsonObject` and `JsonArray`, or `List<T>` for typed arrays.
  3. `round()` becomes `GdMath.Round`.
  4. `RandomNumberGenerator` and `hash()` become `GodotRandom` and `GodotHash`.
  5. `Time.*`, `FileAccess`, and `Engine` become the injected services.
  6. `preload` becomes a type reference, and `Callable` becomes an `Action` or `Func`. Signals become C# events, or `SessionEvents` for cross-cutting outcomes.
  7. **`has_method`/`.call`** becomes an interface parameter. Keep `as I*` plus a null check where the call is genuinely optional.
  8. Never let a `Dictionary<,>` reach output order. Keep the same sort comparators.
  9. Add a round-trip EditMode test, plus a tick-fixture test where one exists.
- **Automated first pass**, then checklist review: the 140 RefCounted models, the pure procgen files, `ship_runtime`, the save files, and the pure audio models.
- **Hand-designed:**
  - The coordinator, loader, and `menu_coordinator`.
  - `audio_manager`, `threat_manager` and `hallucination_manager` (split into Core state plus a Runtime host), and all 29 UI panels.
  - The 15 `tools/*.gd` files and 4 `interaction/*.gd` files, collapsed onto a shared `ProximitySensor`.
  - The player, camera, `ceiling_fade_controller`, `slice_atmosphere_applier`, `ship_generator` (with the temp-file round trip removed), and `title_main`/`main`.
- **Dropped:** the roughly 160 `*_for_validation` seams, replaced by the public `RunSession` API plus a PlayMode `SessionTestHarness`. Also dropped: the orphaned 2D top-down branch (`scripts/topdown`, `scripts/threats`, `scripts/render`, `scenes/topdown`), `addons/godot_control_mcp`, and the unused NavigationRegion3D bake.

---

## Phase 3 — Leaf domain models (Wave 1, no cross-domain dependencies)

Port these domains into `Core/Systems/<domain>/`:
- **infra:** phase_timer, rarity/quality tiers, localization_catalog, build_metadata, demo_scope_gate, settings/menu/tutorial/tooltip/controller_glyph state, achievements, autosave_policy, and the ledgers.
- **inventory:** item_defs, inventory, equipment, encumbrance, ship_inventory, cart, cargo_transfer, unique_item, and selection.
- **survival:** oxygen, fire, electrical_arc, radiation, hull_integrity, web_infestation, vitals, sanity, body_temperature, status_effects, wound, hallucination_director, and life_support_state.
- **consumables:** the effect_dispatcher plus 5 resolvers.
- **food:** spoilage, cooking, food, hydroponics, water_recycler, and sustenance.
- **loot:** loot_roller, loot_distribution, junk_yield, and manifestation_pool.
- **objectives:** objective_progress, route_control, and derelict_objective_controller.
- **progression:** progression, training_event_bus, skill_tree, meta_progression, hub_upgrade, unlock_registry, class_definition, and recipe_knowledge.

**Done when:** every model implements its interfaces and passes a configure, summary, apply, summary round-trip. Tick fixtures pass for the numeric models, and `loot_rolls_fixture` passes, which gates RNG and hash parity.

## Phase 4 — Procgen + save service (Wave 2)

- **Procgen** (in `Procgen/`): ShipBlueprint, TemplateSelector, RoomAssigner, CellLayoutEngine, WallDoorResolver, StructuralEdgePlan/Compiler/Validator, LayoutMutator, LayoutSerializer (schema 1.2.0), EncounterInjector, Biome/DifficultyProfile, RoomVariantSelector, GameplaySliceBuilder, ShipLayoutGenerator, SeedDeterminismContract, KitCatalog, ModularSocketCatalog, and FirstRunContract.
- Add the `IDerelictLayoutSource` seam. `GdScriptPipelineSource` is the only implementation for now. Port the `_generate_via_worldgen` merge logic behind it, so a future Rust `cdylib` over P/Invoke is a drop-in.
- **Save** (pure): run_snapshot, world_snapshot, save_slot/index, save_migration_service, permadeath_resolver, cloud_manifest, crash_report_bundle, and save_load_service over `IStorage`.

**Done when:**
- Layouts and gameplay slices for seed 17 and seeds 42/777/999/7777 tree-match the fixtures.
- Golden 001–003 pass the validator, and the determinism FNV hashes match Godot's.
- The save fixtures load through migration and re-serialize semantically equal.

## Phase 5 — Ship systems, crafting, travel, combat, audio (pure) (Waves 3–4)

- **ship_systems:** manager, subcomponents, power_grid, life_support_system, propulsion, the module_integrity family, module_damage_router, the component catalog/placement/mount, ship_modification, the work_action channel/driver, deconstruction, hangar_bay, dock_ports, docking_manager, ship_instance, ship_runtime, and ship_access.
- **crafting:** crafting, material, and field_crafting state.
- **travel:** sea_graph, synaptic_sea_world, marker_generator, scanner, and travel_controller.
- **combat:** threat_ai, detection, damage_pipeline, armor, ammo, ship_nav_graph, threat_pathfinder, spatial_perception, and catalogs.
- **audio:** bus_config, ambient_zone, sfx_event_router, dynamic_music, spatial_audio_resolver, meta_event, and the audio_log data.

**Done when:** `ShipRuntime` snapshot parity and the ship_systems_manager break-roll fixture pass. The docking edge tests pass. The nav-graph A* path lengths on seed 17 match, and the music state-machine transition table passes.

## Phase 6 — RunSession: coordinator decomposition (Wave 5, the critical redesign)

`playable_generated_ship.gd` is **not** ported as one MonoBehaviour. It is split into Core session classes plus Runtime appliers.

- **`RunSession`** (Core) owns every model and runs a single `Tick(in TickContext ctx)`. `TickContext` carries Delta, a `LocationContext {Home, Away}`, player room and position, breach-zone and field-atmosphere flags, moving/crouching, lit, and safe-zone state. It iterates **one ordered `ITickStage[]` for both locations**, which removes the dual-branch `_process` early-return that caused PR #42–#44 regressions.
  - Canonical order: Autosave, Oxygen, Threat, SanityHallucination, ActiveFire, FieldCraft, SurvivalAttrition, PlayerVitals, TrackerStatus, Audio, PresentShips (hub slow band), RechargePortPower, Food, AmmoConsumableDecay, ElectricalArc, WorkAction.
  - Home-only behaviour becomes in-stage gates. The order is documented in `docs/TickOrder.md`.
- **Other Core classes:**
  - `SessionEvents` holds typed events.
  - `RunSnapshotAssembler` and `WorldSnapshotAssembler` cover source lines 10112–11250.
  - `RunContextResolver`, `HazardSeeder` (breach, then fire, then arc), and `ObjectiveCompletionHandler` port directly.
  - `StatusLineComposer`, `RunLifecycle`, `TravelPlanner`, `ShipOccupancy`, and `ProgressionAssembler` port directly.
  - `CraftingController`, `WorkActionController`, `ShipModificationController`, `WoundsController`, `CombatController`, `ConsumableController`, `LootController`, `PortalStateRegistry`, and `CorpseLootRegistry` round out the set.
- **Interaction order** lives in `InteractionRegistry`, a single ordered table in which each handler declares Scope {Home, Away, Both}. It replaces `_on_player_interact_requested` (lines 7953–8180). The order is barrier, terminal, fire point, repair, breach seal, [Home] crafting/production, loot, [Away] portal/hatch/reseal/derelict objectives, [Home] pickups/objectives, hangar, cargo, cart, yield drop, work action, then the miss SFX. The table is checked into `docs/InteractionOrder.md`.

**Done when:**
- The `TickStages_RunInBothLocations` test passes: a probe proves every stage runs under both Home and Away.
- The interaction-order table test passes.
- A headless session boots from seed 17 documents with no scene, completes objectives through the API, and its `RunSnapshotAssembler.Build` output tree-matches the Godot `current_run.json` fixture. `WorldSnapshot` matches `world.json`.

---

## Phase 7 — Content pipeline (can run in parallel with Phases 3–6)

**Asset copy** (about 35 MB live):
- `assets/imported/structural/ship_structural_v0/**` goes to `Content/Structural/ship_structural_v0/`. The `_textured.glb` variants are re-exported from Blender as glTF Separate via `tools/blender/reexport_gltf_separate.py`, so Unity texture presets (BC7, 2048) apply.
- The 26 prop GLBs and their sidecars go to `Content/Props/{components,dressing,objectives}/`.
- `data/audio/**/*.wav` goes to `Content/Audio/`, and `assets/ui/**` goes to `Content/UI/Icons/`.
- **Excluded:** `.godot/`, `*.import`, `*.uid`, `.tres` twins, `assets/tiles`, winlu, `_staging`, the ithappy structural set, and `data/training`/`asset_generation`/`comfyui`.
- **The ithappy pack (1.77 GB)** goes to `art-library/` outside Unity. An editor menu, `Import Library Asset…`, copies one asset in on demand. Note that GitHub LFS's free 1 GB quota cannot hold the pack.

**glTF import.** `ContentImportPostprocessor` applies glTFast settings: original node names, no animation, mipmaps, and non-readable textures. It also sets texture presets by suffix (albedo sRGB, normal/ORM linear) and audio presets (SFX decompress-on-load mono, music streaming Vorbis).

**Coordinate frame (verification gate V-FRAME).** Models, layouts, saves, and the nav graph stay in Godot's right-handed frame. **One** `Frame`/`SpaceConv` class converts at the view boundary.
- Unity yaw is always −Godot yaw.
- Position negates one axis, depending on how glTFast mirrors: X per its docs, still to be verified.
- `FrameConventionTests` places `wall_straight_1x1` and the chiral `wall_outer_corner` at yaw 0/90/180/270 and compares socket world positions to the contract math `p + local.rotated(UP, yaw)`. It also compares the corner's renderer bounds.
- The Godot yaw convention is south 0, west 90, north 180, east 270 (`structural_edge_plan.gd`).

**`StructuralPrefabBuilder`** (menu item plus `-executeMethod …BuildAll -strict`) builds one prefab per module in `Content/Prefabs/Structural/ship_structural_v0/<module>.prefab`.
- **Inputs:**
  - Kit JSON for module_id, family, footprint, nav_blocker, and pivot_policy.
  - **Contract JSON for socket positions.** The wrapper `.tscn` markers all sit at the origin, and the `.manifest.json` files carry names only.
  - **Wrapper `.tscn` `BoxShape3D`s for collision**, parsed as text from `fixtures/godot_wrappers/`. The kit's `collision_proxy_records` are Z-up Blender boxes and must **not** be used.
  - The variant GLB paths.
- **Output hierarchy:**
  - Root with `StructuralModule` (and `SetIntegrity` toggling).
  - `Sockets/<id>` empties with a `SocketMarker`.
  - `CollisionRoot` BoxColliders on the Structure layer.
  - `Visual/{Intact,Damaged,Breached}` GLB instances, with materials remapped through `StructuralMaterialTable` and any `Collision_*` / `*-col` nodes stripped. Ceilings go on the Ceiling layer with a `CeilingFadeTarget`.
- **Report** to `builds/logs/structural-prefab-report.json`. It lists missing GLBs, socket mismatches against the kit, bounds drift, unmapped materials, and placeholder unit-cube collision. The known placeholders are `ramp_up_1x2` and `ceiling_cap_1x1`; derive their collision from contract bounds.

**`PropPrefabBuilder`** reads the sidecars and produces `Content/Prefabs/Props/<asset_id>.prefab` with `PropVisual`. The Visual child bakes the offset, rotation, and scale. It uses the Prop layer with no collider, and checks sha256 and bounds.

The builders regenerate the committed catalogs in `Resources/Catalogs/`: `KitCatalog` and `PropCatalog`. The runtime never touches file paths.

---

## Phase 8 — Runtime scene layer (Wave 6)

- **`ShipSceneBuilder`** ports `generated_ship_loader.gd`. `LoadFromDocuments(layout, kit, slice, isAway)` builds, in order:
  - Wrapper instances for the placements, floors, and ceilings.
  - Integrity visuals.
  - Marker empties.
  - Trigger volumes (a `ZoneVolume` on the Sensor layer).
  - Placed props, via `PropCatalog` or `PrimitivePropFactory`.
  - Authored portals.
  - Per-room point light plus fog sphere.
  - Atmosphere.
  - Objective volumes.

  It returns a `ShipView` with typed lists and `ModulesByKey`, which replaces the 49 `set_meta` sites and the tree scans with a `ModuleMetadata` component. It raises `ShipLoaded` **synchronously** and builds detached before parenting.
- **`RunSessionHost`** (MonoBehaviour) runs `TickContextBuilder`, then `session.Tick`, then the `IViewApplier` list: BreachZoneView, ArcZoneView, RouteGateView, FireZoneView, ModuleIntegrityView, ThreatPlaceholderView, HudPresenter, AudioRuntimeBridge, WorkActionHud, and TooltipFocus.
- **Composition root:** `PlayableBootstrap`.
- **Player, camera, and sensors:**
  - `Player.prefab` uses a CharacterController (r 0.35, h 1.6) with manual gravity. Teleport disables the CC, sets the transform, calls `Physics.SyncTransforms`, and re-enables it.
  - `IsoCameraRig` is hand-rolled: orthographic, **size 11** (Godot 22 is full height), offset `Frame(16,18,16)`, LookAt in LateUpdate, near 1, far 120.
  - `ProximitySensor` is a trigger SphereCollider with a distance check and an OverlapSphere refresh after spawn or re-peg.
  - The 17 interaction prefabs: Interactable, ToolPickup, LootContainer, RepairPoint, BreachSealPoint, FireSuppressionPoint, ExtinguisherRechargePort, CraftingStation, ProductionStation, DockPortBarrier, BridgeTerminal, HangarBayControl, CargoHoldControl, CartControl, WorkYieldDrop, SealedHatch, and AuthoredPortalRuntime.
- **Zones:** Breach, Arc, Fire, and RouteGate spawners. `Collider.enabled` follows the model's passability.
- **Threat line of sight:** `ThreatLosProbe` calls `Physics.Raycast` with the Structure, ZoneBlocker, and Portal mask. `ThreatManagerHost` and `HallucinationHost` hold the scene-side halves.
- **Travel and save:** `TravelCoordinator` plus `DockTransformCarry` port `_attach_derelict_active` (reparent, re-peg player and docked ships), `travel_to`, and `travel_home`. `SaveCoordinator` handles F5/F6/F9, result routing, and `SceneReset`.

**Done when:** PlayMode tests confirm all of the following.
- Golden `coherent_ship_001` loads, with the wrapper count equal to the placement count, and socket parity holds for every placement.
- The player walks, and the interact chain follows the registered order.
- Breach, arc, and route colliders toggle with model state.
- Travel to a generated derelict and back works, and F5/F9 round-trips.

---

## Phase 9 — Rendering (URP), addressing the visual-quality pain point

- **URP asset and renderer:**
  - Forward+ (the per-camera light cap handles the roughly 80 point lights), HDR, and MSAA 4×.
  - No TAA, because it ghosts dither ceilings.
  - SRP Batcher and GPU Resident Drawer on.
  - Main-light soft shadows at 2048 with 2 cascades over 60 m. Point-light shadows off initially.
  - Renderer features: **SSAO** (verify it works under an orthographic camera), **Decals** (DBuffer), and a **Full Screen Pass** for hallucination FX.
- **Global Volume** (`urp-postprocessing` skill): ACES tonemapping, Bloom (threshold 1.0, intensity 0.35), Vignette 0.25, and Color Adjustments. Film grain is an opt-in setting. Motion blur and chromatic aberration stay off, the latter except inside the hallucination effect. Honour the `motion_reduce` setting.
- **`AtmosphereApplier`** ports `slice_atmosphere_applier.gd`. It maps each biome's `atmosphere` block onto Unity:
  - Flat ambient color × energy goes to `RenderSettings.ambientLight`.
  - Exponential fog density, times `away_fog_density_mult` when away, goes to the fog settings.
  - The key light becomes a Directional Light at `(−55, +35, 0)`.
  - The emergency accent becomes a Point Light at `(0,2.5,0)` with range 12.
  - The clear color is `(0.05,0.05,0.07)`.
  - One `K_omni` calibration constant in `RuntimeVisualCatalog` converts Godot omni energy to URP intensity.

  Use no baked GI or light probes, because the interiors are assembled procedurally. A small dark custom reflection cubemap keeps metals from going black.
- **Ceiling fade.** `SG_Lit_DitherFade.shadergraph` does alpha clip against a screen-space Bayer `_Fade`. `CeilingFadeController` sets it through a MaterialPropertyBlock: 1.0 within 12 m, otherwise 0.15. Dither keeps SSAO, shadows, and depth valid, and needs no transparency sorting.
- **Runtime materials** in `Content/Materials/Runtime/`: Unlit alpha for zones and gates, Lit emissive, Lit primitive, and FogSphere. All are referenced from `RuntimeVisualCatalog`.
- **World labels** use TMP 3D billboards sized to target screen pixels, scaled by the accessibility setting and distance-culled. Hazard warnings are never culled.
- **VFX:** Shuriken `Fire`, `Smoke`, `Embers`, and `Sparks` prefabs, modelled on the unused Godot `scenes/vfx/timed_fire.tscn`. `EmissivePulse` drives emissive-pulse animations.

**Done when:** `ScreenshotCaptureTests` renders coherent_ship_001 at 720p, 1080p, and 1440p into `artifacts/screenshots/`, and the images are reviewed against a Godot capture for orientation (north up-left, not mirrored), light level, and fog.

## Phase 10 — UI (UI Toolkit, built directly to the 2026-09-05 UI presentation spec)

The spec in `docs/game/features/ui_presentation_program.md` was never implemented in Godot. Unity builds straight to it, using the `ui-uitk` skill.
- **Tokens and theme.** `Content/UI/Theme/tokens.uss` holds the spec tokens:
  - Panels `#0D151C` / `#17232C`, text `#E8EEE9` / `#B6C4C9`, and info/focus `#69D2DF`.
  - Caution `#F2BE62`, danger `#F17B72`, and success `#9CCB9B`.
  - Spacing 4/8/12/16/24/32, body text 18 px, and 44 px rows.
  - Transitions of 120–180 ms, and a visible `:focus` outline.

  Text scale 1×/1.5×/2× is applied **by reflow**: `.scale-150` and `.scale-200` redefine the font and row variables, never `PanelSettings.scale`. `PanelSettings` uses Constant Physical Size at 96 DPI, with separate HUD and menu panels.
- **Screens:**
  - HUD lower-left cluster: vitals, urgent statuses, weapon/ammo, quick-use 1–3, work strip, captions, and context prompt.
  - Upper-left objective chip.
  - Live inspection panels: Inventory/Transfer (virtualised `ListView`), Wounds, ShipMod, Scanner, Chart, RecipePicker, Codex, and TutorialOverlay.
  - Pause stack: Pause, Settings (Accessibility/Input/Audio/Language), Records, SaveLoad, Credits, Achievements, ClassRoster, SkillTree, ConfirmDialog, and Toast.
  - RunResults and Title.
- **`MenuCoordinator`** keeps the state machine, with its 14 dependencies becoming constructor arguments. `ModalStack` gives each panel an `IInputConsumer`, and the top-most one consumes input. `GameplayInputRouter` owns which Input System maps are enabled.
- **Fonts:** Inter and JetBrains Mono (SIL OFL) as SDF FontAssets. Record licenses in `Content/UI/Fonts/LICENSES.md` and add them to `credits.json`.
- **Hallucination FX:** a URP Full Screen Pass with `SG_Hallucination` (chroma, warp, desaturation, vignette pulse) placed after post-processing, so the UI stays clean.

**Done when:**
- The HUD covers 20% or less at 1× and 30% or less at 1.5× and 2×, and never overlaps the protected centre or corridor. An EditMode layout test checks this.
- A gamepad-only navigation PlayMode test passes: Title, Settings, Back; open Inventory, transfer, close; Pause over Inventory, then Resume restores focus.

## Phase 11 — Audio and input

- **Mixer** (`audio-setup-mixers` skill): Master with sfx −3, music −6, voice −3, ui −6, ambient −9, and meta −6 dB. Expose `Vol_*` parameters. Add snapshots Default, Paused (lowpass on sfx and ambient, with ui and voice untouched), and Death.
- **`AudioManager`:**
  - One 2D source per bus, plus a pool of 16 3D sources.
  - The **AudioListener sits on the player**, because the camera is 29 m away.
  - The 3 music stems start with `PlayScheduled` so they stay sample-aligned, with volume crossfades.
  - Audio is skipped when running headless.
  - `AudioCatalog.asset` holds the 16 event→clip entries from `STREAM_CATALOG`. Unlisted events log once, per ADR-0044.
- **Input:** `SynapticSea.inputactions` with a generated C# class and no `PlayerInput` component.
  - Maps: Player (move, interact, attack, reload, crouch, field_craft, hotbar_1..3), Panels (toggle_inventory/scanner/ship_mod/wounds/map/codex, ui_pause, save/quick/load, which are dev-only in release), and the standard UI map.
  - Action ids equal the Godot ids, so `data/ui/input_glyphs.json` resolves unchanged.
  - Two control schemes, KeyboardMouse and Gamepad. The last-used device selects the glyph scheme.

**Done when:** `AudioCatalogTests` and `InputActionTests` pass. Every glyph action must exist and have both a keyboard and a gamepad binding.

## Phase 12 — Parity closure, builds, platform

- **`Builder.PerformBuild`** (Editor), in order:
  1. Runs `DataManifestValidator`.
  2. Stamps `bundleVersion` as `0.1.0+<sha>` and writes `build_stamp.json`.
  3. Sets the `SS_BUILD_DEV|DEMO|RELEASE` define.
  4. Uses Mono for dev and demo, and IL2CPP for release. It falls back to Mono with a warning if the C++ toolchain is missing.
- **Scripts in `tools/`:**
  - `build.ps1` wraps `unity build … --execute-method SynapticSea.EditorTools.Builder.PerformBuild`.
  - `test.ps1` wraps `unity test … --mode EditMode|PlayMode --report-format junit`; exit code 8 means tests failed.
  - `verify-headless.ps1` wraps `unity run` plus `unity pipeline list`.
  - `publish-itch.ps1` wraps butler.
- **Targets:** Windows x64 and macOS. macOS Mono from Windows is unsigned; notarisation needs a Mac. Linux is optional. itch.io comes first.
- **Steam (stretch):** reserve `Assets/Platform/Steam/` behind an `SS_STEAM` define, and prefer Facepunch.Steamworks. Achievements map 1:1 to `data/release/achievement_catalog.json`.
- **Optional CI:** `game-ci/unity-builder@v4` and `unity-test-runner@v4`, which need a `UNITY_LICENSE` secret.
- **Tooling continuity:**
  - Keep `tools/blender/*.py`, which run on Blender's bundled Python.
  - Keep `tools/python/` for the Meshy pipeline and sidecar generators, which need Python 3.12.
  - Replace the Godot-dependent `gltf_collision_postprocess.py`, `promote_structural_sources.py`, and `meshy_runtime_review.py` with the C# editor validators `PromoteStructuralSource` and `MeshyRuntimeReview`.
  - Port `build_system_inventory.py` later so the system inventory points at C# files.

**Done when:** `tools/build.ps1 -Target StandaloneWindows64 -Kind dev` exits 0. The exe starts under `-batchmode -nographics -quit` with no exceptions in its log. `butler validate` passes. Every parity fixture and test is green.

---

## Relative effort (S/M/L/XL)

| Area | Size | Driver |
|---|---|---|
| Kernel (RNG, JSON, Variant) | M | Small but gates everything |
| infra, inventory, survival, consumables, food | L / M / M / M / M | Many mechanical files |
| loot, objectives | S / S | Loot carries RNG and hash risk |
| progression | M | Disk persistence, skill gate |
| procgen | **XL** | About 40 files, and RNG consumption order must match exactly |
| ship_systems, travel, combat | L / L / L | Cross-model seams, reparent surgery, AI plus LOS |
| save (assemblers) | L | Touches every model, parity-gated |
| audio | M + M | Pure models plus new mixer work |
| Coordinator decomposition | **XL** | 12k lines split into about 45 classes; the design is the deliverable |
| Scene builder, prefabs, asset pipeline | L | glTFast, frame convention, socket alignment |
| UI (29 panels, new spec) | **XL** | New stack and new design system |
| Rendering | M–L | Calibration and art-direction iteration |

## Key risks and mitigations

1. **RNG, hash, and rounding drift silently desyncs procgen and loot.** Kernel fixtures come first, and layout and loot parity run on every test pass.
2. **glTF handedness.** Keep one `Frame` class plus the V-FRAME test with a chiral module. Never convert coordinates anywhere else.
3. **Unifying the tick order changes intra-frame arithmetic.** Keep the documented canonical order and the both-locations test. Check parity at rest on post-tick summaries.
4. **Rust worldgen divergence.** Windows-Godot derelicts differ from Unity's for the same seed. Saves are safe because geometry isn't stored, but `visited_ships` room ids may not match; this is a QA item.
5. **Godot 4.6.2 vs 4.7.** Capture fixtures with a 4.7 binary and record the version in `meta.json`.
6. **Paused feature branch.** Port from `main`, and track the branch's save v7 and crafting/restoration work as a later delta.
7. **No IL2CPP toolchain yet.** Ship dev builds on Mono until VS Build Tools are installed.
8. **Removing validation seams** loses test reach. `SessionTestHarness` exposes the same verbs as public API.
9. **UI Toolkit gamepad focus risk.** Spike one panel in Phase 1. Presenters are stack-agnostic (`Bind`/`Refresh`), so a uGUI fallback never touches Core.
10. **Full parity before the first playable delays feedback on feel.** PlayMode screenshot tests from Phase 8 onward give visual checkpoints without breaking the parity-first order.

## Critical source files (reference while porting)

- `D:\the-synaptic-sea\scripts\procgen\playable_generated_ship.gd`. Sections: tick 8525–8576, interact 7953–8180, build 1245–1454, attach 2206–2360, save 10104–11420, input 11678–11860.
- `D:\the-synaptic-sea\scripts\procgen\generated_ship_loader.gd`. Wrapper instancing is at 706–780, and the nav bake at 923–969 is dropped.
- `D:\the-synaptic-sea\scripts\procgen\ship_layout_generator.gd` and `ship_generator.gd:100–205`, which hold the worldgen seam.
- `D:\the-synaptic-sea\scripts\systems\save_load_service.gd`, `run_snapshot.gd`, and `world_snapshot.gd`.
- `D:\the-synaptic-sea\scripts\systems\ship_runtime.gd`, `oxygen_state.gd`, and `loot_distribution.gd`.
- `D:\the-synaptic-sea\scenes\wrappers\structural\ship_structural_v0\*.tscn` (collision truth) and `data\placement\contracts\structural\ship_structural_v0\*_contract.json` (socket truth).
- `D:\the-synaptic-sea\scripts\procgen\slice_atmosphere_applier.gd`, `scripts\camera\iso_camera_rig.gd`, and `scripts\audio\audio_manager.gd`.
- `D:\the-synaptic-sea\docs\game\features\ui_presentation_program.md`.

## End-to-end verification summary

1. `tools/verify-headless.ps1` reports zero compile errors and no Safe Mode.
2. `tools/test.ps1 -Mode EditMode` passes: kernel fixtures, model round-trips and ticks, loot rolls, procgen layout parity, save parity, RunSession boot and snapshot, tick both-locations, interaction order, frame convention, data manifest, HUD layout, audio catalog, and input actions.
3. `dotnet test tools/dotnet` passes the same Core and Procgen subset without Unity.
4. The prefab builders run `-strict` with exit 0: 15 structural and 26 prop prefabs, with zero missing assets and zero socket mismatches.
5. `tools/test.ps1 -Mode PlayMode` passes: golden load, walk and interact, zone toggles, travel round-trip, F5/F9, gamepad UI navigation, and screenshot capture.
6. `tools/build.ps1` produces a Windows dev build, the smoke launch is clean, and `butler validate` passes.
