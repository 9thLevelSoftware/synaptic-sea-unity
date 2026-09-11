# Godot parity fixtures

These are one-time captures from the original Godot 4 GDScript game ("The Synaptic Sea" / "The Sargasso of Stars"). The Unity C# port uses them to show it produces the same outputs as the Godot code. Everything under `godot/` comes from a single exporter run. `golden/` and `godot_wrappers/` are plain copies of files already checked into the Godot repo.

| | |
|---|---|
| Godot binary | `F:/Tools/Godot/Godot_v4.7.1-stable_win64_console.exe` (`4.7.1.stable.official.a13da4feb`) |
| Source repo sha (game code under test) | `96ecb2b002b93be6907b25ed7d20839e24b81869` (`main`) |
| Exporter | `scripts/validation/export_parity_fixtures.gd` + `scripts/validation/parity_fixtures/*.gd`, committed on the Godot repo's local branch `unity/parity-fixtures` (on top of `96ecb2b0`; the exporter commit adds only validation scripts) |
| Captured | 2026-09-11, UTC time is in `godot/meta.json` (`captured_at_utc`) |

## Layout

```
fixtures/
  godot/                      exporter output (meta.json lists every file)
    meta.json                 Engine.get_version_info(), sha, binary, UTC time, stage status, file list
    kernel/                   engine-level behaviour the port must match bit-for-bit
    procgen/                  pure-GDScript procgen layouts + gameplay slices + recipes
    procgen/structural_debug/ unmodified procgen_structural_debug_export.gd output
    loot/                     LootDistribution / LootRoller rolls for every loot table
    models/                   pure RefCounted model tick + save round-trip fixtures
    save/                     real on-disk save files written by the game's save code
  golden/                     copy of data/procgen/golden/** (git blob bytes, LF)
    smoke_seed_000017/        copy of data/procgen/smoke/seed_000017/**
  godot_wrappers/ship_structural_v0/   scenes/wrappers/structural/ship_structural_v0/*.tscn + *.manifest.json
```

### JSON conventions

- Every exporter JSON is `JSON.stringify(value, "\t")`. That is Godot's default: `sort_keys=true` and `full_precision=false`. Before writing, engine types are canonicalised: `Vector2i`/`Vector3`/`Vector2`/`Vector3i` become arrays, `StringName` becomes a string, and Dictionary keys become sorted strings. This follows `_canonical_value` in `procgen_structural_debug_export.gd`.
- The default Godot JSON writer is **lossy for floats**: it keeps about 15 significant digits. When the default text would not parse back to exactly the same float64 values, the exporter also writes a `<name>.fullprec.json` companion. That file holds the same document written with `full_precision=true`. Model fixtures always get a companion. Use the `.fullprec.json` file whenever you compare floats exactly.

### `godot/kernel/`

- `rng_fixture.json` (+ `.fullprec`): `RandomNumberGenerator` output for seeds `0, 1, 17, 42, 777, 999, 7777, 2147483647, 12345678901`. It records `state` after seeding and 64 values each of `randi`, `randf`, `randi_range(1,100)`, `randi_range(-5,5)`, `randf_range(-2.5,7.5)` and `randfn(0,1)`, each from a fresh generator. It also has a 32-step interleaved `[randi, randf, randi_range(0,9)]` sequence and seed 42 `randi() % 7`. Float sequences have `_str` (`var_to_str`) and `_bits` (IEEE-754 hex) parallel arrays.
- `string_hash_fixture.json`: `String.hash()`, `hash(s)`, `abs(s.hash())` (the `LootRoller._stable_seed`), code points and UTF-8 for 23 strings. Also `hash()` of ints `0, 1, 42, -1` and of `StringName("abc")`.
- `float_format_fixture.json`, `float_format_raw.json`, `stringify_sample_raw.json`, `stringify_sample_raw_2space.json`: how Godot's JSON writer and `var_to_str`/`str` format numbers and strings. The `*_raw.json` files are byte-for-byte `JSON.stringify` output with no trailing newline. The fixture also records the `typeof()` results from a `JSON.parse_string` round trip.
- `fnv1a_fixture.json` + `fnv1a_inputs/`: `SeedDeterminismContract.fnv1a_64` applied to short strings, to the golden and seed-17 layout files (LF text, and re-serialised with `"  "`), and to `record_golden`/`assert_layout_match` for 3 blueprints. The exact hashed texts are saved in `fnv1a_inputs/`.

### `godot/procgen/`

Every `<tag>` has three files:
- `layout_<tag>.json`: the layout. Many tags also have a `.fullprec` companion because wreck `module_damage.amount` values are RNG floats.
- `gameplay_slice_<tag>.json`: the gameplay slice.
- `recipe_<tag>.json`: the exact inputs (blueprint size/condition/seed/room range, archetype dict, biome, difficulty, extended flag) and the post-steps. It also has an FNV-1a fingerprint of the raw `JSON.stringify(layout, "  ")` text.

