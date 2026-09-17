"""Draft structural module contracts generated from family templates.

The creator is deliberately Blender-free so contract generation can be tested and
used by the add-on before Blender scene state exists.  Coordinates in the
contract are Y-up: X/Z are the horizontal grid axes and Y is height.
"""

from __future__ import annotations

from dataclasses import dataclass
import re
from typing import Callable, Sequence


GRID_STEP_M = 4.0
KIT_ID = "ship_structural_v0"
_SCHEMA_VERSION = "1.0.0"
_MODULE_ID_PATTERN = re.compile(r"[A-Za-z0-9_-]+")


@dataclass(frozen=True)
class FamilyTemplate:
    """The contract defaults associated with one structural module family."""

    placement_origin: str
    height_m: float
    socket_count: int
    socket_builder: Callable[[float, float, float], list[dict[str, object]]]
    nav_blocker: bool = False
    thickness_m: float = 0.25



def _socket(
    socket_id: str,
    kind: str,
    position: Sequence[float],
    compatible_kinds: Sequence[str],
) -> dict[str, object]:
    return {
        "id": socket_id,
        "kind": kind,
        "position_m": [float(value) for value in position],
        "compatible_kinds": list(compatible_kinds),
    }



def _edge_sockets(width_m: float, depth_m: float, _height_m: float) -> list[dict[str, object]]:
    """Return the four cardinal sockets at the footprint boundaries."""

    compatible = ("floor_edge", "corridor_edge")
    return [
        _socket("floor_edge_north_01", "floor_edge", (0.0, 0.0, depth_m / 2.0), compatible),
        _socket("floor_edge_south_01", "floor_edge", (0.0, 0.0, -depth_m / 2.0), compatible),
        _socket("floor_edge_east_01", "floor_edge", (width_m / 2.0, 0.0, 0.0), compatible),
        _socket("floor_edge_west_01", "floor_edge", (-width_m / 2.0, 0.0, 0.0), compatible),
    ]



def _support_sockets(width_m: float, depth_m: float, _height_m: float) -> list[dict[str, object]]:
    compatible = ("support_edge", "floor_attach")
    return [
        _socket("support_edge_north_01", "support_edge", (0.0, 0.0, depth_m / 2.0), compatible),
        _socket("support_edge_south_01", "support_edge", (0.0, 0.0, -depth_m / 2.0), compatible),
        _socket("support_edge_east_01", "support_edge", (width_m / 2.0, 0.0, 0.0), compatible),
        _socket("support_edge_west_01", "support_edge", (-width_m / 2.0, 0.0, 0.0), compatible),
    ]



def _ceiling_sockets(width_m: float, depth_m: float, _height_m: float) -> list[dict[str, object]]:
    compatible = ("ceiling_edge", "occlusion")
    return [
        _socket("ceiling_edge_north_01", "ceiling_edge", (0.0, 0.0, depth_m / 2.0), compatible),
        _socket("ceiling_edge_south_01", "ceiling_edge", (0.0, 0.0, -depth_m / 2.0), compatible),
        _socket("ceiling_edge_east_01", "ceiling_edge", (width_m / 2.0, 0.0, 0.0), compatible),
        _socket("ceiling_edge_west_01", "ceiling_edge", (-width_m / 2.0, 0.0, 0.0), compatible),
    ]



def _vertical_transition_sockets(
    width_m: float, depth_m: float, _height_m: float
) -> list[dict[str, object]]:
    compatible = ("ramp_edge", "floor_edge")
    return [
        _socket("vertical_edge_north_01", "vertical_edge", (0.0, 0.0, depth_m / 2.0), compatible),
        _socket("vertical_edge_south_01", "vertical_edge", (0.0, 0.0, -depth_m / 2.0), compatible),
        _socket("vertical_edge_east_01", "vertical_edge", (width_m / 2.0, 0.0, 0.0), compatible),
        _socket("vertical_edge_west_01", "vertical_edge", (-width_m / 2.0, 0.0, 0.0), compatible),
    ]



def _wall_sockets(width_m: float, _depth_m: float, height_m: float) -> list[dict[str, object]]:
    edge_compatible = ("wall_edge", "portal_edge")
    return [
        _socket("wall_edge_north_01", "wall_edge", (-width_m / 2.0, 0.0, 0.0), edge_compatible),
        _socket("wall_edge_south_01", "wall_edge", (width_m / 2.0, 0.0, 0.0), edge_compatible),
        _socket("wall_face_01", "wall_face", (0.0, height_m / 2.0, 0.0), ("wall_face",)),
    ]



