#!/usr/bin/env python3
"""Create and refresh deterministic prop visual sidecars and their index."""

from __future__ import annotations

import argparse
import copy
import json
import sys
from pathlib import Path
from typing import Any

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from tools.prop_visual_metadata import read_glb_metadata, validate_sidecar, write_canonical_json


PROP_GROUPS = ("components", "dressing", "objectives")
EXPECTED_ASSET_COUNTS = {"component": 11, "dressing": 11, "objective": 4}
DRESSING_SURFACES = {
    "cable_tray": "wall",
    "emergency_wall": "wall",
    "practical_overhead": "ceiling",
    "cargo_pallet": "floor",
    "focused_work_lamp": "floor",
    "generic_crate": "floor",
    "generic_locker": "floor",
    "maintenance_bench": "floor",
    "medical_cabinet": "floor",
    "salvage_cart": "floor",
    "service_rack": "floor",
}
OBJECTIVE_IDS = {
    "medbay_terminal": ["medbay_terminal"],
    "reactor_control_panel": ["reactor_control_panel"],
    "repair_junction": ["maintenance_breaker_panel"],
    "supply_cache": ["cargo_supply_cache", "supply_cache"],
}


def _relative(project_root: Path, path: Path) -> str:
    return path.relative_to(project_root).as_posix()


def _ensure_contained(project_root: Path, path: Path) -> None:
    root = project_root.resolve()
    resolved = path.resolve(strict=False)
    if resolved != root and root not in resolved.parents:
        try:
            relative = path.relative_to(project_root).as_posix()
        except ValueError:
            relative = path.as_posix()
        raise ValueError(f"path escapes project root via symlink: {relative}")


def _res_path(project_root: Path, path: Path) -> str:
    _ensure_contained(project_root, path)
    return f"res://{_relative(project_root, path)}"


def _binding(prop_kind: str, asset_id: str) -> dict[str, Any]:
    if prop_kind == "component":
        return {"namespace": "component_id", "ids": [asset_id]}
    if prop_kind == "dressing":
        return {"namespace": "visual_prop_id", "ids": [asset_id]}
    return {"namespace": "gameplay_placement_id", "ids": OBJECTIVE_IDS[asset_id]}


def _canonical_sidecar(project_root: Path, glb_path: Path, prop_kind: str) -> dict[str, Any]:
    asset_id = glb_path.stem
    metadata = read_glb_metadata(glb_path)
    placement: dict[str, Any] = {
        "origin": "scene_origin",
        "offset_m": [0.0, 0.0, 0.0],
        "rotation_degrees": [0.0, 0.0, 0.0],
        "allowed_yaw_deg": [0.0, 90.0, 180.0, 270.0],
        "scale": 1.0,
    }
    if prop_kind == "dressing":
        placement["surface"] = DRESSING_SURFACES[asset_id]
    return {
        "schema_version": "1.0.0",
        "document_kind": "prop_visual_binding",
        "asset_id": asset_id,
        "prop_kind": prop_kind,
        "visual_scene_path": _res_path(project_root, glb_path),
        "binding": _binding(prop_kind, asset_id),
        "placement": placement,
        "source": {
            "sha256": metadata["sha256"],
            "byte_size": metadata["byte_size"],
            "mesh_count": metadata["mesh_count"],
            "gltf_version": metadata["gltf_version"],
        },
        "bounds": {
            "local_min_m": metadata["local_min_m"],
            "local_max_m": metadata["local_max_m"],
        },
        "collision_policy": "none_visual_only",
        "provenance": {"license_state": "self-authored", "source_platform": "self-authored"},
        "extensions": {},
    }


def _iter_glbs(project_root: Path) -> list[tuple[str, Path]]:
    prop_root = project_root / "assets/imported/props"
    kind_by_group = {"components": "component", "dressing": "dressing", "objectives": "objective"}
    result: list[tuple[str, Path]] = []
    for prop_kind in PROP_GROUPS:
        group = prop_root / prop_kind
        _ensure_contained(project_root, group)
        for glb_path in sorted(group.glob("*.glb"), key=lambda path: path.name):
            _ensure_contained(project_root, glb_path)
            result.append((kind_by_group[prop_kind], glb_path))
    return result


