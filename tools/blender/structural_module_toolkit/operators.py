"""Button operators for the Structural Module Toolkit."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import bpy

from .contract_creator import create_draft_contract
from .export import export_scene_to_staging


_CONTRACT_RELATIVE = Path(
    "data/placement/contracts/structural/ship_structural_v0"
)


def _set_status(context: Any, message: str) -> None:
    """Update the panel status when the toolkit scene property is available."""

    scene = getattr(context, "scene", None)
    if scene is not None and hasattr(scene, "structural_status"):
        scene.structural_status = message


def _module_summary(context: Any) -> str:
    scene = getattr(context, "scene", None)
    if scene is None:
        return "<no scene>"
    module_id, family, grid_w, grid_d = _scene_settings(scene)
    return f"module_id={module_id!r}, family={family!r}, grid={grid_w}x{grid_d}"


def _scene_settings(scene: Any) -> tuple[str, str, int, int]:
    """Read canonical scene properties plus the names used by the smoke probe."""

    module_id = str(getattr(scene, "structural_module_id", "")).strip()
    family = str(getattr(scene, "structural_module_family", "floor"))
    alias_family = getattr(scene, "structural_family", None)
    if family == "floor" and alias_family not in (None, "", "floor"):
        family = str(alias_family)

    grid_w = int(getattr(scene, "structural_grid_size_x", 3))
    grid_d = int(getattr(scene, "structural_grid_size_y", 3))
    alias_grid_w = getattr(scene, "structural_grid_w", None)
    alias_grid_d = getattr(scene, "structural_grid_d", None)
    if alias_grid_w is not None and int(alias_grid_w) != 3:
        grid_w = int(alias_grid_w)
    if alias_grid_d is not None and int(alias_grid_d) != 3:
        grid_d = int(alias_grid_d)
    return module_id, family, grid_w, grid_d


def _addon_preferences(context: Any) -> Any | None:
    preferences = getattr(context, "preferences", None)
    addons = getattr(preferences, "addons", None)
    if addons is None:
        return None
    addon = addons.get("structural_module_toolkit")
    return getattr(addon, "preferences", None) if addon is not None else None


def _repository_root() -> Path:
    # operators.py -> structural_module_toolkit -> blender_addons -> tools -> repo
    return Path(__file__).resolve().parents[3]


def _configured_source_root(context: Any) -> Path | None:
    preferences = _addon_preferences(context)
    configured = getattr(preferences, "source_root", "") if preferences else ""
    if not configured:
        return None
    return Path(str(configured)).expanduser()


def _contract_path(root: Path, module_id: str) -> Path:
    return root / _CONTRACT_RELATIVE / f"{module_id}_contract.json"


def _contract_candidates(context: Any, module_id: str) -> list[Path]:
    roots: list[Path] = []
    configured = _configured_source_root(context)
    if configured is not None:
        roots.append(configured)
    roots.extend((_repository_root(), Path.cwd()))

    candidates: list[Path] = []
    seen: set[Path] = set()
    for root in roots:
        for candidate in (
            _contract_path(root, module_id),
            root / f"{module_id}_contract.json",
            root / module_id / f"{module_id}_contract.json",
        ):
            candidate = candidate.resolve(strict=False)
            if candidate not in seen:
                seen.add(candidate)
                candidates.append(candidate)
    return candidates


def _draft_contract_path(context: Any, module_id: str) -> Path:
    configured = _configured_source_root(context)
    if configured is not None and (configured / "data").exists():
        return _contract_path(configured, module_id)
    return _contract_path(_repository_root(), module_id)


def _canonical_json(document: dict[str, object]) -> str:
    return json.dumps(
        document,
        sort_keys=True,
        separators=(",", ":"),
        ensure_ascii=False,
        allow_nan=False,
    ) + "\n"


def _load_or_create_contract(context: Any) -> tuple[dict[str, object], Path, bool]:
    """Load a contract JSON, or write a deterministic draft when none exists."""

    scene = context.scene
    module_id, family, grid_w, grid_d = _scene_settings(scene)
    if not module_id:
        raise ValueError("Module ID is required before generating helpers")

    for candidate in _contract_candidates(context, module_id):
        if not candidate.is_file():
            continue
        try:
            document = json.loads(candidate.read_text(encoding="utf-8"))
        except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise ValueError(f"cannot read structural contract: {candidate}") from exc
        if not isinstance(document, dict):
            raise ValueError(f"structural contract must be a JSON object: {candidate}")
        if document.get("module_id") not in (None, module_id):
            raise ValueError(f"contract module_id mismatch: expected {module_id!r}")
        scene["structural_contract_path"] = str(candidate)
        return document, candidate, False

    document = create_draft_contract(module_id, family, (grid_w, grid_d))
    destination = _draft_contract_path(context, module_id)
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(_canonical_json(document), encoding="utf-8")
    scene["structural_contract_path"] = str(destination)
    return document, destination, True


def _ensure_collection(name: str, scene: Any) -> Any:
    collection = bpy.data.collections.get(name)
    if collection is None:
        collection = bpy.data.collections.new(name)
    if collection.name not in {child.name for child in scene.collection.children}:
        scene.collection.children.link(collection)
    return collection


def _link_object(obj: Any, collection: Any) -> None:
    for existing in list(obj.users_collection):
        existing.objects.unlink(obj)
    collection.objects.link(obj)


def _remove_generated_helpers(helpers: Any) -> None:
    for obj in list(helpers.objects):
        if (
            obj.name in {"Origin", "Anchor_FloorCenter", "CollisionProxy"}
            or obj.name.startswith("Anchor_SOCK_")
        ):
            bpy.data.objects.remove(obj, do_unlink=True)


def _ensure_module_root(module_id: str, scene: Any) -> Any:
    root_name = f"ModuleRoot_{module_id}"
    root = bpy.data.objects.get(root_name)
    if root is None:
        root = bpy.data.objects.new(root_name, None)
        scene.collection.objects.link(root)
    elif not any(collection == scene.collection for collection in root.users_collection):
        scene.collection.objects.link(root)
    root.parent = None
    root.location = (0.0, 0.0, 0.0)
    root.rotation_euler = (0.0, 0.0, 0.0)
    root.scale = (1.0, 1.0, 1.0)
    return root


def _new_empty(name: str, display_type: str, root: Any, helpers: Any, location: tuple[float, float, float]) -> Any:
    empty = bpy.data.objects.new(name, None)
    empty.empty_display_type = display_type
    empty.empty_display_size = 0.3
    helpers.objects.link(empty)
    empty.parent = root
    empty.location = location
    return empty


def _y_up_to_z_up(value: Any) -> tuple[float, float, float]:
    if not isinstance(value, (list, tuple)) or len(value) != 3:
        raise ValueError("contract position must contain three numeric values")
    return (float(value[0]), float(value[2]), float(value[1]))


def _socket_normal(socket_id: str) -> tuple[float, float, float]:
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


def _add_origin_and_floor_center(root: Any, helpers: Any) -> None:
    origin = _new_empty("Origin", "PLAIN_AXES", root, helpers, (0.0, 0.0, 0.0))
    origin.empty_display_size = 0.5
    origin["marker_kind"] = "placement_origin"
    origin["origin_policy"] = "contract_defined"

    anchor = _new_empty(
        "Anchor_FloorCenter", "PLAIN_AXES", root, helpers, (0.0, 0.0, 0.0)
    )
    anchor["marker_kind"] = "floor_center"
    anchor["position_contract_y_up"] = json.dumps([0.0, 0.0, 0.0])
    anchor["position_blender_z_up"] = json.dumps([0.0, 0.0, 0.0])


def _add_sockets(contract: dict[str, object], root: Any, helpers: Any) -> list[Any]:
    sockets = contract.get("sockets", [])
    if not isinstance(sockets, list):
        raise ValueError("contract sockets must be a list")

    result: list[Any] = []
    for socket in sockets:
        if not isinstance(socket, dict):
            raise ValueError("contract socket must be an object")
        socket_id = str(socket.get("id", "")).strip()
        if not socket_id:
            raise ValueError("contract socket is missing id")
        position_contract = socket.get("position_m")
        position_blender = _y_up_to_z_up(position_contract)
        empty = _new_empty(
            f"Anchor_SOCK_{socket_id}",
            "ARROWS",
            root,
            helpers,
            position_blender,
        )
        compatible = socket.get("compatible_kinds", [])
        empty["socket_id"] = socket_id
        empty["kind"] = str(socket.get("kind", ""))
        empty["compatible_kinds"] = json.dumps(list(compatible))
        empty["position_contract_y_up"] = json.dumps(list(position_contract))
        empty["position_blender_z_up"] = json.dumps(list(position_blender))
        empty["orientation_source"] = "socket-id-cardinal-convention"
        empty["normal_local"] = json.dumps(list(_socket_normal(socket_id)))
        empty["up_local"] = json.dumps([0.0, 0.0, 1.0])
        empty["rotation_step_deg"] = 90
        empty["terminal_allowed"] = True
        result.append(empty)
    return result


def _add_collision_proxy(contract: dict[str, object], root: Any, helpers: Any) -> Any:
    bounds = contract.get("bounds")
    if not isinstance(bounds, dict):
        raise ValueError("contract bounds must be an object")
    minimum = bounds.get("local_min_m")
    maximum = bounds.get("local_max_m")
    if not isinstance(minimum, (list, tuple)) or not isinstance(maximum, (list, tuple)):
        raise ValueError("contract bounds must contain local_min_m/local_max_m")
    if len(minimum) != 3 or len(maximum) != 3:
        raise ValueError("contract bounds must contain three coordinates")

    size_contract = tuple(float(hi) - float(lo) for lo, hi in zip(minimum, maximum))
    center_contract = tuple((float(lo) + float(hi)) / 2.0 for lo, hi in zip(minimum, maximum))
    size_blender = _y_up_to_z_up(size_contract)
    center_blender = _y_up_to_z_up(center_contract)

    bpy.ops.mesh.primitive_cube_add(size=1.0, location=(0.0, 0.0, 0.0))
    collision = bpy.context.active_object
    if collision is None:
        raise RuntimeError("Blender did not create CollisionProxy")
    collision.name = "CollisionProxy"
    _link_object(collision, helpers)
    collision.dimensions = size_blender
    bpy.context.view_layer.objects.active = collision
    collision.select_set(True)
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    collision.parent = root
    collision.location = center_blender
    collision.display_type = "WIRE"
    collision.hide_render = True
    collision["proxy_shape"] = str((contract.get("collision") or {}).get("proxy_shape", "box"))
    collision["nav_blocker"] = bool((contract.get("collision") or {}).get("nav_blocker", False))
    collision["bounds_min_contract_y_up"] = json.dumps(list(minimum))
    collision["bounds_max_contract_y_up"] = json.dumps(list(maximum))
    collision["bounds_min_blender_z_up"] = json.dumps(list(_y_up_to_z_up(minimum)))
    collision["bounds_max_blender_z_up"] = json.dumps(list(_y_up_to_z_up(maximum)))
    return collision


def _set_root_properties(root: Any, contract: dict[str, object], contract_path: Path, draft: bool) -> None:
    root["module_id"] = str(contract.get("module_id", contract.get("asset_id", "")))
    root["kit_id"] = str(contract.get("kit_id", ""))
    root["module_family"] = str(contract.get("module_family", ""))
    root["grid_step_m"] = float(contract.get("grid_step_m", 4.0))
    root["footprint_cells"] = list(contract.get("footprint_cells", []))
    bounds = contract.get("bounds") or {}
    root["placement_origin"] = str(bounds.get("placement_origin", ""))
    root["contract_path"] = str(contract_path)
    root["draft_contract"] = draft
    root["contract_json"] = _canonical_json(contract)


def generate_helpers(context: Any) -> dict[str, Any]:
    """Generate contract-derived authoring helpers in the current Blender scene."""

    contract, contract_path, draft = _load_or_create_contract(context)
    module_id = str(contract.get("module_id") or contract.get("asset_id") or "").strip()
    if not module_id:
        raise ValueError("contract is missing module_id")

    scene = context.scene
    geometry = _ensure_collection("Geometry", scene)
    helpers = _ensure_collection("AuthoringHelpers", scene)
    root = _ensure_module_root(module_id, scene)
    _remove_generated_helpers(helpers)
    _set_root_properties(root, contract, contract_path, draft)

    _add_origin_and_floor_center(root, helpers)
    sockets = _add_sockets(contract, root, helpers)
    collision = _add_collision_proxy(contract, root, helpers)

    scene["module_id"] = module_id
    scene["module_family"] = str(contract.get("module_family", ""))
    scene["socket_count"] = len(sockets)
    scene["collision_proxy_name"] = collision.name
    scene["structural_contract_path"] = str(contract_path)
    # Keep Geometry referenced in the result so callers can inspect the scene graph.
    return {
        "module_id": module_id,
        "contract_path": contract_path,
        "draft": draft,
        "geometry": geometry,
        "helpers": helpers,
        "root": root,
        "sockets": sockets,
        "collision": collision,
    }


class STRUCTURAL_OT_load_contract(bpy.types.Operator):
    """Load an existing contract or create the selected module's draft contract."""

    bl_idname = "structural.load_contract"
    bl_label = "Load Contract"
    bl_description = "Load or create the selected structural module contract JSON"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        try:
            _contract, path, draft = _load_or_create_contract(context)
        except (OSError, ValueError, TypeError) as exc:
            self.report({"ERROR"}, str(exc))
            _set_status(context, f"Contract error: {exc}")
            return {"CANCELLED"}
        message = f"Draft contract created: {path}" if draft else f"Contract loaded: {path}"
        print(f"[Structural Module Toolkit] {message}: {_module_summary(context)}")
        _set_status(context, message)
        self.report({"INFO"}, message)
        return {"FINISHED"}


