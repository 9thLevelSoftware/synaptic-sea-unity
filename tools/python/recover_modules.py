#!/usr/bin/env python3
"""Recover structural Blender sources from existing imported GLB files.

This script is intentionally source-only: it imports the contract-owned GLB for
 each module, adds placement and authoring helpers, and writes a Blender source
 file plus its deterministic ``.source.json`` record.  It never exports a GLB
 and it does not author or modify visual geometry.

Run from the repository root with Blender, for example::

    blender --background --factory-startup \
        --python tools/recover_modules.py -- \
        --project-root . \
        --source-root /tmp/ship-structural-source \
        --module floor_1x1

The contract coordinate system is Y-up.  Blender is Z-up, so contract points
are converted with ``[x, y, z] -> [x, z, y]`` for helper placement.
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import sys
import tempfile
from typing import Any, Sequence

try:
    from tools.structural_source_contract import (
        STRUCTURAL_SOURCE_MODULE_IDS,
        StructuralSourceSpec,
        build_source_record,
        canonical_json,
        load_source_spec,
        source_output_paths,
    )
except ModuleNotFoundError:  # Blender runs a script with ``tools`` as sys.path[0].
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
    from tools.structural_source_contract import (
        STRUCTURAL_SOURCE_MODULE_IDS,
        StructuralSourceSpec,
        build_source_record,
        canonical_json,
        load_source_spec,
        source_output_paths,
    )


_ORIENTATION_SOURCE = "socket-id-cardinal-convention"

# Blender is deliberately loaded lazily.  This module is also imported by the
# Python-only CLI tests, where bpy is unavailable.
_BPY: Any | None = None


def _require_bpy() -> Any:
    """Import Blender's Python API only after CLI validation has completed."""

    global _BPY
    if _BPY is None:
        _BPY = __import__("bpy")
    return _BPY


def y_up_to_z_up(vector: Sequence[float]) -> tuple[float, float, float]:
    """Convert a contract vector ``[x, y, z]`` to Blender ``[x, y, z]``."""

    return (float(vector[0]), float(vector[2]), float(vector[1]))


def clear_scene() -> None:
    """Remove the current scene contents and restore metric scene units."""

    bpy = _require_bpy()
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)

    # Keep Blender's default root collection, but remove all collections made by
    # a previous recovery so repeated runs cannot create ``.001`` collections.
    for collection in list(bpy.data.collections):
        if collection.name != "Collection":
            bpy.data.collections.remove(collection)
    try:
        bpy.ops.outliner.orphans_purge(do_recursive=True)
    except RuntimeError:
        # The operator is context-sensitive in some Blender background builds;
        # deleting the linked scene objects/collections above is sufficient.
        pass

    scene = bpy.context.scene
    scene.unit_settings.system = "METRIC"
    scene.unit_settings.scale_length = 1.0
    scene.unit_settings.length_unit = "METERS"


def create_collection(name: str) -> Any:
    """Create and link a named top-level scene collection."""

    bpy = _require_bpy()
    collection = bpy.data.collections.new(name)
    bpy.context.scene.collection.children.link(collection)
    return collection


def link_object_to_collection(obj: Any, collection: Any) -> None:
    """Move an object into exactly one collection."""

    for existing in list(obj.users_collection):
        existing.objects.unlink(obj)
    collection.objects.link(obj)


def create_box(
    name: str,
    size: Sequence[float],
    location: Sequence[float],
    collection: Any,
) -> Any:
    """Create an applied-transform cube in Blender metres."""

    bpy = _require_bpy()
    bpy.ops.mesh.primitive_cube_add(size=1.0, location=tuple(location))
    obj = bpy.context.active_object
    obj.name = name
    link_object_to_collection(obj, collection)
    obj.dimensions = tuple(float(value) for value in size)
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    return obj