`ShipGenerator.generate_from_seed` is **never** used (on Windows it routes to the native Rust `DerelictGenerator`). All layouts come from `ShipLayoutGenerator.generate_with_options`. The slices follow the data-only part of `ShipGenerator._load_layout_as_scene`: `GameplaySliceBuilder.build`, then `arc_zones` are copied onto the layout.

| tag | recipe |
|---|---|
| `s17_medium_pristine` | `refresh_seed_000017_fixture.gd`: MEDIUM/PRISTINE seed 17, archetype `{}`, no biome/difficulty, legacy templates. The structural plan is recompiled, then the slice is built. `checked_in_comparison` holds the diff against `data/procgen/smoke/seed_000017`. |
| `s{42,777,999,7777}_small_wrecked_ext` | `procgen_structural_debug_export.gd` generation inputs: SMALL/WRECKED, `room_count_range` 5..8, inline `derelict_a` archetype, extended templates, no biome/difficulty. The exporter's own sorted documents and PNG are in `structural_debug/seed_<n>/`. |
| `s42_<biome>_<difficulty>` | Production travel path (`ShipGenerator.configure_run_context` + `generate(blueprint, {})`) for all 3 biomes × 3 difficulties in `data/procgen/{biomes,difficulty}`. The archetype is `data/procgen/archetypes/derelict.json`, extended templates are on (difficulty is set), and the blueprint is SMALL/DAMAGED seed 42 as in `procgen_variation_smoke.gd`. |

### `godot/loot/`

`loot_rolls_fixture.json` covers all 10 tables in `data/items/loot_tables.json`. It crosses 5 seed sources with 2 contexts: `{}`, and a context containing every key `LootDistribution.roll` reads (`biome_id`, `loot_quality_modifier`, `depth`, `condition`, `container_kind`). Each result is recorded with its RNG seed string and seed. The file also has the matching `LootRoller.roll` results. `inputs/` holds the loot table file and the merged `ItemDefs.load_definitions()`.

### `godot/models/`

There is one `<model>_tick_fixture.json` (+ `.fullprec`) per model: oxygen_state, fire_suppression_state, electrical_arc_state, radiation_state, vitals_state, sanity_state, body_temperature_state, spoilage_state, hydroponics_state, web_infestation_state, ship_systems_manager.

- Each model is constructed and configured as its `scripts/validation/<model>_smoke.gd` does. The exact config is recorded.
- Any setup ops (ignite, add_food, plant, reactor knocked out) are recorded too.
- Each model then runs 20 steps: `1/60 ×10, 0.35 ×5, 3.0 ×3, 5.0 ×2`. The call, args/context and return value are stored for every step. `get_summary()` is recorded after configure, after each setup op, and after every step.
- Each file ends with a `get_summary → apply_summary → get_summary` round trip into a fresh instance. The prepare step for that instance is recorded.

There is **no `fire_state.gd`**. The timer-based FireState was retired, and `FireSuppressionState` (ADR-0041) is the fire model in use. `inputs/ship_systems_definitions.json` is `data/ship_systems/systems.json`.

### `godot/save/`

`README.json` lists which process produced each file, with the command, exit code, PASS marker and any unexpected ERROR/WARNING lines. Before each run, `user://saves` and the `user://*.json` files are cleared. The user's real data is moved aside and restored afterwards.

- `main_playable_slice_save_load_smoke/`, `main_playable_slice_multislot_save_smoke/`, `world_snapshot_smoke/`: the unmodified smokes. Only `residual/` files survive, because the smokes delete their run saves on completion. `world_snapshot_smoke` writes nothing.
- `capture_main_playable/`: `parity_capture_saves.gd --mode main` replays the save-load smoke flow. Real files are copied at `mid_run/` (`world.json` from `request_save()`, plus `slot_01.json`, `quicksave.json`, `current_run.json`, `index.json` and the `.cloud/*.manifest.json` files) and at `after_completion/` (with `meta_progression.json` and `unlock_registry.json`).
- `capture_world_snapshot/`: the `world_snapshot_smoke` WorldSnapshot, saved through `SaveLoadService.save_world`.
- `legacy/`: output of the migration smokes (`slot_legacy.migrated.json`) and `migration_cases.json`. That file holds `SaveMigrationService.migrate_run`/`migrate_world` input→output pairs for every known version, a newer-than-current save, and the world-2/world-4/world-99 cases. **The repo has no legacy save fixture files.** The migration smokes only embed the legacy dicts inline.
- Expect save files to differ between captures in `saved_at`, `run_id` and the `.cloud` manifests. Everything else in the export is deterministic: two runs gave byte-identical `procgen/`, `models/` and `loot/` output.

## Regenerate

From a Godot checkout of `unity/parity-fixtures` (or any tree containing the exporter scripts at the source sha) with its `.godot/` import cache populated (`godot --headless --path . --import` once):

