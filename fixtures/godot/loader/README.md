# GeneratedShipLoader parity fixtures

Captured from the Godot 4.7.1 `GeneratedShipLoader` (`scripts/procgen/generated_ship_loader.gd` @ `96ecb2b0`) by
`scripts/validation/parity_fixtures/parity_loader.gd` on the `unity/parity-fixtures` branch
(`F:\work\godot-parity-fixtures`). Consumed by:

- `Tests/EditMode/Procgen/GeneratedShipLayoutParityTests.cs` (dotnet + Unity): the pure half (`GeneratedShipLayout`).
- `Tests/EditModeUnity/ShipSceneBuilderTests.cs` (Unity): the full `ShipSceneBuilder` load, including scene nodes.

## Regenerate

```
F:\Tools\Godot\Godot_v4.7.1-stable_win64_console.exe --headless --path F:/work/godot-parity-fixtures ^
  --script res://scripts/validation/parity_fixtures/parity_loader.gd -- --out F:/work/port-runtime-loader/fixtures/godot/loader
```

The script prints `PARITY LOADER PASS files=13` (the `push_error` lines before it come from the deliberate failure
cases).

## Files

| File | Content |
|---|---|
| `<case>_home.json` / `<case>_away.json` | One `load_from_paths(layout, data/kits/ship_structural_v0.json, gameplay_slice, is_away)` per case: the `ship_loaded` summary (paths made repo-relative), every public getter (objective / loot / placed-prop / portal specs, room centers / roles / decks, critical path, links, encounters, landmarks, zone markers + specs, radiation segments, authored atmosphere specs, room variant descriptors, `count_collision_shapes`), point probes of `get_radiation_zone_at` / `get_authored_atmosphere_at` / drain multiplier, and the scene nodes (landmark / blocked-route / vertical-transition markers, placed props, portals, objective volumes, trigger volumes, `DressingVisuals` children, module keys, integrity states, vertical links, atmosphere). |
| `failures.json` | `load_from_documents` failure reasons for eight broken inputs derived from `coherent_ship_001`. |
| `inputs/seed_000017_augmented.*.json` | seed_000017 augmented with room variants (dressing lights / fog / RNG props), atmosphere rooms, radiation / breach / fire / arc zones, landmarks, blocked links, module damage, placed props (primitive, imported binding, unknown, rotation fallbacks) and a slotted loot container. Written with `sort_keys=false`, then loaded back from disk by both engines. |

Cases: golden `coherent_ship_001..003`, `procgen/smoke/seed_000017`, and `seed_000017_augmented`, each with
`is_away` false (`_home`) and true (`_away`).

## Reference renders

`fixtures/godot/screens/seed_000017_augmented_godot_iso_{gameplay,overview}.png` show the augmented case (room
dressing lights / fog / props, zones, placed props) rendered by the Godot loader; compare with the Unity
`ScreenshotRunner` output for the same inputs (`-layout <abs>/inputs/seed_000017_augmented.layout.json -slice
<abs>/inputs/seed_000017_augmented.gameplay_slice.json -stem seed_000017_augmented`). Regenerate (needs a window):

```
F:\Tools\Godot\Godot_v4.7.1-stable_win64_console.exe --path F:/work/godot-parity-fixtures ^
  --script res://scripts/validation/parity_fixtures/capture_loader_screenshot.gd -- ^
  --layout F:/work/port-runtime-loader/fixtures/godot/loader/inputs/seed_000017_augmented.layout.json ^
  --slice F:/work/port-runtime-loader/fixtures/godot/loader/inputs/seed_000017_augmented.gameplay_slice.json ^
  --stem seed_000017_augmented --out F:/work/port-runtime-loader/fixtures/godot/screens
```

## Conventions

- Vectors are `[x, y, z]` in **Godot's frame**, local to the loader node. Unresolved positions (`Vector3.INF`) are
  written as `1e99999` and parse to `+Infinity`; an unresolved room center is `null`.
- Node transforms are `{name, position, basis: [x_axis, y_axis, z_axis]}` (basis columns). The Unity test converts its
  transforms back through `Frame.ToGodot` / `Frame.ToGodotBasis`.
- Files are full-precision JSON (`JSON.stringify(v, "\t", true, true)`) with sorted keys; ints stay ints.
- Known divergence: `failures.json` case `unknown_structural_module` is rejected by Godot's `StructuralPlanValidator`
  (not ported yet); Unity rejects it in the wrapper preflight instead (`structural wrapper preflight failed`).
