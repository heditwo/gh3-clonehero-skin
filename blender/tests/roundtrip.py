"""Round trip of one venue package through the add-on: Open .chstage, Export .chstage without edits, compare.

blender --background --factory-startup --python-exit-code 1 --python blender/tests/roundtrip.py -- ZIP PACKAGE OUTPUT

It writes OUTPUT/<id>.chstage (export with the kept source rows), OUTPUT/computed/<id>.chstage (export from the Blender
data only), and OUTPUT/<id>.json (the comparison of both with PACKAGE, pipeline.package.compare).
"""

import json
import resource
import sys
import time
from pathlib import Path

import bpy

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
from pipeline.package import compare  # noqa: E402

args = sys.argv[sys.argv.index("--") + 1 :]
archive, package, output = Path(args[0]), Path(args[1]), Path(args[2])
output.mkdir(parents=True, exist_ok=True)
module = "bl_ext.user_default.chstage_tools"
if module not in bpy.context.preferences.addons:
    bpy.ops.extensions.package_install_files(filepath=str(archive), repo="user_default", enable_on_install=True)
validate = sys.modules[module + ".vendor.package_format.validate"]

started = time.perf_counter()
bpy.ops.chstage.open_package(filepath=str(package))
opened = time.perf_counter() - started
scene = next(s for s in bpy.data.scenes if s.chstage.source_package)
name = scene.chstage.package.package_id
open_messages = [(i.severity, i.message) for i in scene.chstage.issues]
result = {
    "blender": bpy.app.version_string,
    "package": str(package),
    "openSeconds": round(opened, 1),
    "openMessages": open_messages,
    "objects": len(scene.objects),
    "keptFiles": [f.path for f in scene.chstage.kept_files],
}
for mode, target in (("keep", output / f"{name}.chstage"), ("compute", output / "computed" / f"{name}.chstage")):
    started = time.perf_counter()
    with bpy.context.temp_override(scene=scene, view_layer=scene.view_layers[0]):
        status = bpy.ops.chstage.export_package(
            filepath=str(target), keep_source=mode == "keep", write_folder=mode == "keep"
        )
    issues = [(i.severity, i.message) for i in scene.chstage.issues]
    entry = {"status": sorted(status), "seconds": round(time.perf_counter() - started, 1), "issues": issues}
    if target.is_file():
        report = validate.validate(target)
        entry["validator"] = {"errors": report.errors, "warnings": report.warnings}
        entry["compare"] = compare.compare_packages(package, target)
    result[mode] = entry
    print(
        f"CHSTAGE-ROUNDTRIP {name} {mode}: {entry['status']} {entry['seconds']} s, "
        f"{len(entry.get('compare', {}).get('failures', ['not written']))} differences"
    )
again = output / "again" / f"{name}.chstage"
with bpy.context.temp_override(scene=scene, view_layer=scene.view_layers[0]):
    bpy.ops.chstage.export_package(filepath=str(again))
result["reproducible"] = again.is_file() and again.read_bytes() == (output / f"{name}.chstage").read_bytes()
result["maxRssMB"] = round(resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 1024)
(output / f"{name}.json").write_text(json.dumps(result, indent=2, default=str) + "\n")
print(f"CHSTAGE-ROUNDTRIP {name} DONE {output / (name + '.json')}")
