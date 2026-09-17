"""N-panel UI for the Structural Module Toolkit."""

from __future__ import annotations

import bpy
from bpy.props import EnumProperty, IntProperty, StringProperty


ADDON_MODULE = "structural_module_toolkit"

FAMILY_ITEMS = (
    ("floor", "floor", "Floor module"),
    ("corridor_floor", "corridor_floor", "Corridor floor module"),
    ("wall", "wall", "Wall module"),
    ("portal", "portal", "Portal module"),
    ("support", "support", "Support module"),
    ("ceiling", "ceiling", "Ceiling module"),
    ("vertical_transition", "vertical_transition", "Vertical transition module"),
)

SCENE_PROPERTIES = {
    "structural_module_id": StringProperty(
        name="Module ID",
        description="Identifier of the structural module being authored",
        default="floor_3x3",
    ),
    "structural_module_family": EnumProperty(
        name="Family",
        description="Structural module family",
        items=FAMILY_ITEMS,
        default="floor",
    ),
    "structural_grid_size_x": IntProperty(
        name="Grid Width",
        description="Structural module grid width in cells",
        default=3,
        min=1,
        max=256,
    ),
    "structural_grid_size_y": IntProperty(
        name="Grid Height",
        description="Structural module grid height in cells",
        default=3,
        min=1,
        max=256,
    ),
    "structural_status": StringProperty(
        name="Status",
        description="Latest toolkit operation status",
        default="Ready",
    ),
    # Compatibility aliases used by the standalone smoke probe and older scene
    # files.  The panel continues to display the canonical properties above.
    "structural_family": EnumProperty(
        name="Family (compatibility)",
        description="Compatibility alias for structural_module_family",
        items=FAMILY_ITEMS,
        default="floor",
    ),
    "structural_grid_w": IntProperty(
        name="Grid Width (compatibility)",
        description="Compatibility alias for structural_grid_size_x",
        default=3,
        min=1,
        max=256,
    ),
    "structural_grid_d": IntProperty(
        name="Grid Depth (compatibility)",
        description="Compatibility alias for structural_grid_size_y",
        default=3,
        min=0,
        max=256,
    ),
}


def register_properties() -> None:
    """Add toolkit properties to Blender scenes."""
    for name, prop in SCENE_PROPERTIES.items():
        setattr(bpy.types.Scene, name, prop)


def unregister_properties() -> None:
    """Remove toolkit properties from Blender scenes."""
    for name in reversed(tuple(SCENE_PROPERTIES)):
        if hasattr(bpy.types.Scene, name):
            delattr(bpy.types.Scene, name)


class VIEW3D_PT_structural_module_tool(bpy.types.Panel):
    """Structural module controls in the 3D Viewport sidebar."""

    bl_idname = "VIEW3D_PT_structural_module_tool"
    bl_label = "Structural Module Tool"
    bl_category = "Structural"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"

    @classmethod
    def poll(cls, context):
        """Show the panel only in a 3D View while this add-on is enabled."""
        area = getattr(context, "area", None)
        preferences = getattr(context, "preferences", None)
        addons = getattr(preferences, "addons", {}) if preferences else {}
        return (
            area is not None
            and area.type == "VIEW_3D"
            and addons.get(ADDON_MODULE) is not None
        )

    def draw(self, context):
        layout = self.layout
        scene = context.scene

        layout.prop(scene, "structural_module_id", text="Module ID")
        layout.prop(scene, "structural_module_family", text="Family")

        grid_row = layout.row(align=True)
        grid_row.label(text="Grid Size")
        grid_row.prop(scene, "structural_grid_size_x", text="X")
        grid_row.label(text="x")
        grid_row.prop(scene, "structural_grid_size_y", text="Y")

        layout.separator()
        layout.operator("structural.load_contract", text="Load Contract")
        layout.operator("structural.import_glb", text="Import GLB")
        layout.operator("structural.generate_helpers", text="Generate Helpers")
        layout.operator("structural.export_glb", text="Export GLB")
        layout.operator("structural.validate", text="Validate")

        layout.separator()
        status_box = layout.box()
        status_box.label(text=f"Status: {scene.structural_status}")


CLASSES = (VIEW3D_PT_structural_module_tool,)