def _portal_sockets(width_m: float, depth_m: float, height_m: float) -> list[dict[str, object]]:
    edge_compatible = ("portal_edge", "wall_edge")
    return [
        _socket("portal_edge_north_01", "portal_edge", (0.0, 0.0, depth_m / 2.0), edge_compatible),
        _socket("portal_edge_south_01", "portal_edge", (0.0, 0.0, -depth_m / 2.0), edge_compatible),
        _socket("portal_jamb_left_01", "portal_jamb", (-width_m / 2.0, height_m / 2.0, 0.0), ("portal_jamb",)),
        _socket("portal_jamb_right_01", "portal_jamb", (width_m / 2.0, height_m / 2.0, 0.0), ("portal_jamb",)),
    ]


FAMILY_TEMPLATES: dict[str, FamilyTemplate] = {
    "floor": FamilyTemplate("cell-center-floor", 0.25, 4, _edge_sockets),
    "corridor_floor": FamilyTemplate("cell-center-floor", 0.25, 4, _edge_sockets),
    "wall": FamilyTemplate("edge-center", 3.0, 3, _wall_sockets),
    "portal": FamilyTemplate("edge-center", 3.2, 4, _portal_sockets),
    "support": FamilyTemplate("edge-center", 3.0, 4, _support_sockets),
    "ceiling": FamilyTemplate("cell-center-floor", 0.2, 4, _ceiling_sockets),
    "vertical_transition": FamilyTemplate("edge-center", 0.55, 4, _vertical_transition_sockets),
}


def _validate_module_id(module_id: object) -> str:
    if not isinstance(module_id, str) or not _MODULE_ID_PATTERN.fullmatch(module_id):
        raise ValueError(
            "invalid module_id; expected a non-empty value containing only "
            "letters, numbers, underscores, or hyphens"
        )
    return module_id



def _validate_footprint(footprint_cells: object) -> tuple[int, int]:
    if (
        not isinstance(footprint_cells, (tuple, list))
        or len(footprint_cells) != 2
        or any(isinstance(value, bool) or not isinstance(value, int) for value in footprint_cells)
        or footprint_cells[0] < 1
        or footprint_cells[1] < 0
    ):
        raise ValueError("footprint_cells must be two integers [width >= 1, depth >= 0]")
    return int(footprint_cells[0]), int(footprint_cells[1])



def create_draft_contract(
    module_id: str,
    family: str,
    footprint_cells: tuple[int, int] | list[int],
    *,
    grid_step_m: float = GRID_STEP_M,
) -> dict[str, object]:
    """Create a deterministic draft contract for a new structural module.

    ``footprint_cells`` is ``(width, depth)``.  The contract uses Y-up coordinates
    and places horizontal edge sockets on the X/Z footprint boundaries.
    """

    module_id = _validate_module_id(module_id)
    if family not in FAMILY_TEMPLATES:
        raise ValueError(
            f"unsupported structural family {family!r}; "
            f"expected one of {', '.join(FAMILY_TEMPLATES)}"
        )
    if isinstance(grid_step_m, bool) or not isinstance(grid_step_m, (int, float)) or grid_step_m <= 0:
        raise ValueError("grid_step_m must be a positive number")
    step = float(grid_step_m)
    width_cells, depth_cells = _validate_footprint(footprint_cells)
    template = FAMILY_TEMPLATES[family]

    width_m = width_cells * step
    depth_m = depth_cells * step
    half_thickness = template.thickness_m / 2.0
    if family in {"wall", "portal"}:
        local_min = [-width_m / 2.0, 0.0, -half_thickness]
        local_max = [width_m / 2.0, template.height_m, half_thickness]
    else:
        local_min = [-width_m / 2.0, 0.0, -depth_m / 2.0]
        local_max = [width_m / 2.0, template.height_m, depth_m / 2.0]

    sockets = template.socket_builder(width_m, depth_m, template.height_m)
    if len(sockets) != template.socket_count:  # pragma: no cover - guards template edits
        raise RuntimeError(f"family template {family!r} produced an unexpected socket count")

    return {
        "schema_version": _SCHEMA_VERSION,
        "document_kind": "modular_asset_spec",
        "asset_id": module_id,
        "module_id": module_id,
        "category": "structural",
        "kit_id": KIT_ID,
        "module_family": family,
        "grid_step_m": step,
        "bounds": {
            "local_min_m": local_min,
            "local_max_m": local_max,
            "placement_origin": template.placement_origin,
            "mesh_origin_offset_m": [0.0, 0.0, 0.0],
        },
        "footprint_cells": [width_cells, depth_cells],
        "sockets": sockets,
        "collision": {
            "proxy_shape": "box",
            "nav_blocker": template.nav_blocker,
        },
    }


__all__ = ["FAMILY_TEMPLATES", "GRID_STEP_M", "KIT_ID", "create_draft_contract"]