class STRUCTURAL_OT_import_glb(bpy.types.Operator):
    """Import the selected runtime GLB into the current scene."""

    bl_idname = "structural.import_glb"
    bl_label = "Import GLB"
    bl_description = "Import the selected runtime structural module GLB"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        message = "Import GLB is not implemented by this toolkit task"
        print(f"[Structural Module Toolkit] {message}: {_module_summary(context)}")
        _set_status(context, message)
        self.report({"INFO"}, message)
        return {"FINISHED"}


class STRUCTURAL_OT_generate_helpers(bpy.types.Operator):
    """Generate socket empties, anchors, and a contract-derived collision proxy."""

    bl_idname = "structural.generate_helpers"
    bl_label = "Generate Helpers"
    bl_description = "Add contract-derived sockets, anchors, and collision proxy"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        try:
            result = generate_helpers(context)
        except (OSError, ValueError, TypeError, RuntimeError) as exc:
            self.report({"ERROR"}, str(exc))
            _set_status(context, f"Helper generation error: {exc}")
            return {"CANCELLED"}
        message = (
            f"Helpers generated for {result['module_id']} "
            f"(sockets={len(result['sockets'])}, draft={result['draft']})"
        )
        print(f"[Structural Module Toolkit] {message}")
        _set_status(context, message)
        self.report({"INFO"}, message)
        return {"FINISHED"}


