# Unity port — status and decision log

Living companion to `docs/unity-port-plan.md`. The plan is the intent; this file records what exists and every place the implementation deliberately departs from the plan.

## Status

| Phase | State | Evidence |
|---|---|---|
| 0 Bootstrap | Done | Unity 6000.6.0f1 URP project, git + LFS, packages (glTFast 6.20, Newtonsoft 3.2, Input System 1.20), layers and collision matrix, IL2CPP/Mac Mono modules, VS Build Tools, .NET 8, Python 3.12 (all under `F:\Tools`). Godot 4.7.1 fixtures captured (`fixtures/`). |
| 1 Kernel | Done | Bit-exact against Godot for RNG, hashing, float formatting, and JSON (`KernelParityTests`) |
| 3 Wave 1 models (107 files, no dependencies) | Done | Merged; tick traces for oxygen, radiation, sanity, web infestation, and hydroponics match Godot bit-exactly; LootRoller matches all 50 Godot rolls |
| 3–5 Wave 2–3 models | Done | Merged; all 11 Godot model tick traces match bit-exactly, 42 Godot save files reproduce byte-for-byte |
| 4 Procgen chain | Done | All 14 end-to-end Godot layout recipes regenerate exactly (layout and gameplay slice, raw text hashes included); validator verdicts on 18 layouts plus 8 broken plans, determinism hashes, life boat, start scene, component placement and work-action traces all match |
| 6 RunSession | Done | `playable_generated_ship.gd` split into engine-free `Core/Session` (RunSession partials, tick stages with Godot's home and away orders, InteractionRegistry, snapshot assemblers, scene ports, 17 interactables). A headless golden-001 session boots, completes objectives 1–4, saves and reloads; a replayed Godot capture writes all 4 save files exactly (`docs/TickOrder.md`, `docs/InteractionOrder.md`) |
| 7 Content pipeline | Done (first pass) | 15 structural prefabs, 26 prop prefabs, catalogs, frame convention verified on 41 authored sockets |
| 8 Runtime scene layer | Loader done | `ShipSceneBuilder` builds wrappers, markers, portals, zones, props, dressing and objective volumes, matching Godot loader fixtures; interaction sensors and session host wait on RunSession |
| 9 Rendering | Done (first pass) | URP Forward+, SSAO, decals, global volume; `SS_LitDitherFade` ceiling fade; hallucination full-screen pass; 4 VFX prefabs; light levels calibrated against the Godot captures (below) |
| 10 UI | Done (first pass) | All 30 `scripts/ui` files ported as UI Toolkit presenters built to the UI presentation spec, with MenuCoordinator and ModalStack; `HudLayoutTests` enforces HUD coverage, protected zones and text sizes at three resolutions and three text scales. Wiring and the gamepad PlayMode journey wait on the session scene (`docs/ui-port-notes.md`) |
| 11 Audio and input | Done (first pass) | AudioManager port with per-bus volumes; input actions mirror the Godot InputMap; typed wrapper generated |
| 12 Builds and tooling | Started | Windows dev (Mono) and release (IL2CPP) and macOS dev (Mono, unsigned) builds pass; `tools/verify-headless.ps1` smoke-launches the Windows player clean; `tools/test.ps1 -Mode All` runs dotnet, EditMode and PlayMode; fixture exporter on the local `unity/parity-fixtures` Godot branch. Build Settings list Boot → Title → Playable (`BuildSettingsSetup`; the Builder skips a missing scene with a warning); Boot composes `AppServices` and loads Title (`FrontEndSceneBuilder`, `FrontEndPlayModeTests`). |

Run everything with `pwsh tools/test.ps1` (dotnet Core suite, then Unity EditMode).

## Decisions that differ from the plan

1. **Procgen lives in the Core assembly** (`Core/Procgen`, namespace `SynapticSea.Core.Procgen`). Two system models depend on procgen files, so a separate Procgen assembly would create a reference cycle.
2. **Variant types are `GdDict` / `GdArray`**, not `JsonObject` / `JsonArray`. The names mirror Godot `Dictionary` / `Array` and avoid clashing with `System.Text.Json`. Keys are Variants (int and float keys are distinct), matching Godot.
3. **Core has its own JSON reader and writer; Newtonsoft is not used in Core.** Godot parses every JSON number as a float using its own strtod, which is not correctly rounded. `GodotStrtod` is a port of that parser, so game data loads to the same doubles as in Godot. Test fixtures use an opt-in exact parse instead.
4. **Float text uses a port of Godot's `String::num`**, not .NET formatting. Mono's "F" formatting caps at 15 significant digits. The digit rounding follows decision 14.
5. **Two float RNG families.** `RandomNumberGenerator.randf()` (float32 with a clz exponent) differs from global `randf()` (`rand() / UINT32_MAX`). Both are ported. `Array.sort_custom` uses a port of Godot's unstable introsort (`GdSort`), because tie order affects procgen.
6. **Frame convention.** glTFast mirrors X, so Godot `(x, y, z)` maps to Unity `(-x, y, z)` and Godot yaw `a` to Unity `-a`. Lights and cameras get an extra 180° yaw, because Godot lights face −Z and Unity lights face +Z. `Runtime/Adapters/Frame.cs` is the only conversion point.
7. **Collision comes from the Godot wrapper scenes, sockets from the placement contracts.** The kit's `collision_proxy_records` are Z-up Blender boxes and are wrong. The wrapper `Marker3D` sockets all sit at the origin.
8. **The GLBs' `Collision_*` mesh nodes are hidden.** Godot rendered them as untextured boxes. They are collision proxies, not art.
9. **Known Godot data defects are kept for parity.** The pillar's `prop_anchor_up_01` and `prop_anchor_down_01` contract sockets carry Z-up values. The ramp, pillar, bulkhead, and ceiling wrappers use 1 m placeholder collision cubes.
10. **The Unity prefab lookup is `KitPrefabCatalog`.** The Godot `kit_catalog.gd` port is `Core.Procgen.KitCatalog`.
11. **No AudioMixer asset yet.** Unity has no public API to create one, and Godot's buses only used volume and mute. The runtime applies per-bus volumes itself; a mixer can be added by hand later for effects.
12. **The seed-17 smoke layout in the Godot repo came from the Rust worldgen.** The GDScript-pipeline target is `fixtures/godot/procgen/layout_s17_medium_pristine.json`. The Rust generator has no source, so Unity uses the GDScript pipeline, and Windows-Godot derelict layouts will differ for the same seed.
13. **String helpers are consolidated in `GdString`.** Wave 1 ports carry equivalent private `*Compat` helpers; new code uses the kernel class.
14. **Float text follows Godot's Windows builds, on every platform.** Official Windows Godot prints through the Microsoft C runtime printf: 17 significant digits (ties toward zero), zeros after that, then half-up rounding to the requested decimals. `GdFloatFormat.FormatFixed` reproduces this and matches 4,202 captured Godot outputs (`fixtures/godot/kernel/float_format_msvcrt.json`). Save and layout text therefore matches what Windows Godot wrote.
15. **No `GdScriptPipelineSource` class.** The GDScript generation pipeline is the fallback inside `ShipGenerator` when no `IDerelictLayoutSource` is set.
16. **Tonemapping is Neutral, not ACES, and the colour adjustments are neutral** (contrast 0, saturation 0). Godot rendered with linear tonemapping. URP's ACES toe crushed the Godot palette: the clear colour went to black and mid-tones fell by about 55%. That could not be recovered with light scales. Neutral stays within about 10% of the Godot captures and still rolls off HDR emissives for bloom. Vignette 0.25 and bloom are unchanged.
17. **The ceiling fade is an opaque dither, and it applies the rule Godot only documented.** `SynapticSea/LitDitherFade` (`Content/Shaders/SS_LitDitherFade.shader`) is hand-written HLSL, not a Shader Graph. It clips against an 8×8 screen-space Bayer matrix in the forward, ShadowCaster, DepthOnly, and DepthNormals passes, so depth, SSAO and shadows stay valid. Its properties sit in `UnityPerMaterial` (SRP Batcher), and the passes include `DOTS.hlsl` (GPU Resident Drawer).
    - `CeilingFadeController` swaps `sharedMaterials` to one shared material per (source material, fade). The Godot rule has two values (1.0 within 12 m, 0.15 beyond), so two materials per source cover it. The swap happens only when a ceiling crosses the radius. MaterialPropertyBlocks are not used because the GPU Resident Drawer does not support them. There is no smoothing, because Godot had none.
    - Godot defect not kept: `_apply_alpha` only visits direct `MeshInstance3D` children of `Ceiling_*`, which has none, so Godot ceilings never actually faded.
    - The `_Fade` dither also applies in the ShadowCaster pass, so a faded ceiling casts about 15% of its shadow.
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
    - The run handoff is `Runtime/Session/RunLaunchRequest.Pending` (mode NewRun / Continue / LoadSlot, slot id, seed 17, `breach_field`, difficulty from settings, meta-selected class, and the settings summary only when changed at the title), then `SceneManager.LoadScene("Playable")`. `RunReturnInfo` carries a failure reason or run outcome back to the title.

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

- Loading a Godot run save and building it again differs on 22 known paths (Godot's JSON load turns nested ints into floats; power grid, propulsion and sustenance summaries are recomputed; crafting stations re-register; the caption queue is not restored). `SessionSaveParityTests` pins that list. Not yet confirmed against a Godot load-then-save.
- Damaged power subcomponents wear from 0.2 to about 0.04 over simulated seconds in the headless session; unverified against Godot.
- Runtime `ShipView` must implement the session's `IShipLoaderView` port; the session host, interaction sensors and threat views are next.
- `StartSceneBuilder.Build` returns null for every seed tried, in Godot too: the legacy template pool generates derelicts without a dock room. Ported as is; needs a design decision.
- Ramp collision needs a sloped collider (Godot used a placeholder cube).
- Biome-less layouts in the game need `AtmosphereApplier.ApplyGodotDefaultEnvironment()` from the composition root (or `ShipSceneBuilder` when the layout names no biome). So far only `ScreenshotRunner` calls it.
- VFX are not wired into gameplay yet: use `VfxCatalog.Spawn(id, parent, godotPosition)`. The hooks are listed in each catalog entry's `usage`.
- GPU Resident Drawer first frame. The GRD applies renderer and material changes only in its player-loop hook (`GPUResidentDrawer.PostPostLateUpdate` → `m_WorldProcessor.Update()`, injected before `PostLateUpdate.FinishFrameRendering`).
  - An editor script that builds a scene and calls `Camera.Render()` synchronously bypasses that hook, so the first capture can use stale instance data. `ScreenshotRunner` keeps its double render.
  - Games are unaffected: the hook runs every frame after `LateUpdate`, so `CeilingFadeController`'s swaps land in the same frame.
- The legacy per-state albedo tint on damaged single-visual wrappers is not ported (decision 19).
- The Godot-side exporter and screenshot scripts live on the local, unpushed `unity/parity-fixtures` branch of the Godot repo.
- Title → Playable: the Playable bootstrap must consume `RunLaunchRequest` and, on failure or return, set `RunReturnInfo` and load Title. After merging Playable, re-run `BuildSettingsSetup` (or any build) so its scene GUID is recorded.
