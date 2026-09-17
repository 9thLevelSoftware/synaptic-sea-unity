#!/usr/bin/env python3
"""Verify a governed, no-promotion locked-isometric Meshy runtime review.

Unity port. The Godot tool rendered six captures in a disposable Godot project overlay and published them with
``runtime-review.json``. In the Unity repository the captures are rendered by the Unity editor
(``SynapticSea.EditorTools.Content.MeshyRuntimeReview``, which writes the same fixed leaves and the same canonical
report into ``artifacts/validation-previews/meshy/<asset_id>/``). This module keeps the engine-free half: the
report schema, the pixel gates re-derived from the PNG leaves, and ``verify_evidence_chain``, which
``meshy_candidate_review`` calls when it binds promotion evidence.

The governance layer (``meshy_governance``) is POSIX-only (fcntl, 0600/0700 modes): run on macOS, Linux or WSL.

    python3 tools/python/meshy_runtime_review.py verify --project-root . --task-dir <task dir>
"""

from __future__ import annotations

import argparse
import binascii
import hashlib
import json
import math
import os
import re
import stat
import sys
import zlib
from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, Optional, Tuple

try:
    import meshy_governance as governance
    from meshy_asset_contract import canonical_json_bytes, load_contract
except ModuleNotFoundError:  # pragma: no cover - direct script execution
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    import meshy_governance as governance
    from meshy_asset_contract import canonical_json_bytes, load_contract