class STRUCTURAL_OT_export_glb(bpy.types.Operator):
    """Export tagged structural collections to the configured staging directory."""

    bl_idname = "structural.export_glb"
    bl_label = "Export GLB"
    bl_description = "Export structural module GLBs to staging_root/module_id"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        scene = context.scene
        module_id, _family, _grid_w, _grid_d = _scene_settings(scene)
        preferences = _addon_preferences(context)
        staging_root = getattr(preferences, "staging_root", "") if preferences else ""
        if not staging_root:
            staging_root = scene.get("structural_staging_root", "")
        if not staging_root:
            message = "Set the Structural Module Toolkit Staging Root before exporting"
            self.report({"ERROR"}, message)
            _set_status(context, message)
            return {"CANCELLED"}
        try:
            exported = export_scene_to_staging(bpy, staging_root, module_id or None)
        except (FileNotFoundError, OSError, RuntimeError, ValueError) as exc:
            self.report({"ERROR"}, str(exc))
            _set_status(context, f"Export error: {exc}")
            return {"CANCELLED"}
        message = f"Exported {len(exported)} GLB file(s) for {module_id or 'detected module'}"
        print(f"[Structural Module Toolkit] {message}")
        _set_status(context, message)
        self.report({"INFO"}, message)
        return {"FINISHED"}


class STRUCTURAL_OT_validate(bpy.types.Operator):
    """Run the structural module inspector on the current scene."""

    bl_idname = "structural.validate"
    bl_label = "Validate"
    bl_description = "Run the structural module inspector on the current scene"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        message = "Validate is not implemented by this toolkit task"
        print(f"[Structural Module Toolkit] {message}: {_module_summary(context)}")
        _set_status(context, message)
        self.report({"INFO"}, message)
        return {"FINISHED"}


CLASSES = (
    STRUCTURAL_OT_load_contract,
    STRUCTURAL_OT_import_glb,
    STRUCTURAL_OT_generate_helpers,
    STRUCTURAL_OT_export_glb,
    STRUCTURAL_OT_validate,
)
