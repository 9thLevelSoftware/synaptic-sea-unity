"""Pure helpers for prop visual sidecar metadata and GLB inspection."""

from __future__ import annotations

import hashlib
import json
import math
import re
import struct
from pathlib import Path
from typing import Any, Optional


FORBIDDEN_GAMEPLAY_FIELDS = {
    "mass",
    "power_draw",
    "condition_default",
    "linked_system",
    "linked_subcomponent",
    "role_weights",
    "room_id",
    "cell",
    "approach_cell",
    "sequence",
    "type",
    "kind",
    "steps",
    "loot_table",
    "item_form",
}

_ROOT_FIELDS = {
    "schema_version",
    "document_kind",
    "asset_id",
    "prop_kind",
    "visual_scene_path",
    "binding",
    "placement",
    "source",
    "bounds",
    "collision_policy",
    "provenance",
    "extensions",
}
_REQUIRED_ROOT_FIELDS = tuple(
    (
        "schema_version",
        "document_kind",
        "asset_id",
        "prop_kind",
        "visual_scene_path",
        "binding",
        "placement",
        "source",
        "bounds",
        "collision_policy",
        "provenance",
        "extensions",
    )
)
_BINDING_FIELDS = {"namespace", "ids"}
_PLACEMENT_FIELDS = {
    "origin",
    "offset_m",
    "rotation_degrees",
    "allowed_yaw_deg",
    "scale",
    "surface",
}
_SOURCE_FIELDS = {"sha256", "byte_size", "mesh_count", "gltf_version"}
_BOUNDS_FIELDS = {"local_min_m", "local_max_m"}
_PROVENANCE_FIELDS = {"license_state", "source_platform"}
_NAMESPACE_BY_KIND = {
    "component": "component_id",
    "dressing": "visual_prop_id",
    "objective": "gameplay_placement_id",
}
_ALLOWED_ORIGINS = ("scene_origin", "marker_anchor")
_SHA256_PATTERN = re.compile(r"^[0-9a-f]{64}$")
_SEMVER_PATTERN = re.compile(r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")
_GLTF_VERSION_PATTERN = re.compile(r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")


def _finite_number(value: Any) -> Optional[float]:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return None
    try:
        converted = float(value)
    except (OverflowError, TypeError, ValueError):
        return None
    return converted if math.isfinite(converted) else None


def _finite_vector(value: Any, size: int = 3) -> Optional[list[float]]:
    if not isinstance(value, (list, tuple)) or len(value) != size:
        return None
    result: list[float] = []
    for item in value:
        converted = _finite_number(item)
        if converted is None:
            return None
        result.append(converted)
    return result


def _reject_json_constant(value: str) -> None:
    raise ValueError(f"non-standard JSON constant: {value}")


def _read_glb_chunks(data: bytes) -> tuple[dict[str, Any], Optional[bytes]]:
    if len(data) < 12:
        raise ValueError("malformed GLB header: fewer than 12 bytes")
    magic, version, declared_length = struct.unpack_from("<4sII", data, 0)
    if magic != b"glTF":
        raise ValueError("malformed GLB header: invalid magic")
    if version != 2:
        raise ValueError(f"unsupported GLB version: {version}")
    if declared_length < 12 or declared_length != len(data):
        raise ValueError("malformed GLB header: invalid total length")
    if declared_length % 4 != 0:
        raise ValueError("malformed GLB header: total length is not 4-byte aligned")

    offset = 12
    json_chunk: Optional[bytes] = None
    binary_chunk: Optional[bytes] = None
    unknown_chunk_seen = False
    while offset < declared_length:
        if declared_length - offset < 8:
            raise ValueError("malformed GLB chunk header")
        chunk_length, chunk_type = struct.unpack_from("<I4s", data, offset)
        if chunk_length % 4 != 0:
            raise ValueError("malformed GLB chunk length: not 4-byte aligned")
        chunk_start = offset + 8
        chunk_end = chunk_start + chunk_length
        if chunk_end > declared_length:
            raise ValueError("malformed GLB chunk length")
        chunk = data[chunk_start:chunk_end]
        if json_chunk is None:
            if chunk_type != b"JSON":
                raise ValueError("malformed GLB: first chunk must be JSON")
            json_chunk = chunk
        elif chunk_type == b"JSON":
            if json_chunk is not None:
                raise ValueError("malformed GLB: duplicate JSON chunk")
            json_chunk = chunk
        elif chunk_type == b"BIN\x00":
            if binary_chunk is not None:
                raise ValueError("malformed GLB: duplicate BIN chunk")
            if unknown_chunk_seen:
                raise ValueError("malformed GLB: illegal chunk sequence")
            binary_chunk = chunk
        else:
            unknown_chunk_seen = True
        offset = chunk_end

    if offset != declared_length or json_chunk is None:
        raise ValueError("malformed GLB: missing JSON chunk")
    try:
        text = json_chunk.decode("utf-8")
        start = 0
        while start < len(text) and text[start] in " \t\r\n":
            start += 1
        document, end = json.JSONDecoder().raw_decode(text, start)
        if any(character != " " for character in text[end:]):
            raise ValueError("illegal JSON chunk padding")
        document = json.loads(text[start:end], parse_constant=_reject_json_constant)
    except (RecursionError, UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ValueError("malformed GLB JSON chunk") from exc
    except ValueError as exc:
        raise ValueError("malformed GLB JSON chunk") from exc
    if not isinstance(document, dict):
        raise ValueError("malformed GLB JSON chunk: document is not an object")
    return document, binary_chunk


def _validate_bin_payload(document: dict[str, Any], binary_chunk: Optional[bytes]) -> None:
    if binary_chunk is None:
        return
    buffers = document.get("buffers")
    if not isinstance(buffers, list) or not buffers or not isinstance(buffers[0], dict):
        raise ValueError("GLB BIN chunk has no buffer[0] linkage")
    buffer = buffers[0]
    if "uri" in buffer:
        raise ValueError("GLB BIN chunk cannot be linked to an external buffer")
    buffer_length = buffer.get("byteLength")
    if isinstance(buffer_length, bool) or not isinstance(buffer_length, int) or buffer_length < 0:
        raise ValueError("GLB BIN buffer has invalid byteLength")
    if len(binary_chunk) < buffer_length:
        raise ValueError("GLB BIN buffer is shorter than declared")
    extra_length = len(binary_chunk) - buffer_length
    if extra_length > 3:
        raise ValueError("GLB BIN buffer has too much padding")
    if any(binary_chunk[buffer_length:]):
        raise ValueError("GLB BIN buffer padding must be zero")


def _accessor_vector(value: Any, label: str) -> list[float]:
    vector = _finite_vector(value)
    if vector is None:
        raise ValueError(f"invalid non-finite or non-3D {label}")
    return vector


def _validate_position_accessor(accessor: dict[str, Any]) -> None:
    if accessor.get("componentType") != 5126 or accessor.get("type") != "VEC3":
        raise ValueError("POSITION accessor must be a FLOAT VEC3")
    count = accessor.get("count")
    if isinstance(count, bool) or not isinstance(count, int) or count <= 0:
        raise ValueError("POSITION accessor has an invalid count")
    if "sparse" in accessor:
        raise ValueError("sparse POSITION accessors are unsupported")
    if ("min" in accessor) != ("max" in accessor):
        raise ValueError("POSITION accessor must provide both min and max")
    if "byteOffset" in accessor and "bufferView" not in accessor:
        raise ValueError("POSITION accessor byteOffset requires a bufferView")


def _round_bound(value: float) -> float:
    rounded = round(value, 6)
    return 0.0 if rounded == 0 else rounded


def _position_data_range(
    accessor: dict[str, Any],
    buffer_views: list[Any],
    buffers: list[Any],
    binary_chunk: Optional[bytes],
) -> tuple[int, int, int]:
    view_index = accessor.get("bufferView")
    if isinstance(view_index, bool) or not isinstance(view_index, int):
        raise ValueError("POSITION accessor has no valid bufferView")
    if view_index < 0 or view_index >= len(buffer_views) or not isinstance(buffer_views[view_index], dict):
        raise ValueError("POSITION accessor bufferView is out of range")
    view = buffer_views[view_index]
    buffer_index = view.get("buffer")
    if isinstance(buffer_index, bool) or not isinstance(buffer_index, int) or buffer_index != 0:
        raise ValueError("POSITION accessor does not reference the GLB BIN buffer")
    if not buffers or not isinstance(buffers[0], dict):
        raise ValueError("POSITION accessor does not reference the GLB BIN buffer")
    if "uri" in buffers[0]:
        raise ValueError("POSITION accessor references an external buffer")

    view_offset = view.get("byteOffset", 0)
    accessor_offset = accessor.get("byteOffset", 0)
    view_length = view.get("byteLength")
    stride = view.get("byteStride", 12)
    if any(isinstance(item, bool) or not isinstance(item, int) or item < 0 for item in (view_offset, accessor_offset)):
        raise ValueError("POSITION accessor has invalid byte offsets")
    if isinstance(view_length, bool) or not isinstance(view_length, int) or view_length < 0:
        raise ValueError("POSITION accessor has invalid bufferView length")
    if (
        isinstance(stride, bool)
        or not isinstance(stride, int)
        or stride < 12
        or stride > 252
        or stride % 4 != 0
    ):
        raise ValueError("POSITION accessor has invalid byte stride")
    if view_offset % 4 != 0 or accessor_offset % 4 != 0:
        raise ValueError("POSITION accessor has invalid byte offsets")

    buffer_length = buffers[0].get("byteLength")
    if isinstance(buffer_length, bool) or not isinstance(buffer_length, int) or buffer_length < 0:
        raise ValueError("GLB BIN buffer has invalid byteLength")
    if binary_chunk is None:
        raise ValueError("POSITION accessor has no GLB BIN buffer")
    if buffer_length > len(binary_chunk):
        raise ValueError("GLB BIN buffer is shorter than declared")

    count = accessor["count"]
    start = view_offset + accessor_offset
    end = start + (count - 1) * stride + 12
    view_end = view_offset + view_length
    if view_end > buffer_length or start < view_offset or end > view_end or end > buffer_length or end > len(binary_chunk):
        raise ValueError("POSITION accessor exceeds its GLB bufferView")
    return start, end, stride


def _scan_position_accessor(
    accessor: dict[str, Any],
    buffer_views: list[Any],
    buffers: list[Any],
    binary_chunk: Optional[bytes],
) -> tuple[list[float], list[float]]:
    if binary_chunk is None:
        raise ValueError("POSITION accessor has no min/max and GLB has no BIN chunk")
    start, _end, stride = _position_data_range(accessor, buffer_views, buffers, binary_chunk)
    count = accessor["count"]

    minimum = [math.inf, math.inf, math.inf]
    maximum = [-math.inf, -math.inf, -math.inf]
    for index in range(count):
        position_offset = start + index * stride
        values = struct.unpack_from("<3f", binary_chunk, position_offset)
        if not all(math.isfinite(value) for value in values):
            raise ValueError("POSITION accessor contains non-finite bounds")
        for axis, value in enumerate(values):
            minimum[axis] = min(minimum[axis], value)
            maximum[axis] = max(maximum[axis], value)
    return minimum, maximum


def read_glb_metadata(path: Path) -> dict[str, Any]:
    """Read deterministic source evidence and local POSITION bounds from a GLB."""
    path = Path(path)
    data = path.read_bytes()
    document, binary_chunk = _read_glb_chunks(data)
    _validate_bin_payload(document, binary_chunk)

    asset = document.get("asset")
    if not isinstance(asset, dict) or asset.get("version") != "2.0":
        raise ValueError("GLB JSON has no valid asset.version")
    if "minVersion" in asset:
        min_version = asset["minVersion"]
        match = _GLTF_VERSION_PATTERN.fullmatch(min_version) if isinstance(min_version, str) else None
        if match is None or (int(match.group(1)), int(match.group(2))) > (2, 0):
            raise ValueError("GLB JSON has an incompatible asset.minVersion")
    gltf_version = asset["version"]

    meshes = document.get("meshes")
    if not isinstance(meshes, list) or not meshes:
        raise ValueError("GLB contains no meshes")
    accessors = document.get("accessors", [])
    buffer_views = document.get("bufferViews", [])
    buffers = document.get("buffers", [])
    if not isinstance(accessors, list) or not isinstance(buffer_views, list) or not isinstance(buffers, list):
        raise ValueError("GLB JSON has invalid accessor or buffer tables")

    minimum = [math.inf, math.inf, math.inf]
    maximum = [-math.inf, -math.inf, -math.inf]
    position_count = 0
    for mesh in meshes:
        if not isinstance(mesh, dict) or not isinstance(mesh.get("primitives"), list):
            raise ValueError("GLB mesh has invalid primitives")
        for primitive in mesh["primitives"]:
            if not isinstance(primitive, dict):
                raise ValueError("GLB mesh has an invalid primitive")
            attributes = primitive.get("attributes", {})
            if not isinstance(attributes, dict) or "POSITION" not in attributes:
                continue
            accessor_index = attributes["POSITION"]
            if isinstance(accessor_index, bool) or not isinstance(accessor_index, int):
                raise ValueError("GLB POSITION accessor index is invalid")
            if accessor_index < 0 or accessor_index >= len(accessors) or not isinstance(accessors[accessor_index], dict):
                raise ValueError("GLB POSITION accessor index is out of range")
            accessor = accessors[accessor_index]
            _validate_position_accessor(accessor)
            if "min" in accessor and "max" in accessor:
                _position_data_range(accessor, buffer_views, buffers, binary_chunk)
                lower = _accessor_vector(accessor["min"], "POSITION min")
                upper = _accessor_vector(accessor["max"], "POSITION max")
            else:
                lower, upper = _scan_position_accessor(accessor, buffer_views, buffers, binary_chunk)
            for axis in range(3):
                if lower[axis] > upper[axis]:
                    raise ValueError("POSITION accessor has inverted bounds")
                minimum[axis] = min(minimum[axis], lower[axis])
                maximum[axis] = max(maximum[axis], upper[axis])
            position_count += 1

    if position_count == 0:
        raise ValueError("GLB contains no POSITION accessors")
    if not all(math.isfinite(value) for value in minimum + maximum):
        raise ValueError("GLB contains non-finite bounds")

    minimum = [_round_bound(value) for value in minimum]
    maximum = [_round_bound(value) for value in maximum]

    return {
        "sha256": hashlib.sha256(data).hexdigest(),
        "byte_size": len(data),
        "gltf_version": gltf_version,
        "mesh_count": len(meshes),
        "local_min_m": minimum,
        "local_max_m": maximum,
    }


def _scan_forbidden_keys(value: Any, found: set[str], in_extensions: bool = False) -> None:
    if isinstance(value, dict):
        for key, child in value.items():
            if not isinstance(key, str):
                continue
            if not in_extensions and key in FORBIDDEN_GAMEPLAY_FIELDS:
                found.add(key)
            if key == "extensions":
                continue
            _scan_forbidden_keys(child, found, in_extensions=in_extensions)
    elif isinstance(value, list):
        for child in value:
            _scan_forbidden_keys(child, found, in_extensions=in_extensions)


def _unknown_fields(errors: list[str], value: Any, allowed: set[str], label: str) -> None:
    if not isinstance(value, dict):
        return
    for key in sorted(value):
        if key not in allowed and key not in FORBIDDEN_GAMEPLAY_FIELDS:
            errors.append(f"unknown {label} field: {key}")


def _require_dict(errors: list[str], document: dict[str, Any], key: str) -> Optional[dict[str, Any]]:
    value = document.get(key)
    if not isinstance(value, dict):
        errors.append(f"{key} must be an object")
        return None
    return value


def _validate_vector_field(errors: list[str], value: Any, label: str) -> None:
    if _finite_vector(value) is None:
        errors.append(f"{label} must be a finite 3-vector")


def _validate_nonnegative_number(errors: list[str], value: Any, label: str) -> None:
    converted = _finite_number(value)
    if converted is None or converted < 0:
        errors.append(f"{label} must be a finite non-negative number")


def validate_sidecar(sidecar: dict, glb_path: Path, project_root: Path) -> list[str]:
    """Return stable contract diagnostics for one prop visual sidecar."""
    if not isinstance(sidecar, dict):
        return ["sidecar must be an object"]

    errors: list[str] = []
    forbidden: set[str] = set()
    _scan_forbidden_keys(sidecar, forbidden)
    errors.extend(f"forbidden gameplay field: {key}" for key in sorted(forbidden))

    for key in _REQUIRED_ROOT_FIELDS:
        if key not in sidecar:
            errors.append(f"missing required field: {key}")
    schema_version = sidecar.get("schema_version")
    if isinstance(schema_version, str):
        match = _SEMVER_PATTERN.fullmatch(schema_version)
        if match is None:
            errors.append("schema_version must use major.minor.patch")
        elif int(match.group(1)) != 1:
            errors.append(f"unsupported schema major version: {match.group(1)}")
    elif "schema_version" in sidecar:
        errors.append("schema_version must be a string")

    if sidecar.get("document_kind") != "prop_visual_binding":
        errors.append("document_kind must be prop_visual_binding")
    if sidecar.get("collision_policy") != "none_visual_only":
        errors.append("collision_policy must be none_visual_only")

    glb_path = Path(glb_path)
    project_root = Path(project_root).resolve()
    expected_glb = glb_path.resolve()
    if expected_glb != project_root and project_root not in expected_glb.parents:
        errors.append("GLB path must be contained in project root")
        expected_res_path = f"res://{expected_glb.as_posix().lstrip('/')}"
    else:
        expected_relative = expected_glb.relative_to(project_root).as_posix()
        expected_res_path = f"res://{expected_relative}"

    asset_id = sidecar.get("asset_id")
    if not isinstance(asset_id, str) or not asset_id or re.fullmatch(r"[a-z0-9][a-z0-9_-]*", asset_id) is None:
        errors.append("asset_id must be a lowercase identifier")
    elif asset_id != glb_path.stem:
        errors.append(f"asset_id must match GLB basename: {glb_path.stem}")

    prop_kind = sidecar.get("prop_kind")
    if prop_kind not in _NAMESPACE_BY_KIND:
        errors.append("prop_kind must be one of: component, dressing, objective")

    visual_scene_path = sidecar.get("visual_scene_path")
    visual_candidate: Optional[Path] = None
    if not isinstance(visual_scene_path, str) or not visual_scene_path.startswith("res://"):
        errors.append("path must be a contained res:// path")
    else:
        raw_relative = visual_scene_path[6:]
        parts = raw_relative.split("/")
        if not raw_relative or raw_relative.startswith("/") or any(part in {".", ".."} for part in parts):
            errors.append("path must be a contained res:// path")
        else:
            visual_candidate = (project_root / Path(*parts)).resolve()
            if visual_candidate != project_root and project_root not in visual_candidate.parents:
                errors.append("path must be a contained res:// path")
            elif visual_candidate != expected_glb:
                errors.append(f"visual_scene_path must match GLB path: {expected_res_path}")

    binding = _require_dict(errors, sidecar, "binding")
    if binding is not None:
        _unknown_fields(errors, binding, _BINDING_FIELDS, "binding")
        namespace = binding.get("namespace")
        if namespace not in _NAMESPACE_BY_KIND.values():
            errors.append("binding.namespace is invalid")
        elif prop_kind in _NAMESPACE_BY_KIND and namespace != _NAMESPACE_BY_KIND[prop_kind]:
            errors.append(f"binding.namespace must be {_NAMESPACE_BY_KIND[prop_kind]} for {prop_kind}")
        ids = binding.get("ids")
        if not isinstance(ids, list) or not ids or any(not isinstance(item, str) or not item for item in ids):
            errors.append("binding.ids must be a non-empty array of strings")
        else:
            seen_ids: set[str] = set()
            duplicate_ids: set[str] = set()
            for identifier in ids:
                if identifier in seen_ids:
                    duplicate_ids.add(identifier)
                else:
                    seen_ids.add(identifier)
            for identifier in sorted(duplicate_ids):
                errors.append(f"duplicate binding id: {identifier}")

    placement = _require_dict(errors, sidecar, "placement")
    if placement is not None:
        _unknown_fields(errors, placement, _PLACEMENT_FIELDS, "placement")
        for key in ("origin", "offset_m", "rotation_degrees", "allowed_yaw_deg", "scale"):
            if key not in placement:
                errors.append(f"missing placement field: {key}")
        if "origin" in placement:
            origin = placement.get("origin")
            if not isinstance(origin, str):
                errors.append("placement.origin must be a string")
            elif origin not in _ALLOWED_ORIGINS:
                errors.append("placement.origin must be one of: marker_anchor, scene_origin")
        _validate_vector_field(errors, placement.get("offset_m"), "placement.offset_m")
        _validate_vector_field(errors, placement.get("rotation_degrees"), "placement.rotation_degrees")
        yaw = placement.get("allowed_yaw_deg")
        if not isinstance(yaw, list) or not yaw:
            errors.append("placement.allowed_yaw_deg must be a non-empty array")
        else:
            invalid_yaw = False
            seen_yaw: set[int | float] = set()
            duplicate_yaw: set[int | float] = set()
            for value in yaw:
                if _finite_number(value) is None:
                    invalid_yaw = True
                    continue
                if value in seen_yaw:
                    duplicate_yaw.add(value)
                else:
                    seen_yaw.add(value)
            if invalid_yaw:
                errors.append("placement.allowed_yaw_deg must contain finite numbers")
            else:
                for value in sorted(duplicate_yaw, key=float):
                    errors.append(f"duplicate placement.allowed_yaw_deg: {float(value):g}")
        if "scale" not in placement:
            pass
        elif (scale := _finite_number(placement["scale"])) is None or scale <= 0:
            errors.append("placement.scale must be a finite positive number")
        if "surface" in placement:
            if prop_kind not in ("dressing", "objective"):
                errors.append("placement.surface is only allowed for dressing or objective")
            elif placement.get("surface") not in ("floor", "wall", "ceiling"):
                errors.append("placement.surface must be one of: ceiling, floor, wall")

    source = _require_dict(errors, sidecar, "source")
    if source is not None:
        _unknown_fields(errors, source, _SOURCE_FIELDS, "source")
        sha256 = source.get("sha256")
        if not isinstance(sha256, str) or _SHA256_PATTERN.fullmatch(sha256) is None:
            errors.append("source.sha256 must be 64 lowercase hexadecimal characters")
        _validate_nonnegative_number(errors, source.get("byte_size"), "source.byte_size")
        if isinstance(source.get("byte_size"), float):
            errors.append("source.byte_size must be an integer")
        mesh_count = source.get("mesh_count")
        if isinstance(mesh_count, bool) or not isinstance(mesh_count, int) or mesh_count <= 0:
            errors.append("source.mesh_count must be a positive integer")
        if source.get("gltf_version") != "2.0":
            errors.append("source.gltf_version must be 2.0")

    bounds = _require_dict(errors, sidecar, "bounds")
    if bounds is not None:
        _unknown_fields(errors, bounds, _BOUNDS_FIELDS, "bounds")
        minimum = _finite_vector(bounds.get("local_min_m"))
        maximum = _finite_vector(bounds.get("local_max_m"))
        if minimum is None:
            errors.append("bounds.local_min_m must be a finite 3-vector")
        if maximum is None:
            errors.append("bounds.local_max_m must be a finite 3-vector")
        if minimum is not None and maximum is not None:
            if any(low > high for low, high in zip(minimum, maximum)):
                errors.append("bounds.local_min_m must not exceed local_max_m")

    provenance = _require_dict(errors, sidecar, "provenance")
    if provenance is not None:
        _unknown_fields(errors, provenance, _PROVENANCE_FIELDS, "provenance")
        for key in sorted(_PROVENANCE_FIELDS):
            if key not in provenance:
                errors.append(f"missing provenance field: {key}")
            elif not isinstance(provenance[key], str) or not provenance[key]:
                errors.append(f"provenance.{key} must be a non-empty string")

    extensions = sidecar.get("extensions")
    if not isinstance(extensions, dict):
        errors.append("extensions must be an object")

    _unknown_fields(errors, sidecar, _ROOT_FIELDS, "root")
    return errors


def write_canonical_json(path: Path, document: dict) -> None:
    """Write compact, recursively sorted-key JSON with one trailing newline."""
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = json.dumps(
        document,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
        allow_nan=False,
    )
    path.write_text(payload + "\n", encoding="utf-8")
