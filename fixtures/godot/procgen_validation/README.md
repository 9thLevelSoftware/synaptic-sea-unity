# Procgen validation fixtures (L2-L6 + pipeline systems)

Godot 4.7.1 reference data for the procgen ports that the capture layouts in `fixtures/godot/procgen/` do not cover
directly. Replayed by `SynapticSea/Assets/_Project/Tests/EditMode/Parity/`:

| File | Exporter | Test | Contents |
|---|---|---|---|
| `validator_verdicts.json` | `export_validator_verdicts.gd` | `ProcgenValidatorParityTests` | StructuralPlanValidator verdicts + WalkabilityContract enclosure/standing adjacency, flood, start-goal reachability, standing path, void reason and per-edge capsule results for golden coherent_ship_001-003 (compiled and embedded plans), every `fixtures/godot/procgen` recipe regenerated through ShipLayoutGenerator (stamped and recompiled plans) and the `procgen_walkability_smoke` layout; fail-closed verdicts for eight tampered plans |
| `pipeline_extras.json` | `export_pipeline_extras.gd` | `ProcgenExtrasParityTests` | LifeBoatBuilder.build_layout (3 biomes) + build_graph; StartSceneBuilder data path (seeds 7, 42); ShipGenerator data path (FNV-1a of the exact layout.json / gameplay_slice.json texts written for the loader); ComponentPlacementState populate / link_ship_systems / mount / dismount over 7 capture layouts and the 3 goldens; PillarPersistence pack/unpack; a WorkActionDriver trace mirroring `work_action_driver_smoke.gd` |

The other pipeline gates need no new files: `ProcgenPipelineParityTests` (14 recipes vs `fixtures/godot/procgen`
layouts + gameplay slices), `ProcgenDeterminismHashTests` (`fixtures/godot/kernel/fnv1a_fixture.json`) and
`ProcgenStructuralDebugParityTests` (`fixtures/godot/procgen/structural_debug/seed_*`).

Values are canonicalized (`Vector2i` -> `[x, y]`, `Vector3` -> `[x, y, z]`) and written with
`JSON.stringify(v, "", true, true)` (sorted keys, full precision).

## Regenerating

Use a scratch copy of the Godot project (never run inside `D:\the-synaptic-sea`, Godot writes `.godot/` caches). Both
exporters load ShipGenerator / StartSceneBuilder / the systems, which preload most of `scripts/`, so copy it whole:

```sh
# bash; SRC = Godot repo at 96ecb2b0, P = scratch project, REPO = this repo
mkdir -p "$P"
cp -r "$SRC/scripts" "$SRC/data" "$P/"
printf 'config_version=5\n\n[application]\nconfig/name="procgen validation"\nconfig/features=PackedStringArray("4.7")\n' > "$P/project.godot"
cp "$REPO"/fixtures/godot/procgen_validation/*.gd "$P/"
GODOT=F:/Tools/Godot/Godot_v4.7.1-stable_win64_console.exe
"$GODOT" --headless --path "$P" --import          # builds the class_name cache once
"$GODOT" --headless --path "$P" --script res://export_validator_verdicts.gd -- \
    --out "$REPO/fixtures/godot/procgen_validation/validator_verdicts.json" --fixtures "$REPO/fixtures/godot/procgen"
"$GODOT" --headless --path "$P" --script res://export_pipeline_extras.gd -- \
    --out "$REPO/fixtures/godot/procgen_validation/pipeline_extras.json" --fixtures "$REPO/fixtures/godot/procgen"
```

Expected noise: GameplaySliceBuilder prints `slot_fallback` lines and RoomAssigner warns about guaranteed roles
without an eligible zone.

## Known kernel divergence

Godot 4.7.1 (Windows) writes some 15-decimal JSON floats one digit higher than the kernel's `GdFloatFormat`
(0.6709878396987915 -> Godot `0.670987839698792`, kernel `0.670987839698791`; the exact binary value is
0.67098783969879149946...). It affects only the non-full-precision texts of layouts with wreck `module_damage`
amounts; the tests compare those amounts at full precision and hash the rest of the text.
