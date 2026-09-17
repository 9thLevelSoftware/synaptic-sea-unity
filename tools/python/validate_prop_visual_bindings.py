#!/usr/bin/env python3
"""Validate all imported prop visual sidecars, GLB evidence, mappings, and index."""

from __future__ import annotations

import argparse
import json
import sys
from collections import defaultdict
from pathlib import Path
from typing import Any

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from tools.generate_prop_sidecars import (  # noqa: E402
    DRESSING_SURFACES,
    OBJECTIVE_IDS,
    PROP_GROUPS,
    _ensure_contained,
    build_index,
)
from tools.prop_visual_metadata import read_glb_metadata, validate_sidecar  # noqa: E402


EXPECTED_COUNTS = {"components": 11, "dressing": 11, "objectives": 4}
KIND_BY_GROUP = {"components": "component", "dressing": "dressing", "objectives": "objective"}


def _relative(project_root: Path, path: Path) -> str:
    return path.relative_to(project_root).as_posix()


def _reject_duplicate_keys(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    value: dict[str, Any] = {}
    for key, item in pairs:
        if key in value:
            raise ValueError(f"duplicate JSON object key: {key}")
        value[key] = item
    return value


def _load_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=_reject_duplicate_keys)


def _canonical_text(document: dict[str, Any]) -> str:
    return json.dumps(document, ensure_ascii=False, sort_keys=True, separators=(",", ":"), allow_nan=False) + "\n"


def _load_component_ids(project_root: Path, errors: list[str]) -> set[str]:
    path = project_root / "data/components/component_catalog.json"
    try:
        document = _load_json(path)
    except (OSError, ValueError) as exc:
        errors.append(f"cannot read component catalog: {exc}")
        return set()
    components = document.get("components") if isinstance(document, dict) else None
    if not isinstance(components, dict):
        errors.append("component catalog has no components object")
        return set()
    return {key for key in components if isinstance(key, str)}


def _walk_placement_ids(value: Any, found: set[str]) -> None:
    if isinstance(value, dict):
        placement_id = value.get("placement_id")
        if isinstance(placement_id, str) and placement_id:
            found.add(placement_id)
        for child in value.values():
            _walk_placement_ids(child, found)
    elif isinstance(value, list):
        for child in value:
            _walk_placement_ids(child, found)


def _load_objective_ids(project_root: Path, errors: list[str]) -> set[str]:
    found: set[str] = set()
    files = sorted((project_root / "data/procgen").rglob("gameplay_slice.json"))
    for path in files:
        try:
            _walk_placement_ids(_load_json(path), found)
        except (OSError, ValueError) as exc:
            errors.append(f"cannot read gameplay slice {_relative(project_root, path)}: {exc}")
    return found


def _expected_binding_ids(group: str, asset_id: str) -> list[str]:
    if group == "components" or group == "dressing":
        return [asset_id]
    return OBJECTIVE_IDS.get(asset_id, [])


def _validate_inventory(project_root: Path, errors: list[str]) -> list[tuple[str, Path, Path, dict[str, Any] | None]]:
    inventory: list[tuple[str, Path, Path, dict[str, Any] | None]] = []
    prop_root = project_root / "assets/imported/props"
    for group in PROP_GROUPS:
        group_root = prop_root / group
        glbs = sorted(group_root.glob("*.glb"), key=lambda path: path.name)
        sidecars = sorted(group_root.glob("*.sidecar.json"), key=lambda path: path.name)
        expected_count = EXPECTED_COUNTS[group]
        if len(glbs) != expected_count:
            errors.append(f"expected {expected_count} {group} GLBs, found {len(glbs)}")
        if len(sidecars) != expected_count:
            errors.append(f"expected {expected_count} {group} sidecars, found {len(sidecars)}")
        glb_names = {path.stem for path in glbs}
        unsafe_sidecars: set[Path] = set()
        for sidecar_path in sidecars:
            asset_id = sidecar_path.name.removesuffix(".sidecar.json")
            if asset_id not in glb_names:
                errors.append(f"sidecar has no matching GLB: {_relative(project_root, sidecar_path)}")
            try:
                _ensure_contained(project_root, sidecar_path)
            except ValueError as exc:
                errors.append(str(exc))
                unsafe_sidecars.add(sidecar_path)
        for glb_path in glbs:
            try:
                _ensure_contained(project_root, glb_path)
            except ValueError as exc:
                errors.append(str(exc))
                inventory.append((group, glb_path, glb_path.with_name(f"{glb_path.stem}.sidecar.json"), None))
                continue
            sidecar_path = glb_path.with_name(f"{glb_path.stem}.sidecar.json")
            if sidecar_path in unsafe_sidecars:
                inventory.append((group, glb_path, sidecar_path, None))
                continue
            if not sidecar_path.is_file():
                errors.append(f"missing sidecar: {_relative(project_root, sidecar_path)}")
                inventory.append((group, glb_path, sidecar_path, None))
                continue
            try:
                sidecar = _load_json(sidecar_path)
            except (OSError, ValueError) as exc:
                errors.append(f"invalid sidecar {_relative(project_root, sidecar_path)}: {exc}")
                inventory.append((group, glb_path, sidecar_path, None))
                continue
            if not isinstance(sidecar, dict):
                errors.append(f"invalid sidecar {_relative(project_root, sidecar_path)}: document must be an object")
                inventory.append((group, glb_path, sidecar_path, None))
                continue
            expected_kind = KIND_BY_GROUP[group]
            if sidecar.get("prop_kind") != expected_kind:
                errors.append(f"prop_kind mismatch: {_relative(project_root, sidecar_path)}")
            diagnostics = validate_sidecar(sidecar, glb_path, project_root)
            errors.extend(f"{_relative(project_root, sidecar_path)}: {diagnostic}" for diagnostic in diagnostics)
            try:
                metadata = read_glb_metadata(glb_path)
            except (OSError, ValueError) as exc:
                errors.append(f"GLB metadata error {_relative(project_root, glb_path)}: {exc}")
            else:
                source = sidecar.get("source")
                if isinstance(source, dict):
                    for field in ("sha256", "byte_size", "mesh_count", "gltf_version"):
                        if source.get(field) != metadata[field]:
                            errors.append(f"{field} mismatch: {_relative(project_root, sidecar_path)}")
                bounds = sidecar.get("bounds")
                if isinstance(bounds, dict):
                    if bounds.get("local_min_m") != metadata["local_min_m"] or bounds.get("local_max_m") != metadata["local_max_m"]:
                        errors.append(f"bounds mismatch: {_relative(project_root, sidecar_path)}")
            inventory.append((group, glb_path, sidecar_path, sidecar))
    return inventory