PREVIEW_ROOT_RELATIVE = Path("artifacts/validation-previews/meshy")
SEEDS = (42, 777)
LIGHTING_MODES = ("normal", "emergency", "dark")
CAPTURE_SIZE = (1600, 900)
IDENTIFIER_RE = re.compile(r"^[a-z0-9][a-z0-9_-]*$")
TASK_ID_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.-]*$")
HASH_RE = re.compile(r"^[0-9a-f]{64}$")
SCHEMA_VERSION = "1.0.0"
DOCUMENT_KIND = "meshy_runtime_review"
LOCKED_CAMERA_DIRECTION = (16.0, 14.0, 16.0)
LOCKED_CAMERA_DIRECTION_TOLERANCE = 1e-4
CAMERA_DISTANCE_MIN = 4.0
CAMERA_SIZE_MIN = 1.5
CAMERA_SIZE_MAX = 4096.0
CAMERA_SIZE_DIMENSION_SCALE = 16.0
STAGED_SAMPLE_STEP_X = max(1, CAPTURE_SIZE[0] // 64)
STAGED_SAMPLE_STEP_Y = max(1, CAPTURE_SIZE[1] // 36)
STAGED_SAMPLE_MAX = len(range(0, CAPTURE_SIZE[0], STAGED_SAMPLE_STEP_X)) * len(
    range(0, CAPTURE_SIZE[1], STAGED_SAMPLE_STEP_Y)
)
# Contextual RGB deltas use the normalized renderer scale [0.0, 1.0].
CONTEXTUAL_REFERENCE_MIN = 2
CONTEXTUAL_CHANGED_MIN = 2
CONTEXTUAL_CHANGED_FRACTION_MIN = 0.5
CONTEXTUAL_RGB_DELTA_MIN = 0.02
CONTEXTUAL_RGB_DELTA_MAX = 1.0
RUNTIME_DOCUMENT_FIELDS = frozenset(
    (
        "schema_version",
        "document_kind",
        "asset_id",
        "task_id",
        "contract_sha256",
        "cleaned_glb_sha256",
        "blender_validation_sha256",
        "seeds",
        "lighting",
        "captures",
        "output_hashes",
        "pass",
        "reason",
    )
)
CAPTURE_FIELDS = frozenset(
    (
        "seed",
        "lighting",
        "camera_transform",
        "staged_visibility",
        "contextual_visibility",
        "output_sha256",
        "pass",
        "reason",
    )
)
FIXED_OUTPUT_NAMES = tuple(
    "seed-{0}-{1}.png".format(seed, lighting)
    for seed in SEEDS
    for lighting in LIGHTING_MODES
) + tuple(
    "seed-{0}-{1}-{2}.png".format(seed, lighting, kind)
    for seed in SEEDS
    for lighting in LIGHTING_MODES
    for kind in ("staged", "reference")
) + ("runtime-review.json",)
# Kept for the small public helper API used by older callers.  Runtime runs
# never use this value as evidence; they record the marker's actual transform.
DEFAULT_CAMERA_TRANSFORM: Dict[str, Any] = {
    "projection": "orthogonal",
    "position": [19.742138317, 18.236871003, 19.242143317],
    "target": [0.5, 1.399999976, 0.000005],
    "size": 1.5,
}


class ReviewError(ValueError):
    """Raised when governed review input, evidence, or publication is invalid."""


@dataclass
class ValidatedTask:
    asset_id: str
    task_dir: Path
    cleaned_glb: Path
    validation_report: Path
    contract_hash: str
    cleaned_glb_hash: str
    cleaned_glb_overlay: Optional[Path] = None
    task_id: str = ""
    blender_validation_hash: str = ""
    cleaned_glb_size: int = 0
    category: str = ""
    bounds_dimensions: Optional[Tuple[float, float, float]] = None


def _reject_duplicate_keys(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON field: " + key)
        result[key] = value
    return result


def _absolute(path: Path) -> Path:
    return Path(os.path.abspath(os.fspath(Path(path).expanduser())))


def _raw_path(value: Path, label: str) -> Path:
    path = Path(value).expanduser()
    if ".." in path.parts:
        raise ValueError("{0} must not contain traversal: {1}".format(label, path))
    return path


def _project_path(project_root: Path, value: Path, label: str) -> Path:
    path = _raw_path(value, label)
    if not path.is_absolute():
        path = project_root / path
    return _absolute(path)


def _contained(root: Path, candidate: Path) -> bool:
    root = _absolute(root)
    candidate = _absolute(candidate)
    return candidate == root or root in candidate.parents


def _reject_symlink(path: Path, label: str) -> None:
    """Reject symlinked existing components for disposable workspace paths."""

    absolute = _absolute(path)
    current = Path(absolute.anchor)
    for part in absolute.parts[1:]:
        current /= part
        try:
            mode = current.lstat().st_mode
        except FileNotFoundError:
            break
        except OSError as exc:
            raise ValueError("cannot inspect {0}: {1}".format(label, path)) from exc
        # /var and /tmp are the only macOS aliases intentionally accepted by
        # the shared governance layer.  The project itself must remain real.
        if stat.S_ISLNK(mode) and current not in (Path("/var"), Path("/tmp")):
            raise ValueError("{0} contains symlink component: {1}".format(label, current))


def _regular_file(path: Path, label: str, *, nonempty: bool = True) -> Path:
    _reject_symlink(path, label)
    try:
        info = path.lstat()
    except FileNotFoundError as exc:
        raise ReviewError("missing {0}: {1}".format(label, path)) from exc
    except OSError as exc:
        raise ReviewError("cannot inspect {0}: {1}".format(label, path)) from exc
    if stat.S_ISLNK(info.st_mode) or not stat.S_ISREG(info.st_mode):
        raise ReviewError("{0} must be a regular file: {1}".format(label, path))
    if nonempty and info.st_size <= 0:
        raise ReviewError("{0} is empty: {1}".format(label, path))
    return path


def _regular_directory(path: Path, label: str) -> Path:
    _reject_symlink(path, label)
    try:
        info = path.lstat()
    except FileNotFoundError as exc:
        raise ReviewError("missing {0}: {1}".format(label, path)) from exc
    except OSError as exc:
        raise ReviewError("cannot inspect {0}: {1}".format(label, path)) from exc
    if stat.S_ISLNK(info.st_mode) or not stat.S_ISDIR(info.st_mode):
        raise ReviewError("{0} must be a regular directory: {1}".format(label, path))
    return path


def _load_json(path: Path, label: str) -> Dict[str, Any]:
    try:
        document = json.loads(
            path.read_bytes().decode("utf-8"), object_pairs_hook=_reject_duplicate_keys
        )
    except (OSError, UnicodeDecodeError, json.JSONDecodeError, RecursionError, ValueError) as exc:
        raise ReviewError("invalid JSON in {0}: {1}".format(label, exc)) from exc
    if not isinstance(document, dict):
        raise ReviewError("{0} must be a JSON object".format(label))
    return document


def _fixed_preview_path(root: Path, asset_id: str, supplied: Optional[Path] = None) -> Path:
    if IDENTIFIER_RE.fullmatch(asset_id) is None:
        raise ReviewError("asset_id must be a safe lowercase identifier")
    expected = _absolute(root) / PREVIEW_ROOT_RELATIVE / asset_id
    if supplied is not None and _absolute(supplied) != expected:
        raise ReviewError("preview directory must be the exact asset validation-preview leaf")
    try:
        governance.reject_protected_output(root, expected, "preview directory")
        governance._reject_symlink_components_below(_absolute(root), expected, "preview directory")
    except (OSError, RuntimeError, TypeError, ValueError) as exc:
        raise ReviewError(str(exc)) from exc
    if expected.exists() or expected.is_symlink():
        if expected.is_symlink() or not expected.is_dir():
            raise ReviewError("preview directory must be a regular directory")
        try:
            if stat.S_IMODE(expected.lstat().st_mode) != 0o700:
                raise ReviewError("preview directory must use mode 0700")
        except OSError as exc:
            raise ReviewError("preview directory could not be inspected") from exc
    return expected


def capture_name(seed: int, lighting: str) -> str:
    if seed not in SEEDS:
        raise ReviewError("unsupported review seed: {0}".format(seed))
    if lighting not in LIGHTING_MODES:
        raise ReviewError("unsupported review lighting mode: {0}".format(lighting))
    return "seed-{0}-{1}.png".format(seed, lighting)


def auxiliary_capture_name(seed: int, lighting: str, kind: str) -> str:
    capture_name(seed, lighting)
    if kind not in ("staged", "reference"):
        raise ReviewError("unsupported auxiliary capture kind: {0}".format(kind))
    return "seed-{0}-{1}-{2}.png".format(seed, lighting, kind)


def _auxiliary_capture_path(output: Path, seed: int, lighting: str, kind: str) -> Path:
    return output.parent / auxiliary_capture_name(seed, lighting, kind)


def _camera_size_limit(
    bounds_dimensions: Optional[Tuple[float, float, float]] = None,
) -> float:
    if bounds_dimensions is None:
        return CAMERA_SIZE_MAX
    diagonal = math.sqrt(sum(float(value) ** 2 for value in bounds_dimensions))
    if not math.isfinite(diagonal) or diagonal < 0.0:
        raise ReviewError("runtime GLB bounds dimensions are invalid")
    return min(CAMERA_SIZE_MAX, max(CAMERA_SIZE_MIN, diagonal * CAMERA_SIZE_DIMENSION_SCALE))


def _validate_camera_transform(
    value: object,
    *,
    bounds_dimensions: Optional[Tuple[float, float, float]] = None,
) -> Dict[str, Any]:
    if not isinstance(value, dict) or set(value) != {"projection", "position", "target", "size"}:
        raise ReviewError("camera transform is not canonical")
    if value.get("projection") != "orthogonal":
        raise ReviewError("camera projection is not orthogonal")
    vectors: Dict[str, list[float]] = {}
    for field in ("position", "target"):
        raw_values = value.get(field)
        if not isinstance(raw_values, list) or len(raw_values) != 3:
            raise ReviewError("camera transform vector is invalid")
        values = [float(item) for item in raw_values] if all(
            not isinstance(item, bool) and isinstance(item, (int, float)) for item in raw_values
        ) else []
        if len(values) != 3 or any(not math.isfinite(item) for item in values):
            raise ReviewError("camera transform vector is invalid")
        vectors[field] = values
    delta = [vectors["position"][index] - vectors["target"][index] for index in range(3)]
    distance = math.sqrt(sum(item * item for item in delta))
    if not math.isfinite(distance) or distance < CAMERA_DISTANCE_MIN:
        raise ReviewError("camera distance is below the governed minimum")
    locked_length = math.sqrt(sum(item * item for item in LOCKED_CAMERA_DIRECTION))
    normalized = [item / distance for item in delta]
    locked = [item / locked_length for item in LOCKED_CAMERA_DIRECTION]
    if math.sqrt(sum((normalized[index] - locked[index]) ** 2 for index in range(3))) > LOCKED_CAMERA_DIRECTION_TOLERANCE:
        raise ReviewError("camera transform direction is not locked isometric")
    size = value.get("size")
    if isinstance(size, bool) or not isinstance(size, (int, float)):
        raise ReviewError("camera size is invalid")
    size = float(size)
    if not math.isfinite(size) or size < CAMERA_SIZE_MIN or size > _camera_size_limit(bounds_dimensions):
        raise ReviewError("camera size is outside the governed bound")
    return {
        "projection": "orthogonal",
        "position": vectors["position"],
        "target": vectors["target"],
        "size": size,
    }


def _validate_staged_visibility(value: object) -> Dict[str, Any]:
    if not isinstance(value, dict) or set(value) != {"pass", "opaque_pixels", "luma_range"}:
        raise ReviewError("staged visibility evidence is not canonical")
    opaque_pixels = value.get("opaque_pixels")
    luma_range = value.get("luma_range")
    if (
        value.get("pass") is not True
        or type(opaque_pixels) is not int
        or opaque_pixels < 2
        or opaque_pixels > STAGED_SAMPLE_MAX
        or isinstance(luma_range, bool)
        or not isinstance(luma_range, (int, float))
        or not math.isfinite(float(luma_range))
        or float(luma_range) < 0.004
        or float(luma_range) > 1.0
    ):
        raise ReviewError("staged visibility evidence is outside the strict pixel gate")
    return {
        "pass": True,
        "opaque_pixels": opaque_pixels,
        "luma_range": float(luma_range),
    }


def _validate_contextual_visibility(value: object) -> Dict[str, Any]:
    if not isinstance(value, dict) or set(value) != {
        "pass",
        "reference_pixels",
        "changed_pixels",
        "max_delta",
    }:
        raise ReviewError("contextual visibility evidence is not canonical")
    reference_pixels = value.get("reference_pixels")
    changed_pixels = value.get("changed_pixels")
    max_delta = value.get("max_delta")
    if (
        value.get("pass") is not True
        or type(reference_pixels) is not int
        or reference_pixels < CONTEXTUAL_REFERENCE_MIN
        or reference_pixels > STAGED_SAMPLE_MAX
        or type(changed_pixels) is not int
        or changed_pixels < CONTEXTUAL_CHANGED_MIN
        or changed_pixels > reference_pixels
        or changed_pixels / reference_pixels < CONTEXTUAL_CHANGED_FRACTION_MIN
        or isinstance(max_delta, bool)
        or not isinstance(max_delta, (int, float))
        or not math.isfinite(float(max_delta))
        or float(max_delta) < CONTEXTUAL_RGB_DELTA_MIN
        or float(max_delta) > CONTEXTUAL_RGB_DELTA_MAX
    ):
        raise ReviewError("contextual visibility evidence is outside the strict pixel gate")
    return {
        "pass": True,
        "reference_pixels": reference_pixels,
        "changed_pixels": changed_pixels,
        "max_delta": float(max_delta),
    }


def _png_chunks(raw: bytes, path: Path) -> Tuple[int, int, int, bytes]:
    if not raw.startswith(b"\x89PNG\r\n\x1a\n"):
        raise ReviewError("capture is not a PNG: {0}".format(path))
    offset = 8
    width = height = bit_depth = color_type = None
    idat: list[bytes] = []
    saw_iend = False
    while offset + 12 <= len(raw):
        length = int.from_bytes(raw[offset : offset + 4], "big")
        kind = raw[offset + 4 : offset + 8]
        end = offset + 12 + length
        if end > len(raw):
            raise ReviewError("capture PNG has a truncated chunk: {0}".format(path))
        payload = raw[offset + 8 : offset + 8 + length]
        crc = int.from_bytes(raw[offset + 8 + length : end], "big")
        if (binascii.crc32(kind + payload) & 0xFFFFFFFF) != crc:
            raise ReviewError("capture PNG has an invalid chunk checksum: {0}".format(path))
        if kind == b"IHDR":
            if length != 13 or width is not None:
                raise ReviewError("capture PNG has an invalid IHDR: {0}".format(path))
            width = int.from_bytes(payload[0:4], "big")
            height = int.from_bytes(payload[4:8], "big")
            bit_depth = payload[8]
            color_type = payload[9]
            if payload[10:] != b"\x00\x00\x00":
                raise ReviewError("capture PNG uses unsupported compression/filter/interlace")
        elif kind == b"IDAT":
            idat.append(payload)
        elif kind == b"IEND":
            if length != 0:
                raise ReviewError("capture PNG has a non-empty IEND")
            saw_iend = True
            offset = end
            break
        offset = end
    if offset != len(raw) or width is None or height is None or not idat or not saw_iend:
        raise ReviewError("capture PNG is incomplete: {0}".format(path))
    if (width, height) != CAPTURE_SIZE or bit_depth != 8 or color_type not in (2, 4, 6):
        raise ReviewError("capture PNG is not an 8-bit {0}x{1} image: {2}".format(*CAPTURE_SIZE, path))
    try:
        decoded = zlib.decompress(b"".join(idat))
    except zlib.error as exc:
        raise ReviewError("capture PNG image data is invalid: {0}".format(path)) from exc
    return width, height, color_type, decoded


def _validate_png(path: Path) -> None:
    _regular_file(path, "capture PNG")
    _png_chunks(path.read_bytes(), path)


def _decode_png_samples(
    path: Path,
    *,
    sample_step_x: int,
    sample_step_y: int,
    sample_rows: Optional[set[int]] = None,
) -> list[Tuple[int, int, float, float, float, float, float]]:
    """Decode exact governed pixels using the bounded PNG decoder."""

    width, height, color_type, decoded = _png_chunks(path.read_bytes(), path)
    channels = {2: 3, 4: 2, 6: 4}[color_type]
    row_bytes = width * channels
    expected = height * (row_bytes + 1)
    if len(decoded) != expected:
        raise ReviewError("capture PNG scanlines are invalid: {0}".format(path))
    previous = bytearray(row_bytes)
    samples: list[Tuple[int, int, float, float, float, float, float]] = []
    for y in range(height):
        filter_type = decoded[y * (row_bytes + 1)]
        source = decoded[y * (row_bytes + 1) + 1 : (y + 1) * (row_bytes + 1)]
        row = bytearray(row_bytes)
        for index, value in enumerate(source):
            left = row[index - channels] if index >= channels else 0
            up = previous[index]
            upper_left = previous[index - channels] if index >= channels else 0
            if filter_type == 0:
                result = value
            elif filter_type == 1:
                result = (value + left) & 0xFF
            elif filter_type == 2:
                result = (value + up) & 0xFF
            elif filter_type == 3:
                result = (value + ((left + up) // 2)) & 0xFF
            elif filter_type == 4:
                estimate = left + up - upper_left
                pa = abs(estimate - left)
                pb = abs(estimate - up)
                pc = abs(estimate - upper_left)
                predictor = left if pa <= pb and pa <= pc else up if pb <= pc else upper_left
                result = (value + predictor) & 0xFF
            else:
                raise ReviewError("capture PNG uses an unknown filter: {0}".format(path))
            row[index] = result
        if (
            sample_rows is not None and y in sample_rows
        ) or (sample_rows is None and y % max(1, sample_step_y) == 0):
            for x in range(0, width, max(1, sample_step_x)):
                pixel = row[x * channels : (x + 1) * channels]
                alpha = float(pixel[-1]) / 255.0 if color_type in (4, 6) else 1.0
                if color_type == 6:
                    rgb = pixel[:3]
                elif color_type == 4:
                    rgb = pixel[:1] * 3
                else:
                    rgb = pixel[:3]
                red = float(rgb[0]) / 255.0
                green = float(rgb[1]) / 255.0
                blue = float(rgb[2]) / 255.0
                luma = 0.2126 * red + 0.7152 * green + 0.0722 * blue
                samples.append((x, y, red, green, blue, alpha, luma))
        previous = row
    return samples


def _png_is_visible(path: Path) -> bool:
    """Apply the same conservative nonblank gate used by the Godot capture."""

    samples = _decode_png_samples(
        path,
        sample_step_x=max(1, CAPTURE_SIZE[0] // 32),
        sample_step_y=1,
        sample_rows={0, CAPTURE_SIZE[1] // 8, CAPTURE_SIZE[1] // 4, CAPTURE_SIZE[1] // 2,
                     (3 * CAPTURE_SIZE[1]) // 4, CAPTURE_SIZE[1] - 1},
    )
    visible = [sample[6] for sample in samples if sample[5] >= 0.05]
    if len(visible) < max(2, len(samples) // 8):
        return False
    return max(visible) - min(visible) >= 0.004


def _staged_pixel_evidence(
    samples: Sequence[Tuple[int, int, float, float, float, float, float]],
) -> Dict[str, Any]:
    minimum = min((sample[6] for sample in samples), default=1.0)
    maximum = max((sample[6] for sample in samples), default=0.0)
    opaque_pixels = sum(1 for sample in samples if sample[5] >= 0.05)
    luma_range = maximum - minimum
    return {
        "pass": opaque_pixels >= 2 and bool(samples) and luma_range >= 0.004,
        "opaque_pixels": opaque_pixels,
        "luma_range": luma_range,
    }


def _contextual_pixel_evidence(
    staged: Sequence[Tuple[int, int, float, float, float, float, float]],
    reference: Sequence[Tuple[int, int, float, float, float, float, float]],
    final: Sequence[Tuple[int, int, float, float, float, float, float]],
) -> Dict[str, Any]:
    if not (len(staged) == len(reference) == len(final)):
        raise ReviewError("governed pixel evidence uses inconsistent sample grids")
    reference_pixels = 0
    changed_pixels = 0
    max_delta = 0.0
    for staged_pixel, reference_pixel, final_pixel in zip(staged, reference, final):
        if staged_pixel[:2] != reference_pixel[:2] or staged_pixel[:2] != final_pixel[:2]:
            raise ReviewError("governed pixel evidence uses inconsistent coordinates")
        if staged_pixel[5] < 0.05:
            continue
        reference_pixels += 1
        delta = max(
            abs(final_pixel[2] - reference_pixel[2]),
            abs(final_pixel[3] - reference_pixel[3]),
            abs(final_pixel[4] - reference_pixel[4]),
        )
        max_delta = max(max_delta, delta)
        if delta >= CONTEXTUAL_RGB_DELTA_MIN:
            changed_pixels += 1
    return {
        "pass": (
            reference_pixels >= CONTEXTUAL_REFERENCE_MIN
            and reference_pixels <= STAGED_SAMPLE_MAX
            and changed_pixels >= CONTEXTUAL_CHANGED_MIN
            and changed_pixels <= reference_pixels
            and float(changed_pixels) / float(reference_pixels) >= CONTEXTUAL_CHANGED_FRACTION_MIN
            and max_delta >= CONTEXTUAL_RGB_DELTA_MIN
            and max_delta <= CONTEXTUAL_RGB_DELTA_MAX
        ),
        "reference_pixels": reference_pixels,
        "changed_pixels": changed_pixels,
        "max_delta": max_delta,
    }


def _derive_pixel_evidence(
    staged_path: Path, reference_path: Path, final_path: Path
) -> Tuple[Dict[str, Any], Dict[str, Any]]:
    staged_samples = _decode_png_samples(
        staged_path,
        sample_step_x=STAGED_SAMPLE_STEP_X,
        sample_step_y=STAGED_SAMPLE_STEP_Y,
    )
    reference_samples = _decode_png_samples(
        reference_path,
        sample_step_x=STAGED_SAMPLE_STEP_X,
        sample_step_y=STAGED_SAMPLE_STEP_Y,
    )
    final_samples = _decode_png_samples(
        final_path,
        sample_step_x=STAGED_SAMPLE_STEP_X,
        sample_step_y=STAGED_SAMPLE_STEP_Y,
    )
    staged_evidence = _staged_pixel_evidence(staged_samples)
    contextual_evidence = _contextual_pixel_evidence(
        staged_samples, reference_samples, final_samples
    )
    _validate_staged_visibility(staged_evidence)
    _validate_contextual_visibility(contextual_evidence)
    return staged_evidence, contextual_evidence


def build_runtime_review_document(
    inputs: ValidatedTask, captures: Sequence[Mapping[str, Any]]
) -> Dict[str, Any]:
    expected = [(seed, lighting) for seed in SEEDS for lighting in LIGHTING_MODES]
    by_key: Dict[Tuple[int, str], Dict[str, Any]] = {}
    evidence_hashes: Dict[Tuple[int, str], Tuple[Optional[str], Optional[str]]] = {}
    for item in captures:
        canonical_item = dict(item)
        staged_hash = canonical_item.pop("_staged_output_sha256", None)
        reference_hash = canonical_item.pop("_reference_output_sha256", None)
        if (staged_hash is None) != (reference_hash is None):
            raise ReviewError("runtime pixel evidence hashes are incomplete")
        if staged_hash is not None and (
            not isinstance(staged_hash, str)
            or not isinstance(reference_hash, str)
            or HASH_RE.fullmatch(staged_hash) is None
            or HASH_RE.fullmatch(reference_hash) is None
        ):
            raise ReviewError("runtime pixel evidence hash is invalid")
        if set(canonical_item) != CAPTURE_FIELDS:
            raise ReviewError("runtime capture fields are not canonical")
        seed = canonical_item.get("seed")
        lighting = canonical_item.get("lighting")
        if type(seed) is not int or not isinstance(lighting, str):
            raise ReviewError("runtime capture identity is invalid")
        capture_name(seed, lighting)
        _validate_camera_transform(
            canonical_item.get("camera_transform"),
            bounds_dimensions=inputs.bounds_dimensions,
        )
        _validate_staged_visibility(canonical_item.get("staged_visibility"))
        _validate_contextual_visibility(canonical_item.get("contextual_visibility"))
        key = (seed, lighting)
        if key in by_key:
            raise ReviewError("duplicate runtime capture")
        by_key[key] = canonical_item
        evidence_hashes[key] = (staged_hash, reference_hash)
    if set(by_key) != set(expected):
        raise ReviewError("runtime review requires exactly six captures")
    ordered = [by_key[key] for key in expected]
    if any(item.get("pass") is not True or item.get("reason") != "pass" for item in ordered):
        raise ReviewError("runtime review cannot publish failed captures")
    task_id = inputs.task_id or inputs.task_dir.name
    if TASK_ID_RE.fullmatch(task_id) is None:
        raise ReviewError("runtime task_id is invalid")
    if not HASH_RE.fullmatch(inputs.contract_hash) or not HASH_RE.fullmatch(inputs.cleaned_glb_hash):
        raise ReviewError("runtime input hashes are invalid")
    if not HASH_RE.fullmatch(inputs.blender_validation_hash):
        raise ReviewError("runtime Blender validation hash is invalid")
    output_hashes: Dict[str, str] = {}
    for key, item in zip(expected, ordered):
        seed, lighting = key
        final_hash = str(item["output_sha256"])
        if HASH_RE.fullmatch(final_hash) is None:
            raise ReviewError("runtime capture hash is invalid")
        output_hashes[capture_name(seed, lighting)] = final_hash
        staged_hash, reference_hash = evidence_hashes[key]
        if staged_hash is not None and reference_hash is not None:
            output_hashes[auxiliary_capture_name(seed, lighting, "staged")] = staged_hash
            output_hashes[auxiliary_capture_name(seed, lighting, "reference")] = reference_hash
    return {
        "schema_version": SCHEMA_VERSION,
        "document_kind": DOCUMENT_KIND,
        "asset_id": inputs.asset_id,
        "task_id": task_id,
        "contract_sha256": inputs.contract_hash,
        "cleaned_glb_sha256": inputs.cleaned_glb_hash,
        "blender_validation_sha256": inputs.blender_validation_hash,
        "seeds": list(SEEDS),
        "lighting": list(LIGHTING_MODES),
        "captures": ordered,
        "output_hashes": output_hashes,
        "pass": True,
        "reason": "pass",
    }


def _output_entries(destination: Path) -> list[str]:
    if not destination.exists():
        return []
    if destination.is_symlink() or not destination.is_dir():
        raise ReviewError("preview directory is not a regular directory")
    try:
        return sorted(entry.name for entry in os.scandir(destination))
    except OSError as exc:
        raise ReviewError("preview directory could not be read") from exc


def _validate_existing_leaf(path: Path, expected: Optional[bytes], label: str) -> None:
    if not os.path.lexists(path):
        return
    try:
        info = path.lstat()
    except OSError as exc:
        raise ReviewError("cannot inspect existing {0}".format(label)) from exc
    if stat.S_ISLNK(info.st_mode) or not stat.S_ISREG(info.st_mode):
        raise ReviewError("existing {0} must be a regular file".format(label))
    if stat.S_IMODE(info.st_mode) != 0o600:
        raise ReviewError("existing {0} must use mode 0600".format(label))
    if expected is not None and path.read_bytes() != expected:
        raise ReviewError("existing {0} differs from the requested evidence".format(label))


def _load_runtime_inputs(
    project_root: Path, contract_path: Optional[Path], task_dir: Path
) -> Tuple[ValidatedTask, Dict[str, Any], Dict[str, Any], Path]:
    """Load the only permitted task, contract, generation, and R4 evidence."""

    try:
        import meshy_candidate_review as candidate_review

        review_path, review, generation, root, _asset_root = candidate_review._load_task_record(
            project_root, task_dir
        )
        resolved_task = Path(review_path).parent
        task_contract_path = candidate_review._governed_artifact(root, resolved_task, "contract.json")
        for private_path, private_label in (
            (review_path, "review.json"),
            (task_contract_path, "contract.json"),
            (candidate_review._governed_artifact(root, resolved_task, "generation.json"), "generation.json"),
        ):
            if private_path.lstat().st_mode & 0o077:
                raise ReviewError("task {0} must be private".format(private_label))
        task_contract, task_contract_raw = governance.strict_load_json_bytes(
            task_contract_path, "task contract", 4 * 1024 * 1024
        )
        if task_contract_raw != canonical_json_bytes(task_contract):
            raise ReviewError("task contract is not canonical JSON")
        task_contract_model = load_contract(task_contract_path)
        if review.get("state") not in ("selected", "promotion_ready"):
            raise ReviewError("runtime review requires a selected or promotion_ready candidate")
        if generation.get("status") != "SUCCEEDED":
            raise ReviewError("runtime review requires SUCCEEDED generation evidence")
        if generation.get("asset_id") != task_contract_model.asset_id or generation.get("task_id") != resolved_task.name:
            raise ReviewError("generation identity is not bound to the task")
        if generation.get("contract_artifact_sha256") != hashlib.sha256(task_contract_raw).hexdigest():
            raise ReviewError("generation contract artifact is not bound")
        caller = load_contract(contract_path) if contract_path is not None else task_contract_model
        if caller.snapshot_bytes() != task_contract_model.snapshot_bytes():
            raise ReviewError("caller contract does not match task-local contract")
        if contract_path is not None and generation.get("contract_sha256") != caller.sha256:
            raise ReviewError("generation contract hash is not bound to caller contract")
        if contract_path is None:
            if generation.get("contract_sha256") != task_contract_model.sha256:
                raise ReviewError("generation contract hash is not bound to the task-local contract")
        bound_contract_hash = task_contract_model.sha256
        cleaned = candidate_review._governed_artifact(root, resolved_task, "cleaned.glb")
        r4_path = candidate_review._governed_artifact(root, resolved_task, "blender-validation.json")
        cleaned_info = cleaned.lstat()
        if stat.S_ISLNK(cleaned_info.st_mode) or not stat.S_ISREG(cleaned_info.st_mode) or cleaned_info.st_size <= 0:
            raise ReviewError("cleaned.glb must be a bounded regular file")
        cleaned_hash = governance.file_sha256(cleaned)
        r4, r4_raw = governance.strict_load_json_bytes(
            r4_path, "Blender validation report", 4 * 1024 * 1024
        )
        if r4_raw != canonical_json_bytes(r4):
            raise ReviewError("Blender validation report is not canonical JSON")
        import meshy_blender_validate as blender_validate

        try:
            expected_r4 = blender_validate.verify_validation_report(
                cleaned,
                task_contract_path,
                r4,
                task_id=resolved_task.name,
                expected_contract_sha256=generation["contract_sha256"],
            )
        except (OSError, TypeError, ValueError, RuntimeError) as exc:
            raise ReviewError("R4 Blender evidence does not match the current task: " + str(exc)) from exc
        inputs = ValidatedTask(
            asset_id=task_contract_model.asset_id,
            task_id=resolved_task.name,
            task_dir=resolved_task,
            cleaned_glb=cleaned,
            validation_report=r4_path,
            contract_hash=bound_contract_hash,
            cleaned_glb_hash=cleaned_hash,
            blender_validation_hash=hashlib.sha256(r4_raw).hexdigest(),
            cleaned_glb_size=cleaned_info.st_size,
            category=str(task_contract_model.document.get("category", "")),
            bounds_dimensions=(
                float(expected_r4["bounds"]["dimensions"][0]),
                float(expected_r4["bounds"]["dimensions"][1]),
                float(expected_r4["bounds"]["dimensions"][2]),
            ),
        )
        return inputs, review, generation, root
    except ReviewError:
        raise
    except (OSError, TypeError, ValueError, RuntimeError, RecursionError) as exc:
        raise ReviewError("runtime task evidence is not fully governed: " + str(exc)) from exc


def _validate_runtime_document(
    document: Mapping[str, Any], inputs: ValidatedTask
) -> Dict[str, Any]:
    if not isinstance(document, dict):
        raise ReviewError("runtime review document must be an object")
    stack: list[Tuple[Any, int]] = [(document, 0)]
    seen: set[int] = set()
    while stack:
        current, depth = stack.pop()
        if depth > 64:
            raise ReviewError("runtime review JSON maximum nesting depth exceeded")
        if isinstance(current, (dict, list)):
            identity = id(current)
            if identity in seen:
                raise ReviewError("runtime review JSON contains a cyclic value")
            seen.add(identity)
            if isinstance(current, dict):
                for key, value in current.items():
                    if not isinstance(key, str):
                        raise ReviewError("runtime review JSON keys must be strings")
                    stack.append((value, depth + 1))
            else:
                stack.extend((value, depth + 1) for value in current)
    if set(document) != RUNTIME_DOCUMENT_FIELDS:
        raise ReviewError("runtime review document fields are not canonical")
    if (
        document.get("schema_version") != SCHEMA_VERSION
        or document.get("document_kind") != DOCUMENT_KIND
        or document.get("asset_id") != inputs.asset_id
        or document.get("task_id") != (inputs.task_id or inputs.task_dir.name)
        or document.get("contract_sha256") != inputs.contract_hash
        or document.get("cleaned_glb_sha256") != inputs.cleaned_glb_hash
        or document.get("blender_validation_sha256") != inputs.blender_validation_hash
        or document.get("seeds") != list(SEEDS)
        or document.get("lighting") != list(LIGHTING_MODES)
        or document.get("pass") is not True
        or document.get("reason") != "pass"
    ):
        raise ReviewError("runtime review document identity or gate is invalid")
    if not HASH_RE.fullmatch(str(document["contract_sha256"])) or not HASH_RE.fullmatch(str(document["cleaned_glb_sha256"])) or not HASH_RE.fullmatch(str(document["blender_validation_sha256"])):
        raise ReviewError("runtime review input hashes are invalid")
    captures = document.get("captures")
    if not isinstance(captures, list) or len(captures) != 6:
        raise ReviewError("runtime review must contain six captures")
    expected = [(seed, lighting) for seed in SEEDS for lighting in LIGHTING_MODES]
    by_key: Dict[Tuple[int, str], Mapping[str, Any]] = {}
    for item in captures:
        if not isinstance(item, dict) or set(item) != CAPTURE_FIELDS:
            raise ReviewError("runtime capture fields are not canonical")
        seed = item.get("seed")
        lighting = item.get("lighting")
        if type(seed) is not int or lighting not in LIGHTING_MODES:
            raise ReviewError("runtime capture identity is invalid")
        key = (seed, str(lighting))
        if key in by_key:
            raise ReviewError("runtime review contains duplicate capture")
        by_key[key] = item
        if item.get("pass") is not True or item.get("reason") != "pass":
            raise ReviewError("runtime review contains a failed capture")
        output_sha = item.get("output_sha256")
        if not isinstance(output_sha, str) or HASH_RE.fullmatch(output_sha) is None:
            raise ReviewError("runtime capture output hash is invalid")
        _validate_camera_transform(
            item.get("camera_transform"),
            bounds_dimensions=inputs.bounds_dimensions,
        )
        _validate_staged_visibility(item.get("staged_visibility"))
        _validate_contextual_visibility(item.get("contextual_visibility"))
    if set(by_key) != set(expected):
        raise ReviewError("runtime review capture identity is invalid")
    if [(item["seed"], item["lighting"]) for item in captures] != expected:
        raise ReviewError("runtime review capture order is not canonical")
    output_hashes = document.get("output_hashes")
    if not isinstance(output_hashes, dict) or set(output_hashes) != set(FIXED_OUTPUT_NAMES[:-1]):
        raise ReviewError("runtime output hash map is not canonical")
    if any(not isinstance(value, str) or HASH_RE.fullmatch(value) is None for value in output_hashes.values()):
        raise ReviewError("runtime output hash map contains an invalid hash")
    for seed, lighting in expected:
        name = capture_name(seed, lighting)
        if output_hashes.get(name) != by_key[(seed, lighting)].get("output_sha256"):
            raise ReviewError("runtime output hash map does not match captures")
    return dict(document)


def verify_evidence_chain(
    project_root: Path, task_dir: Path
) -> Dict[str, Any]:
    """Verify only evidence derived from the fixed governed task/report/leaves."""

    inputs, _review, _generation, root = _load_runtime_inputs(project_root, None, task_dir)
    destination = _fixed_preview_path(root, inputs.asset_id)
    entries = _output_entries(destination)
    if sorted(entries) != sorted(FIXED_OUTPUT_NAMES):
        raise ReviewError("fixed runtime preview directory is incomplete or has unexpected entries")
    report_path = destination / "runtime-review.json"
    try:
        document, raw = governance.strict_load_json_bytes(
            report_path, "runtime-review.json", 4 * 1024 * 1024
        )
    except (OSError, TypeError, ValueError, RecursionError) as exc:
        raise ReviewError("runtime-review.json is invalid") from exc
    if raw != canonical_json_bytes(document) or stat.S_IMODE(report_path.lstat().st_mode) != 0o600:
        raise ReviewError("runtime-review.json is not canonical mode-0600 evidence")
    validated = _validate_runtime_document(document, inputs)
    captures = {
        (item["seed"], item["lighting"]): item for item in validated["captures"]
    }
    for seed, lighting in ((seed, lighting) for seed in SEEDS for lighting in LIGHTING_MODES):
        final_path = destination / capture_name(seed, lighting)
        staged_path = destination / auxiliary_capture_name(seed, lighting, "staged")
        reference_path = destination / auxiliary_capture_name(seed, lighting, "reference")
        for path in (final_path, staged_path, reference_path):
            _validate_existing_leaf(path, None, "capture " + path.name)
            _validate_png(path)
            digest = hashlib.sha256(path.read_bytes()).hexdigest()
            if digest != validated["output_hashes"][path.name]:
                raise ReviewError("runtime capture hash does not match report: " + path.name)
        if not _png_is_visible(final_path):
            raise ReviewError("runtime capture is blank or near-uniform: " + final_path.name)
        staged_visibility, contextual_visibility = _derive_pixel_evidence(
            staged_path, reference_path, final_path
        )
        expected_capture = captures[(seed, lighting)]
        if (
            staged_visibility != expected_capture["staged_visibility"]
            or contextual_visibility != expected_capture["contextual_visibility"]
        ):
            raise ReviewError("runtime pixel evidence does not match report: " + final_path.name)
    return document


def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="Verify a published Meshy runtime review against its task evidence.")
    sub = parser.add_subparsers(dest="command", required=True)
    verify = sub.add_parser("verify", help="re-derive every capture gate and hash from the fixed preview leaves")
    verify.add_argument("--project-root", type=Path, required=True)
    verify.add_argument("--task-dir", type=Path, required=True)
    args = parser.parse_args(argv)
    try:
        root = governance.physical_project_root(args.project_root)
        document = verify_evidence_chain(root, _project_path(root, args.task_dir, "task directory"))
    except (OSError, TypeError, ValueError, ReviewError) as exc:
        print("meshy_runtime_review: " + str(exc), file=sys.stderr)
        return 1
    print("MESHY RUNTIME REVIEW VERIFY PASS asset={0} task_id={1} captures={2}".format(
        document["asset_id"], document["task_id"], len(document["captures"])))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
