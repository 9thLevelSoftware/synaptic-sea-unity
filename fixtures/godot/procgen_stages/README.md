# Procgen stage fixtures (Wave 2, group E2)

Godot 4.7.1 reference outputs for the procgen pipeline stages ported in `SynapticSea/Assets/_Project/Core/Procgen/`:
`TemplateSelector`, `RoomAssigner`, `RoomGraphGenerator`, `StructuralPlacer`, `EncounterInjector`,
`StructuralEdgeCompiler` and `FirstRunContract`. `Tests/EditMode/Procgen/ProcgenStageParityTests.cs` replays every
case with the same inputs and compares the canonical output type-aware and bit-exact (`TreeDiff`).

| File | Contents |
|---|---|
| `template_selector.json` | `select` / `select_with_options` picks for 10 seeds, the parsed 14 templates, pool lists |
| `room_assigner.json` | room plans: 14 templates x 5 archetypes (none + 4 authored) x sizes 1-2 x seeds 7, 42 x (no selector, `dead_fleet` selector); `normalize_archetype` |
| `room_graph_generator.json` | `RoomGraph.to_dict()` for 5 archetypes x 3 sizes x 10 seeds |
| `structural_placer.json` | `place_structure()` room nodes (name, position) + module lists for the same graphs, biome `""` and `breach_field` |
| `encounter_injector.json` | `inject()` + `validate()` on the `fixtures/godot/procgen` capture layouts: recipe replay for all 14, and for 4 layouts a biome x difficulty x seed grid plus no-critical-path / no-cells / Vector2i-cells variants |
| `structural_edge_compiler.json` | `compile()` of a perturbed capture layout (HATCH / BREACH / "open" portals, declared module ids, fallback kit) and of malformed layouts (error paths), with occupancy / edge key order |
| `first_run_contract.json` | `validate()` / `pick_seed()` over the capture layouts and slices |

Inputs are the committed captures in `fixtures/godot/procgen/` (layouts are read with `JSON.parse_string` on both
sides), the game data under `data/` (mirrored in `StreamingAssets/data`), and the literal seeds in the exporter.
Values are canonicalized like `scripts/validation/procgen_structural_debug_export.gd::_canonical_value`
(`Vector2i` -> `[x, y]`, `Vector3` -> `[x, y, z]`) and written with `JSON.stringify(v, "", true, true)`
(sorted keys, full precision, compact). Dictionary insertion order that later code iterates is exported
explicitly (`key_order`, `occupancy_keys`, `edge_keys`).

The plain compile of each capture layout is not exported: the test checks it against the `structural_plan`
already embedded in each `fixtures/godot/procgen/layout_*.json` (JSON cells and `Vector2i` cells).

## Regenerating

Never run the exporter inside `D:\the-synaptic-sea` (Godot writes `.godot/` caches). Use a scratch project:

```sh
# bash; SRC = Godot repo at 96ecb2b0, P = scratch project, REPO = this repo
mkdir -p "$P/scripts" "$P/data"
cp -r "$SRC/scripts/procgen" "$P/scripts/"
cp -r "$SRC/data/procgen" "$SRC/data/placement" "$SRC/data/kits" "$P/data/"
printf 'config_version=5\n\n[application]\nconfig/name="procgen stages"\nconfig/features=PackedStringArray("4.7")\n' > "$P/project.godot"
cp "$REPO/fixtures/godot/procgen_stages/export_procgen_stage_fixtures.gd" "$P/"
GODOT=F:/Tools/Godot/Godot_v4.7.1-stable_win64_console.exe
"$GODOT" --headless --path "$P" --import          # builds the class_name cache once
"$GODOT" --headless --path "$P" --script res://export_procgen_stage_fixtures.gd -- \
    --out "$REPO/fixtures/godot/procgen_stages" --fixtures "$REPO/fixtures/godot/procgen"
```

`StructuralPlacer` prints one "module not found" error per module (the wrapper scenes are deliberately not copied;
the room nodes are still built) and one deprecation warning per call; `RoomAssigner` warns when a template has no
zone for a guaranteed role. Both are expected.

Add `--full` (and a scratch `--out`) for the broad validation sweep: all 10 seeds, sizes 0-2, four selector modes,
the encounter grid on all 14 layouts with 3 seeds, and every compiler variant on all 14 layouts (about 50 MB,
tab-indented). Replay it with `PROCGEN_STAGES_DIR=<out> dotnet test --filter ProcgenStageParity`.
