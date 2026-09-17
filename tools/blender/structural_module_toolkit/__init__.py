"""Structural Module Toolkit Blender add-on.

The toolkit provides a small N-panel workflow for authoring structural module
source assets.  Contract generation, helper creation, and staged GLB export are
implemented in the registered operators; import and validation remain explicit
extension points for the later toolkit tasks.
"""

from __future__ import annotations

try:
    import bpy
except ModuleNotFoundError:  # Pure-Python helpers remain importable for unit tests.
    bpy = None  # type: ignore[assignment]

if bpy is not None:
    from . import operators, panels, preferences
else:
    operators = panels = preferences = None  # type: ignore[assignment]


bl_info = {
    "name": "Structural Module Toolkit",
    "author": "Synaptic Sea",
    "version": (1, 0, 0),
    "blender": (4, 0, 0),
    "location": "View3D > Sidebar > Structural",
    "description": "Create and edit structural modules with contract-derived helpers",
    "category": "Object",
}


if bpy is not None:
    _CLASSES = (
        preferences.StructuralModulePreferences,
        *operators.CLASSES,
        panels.VIEW3D_PT_structural_module_tool,
    )
else:
    _CLASSES = ()


def register() -> None:
    """Register the add-on classes and scene properties with Blender."""
    for cls in _CLASSES:
        bpy.utils.register_class(cls)
    panels.register_properties()


def unregister() -> None:
    """Unregister the add-on classes and scene properties from Blender."""
    panels.unregister_properties()
    for cls in reversed(_CLASSES):
        bpy.utils.unregister_class(cls)


if __name__ == "__main__":
    register()
