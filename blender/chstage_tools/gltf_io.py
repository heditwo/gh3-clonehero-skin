"""Safe glTF import and export settings for package GLB files (docs/spike/blender.md, docs/BLENDER-ADDON.md).

This module imports bpy only inside functions, so the spike tools can load it from its file path.
"""

import importlib
import inspect
import re

# Measured in the Blender round-trip spike: other values break GH3 bone frames, normals, or UV seams.
IMPORT_OPTIONS = {
    "bone_heuristic": "BLENDER",
    "merge_vertices": False,
    "guess_original_bind_pose": True,
    "import_shading": "NORMALS",
}
EXPORT_OPTIONS = {
    "export_format": "GLB",
    # The default exports every scene of the file (and crashes Blender 5.2 in background mode with another scene).
    "use_active_scene": True,
    "export_extras": True,
    "export_attributes": True,
    "export_yup": True,
    "export_rest_position_armature": True,
    "export_hierarchy_flatten_bones": False,
    "export_image_format": "AUTO",
}
IMPORTER_MESH_MODULES = (
    "io_scene_gltf2.blender.imp.gltf2_blender_mesh",
    "io_scene_gltf2.blender.imp.mesh",
)


class _OrderedSet(dict):
    """The parts of 'set' that the Blender 4.2 glTF mesh importer uses, in insertion order."""

    def __init__(self, items=()):
        super().__init__((item, None) for item in items)

    def add(self, item):
        self[item] = None

    def update(self, items):
        for item in items:
            self[item] = None


def fix_custom_attribute_import():
    """Make the Blender 4.2 glTF importer keep custom vertex attributes (_GH3_*) in a stable order.

    The 4.2 importer collects the attribute names in a 'set' but the data in a list. With two or more attributes, the
    set order (random for each Blender process) differs from the list order: import fails or the attribute data goes
    to the wrong names. Later importers use a dict. Returns True when the importer needed the fix.
    """
    for name in IMPORTER_MESH_MODULES:
        try:
            module = importlib.import_module(name)
        except ImportError:
            continue
        try:
            source = inspect.getsource(module)
        except (OSError, TypeError):
            return False
        # Only this importer: 'set' has no other use in the module, so the module-level name is safe to replace.
        if "attributes = set({})" in source and len(re.findall(r"(?<![\w.])set\(", source)) == 2:
            module.set = _OrderedSet
            return True
        return False
    return False


def _supported(operator, options):
    names = set(operator.get_rna_type().properties.keys())
    return {key: value for key, value in options.items() if key in names}


def set_frame_rate(scene, fps):
    """The importer converts key times at the scene rate and the exporter samples whole frames: set it to the clip rate."""
    scene.render.fps = int(fps)
    scene.render.fps_base = 1.0


def import_glb(path, fps=None, **overrides):
    """Import a GLB with the safe options. Set fps to the clip rate of the file (30 for band clips, 60 for venue actors)."""
    import bpy

    if fps:
        set_frame_rate(bpy.context.scene, fps)
    fix_custom_attribute_import()
    operator = bpy.ops.import_scene.gltf
    return operator(filepath=str(path), **_supported(operator, {**IMPORT_OPTIONS, **overrides}))


def export_glb(path, fps=None, **overrides):
    """Export a GLB with the safe options. Blender samples clips at the scene rate, so fps must be the clip rate."""
    import bpy

    if fps:
        set_frame_rate(bpy.context.scene, fps)
    operator = bpy.ops.export_scene.gltf
    return operator(filepath=str(path), **_supported(operator, {**EXPORT_OPTIONS, **overrides}))