def socket_normal_from_id(socket_id: str) -> tuple[float, float, float]:
    """Return the local cardinal normal implied by a socket identifier."""

    lower = socket_id.lower()
    if "north" in lower or "up" in lower:
        return (0.0, 1.0, 0.0)
    if "south" in lower or "down" in lower:
        return (0.0, -1.0, 0.0)
    if "east" in lower or "right" in lower:
        return (1.0, 0.0, 0.0)
    if "west" in lower or "left" in lower:
        return (-1.0, 0.0, 0.0)
    return (0.0, 0.0, 1.0)


def _new_empty(name: str, empty_display_type: str, root: Any, helpers: Any) -> Any:
    bpy = _require_bpy()
    empty = bpy.data.objects.new(name, None)
    empty.empty_display_type = empty_display_type
    empty.empty_display_size = 0.3
    empty.location = (0.0, 0.0, 0.0)
    helpers.objects.link(empty)
    empty.parent = root
    return empty


def create_origin(root: Any, helpers: Any) -> Any:
    """Add the contract-defined placement origin marker."""

    origin = _new_empty("Origin", "PLAIN_AXES", root, helpers)
    origin.empty_display_size = 0.5
    origin["marker_kind"] = "placement_origin"
    origin["origin_policy"] = "contract_defined"
    return origin


def create_floor_center(root: Any, helpers: Any) -> Any:
    """Add the stable floor-center anchor expected by structural wrappers."""

    anchor = _new_empty("Anchor_FloorCenter", "PLAIN_AXES", root, helpers)
    anchor["marker_kind"] = "floor_center"
    anchor["position_contract_y_up"] = json.dumps([0.0, 0.0, 0.0])
    anchor["position_blender_z_up"] = json.dumps([0.0, 0.0, 0.0])
    return anchor


def add_sockets(spec: StructuralSourceSpec, root: Any, helpers: Any) -> list[Any]:
    """Add contract sockets and their Blender-coordinate metadata."""

    bpy = _require_bpy()
    socket_objects: list[Any] = []
    for socket in spec.sockets:
        empty = bpy.data.objects.new(socket.anchor_name, None)
        empty.empty_display_type = "ARROWS"
        empty.empty_display_size = 0.3
        empty.location = socket.position_z_up
        helpers.objects.link(empty)
        empty.parent = root

        empty["socket_id"] = socket.socket_id
        empty["kind"] = socket.kind
        empty["compatible_kinds"] = json.dumps(list(socket.compatible_kinds))
        empty["position_contract_y_up"] = json.dumps(list(socket.position_y_up))
        empty["position_blender_z_up"] = json.dumps(list(socket.position_z_up))
        empty["orientation_source"] = _ORIENTATION_SOURCE
        # Retain the useful authoring orientation fields from the old helper.
        empty["normal_local"] = json.dumps(list(socket_normal_from_id(socket.socket_id)))
        empty["up_local"] = json.dumps([0.0, 0.0, 1.0])
        empty["rotation_step_deg"] = 90
        empty["terminal_allowed"] = True
        socket_objects.append(empty)
    return socket_objects


def add_collision_proxy(spec: StructuralSourceSpec, root: Any, helpers: Any) -> Any:
    """Add a hidden-render wireframe proxy from the contract bounds."""

    size_y_up = tuple(
        maximum - minimum
        for minimum, maximum in zip(
            spec.bounds_min_y_up, spec.bounds_max_y_up, strict=True
        )
    )
    center_y_up = tuple(
        (minimum + maximum) / 2.0
        for minimum, maximum in zip(
            spec.bounds_min_y_up, spec.bounds_max_y_up, strict=True
        )
    )
    size_z_up = y_up_to_z_up(size_y_up)
    center_z_up = y_up_to_z_up(center_y_up)

    collision = create_box("CollisionProxy", size_z_up, center_z_up, helpers)
    collision.display_type = "WIRE"
    collision.hide_render = True
    collision.parent = root
    collision["proxy_shape"] = spec.collision_proxy_shape
    collision["nav_blocker"] = spec.nav_blocker
    collision["bounds_min_contract_y_up"] = json.dumps(list(spec.bounds_min_y_up))
    collision["bounds_max_contract_y_up"] = json.dumps(list(spec.bounds_max_y_up))
    collision["bounds_min_blender_z_up"] = json.dumps(
        list(y_up_to_z_up(spec.bounds_min_y_up))
    )
    collision["bounds_max_blender_z_up"] = json.dumps(
        list(y_up_to_z_up(spec.bounds_max_y_up))
    )
    return collision


