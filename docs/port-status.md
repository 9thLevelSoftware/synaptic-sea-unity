# Unity port — status and decision log

Living companion to `docs/unity-port-plan.md`. The plan is the intent; this file records what exists and every place the implementation deliberately departs from the plan.

## Status

| Phase | State | Evidence |
|---|---|---|
| 0 Bootstrap | Done | Unity 6000.6.0f1 URP project, git + LFS, packages (glTFast 6.20, Newtonsoft 3.2, Input System 1.20), layers and collision matrix, IL2CPP/Mac Mono modules, VS Build Tools, .NET 8, Python 3.12 (all under `F:\Tools`). Godot 4.7.1 fixtures captured (`fixtures/`). |
| 1 Kernel | Done | Bit-exact against Godot for RNG, hashing, float formatting, and JSON (`KernelParityTests`) |
| 3 Wave 1 models (107 files, no dependencies) | Done | Merged; tick traces for oxygen, radiation, sanity, web infestation, and hydroponics match Godot bit-exactly; LootRoller matches all 50 Godot rolls |
| 3–5 Wave 2–3 models | Done | Merged; all 11 Godot model tick traces match bit-exactly, 42 Godot save files reproduce byte-for-byte |
| 4 Procgen chain | In progress | Stages match Godot; the generator, serializer, validator and start scene builder are being ported |
| 6 RunSession | In progress | Coordinator split into Core/Session with both tick orders |
| 7 Content pipeline | Done (first pass) | 15 structural prefabs, 26 prop prefabs, catalogs, frame convention verified on 41 authored sockets |
| 8 Runtime scene layer | Loader done | `ShipSceneBuilder` builds wrappers, markers, portals, zones, props, dressing and objective volumes, matching Godot loader fixtures; interaction sensors and session host wait on RunSession |
| 9 Rendering | In progress (ceiling fade, VFX, hallucination FX, calibration) | URP Forward+, SSAO, decals, global volume; first renders match Godot's silhouette and orientation |
| 10 UI | In progress | 29 panels plus MenuCoordinator on UI Toolkit |
| 11 Audio and input | Done (first pass) | AudioManager port with per-bus volumes; input | actions mirror the Godot InputMap; typed wrapper generated |
| 12 Tooling | Started | `tools/test.ps1`, `tools/sync-godot-data.ps1`, fixture exporter on the local `unity/parity-fixtures` Godot branch |

Run everything with `pwsh tools/test.ps1` (dotnet Core suite, then Unity EditMode).

## Decisions that differ from the plan

1. **Procgen lives in the Core assembly** (`Core/Procgen`, namespace `SynapticSea.Core.Procgen`). Two system models depend on procgen files, so a separate Procgen assembly would create a reference cycle.
2. **Variant types are `GdDict` / `GdArray`**, not `JsonObject` / `JsonArray`. The names mirror Godot `Dictionary` / `Array` and avoid clashing with `System.Text.Json`. Keys are Variants (int and float keys are distinct), matching Godot.
3. **Core has its own JSON reader and writer; Newtonsoft is not used in Core.** Godot parses every JSON number as a float using its own strtod, which is not correctly rounded. `GodotStrtod` is a port of that parser, so game data loads to the same doubles as in Godot. Test fixtures use an opt-in exact parse instead.
4. **Float text uses a port of Godot's `String::num`** with exact round-half-even. Mono's "F" formatting caps at 15 significant digits.
5. **Two float RNG families.** `RandomNumberGenerator.randf()` (float32 with a clz exponent) differs from global `randf()` (`rand() / UINT32_MAX`). Both are ported. `Array.sort_custom` uses a port of Godot's unstable introsort (`GdSort`), because tie order affects procgen.
6. **Frame convention.** glTFast mirrors X, so Godot `(x, y, z)` maps to Unity `(-x, y, z)` and Godot yaw `a` to Unity `-a`. Lights and cameras get an extra 180° yaw, because Godot lights face −Z and Unity lights face +Z. `Runtime/Adapters/Frame.cs` is the only conversion point.
7. **Collision comes from the Godot wrapper scenes, sockets from the placement contracts.** The kit's `collision_proxy_records` are Z-up Blender boxes and are wrong. The wrapper `Marker3D` sockets all sit at the origin.
8. **The GLBs' `Collision_*` mesh nodes are hidden.** Godot rendered them as untextured boxes. They are collision proxies, not art.
9. **Known Godot data defects are kept for parity.** The pillar's `prop_anchor_up_01` and `prop_anchor_down_01` contract sockets carry Z-up values. The ramp, pillar, bulkhead, and ceiling wrappers use 1 m placeholder collision cubes.
10. **The Unity prefab lookup is `KitPrefabCatalog`.** The Godot `kit_catalog.gd` port is `Core.Procgen.KitCatalog`.
11. **No AudioMixer asset yet.** Unity has no public API to create one, and Godot's buses only used volume and mute. The runtime applies per-bus volumes itself; a mixer can be added by hand later for effects.
12. **The seed-17 smoke layout in the Godot repo came from the Rust worldgen.** The GDScript-pipeline target is `fixtures/godot/procgen/layout_s17_medium_pristine.json`. The Rust generator has no source, so Unity uses the GDScript pipeline, and Windows-Godot derelict layouts will differ for the same seed.
13. **String helpers are consolidated in `GdString`.** Wave 1 ports carry equivalent private `*Compat` helpers; new code uses the kernel class.

## Open items

- Calibrate `AtmosphereApplier.DirectionalEnergyScale` / `OmniEnergyScale` against Godot renders once room lights are ported.
- Ramp collision needs a sloped collider (Godot used a placeholder cube).
- The Godot-side exporter and screenshot scripts live on the local, unpushed `unity/parity-fixtures` branch of the Godot repo.