```bash
cd F:/work/godot-parity-fixtures
F:/Tools/Godot/Godot_v4.7.1-stable_win64_console.exe --headless --path . \
  --script res://scripts/validation/export_parity_fixtures.gd -- \
  --out F:/synaptic-sea/fixtures/godot --sha 96ecb2b002b93be6907b25ed7d20839e24b81869
# success marker: PARITY FIXTURES PASS files=172
# --script can exit 0 on parse errors: also check the output for SCRIPT ERROR / Parse Error / ERROR:

# plain copies (git blob bytes, so LF regardless of core.autocrlf)
F=F:/synaptic-sea/fixtures
for p in $(git ls-files data/procgen/golden); do d=$F/golden/${p#data/procgen/golden/}; mkdir -p "$(dirname "$d")"; git show HEAD:$p > "$d"; done
mkdir -p $F/golden/smoke_seed_000017 $F/godot_wrappers/ship_structural_v0
for p in $(git ls-files data/procgen/smoke/seed_000017); do git show HEAD:$p > $F/golden/smoke_seed_000017/$(basename $p); done
for p in $(git ls-files 'scenes/wrappers/structural/ship_structural_v0/*.tscn' 'scenes/wrappers/structural/ship_structural_v0/*.manifest.json'); do git show HEAD:$p > $F/godot_wrappers/ship_structural_v0/$(basename $p); done
```

`--skip-saves` skips the child-process save stage, which is the only stage that touches `user://`. The exporter deletes and rewrites only `kernel/ procgen/ loot/ models/ save/ meta.json` under `--out`.

## Findings the port must respect

- **RNG.** `RandomNumberGenerator.randi()` is standard PCG32 (`pcg32_srandom_r(seed, initseq=1442695040888963407)`), checked against every seed here. `randf()`/`randf_range()` return float32-representable values and are **not** `randi()/2^32`, so port Godot's `RandomPCG::randf`. `RandomNumberGenerator.new()` without `.seed` is randomly seeded.
- **`String.hash()`** is djb2 (`h = h*33 + c`, uint32, start 5381) over UTF-32 code points. This was checked for all 23 strings, including non-BMP emoji. Loot seeds are `abs(s.hash())`.
- **`SeedDeterminismContract.fnv1a_64`** hashes UTF-32 code points, not UTF-8 bytes. It matches the standard FNV-1a-64 vectors for ASCII.
- **JSON writer.**
  - Floats use about 15 significant digits: `0.30000000000000004` becomes `0.3`, and `1/3` becomes `0.333333333333333`.
  - Integral floats keep `.0`, and large ones print in full (`1e21` becomes `1000000000000000000000.0`).
  - Ints are exact (`9007199254740993`).
  - `-0.0` loses its sign in every formatter.
  - Control character U+0001 is written raw, not escaped.
  - `full_precision=true` gives shortest-round-trip text (`1e-07`, `1e+21`).
- **`JSON.parse_string`** returns every number as `float`, including `17` and `12345678901234`.
- **`var_to_str`** on a float that is exactly representable as float32 (every `randf()` output) prints the shortest **float32** text (`0.2981868`). That text does not parse back to the same float64. Use the `_bits` arrays or the `.fullprec.json` files for exact values.
- **Raw layouts.** Godot's raw `JSON.stringify(layout)` renders `Vector2i`/`Vector3` as strings (`"(3, 4)"`). This is how `refresh_seed_000017_fixture.gd` writes files and what the FNV fingerprints hash. The exporter's files canonicalise these to arrays.
- **Spoilage round trip is lossy in Godot.** `SpoilageState.apply_summary` into a fresh instance does not restore FoodState `display_name`/multipliers. The restored foods get default values, so `round_trip.equal=false` in `spoilage_state_tick_fixture.json`. The other 10 models round-trip exactly.
- **Seed-17 fixture.** The checked-in `data/procgen/smoke/seed_000017/layout.json` was regenerated from the Rust worldgen v2 export (commit `149ed476`; `generator.name = "worldgen"`, template `derelict_b`). Regenerating it with the GDScript `refresh_seed_000017_fixture.gd` recipe therefore matches **neither byte-for-byte nor semantically**; the diff is in `recipe_s17_medium_pristine.json`.
  - Compared with the last GDScript-generated version (`05898ae5`), the same rooms/topology are produced. The differences are the `Vector2i`-as-string serialisation, the newly stamped `template_id`/`structural_plan_validated`, and structural-plan and slot changes from 75 later procgen commits.
  - Use `procgen/layout_s17_medium_pristine.json` as the GDScript parity target, and `golden/smoke_seed_000017/` as the worldgen-v2 target.
- **Biome × difficulty matrix.** All 9 `s42_<biome>_<difficulty>` layouts select the `hive` template (kit `ship_structural_biomatter`) and emit no `encounters`. They differ in `biome_id`/`difficulty_id`/`encounter_pacing` and room variants.
- **Git settings in this repo.** `.gitattributes` routes `*.png` through LFS, which affects `procgen/structural_debug/seed_*/topdown_layout.png`. `*.log` is gitignored, which is why child-process logs are saved as `process_output.txt`.
