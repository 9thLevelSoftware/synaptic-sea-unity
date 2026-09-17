#!/usr/bin/env python3
"""Strictly validate a normalized Meshy GLB against its asset contract.

The host parser is deliberately independent of Blender.  A report can only be
published after the Blender runtime re-imports the same task-local ``cleaned.glb``.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import re
import stat
import struct
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, Iterable, List, Optional, Sequence, Tuple, Union

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from tools import meshy_governance as governance  # noqa: E402
from tools.meshy_asset_contract import AssetContract, canonical_json_bytes, load_contract  # noqa: E402


BLENDER_PATH = "/opt/homebrew/bin/blender"
PathLike = Union[str, os.PathLike]
Vector3 = Tuple[float, float, float]
Matrix4 = Tuple[float, ...]
_MAX_GLB_BYTES = 512 * 1024 * 1024
_MAX_JSON_BYTES = 16 * 1024 * 1024
_MAX_JSON_DEPTH = 64
_MAX_PROCESS_OUTPUT = 1024 * 1024
# Bound untrusted hinge-sampler decode; lid_open does not need dense baked keys.
_MAX_HINGE_SAMPLES = 1024
_CANONICAL_FIELDS = (
    "schema_version", "document_kind", "status", "task_id", "asset_id",
    "contract_sha256", "sha256", "byte_size", "mesh_count", "triangle_count",
    "material_names", "bounds", "uvs_present", "uv_evidence",
    "blender_reimport_passed", "master_provenance",
)


class BlenderValidationError(ValueError):
    """Raised when a normalized GLB fails a contract quality gate."""


@dataclass(frozen=True)
class ParsedGlb:
    document: Dict[str, Any]
    binary: bytes


@dataclass(frozen=True)
class AccessorData:
    values: Tuple[Tuple[Union[int, float], ...], ...]
    component_type: int
    accessor_type: str
    count: int


@dataclass(frozen=True)
class _BlenderReimportEvidence:
    sha256: str
    byte_size: int
    triangle_count: int


_TASK_ID_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.-]*$")
_ASSET_ID_RE = re.compile(r"^[a-z0-9][a-z0-9_-]*$")
_HASH_RE = re.compile(r"^[0-9a-f]{64}$")


@dataclass(frozen=True)
class PrimitiveData:
    mesh_index: int
    primitive_index: int
    positions: Tuple[Vector3, ...]
    indices: Tuple[int, ...]
    triangles: int
    uv_evidence: Dict[str, Any]
    material_index: Optional[int]


def _reject_duplicate_keys(pairs: List[Tuple[str, Any]]) -> Dict[str, Any]:
    result: Dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON key: " + key)
        result[key] = value
    return result


def _reject_constant(value: str) -> None:
    raise ValueError("non-finite JSON constant: " + value)


def _parse_finite_float(value: str) -> float:
    number = float(value)
    if not math.isfinite(number):
        raise ValueError("non-finite JSON number: " + value)
    return number


def _check_json_depth(value: Any, depth: int = 0) -> None:
    if depth > _MAX_JSON_DEPTH:
        raise BlenderValidationError("GLB JSON nesting depth exceeds the limit")
    if isinstance(value, dict):
        for key, child in value.items():
            if not isinstance(key, str):
                raise BlenderValidationError("GLB JSON object key is not text")
            _check_json_depth(child, depth + 1)
    elif isinstance(value, list):
        for child in value:
            _check_json_depth(child, depth + 1)


def _parse_glb(raw: bytes) -> ParsedGlb:
    """Parse the closed GLB 2.0 container and its strict JSON/BIN linkage."""

    if not isinstance(raw, (bytes, bytearray)):
        raise BlenderValidationError("GLB payload must be bytes")
    raw = bytes(raw)
    if len(raw) < 12 or len(raw) > _MAX_GLB_BYTES or len(raw) % 4:
        raise BlenderValidationError("GLB file length must be 4-byte aligned")
    magic, version, total_length = struct.unpack_from("<4sII", raw, 0)
    if magic != b"glTF":
        raise BlenderValidationError("invalid GLB magic")
    if version != 2:
        raise BlenderValidationError("unsupported GLB version: " + str(version))
    if total_length != len(raw):
        raise BlenderValidationError("GLB header length does not match file size")

    offset = 12
    json_chunk: Optional[bytes] = None
    binary: Optional[bytes] = None
    chunk_index = 0
    while offset < total_length:
        if offset % 4 or offset + 8 > total_length:
            raise BlenderValidationError("GLB chunk header is not aligned or is truncated")
        chunk_length, chunk_type = struct.unpack_from("<I4s", raw, offset)
        if chunk_length > _MAX_GLB_BYTES or chunk_length % 4:
            raise BlenderValidationError("GLB chunk length must be 4-byte aligned")
        chunk_start = offset + 8
        chunk_end = chunk_start + chunk_length
        if chunk_end > total_length:
            raise BlenderValidationError("truncated GLB chunk")
        chunk = raw[chunk_start:chunk_end]
        if chunk_type not in (b"JSON", b"BIN\x00"):
            raise BlenderValidationError("unknown GLB chunk type")
        if chunk_type == b"JSON":
            if chunk_index != 0 or json_chunk is not None:
                raise BlenderValidationError("GLB must contain exactly one first JSON chunk")
            json_chunk = chunk
        else:
            if json_chunk is None or binary is not None:
                raise BlenderValidationError("GLB BIN chunk ordering/cardinality is invalid")
            binary = chunk
        offset = chunk_end
        chunk_index += 1
    if offset != total_length or json_chunk is None:
        raise BlenderValidationError("GLB must contain a JSON chunk")
    if binary is None:
        binary = b""
    if len(json_chunk) > _MAX_JSON_BYTES:
        raise BlenderValidationError("GLB JSON chunk exceeds the validator limit")

    try:
        text = json_chunk.decode("utf-8")
        leading = len(text) - len(text.lstrip(" \t\r\n"))
        document, json_end = json.JSONDecoder(
            object_pairs_hook=_reject_duplicate_keys,
            parse_constant=_reject_constant,
            parse_float=_parse_finite_float,
        ).raw_decode(text, leading)
        if any(character != " " for character in text[json_end:]):
            raise BlenderValidationError("GLB JSON padding must contain spaces only")
        _check_json_depth(document)
    except RecursionError as exc:
        raise BlenderValidationError("GLB JSON nesting depth exceeds the limit") from exc
    except (UnicodeDecodeError, json.JSONDecodeError, TypeError, ValueError) as exc:
        raise BlenderValidationError("GLB JSON chunk is unreadable: " + str(exc)) from exc
    if not isinstance(document, dict):
        raise BlenderValidationError("GLB JSON chunk must be an object")
    asset = document.get("asset")
    if not isinstance(asset, dict) or asset.get("version") != "2.0":
        raise BlenderValidationError("GLB must declare glTF asset version 2.0")
    _validate_document_layout(document, binary)
    return ParsedGlb(document, binary)


def _integer(value: Any, label: str, minimum: int = 0) -> int:
    if not isinstance(value, int) or isinstance(value, bool) or value < minimum:
        raise BlenderValidationError(label + " must be an integer >= " + str(minimum))
    return value


_SUPPORTED_ANIMATION_KINDS = frozenset(
    {
        "hinge",
        "optional_humanoid_biped_rig_after_selection",
        "blender_segmented_chain_rig",
        "godot_multimesh_particles_behavior",
        "static_gameplay_prop",
        "static_mesh",
        "structural_architecture",
        "environment",
        "static",  # legacy fixture contract retained for validator compatibility
    }
)


def _validate_animation_policy(document: Dict[str, Any], contract_document: Dict[str, Any], binary: bytes) -> None:
    animations = document.get("animations", [])
    skins = document.get("skins", [])
    if not isinstance(animations, list) or not isinstance(skins, list):
        raise BlenderValidationError("animation and skin inventories must be arrays")

    animation_document = contract_document.get("animation")
    if not isinstance(animation_document, dict):
        raise BlenderValidationError("contract animation policy must be an object")
    kind = animation_document.get("kind")
    if not isinstance(kind, str) or kind not in _SUPPORTED_ANIMATION_KINDS:
        raise BlenderValidationError("contract animation kind is unsupported")

    rigging_allowed = animation_document.get("meshy_rigging_allowed")
    rigging_target = animation_document.get("rigging_target")
    if kind == "hinge":
        if rigging_allowed is not False or rigging_target != "non_humanoid":
            raise BlenderValidationError("hinge animation contract flags are invalid")
        if skins:
            raise BlenderValidationError("hinge animation contract forbids skins")
        if len(animations) != 1:
            raise BlenderValidationError("hinge animation inventory must contain exactly one animation")
        animation = animations[0]
        if not isinstance(animation, dict) or animation.get("name") != "lid_open":
            raise BlenderValidationError("hinge animation must be named lid_open")
        channels = animation.get("channels")
        samplers = animation.get("samplers")
        if not isinstance(channels, list) or not channels or not isinstance(samplers, list) or not samplers:
            raise BlenderValidationError("hinge animation channels and samplers must be non-empty arrays")
        if len(channels) != 1 or len(samplers) != 1:
            raise BlenderValidationError("hinge animation must contain exactly one channel and sampler")

        channel = channels[0]
        if not isinstance(channel, dict):
            raise BlenderValidationError("hinge animation channel must be an object")
        sampler_index = _integer(channel.get("sampler"), "hinge animation sampler index")
        if sampler_index >= len(samplers):
            raise BlenderValidationError("hinge animation sampler index is out of range")
        target = channel.get("target")
        if not isinstance(target, dict):
            raise BlenderValidationError("hinge animation channel target must be an object")
        node_index = _integer(target.get("node"), "hinge animation target node index")
        nodes = document.get("nodes")
        if not isinstance(nodes, list) or node_index >= len(nodes):
            raise BlenderValidationError("hinge animation target node index is out of range")
        node = nodes[node_index]
        if not isinstance(node, dict) or node.get("name") != "HingePivot":
            raise BlenderValidationError("hinge animation target node must be HingePivot")
        if target.get("path") != "rotation":
            raise BlenderValidationError("hinge animation target path must be rotation")

        sampler = samplers[sampler_index]
        if not isinstance(sampler, dict):
            raise BlenderValidationError("hinge animation sampler must be an object")
        accessors = document.get("accessors")
        if not isinstance(accessors, list):
            raise BlenderValidationError("hinge animation accessors must be an array")
        accessor_indices = {}
        for field in ("input", "output"):
            accessor_index = _integer(sampler.get(field), "hinge animation sampler " + field + " accessor")
            if accessor_index >= len(accessors):
                raise BlenderValidationError("hinge animation sampler accessor index is out of range")
            accessor_indices[field] = accessor_index
        input_accessor = accessors[accessor_indices["input"]]
        output_accessor = accessors[accessor_indices["output"]]
        if not isinstance(input_accessor, dict) or not isinstance(output_accessor, dict):
            raise BlenderValidationError("hinge animation sampler accessors must be objects")
        if input_accessor.get("type") != "SCALAR" or input_accessor.get("componentType") != 5126:
            raise BlenderValidationError("hinge animation sampler input must be a float SCALAR accessor")
        input_count = _integer(input_accessor.get("count"), "hinge animation sampler input accessor count", 3)
        if output_accessor.get("type") != "VEC4" or output_accessor.get("componentType") != 5126:
            raise BlenderValidationError("hinge animation sampler output must be a float VEC4 accessor")
        output_count = _integer(output_accessor.get("count"), "hinge animation sampler output accessor count")
        if output_count != input_count:
            raise BlenderValidationError("hinge animation sampler input and output accessor counts must match")
        if input_count > _MAX_HINGE_SAMPLES:
            raise BlenderValidationError(
                "hinge animation sampler accessor count exceeds the limit ({0} > {1})".format(
                    input_count, _MAX_HINGE_SAMPLES
                )
            )
        if sampler.get("interpolation") != "LINEAR":
            raise BlenderValidationError("hinge animation sampler interpolation must be LINEAR")
        try:
            input_samples = _accessor_values(document, binary, accessor_indices["input"])
            output_samples = _accessor_values(document, binary, accessor_indices["output"])
        except (BlenderValidationError, struct.error) as exc:
            raise BlenderValidationError("hinge animation samples could not be decoded") from exc
        if len(input_samples.values) != input_count or len(output_samples.values) != output_count:
            raise BlenderValidationError("hinge animation samples could not be decoded")
        times = [float(sample[0]) for sample in input_samples.values]
        if any(later <= earlier for earlier, later in zip(times, times[1:])):
            raise BlenderValidationError("hinge animation input times must be strictly increasing")
        if times[0] < 0.0:
            raise BlenderValidationError("hinge animation input times must be nonnegative")

        quaternions: List[Tuple[float, float, float, float]] = []
        for sample in output_samples.values:
            qx, qy, qz, qw = (float(value) for value in sample)
            length = math.sqrt(qx * qx + qy * qy + qz * qz + qw * qw)
            if length <= 1e-12:
                raise BlenderValidationError("hinge animation quaternion samples must be nonzero")
            if abs(length - 1.0) > 1e-4:
                raise BlenderValidationError("hinge animation quaternion samples must be unit length")
            # Normalize so 1e-4 unit-length slack cannot mask a stationary track.
            quaternions.append((qx / length, qy / length, qz / length, qw / length))

        rest_rotation = _finite_vector(
            node.get("rotation", [0.0, 0.0, 0.0, 1.0]),
            "hinge animation target node rest rotation",
            4,
        )
        rest_length = math.sqrt(sum(value * value for value in rest_rotation))
        if not math.isfinite(rest_length) or rest_length <= 1e-12:
            raise BlenderValidationError("hinge animation target node rest rotation must be nonzero")
        rest_rotation = tuple(value / rest_length for value in rest_rotation)
        first = quaternions[0]
        final = quaternions[-1]
        first_rest_dot = abs(sum(first[index] * rest_rotation[index] for index in range(4)))
        if first_rest_dot < 1.0 - 1e-4:
            raise BlenderValidationError("hinge animation first sample must match node rest rotation")
        first_final_dot = abs(sum(first[index] * final[index] for index in range(4)))
        if first_final_dot >= 1.0 - 1e-4:
            raise BlenderValidationError("hinge animation final sample must be rotation-distinct")
        return

    if kind == "optional_humanoid_biped_rig_after_selection":
        if rigging_allowed is not True or rigging_target != "humanoid_biped":
            raise BlenderValidationError("humanoid animation contract flags are invalid")
        return

    if rigging_allowed is not False or rigging_target != ("none" if kind == "static" else "non_humanoid"):
        raise BlenderValidationError("non-humanoid animation contract flags are invalid")
    if animations or skins:
        raise BlenderValidationError("animation or rig data is forbidden by the contract")


def _number(value: Any, label: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(float(value)):
        raise BlenderValidationError(label + " must be finite")
    return float(value)


def _finite_vector(value: object, label: str, size: int = 3) -> Tuple[float, ...]:
    if not isinstance(value, list) or len(value) != size:
        raise BlenderValidationError(label + " must contain " + str(size) + " numbers")
    return tuple(_number(item, label) for item in value)


def _component_format(component_type: int) -> Tuple[str, int]:
    formats = {5121: ("B", 1), 5123: ("H", 2), 5125: ("I", 4), 5126: ("f", 4)}
    if component_type not in formats:
        raise BlenderValidationError("unsupported accessor component type: " + str(component_type))
    return formats[component_type]


def _type_components(accessor_type: Any) -> int:
    components = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}
    if accessor_type not in components:
        raise BlenderValidationError("unsupported accessor type: " + str(accessor_type))
    return components[accessor_type]


def _validate_document_layout(document: Dict[str, Any], binary: bytes) -> Tuple[List[Dict[str, Any]], List[Dict[str, Any]], List[Dict[str, Any]]]:
    buffers = document.get("buffers")
    if not isinstance(buffers, list) or len(buffers) != 1 or not isinstance(buffers[0], dict):
        raise BlenderValidationError("GLB must contain exactly one embedded buffer")
    if "uri" in buffers[0]:
        raise BlenderValidationError("GLB buffer must be embedded in its BIN chunk")
    declared_buffer_length = _integer(buffers[0].get("byteLength"), "buffer byteLength")
    if declared_buffer_length > len(binary) or len(binary) - declared_buffer_length > 3:
        raise BlenderValidationError("embedded buffer length does not match BIN chunk")
    if any(binary[declared_buffer_length:]):
        raise BlenderValidationError("GLB BIN padding must be zero")

    views = document.get("bufferViews")
    if not isinstance(views, list):
        raise BlenderValidationError("bufferViews must be an array")
    for index, view in enumerate(views):
        if not isinstance(view, dict):
            raise BlenderValidationError("bufferView " + str(index) + " must be an object")
        if not isinstance(view.get("buffer"), int) or isinstance(view.get("buffer"), bool) or view.get("buffer") != 0:
            raise BlenderValidationError("bufferView must reference embedded buffer 0")
        offset = _integer(view.get("byteOffset", 0), "bufferView byteOffset")
        length = _integer(view.get("byteLength"), "bufferView byteLength")
        if offset % 4 or offset + length > declared_buffer_length:
            raise BlenderValidationError("bufferView range is invalid")
        if "byteStride" in view:
            stride = _integer(view["byteStride"], "bufferView byteStride", 1)
            if stride < 4 or stride > 252 or stride % 4:
                raise BlenderValidationError("bufferView byteStride is invalid")
        if "target" in view:
            _integer(view["target"], "bufferView target", 1)

    accessors = document.get("accessors")
    if not isinstance(accessors, list) or not accessors:
        raise BlenderValidationError("accessors must be a non-empty array")
    for index, accessor in enumerate(accessors):
        if not isinstance(accessor, dict):
            raise BlenderValidationError("accessor " + str(index) + " must be an object")
        if "sparse" in accessor:
            raise BlenderValidationError("sparse accessors are unsupported")
        view_index = _integer(accessor.get("bufferView"), "accessor bufferView")
        if view_index >= len(views):
            raise BlenderValidationError("accessor bufferView is out of range")
        component_type = _integer(accessor.get("componentType"), "accessor componentType", 1)
        _component_format(component_type)
        component_count = _type_components(accessor.get("type"))
        count = _integer(accessor.get("count"), "accessor count")
        accessor_offset = _integer(accessor.get("byteOffset", 0), "accessor byteOffset")
        view = views[view_index]
        view_offset = _integer(view.get("byteOffset", 0), "bufferView byteOffset")
        view_length = _integer(view.get("byteLength"), "bufferView byteLength")
        format_code, component_size = _component_format(component_type)
        element_size = component_size * component_count
        stride = _integer(view.get("byteStride", element_size), "bufferView byteStride", 1)
        if stride < element_size:
            raise BlenderValidationError("accessor byteStride is smaller than its element")
        start = view_offset + accessor_offset
        if start % component_size:
            raise BlenderValidationError("accessor byteOffset is not aligned")
        used = 0 if count == 0 else (count - 1) * stride + element_size
        if accessor_offset + used > view_length or start + used > declared_buffer_length:
            raise BlenderValidationError("accessor exceeds its bufferView")
        if "normalized" in accessor and not isinstance(accessor["normalized"], bool):
            raise BlenderValidationError("accessor normalized must be boolean")
        for extrema in ("min", "max"):
            if extrema in accessor:
                _finite_vector(accessor[extrema], "accessor " + extrema, component_count)
    return buffers, views, accessors


def _accessor_values(document: Dict[str, Any], binary: bytes, accessor_index: int) -> AccessorData:
    accessors = document.get("accessors")
    views = document.get("bufferViews")
    if not isinstance(accessors, list) or not isinstance(accessor_index, int) or isinstance(accessor_index, bool) or not 0 <= accessor_index < len(accessors):
        raise BlenderValidationError("accessor index is invalid")
    accessor = accessors[accessor_index]
    if not isinstance(accessor, dict) or not isinstance(views, list):
        raise BlenderValidationError("accessor metadata is invalid")
    view_index = _integer(accessor.get("bufferView"), "accessor bufferView")
    if view_index >= len(views) or not isinstance(views[view_index], dict):
        raise BlenderValidationError("accessor bufferView is invalid")
    view = views[view_index]
    component_type = _integer(accessor.get("componentType"), "accessor componentType", 1)
    format_code, component_size = _component_format(component_type)
    accessor_type = accessor.get("type")
    component_count = _type_components(accessor_type)
    count = _integer(accessor.get("count"), "accessor count")
    view_offset = _integer(view.get("byteOffset", 0), "bufferView byteOffset")
    accessor_offset = _integer(accessor.get("byteOffset", 0), "accessor byteOffset")
    stride = _integer(view.get("byteStride", component_size * component_count), "bufferView byteStride", 1)
    start = view_offset + accessor_offset
    format_string = "<" + format_code * component_count
    values: List[Tuple[Union[int, float], ...]] = []
    for item_index in range(count):
        item_offset = start + item_index * stride
        unpacked = struct.unpack_from(format_string, binary, item_offset)
        if component_type == 5126 and not all(math.isfinite(float(value)) for value in unpacked):
            raise BlenderValidationError("accessor contains non-finite values")
        values.append(tuple(unpacked))
    return AccessorData(tuple(values), component_type, str(accessor_type), count)


def _matrix_identity() -> Matrix4:
    return (1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0)


def _matrix_multiply(left: Matrix4, right: Matrix4) -> Matrix4:
    return tuple(sum(left[row * 4 + i] * right[i * 4 + column] for i in range(4)) for row in range(4) for column in range(4))


def _matrix_point(matrix: Matrix4, point: Vector3) -> Vector3:
    x, y, z = point
    return (matrix[0] * x + matrix[1] * y + matrix[2] * z + matrix[3], matrix[4] * x + matrix[5] * y + matrix[6] * z + matrix[7], matrix[8] * x + matrix[9] * y + matrix[10] * z + matrix[11])


def _matrix_determinant3(matrix: Matrix4) -> float:
    return matrix[0] * (matrix[5] * matrix[10] - matrix[6] * matrix[9]) - matrix[1] * (matrix[4] * matrix[10] - matrix[6] * matrix[8]) + matrix[2] * (matrix[4] * matrix[9] - matrix[5] * matrix[8])


def _local_matrix(node: Dict[str, Any]) -> Matrix4:
    if "matrix" in node:
        values = _finite_vector(node["matrix"], "node matrix", 16)
        if values[3] != 0.0 or values[7] != 0.0 or values[11] != 0.0 or values[15] != 1.0:
            raise BlenderValidationError("node matrix must be affine")
        # glTF matrices are column-major; internal matrices are row-major.
        return tuple(values[column * 4 + row] for row in range(4) for column in range(4))
    translation = _finite_vector(node.get("translation", [0.0, 0.0, 0.0]), "node translation", 3)
    scale = _finite_vector(node.get("scale", [1.0, 1.0, 1.0]), "node scale", 3)
    rotation = _finite_vector(node.get("rotation", [0.0, 0.0, 0.0, 1.0]), "node rotation", 4)
    if any(value <= 0.0 for value in scale):
        raise BlenderValidationError("node transforms must have positive scale")
    qx, qy, qz, qw = rotation
    length = math.sqrt(qx * qx + qy * qy + qz * qz + qw * qw)
    if length <= 1e-12:
        raise BlenderValidationError("node rotation quaternion is zero")
    qx, qy, qz, qw = (value / length for value in rotation)
    rotation_matrix = (1 - 2 * (qy * qy + qz * qz), 2 * (qx * qy - qz * qw), 2 * (qx * qz + qy * qw), translation[0], 2 * (qx * qy + qz * qw), 1 - 2 * (qx * qx + qz * qz), 2 * (qy * qz - qx * qw), translation[1], 2 * (qx * qz - qy * qw), 2 * (qy * qz + qx * qw), 1 - 2 * (qx * qx + qy * qy), translation[2], 0.0, 0.0, 0.0, 1.0)
    return tuple(rotation_matrix[row * 4 + column] * (scale[column] if column < 3 else 1.0) for row in range(4) for column in range(4))


def _node_world_matrices(document: Dict[str, Any]) -> Dict[int, Matrix4]:
    nodes = document.get("nodes")
    if not isinstance(nodes, list):
        raise BlenderValidationError("nodes must be an array")
    parents: Dict[int, int] = {}
    for parent_index, node in enumerate(nodes):
        if not isinstance(node, dict):
            raise BlenderValidationError("node must be an object")
        children = node.get("children", [])
        if not isinstance(children, list):
            raise BlenderValidationError("node children must be an array")
        for child in children:
            child_index = _integer(child, "node child index")
            if child_index >= len(nodes) or child_index == parent_index or child_index in parents:
                raise BlenderValidationError("node hierarchy is invalid")
            parents[child_index] = parent_index
    worlds: Dict[int, Matrix4] = {}
    visiting: set = set()

    def visit(index: int) -> Matrix4:
        if index in worlds:
            return worlds[index]
        if index in visiting:
            raise BlenderValidationError("node hierarchy contains a cycle")
        visiting.add(index)
        local = _local_matrix(nodes[index])
        parent = parents.get(index)
        world = _matrix_multiply(visit(parent), local) if parent is not None else local
        visiting.remove(index)
        worlds[index] = world
        return world

    for index in range(len(nodes)):
        visit(index)
    return worlds


def _material_names(document: Dict[str, Any]) -> List[str]:
    materials = document.get("materials", [])
    if not isinstance(materials, list):
        raise BlenderValidationError("materials must be an array")
    names: List[str] = []
    for index, material in enumerate(materials):
        if not isinstance(material, dict):
            raise BlenderValidationError("material " + str(index) + " must be an object")
        name = material.get("name", "")
        if not isinstance(name, str) or not name.strip() or name.strip().lower() == "material" or name.strip().lower().startswith("material."):
            raise BlenderValidationError("material names must be descriptive")
        names.append(name)
    if len(names) != len(set(names)):
        raise BlenderValidationError("material names must be unique")
    return names


def _texture_reference(value: Any, limit: int, label: str) -> None:
    index = _integer(value, label + " index")
    if index >= limit:
        raise BlenderValidationError(label + " index is out of range")


def _validate_material_texture_infos(value: Any, texture_count: int, label: str) -> None:
    if isinstance(value, dict):
        for key, child in value.items():
            child_label = label + "." + str(key)
            if isinstance(key, str) and key.endswith("Texture"):
                if not isinstance(child, dict):
                    raise BlenderValidationError(child_label + " must be a texture info object")
                _texture_reference(child.get("index"), texture_count, child_label)
                _validate_material_texture_infos(child, texture_count, child_label)
            else:
                _validate_material_texture_infos(child, texture_count, child_label)
    elif isinstance(value, list):
        for index, child in enumerate(value):
            _validate_material_texture_infos(child, texture_count, label + "[" + str(index) + "]")


def _validate_khr_materials_variants_root(document: Dict[str, Any]) -> Optional[int]:
    extensions = document.get("extensions", {})
    if not isinstance(extensions, dict) or "KHR_materials_variants" not in extensions:
        return None
    extension = extensions["KHR_materials_variants"]
    if not isinstance(extension, dict):
        raise BlenderValidationError("KHR_materials_variants root extension must be an object")
    variants = extension.get("variants")
    if not isinstance(variants, list) or not variants:
        raise BlenderValidationError("KHR_materials_variants variants must be a non-empty array")
    for index, variant in enumerate(variants):
        if not isinstance(variant, dict) or not isinstance(variant.get("name"), str) or not variant["name"].strip():
            raise BlenderValidationError("KHR_materials_variants variant " + str(index) + " must have a non-empty name")
    return len(variants)


def _validate_material_texture_graph(document: Dict[str, Any]) -> None:
    materials = document.get("materials", [])
    textures = document.get("textures", [])
    images = document.get("images", [])
    samplers = document.get("samplers", [])
    if not isinstance(materials, list) or not isinstance(textures, list):
        raise BlenderValidationError("materials and textures must be arrays")
    if not isinstance(images, list) or not isinstance(samplers, list):
        raise BlenderValidationError("images and samplers must be arrays")
    for index, texture in enumerate(textures):
        if not isinstance(texture, dict):
            raise BlenderValidationError("texture " + str(index) + " must be an object")
        if "source" in texture:
            _texture_reference(texture.get("source"), len(images), "texture " + str(index) + " source")
        if "sampler" in texture:
            _texture_reference(texture.get("sampler"), len(samplers), "texture " + str(index) + " sampler")
        extensions = texture.get("extensions", {})
        if not isinstance(extensions, dict):
            raise BlenderValidationError("texture extensions must be an object")
        for extension_name, extension in extensions.items():
            if not isinstance(extension, dict):
                raise BlenderValidationError("texture extension must be an object")
            if "source" in extension:
                _texture_reference(extension.get("source"), len(images), "texture " + str(index) + " " + str(extension_name) + " source")
    for index, material in enumerate(materials):
        _validate_material_texture_infos(material, len(textures), "material " + str(index))


def _validate_images(document: Dict[str, Any], views: List[Dict[str, Any]], declared_length: int) -> None:
    images = document.get("images", [])
    if not isinstance(images, list):
        raise BlenderValidationError("images must be an array")
    valid_mimes = {"image/png", "image/jpeg", "image/webp", "image/ktx2"}
    for index, image in enumerate(images):
        if not isinstance(image, dict):
            raise BlenderValidationError("image " + str(index) + " must be an object")
        uri = image.get("uri")
        view = image.get("bufferView")
        has_uri = isinstance(uri, str) and bool(uri.strip()) and "\x00" not in uri
        has_view = isinstance(view, int) and not isinstance(view, bool)
        if has_uri == has_view:
            raise BlenderValidationError("image " + str(index) + " must have exactly one usable data reference")
        mime = image.get("mimeType")
        if mime is not None and (not isinstance(mime, str) or mime not in valid_mimes):
            raise BlenderValidationError("image " + str(index) + " has invalid MIME metadata")
        if has_view:
            if view < 0 or view >= len(views):
                raise BlenderValidationError("image bufferView is out of range")
            descriptor = views[view]
            offset = _integer(descriptor.get("byteOffset", 0), "image bufferView byteOffset")
            length = _integer(descriptor.get("byteLength"), "image bufferView byteLength")
            if descriptor.get("buffer") != 0 or length <= 0 or offset + length > declared_length:
                raise BlenderValidationError("image bufferView range is invalid")


def _state_tokens(contract_document: Dict[str, Any]) -> List[str]:
    states = contract_document.get("required_states")
    if not isinstance(states, list) or not states or any(not isinstance(value, str) or not value.strip() for value in states):
        raise BlenderValidationError("contract required_states is invalid")
    return [value.strip().lower() for value in states]


def _scene_reachable_nodes(document: Dict[str, Any]) -> set:
    nodes = document.get("nodes")
    scenes = document.get("scenes")
    scene_index = document.get("scene", 0)
    if not isinstance(nodes, list) or not nodes:
        raise BlenderValidationError("GLB has no nodes")
    if not isinstance(scenes, list) or not scenes or not isinstance(scene_index, int) or isinstance(scene_index, bool) or not 0 <= scene_index < len(scenes):
        raise BlenderValidationError("default scene is invalid")
    reachable = set()
    default_reachable = set()
    child_indices = set()
    for parent_index, node in enumerate(nodes):
        if not isinstance(node, dict):
            raise BlenderValidationError("node " + str(parent_index) + " must be an object")
        children = node.get("children", [])
        if not isinstance(children, list):
            raise BlenderValidationError("node children must be an array")
        for child in children:
            child_index = _integer(child, "node child index")
            if child_index >= len(nodes):
                raise BlenderValidationError("node child index is invalid")
            child_indices.add(child_index)

    def mark(index: Any, target: set) -> None:
        node_index = _integer(index, "scene node index")
        if node_index >= len(nodes):
            raise BlenderValidationError("scene node index is invalid")
        if node_index in target:
            return
        node = nodes[node_index]
        if not isinstance(node, dict):
            raise BlenderValidationError("scene node must be an object")
        target.add(node_index)
        for child in node.get("children", []):
            mark(child, target)

    for index, scene in enumerate(scenes):
        if not isinstance(scene, dict):
            raise BlenderValidationError("scene " + str(index) + " must be an object")
        roots = scene.get("nodes", [])
        if not isinstance(roots, list):
            raise BlenderValidationError("scene nodes must be an array")
        for root in roots:
            root_index = _integer(root, "scene node index")
            if root_index >= len(nodes):
                raise BlenderValidationError("scene node index is invalid")
            if root_index in child_indices:
                raise BlenderValidationError("scene root is listed as a child node")
            mark(root_index, reachable)
            if index == scene_index:
                mark(root_index, default_reachable)
    if reachable != set(range(len(nodes))):
        raise BlenderValidationError("GLB contains nodes outside declared scenes")
    return default_reachable


def _validate_node_policies(document: Dict[str, Any], worlds: Dict[int, Matrix4], bounds_min: Vector3, bounds_max: Vector3, contract_document: Dict[str, Any], default_reachable: Optional[set] = None) -> None:
    nodes = document.get("nodes")
    if not isinstance(nodes, list) or not nodes:
        raise BlenderValidationError("GLB has no nodes")
    meshes = document.get("meshes")
    if not isinstance(meshes, list):
        raise BlenderValidationError("meshes must be an array")
    cameras = document.get("cameras", [])
    if not isinstance(cameras, list) or cameras:
        raise BlenderValidationError("visual GLB contains cameras")
    extensions_used = document.get("extensionsUsed", [])
    if not isinstance(extensions_used, list) or "KHR_lights_punctual" in extensions_used:
        raise BlenderValidationError("visual GLB contains lights")
    if default_reachable is None:
        default_reachable = _scene_reachable_nodes(document)

    state_tokens = _state_tokens(contract_document)
    found_states = set()
    mesh_node_count = 0
    forbidden_tokens = ("collision", "physics", "helper", "socket", "marker")
    for index, node in enumerate(nodes):
        if not isinstance(node, dict):
            raise BlenderValidationError("node must be an object")
        name = node.get("name", "")
        if not isinstance(name, str):
            raise BlenderValidationError("node name must be text")
        lowered = name.lower()
        extensions = node.get("extensions", {})
        if not isinstance(extensions, dict):
            raise BlenderValidationError("node extensions must be an object")
        if any(token in lowered for token in forbidden_tokens) or "camera" in node or "light" in node or "KHR_lights_punctual" in extensions:
            raise BlenderValidationError("visual GLB contains a forbidden helper, camera, or light node")
        for token in state_tokens:
            if token in lowered:
                found_states.add(token)
        mesh_index = node.get("mesh")
        children = node.get("children", [])
        if mesh_index is None:
            if not children:
                raise BlenderValidationError("visual GLB contains a neutral non-mesh leaf")
        else:
            mesh_node_count += 1
            mesh_index = _integer(mesh_index, "node mesh index")
            if mesh_index >= len(meshes):
                raise BlenderValidationError("node mesh index is out of range")
            world = worlds[index]
            if _matrix_determinant3(world) <= 0.0:
                raise BlenderValidationError("node transforms must preserve positive orientation")
            forward = (world[2], world[6], world[10])
            length = math.sqrt(sum(value * value for value in forward))
            if length <= 1e-12:
                raise BlenderValidationError("forward transform has zero length")
            expected_forward = contract_document.get("forward_axis")
            if expected_forward != "+Z":
                raise BlenderValidationError("contract forward_axis must be +Z")
            normalized = tuple(value / length for value in forward)
            if abs(normalized[0]) > 1e-5 or abs(normalized[1]) > 1e-5 or normalized[2] <= 0.999:
                raise BlenderValidationError("forward axis must be canonical +Z")
            mesh = meshes[mesh_index]
            mesh_name = mesh.get("name", "") if isinstance(mesh, dict) else ""
            if not isinstance(mesh_name, str):
                raise BlenderValidationError("mesh name must be text")
            if any(token in mesh_name.lower() for token in forbidden_tokens):
                raise BlenderValidationError("visual GLB contains a forbidden helper mesh")
            for token in state_tokens:
                if token in mesh_name.lower():
                    found_states.add(token)
    if mesh_node_count == 0:
        raise BlenderValidationError("GLB has no mesh node")
    if len(found_states) >= 2:
        raise BlenderValidationError("visual GLB contains independently generated state meshes")

    tolerance = _number(contract_document.get("dimension_tolerance_m"), "contract dimension_tolerance_m")
    pivot = contract_document.get("pivot")
    if pivot == "bottom_center":
        center_x = (bounds_min[0] + bounds_max[0]) / 2.0
        center_y = (bounds_min[1] + bounds_max[1]) / 2.0
        if abs(center_x) > tolerance or abs(center_y) > tolerance or abs(bounds_min[2]) > tolerance:
            raise BlenderValidationError("bottom_center pivot policy is not satisfied")
    elif pivot == "scene_origin":
        for index, node in enumerate(nodes):
            if isinstance(node, dict) and isinstance(node.get("mesh"), int):
                world = worlds[index]
                if any(abs(world[offset]) > tolerance for offset in (3, 7, 11)):
                    raise BlenderValidationError("scene_origin pivot policy is not satisfied")
    elif pivot == "attachment":
        if not all(bounds_min[index] - tolerance <= 0.0 <= bounds_max[index] + tolerance for index in range(3)):
            raise BlenderValidationError("attachment pivot must lie within visual bounds")
    else:
        raise BlenderValidationError("contract pivot policy is invalid")


def _mesh_primitives(document: Dict[str, Any], binary: bytes, material_names: List[str], khr_variant_count: Optional[int] = None) -> Tuple[List[PrimitiveData], List[Vector3], int, List[Dict[str, Any]]]:
    meshes = document.get("meshes")
    if not isinstance(meshes, list) or not meshes:
        raise BlenderValidationError("GLB mesh inventory is empty")
    primitives: List[PrimitiveData] = []
    all_positions: List[Vector3] = []
    triangle_count = 0
    uv_evidence: List[Dict[str, Any]] = []
    for mesh_index, mesh in enumerate(meshes):
        if not isinstance(mesh, dict):
            raise BlenderValidationError("mesh must be an object")
        raw_primitives = mesh.get("primitives")
        if not isinstance(raw_primitives, list) or not raw_primitives:
            raise BlenderValidationError("mesh has no primitives")
        for primitive_index, primitive in enumerate(raw_primitives):
            if not isinstance(primitive, dict) or primitive.get("mode", 4) != 4:
                raise BlenderValidationError("mesh primitive must use TRIANGLES mode")
            primitive_extensions = primitive.get("extensions", {})
            if isinstance(primitive_extensions, dict) and "KHR_materials_variants" in primitive_extensions:
                if khr_variant_count is None:
                    raise BlenderValidationError("KHR_materials_variants primitive extension requires root inventory")
                extension = primitive_extensions["KHR_materials_variants"]
                if not isinstance(extension, dict):
                    raise BlenderValidationError("KHR_materials_variants primitive extension must be an object")
                mappings = extension.get("mappings")
                if not isinstance(mappings, list) or not mappings:
                    raise BlenderValidationError("KHR_materials_variants mappings must be a non-empty array")
                used_variants = set()
                for mapping_index, mapping in enumerate(mappings):
                    if not isinstance(mapping, dict):
                        raise BlenderValidationError("KHR_materials_variants mapping " + str(mapping_index) + " must be an object")
                    material_index = _integer(mapping.get("material"), "KHR_materials_variants mapping material index")
                    if material_index >= len(material_names):
                        raise BlenderValidationError("KHR_materials_variants mapping material index is out of range")
                    variants = mapping.get("variants")
                    if not isinstance(variants, list) or not variants:
                        raise BlenderValidationError("KHR_materials_variants mapping variants must be a non-empty array")
                    for variant in variants:
                        variant_index = _integer(variant, "KHR_materials_variants mapping variant index")
                        if variant_index >= khr_variant_count:
                            raise BlenderValidationError("KHR_materials_variants mapping variant index is out of range")
                        if variant_index in used_variants:
                            raise BlenderValidationError("KHR_materials_variants variant is assigned more than once")
                        used_variants.add(variant_index)
            attributes = primitive.get("attributes")
            if not isinstance(attributes, dict) or not isinstance(attributes.get("POSITION"), int) or isinstance(attributes.get("POSITION"), bool):
                raise BlenderValidationError("mesh primitive has no POSITION attribute")
            for attribute_name, accessor_index in attributes.items():
                if not isinstance(attribute_name, str) or not isinstance(accessor_index, int) or isinstance(accessor_index, bool):
                    raise BlenderValidationError("mesh attribute reference is invalid")
                _accessor_values(document, binary, accessor_index)
            position_accessor = _accessor_values(document, binary, attributes["POSITION"])
            if position_accessor.component_type != 5126 or position_accessor.accessor_type != "VEC3" or position_accessor.count <= 0:
                raise BlenderValidationError("POSITION must be a non-empty float VEC3 accessor")
            positions = tuple(_finite_vector(list(value), "POSITION", 3) for value in position_accessor.values)  # type: ignore
            uv_accessor_index = attributes.get("TEXCOORD_0")
            if not isinstance(uv_accessor_index, int) or isinstance(uv_accessor_index, bool):
                raise BlenderValidationError("every visual primitive requires TEXCOORD_0")
            uv_accessor = _accessor_values(document, binary, uv_accessor_index)
            if uv_accessor.component_type != 5126 or uv_accessor.accessor_type != "VEC2" or uv_accessor.count != position_accessor.count:
                raise BlenderValidationError("TEXCOORD_0 must be a float VEC2 matching POSITION")
            uv_values = [tuple(_number(value, "TEXCOORD_0") for value in item) for item in uv_accessor.values]
            if any(value < 0.0 or value > 1.0 for item in uv_values for value in item):
                raise BlenderValidationError("TEXCOORD_0 values must be within [0, 1]")
            uv_record = {
                "mesh_index": mesh_index,
                "primitive_index": primitive_index,
                "accessor": uv_accessor_index,
                "vertex_count": position_accessor.count,
                "uv_count": uv_accessor.count,
                "finite": True,
                "range_valid": True,
            }
            uv_evidence.append(uv_record)
            indices_accessor_index = primitive.get("indices")
            if "indices" not in primitive:
                index_values = tuple(range(position_accessor.count))
            else:
                if not isinstance(indices_accessor_index, int) or isinstance(indices_accessor_index, bool):
                    raise BlenderValidationError("indices accessor is invalid")
                decoded = _accessor_values(document, binary, indices_accessor_index)
                if decoded.accessor_type != "SCALAR" or decoded.component_type not in (5121, 5123, 5125):
                    raise BlenderValidationError("indices must be an unsigned scalar accessor")
                index_values = tuple(int(item[0]) for item in decoded.values)
            if not index_values or len(index_values) % 3:
                raise BlenderValidationError("mesh primitive does not contain complete triangles")
            if any(value < 0 or value >= position_accessor.count for value in index_values):
                raise BlenderValidationError("mesh primitive index is outside POSITION")
            material = primitive.get("material")
            if material is not None:
                if not isinstance(material, int) or isinstance(material, bool) or material < 0 or material >= len(material_names):
                    raise BlenderValidationError("primitive material index is out of range")
            triangles = len(index_values) // 3
            triangle_count += triangles
            all_positions.extend(positions)
            primitive_data = PrimitiveData(mesh_index, primitive_index, positions, index_values, triangles, uv_record, material)
            primitives.append(primitive_data)
    return primitives, all_positions, triangle_count, uv_evidence


def check_dimensions(actual: Sequence[float], expected: Sequence[float], tolerance: float) -> bool:
    if len(actual) != 3 or len(expected) != 3 or not math.isfinite(float(tolerance)):
        return False
    return all(math.isfinite(float(observed)) and math.isfinite(float(target)) and abs(float(observed) - float(target)) <= float(tolerance) + 1e-9 for observed, target in zip(actual, expected))


def _triangle_budget_range(budget: object) -> Tuple[int, int]:
    if isinstance(budget, int) and not isinstance(budget, bool):
        return 1, budget
    if isinstance(budget, dict):
        minimum, maximum = budget.get("min"), budget.get("max")
        if isinstance(minimum, int) and not isinstance(minimum, bool) and isinstance(maximum, int) and not isinstance(maximum, bool):
            return minimum, maximum
    raise BlenderValidationError("contract triangle budget is invalid")


def check_triangle_budget(triangle_count: int, budget: object) -> bool:
    try:
        minimum, maximum = _triangle_budget_range(budget)
    except BlenderValidationError:
        return False
    return isinstance(triangle_count, int) and not isinstance(triangle_count, bool) and minimum <= triangle_count <= maximum


validate_dimensions = check_dimensions
dimensions_within_tolerance = check_dimensions
validate_triangle_budget = check_triangle_budget
triangle_budget_ok = check_triangle_budget


def _coerce_contract(contract: Union[AssetContract, PathLike]) -> AssetContract:
    if isinstance(contract, AssetContract):
        return contract
    return load_contract(Path(contract))


def validate_cleaned_glb(glb: PathLike, contract: Union[AssetContract, PathLike], task_id: Optional[str] = None) -> Dict[str, Any]:
    """Perform pure validation and return a provisional report.

    ``blender_reimport_passed`` is false here by design.  Only the Blender
    runtime path may flip it and ``write_validation_report`` rejects false values.
    """

    path = Path(glb).expanduser()
    try:
        info = os.lstat(path)
    except OSError as exc:
        raise BlenderValidationError("GLB file does not exist") from exc
    if stat.S_ISLNK(info.st_mode) or not stat.S_ISREG(info.st_mode):
        raise BlenderValidationError("GLB path must be a regular file")
    if info.st_size <= 0 or info.st_size > _MAX_GLB_BYTES:
        raise BlenderValidationError("GLB file size is outside the validator limit")
    try:
        raw = governance._read_bounded_regular_file(path, "GLB", _MAX_GLB_BYTES)
    except (OSError, ValueError) as exc:
        raise BlenderValidationError("GLB file could not be read") from exc
    parsed = _parse_glb(raw)
    document = parsed.document
    buffers, views, _accessors = _validate_document_layout(document, parsed.binary)
    material_names = _material_names(document)
    khr_variant_count = _validate_khr_materials_variants_root(document)
    _validate_material_texture_graph(document)
    primitives, _all_positions, _mesh_triangle_count, all_uv_evidence = _mesh_primitives(document, parsed.binary, material_names, khr_variant_count)
    worlds = _node_world_matrices(document)
    nodes = document.get("nodes")
    assert isinstance(nodes, list)
    default_reachable = _scene_reachable_nodes(document)
    materialized_nodes = [
        node for node_index, node in enumerate(nodes)
        if isinstance(node, dict) and isinstance(node.get("mesh"), int) and not isinstance(node.get("mesh"), bool) and node_index in default_reachable
    ]
    materialized_meshes = {node["mesh"] for node in materialized_nodes}
    triangle_count = sum(
        primitive.triangles
        for node in materialized_nodes
        for primitive in primitives
        if primitive.mesh_index == node["mesh"]
    )
    uv_evidence = [item for item in all_uv_evidence if item["mesh_index"] in materialized_meshes]
    transformed_positions: List[Vector3] = []
    for node_index, node in enumerate(nodes):
        if isinstance(node, dict) and isinstance(node.get("mesh"), int) and not isinstance(node.get("mesh"), bool) and node_index in default_reachable:
            mesh_index = node["mesh"]
            for primitive in primitives:
                if primitive.mesh_index == mesh_index:
                    transformed_positions.extend(_matrix_point(worlds[node_index], point) for point in primitive.positions)
    if not transformed_positions:
        raise BlenderValidationError("GLB has no materialized mesh geometry")
    if any(not all(math.isfinite(value) for value in point) for point in transformed_positions):
        raise BlenderValidationError("geometry contains non-finite value")
    bounds_min = tuple(min(point[index] for point in transformed_positions) for index in range(3))
    bounds_max = tuple(max(point[index] for point in transformed_positions) for index in range(3))
    dimensions = tuple(bounds_max[index] - bounds_min[index] for index in range(3))
    if any(not math.isfinite(value) or value < 0.0 for value in dimensions):
        raise BlenderValidationError("geometry dimensions are invalid")
    asset_contract = _coerce_contract(contract)
    contract_document = asset_contract.document
    expected_dimensions = contract_document.get("dimensions_m")
    tolerance = contract_document.get("dimension_tolerance_m")
    if not isinstance(expected_dimensions, list) or not isinstance(tolerance, (int, float)) or not check_dimensions(dimensions, expected_dimensions, float(tolerance)):
        raise BlenderValidationError("dimensions are outside contract tolerance")
    budget_document = contract_document.get("budget")
    budget = budget_document.get("triangles") if isinstance(budget_document, dict) else None
    if not check_triangle_budget(triangle_count, budget):
        raise BlenderValidationError("triangle count exceeds contract budget")
    material_budget = budget_document.get("material_slots") if isinstance(budget_document, dict) else None
    if not isinstance(material_budget, int) or isinstance(material_budget, bool) or len(material_names) > material_budget:
        raise BlenderValidationError("material slot count exceeds contract budget")
    declared_length = _integer(buffers[0].get("byteLength"), "buffer byteLength")
    _validate_images(document, views, declared_length)
    _validate_node_policies(document, worlds, bounds_min, bounds_max, contract_document, default_reachable)
    _validate_animation_policy(document, contract_document, parsed.binary)
    return {
        "schema_version": "1.0.0",
        "document_kind": "meshy_blender_validation",
        "status": "PASS",
        "task_id": task_id or "",
        "asset_id": asset_contract.asset_id,
        "contract_sha256": asset_contract.sha256,
        "sha256": hashlib.sha256(raw).hexdigest(),
        "byte_size": len(raw),
        "mesh_count": len({node["mesh"] for index, node in enumerate(nodes) if index in default_reachable and isinstance(node, dict) and isinstance(node.get("mesh"), int) and not isinstance(node.get("mesh"), bool)}),
        "triangle_count": triangle_count,
        "material_names": material_names,
        "bounds": {"min": [float(value) for value in bounds_min], "max": [float(value) for value in bounds_max], "dimensions": [float(value) for value in dimensions]},
        "uvs_present": len(uv_evidence) == len(primitives) and bool(uv_evidence),
        "uv_evidence": uv_evidence,
        "blender_reimport_passed": False,
        "master_provenance": None,
    }


def validate_glb(glb: PathLike, contract: Union[AssetContract, PathLike]) -> Dict[str, Any]:
    return validate_cleaned_glb(glb, contract)


def _report_number(value: Any, label: str) -> float:
    return _number(value, label)


def _validate_report_record(report: Any) -> None:
    if not isinstance(report, dict) or set(report) != set(_CANONICAL_FIELDS):
        raise BlenderValidationError("validation report fields are not canonical")
    if report.get("schema_version") != "1.0.0" or report.get("document_kind") != "meshy_blender_validation" or report.get("status") != "PASS":
        raise BlenderValidationError("validation report document identity is invalid")
    for field in ("task_id", "asset_id", "contract_sha256", "sha256"):
        if not isinstance(report.get(field), str) or not report[field].strip():
            raise BlenderValidationError("validation report " + field + " is invalid")
    if _TASK_ID_RE.fullmatch(report["task_id"]) is None or _ASSET_ID_RE.fullmatch(report["asset_id"]) is None:
        raise BlenderValidationError("validation report identifier is invalid")
    if len(report["contract_sha256"]) != 64 or len(report["sha256"]) != 64 or any(char not in "0123456789abcdef" for char in report["contract_sha256"] + report["sha256"]):
        raise BlenderValidationError("validation report hash is invalid")
    if not isinstance(report.get("byte_size"), int) or isinstance(report["byte_size"], bool) or report["byte_size"] <= 0:
        raise BlenderValidationError("validation report byte_size is invalid")
    for field in ("mesh_count", "triangle_count"):
        if not isinstance(report.get(field), int) or isinstance(report[field], bool) or report[field] <= 0:
            raise BlenderValidationError("validation report " + field + " is invalid")
    names = report.get("material_names")
    if not isinstance(names, list) or any(not isinstance(name, str) or not name.strip() for name in names) or len(names) != len(set(names)):
        raise BlenderValidationError("validation report material_names is invalid")
    bounds = report.get("bounds")
    if not isinstance(bounds, dict) or set(bounds) != {"min", "max", "dimensions"}:
        raise BlenderValidationError("validation report bounds are invalid")
    for field in ("min", "max", "dimensions"):
        if not isinstance(bounds[field], list) or len(bounds[field]) != 3:
            raise BlenderValidationError("validation report bounds are invalid")
        for value in bounds[field]:
            _report_number(value, "validation report bounds")
    if any(bounds["min"][index] > bounds["max"][index] for index in range(3)):
        raise BlenderValidationError("validation report bounds are invalid")
    if any(abs(bounds["dimensions"][index] - (bounds["max"][index] - bounds["min"][index])) > 1e-9 for index in range(3)):
        raise BlenderValidationError("validation report bounds dimensions are invalid")
    if report.get("uvs_present") is not True or report.get("blender_reimport_passed") is not True:
        raise BlenderValidationError("validation report requires UV and Blender re-import evidence")
    if report.get("master_provenance") is not None:
        raise BlenderValidationError("master_provenance must be null")
    evidence = report.get("uv_evidence")
    if not isinstance(evidence, list) or not evidence:
        raise BlenderValidationError("validation report UV evidence is empty")
    required_evidence = {"mesh_index", "primitive_index", "accessor", "vertex_count", "uv_count", "finite", "range_valid"}
    for item in evidence:
        if not isinstance(item, dict) or set(item) != required_evidence:
            raise BlenderValidationError("validation report UV evidence is not canonical")
        for field in ("mesh_index", "primitive_index", "accessor", "vertex_count", "uv_count"):
            minimum = 1 if field in ("vertex_count", "uv_count") else 0
            if not isinstance(item[field], int) or isinstance(item[field], bool) or item[field] < minimum:
                raise BlenderValidationError("validation report UV evidence types are invalid")
        if item["finite"] is not True or item["range_valid"] is not True or item["uv_count"] != item["vertex_count"]:
            raise BlenderValidationError("validation report UV evidence failed")


def verify_validation_report(
    cleaned_glb: PathLike,
    contract: Union[AssetContract, PathLike],
    report: Dict[str, Any],
    *,
    task_id: Optional[str] = None,
    expected_contract_sha256: Optional[str] = None,
) -> Dict[str, Any]:
    """Recompute and re-import one fixed R4 report without publishing it."""

    if expected_contract_sha256 is not None and (
        not isinstance(expected_contract_sha256, str)
        or _HASH_RE.fullmatch(expected_contract_sha256) is None
    ):
        raise BlenderValidationError("expected contract hash must be lowercase 64-hex")
    _validate_report_record(report)
    expected = validate_cleaned_glb(cleaned_glb, contract, task_id=task_id)
    if expected_contract_sha256 is not None:
        expected["contract_sha256"] = expected_contract_sha256
    expected["blender_reimport_passed"] = True
    try:
        reimport_evidence = _reimport_with_blender(
            Path(cleaned_glb), int(expected["triangle_count"])
        )
    except BlenderValidationError:
        raise
    except Exception as exc:
        raise BlenderValidationError("Blender re-import authority failed: " + str(exc)) from exc
    if (
        reimport_evidence.sha256 != expected["sha256"]
        or reimport_evidence.byte_size != expected["byte_size"]
        or reimport_evidence.triangle_count != expected["triangle_count"]
    ):
        raise BlenderValidationError("runtime Blender re-import evidence does not match cleaned.glb")
    if any(report[field] != expected[field] for field in _CANONICAL_FIELDS):
        raise BlenderValidationError("validation report semantic fields do not match cleaned.glb")
    return expected


def _resolve_task_inputs(project_root: PathLike, contract_path: PathLike, task_dir: PathLike, glb_alias: Optional[PathLike] = None, report_alias: Optional[PathLike] = None) -> Tuple[AssetContract, Path, Path]:
    """Resolve only selected-task ``cleaned.glb`` and fixed report leaves."""

    try:
        from tools import meshy_candidate_review as candidate_review
        review_path, review, generation, root, _asset_root = candidate_review._load_task_record(project_root, task_dir)
    except Exception as exc:
        raise BlenderValidationError("task evidence is not fully governed: " + str(exc)) from exc
    if review.get("state") != "selected" or generation.get("status") != "SUCCEEDED":
        raise BlenderValidationError("validator requires a selected review and SUCCEEDED generation")
    resolved_task = Path(review_path).parent
    contract_file = Path(contract_path).expanduser()
    if not contract_file.is_absolute():
        contract_file = root / contract_file
    contract_file = Path(os.path.abspath(os.fspath(contract_file)))
    try:
        contract = load_contract(contract_file)
    except (OSError, TypeError, ValueError) as exc:
        raise BlenderValidationError("contract is invalid: " + str(exc)) from exc
    asset_id = resolved_task.parent.name
    if contract.asset_id != asset_id or review.get("asset_id") != asset_id or generation.get("asset_id") != asset_id or review.get("task_id") != resolved_task.name or generation.get("task_id") != resolved_task.name:
        raise BlenderValidationError("task/asset identity does not match contract")
    if generation.get("contract_sha256") != contract.sha256:
        raise BlenderValidationError("contract hash does not match generation evidence")
    cleaned = resolved_task / "cleaned.glb"
    report = resolved_task / "blender-validation.json"
    for alias, expected, label in ((glb_alias, cleaned, "GLB"), (report_alias, report, "report")):
        if alias is None:
            continue
        candidate = Path(alias).expanduser()
        if not candidate.is_absolute():
            candidate = root / candidate
        candidate = Path(os.path.abspath(os.fspath(candidate)))
        if candidate != expected:
            raise BlenderValidationError(label + " alias must resolve to the exact task leaf")
    try:
        info = os.lstat(cleaned)
    except OSError as exc:
        raise BlenderValidationError("cleaned.glb is missing") from exc
    if stat.S_ISLNK(info.st_mode) or not stat.S_ISREG(info.st_mode) or info.st_size <= 0 or info.st_size > _MAX_GLB_BYTES:
        raise BlenderValidationError("cleaned.glb must be a bounded regular file")
    return contract, cleaned, report


def _resolve_verify_inputs(
    project_root: PathLike,
    contract_path: PathLike,
    task_dir: PathLike,
    glb_alias: PathLike,
    report_alias: PathLike,
) -> Tuple[AssetContract, str, Path, Path]:
    """Resolve governed source/artifact bindings for fixed report verification."""

    _caller_contract, cleaned, report = _resolve_task_inputs(
        project_root, contract_path, task_dir, glb_alias, report_alias
    )
    try:
        from tools import meshy_candidate_review as candidate_review

        review_path, review, generation, root, _asset_root = candidate_review._load_task_record(
            project_root, task_dir
        )
        if review.get("state") != "selected" or generation.get("status") != "SUCCEEDED":
            raise BlenderValidationError(
                "verify-only requires a selected review and SUCCEEDED generation"
            )
        resolved_task = Path(review_path).parent
        task_contract_path = candidate_review._governed_artifact(
            root, resolved_task, "contract.json"
        )
        task_contract_document, task_contract_raw = governance.strict_load_json_bytes(
            task_contract_path, "task contract", 4 * 1024 * 1024
        )
        if task_contract_raw != canonical_json_bytes(task_contract_document):
            raise BlenderValidationError("task contract is not canonical JSON")
        task_contract = load_contract(task_contract_path)
        if (
            task_contract.asset_id != resolved_task.parent.name
            or generation.get("contract_artifact_sha256")
            != hashlib.sha256(task_contract_raw).hexdigest()
        ):
            raise BlenderValidationError("task contract artifact is not bound")
        return task_contract, str(generation["contract_sha256"]), cleaned, report
    except BlenderValidationError:
        raise
    except Exception as exc:
        raise BlenderValidationError("verify-only task evidence is not fully governed: " + str(exc)) from exc


def write_validation_report(project_root: PathLike, task_dir: PathLike, report: Optional[Dict[str, Any]] = None) -> None:
    """Publish one fully validated PASS at the fixed task-local report leaf."""

    if report is None:
        raise BlenderValidationError("write_validation_report requires project root, task directory, and report")
    _validate_report_record(report)
    try:
        from tools import meshy_candidate_review as candidate_review
        review_path, review, generation, root, _asset_root = candidate_review._load_task_record(project_root, task_dir)
    except Exception as exc:
        raise BlenderValidationError("report task evidence is not fully governed: " + str(exc)) from exc
    if review.get("state") != "selected" or generation.get("status") != "SUCCEEDED":
        raise BlenderValidationError("report publication requires a selected review and SUCCEEDED generation")
    resolved_task = Path(review_path).parent
    target = resolved_task / "blender-validation.json"
    if report.get("task_id") != resolved_task.name or report.get("asset_id") != resolved_task.parent.name:
        raise BlenderValidationError("validation report identity does not match task")
    contract_file = resolved_task / "contract.json"
    try:
        task_contract = load_contract(contract_file)
    except (OSError, TypeError, ValueError) as exc:
        raise BlenderValidationError("task contract is invalid") from exc
    if task_contract.asset_id != resolved_task.parent.name or generation.get("contract_artifact_sha256") != task_contract.sha256:
        raise BlenderValidationError("task contract artifact is not bound")
    if report.get("contract_sha256") != generation.get("contract_sha256"):
        raise BlenderValidationError("validation report contract hash is not bound")
    cleaned = resolved_task / "cleaned.glb"
    verify_validation_report(
        cleaned,
        task_contract,
        report,
        task_id=resolved_task.name,
        expected_contract_sha256=generation["contract_sha256"],
    )
    try:
        governance.atomic_write_json(target, report, project_root=root, allowed_root=resolved_task, mode=0o600)
        persisted, raw = governance.strict_load_json_bytes(target, "Blender validation report", 4 * 1024 * 1024)
    except (OSError, TypeError, ValueError, RuntimeError) as exc:
        raise BlenderValidationError("validation report publication failed") from exc
    if raw != canonical_json_bytes(persisted) or persisted != report:
        raise BlenderValidationError("published validation report was not canonical or exact")
    if stat.S_IMODE(os.lstat(target).st_mode) != 0o600:
        raise BlenderValidationError("validation report mode is not 0600")


def _reimport_with_blender(glb_path: Path, expected_triangles: int) -> _BlenderReimportEvidence:
    """Re-import the exact GLB in Blender and compare actual mesh triangles."""

    try:
        import bpy  # type: ignore
    except Exception:
        # The authority remains this fixed function even for the host Python
        # process.  It obtains the same evidence from a clean bounded Blender
        # child; callers cannot replace the authority with an injected callable.
        return _reimport_with_blender_process(glb_path, expected_triangles)

    try:
        for obj in list(bpy.data.objects):
            bpy.data.objects.remove(obj, do_unlink=True)
        result = bpy.ops.import_scene.gltf(filepath=str(glb_path))
        try:
            result_values = set(result)
        except TypeError:
            result_values = {str(result)}
        if "FINISHED" not in result_values or "CANCELLED" in result_values:
            raise BlenderValidationError("Blender could not re-import cleaned.glb")
        mesh_objects = [obj for obj in bpy.data.objects if obj.type == "MESH"]
        if not mesh_objects:
            raise BlenderValidationError("Blender re-import produced no mesh objects")
        imported_triangles = 0
        for obj in mesh_objects:
            scale = obj.scale
            if any(float(value) <= 0.0 or not math.isfinite(float(value)) for value in scale):
                raise BlenderValidationError("Blender re-import contains non-positive transforms")
            imported_triangles += sum(max(len(polygon.vertices) - 2, 0) for polygon in obj.data.polygons)
        if imported_triangles != expected_triangles:
            raise BlenderValidationError("Blender re-import triangle count differs from GLB count: " + str(imported_triangles) + " != " + str(expected_triangles))
        info = os.lstat(glb_path)
        digest = governance.file_sha256(glb_path, max_bytes=_MAX_GLB_BYTES)
        if stat.S_ISLNK(info.st_mode) or not stat.S_ISREG(info.st_mode):
            raise BlenderValidationError("cleaned.glb must remain a regular file after Blender re-import")
        return _BlenderReimportEvidence(digest, info.st_size, imported_triangles)
    except BlenderValidationError:
        raise
    except Exception as exc:
        raise BlenderValidationError("Blender re-import failed: " + str(exc)) from exc


def _reimport_with_blender_process(glb_path: Path, expected_triangles: int) -> _BlenderReimportEvidence:
    """Obtain re-import authority from a bounded clean Blender process."""

    from tools.meshy_blender_master import _run_bounded_process

    task_dir = Path(glb_path).parent
    contract_path = task_dir / "contract.json"
    command = [
        BLENDER_PATH,
        "--background",
        "--factory-startup",
        "--python",
        str(Path(__file__).resolve()),
        "--",
        "--project-root",
        str(task_dir),
        "--contract",
        str(contract_path),
        "--task-dir",
        str(task_dir),
        "--glb",
        str(glb_path),
        "--reimport-only",
        "--expected-triangles",
        str(expected_triangles),
    ]
    try:
        result = _run_bounded_process(command, cwd=task_dir, timeout=120.0)
    except Exception as exc:
        raise BlenderValidationError("bounded Blender re-import failed: " + str(exc)) from exc
    stdout = result.stdout.decode("utf-8", errors="replace") if isinstance(result.stdout, bytes) else str(result.stdout or "")
    stderr = result.stderr.decode("utf-8", errors="replace") if isinstance(result.stderr, bytes) else str(result.stderr or "")
    if len(stdout.encode("utf-8")) + len(stderr.encode("utf-8")) > _MAX_PROCESS_OUTPUT:
        raise BlenderValidationError("Blender re-import output exceeded the bounded limit")
    if result.returncode != 0:
        raise BlenderValidationError("Blender re-import process failed: " + (stderr or stdout)[-4096:])
    matches = list(re.finditer(
        r"^MESHY BLENDER REIMPORT PASS sha256=(?P<sha256>[0-9a-f]{64}) "
        r"byte_size=(?P<byte_size>[0-9]+) triangle_count=(?P<triangle_count>[0-9]+)$",
        stdout,
        re.MULTILINE,
    ))
    if len(matches) != 1:
        raise BlenderValidationError("Blender re-import process did not emit canonical authority")
    match = matches[0].groupdict()
    evidence = _BlenderReimportEvidence(
        match["sha256"], int(match["byte_size"]), int(match["triangle_count"])
    )
    if evidence.triangle_count != expected_triangles:
        raise BlenderValidationError("Blender re-import triangle count differs from expected")
    return evidence


def _positive_integer(value: str) -> int:
    try:
        parsed = int(value)
    except ValueError as exc:
        raise argparse.ArgumentTypeError("expected a positive integer") from exc
    if parsed <= 0:
        raise argparse.ArgumentTypeError("expected a positive integer")
    return parsed


def _reimport_only_inputs(args: argparse.Namespace) -> Tuple[Path, int]:
    if not _is_blender_runtime():
        raise BlenderValidationError("reimport-only requires Blender runtime")
    try:
        import bpy  # type: ignore  # noqa: F401
    except Exception as exc:
        raise BlenderValidationError("reimport-only requires Blender runtime") from exc
    if args.glb is None:
        raise BlenderValidationError("reimport-only requires --glb")
    if args.expected_triangles is None:
        raise BlenderValidationError("reimport-only requires --expected-triangles")
    if not isinstance(args.expected_triangles, int) or isinstance(args.expected_triangles, bool) or args.expected_triangles <= 0:
        raise BlenderValidationError("reimport-only requires positive --expected-triangles")
    if args.report is not None:
        raise BlenderValidationError("reimport-only does not accept --report")
    task_dir = Path(args.task_dir).expanduser().resolve()
    cleaned = Path(args.glb).expanduser().resolve()
    contract_path = Path(args.contract).expanduser().resolve()
    if cleaned != task_dir / "cleaned.glb":
        raise BlenderValidationError("reimport-only GLB must be the exact task cleaned.glb leaf")
    if contract_path != task_dir / "contract.json":
        raise BlenderValidationError("reimport-only contract must be the exact task contract.json leaf")
    for path, label in ((cleaned, "cleaned.glb"), (contract_path, "contract.json")):
        try:
            info = os.lstat(path)
        except OSError as exc:
            raise BlenderValidationError("reimport-only " + label + " is missing") from exc
        if stat.S_ISLNK(info.st_mode) or not stat.S_ISREG(info.st_mode):
            raise BlenderValidationError("reimport-only " + label + " must be a regular file")
    return cleaned, args.expected_triangles


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project-root", type=Path, required=True)
    parser.add_argument("--contract", type=Path, required=True)
    parser.add_argument("--task-dir", type=Path, required=True)
    parser.add_argument("--glb", type=Path, required=False)
    parser.add_argument("--report", type=Path, required=False)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--verify-only", action="store_true")
    mode.add_argument("--reimport-only", action="store_true")
    parser.add_argument("--expected-triangles", type=_positive_integer, required=False)
    return parser


_build_parser = build_parser


def _is_blender_runtime() -> bool:
    return Path(sys.executable).name.lower().startswith("blender") or "--background" in sys.argv


def _runtime_argv(argv: Optional[Sequence[str]]) -> Optional[List[str]]:
    if argv is not None:
        return list(argv)
    if "--" not in sys.argv:
        return None
    return list(sys.argv[sys.argv.index("--") + 1:])


def parse_args(argv: Optional[Sequence[str]] = None) -> argparse.Namespace:
    runtime = argv is None and _is_blender_runtime()
    return build_parser().parse_args(_runtime_argv(argv) if runtime else argv)


write_report = write_validation_report
build_validation_report = validate_cleaned_glb


def main(argv: Optional[Sequence[str]] = None) -> int:
    try:
        args = parse_args(argv)
        if args.verify_only:
            if args.glb is None or args.report is None:
                raise BlenderValidationError("verify-only requires --glb and --report")
            task_contract, source_contract_hash, cleaned, report_path = _resolve_verify_inputs(
                args.project_root,
                args.contract,
                args.task_dir,
                args.glb,
                args.report,
            )
            report, raw = governance.strict_load_json_bytes(
                report_path, "Blender validation report", 4 * 1024 * 1024
            )
            if raw != canonical_json_bytes(report):
                raise BlenderValidationError("Blender validation report is not canonical JSON")
            _validate_report_record(report)
            if report.get("contract_sha256") != source_contract_hash:
                raise BlenderValidationError("validation report contract hash is not bound")
            expected = verify_validation_report(
                cleaned,
                task_contract,
                report,
                task_id=cleaned.parent.name,
                expected_contract_sha256=source_contract_hash,
            )
            print(
                "MESHY BLENDER VALIDATION VERIFY PASS sha256={0} byte_size={1} triangle_count={2}".format(
                    expected["sha256"], expected["byte_size"], expected["triangle_count"]
                )
            )
            return 0
        if args.reimport_only:
            cleaned, expected_triangles = _reimport_only_inputs(args)
            evidence = _reimport_with_blender(cleaned, expected_triangles)
            print(
                "MESHY BLENDER REIMPORT PASS sha256={0} byte_size={1} triangle_count={2}".format(
                    evidence.sha256, evidence.byte_size, evidence.triangle_count
                )
            )
            return 0
        if args.expected_triangles is not None:
            raise BlenderValidationError("--expected-triangles requires --reimport-only")
        contract, cleaned, report_path = _resolve_task_inputs(args.project_root, args.contract, args.task_dir, args.glb, args.report)
        report = validate_cleaned_glb(cleaned, contract, task_id=cleaned.parent.name)
        report["blender_reimport_passed"] = True
        write_validation_report(args.project_root, cleaned.parent, report)
        print("MESHY BLENDER VALIDATION PASS asset={0} triangles={1} materials={2}".format(report["asset_id"], report["triangle_count"], len(report["material_names"])))
        return 0
    except (BlenderValidationError, OSError, ValueError) as exc:
        print("meshy_blender_validate: " + str(exc), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
