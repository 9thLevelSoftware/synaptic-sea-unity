# tools/python

Python tooling carried over from the Godot repository (`D:\the-synaptic-sea\tools`, `main` @ `96ecb2b0`), plus the
port's own generators. Use Python 3.12 (`F:\Tools\Python312\python.exe`). Everything is stdlib only unless a row
says otherwise.

## Layout mapping

The copied scripts addressed files the way the Godot project laid them out. `synaptic_layout.py` is the single
place that maps those paths onto this repository; `res://` strings inside data files are unchanged.

| Godot project path | This repository |
|---|---|
| `data/asset_generation/` | `tools/asset_generation/` (Meshy contracts, schemas, pricing, prompt profiles; not runtime data) |
| `data/` | `SynapticSea/Assets/StreamingAssets/data/` |
| `assets/imported/structural/` | `SynapticSea/Assets/Content/Structural/` |
| `assets/imported/props/` | `SynapticSea/Assets/Content/Props/` |
| `assets/imported/` | `SynapticSea/Assets/Content/` |
| `assets/_staging/`, `assets/_review/` | `artifacts/_staging/`, `artifacts/_review/` (git-ignored) |
| `scenes/wrappers/structural/` | `fixtures/godot_wrappers/` |
| `data/audio/{sfx,ui,music}/` | `SynapticSea/Assets/Content/Audio/{SFX,UI,Music}/` (audio generator only) |

## Copied and ported

| Script | Purpose | Runs on | Verified |
|---|---|---|---|
| `build_system_inventory.py` + `test_build_system_inventory.py` | System inventory (`docs/inventory/`), now keyed by C# file. `--check`, `--coverage`, one-time `--from-godot <repo>` import through the `// Ported from` headers | any | self-test, `--check` |
| `generate_prop_sidecars.py` | Create/refresh prop visual sidecars and `data/props/visual_bindings.generated.json` | any | `--check` passes on the 26 props (index byte-identical) |
| `prop_visual_metadata.py` | Sidecar schema and GLB evidence helpers (sidecars keep Godot `res://` paths) | any | via the two prop tools |
| `validate_prop_visual_bindings.py` | Validate sidecars, GLB evidence, bindings and index | any | passes, `--check-index` passes |
| `structural_source_contract.py` | Strict contract API for recovered structural Blender sources | any | all 15 module specs load |
| `validate_structural_sources.py`, `inspect_structural_sources.py`, `recover_modules.py` | Validate / inspect / recover the external `.blend` structural sources | Blender for the source steps | `--help` |
| `backup_structural_sources.py` | Sync the external sources to a local, S3 or GCS backup | any (`aws`/`gsutil` for cloud) | `--help` |
| `export_structural_glb.py` | Blender: export structural source collections as staged GLBs | Blender | copied unchanged |
| `generate_placeholder_audio.py` | Deterministic placeholder clips; `--out-root` writes elsewhere | any | output byte-identical to the 15 committed clips |
| `meshy_governance.py`, `meshy_asset_contract.py`, `meshy_stage.py`, `meshy_candidate_review.py`, `meshy_blender_master.py`, `meshy_blender_validate.py`, `meshy_promotion_packet.py`, `meshy_texture_packet.py` | Governed Meshy candidate pipeline: contracts, credit-gated staging, Blender master and validation, candidate review, proposal packets | **POSIX only** (`fcntl`, 0600/0700 modes): macOS, Linux or WSL; `BLENDER` and `SYNAPTIC_SEA_MESHY_MASTER_ROOT` override the macOS defaults | `--help` under WSL; contracts load |
| `meshy_runtime_review.py` | **Reduced to verification.** Report schema, pixel gates re-derived from the PNG leaves, `verify_evidence_chain` (used by `meshy_candidate_review bind`) | POSIX only | `verify --help` |

`gen_input_actions.py` is the port's own generator for `SynapticSea.inputactions`.

## Replaced by Unity editor tools

| Godot script | Replacement |
|---|---|
| `promote_structural_sources.py` | `Synaptic Sea/Content/Promote Structural Source…` (`SynapticSea.EditorTools.Content.PromoteStructuralSource.Run`). The Blender export stays `export_structural_glb.py`; the Godot import-smoke overlay becomes an AssetDatabase import plus a structural prefab rebuild |
| `validate_promoted_sources.py` | The import smoke and prefab rebuild inside `PromoteStructuralSource` |
| `meshy_runtime_review.py` (capture half) | `Synaptic Sea/Content/Meshy Runtime Review…` (`SynapticSea.EditorTools.Content.MeshyRuntimeReview.Run`) renders the six captures and writes `runtime-review.json` |
| `gltf_collision_postprocess.py` | `StructuralPrefabBuilder` hides `Collision_*` / `-col` proxy meshes; collision comes from the Godot wrapper boxes |
| `check_export_pipeline.py` | `tools/build.ps1` and `Editor/Build/Builder.cs` |
| `validate_structural_variant_bindings.py` | `StructuralPrefabBuilder`'s report and `FrameConventionTests.EveryKitModuleHasAPrefabWithCollision` (it parsed Godot wrapper scenes and a GDScript resolver) |

## Not carried over

- `meshy_loot_container_recipe.py`: builds a Godot scene recipe.
- `focused_nine_*.py` (8 files): Godot preview/capture pipeline for the focused-nine structural set.
- Tile, ComfyUI and LoRA experiments (`apply_tile_texture.py`, `batch_*_tiles.py`, `batch_render_modules.py`, `comfyui_tile_workflow.py`, `create_material_library.py`, `improve_floor_geometry.py`, `inpaint_tile_edges.py`, `render_module_passes.py`, `prepare_lora_dataset.py`, `train_lora.sh`, `lora_train_config.toml`): the legacy tile pipeline, not used by the port.
- `validate_architecture_diagrams.py` (+ test), `classify_orphan_smokes.sh`, `synaptic_sea_gate4_regression.sh`: Godot docs and smoke-suite maintenance.
- The Godot repository's `tests/test_*.py` suites for these scripts were not copied; only the inventory self-test was.

## Blender

`tools/blender/structural_module_toolkit/` is the authoring add-on (contracts are looked up under
`SynapticSea/Assets/StreamingAssets/data/placement/contracts/...`). `tools/blender/reexport_gltf_separate.py`
re-exports `*_textured.glb` as glTF Separate so Unity's texture presets apply:

```
"C:\Program Files\Blender Foundation\Blender 5.2\blender.exe" --background --factory-startup ^
    --python tools/blender/reexport_gltf_separate.py -- --input <glb or dir> --output <dir>
```