def _validate_complete_inventory(
    glbs: list[tuple[str, Path]],
    expected_asset_ids: dict[str, set[str]],
) -> None:
    actual_asset_ids: dict[str, set[str]] = {"component": set(), "dressing": set(), "objective": set()}
    for prop_kind, glb_path in glbs:
        actual_asset_ids[prop_kind].add(glb_path.stem)

    diagnostics: list[str] = []
    for prop_kind in ("component", "dressing", "objective"):
        expected = expected_asset_ids[prop_kind]
        if len(expected) != EXPECTED_ASSET_COUNTS[prop_kind]:
            diagnostics.append(
                f"invalid governed {prop_kind} asset inventory: expected {EXPECTED_ASSET_COUNTS[prop_kind]} assets, found {len(expected)}"
            )
        for asset_id in sorted(expected - actual_asset_ids[prop_kind]):
            diagnostics.append(f"missing {prop_kind} asset: {asset_id}")
        for asset_id in sorted(actual_asset_ids[prop_kind] - expected):
            diagnostics.append(f"unexpected {prop_kind} asset: {asset_id}")

    if diagnostics:
        raise ValueError("; ".join(diagnostics))


def _sidecar_path(glb_path: Path) -> Path:
    return glb_path.with_name(f"{glb_path.stem}.sidecar.json")


def _reject_duplicate_keys(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    value: dict[str, Any] = {}
    for key, item in pairs:
        if key in value:
            raise ValueError(f"duplicate JSON object key: {key}")
        value[key] = item
    return value


def _load_json(path: Path) -> dict[str, Any]:
    value = json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=_reject_duplicate_keys)
    if not isinstance(value, dict):
        raise ValueError(f"JSON document must be an object: {path}")
    return value


def _index_record(sidecar: dict[str, Any]) -> dict[str, Any]:
    # Keep the generated record a complete sidecar-derived record. This keeps
    # the runtime catalog independent of sidecar location while retaining all
    # validated visual transform/source evidence.
    return copy.deepcopy(sidecar)


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


def _load_component_ids(project_root: Path) -> set[str]:
    path = project_root / "data/components/component_catalog.json"
    document = _load_json(path)
    components = document.get("components")
    if not isinstance(components, dict):
        raise ValueError("component catalog has no components object")
    return {key for key in components if isinstance(key, str)}


def _load_objective_ids(project_root: Path) -> set[str]:
    found: set[str] = set()
    for path in sorted((project_root / "data/procgen").rglob("gameplay_slice.json")):
        _walk_placement_ids(_load_json(path), found)
    return found


def _expected_binding_ids(prop_kind: str, asset_id: str) -> list[str]:
    if prop_kind in ("component", "dressing"):
        return [asset_id]
    return OBJECTIVE_IDS.get(asset_id, [])


def _validate_index_sidecar(
    project_root: Path,
    prop_kind: str,
    glb_path: Path,
    sidecar_path: Path,
    sidecar: dict[str, Any],
    component_ids: set[str],
    objective_ids: set[str],
) -> None:
    diagnostics = validate_sidecar(sidecar, glb_path, project_root)
    if sidecar.get("prop_kind") != prop_kind:
        diagnostics.append(f"prop_kind mismatch: {_relative(project_root, sidecar_path)}")

    try:
        metadata = read_glb_metadata(glb_path)
    except (OSError, ValueError) as exc:
        diagnostics.append(f"GLB metadata error: {exc}")
    else:
        source = sidecar.get("source")
        if isinstance(source, dict):
            for field in ("sha256", "byte_size", "mesh_count", "gltf_version"):
                if source.get(field) != metadata[field]:
                    diagnostics.append(f"{field} mismatch")
        bounds = sidecar.get("bounds")
        if isinstance(bounds, dict):
            if bounds.get("local_min_m") != metadata["local_min_m"] or bounds.get("local_max_m") != metadata["local_max_m"]:
                diagnostics.append("bounds mismatch")

    binding = sidecar.get("binding")
    ids = binding.get("ids", []) if isinstance(binding, dict) else []
    expected_ids = _expected_binding_ids(prop_kind, glb_path.stem)
    if isinstance(ids, list) and expected_ids and ids != expected_ids:
        diagnostics.append("binding ids mismatch")
    if isinstance(ids, list):
        if prop_kind == "component":
            for identifier in ids:
                if identifier not in component_ids:
                    diagnostics.append(f"unknown component_id: {identifier}")
        elif prop_kind == "dressing":
            for identifier in ids:
                if identifier not in DRESSING_SURFACES:
                    diagnostics.append(f"unknown visual_prop_id: {identifier}")
        elif prop_kind == "objective":
            for identifier in ids:
                if identifier not in objective_ids:
                    diagnostics.append(f"unknown gameplay_placement_id: {identifier}")

    if diagnostics:
        relative = _relative(project_root, sidecar_path)
        raise ValueError(f"invalid sidecar {relative}: {'; '.join(diagnostics)}")


