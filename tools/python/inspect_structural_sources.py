#!/usr/bin/env python3
"""Inspect one recovered Blender structural source without mutating it.

Blender opens the ``.blend`` file before this script runs.  The script only reads
that data-block and emits one machine-readable report line; it never invokes an
operator, changes selection, creates objects, or saves the file.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys
from collections.abc import Sequence
from typing import Any

try:
    from tools.structural_source_contract import STRUCTURAL_SOURCE_MODULE_IDS
except ModuleNotFoundError:  # Blender runs a script with ``tools`` as sys.path[0].
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
    from tools.structural_source_contract import STRUCTURAL_SOURCE_MODULE_IDS


_BPY: Any | None = None


def _require_bpy() -> Any:
    """Import Blender's Python API only when the inspector is executed by Blender."""

    global _BPY
    if _BPY is None:
        _BPY = __import__("bpy")
    return _BPY


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--project-root",
        required=True,
        type=Path,
        help="repository root containing the authoritative structural contracts",
    )
    parser.add_argument(
        "--module",
        required=True,
        help="allowlisted structural source module id",
    )
    return parser


def _argv_from_blender(argv: list[str] | None) -> list[str] | None:
    if argv is not None:
        return list(argv)
    raw = list(sys.argv[1:])
    if "--" in raw:
        return raw[raw.index("--") + 1 :]
    return raw


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = build_parser()
    args = parser.parse_args(_argv_from_blender(argv))
    if args.module not in STRUCTURAL_SOURCE_MODULE_IDS:
        parser.error(f"unsupported structural source module: {args.module!r}")
    return args


def _as_json_value(value: Any) -> Any:
    """Convert Blender IDProperty values to ordinary JSON-compatible values."""

    if isinstance(value, str):
        try:
            return json.loads(value)
        except json.JSONDecodeError:
            return value
    if isinstance(value, Sequence) and not isinstance(value, (str, bytes, bytearray)):
        return [_as_json_value(item) for item in value]
    if isinstance(value, (str, int, float, bool)) or value is None:
        return value
    return str(value)


def _identity_transform(obj: Any) -> bool:
    """Return whether an object's world transform is identity to six decimals."""

    try:
        matrix = obj.matrix_world
        expected = (
            (1.0, 0.0, 0.0, 0.0),
            (0.0, 1.0, 0.0, 0.0),
            (0.0, 0.0, 1.0, 0.0),
            (0.0, 0.0, 0.0, 1.0),
        )
        return all(
            round(float(matrix[row][column]), 6) == expected[row][column]
            for row in range(4)
            for column in range(4)
        )
    except (AttributeError, IndexError, TypeError, ValueError):
        return False


def _location_z_up(obj: Any) -> list[float]:
    try:
        return [float(value) for value in obj.location]
    except (AttributeError, TypeError, ValueError):
        return []


def inspect_loaded_source(module_id: str) -> dict[str, Any]:
    """Read the loaded Blender source and return its deterministic report."""

    bpy = _require_bpy()
    root_name = f"ModuleRoot_{module_id}"
    root = bpy.data.objects.get(root_name)
    helpers = bpy.data.collections.get("AuthoringHelpers")

    helper_names = sorted(obj.name for obj in helpers.objects) if helpers else []
    collision = helpers.objects.get("CollisionProxy") if helpers else None
    collision_report = {
        "proxy_shape": _as_json_value(collision.get("proxy_shape")) if collision else None,
        "nav_blocker": _as_json_value(collision.get("nav_blocker")) if collision else None,
    }

    socket_records: list[dict[str, Any]] = []
    if helpers:
        socket_objects = sorted(
            (obj for obj in helpers.objects if obj.name.startswith("Anchor_SOCK_")),
            key=lambda obj: obj.name,
        )
        for socket in socket_objects:
            socket_records.append(
                {
                    "name": socket.name,
                    "location_z_up": _location_z_up(socket),
                    "socket_id": _as_json_value(socket.get("socket_id")),
                    "kind": _as_json_value(socket.get("kind")),
                    "compatible_kinds": _as_json_value(socket.get("compatible_kinds")),
                    "position_contract_y_up": _as_json_value(
                        socket.get("position_contract_y_up")
                    ),
                }
            )

    return {
        "module_id": module_id,
        "root_name": root.name if root else None,
        "root_identity": _identity_transform(root) if root else False,
        "helper_names": helper_names,
        "collision": collision_report,
        "socket_records": socket_records,
    }


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    report = inspect_loaded_source(args.module)
    print(
        "STRUCTURAL_SOURCE_REPORT "
        + json.dumps(report, sort_keys=True, separators=(",", ":"), allow_nan=False)
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
