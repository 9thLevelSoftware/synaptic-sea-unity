# Unity port — status and decision log

Living companion to `docs/unity-port-plan.md`. The plan is the intent; this file records what exists and every place the implementation deliberately departs from the plan.

## Status

| Phase | State | Evidence |
|---|---|---|
| 0 Bootstrap | Done | Unity 6000.6.0f1 URP project, git + LFS, packages (glTFast 6.20, Newtonsoft 3.2, Input System 1.20), layers and collision matrix, IL2CPP, Mac Mono and Linux Mono modules, VS Build Tools, butler, .NET 8, Python 3.12 (all under `F:\Tools`). Godot 4.7.1 fixtures captured (`fixtures/`). |
| 1 Kernel | Done | Bit-exact against Godot for RNG, hashing, float formatting, and JSON (`KernelParityTests`) |
| 3 Wave 1 models (107 files, no dependencies) | Done | Merged; tick traces for oxygen, radiation, sanity, web infestation, and hydroponics match Godot bit-exactly; LootRoller matches all 50 Godot rolls |
| 3–5 Wave 2–3 models | Done | Merged; all 11 Godot model tick traces match bit-exactly, 42 Godot save files reproduce byte-for-byte |
| 4 Procgen chain | Done | All 14 end-to-end Godot layout recipes regenerate exactly (layout and gameplay slice, raw text hashes included); validator verdicts on 18 layouts plus 8 broken plans, determinism hashes, life boat, start scene, component placement and work-action traces all match |
| 6 RunSession | Done | `playable_generated_ship.gd` split into engine-free `Core/Session` (RunSession partials, tick stages with Godot's home and away orders, InteractionRegistry, snapshot assemblers, scene ports, 17 interactables). A headless golden-001 session boots, completes objectives 1–4, saves and reloads; a replayed Godot capture writes all 4 save files exactly (`docs/TickOrder.md`, `docs/InteractionOrder.md`) |
| 7 Content pipeline | Done (first pass) | 15 structural prefabs, 26 prop prefabs, catalogs, frame convention verified on 41 authored sockets |
| 8 Runtime scene layer | Done (first pass) | `ShipSceneBuilder` builds wrappers, markers, portals, zones, props, dressing and objective volumes, matching Godot loader fixtures. `Assets/Scenes/Playable.unity` (`PlayableSceneBuilder`) boots a `RunSession` through `RunSessionHost` with real scene ports (`Runtime/Session`), interaction/zone/threat/hallucination views, travel and F5/F9; the HUD and menus are wired by `Game/SessionUiBridge`. `PlayableScenePlayModeTests` runs the real loop with the golden ship's threats alive. It covers:<br>• boot, walk, registry interact, route-gate colliders and F5/F9 (including wounds and the web chart);<br>• a real stalker detecting and hitting the player, shown on the HUD, and attack keys killing a threat, reload and hotbar;<br>• making the life boat flyable through repair, fire suppression and breach seal points, then travelling to breach_field (hazard kit), dead_fleet (industrial) and hive (biomatter) derelicts and back;<br>• the gamepad inventory→pause→resume journey, settings persisting from Title through play and pause back to Title, and Title → New Run → quit.<br>`RunLifecyclePlayModeTests` covers generated New Runs (no warnings or errors logged), Continue, death → results → Title, and failures → Title. Capture: `PlayableScreenshotRunner` |
| 9 Rendering | Done (first pass) | URP Forward+, SSAO, decals, global volume; ceilings hidden in interior play; hallucination full-screen pass; 4 VFX prefabs; light levels calibrated against the Godot captures (below) |
| 10 UI | Done (first pass) | All 30 `scripts/ui` files ported as UI Toolkit presenters built to the UI presentation spec, with MenuCoordinator and ModalStack; `HudLayoutTests` enforces HUD coverage, protected zones and text sizes at three resolutions and three text scales. Wired to the session in the Playable scene (settings, tutorials, wounds, combat feedback, world labels, run results); gamepad journeys in `FrontEndPlayModeTests` and `PlayableScenePlayModeTests` (`docs/ui-port-notes.md`) |
| 11 Audio and input | Done | AudioManager port with per-bus volumes persisted in `user://settings.json`; Godot's 21 unused clips mapped, three music stems, ambient beds (decision 25); input actions mirror the Godot InputMap, and combat, reload, hotbar and hold-to-work are routed through `RunSessionHost` |
| 12 Builds and tooling | Done | Builds pass for Windows dev (Mono), demo (Mono) and release (IL2CPP), Linux dev (Mono) and macOS dev (Mono, unsigned). `tools/verify-headless.ps1` boots Windows players and Linux players under WSL, and checks the reported build kind (`build=demo` for demo). `tools/publish-itch.ps1` stages each build and passes `butler validate` (dry run; nothing pushed). `Builder.PerformBuild` runs `DataManifestValidator` and removes the build stamp afterwards (decision 45). `tools/test.ps1` exits 8 for failing tests and 1 for infrastructure errors. Manual GitHub workflows (`.github/workflows`), an inert Steam scaffold behind `SS_STEAM`, and tooling continuity (`tools/python`, `tools/blender`, `docs/inventory`, `PromoteStructuralSource`, `MeshyRuntimeReview`, Import Library Asset). The fixture exporter is on `origin/unity/parity-fixtures` in the Godot repo. Build Settings list Boot → Title → Playable (`BuildSettingsSetup`); Boot composes `AppServices` and loads Title (`FrontEndSceneBuilder`, `FrontEndPlayModeTests`). |
| Integration gap closure | Done | Plan `tender-swinging-badger` (2026-09-17). Wave 1: kit catalog resolution, mapped audio, icons, session gaps (wounds, hold-to-work, run context, saves run-5), fixture guards and round-trip replay. Wave 2: in-play integration, generated New Run, results and failure flow. Wave 3: real-loop PlayMode suite and these docs. `pwsh tools/test.ps1 -Mode All`: dotnet 626, EditMode 767, PlayMode 37, none skipped |
| Open-item and Phase 12 closure | Done | Plan `compiled-noodling-minsky` (2026-09-17), branches `close/session`, `close/scene`, `close/build`, `close/tools`. Session and save: decisions 35–40. Scene, visuals and UI: decisions 41–43 and the amendments to 9 and 19. Builds, platform and tooling: decisions 44–54. The follow-up decisions on the life boat spawn, suit air and ramps are 55–57. `pwsh tools/test.ps1 -Mode All` on the merged result: dotnet 644, EditMode 837, PlayMode 38, none skipped; after decisions 55–57: dotnet 648, EditMode 841, PlayMode 38 |

Run everything with `pwsh tools/test.ps1 -Mode All` (dotnet Core suite, then Unity EditMode and PlayMode).

## Decisions that differ from the plan

1. **Procgen lives in the Core assembly** (`Core/Procgen`, namespace `SynapticSea.Core.Procgen`). Two system models depend on procgen files, so a separate Procgen assembly would create a reference cycle.
2. **Variant types are `GdDict` / `GdArray`**, not `JsonObject` / `JsonArray`. The names mirror Godot `Dictionary` / `Array` and avoid clashing with `System.Text.Json`. Keys are Variants (int and float keys are distinct), matching Godot.
3. **Core has its own JSON reader and writer; Newtonsoft is not used in Core.** Godot parses every JSON number as a float using its own strtod, which is not correctly rounded. `GodotStrtod` is a port of that parser, so game data loads to the same doubles as in Godot. Test fixtures use an opt-in exact parse instead.
4. **Float text uses a port of Godot's `String::num`**, not .NET formatting. Mono's "F" formatting caps at 15 significant digits. The digit rounding follows decision 14.
5. **Two float RNG families.** `RandomNumberGenerator.randf()` (float32 with a clz exponent) differs from global `randf()` (`rand() / UINT32_MAX`). Both are ported. `Array.sort_custom` uses a port of Godot's unstable introsort (`GdSort`), because tie order affects procgen.
6. **Frame convention.** glTFast mirrors X, so Godot `(x, y, z)` maps to Unity `(-x, y, z)` and Godot yaw `a` to Unity `-a`. Lights and cameras get an extra 180° yaw, because Godot lights face −Z and Unity lights face +Z. `Runtime/Adapters/Frame.cs` is the only conversion point.
7. **Collision comes from the Godot wrapper scenes, sockets from the placement contracts.** The kit's `collision_proxy_records` are Z-up Blender boxes and are wrong. The wrapper `Marker3D` sockets all sit at the origin.
8. **The GLBs' `Collision_*` mesh nodes are hidden.** Godot rendered them as untextured boxes. They are collision proxies, not art.
9. **Known Godot data defects are kept for parity.** The pillar's `prop_anchor_up_01` and `prop_anchor_down_01` contract sockets carry Z-up values. The pillar, bulkhead, and ceiling wrappers use 1 m placeholder collision cubes. The ceiling cube is inert, because the Ceiling layer collides with nothing.
   - **Ramp amendment (2026-09-17).** The ramp's Godot placeholder cube is replaced. `StructuralPrefabBuilder` has a collision-override table that applies only when every wrapper box is the placeholder cube. For `ramp_up_1x2` it derives one rotated `RampSlope` box from the imported GLB's six treads: 0.7 m of rise over 6.67 m (about 6°), 3.6 × 0.2 × 8.0 m (`StructuralPrefabCollisionTests`). The tread tops reach about 0.87 m while both contract sockets sit at y = 0, so the high end is a ledge against a flat neighbour (open items).
10. **The Unity prefab lookup is `KitPrefabCatalog`.** The Godot `kit_catalog.gd` port is `Core.Procgen.KitCatalog`.
11. **No AudioMixer asset yet.** Unity has no public API to create one, and Godot's buses only used volume and mute. The runtime applies per-bus volumes itself; a mixer can be added by hand later for effects.
12. **The seed-17 smoke layout in the Godot repo came from the Rust worldgen.** The GDScript-pipeline target is `fixtures/godot/procgen/layout_s17_medium_pristine.json`. The Rust generator has no source, so Unity uses the GDScript pipeline, and Windows-Godot derelict layouts will differ for the same seed.
13. **String helpers are consolidated in `GdString`.** Wave 1 ports carry equivalent private `*Compat` helpers; new code uses the kernel class.
14. **Float text follows Godot's Windows builds, on every platform.** Official Windows Godot prints through the Microsoft C runtime printf: 17 significant digits (ties toward zero), zeros after that, then half-up rounding to the requested decimals. `GdFloatFormat.FormatFixed` reproduces this and matches 4,202 captured Godot outputs (`fixtures/godot/kernel/float_format_msvcrt.json`). Save and layout text therefore matches what Windows Godot wrote.
15. **No `GdScriptPipelineSource` class.** The GDScript generation pipeline is the fallback inside `ShipGenerator` when no `IDerelictLayoutSource` is set.
16. **Tonemapping is Neutral, not ACES, and the colour adjustments are neutral** (contrast 0, saturation 0). Godot rendered with linear tonemapping. URP's ACES toe crushed the Godot palette: the clear colour went to black and mid-tones fell by about 55%. That could not be recovered with light scales. Neutral stays within about 10% of the Godot captures and still rolls off HDR emissives for bloom. Vignette 0.25 and bloom are unchanged.
17. **No ceilings while inside a ship** (design decision, 2026-09-17). Ceilings exist only for future exterior views such as space walks. The ceiling modules are still built, saved and damaged exactly as in Godot; the gameplay camera (`IsoCameraRig.showCeilings`, default off) just culls `PhysicsLayers.Ceiling`, which also removes their shadows. This replaces Godot's 12 m ceiling fade, which never ran in Godot and left the player's own room covered. The dither-fade shader and `CeilingFadeController` were removed. Screenshot captures take `-ceilings show` for exterior review.
18. **The environment matches Godot's defaults.**
    - Ambient is `linear(color) × energy` (Godot semantics). `RenderSettings.ambientLight` stores the sRGB encoding of that value. It used to hold `color × energy` in sRGB, which scaled the light by energy^2.2.
    - `RenderSettings.ambientProbe` is set explicitly, because URP shades from the probe and a procedural scene otherwise keeps the default sky ambient.
    - A 4×4 cubemap of the clear colour replaces the sky reflection, because Godot's reflected-light source is the background.
    - The camera clears to `AtmosphereApplier.BackgroundColor`: the fog colour when fog is on (Godot fog covers the background, `fog_sky_affect = 1`), otherwise the Godot clear colour (0.05, 0.05, 0.07). The iso rig copies it every frame.
    - Layouts without a biome render like Godot with no WorldEnvironment: clear-colour ambient at energy 1, no key light, no fog (`AtmosphereApplier.ApplyGodotDefaultEnvironment`). Only `ScreenshotRunner` calls it so far.
19. **Single-visual structural wrappers were invisible.** The corners, T-junction, end cap, ceiling and bulkhead have one Godot `VisualInstance`, so the builder points all three variant slots at it. `SetIntegrity` then hid it again through the damaged/breached slot.
    - In Godot these modules draw a solid 4×2×4 `Visual_*` box in `SSV0_BULKHEAD` (0.22, 0.25, 0.29). Those are the "dark blocks". Their `Collision_*` box sits inside that visual, so hiding it changes nothing.
    - `StructuralModule.SetIntegrity` now keeps a single visual visible for every state except `destroyed`, as in Godot's legacy branch. The legacy per-state albedo tint is not ported.
    - The prefabs were rebuilt.
    - **Legacy tint amendment (2026-09-17).** Single-visual wrappers now take Godot's legacy per-state tint from `ModuleIntegrityConsequences`: damaged (0.85, 0.70, 0.55), breached (0.55, 0.60, 0.75). `StructuralModule.ApplyLegacyTint` multiplies the original colour through a per-material `MaterialPropertyBlock` on `baseColorFactor` (glTFast), `_BaseColor` or `_Color`, so shared materials are untouched. `SceneModuleNode.TintMeshes` forwards to it.
      - Intact clears the blocks, so a repair re-tints to white. Godot's loader never did that, because it only called the resolver for non-intact states.
      - Destroyed hides the visual, so blending is not switched (`StructuralModuleTintTests`).
20. **VFX are ported from the Godot scenes, but nothing in Godot uses them.** At 96ecb2b0 no script instances `scenes/vfx/*.tscn`. `VfxCatalog` records the intended hook for each scene.
    - Only `timed_fire` has particles: three GPUParticles3D become three Shuriken systems.
    - `beacon_blue`, `reactor_green` and `biomatter_blockage` are an emissive mesh, an OmniLight3D, and a looping pulse on the light and emission energy (`VfxEffect`). They get no invented particles.
    - Dropped: GPUParticles3D `randomness` (emission-time jitter; Shuriken has no equivalent) and `subsurf_scatter`.
21. **The hallucination FX keeps Godot's tint and adds the plan's distortion.** Godot's overlay was only a red ColorRect, with alpha = intensity × 0.35. `SS_Hallucination` keeps that tint, blended in sRGB, and adds chroma offset, warp, desaturation and a vignette pulse, all driven by the same intensity. `motion_reduce` turns off the warp and the pulse.
    - It runs after post-processing (`HallucinationRendererFeature`, RenderGraph blit).
    - Intensity is a process-wide static (`HallucinationFx.Intensity`), because the feature lives on the pipeline asset. At 0 the pass is not enqueued.

22. **Front end: Boot → Title → Playable.** `App/` (`SynapticSea.App`, references UI) holds the composition root and the title.
    - `AppServices` (DontDestroyOnLoad) sets the `CoreServices` seams (StreamingAssets, `persistentDataPath`, clock, `UnityLog`), reads `build_stamp.json` (its kind and version override `build_metadata.json`), configures the demo gate, the shared `SynapticSeaInput`, an EventSystem with `InputSystemUIInputModule` on the input asset's UI map, and `AudioManager` (skipped in batch mode). Scenes call `AppServices.Ensure()`, so Title opens directly in the editor.
    - Preferences live in `user://settings.json` (a `SettingsState` summary), per the spec's "one user preference source"; Godot had no standalone settings file. The env var seeds the text scale on first run.
    - `TitleScreen` runs the real `MenuCoordinator` in `TitleMode` instead of Godot's title-local text menu and settings mirror. The title adds a Records row (Save / Load, achievements, skill tree, hub upgrades, class roster, audio, language, build info, credits), which Godot's title did not offer.
    - The run handoff is `Runtime/Session/RunLaunchRequest.Pending`, then `SceneManager.LoadScene("Playable")`. The request carries:
      - the mode (NewRun / Continue / LoadSlot) and slot id;
      - the seed, biome and difficulty from the New Run setup (`App/Title/NewRunSetupPanel`; defaults seed 17, `breach_field`, `standard`);
      - the meta-selected class;
      - the settings summary, only when it changed at the title.
    - `RunReturnInfo` carries a failure reason or the run outcome and context back to the title.

23. **Playable scene: the scene half of `playable_generated_ship.gd`.**
    - `Runtime/Session` implements the session ports: `ShipLoaderNode` (`IShipLoaderView` over `ShipView`; room-node positions are the structural placements' `world_position`s, as in the headless harness), `UnityShipSceneHost` (detached roots stay inactive until attached; roots sit under the session root at the origin, so global = local), `UnityRunSceneState`, `AudioManagerSink`, `PhysicsLineOfSightProbe` (Structure | ZoneBlocker | Portal). Ship-root transforms keep the Godot `Xform3` exactly and mirror it through `Frame.ApplyLocal` (added to `Frame.cs`).
    - `RunSessionHost` ticks in `Update` (Godot `_process`, variable delta; the player stays in `FixedUpdate`) and skips the tick while `ModalStack.SimulationPaused`. Views follow session events and a per-frame reconcile of the session's node lists; nodes parented to a freed ship root are removed with it.
    - Interaction: one `ProximitySensor` (kinematic trigger on the player) sets `CandidatePlayerInRange` on `InteractableView` triggers (Sensor layer), refreshed with `OverlapSphere` after spawn, teleport and re-peg. Which handler claims an interact is still `InteractionRegistry`; the focus highlight picks the in-range view with the earliest registry handler.
    - Zones: route gate / breach / arc are ZoneBlocker box colliders whose `enabled` follows `SessionZone.CollisionEnabled`; fire zones spawn `timed_fire`, closed route gates `biomatter_blockage` (the VfxCatalog hooks).
    - Structural integrity writes go through `StructuralModule.SetIntegrity` (the resolver's per-child visibility calls fold into it).
    - Composition: `Game/` (`SynapticSea.Game`, references App and UI) holds `PlayableBootstrap` (uses `AppServices.Ensure()`, consumes `RunLaunchRequest`) and `SessionUiBridge`. A New Run, and a scene opened with no request, generate the home ship (decision 30). Golden `coherent_ship_001` boots only through an explicit `RunLaunchRequest.GoldenShip()` (tests). The UI audio seam is `SessionUiAudio` over the session's `SessionAudio` models, so bus volumes have one source of truth.
    - Core change: `RunSession.Create(deps, beforeReady)`, so the scene can subscribe before `_ready` raises the boot events.

24. **The kit prefab catalog follows the kit document that was loaded, not the layout's `kit_id`.** `KitCatalogResolver` (Runtime/Ship) keys the catalog by the folder of the modules' `godot_wrapper_scene` (`ship_structural_biomatter` reuses the `ship_structural_v0` wrappers, so it gets `KitCatalog_ship_structural_v0`). Kits without a complete wrapper map (`ship_structural_hazard`, `_industrial`) fall back to v0, like Godot's `kit_path_for_layout`. Callers pass the Core kit path (`ShipSceneBuilder.LoadFromPaths`' kit argument, `ShipSceneBuilder.KitPath` = `ShipDocuments.KitPath` for derelicts, `LifeBoatBuilder.BuildResult.KitPath` for the life boat) and `KitCatalogResolver.ForKitPath` reads that document; the loaded kit document is the fallback.

25. **Audio: Godot's 21 unreferenced `assets/audio` clips are mapped, and music plays as stems.**
    - The clips live in `Content/Audio/Clips` under `res://assets/audio/<file>`. Import presets: SFX are decompress-on-load mono PCM, ambient beds (`ambient_*`, `reactor_hum`) stream as mono Vorbis, and music stems decompress on load so `PlayScheduled` is sample-exact.
    - `meta.hull.groan` now uses `hull_groan.wav` instead of the `breach_alarm.wav` stand-in.
    - Music: every stem is scheduled on one shared DSP time. Each stem follows its own layer gain from the session's `DynamicMusicState` at `-24 + gain × 24` dB, and is silent at zero gain. Godot collapsed the layers into one player at the loudest layer's level. `layer.combat_percussion` has no stream, so its stem stays silent.
    - The `IAudioSink` contract only carries the collapsed level, so `RunSessionHost.Boot` binds the session's `DynamicMusicState` and `AmbientZoneState` explicitly (`AudioManager.BindSessionModels`).
    - **Ambient beds are new.** The session's `AmbientZoneState` role now drives two crossfading ambient beds, at crossfade gain × role intensity × threat multiplier. Godot computed the role and gains but never played a bed.

    | Event id(s) | Clip |
    |---|---|
    | `sfx.work.weld`, `sfx.arc.zap` | `weld.wav` |
    | `sfx.work.cut` | `cut.wav` |
    | `sfx.work.patch`, `sfx.wound.bandage`, `sfx.wound.treat` | `patch.wav` |
    | `sfx.work.unbolt`, `sfx.work.pry`, `sfx.work.mount`, `ui.ship_mod.install`, `ui.ship_mod.uninstall` | `unbolt_pry.wav` |
    | `sfx.work.splice`, `sfx.tool.use` | `tool_use.wav` |
    | `sfx.work.harvest` | `pickup.wav` |
    | `sfx.work.plant`, `sfx.drop.item` | `drop.wav` |
    | `sfx.suit.breath` | `suit_breath.wav` |
    | `sfx.craft.complete` | `dock_land.wav` (assets variant, 1 s thunk) |
    | `sfx.repair.complete` | `door_close.wav` (assets variant, 0.5 s clunk) |
    | `meta.hull.groan` / `meta.reactor.hum` / `meta.biomatter.pulse` | `hull_groan.wav` / `reactor_hum.wav` / `biomatter_pulse.wav` |
    | `amb.docking` / `amb.engine` / `amb.cargo` / `amb.med_bay` / `amb.crew_quarters` | `ambient_docking` / `ambient_reactor` / `ambient_engineering` / `ambient_medical` / `ambient_corridor` |
    | `ui.inventory.open`, `ui.wounds.open`, `ui.ship_mod.open` / `ui.inventory.close` | existing `ui/panel_open.wav` / `ui/panel_close.wav` |

    - Three copied clips stay unmapped because their events already use Godot's `data/audio` versions: `footstep_metal`, `door_open` and `fire_crackle`.
    - Still silent (12; `AudioContentTests.ReportsEventsStillWithoutClips` lists them): `meta.beacon.distress`, `sfx.hallucination.whisper`, `sfx.sanity.ambient`, `sfx.sanity.hud_glitch`, `sfx.sanity.phantom`, `ui.chart.route`, `ui.load`, `ui.save`, `ui.objective.advance`, `ui.work.progress`, `voice.log.play` (voice-log files exist in neither engine) and `layer.combat_percussion`.

26. **Icons: `IconCatalog` (Runtime/Content, `Resources/Catalogs/IconCatalog.asset`, built by `IconCatalogBuilder`).**
    - `IconCatalog.Resolve(resPath, categoryHint)` returns the imported texture for `res://assets/ui/{status,achievements}/*.png`.
    - Item icon paths have no files in either engine, so they resolve to a generated placeholder. The choice goes: the hint's category, then the path folder (`materials` → raw, `loot` → unique), then the generic item placeholder.
    - Placeholders cover 16 categories (`Content/UI/Icons/items/placeholder_<category>.png`). Each unresolved path is logged once, at info level.
    - `UiIcons` wires it into the HUD status-effect chips, `AchievementsPanel` and the inventory rows.

27. **Template leftovers removed** (`Editor/Bootstrap/TemplateCleanup`, idempotent). The project-wide input actions are now `Content/Input/SynapticSea.inputactions`. Both URP assets use `SS_GlobalVolume` as their default volume profile. `Assets/InputSystem_Actions.inputactions` and `Settings/SampleSceneProfile.asset` are deleted.

28. **Wound healing rates were chosen without Godot data.** Godot never ticked `WoundState` (TickOrder "Port-added stages"). The port heals treated wounds at 0.004 severity/s and bandaged-only wounds at 0.001/s (`RunSession.WOUND_TREATED_HEAL_PER_SECOND` / `WOUND_BANDAGED_HEAL_PER_SECOND`); untreated wounds never heal.
    - They are tuning values, not parity values.
    - Wounds from combat damage always use the torso (`WoundState.SuggestFromDamage` default body part); see the open items.
29. **Hold-to-work.** `SettingsState.hold_to_tap` is read live (`RunSession.HoldToWorkEnabled`).
    - **Hold mode (default).** A work action progresses only while interact is held. Releasing pauses it without losing progress (`EndWorkHold`), and the next press on the same action resumes it (`BeginWorkHold` consumes the press).
    - **Tap mode.** A press starts the action and it runs on its own. The next press cancels it and progress is lost (`CancelWorkAction`).
    - Switching to tap mode mid-action releases the hold requirement.
    - Repair, seal and extinguish channels are separate interactables: they run once started, and cancel when the player leaves range (Godot).
30. **Generated New Run.** Title → New Run opens a setup for biome, difficulty and seed. `StartSceneBuilder.BuildHomeStart` then generates the home ship and writes `layout.json`, `gameplay_slice.json` and `blueprint.json` to `user://runs/<run_id>/`, so saves and Continue reload it by path.
    - **Start gate.** The gate is the stamped structural plan valid, objectives and a start room present, every objective and goal room walkable from the start room, and a life boat anchor.
    - **Anchor fallback.** The anchor is the dock room; with none (the legacy templates have none, which is why Godot's `StartSceneBuilder.Build` returns null), it falls back to the boarding/airlock cell.
    - **Reseed.** A rejected seed reseeds `seed+1` for up to 8 tries, with a warning.
    - **Tolerated warning.** `RoomAssigner`'s "guaranteed role 'dock' has no eligible zone" warning is logged at info level on this path only (`RoomAssigner.ToleratedMissingRoles`). Godot parity tests and derelict generation keep the warning. `RunLifecyclePlayModeTests` asserts that a generated boot logs no warnings or errors.
31. **Language is persisted only.** The language selector's choice is saved in `user://settings.json` and restored. Godot's `LocalizationCatalog` only has `en` and no UI string reads it, so nothing is translated.
32. **Affordance labels are on by default.** Godot built the slice's affordance world labels only when `debug_affordance_labels_enabled` was set. The port shows them (`AffordanceView.ShowAffordanceLabels = true`), projected into the HUD label layer, scaled by text scale and distance-culled; hazard labels are never culled.
33. **A bridge terminal of the ship the player already pilots does not claim interact** (`RunSession.TryBridgeTerminals`). Godot's `try_login` claimed every in-range press. The terminal sits on the command room's centre, which is also where `LifeboatLocalRepairPositions` puts the life boat's repair and fire suppression points, so those points could never be used. Logging in again changed nothing, so the press now falls through.
34. **PlayMode tests run the real loop.** No global threat clearing: a test that needs a quiet ship calls `QuietShip(reason)`.
    - Propulsion is made flyable through the repair, fire suppression and breach seal points, with parts and repair skill given as setup. The test does not use `ForceRepairAll`.
    - Hull integrity caps thrust (`PropulsionState`: target = 100 × (powered ratio − hull penalty)), so golden 001 also needs its breaches sealed before the drive reaches the 50 % travel threshold.
    - Long simulated waits run at `Time.timeScale = 20`.
    - An idle player still dies after a few minutes on every home ship (decision 56, open items), so no test waits idle for long.
35. **Manual slots restore world-only state (run schema `gate2-current-run-6`).** Godot's manual slot restored none of this; only its world save did.
    - The run snapshot's port extensions add `home_ship_carts`, `home_breach_environment`, `meta_progression_summary`, `unique_item_summary` and `visited_ships`. `SaveMigrationService` gives run-5 and older saves empty defaults, which mean "not saved" (`PortDefaults`).
    - `ApplyManualSlot` restores them after the reload through helpers shared with the world load. The world load is unchanged and ignores the embedded copies, so nothing is applied twice.
    - The breach environment is read without syncing ship state (`HomeBreachEnvironmentForSave`), so quick and auto saves do not mutate the ship.
    - The 42 Godot save reproductions are unaffected: `SavePortSchema.GodotView` strips the extensions.
36. **Finished run directories are deleted** (`Core/Systems/Save/RunDirectoryJanitor`). It removes `user://runs/<id>/` folders that no save payload's `layout_path` and no world save's home ship references.
    - It runs at the end of a run (`RunSession.EndRun`) and when the title builds.
    - A death keeps its own directory while frozen slots point at it. Nothing outside `user://runs` is touched.
    - `IStorage` gained `ListDirectories` and `DeleteDirectory`.
37. **Difficulty reaches electrical arcs, and the New Run choice is remembered.** These are tuning values, not parity values.
    - At home the arcing phase lasts 2.5 s × the hazard dial and the discharged window 1.5 s ÷ the dial (`RunSession.ArcConfig`; dial floored at 0.1). Standard difficulty and away ships keep Godot's timing.
    - Starting a New Run stores the setup's difficulty as the settings preference, saved once through `AppServices.ApplySettings` (`TitleScreen.RememberNewRunDifficulty`).
38. **Combat wounds pick a body part.** Godot never called `suggest_from_damage` from gameplay, so no body part was ever designed.
    - Weights: torso 50, arm 20, leg 20, head 10 (`RunSession.RollWoundBodyPart`).
    - The roll uses `GodotRandom` seeded from the run seed × 31 plus a per-session counter, which restarts on reload. The sequence is deterministic per boot but is not saved.
39. **Emergency life-support power floor** (`RunSessionDeps.HomeLifeSupportPowerFloor = 0.75`). Home life support ticks with at least this powered ratio while its own subsystems work, even when the `power` dependency is down.
    - At 0.75, recovery matches one open breach; 0.5 still let one breach foul the air.
    - Grid allocation and saved summaries are unchanged. The Godot session harness sets the floor to 0.0, so `PowerWearParityTests` still dies at 29.25 s.
    - Golden 001's idle death at 29 s is gone, but an idle player still dies at about 54 s: the engineering fire burns power to 0, radiation then drains health, and web damage multiplies the breaches. That cascade is an open design item.
40. **Crafting and production stations are placed by room role** (`Core/Session/StationPlacer`).
    - Each station kind has preferred room roles. It takes the first spot there whose 1.8 m interaction area overlaps no live interactable, other station or the player start, and that is not a doorway cell.
    - Otherwise it takes the first free legacy position (Godot's structural placement list).
    - On golden 001, five stations get rooms (maintenance, medbay, cargo, reactor ×2). Salvage, hydroponics and water recycler take free passage spots, because those rooms are full of objectives, loot and hub controls. The interaction order is unchanged (`docs/InteractionOrder.md`).
41. **Blocked-route node colliders follow their powered gate.** Godot's loader kept the `BlockedRoute_*` markers collidable after `restore_systems` opened the gates, so the route stayed physically closed.
    - `IShipLoaderView.SetBlockedRouteCollisionEnabled(index, enabled)` does the toggle.
    - Each gate stores `blocked_route_index`, which follows the `BlockedRoutes` list order, and `ApplyRouteGateSceneState` toggles that node with the gate.
42. **Affordances are built per ship, derelicts included, and props use imported visuals.**
    - `AffordanceView` keeps one set per loader root. `SessionEvents.AffordancesRebuilt` carries the root and is also raised when a derelict is boarded; `AffordancesCleared`, or the root being freed, drops the set.
    - Derelict labels use the prefix `affordance@<root>:`, and the home ship keeps `affordance:`. Clearing blocked props stays home-only.
    - Component markers mount the bound prop prefab (`RuntimePropVisualBinder.MountComponentVisual`; `visual_source` imported or fallback). Objective props try the objective binding first, as in Godot.
    - Godot's loader objective specs never carried `placement_id`, so its objective binding never fired. The view reads the id from the gameplay slice by sequence instead; adding the key to the specs would break the loader parity fixtures.
43. **The run results panel is styled** (`RunResultsPanel`, `.ss-results*` in `panels.uss`, `docs/ui-port-notes.md`). It shows:
    - a severity banner;
    - 44 px label/value rows, for tracked stats only;
    - an epitaph block on death;
    - the seed · biome · difficulty line (`SetContextLine`).
    `BodyText` keeps Godot's line list.
44. **`DataManifestValidator` lives in Core, not the Editor** (`Core/Services`). It checks that every data JSON parses and every `res://data` JSON reference resolves. `Builder` and `DataManifestTests` share it, and the dotnet suite still compiles it.
45. **The build stamp exists only during a build.** `Builder` writes `StreamingAssets/build_stamp.json` for the player and deletes it and its `.meta` afterwards. `BuildStampTests` fails if a stamp is left in the project, because the editor would otherwise report the last built kind.
46. **Test and headless check scripts.**
    - `tools/test.ps1` exits 8 when tests fail and 1 for infrastructure errors.
    - `tools/verify-headless.ps1 -Target -Kind` requires the boot line to report `build=<kind>`. Windows waits for the title-ready line (up to 30 s). Linux players run under WSL (`wsl --exec`, stopped by timeout, a crash exit code fails the check).
47. **itch publishing is a dry run by default** (`tools/publish-itch.ps1`). It stages the build without Unity's do-not-ship folders or debug symbols, then runs `butler validate` (butler 15.31.0 in `F:\Tools\butler`). It pushes only with `-Push -Project`. Channels are `win-rc`, `mac-rc` and `linux-rc`, plus `-demo`, from Godot's ITCH-017 names; the user version comes from the player's own stamp.
48. **Linux builds use Mono only.** Only `linux-mono` is installed; `Builder` falls back to Mono with a warning unless the linux-il2cpp variation exists.
49. **Steam is an inert drop-in** (`Platform/Steam`, asmdef `defineConstraints: SS_STEAM`).
    - Facepunch.Steamworks goes into a gitignored `Plugins/` folder by hand. It is never a package dependency, so native Steam libraries never enter normal builds.
    - Catalog achievement ids are the Steam names 1:1 (`PlatformAchievementIds`). `AchievementState.Unlocked` fires for new unlocks only.
    - The Steam assembly itself has not been compiled, because the plugin is absent.
50. **CI workflows are manual only** (`.github/workflows/unity-tests.yml`, `unity-build.yml`: game-ci v4, LFS checkout, Library cache, a license-free dotnet job). They are `workflow_dispatch` until the Unity license secrets exist.
51. **Tooling continuity.** The Godot repo's engine-agnostic Python tools live in `tools/python/`.
    - `synaptic_layout.py` maps Godot project paths onto StreamingAssets, Content, `fixtures/godot_wrappers` and `artifacts/_staging`; `res://` strings in data stay Godot's.
    - The Meshy governance chain runs only on POSIX (macOS, Linux or WSL), because it needs `fcntl` and file modes.
    - `tools/python/README.md` lists what was copied, replaced and dropped.
    - The Blender structural toolkit and `reexport_gltf_separate.py` (glTF Separate re-export of the `*_textured.glb` files) live in `tools/blender/`.
    - `Synaptic Sea/Content/Import Library Asset…` copies one `art-library` asset into `Assets/Content/Library/<category>/`.
52. **The system inventory stays Python** (`tools/python/build_system_inventory.py`, output in `docs/inventory/`). Each system's `file` is its C# port, `godot_file` keeps the Godot source, and `port_files` lists partials. It was imported once with `--from-godot` from the `// Ported from` headers. `--check` is the gate; `--coverage` is informational.
53. **Structural promotion is a Unity editor tool** (`Editor/Content/PromoteStructuralSource`, menu plus `-executeMethod`, `-dryRun`, `-force`).
    - It keeps the Python script's allowlist, strict contract rules, GLB header checks and hash skip.
    - The Godot overlay import is replaced by an AssetDatabase import smoke test in a disposable folder, then an atomic copy and a structural prefab rebuild.
    - The Blender export stays in Python.
54. **The Meshy runtime review renders in Unity** (`Editor/Content/MeshyRuntimeReview`, logic in `MeshyReviewEvidence`).
    - It reproduces the Godot capture: derelict, harness lighting, the locked (16,14,16) orthographic camera and the staged, contextual and non-blank pixel gates.
    - It writes the same 19 leaves with Python-canonical JSON, and the Python verifier re-derived identical evidence on a synthetic task.
    - Ceilings are hidden everywhere (decision 17). Evidence binding stays in Python.
55. **The boot moves a spawn that overlaps wall geometry** (`Runtime/Session/SpawnClearance`, `RunSessionHost.ResolveSpawnClearance`). The docked life boat keeps overlapping the home airlock, because that overlap is the docking connection: its repair, fire suppression and breach seal points are reachable from the home airlock only through it, and derelicts dock the same way.
    - What was wrong was the spawn: one of the boat's walls runs through the start marker, and the CharacterController pushed the player out on the first physics step.
    - After the session boots, if the player capsule overlaps a Structure, ZoneBlocker or Portal collider, the host searches rings 0.5 m apart, up to 4 m, for the nearest clear spot standing on the home ship's floor, and teleports the player there. The session still spawns at the start marker, so saves and headless parity are unchanged.
    - `BootBuildsTheGoldenShipPlayerCameraAndHud` asserts the move, a clear capsule over home floor, and no depenetration drift afterwards.
56. **The suit supplies the player's air while the home atmosphere is fouled** (`RunSessionDeps.HomeSuitAirReserveSeconds = 150`, `RunSession.ApplySuitAirReserve`).
    - Measured before the change: every generated New Run home ship (seeds 3, 17, 42, 777, 999, 7777; all biomes and difficulties) starts with 4–9 breaches, and an idle player died of the atmosphere in 24–30 s, threats or not, while the HUD's Suit O2 meter read 100 %. The power floor (decision 39) could not help, because the leaks outpace any recovery.
    - Now, while `LifeSupportState.GetHealthDrainPerSecond()` is above 0 at home, the suit O2 meter drains at severity × 100 / 150 s × the hazard dial, its regen is withheld, and the atmosphere does no health damage. With the suit empty the air hurts again. Away from home nothing changes.
    - The Suit O2 meter, its danger colour and the "O2 LOW" chip are the visible countdown; no new UI.
    - The Godot session harness sets the reserve to 0 (`SuitAirReserveTests.GodotHarness_KeepsTheSuitOutOfShipAtmosphere`), so parity tests and saves are unchanged.
    - After the change an idle player dies at about 100 s on every ship (86 s hardened), from the base survival clocks instead: radiation drains 1 HP/s from about 25–40 s at home, thirst empties at about 75 s (0.8/s, faster in a warm or cold ship), and sanity empties at about 67 s, adding 0.5 HP/s. Those are Godot's data-driven pacing values; retuning them is a playtest decision (open items).
57. **Ramps and upper decks stay as in Godot.** Only the hand-authored golden `coherent_ship_001` and `coherent_ship_003` have a second deck. Every generated layout, and so every New Run and derelict, is single-deck.
    - In both engines a ramp room joins deck 0 to the deck-1 cell 4 m directly above it, the ramp art rises 0.87 m, and the loader's `VisibleVerticalTransition_*` marker is a solid box in the middle. No path between decks exists in Godot or in the port; tests teleport to deck-1 objectives.
    - The sloped collider (decision 9) makes the art walkable; its high end stopping at 0.87 m is expected.
    - Walkable deck changes (a climb or lift interaction on `vertical_connections`) belong with multi-deck generation, when the generator produces decks.
58. **Multi-wing vertex wrappers are built at the cell centre they belong to** (`Core/Procgen/VertexWrapperPlacement`, applied by `StructuralLayoutBuilder`; 2026-09-17). Godot's `structural_edge_compiler` stamps `wall_inner_corner`, `wall_outer_corner` and `wall_t_junction` onto an EDGE record and leaves that edge's midpoint pose alone, but those three wrappers are authored around a CELL CENTRE, with whole 4 m wings 2 m out on the cell's north and east sides (the T-junction adds west). Every wing therefore landed half a cell off, leaving walls inside rooms: on golden `coherent_ship_001` a wing ran along z = 0 through the middle of `life_support_01`, blocking the player (a raycast hit it) and cutting 6 of 46 floor cells off any NavMesh built from the colliders. Godot's own captures carry the same misplacement, so this is an inherited bug, fixed under the user's "inherited gaps are bugs" rule.
    - The fix is a scene-build deviation, never a plan change: `StructuralPlanValidator` requires every edge placement to keep its canonical edge pose, and both the plan and the validator are compared bit-exact with Godot (`ProcgenStageParityTests`, `ProcgenValidatorParityTests`). Those suites are untouched.
    - A wrapper moves to the cell centre whose own edges its wings cover, yawed to match. The invariant: every wing lands on a SOLID non-portal edge that was getting geometry anyway, so no opening is sealed and no wall stands in open space. Edges a wing covers build no wrapper of their own, which keeps exactly one wall per edge; where no placement satisfies the invariant the edge takes a plain `wall_straight_1x1`.
    - On golden `coherent_ship_001` (New Run recompiles its plan) this leaves 9 outer and 2 inner corners placed, 11 edges covered, 5 T-junctions fallen back: no walls inside rooms, no duplicate coplanar walls, and all 46 floor cells reachable across a NavMesh built at agent radius 0.35.
    - The Godot loader-tree parity (`ShipSceneBuilderTests.LoadMatchesGodotLoader`) subtracts exactly this deviation from the capture before diffing; everything else stays exact. `VertexWrapperPlacementTests` checks the invariants against every capture layout and the shipped ship data.

## Light calibration (2026-09-11)

Method: `ScreenshotRunner.Sweep` renders one layout under several `(tonemap, dir, omni, ambient)` configs. `tools/image-luma.ps1` then compares the mean sRGB luma of matching 1920×1080 crops against `fixtures/godot/screens/`. Ceilings are hidden (`-ceilings hide`), as in the Godot captures.

Chosen constants: `DirectionalEnergyScale = 1.3`, `OmniEnergyScale = 2.5`, `AmbientEnergyScale = 1.0` (`AtmosphereApplier.Calibrated*`).

| Crop (x,y,w,h) | Lit by | Godot | Unity (final) |
|---|---|---|---|
| seed_000017_augmented (breach_field), full frame | everything | 70.8 | 60.7 |
| wall 420,540,100,80 | key + ambient + fog | 73.2 | 67.0 |
| wall 1560,560,60,90 | key + ambient + fog | 65.8 | 62.9 |
| block top 650,760,150,60 | key + ambient + fog | 70.9 | 76.9 |
| red-lit wall 1130,850,90,70 | dressing omni light | 67.0 | 62.7 |
| pink-lit wall 710,400,90,100 | dressing omni light | 70.8 | 70.0 |
| background 1500,150,150,100 | fog colour | 51.5 | 46.1 (vignette) |
| coherent_ship_001 (no biome), full frame | Godot default environment | 12.8 | 12.3 |
| floor 700,480,200,150 | clear-colour ambient | 6.9 | 6.4 |
| floor 1280,800,200,150 | clear-colour ambient | 7.0 | 7.4 |
| background 400,700,150,100 | clear colour | 13.4 | 12.4 |

Before calibration (ACES, contrast +10, sRGB ambient, scales 1.0, no ambient probe), the seed-17 frame was 26.2 and the coherent_ship_001 background was 0.

Why the scales are not 1:
- **Directional.** The best fit is 1.08 on horizontal tops and about 1.46 on walls. Godot's default diffuse is Burley; URP's is Lambert. 1.3 splits the difference.
- **Omni.** Godot's OmniLight3D falls off as 1/d (`omni_attenuation = 1`), URP's as 1/d², with the same range window. The ratio is d, and dressing lights sit about 2–3 m from the surfaces they light. The per-crop fits were 2.7 and 1.6.

The remaining full-frame gap is floors. Godot draws the GLBs' untextured `Collision_*` boxes on top of them (decision 8), which makes them lighter and gives the hatched z-fighting. Godot's key light also casts no shadows (`DirectionalLight3D` default), while URP's does, per the plan.

## Open items

- **Survival pacing needs a playtest pass.** With the suit reserve (decision 56), an idle player dies at about 100 s on every home ship (86 s hardened). The limit is the base survival clocks: radiation at home from about 25–40 s, thirst empty at about 75 s, sanity empty at about 67 s. Golden 001 adds its own cascade: the engineering fire burns power to 0 by 22 s and web damage raises breaches from 1 to 8 at about 35 s. Tune the vitals, sanity and radiation data against real play, not idle runs.
- **The docked life boat overlaps the home airlock by design** (decision 55). The overlap is the docking connection; the spawn is moved clear of its wall at boot.
- **Silent audio events.** 12 events are still silent (decision 25; `AudioContentTests.ReportsEventsStillWithoutClips`).
- **Publishing and platform setup that needs the owner** (step-by-step in `docs/release-setup.md`):
  - The itch project slug is unknown, and butler needs `butler login` or `BUTLER_API_KEY` before any `-Push`.
  - The CI workflows need the `UNITY_LICENSE`, `UNITY_EMAIL` and `UNITY_PASSWORD` secrets.
  - Linux IL2CPP is not installed.
  - macOS builds are unsigned and not notarised; that needs a Mac.
  - Steam needs the Facepunch plugin, `SS_STEAM`, `steam_appid.txt` and the 8 achievements created on Steamworks. Steam uploads are not scripted.
- **Tooling left to calibrate or finish:**
  - `MeshyRuntimeReview`'s pixel thresholds are Godot's and have only been checked on a synthetic task under URP.
  - `meshy_candidate_review bind` and the Python evidence verifier need POSIX file modes and a Blender re-import, so they do not run on Windows.
  - The Godot `tests/test_*.py` suites for the copied scripts were not ported.
  - The 15 `*_textured.glb` files have not been re-exported to glTF Separate.
- **Running Unity on a worktree dirties `ProjectSettings`** (the app-ui config object and the `APP_UI_EDITOR_ONLY` define). It is package noise; do not commit it.
- `PlayableScenePlayModeTests` taps keys with queued `KeyboardState` events: `InputTestFixture.Press` has no keyboard state pointer in the batch-mode editor.
- GPU Resident Drawer first frame. The GRD applies renderer and material changes only in its player-loop hook (`GPUResidentDrawer.PostPostLateUpdate` → `m_WorldProcessor.Update()`, injected before `PostLateUpdate.FinishFrameRendering`).
  - An editor script that builds a scene and calls `Camera.Render()` synchronously bypasses that hook, so the first capture can use stale instance data. `ScreenshotRunner` keeps its double render.
  - Games are unaffected: the hook runs every frame after `LateUpdate`, so runtime material changes land in the same frame.
- The Godot-side exporter and screenshot scripts live on the `unity/parity-fixtures` branch of the Godot repo (pushed to origin 2026-09-17, not merged into Godot `main`).
