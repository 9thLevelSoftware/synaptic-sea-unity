# Playtest QA / eng — ithappy_scifi_v0 StructuralPrefabBuilder bake

Verify path for KEEP ithappy kit authority after the additive import (decision 61) and the companion `{module_id}.asset.json` gate. Do **not** regenerate art. Track A New Run / run-results wiring is out of scope.

Automated coverage (no Unity Editor):

```
dotnet test tools/dotnet/SynapticSea.Core.Tests --filter KitAuthorityAudit
python3 tools/test_import_ithappy_keep.py
```

`KitAuthorityAudit` reads kit rows + `data/placement/contracts/structural/ithappy_scifi_v0/` only. Missing sockets or footprint on either row mark the module **ungated**. New modules (no `ship_structural_v0` twin) also need `Assets/Content/Structural/ithappy/{module_id}/{module_id}.asset.json` beside the GLB.

## Exact Deviltop bake command

Deviltop Unity is `F:\Unity\6000.6.0f1\Editor\Unity.exe` (same editor as `tools/build.ps1`). From the repo root, project path is the `SynapticSea` folder. `builds/logs` is gitignored — create it first so Unity can open `-logFile`:

```
New-Item -ItemType Directory -Force builds\logs | Out-Null
F:\Unity\6000.6.0f1\Editor\Unity.exe -batchmode -nographics -projectPath SynapticSea -executeMethod SynapticSea.EditorTools.Content.StructuralPrefabBuilder.BuildAll -quit -kit ithappy_scifi_v0 -logFile builds/logs/ithappy-scifi-bake.log
```

Editor menu equivalent: **Synaptic Sea → Content → Build Structural Prefabs** only bakes the default `ship_structural_v0` kit. Ithappy **must** use `-kit ithappy_scifi_v0` (or a one-off `StructuralPrefabBuilder.Build("ithappy_scifi_v0", …)` call). Do not run the default menu item and assume ithappy filled.

Optional: append `-strict` to treat builder warnings as a non-zero exit.

The Editor must not already have the project open.

## What "bake complete" means for `KitPrefabCatalog`

Until this bake, `Resources/Catalogs/KitCatalog_ithappy_scifi_v0.asset` may list `modules: []`. `KitPrefabCatalog.TryGetPrefab` then falls back to `ship_structural_v0` for the 15 shared ids and **does not** resolve `wall_x_junction`.

Bake is complete only when **all** of the following are true:

1. Unity exits **0**.
2. `builds/logs/structural-prefab-report-ithappy_scifi_v0.json` exists and has `"errors": []` (empty). The log line is:
   `STRUCTURAL PREFABS PASS kit=ithappy_scifi_v0 prefabs=16 …`
3. `Resources/Catalogs/KitCatalog_ithappy_scifi_v0.asset` lists **16** `modules` entries — every kit `module_id`, including `wall_x_junction`. Shared ids must point at `Assets/Content/Prefabs/Structural/ithappy_scifi_v0/<module_id>.prefab`, **not** the v0 prefab.
4. `TryGetPrefab("wall_x_junction")` succeeds on that catalog (no v0 twin to fall back to).
5. Shared ids no longer need the v0 fallback: `TryGetPrefab("floor_1x1")` (and the other 14) returns the ithappy prefab from this catalog.

If the report lists a module as missing its companion `{module_id}.asset.json`, that module is **ungated** and the bake must fail. `wall_x_junction.asset.json` sits beside `wall_x_junction.glb` and must keep `SOCK_wall_face_{north,east,south,west}_01`.

## After-bake EditMode checks (needs the project Editor)

```
pwsh tools/test.ps1 -Mode Dotnet -Filter KitAuthorityAudit
pwsh tools/test.ps1 -Mode EditMode -Filter KitCatalogResolution
```

`KitCatalogResolution` only proves the ithappy catalog **asset** loads (`kitId == ithappy_scifi_v0`). It still passes when `modules: []`. Bake-complete is the checklist above (report `errors: []`, 16 ithappy prefab paths, `wall_x_junction` resolves).

`FrameConventionTests` / `StructuralPrefabCollisionTests` still target the v0 catalog unless pointed at ithappy after bake.

## Authority (do not invent SOCK names)

| Consumer | Reads | Does not read |
|---|---|---|
| `ModularSocketCatalog` / `StructuralEdgeCompiler` | `data/placement/contracts/structural/{kit}/*_contract.json` | Mesh node names, wrapper Marker3D poses (they sit at origin) |
| `StructuralPrefabBuilder` | kit JSON (id, family, footprint, nav, pivot, `socket_names`) + contract sockets/bounds + companion `.asset.json` + wrapper BoxShape3D + GLB | Ad-hoc SOCK synonyms |
| Legacy `StructuralPlacer` | kit `role_modules` only (deprecated debug path) | Socket rows |

## Known gaps (not bake blockers)

- **`wall_face` is not in `ModularSocketCatalog.ENCLOSURE_KINDS`.** Godot parity: `SocketsCompatible` will not bind `wall_x_junction` / `wall_t_junction` wall-face sockets. The compiler still picks T-junctions by `HasModule` for 4-way walls; `wall_x_junction` is for authored placements.
- **Kit `socket_names` can be a subset of contract sockets.** Inherited Godot extras on contract rows (`floor_top`, `wall_base`, `ceiling_edge`, `ceiling_bottom`) are ingested from the contract, not invented. They do **not** mark a module ungated.
- Default generation stays on `ship_structural_v0`. Point a layout at `"kit_id": "ithappy_scifi_v0"` after this bake.