def _validate_mappings(
    inventory: list[tuple[str, Path, Path, dict[str, Any] | None]],
    project_root: Path,
    errors: list[str],
) -> None:
    component_ids = _load_component_ids(project_root, errors)
    objective_ids = _load_objective_ids(project_root, errors)
    component_owners: dict[str, list[str]] = defaultdict(list)
    dressing_owners: dict[str, list[str]] = defaultdict(list)
    objective_owners: dict[str, list[str]] = defaultdict(list)

    for group, glb_path, sidecar_path, sidecar in inventory:
        if sidecar is None:
            continue
        asset_id = glb_path.stem
        binding = sidecar.get("binding")
        ids = binding.get("ids", []) if isinstance(binding, dict) else []
        if not isinstance(ids, list):
            continue
        expected = _expected_binding_ids(group, asset_id)
        if expected and ids != expected:
            errors.append(f"binding ids mismatch: {_relative(project_root, sidecar_path)}")
        namespace = binding.get("namespace") if isinstance(binding, dict) else None
        if group == "components" and namespace == "component_id":
            for identifier in ids:
                if identifier not in component_ids:
                    errors.append(f"unknown component_id: {identifier}")
                component_owners[identifier].append(asset_id)
        elif group == "dressing" and namespace == "visual_prop_id":
            for identifier in ids:
                if identifier not in DRESSING_SURFACES:
                    errors.append(f"unknown visual_prop_id: {identifier}")
                dressing_owners[identifier].append(asset_id)
        elif group == "objectives" and namespace == "gameplay_placement_id":
            for identifier in ids:
                if identifier not in objective_ids:
                    errors.append(f"unknown gameplay_placement_id: {identifier}")
                objective_owners[identifier].append(asset_id)

    for identifier in sorted(component_ids):
        owners = component_owners.get(identifier, [])
        if not owners:
            errors.append(f"missing component_id: {identifier}")
        elif len(owners) > 1:
            errors.append(f"duplicate component_id: {identifier}")
    for identifier in sorted(dressing_owners):
        if len(dressing_owners[identifier]) > 1:
            errors.append(f"duplicate visual_prop_id: {identifier}")
    for identifier in sorted(objective_owners):
        if len(objective_owners[identifier]) > 1:
            errors.append(f"duplicate gameplay_placement_id: {identifier}")


def validate_project(project_root: Path, check_index: bool = False) -> list[str]:
    errors: list[str] = []
    inventory = _validate_inventory(project_root, errors)
    _validate_mappings(inventory, project_root, errors)
    if check_index:
        index_path = project_root / "data/props/visual_bindings.generated.json"
        try:
            _ensure_contained(project_root, index_path)
        except ValueError as exc:
            errors.append(str(exc))
            return errors
        try:
            expected = build_index(project_root)
            if not index_path.is_file():
                errors.append("missing generated index: data/props/visual_bindings.generated.json")
            else:
                try:
                    actual = _load_json(index_path)
                except (OSError, ValueError) as exc:
                    errors.append(f"invalid generated index: {exc}")
                else:
                    if actual != expected or index_path.read_bytes() != _canonical_text(expected).encode("utf-8"):
                        errors.append("generated index differs from sidecars")
        except (OSError, ValueError, KeyError) as exc:
            errors.append(str(exc))
    return errors


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project-root", type=Path, default=Path("."))
    parser.add_argument("--check-index", action="store_true")
    args = parser.parse_args(argv)
    project_root = args.project_root.expanduser().resolve()
    errors = validate_project(project_root, check_index=args.check_index)
    for error in errors:
        print(f"ERROR: {error}")
    return 1 if errors else 0


if __name__ == "__main__":
    raise SystemExit(main())
