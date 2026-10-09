"""Open the example .blend files, export them again, and compare the result with the example packages. Run inside Blender:

blender --background --factory-startup --python-exit-code 1 --python examples/check_examples.py -- ADDON_ZIP LIBRARY OUTPUT_DIR

Checks: one scene in each file, no GH3 helper meshes and no local paths in the .blend files, the export is valid, and
it has the same content as the package in examples/ (pipeline.package.compare).
"""

import sys
from pathlib import Path

import bpy

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from pipeline.package import compare  # noqa: E402

args = sys.argv[sys.argv.index("--") + 1:]
archive, library, output = Path(args[0]), Path(args[1]), Path(args[2])
output.mkdir(parents=True, exist_ok=True)
MODULE = "bl_ext.user_default.chstage_tools"
failures = []


def check(name, condition, detail=""):
    print(f"EXAMPLES-CHECK {'PASS' if condition else 'FAIL'} {name} {detail}"[:600])
    if not condition: failures.append(name)


bpy.ops.extensions.package_install_files(filepath=str(archive), repo="user_default", enable_on_install=True)
bpy.context.preferences.addons[MODULE].preferences.library = str(library)
for blend, package, operator in (("example-club.blend", "example.club.chstage", "export_package"),
                                 ("example-guitarist.blend", "example.guitarist.chchar", "export_character")):
    bpy.ops.wm.open_mainfile(filepath=str(ROOT / "examples" / blend))
    scenes = list(bpy.data.scenes)
    check(f"{blend}: one scene", len(scenes) == 1, [s.name for s in scenes])
    scene = scenes[0]
    helpers = [o.name for o in bpy.data.objects if "chstage_helper" in o]
    check(f"{blend}: no GH3 helper meshes", not helpers, helpers)
    paths = [i.filepath for i in bpy.data.images if i.filepath and not i.packed_file]
    check(f"{blend}: all images packed", not paths, paths)
    check(f"{blend}: no library path", not scene.chstage.character.library, scene.chstage.character.library)
    target = output / package
    with bpy.context.temp_override(scene=scene, view_layer=scene.view_layers[0]):
        status = getattr(bpy.ops.chstage, operator)(filepath=str(target))
    issues = [(i.severity, i.message) for i in scene.chstage.issues]
    check(f"{blend}: export", status == {"FINISHED"} and target.is_file(), issues)
    if target.is_file():
        result = compare.compare_packages(ROOT / "examples" / package, target)
        # Each Blender version renders the preview image a little differently.
        other = [f for f in result["failures"] if not f.startswith("preview.png")]
        check(f"{blend}: export equals {package} (preview image not compared)", not other, result["failures"][:5])
print(f"EXAMPLES-CHECK RESULT {'FAIL' if failures else 'PASS'} {bpy.app.version_string}")
sys.exit(1 if failures else 0)