def set_root_properties(root: Any, spec: StructuralSourceSpec) -> None:
    """Attach the immutable source contract identity to the module root."""

    root["module_id"] = spec.module_id
    root["kit_id"] = spec.kit_id
    root["module_family"] = spec.module_family
    root["grid_step_m"] = spec.grid_step_m
    root["footprint_cells"] = list(spec.footprint_cells)
    root["placement_origin"] = spec.placement_origin
    root["contract_sha256"] = spec.contract_sha256
    root["source_glb_sha256"] = spec.source_glb_sha256
    # These bounds are useful for source inspection and collision validation.
    root["bounds_min_contract_y_up"] = json.dumps(list(spec.bounds_min_y_up))
    root["bounds_max_contract_y_up"] = json.dumps(list(spec.bounds_max_y_up))


def import_existing_glb(spec: StructuralSourceSpec, root: Any, geometry: Any) -> list[Any]:
    """Import the contract GLB and move every imported object into Geometry."""

    bpy = _require_bpy()
    before_names = {obj.name for obj in bpy.data.objects}
    result = bpy.ops.import_scene.gltf(filepath=str(spec.source_glb_path))
    if "FINISHED" not in result:
        raise RuntimeError(f"Blender failed to import source GLB: {spec.source_glb_path}")

    imported = [obj for obj in bpy.data.objects if obj.name not in before_names]
    if not imported:
        raise RuntimeError(f"source GLB imported no objects: {spec.source_glb_path}")

    # Save world matrices before changing parents so imported visual transforms
    # remain exactly as authored in the existing GLB.
    world_matrices = {obj.name: obj.matrix_world.copy() for obj in imported}
    for obj in imported:
        link_object_to_collection(obj, geometry)
        obj.parent = root
        obj.matrix_world = world_matrices[obj.name]
    return imported


def _atomic_write_bytes(path: Path, payload: bytes) -> None:
    """Atomically replace a file with bytes in the same directory."""

    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary_name = tempfile.mkstemp(
        prefix=f".{path.name}.", suffix=".tmp", dir=str(path.parent)
    )
    try:
        with os.fdopen(fd, "wb") as handle:
            handle.write(payload)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary_name, path)
    except BaseException:
        try:
            os.unlink(temporary_name)
        except FileNotFoundError:
            pass
        raise


def _save_blend_atomically(blend_path: Path) -> None:
    """Save the current Blender file to a temporary sibling, then replace."""

    bpy = _require_bpy()
    blend_path.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary_name = tempfile.mkstemp(
        prefix=f".{blend_path.stem}.", suffix=".blend", dir=str(blend_path.parent)
    )
    os.close(fd)
    try:
        os.unlink(temporary_name)
        result = bpy.ops.wm.save_as_mainfile(filepath=temporary_name)
        if "FINISHED" not in result or not os.path.isfile(temporary_name):
            raise RuntimeError(f"Blender failed to save source: {blend_path}")
        os.replace(temporary_name, blend_path)
    except BaseException:
        try:
            os.unlink(temporary_name)
        except FileNotFoundError:
            pass
        raise


