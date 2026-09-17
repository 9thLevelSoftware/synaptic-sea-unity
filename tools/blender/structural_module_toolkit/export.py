"""In-process GLB export helpers for the Structural Module Toolkit.

This module intentionally has no top-level Blender import.  It can therefore be
unit-tested with a small fake ``bpy`` object while the actual export operator is
only invoked inside Blender.
"""

from __future__ import annotations

import os
from pathlib import Path
import re
from typing import Any


_ALLOWED_VARIANTS = frozenset(("intact", "damaged", "breached"))
_MODULE_ID_PATTERN = re.compile(r"[A-Za-z0-9_-]+")


def validate_module_id(module_id: str) -> str:
    """Reject IDs that could escape the configured staging root."""

    if not isinstance(module_id, str) or not _MODULE_ID_PATTERN.fullmatch(module_id):
        raise ValueError(
            f"invalid module id {module_id!r}; expected only alphanumeric, "
            "underscore, or hyphen characters"
        )
    return module_id


def detect_module_id(bpy: Any) -> str:
    """Read the module ID from scene metadata or a ModuleRoot object."""

    scene = bpy.context.scene
    for key in ("module_id", "structural_module_id"):
        value = scene.get(key)
        if value is not None and str(value).strip():
            return str(value).strip()
    for attribute in ("structural_module_id",):
        value = getattr(scene, attribute, "")
        if str(value).strip():
            return str(value).strip()

    root_names = sorted(
        obj.name
        for obj in bpy.data.objects
        if obj.name.startswith("ModuleRoot_") and obj.name[len("ModuleRoot_") :]
    )
    if root_names:
        return root_names[0][len("ModuleRoot_") :]
    raise ValueError("could not detect module ID; set the module ID or create a ModuleRoot")


def tagged_export_collections(bpy: Any) -> list[Any]:
    """Return collections explicitly marked for variant export."""

    return sorted(
        (
            collection
            for collection in bpy.data.collections
            if collection.name.startswith("Export_")
        ),
        key=lambda collection: collection.name,
    )


def collections_to_export(bpy: Any) -> list[Any]:
    """Find tagged export collections, falling back to the Geometry collection."""

    tagged = tagged_export_collections(bpy)
    if tagged:
        return tagged
    geometry = bpy.data.collections.get("Geometry")
    if geometry is not None:
        return [geometry]
    raise FileNotFoundError(
        "no tagged Export_* collections and no Geometry collection found"
    )


def variant_role(collection: Any) -> str:
    """Read and validate an export collection's variant role."""

    role = str(collection.get("variant_role", "intact")).strip().lower()
    if role not in _ALLOWED_VARIANTS:
        raise ValueError(
            f"unsupported variant_role {role!r} on collection {collection.name!r}; "
            f"expected one of {', '.join(sorted(_ALLOWED_VARIANTS))}"
        )
    return role


def output_path(staging_root: Path, module_id: str, variant: str) -> Path:
    """Return ``staging_root/<module_id>/<module>[_{variant}].glb``."""

    validate_module_id(module_id)
    if variant not in _ALLOWED_VARIANTS:
        raise ValueError(f"unsupported export variant: {variant!r}")
    suffix = "" if variant == "intact" else f"_{variant}"
    return staging_root / module_id / f"{module_id}{suffix}.glb"


def _temporary_output_path(glb_path: Path) -> Path:
    return glb_path.with_name(f".{glb_path.stem}.tmp{glb_path.suffix}")


def select_collection_objects(bpy: Any, collection: Any) -> list[Any]:
    """Select exactly the objects directly contained by one collection."""

    bpy.ops.object.select_all(action="DESELECT")
    selected = list(collection.objects)
    for obj in selected:
        obj.select_set(True)
    if selected:
        bpy.context.view_layer.objects.active = selected[0]
    return selected


def export_scene_to_staging(
    bpy: Any,
    staging_root: Path | str,
    module_id: str | None = None,
) -> list[Path]:
    """Export all tagged variants (or Geometry) into a module staging folder."""

    module_id = validate_module_id(module_id or detect_module_id(bpy))
    staging_root = Path(staging_root).expanduser()
    collections = collections_to_export(bpy)

    variants: list[tuple[Any, str]] = []
    seen_variants: set[str] = set()
    for collection in collections:
        role = variant_role(collection) if collection.name.startswith("Export_") else "intact"
        if role in seen_variants:
            raise ValueError(
                f"duplicate variant_role {role!r} on collection {collection.name!r}"
            )
        seen_variants.add(role)
        variants.append((collection, role))

    exported: list[Path] = []
    for collection, role in variants:
        glb_path = output_path(staging_root, module_id, role)
        temporary_path = _temporary_output_path(glb_path)
        glb_path.parent.mkdir(parents=True, exist_ok=True)
        temporary_path.unlink(missing_ok=True)
        selected = select_collection_objects(bpy, collection)
        if not selected:
            raise ValueError(f"export collection {collection.name!r} contains no objects")
        try:
            result = bpy.ops.export_scene.gltf(
                filepath=str(temporary_path),
                export_format="GLB",
                export_apply=True,
                use_selection=True,
            )
            if result is not None and "CANCELLED" in result:
                raise RuntimeError(
                    f"Blender cancelled GLB export for module={module_id} variant={role}"
                )
            if not temporary_path.is_file():
                raise FileNotFoundError(
                    f"Blender did not create temporary GLB output: {temporary_path}"
                )
            byte_count = temporary_path.stat().st_size
            if byte_count <= 0:
                raise ValueError(f"Blender created an empty GLB output: {temporary_path}")
            os.replace(temporary_path, glb_path)
        finally:
            temporary_path.unlink(missing_ok=True)

        print(
            "STRUCTURAL_GLB_EXPORTED "
            f"module={module_id} variant={role} glb={glb_path} bytes={byte_count}"
        )
        exported.append(glb_path)
    return exported


__all__ = [
    "collections_to_export",
    "detect_module_id",
    "export_scene_to_staging",
    "output_path",
    "select_collection_objects",
    "tagged_export_collections",
    "validate_module_id",
    "variant_role",
]
