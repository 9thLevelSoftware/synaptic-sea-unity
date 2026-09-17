"""User preferences for the Structural Module Toolkit add-on."""

from __future__ import annotations

import bpy
from bpy.props import StringProperty


class StructuralModulePreferences(bpy.types.AddonPreferences):
    """Configure source and staging roots used by later toolkit operators."""

    bl_idname = "structural_module_toolkit"

    source_root: StringProperty(
        name="Source Root",
        description="External source root containing structural module assets and contracts",
        subtype="DIR_PATH",
        default="",
    )
    staging_root: StringProperty(
        name="Staging Root",
        description="Directory where runtime GLB exports are staged",
        subtype="DIR_PATH",
        default="",
    )

    def draw(self, context):
        layout = self.layout
        layout.prop(self, "source_root")
        layout.prop(self, "staging_root")


CLASSES = (StructuralModulePreferences,)
