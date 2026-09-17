#!/usr/bin/env python3
"""Export structural Blender source collections as staged GLB files.

The script is intentionally Blender-compatible but keeps its argument parsing
free of a top-level ``bpy`` import so the CLI contract can be tested with a
normal Python interpreter.

Run it through Blender, for example::

    blender --background --factory-startup \
        --python tools/export_structural_glb.py -- \
        --blend-path /absolute/path/module.blend \
        --staging-dir /absolute/path/staging \
        --module floor_1x1
"""

from __future__ import annotations

import argparse
import os
from pathlib import Path
import re
import sys
from typing import Any, Sequence


_ALLOWED_VARIANTS = frozenset(("intact", "damaged", "breached"))
_MODULE_ID_PATTERN = re.compile(r"[A-Za-z0-9_-]+")


def build_parser() -> argparse.ArgumentParser:
    """Build the parser without importing Blender's Python API."""

    parser = argparse.ArgumentParser(
        description="Export tagged structural Blender collections as GLB files."
    )
    parser.add_argument(
        "--blend-path",
        required=True,
        type=Path,
        help="source .blend file to open",
    )
    parser.add_argument(
        "--staging-dir",
        required=True,
        type=Path,
        help="directory receiving exported GLB files",
    )
    parser.add_argument(
        "--module",
        default=None,
        help="module identifier; detected from the source when omitted",
    )
    return parser


def _argv_from_blender(argv: Sequence[str] | None = None) -> list[str]:
    """Return script arguments, removing Blender's ``--`` separator."""

    if argv is not None:
        raw = list(argv)
    else:
        raw = list(sys.argv[1:])
    if "--" in raw:
        return raw[raw.index("--") + 1 :]
    return raw


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    """Parse and validate CLI arguments without importing ``bpy``."""

    parser = build_parser()
    args = parser.parse_args(_argv_from_blender(argv))
    if not args.blend_path.is_file():
        parser.error(f"blend file does not exist: {args.blend_path}")
    return args


def _require_bpy() -> Any:
    """Import Blender lazily, after parser validation has completed."""

    return __import__("bpy")


def _detect_module_id(bpy: Any) -> str:
    """Detect a module ID from scene metadata, then the ModuleRoot object."""

    scene = bpy.context.scene
    module_id = scene.get("module_id")
    if module_id is not None and str(module_id):
        return str(module_id)

    root_names = sorted(
        obj.name
        for obj in bpy.data.objects
        if obj.name.startswith("ModuleRoot_") and obj.name[len("ModuleRoot_") :]
    )
    if root_names:
        return root_names[0][len("ModuleRoot_") :]
    return "unknown"


def _tagged_export_collections(bpy: Any) -> list[Any]:
    """Return collections explicitly tagged for structural export."""

    return sorted(
        (
            collection
            for collection in bpy.data.collections
            if collection.name.startswith("Export_")
            and collection.get("variant_role") is not None
        ),
        key=lambda collection: collection.name,
    )


def _collections_to_export(bpy: Any) -> list[Any]:
    """Find tagged variants, or use Geometry as the intact fallback."""

    tagged = _tagged_export_collections(bpy)
    if tagged:
        return tagged

    geometry = bpy.data.collections.get("Geometry")
    if geometry is not None:
        return [geometry]

    raise FileNotFoundError(
        "no tagged Export_* collections and no Geometry collection found"
    )


def _variant_role(collection: Any) -> str:
    """Return and validate the role stored on an export collection."""

    role = str(collection.get("variant_role", "intact")).strip().lower()
    if role not in _ALLOWED_VARIANTS:
        raise ValueError(
            f"unsupported variant_role {role!r} on collection {collection.name!r}; "
            f"expected one of {', '.join(sorted(_ALLOWED_VARIANTS))}"
        )
    return role


def _select_collection_objects(bpy: Any, collection: Any) -> None:
    """Select exactly the objects directly contained by a collection."""

    bpy.ops.object.select_all(action="DESELECT")
    selected = list(collection.objects)
    for obj in selected:
        obj.select_set(True)
    if selected:
        bpy.context.view_layer.objects.active = selected[0]


def _output_path(staging_dir: Path, module_id: str, variant: str) -> Path:
    """Return the canonical staging filename for a module variant."""

    suffix = "" if variant == "intact" else f"_{variant}"
    return staging_dir / f"{module_id}{suffix}.glb"


def _validate_module_id(module_id: str) -> str:
    """Ensure the module ID can only name a file within the staging directory."""

    if not _MODULE_ID_PATTERN.fullmatch(module_id):
        raise ValueError(
            f"invalid module id {module_id!r}; expected only alphanumeric, "
            "underscore, or hyphen characters"
        )
    return module_id


def _temporary_output_path(glb_path: Path) -> Path:
    """Return a temporary sibling path for an atomic GLB export."""

    return glb_path.with_name(f".{glb_path.stem}.tmp{glb_path.suffix}")


def export_blend(args: argparse.Namespace, bpy: Any) -> list[Path]:
    """Open the source and export every selected variant collection."""

    bpy.ops.wm.open_mainfile(filepath=str(args.blend_path))
    module_id = _validate_module_id(str(args.module or _detect_module_id(bpy)))
    collections = _collections_to_export(bpy)

    variants: list[tuple[Any, str]] = []
    seen_variants: set[str] = set()
    for collection in collections:
        variant = (
            _variant_role(collection)
            if collection.name.startswith("Export_")
            else "intact"
        )
        if variant in seen_variants:
            raise ValueError(
                f"duplicate variant_role {variant!r} on collection {collection.name!r}"
            )
        seen_variants.add(variant)
        variants.append((collection, variant))

    args.staging_dir.mkdir(parents=True, exist_ok=True)
    exported: list[Path] = []
    for collection, variant in variants:
        glb_path = _output_path(args.staging_dir, module_id, variant)
        temporary_path = _temporary_output_path(glb_path)
        temporary_path.unlink(missing_ok=True)
        _select_collection_objects(bpy, collection)
        try:
            result = bpy.ops.export_scene.gltf(
                filepath=str(temporary_path),
                export_format="GLB",
                export_apply=True,
                use_selection=True,
            )
            if result is not None and "CANCELLED" in result:
                print(
                    f"WARNING: Blender cancelled GLB export for module={module_id} "
                    f"variant={variant}",
                    file=sys.stderr,
                )
                continue
            if not temporary_path.is_file():
                raise FileNotFoundError(
                    f"Blender did not create temporary GLB output: {temporary_path}"
                )
            byte_count = temporary_path.stat().st_size
            if byte_count <= 0:
                raise ValueError(
                    f"Blender created an empty temporary GLB output: {temporary_path}"
                )
            os.replace(temporary_path, glb_path)
        finally:
            temporary_path.unlink(missing_ok=True)
        print(
            "STRUCTURAL_GLB_EXPORTED "
            f"module={module_id} variant={variant} "
            f"glb={glb_path} bytes={byte_count}"
        )
        exported.append(glb_path)
    return exported


def main(argv: Sequence[str] | None = None) -> int:
    """Run the staged export and return a process exit status."""

    args = parse_args(argv)
    args.blend_path = args.blend_path.resolve()
    args.staging_dir = args.staging_dir.resolve()
    bpy = _require_bpy()
    try:
        export_blend(args, bpy)
    except (FileNotFoundError, OSError, RuntimeError, ValueError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1
    finally:
        bpy.ops.wm.quit_blender()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