def build_index(project_root: Path) -> dict[str, Any]:
    groups: dict[str, dict[str, Any]] = {"components": {}, "objectives": {}, "dressing": {}}
    component_ids = _load_component_ids(project_root)
    objective_ids = _load_objective_ids(project_root)
    glbs = _iter_glbs(project_root)
    _validate_complete_inventory(
        glbs,
        {"component": component_ids, "dressing": set(DRESSING_SURFACES), "objective": set(OBJECTIVE_IDS)},
    )
    owned_component_ids: set[str] = set()
    for prop_kind, glb_path in glbs:
        sidecar_path = _sidecar_path(glb_path)
        _ensure_contained(project_root, sidecar_path)
        if not sidecar_path.is_file():
            raise FileNotFoundError(f"missing sidecar: {_relative(project_root, sidecar_path)}")
        sidecar = _load_json(sidecar_path)
        _validate_index_sidecar(
            project_root,
            prop_kind,
            glb_path,
            sidecar_path,
            sidecar,
            component_ids,
            objective_ids,
        )
        binding = sidecar["binding"]
        if not isinstance(binding, dict) or not isinstance(binding.get("ids"), list):
            raise ValueError(f"sidecar has no binding ids: {_relative(project_root, sidecar_path)}")
        index_group = {"component": "components", "dressing": "dressing", "objective": "objectives"}[prop_kind]
        for binding_id in binding["ids"]:
            if not isinstance(binding_id, str) or not binding_id:
                raise ValueError(f"sidecar has invalid binding id: {_relative(project_root, sidecar_path)}")
            if binding_id in groups[index_group]:
                raise ValueError(f"duplicate binding id: {binding_id}")
            groups[index_group][binding_id] = _index_record(sidecar)
            if prop_kind == "component":
                owned_component_ids.add(binding_id)
    for identifier in sorted(component_ids - owned_component_ids):
        raise ValueError(f"missing component_id: {identifier}")
    return {
        "schema_version": "1.0.0",
        "document_kind": "prop_visual_binding_index",
        "components": dict(sorted(groups["components"].items())),
        "objectives": dict(sorted(groups["objectives"].items())),
        "dressing": dict(sorted(groups["dressing"].items())),
    }


def _canonical_text(document: dict[str, Any]) -> str:
    return json.dumps(document, ensure_ascii=False, sort_keys=True, separators=(",", ":"), allow_nan=False) + "\n"


def _write_missing(project_root: Path) -> list[str]:
    changes: list[str] = []
    for prop_kind, glb_path in _iter_glbs(project_root):
        sidecar_path = _sidecar_path(glb_path)
        _ensure_contained(project_root, sidecar_path)
        if sidecar_path.exists():
            continue
        sidecar = _canonical_sidecar(project_root, glb_path, prop_kind)
        write_canonical_json(sidecar_path, sidecar)
        changes.append(f"created sidecar: {sidecar_path.relative_to(project_root).as_posix()}")
    return changes


def _refresh_derived(project_root: Path, asset_id: str) -> str:
    matches = [(kind, path) for kind, path in _iter_glbs(project_root) if path.stem == asset_id]
    if len(matches) != 1:
        raise ValueError(f"expected one GLB for asset_id {asset_id}, found {len(matches)}")
    prop_kind, glb_path = matches[0]
    sidecar_path = _sidecar_path(glb_path)
    _ensure_contained(project_root, sidecar_path)
    if not sidecar_path.is_file():
        raise FileNotFoundError(f"missing sidecar: {_relative(project_root, sidecar_path)}")
    existing = _load_json(sidecar_path)
    refreshed = _canonical_sidecar(project_root, glb_path, prop_kind)
    for field in ("binding", "placement", "provenance", "extensions"):
        if field in existing:
            refreshed[field] = copy.deepcopy(existing[field])
    write_canonical_json(sidecar_path, refreshed)
    return f"refreshed derived fields: {sidecar_path.relative_to(project_root).as_posix()}"


