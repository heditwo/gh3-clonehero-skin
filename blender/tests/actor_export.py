"""Export the actor models of venue packages from Blender (Open .chstage, then Export .chstage with every actor model
exported from its Blender armature and meshes). Compare the result with blender/tests/actor_compare.py.

blender --background --factory-startup --python blender/tests/actor_export.py -- ZIP OUTPUT PACKAGE ...
"""

import json
import resource
import sys
import time
from pathlib import Path

import bpy

args = sys.argv[sys.argv.index("--") + 1 :]
archive, output, packages = Path(args[0]), Path(args[1]), [Path(p) for p in args[2:]]
output.mkdir(parents=True, exist_ok=True)
module = "bl_ext.user_default.chstage_tools"
if module not in bpy.context.preferences.addons:
    bpy.ops.extensions.package_install_files(filepath=str(archive), repo="user_default", enable_on_install=True)
validate = sys.modules[module + ".vendor.package_format.validate"]
for package in packages:
    started = time.perf_counter()
    bpy.ops.chstage.open_package(filepath=str(package))
    opened = time.perf_counter() - started
    scene = next(s for s in bpy.data.scenes if s.chstage.source_package == str(package.resolve()))
    target = output / package.name
    started = time.perf_counter()
    with bpy.context.temp_override(scene=scene, view_layer=scene.view_layers[0]):
        status = bpy.ops.chstage.export_package(filepath=str(target), actor_models="BLENDER")
    report = validate.validate(target) if target.is_file() else None
    print(
        "CHSTAGE-ACTORS "
        + json.dumps(
            {
                "package": package.name,
                "blender": bpy.app.version_string,
                "status": sorted(status),
                "openSeconds": round(opened, 1),
                "exportSeconds": round(time.perf_counter() - started, 1),
                "issues": [(i.severity, i.message) for i in scene.chstage.issues],
                "errors": report.errors if report else None,
                "warnings": report.warnings if report else None,
                "maxRssMB": round(resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 1024),
            }
        )
    )
    bpy.data.scenes.remove(scene)
