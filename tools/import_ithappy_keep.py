#!/usr/bin/env python3
"""Copy KEEP ithappy structural (and a small props starter) from a Godot checkout.

Does not regenerate art. Skips Godot .import files. Writes Unity .meta files so
glTFast / TextureImporter pick the assets up on the next Editor refresh.

  python3 tools/import_ithappy_keep.py --godot /tmp/the-synaptic-sea
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import shutil
from pathlib import Path

GLTFAST_SCRIPT = "715df9372183c47e389bb6e19fbc3b52"
SHARED_V0_MODULE_IDS = [
    "floor_1x1",
    "floor_2x1",
    "corridor_floor_1x1",
    "corridor_floor_1x2",
    "wall_straight_1x1",
    "wall_end_cap",
    "wall_inner_corner",
    "wall_outer_corner",
    "wall_t_junction",
    "doorway_frame_open_1x1",
    "bulkhead_portal_2x1",
    "doorway_frame_blocked_1x1",
    "ramp_up_1x2",
    "pillar_support_1x1",
    "ceiling_cap_1x1",
]
PROP_STARTER_CATEGORIES = ["doors_hatches", "security"]


def guid_for(*parts: str) -> str:
    return hashlib.md5(("synaptic-sea-ithappy:" + "/".join(parts)).encode("utf-8")).hexdigest()


def write_text(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8", newline="\n")


def folder_meta(guid: str) -> str:
    return (
        "fileFormatVersion: 2\n"
        f"guid: {guid}\n"
        "folderAsset: yes\n"
        "DefaultImporter:\n"
        "  externalObjects: {}\n"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n"
    )


def default_meta(guid: str) -> str:
    return (
        "fileFormatVersion: 2\n"
        f"guid: {guid}\n"
        "DefaultImporter:\n"
        "  externalObjects: {}\n"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n"
    )


def glb_meta(guid: str) -> str:
    return (
        "fileFormatVersion: 2\n"
        f"guid: {guid}\n"
        "ScriptedImporter:\n"
        "  internalIDToNameTable: []\n"
        "  externalObjects: {}\n"
        "  serializedVersion: 2\n"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n"
        f"  script: {{fileID: 11500000, guid: {GLTFAST_SCRIPT}, type: 3}}\n"
        "  editorImportSettings:\n"
        "    generateSecondaryUVSet: 0\n"
        "  importSettings:\n"
        "    nodeNameMethod: 1\n"
        "    animationMethod: 2\n"
        "    generateMipMaps: 1\n"
        "    texturesReadable: 0\n"
        "    defaultMinFilterMode: 9729\n"
        "    defaultMagFilterMode: 9729\n"
        "    anisotropicFilterLevel: 1\n"
        "  instantiationSettings:\n"
        "    mask: -1\n"
        "    layer: 0\n"
        "    skinUpdateWhenOffscreen: 1\n"
        "    lightIntensityFactor: 1\n"
        "    sceneObjectCreation: 2\n"
        "  assetDependencies: []\n"
        "  reportItems: []\n"
    )


def png_meta(guid: str) -> str:
    return (
        "fileFormatVersion: 2\n"
        f"guid: {guid}\n"
        "TextureImporter:\n"
        "  internalIDToNameTable: []\n"
        "  externalObjects: {}\n"
        "  serializedVersion: 13\n"
        "  mipmaps:\n"
        "    mipMapMode: 0\n"
        "    enableMipMap: 1\n"
        "    sRGBTexture: 1\n"
        "    linearTexture: 0\n"
        "    fadeOut: 0\n"
        "    borderMipMap: 0\n"
        "    mipMapsPreserveCoverage: 0\n"
        "    alphaTestReferenceValue: 0.5\n"
        "    mipMapFadeDistanceStart: 1\n"
        "    mipMapFadeDistanceEnd: 3\n"
        "  bumpmap:\n"
        "    convertToNormalMap: 0\n"
        "    externalNormalMap: 0\n"
        "    heightScale: 0.25\n"
        "    normalMapFilter: 0\n"
        "    flipGreenChannel: 0\n"
        "  isReadable: 0\n"
        "  streamingMipmaps: 0\n"
        "  streamingMipmapsPriority: 0\n"
        "  vTOnly: 0\n"
        "  ignoreMipmapLimit: 0\n"
        "  grayScaleToAlpha: 0\n"
        "  generateCubemap: 6\n"
        "  cubemapConvolution: 0\n"
        "  seamlessCubemap: 0\n"
        "  textureFormat: 1\n"
        "  maxTextureSize: 2048\n"
        "  textureSettings:\n"
        "    serializedVersion: 2\n"
        "    filterMode: 1\n"
        "    aniso: 1\n"
        "    mipBias: 0\n"
        "    wrapU: 0\n"
        "    wrapV: 0\n"
        "    wrapW: 0\n"
        "  nPOTScale: 1\n"
        "  lightmap: 0\n"
        "  compressionQuality: 50\n"
        "  spriteMode: 0\n"
        "  spriteExtrude: 1\n"
        "  spriteMeshType: 1\n"
        "  alignment: 0\n"
        "  spritePivot: {x: 0.5, y: 0.5}\n"
        "  spritePixelsToUnits: 100\n"
        "  spriteBorder: {x: 0, y: 0, z: 0, w: 0}\n"
        "  spriteGenerateFallbackPhysicsShape: 1\n"
        "  alphaUsage: 1\n"
        "  alphaIsTransparency: 0\n"
        "  textureType: 0\n"
        "  textureShape: 1\n"
        "  singleChannelComponent: 0\n"
        "  flipbookRows: 1\n"
        "  flipbookColumns: 1\n"
        "  maxTextureSizeSet: 0\n"
        "  compressionQualitySet: 0\n"
        "  textureFormatSet: 0\n"
        "  ignorePngGamma: 0\n"
        "  applyGammaDecoding: 0\n"
        "  swizzle: 50462976\n"
        "  cookieLightType: 0\n"
        "  platformSettings:\n"
        "  - serializedVersion: 4\n"
        "    buildTarget: DefaultTexturePlatform\n"
        "    maxTextureSize: 2048\n"
        "    resizeAlgorithm: 0\n"
        "    textureFormat: -1\n"
        "    textureCompression: 1\n"
        "    compressionQuality: 50\n"
        "    crunchedCompression: 0\n"
        "    allowsAlphaSplitting: 0\n"
        "    overridden: 0\n"
        "    ignorePlatformSupport: 0\n"
        "    androidETC2FallbackOverride: 0\n"
        "    forceMaximumCompressionQuality_BC6H_BC7: 0\n"
        "  spriteSheet:\n"
        "    serializedVersion: 2\n"
        "    sprites: []\n"
        "    outline: []\n"
        "    customData: \n"
        "    physicsShape: []\n"
        "    bones: []\n"
        "    spriteID: \n"
        "    internalID: 0\n"
        "    vertices: []\n"
        "    indices: \n"
        "    edges: []\n"
        "    weights: []\n"
        "    secondaryTextures: []\n"
        "    nameFileIdTable: {}\n"
        "  mipmapLimitGroupName: \n"
        "  pSDRemoveMatte: 0\n"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n"
    )


def write_meta_for(path: Path, kind: str) -> None:
    rel = str(path).replace("\\", "/")
    guid = guid_for(kind, rel)
    meta = path.with_name(path.name + ".meta")
    if kind == "folder":
        write_text(meta, folder_meta(guid))
    elif path.suffix.lower() == ".glb":
        write_text(meta, glb_meta(guid))
    elif path.suffix.lower() == ".png":
        write_text(meta, png_meta(guid))
    else:
        write_text(meta, default_meta(guid))


def copy_tree_assets(src: Path, dst: Path, root_label: str) -> list[Path]:
    copied: list[Path] = []
    dst.mkdir(parents=True, exist_ok=True)
    write_meta_for(dst, "folder")
    for src_file in sorted(src.rglob("*")):
        if src_file.name.endswith(".import") or src_file.name.endswith(".uid"):
            continue
        rel = src_file.relative_to(src)
        dest = dst / rel
        if src_file.is_dir():
            dest.mkdir(parents=True, exist_ok=True)
            write_meta_for(dest, "folder")
            continue
        dest.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(src_file, dest)
        write_meta_for(dest, root_label)
        copied.append(dest)
    return copied


def rewrite_wrapper(text: str, module_id: str) -> str:
    ithappy_prefix = f"res://assets/imported/structural/ithappy/{module_id}/"

    def variant_path(kind: str) -> str:
        name = module_id if kind == "intact" else f"{module_id}_{kind}"
        return f"{ithappy_prefix}{name}.glb"

    text = re.sub(
        r'path="res://assets/imported/structural/[^"]+" id="1_visual"',
        f'path="{variant_path("intact")}" id="1_visual"',
        text,
    )
    text = re.sub(
        r'path="res://assets/imported/structural/[^"]+" id="2_visual_damaged"',
        f'path="{variant_path("damaged")}" id="2_visual_damaged"',
        text,
    )
    text = re.sub(
        r'path="res://assets/imported/structural/[^"]+" id="3_visual_breached"',
        f'path="{variant_path("breached")}" id="3_visual_breached"',
        text,
    )
    return text


def copy_wrappers(godot: Path, dest: Path) -> None:
    src = godot / "scenes/wrappers/structural/ithappy"
    dest.mkdir(parents=True, exist_ok=True)
    for tscn in sorted(src.glob("*.tscn")):
        text = rewrite_wrapper(tscn.read_text(encoding="utf-8"), tscn.stem)
        write_text(dest / tscn.name, text)


def copy_contracts(repo: Path) -> None:
    src = repo / "SynapticSea/Assets/StreamingAssets/data/placement/contracts/structural/ship_structural_v0"
    dst = repo / "SynapticSea/Assets/StreamingAssets/data/placement/contracts/structural/ithappy_scifi_v0"
    dst.mkdir(parents=True, exist_ok=True)
    write_meta_for(dst, "folder")
    for src_file in sorted(src.glob("*_contract.json")):
        data = json.loads(src_file.read_text(encoding="utf-8"))
        rewrite_kit_id(data, "ithappy_scifi_v0")
        module_id = data.get("module_id") or src_file.name.replace("_contract.json", "")
        data["wrapper_scene"] = f"res://scenes/wrappers/structural/ithappy/{module_id}.tscn"
        data["source_asset_path"] = f"assets/imported/structural/ithappy/{module_id}/{module_id}.glb"
        data["contract_path"] = (
            f"res://data/placement/contracts/structural/ithappy_scifi_v0/{module_id}_contract.json"
        )
        if isinstance(data.get("asset"), dict):
            rewrite_kit_id(data["asset"], "ithappy_scifi_v0")
            data["asset"]["wrapper_scene"] = data["wrapper_scene"]
            data["asset"]["source_asset_path"] = data["source_asset_path"]
            data["asset"]["contract_path"] = data["contract_path"]
        dest = dst / src_file.name
        write_text(dest, json.dumps(data, indent=2) + "\n")
        write_meta_for(dest, "json")


def rewrite_kit_id(node: object, kit_id: str) -> None:
    if isinstance(node, dict):
        if "kit_id" in node:
            node["kit_id"] = kit_id
        if isinstance(node.get("kit"), dict) and "kit_id" in node["kit"]:
            node["kit"]["kit_id"] = kit_id
        if isinstance(node.get("provenance"), dict) and "provenance_id" in node["provenance"]:
            pid = str(node["provenance"]["provenance_id"])
            node["provenance"]["provenance_id"] = pid.replace("ship_structural_v0:", "ithappy_scifi_v0:", 1)
        for value in node.values():
            rewrite_kit_id(value, kit_id)
    elif isinstance(node, list):
        for item in node:
            rewrite_kit_id(item, kit_id)


def wall_x_contract() -> dict:
    sockets = [
        {"id": "wall_face_north_01", "kind": "wall_face", "position_m": [0.0, 0.0, -2.0],
         "compatible_kinds": ["wall_edge", "portal_edge"]},
        {"id": "wall_face_east_01", "kind": "wall_face", "position_m": [2.0, 0.0, 0.0],
         "compatible_kinds": ["wall_edge", "portal_edge"]},
        {"id": "wall_face_south_01", "kind": "wall_face", "position_m": [0.0, 0.0, 2.0],
         "compatible_kinds": ["wall_edge", "portal_edge"]},
        {"id": "wall_face_west_01", "kind": "wall_face", "position_m": [-2.0, 0.0, 0.0],
         "compatible_kinds": ["wall_edge", "portal_edge"]},
    ]
    bounds = {
        "local_min_m": [-2.0, 0.0, -2.0],
        "local_max_m": [2.0, 3.0, 2.0],
        "placement_origin": "edge-center",
        "mesh_origin_offset_m": [0.0, 0.0, 0.0],
        "notes": (
            "Art-director pack authored at ~1x1x4 Z-up / edge-center. Contract sockets "
            "use the 4 m kit cell so procgen can bind the same wall_face kinds as wall_t_junction. "
            "Do not regenerate the GLB; recenter in the prefab if the mesh pivot is still Blender-local."
        ),
    }
    return {
        "schema_version": "1.0.0",
        "document_kind": "modular_asset_spec",
        "asset_id": "wall_x_junction",
        "module_id": "wall_x_junction",
        "category": "structural",
        "kit_id": "ithappy_scifi_v0",
        "module_family": "junction",
        "grid_step_m": 4.0,
        "bounds": bounds,
        "footprint_cells": [1, 1],
        "sockets": sockets,
        "collision": {
            "kind": "static-body-proxy",
            "nav_blocker": True,
            "proxy_shape": "box",
            "notes": "four 4x3x0.2 wing boxes matching v0 wall_t_junction plus the south face",
        },
        "provenance": {
            "source_platform": "ithappy-keep",
            "license_state": "imported",
            "provenance_id": "ithappy_scifi_v0:wall_x_junction",
        },
        "source_asset_path": "assets/imported/structural/ithappy/wall_x_junction/wall_x_junction.glb",
        "wrapper_scene": "res://scenes/wrappers/structural/ithappy/wall_x_junction.tscn",
        "contract_path": "res://data/placement/contracts/structural/ithappy_scifi_v0/wall_x_junction_contract.json",
        "asset": {
            "id": "wall_x_junction",
            "module_id": "wall_x_junction",
            "category": "structural",
            "kit": {
                "kit_id": "ithappy_scifi_v0",
                "module_id": "wall_x_junction",
                "module_family": "junction",
                "pivot_policy": "edge-center",
            },
            "bounds": bounds,
            "footprint_cells": [1, 1],
            "sockets": sockets,
        },
    }


def wall_x_wrapper() -> str:
    glb = "res://assets/imported/structural/ithappy/wall_x_junction/wall_x_junction.glb"
    return (
        "[gd_scene load_steps=5 format=3]\n"
        "\n"
        f'[ext_resource type="PackedScene" path="{glb}" id="1_visual"]\n'
        f'[ext_resource type="PackedScene" path="{glb}" id="2_visual_damaged"]\n'
        f'[ext_resource type="PackedScene" path="{glb}" id="3_visual_breached"]\n'
        "\n"
        '[sub_resource type="BoxShape3D" id="BoxShape3D_1"]\n'
        "size = Vector3(4, 3, 0.2)\n"
        "\n"
        '[node name="Wall_X_Junction" type="Node3D"]\n'
        "\n"
        '[node name="Anchor_SOCK_wall_face_north_01" type="Marker3D" parent="."]\n'
        '[node name="Anchor_SOCK_wall_face_east_01" type="Marker3D" parent="."]\n'
        '[node name="Anchor_SOCK_wall_face_south_01" type="Marker3D" parent="."]\n'
        '[node name="Anchor_SOCK_wall_face_west_01" type="Marker3D" parent="."]\n'
        "\n"
        '[node name="CollisionRoot" type="StaticBody3D" parent="."]\n'
        "collision_layer = 1\n"
        "collision_mask = 1\n"
        "\n"
        '[node name="CollisionShape3D_WingNorth" type="CollisionShape3D" parent="CollisionRoot"]\n'
        "position = Vector3(0, 1.5, -2)\n"
        'shape = SubResource("BoxShape3D_1")\n'
        "\n"
        '[node name="CollisionShape3D_WingSouth" type="CollisionShape3D" parent="CollisionRoot"]\n'
        "position = Vector3(0, 1.5, 2)\n"
        'shape = SubResource("BoxShape3D_1")\n'
        "\n"
        '[node name="CollisionShape3D_WingEast" type="CollisionShape3D" parent="CollisionRoot"]\n'
        "transform = Transform3D(0, 0, 1, 0, 1, 0, -1, 0, 0, 2, 1.5, 0)\n"
        'shape = SubResource("BoxShape3D_1")\n'
        "\n"
        '[node name="CollisionShape3D_WingWest" type="CollisionShape3D" parent="CollisionRoot"]\n'
        "transform = Transform3D(0, 0, 1, 0, 1, 0, -1, 0, 0, -2, 1.5, 0)\n"
        'shape = SubResource("BoxShape3D_1")\n'
        "\n"
        '[node name="Visual" type="Node3D" parent="."]\n'
        "\n"
        '[node name="VisualInstance_Intact" parent="Visual" instance=ExtResource("1_visual")]\n'
        "visible = true\n"
        '[node name="VisualInstance_Damaged" parent="Visual" instance=ExtResource("2_visual_damaged")]\n'
        "visible = false\n"
        '[node name="VisualInstance_Breached" parent="Visual" instance=ExtResource("3_visual_breached")]\n'
        "visible = false\n"
    )


def wall_x_module() -> dict:
    return {
        "module_id": "wall_x_junction",
        "module_family": "junction",
        "footprint_cells": [1, 1],
        "nav_blocker": True,
        "role": "X / four-way wall junction for procgen",
        "processed_asset_source": "assets/imported/structural/ithappy/wall_x_junction/wall_x_junction.glb",
        "socket_names": [
            "SOCK_wall_face_north_01",
            "SOCK_wall_face_east_01",
            "SOCK_wall_face_south_01",
            "SOCK_wall_face_west_01",
        ],
        "pivot_policy": "edge-center",
        "godot_wrapper_scene": "res://scenes/wrappers/structural/ithappy/wall_x_junction.tscn",
        "godot_contract": "res://data/placement/contracts/structural/ithappy_scifi_v0/wall_x_junction_contract.json",
        "provenance_id": "ithappy_scifi_v0:wall_x_junction",
        "collision_policy": "static-body-proxy",
    }


def update_kit_json(path: Path) -> None:
    data = json.loads(path.read_text(encoding="utf-8"))
    modules = data.setdefault("modules", [])
    modules = [m for m in modules if m.get("module_id") != "wall_x_junction"]
    modules.append(wall_x_module())
    data["modules"] = modules
    data["module_count"] = len(modules)
    data["description"] = (
        "ithappy KEEP structural spine — same module_id strings as ship_structural_v0, "
        "plus wall_x_junction. Visuals live under Assets/Content/Structural/ithappy."
    )
    data["catalog_path"] = "data/kits/ithappy_scifi_v0.json"
    write_text(path, json.dumps(data, indent=2) + "\n")


def import_wall_x(pack: Path, structural_dst: Path, wrappers_dst: Path, contracts_dst: Path) -> None:
    dest_dir = structural_dst / "wall_x_junction"
    dest_dir.mkdir(parents=True, exist_ok=True)
    write_meta_for(dest_dir, "folder")
    for name in ("wall_x_junction.glb", "wall_x_junction.note.json", "wall_x_junction_preview.png"):
        src = pack / name
        if not src.exists():
            raise FileNotFoundError(src)
        dest = dest_dir / name
        shutil.copy2(src, dest)
        write_meta_for(dest, "wall_x")
    write_text(wrappers_dst / "wall_x_junction.tscn", wall_x_wrapper())
    contract = contracts_dst / "wall_x_junction_contract.json"
    write_text(contract, json.dumps(wall_x_contract(), indent=2) + "\n")
    write_meta_for(contract, "json")


def write_prop_inventory(godot: Path, dest: Path) -> dict:
    root = godot / "assets/imported/props/ithappy"
    inventory = {"source": "the-synaptic-sea/assets/imported/props/ithappy", "categories": {}}
    for category in sorted(p.name for p in root.iterdir() if p.is_dir()):
        glbs = sorted(p.stem for p in (root / category).rglob("*.glb"))
        inventory["categories"][category] = {
            "glb_count": len(glbs),
            "imported": category in PROP_STARTER_CATEGORIES,
            "assets": glbs,
        }
    dest.mkdir(parents=True, exist_ok=True)
    write_meta_for(dest, "folder")
    inv_path = dest / "inventory.json"
    write_text(inv_path, json.dumps(inventory, indent=2) + "\n")
    write_meta_for(inv_path, "json")
    return inventory


def write_catalog(path: Path) -> None:
    write_text(
        path,
        """%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!114 &11400000