def _check_sidecars(project_root: Path) -> list[str]:
    errors: list[str] = []
    for prop_kind, glb_path in _iter_glbs(project_root):
        sidecar_path = _sidecar_path(glb_path)
        _ensure_contained(project_root, sidecar_path)
        relative = _relative(project_root, sidecar_path)
        if not sidecar_path.is_file():
            errors.append(f"missing sidecar: {relative}")
            continue
        try:
            actual = _load_json(sidecar_path)
            expected = _canonical_sidecar(project_root, glb_path, prop_kind)
        except (OSError, ValueError, KeyError) as exc:
            errors.append(f"cannot canonicalize sidecar {relative}: {exc}")
            continue
        for field in ("binding", "placement", "provenance", "extensions"):
            if field in actual:
                expected[field] = copy.deepcopy(actual[field])
        if sidecar_path.read_text(encoding="utf-8") != _canonical_text(expected):
            errors.append(f"sidecar differs from canonical output: {relative}")
    prop_root = project_root / "assets/imported/props"
    for group in PROP_GROUPS:
        group_root = prop_root / group
        glb_names = {path.stem for path in group_root.glob("*.glb")}
        for sidecar_path in sorted(group_root.glob("*.sidecar.json")):
            asset_id = sidecar_path.name.removesuffix(".sidecar.json")
            if asset_id not in glb_names:
                errors.append(f"sidecar has no matching GLB: {sidecar_path.relative_to(project_root).as_posix()}")
    return errors


def _check_index(project_root: Path) -> list[str]:
    path = project_root / "data/props/visual_bindings.generated.json"
    try:
        _ensure_contained(project_root, path)
        expected = build_index(project_root)
    except (OSError, ValueError, KeyError) as exc:
        return [str(exc)]
    if not path.is_file():
        return [f"missing generated index: {path.relative_to(project_root).as_posix()}"]
    try:
        _load_json(path)
    except (OSError, ValueError) as exc:
        return [f"invalid generated index: {exc}"]
    if path.read_text(encoding="utf-8") != _canonical_text(expected):
        return ["generated index differs from sidecars"]
    return []


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project-root", type=Path, default=Path("."))
    parser.add_argument("--write-missing", action="store_true")
    parser.add_argument("--write-index", action="store_true")
    parser.add_argument("--refresh-derived", action="store_true")
    parser.add_argument("--asset-id")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args(argv)
    project_root = args.project_root.expanduser().resolve()
    if args.refresh_derived and not args.asset_id:
        parser.error("--refresh-derived requires --asset-id")
    if not (args.write_missing or args.write_index or args.refresh_derived or args.check):
        parser.error("one of --write-missing, --write-index, --refresh-derived, or --check is required")

    diagnostics: list[str] = []
    had_error = False
    try:
        if args.write_missing:
            diagnostics.extend(_write_missing(project_root))
        if args.refresh_derived:
            diagnostics.append(_refresh_derived(project_root, args.asset_id))
        if args.write_index:
            index_path = project_root / "data/props/visual_bindings.generated.json"
            _ensure_contained(project_root, index_path)
            write_canonical_json(index_path, build_index(project_root))
            diagnostics.append(f"wrote generated index: {_relative(project_root, index_path)}")
        if args.check:
            diagnostics.extend(_check_sidecars(project_root))
            diagnostics.extend(_check_index(project_root))
    except (OSError, ValueError, KeyError) as exc:
        diagnostics.append(str(exc))
        had_error = True

    for diagnostic in diagnostics:
        print(
            diagnostic,
            file=sys.stderr
            if had_error or (args.check and not diagnostic.startswith(("created ", "refreshed ", "wrote ")))
            else sys.stdout,
        )
    if had_error or (args.check and any(diagnostic for diagnostic in diagnostics)):
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
