"""Blender side of the round trip. Run: blender-clean --background --factory-startup [file.blend] --python roundtrip.py -- MODE ..."""

import json
import sys

import bpy

IMPORT = {
    "default": {},
    "temperance": {"bone_heuristic": "TEMPERANCE"},
    "fortune": {"bone_heuristic": "FORTUNE"},
    "nobind": {"guess_original_bind_pose": False},
    "merge": {"merge_vertices": True},
}
EXPORT = {
    "default": {},
    "keep": {"export_extras": True, "export_attributes": True},
    "defbones": {"export_def_bones": True},
    "posed": {"export_rest_position_armature": False},
    "flatten": {"export_hierarchy_flatten_bones": True},
    "zup": {"export_yup": False},
    "noanim": {"export_animations": False},
}


def export(path, settings):
    bpy.ops.export_scene.gltf(filepath=path, export_format="GLB", **EXPORT[settings])


args = sys.argv[sys.argv.index("--")+1:]
mode = args[0]
if mode == "import":
    source, output, import_settings, export_settings = args[1:5]
    bpy.ops.wm.read_factory_settings(use_empty=True)
    if import_settings.startswith("fps"):
        # The importer converts key times to frames at the scene rate; the exporter samples whole frames.
        bpy.context.scene.render.fps = int(import_settings[3:])
        import_settings = "default"
    bpy.ops.import_scene.gltf(filepath=source, **IMPORT[import_settings])
    if export_settings.startswith("blend:"):
        bpy.ops.wm.save_as_mainfile(filepath=output)
    else:
        if export_settings == "posed-frame":
            # Creator left the timeline on a clip frame: the armature is posed, not at rest.
            bpy.context.scene.frame_set(20)
            export(output, "posed")
        else:
            export(output, export_settings)
    armature = next(o for o in bpy.data.objects if o.type == "ARMATURE")
    print("SPIKE", json.dumps({"bones": len(armature.data.bones), "actions": len(bpy.data.actions),
                               "fps": bpy.context.scene.render.fps, "objects": [o.name for o in bpy.data.objects]}))
elif mode == "export":
    export(args[1], args[2])