MonoBehaviour:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {fileID: 0}
  m_PrefabInstance: {fileID: 0}
  m_PrefabAsset: {fileID: 0}
  m_GameObject: {fileID: 0}
  m_Enabled: 1
  m_EditorHideFlags: 0
  m_Script: {fileID: 11500000, guid: 91bced3ceac6ab04baf147d8c1ef449e, type: 3}
  m_Name: KitCatalog_ithappy_scifi_v0
  m_EditorClassIdentifier: SynapticSea.Runtime::SynapticSea.Runtime.KitPrefabCatalog
  kitId: ithappy_scifi_v0
  gridStepMetres: 4
  modules: []
""",
    )
    write_text(
        path.with_suffix(".asset.meta"),
        default_meta(guid_for("catalog", str(path))).replace("DefaultImporter:", "NativeFormatImporter:\n  mainObjectFileID: 11400000\n  unused:"),
    )
    # NativeFormatImporter block — write a proper meta.
    write_text(
        path.with_suffix(".asset.meta"),
        (
            "fileFormatVersion: 2\n"
            f"guid: {guid_for('catalog', str(path))}\n"
            "NativeFormatImporter:\n"
            "  externalObjects: {}\n"
            "  mainObjectFileID: 11400000\n"
            "  userData: \n"
            "  assetBundleName: \n"
            "  assetBundleVariant: \n"
        ),
    )


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--godot", type=Path, required=True)
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--wall-x-pack", type=Path, default=Path("/tmp/wall_x_junction_pack"))
    args = parser.parse_args()

    repo = args.repo
    godot = args.godot
    structural_src = godot / "assets/imported/structural/ithappy"
    structural_dst = repo / "SynapticSea/Assets/Content/Structural/ithappy"
    wrappers_dst = repo / "fixtures/godot_wrappers/ithappy"
    contracts_dst = repo / "SynapticSea/Assets/StreamingAssets/data/placement/contracts/structural/ithappy_scifi_v0"
    props_dst = repo / "SynapticSea/Assets/Content/Props/ithappy"
    kit_json = repo / "SynapticSea/Assets/StreamingAssets/data/kits/ithappy_scifi_v0.json"

    if not structural_src.is_dir():
        raise SystemExit(f"missing Godot structural set: {structural_src}")

    copied = copy_tree_assets(structural_src, structural_dst, "structural")
    copy_wrappers(godot, wrappers_dst)
    copy_contracts(repo)
    import_wall_x(args.wall_x_pack, structural_dst, wrappers_dst, contracts_dst)
    update_kit_json(kit_json)

    inventory = write_prop_inventory(godot, props_dst)
    prop_copied = []
    for category in PROP_STARTER_CATEGORIES:
        src = godot / "assets/imported/props/ithappy" / category
        if src.is_dir():
            prop_copied.extend(copy_tree_assets(src, props_dst / category, "props"))

    write_catalog(repo / "SynapticSea/Assets/Resources/Catalogs/KitCatalog_ithappy_scifi_v0.asset")

    missing = [mid for mid in SHARED_V0_MODULE_IDS if not (structural_dst / mid / f"{mid}.glb").exists()]
    if missing:
        raise SystemExit(f"missing KEEP structural GLBs: {missing}")
    if not (structural_dst / "wall_x_junction/wall_x_junction.glb").exists():
        raise SystemExit("wall_x_junction.glb was not imported")

    print(f"structural files: {len(copied)}")
    print(f"prop starter files: {len(prop_copied)}")
    print(f"prop categories indexed: {len(inventory['categories'])}")
    print(f"wrappers: {len(list(wrappers_dst.glob('*.tscn')))}")
    print(f"contracts: {len(list(contracts_dst.glob('*_contract.json')))}")


if __name__ == "__main__":
    main()