def recover_one(args: argparse.Namespace, module_id: str) -> dict[str, Any]:
    """Recover one allowlisted module into a Blender source and source record."""

    spec = load_source_spec(args.project_root, module_id)
    blend_path, record_path = source_output_paths(args.source_root, module_id)
    if not args.overwrite and (blend_path.exists() or record_path.exists()):
        existing = [str(path) for path in (blend_path, record_path) if path.exists()]
        raise FileExistsError(
            f"refusing to overwrite existing structural source output(s): {', '.join(existing)}"
        )

    clear_scene()
    bpy = _require_bpy()
    geometry = create_collection("Geometry")
    helpers = create_collection("AuthoringHelpers")

    root = bpy.data.objects.new(f"ModuleRoot_{spec.module_id}", None)
    root.location = (0.0, 0.0, 0.0)
    root.rotation_euler = (0.0, 0.0, 0.0)
    root.scale = (1.0, 1.0, 1.0)
    bpy.context.scene.collection.objects.link(root)
    set_root_properties(root, spec)

    imported_objects = import_existing_glb(spec, root, geometry)
    create_origin(root, helpers)
    create_floor_center(root, helpers)
    socket_objects = add_sockets(spec, root, helpers)
    add_collision_proxy(spec, root, helpers)

    scene = bpy.context.scene
    scene["module_id"] = spec.module_id
    scene["socket_count"] = len(socket_objects)
    scene["collision_proxy_name"] = "CollisionProxy"
    scene["source_glb_sha256"] = spec.source_glb_sha256
    scene["contract_sha256"] = spec.contract_sha256

    _save_blend_atomically(blend_path)
    source_record = build_source_record(spec, blend_path)
    _atomic_write_bytes(record_path, canonical_json(source_record))

    print(
        "STRUCTURAL_SOURCE_RECOVERED "
        f"module={spec.module_id} sockets={len(socket_objects)} "
        f"blend={blend_path} source_record={record_path}"
    )
    return {
        "module_id": spec.module_id,
        "socket_count": len(socket_objects),
        "imported_object_count": len(imported_objects),
        "blend": blend_path,
        "source_record": record_path,
    }


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Recover existing structural GLBs into Blender source files."
    )
    parser.add_argument(
        "--project-root",
        required=True,
        type=Path,
        help="repository root containing structural contracts and imported GLBs",
    )
    parser.add_argument(
        "--source-root",
        required=True,
        type=Path,
        help="directory receiving one .blend/.source.json pair per module",
    )
    selection = parser.add_mutually_exclusive_group(required=True)
    selection.add_argument(
        "--module",
        action="append",
        metavar="MODULE_ID",
        help="recover one module; repeat for multiple modules",
    )
    selection.add_argument(
        "--all",
        action="store_true",
        help="recover all allowlisted structural source modules",
    )
    parser.add_argument(
        "--overwrite",
        action="store_true",
        help="replace existing .blend/.source.json outputs",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="print planned output paths without importing Blender",
    )
    return parser


def _argv_from_blender(argv: Sequence[str] | None) -> list[str]:
    if argv is not None:
        return list(argv)
    raw = list(sys.argv[1:])
    if "--" in raw:
        return raw[raw.index("--") + 1 :]
    return raw


def _selected_module_ids(args: argparse.Namespace) -> tuple[str, ...]:
    if args.all:
        return STRUCTURAL_SOURCE_MODULE_IDS
    # The mutually-exclusive group is required, so this is a non-empty list.
    return tuple(args.module)


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    """Parse and validate CLI arguments without importing Blender."""

    parser = build_parser()
    args = parser.parse_args(_argv_from_blender(argv))
    module_ids = _selected_module_ids(args)
    invalid = [
        module_id
        for module_id in module_ids
        if module_id not in STRUCTURAL_SOURCE_MODULE_IDS
    ]
    if invalid:
        parser.error(
            "unsupported structural source module(s): " + ", ".join(repr(item) for item in invalid)
        )
    return args


def _print_dry_run(args: argparse.Namespace, module_ids: Sequence[str]) -> None:
    for module_id in module_ids:
        blend_path, record_path = source_output_paths(args.source_root, module_id)
        print(
            "STRUCTURAL_SOURCE_PLAN "
            f"module={module_id} blend={blend_path} source_record={record_path}"
        )


def main(argv: Sequence[str] | None = None) -> int:
    """Run the requested source recoveries and return a process exit status."""

    args = parse_args(argv)
    module_ids = _selected_module_ids(args)
    if args.dry_run:
        _print_dry_run(args, module_ids)
        return 0

    # This is intentionally after parse_args and the allowlist check: invalid
    # module requests must work under Python 3.11 without Blender installed.
    _require_bpy()
    try:
        for module_id in module_ids:
            recover_one(args, module_id)
    except (FileExistsError, ValueError, FileNotFoundError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
