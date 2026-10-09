"""Headless add-on test. Run inside Blender (see blender/test.sh):

blender --background --factory-startup --python-exit-code 1 --python blender/tests/blender_test.py -- ZIP OUTPUT [RIG_GLB]

It installs and enables the extension zip, makes a stage from the template, sets properties, runs Validate, and checks
the exported files. OUTPUT receives the development folder of the stage and a summary JSON file.
"""

import json
import struct
import sys
from pathlib import Path

import bpy

args = sys.argv[sys.argv.index("--") + 1:]
archive, output = Path(args[0]), Path(args[1])
rig = Path(args[2]) if len(args) > 2 else None
output.mkdir(parents=True, exist_ok=True)
summary = {"blender": bpy.app.version_string, "checks": []}


def check(name, condition, detail=""):
    summary["checks"].append({"check": name, "ok": bool(condition), "detail": str(detail)[:400]})
    print(f"CHSTAGE-TEST {'PASS' if condition else 'FAIL'} {name} {detail}"[:600])


def glb_json(path):
    data = Path(path).read_bytes()
    length, = struct.unpack_from("<I", data, 12)
    return json.loads(data[20:20 + length])


result = bpy.ops.extensions.package_install_files(filepath=str(archive), repo="user_default", enable_on_install=True)
module = "bl_ext.user_default.chstage_tools"
check("install", result == {"FINISHED"} and module in bpy.context.preferences.addons, result)
addon = sys.modules[module]
collect = sys.modules[module + ".collect"]
gltf_io = sys.modules[module + ".gltf_io"]

# Safe glTF import settings, with the Blender 4.2 custom attribute fix.
fixed = gltf_io.fix_custom_attribute_import()
check("custom attribute import fix", fixed == (bpy.app.version < (4, 3, 0)) or not fixed, f"needed: {fixed}")
summary["importerFix"] = fixed
if rig is not None and rig.is_file():
    # In the default scene: background mode has no window, and the importer uses the context scene.
    rig_scene = bpy.context.scene
    gltf_io.import_glb(rig, fps=30)
    mesh = next(o for o in rig_scene.objects if o.type == "MESH" and o.parent and o.parent.type == "ARMATURE")
    names = sorted(a.name for a in mesh.data.attributes if a.name.startswith("_GH3"))
    check("import GH3 rig with custom attributes", rig_scene.render.fps == 30 and len(names) == 8, names)

bpy.ops.chstage.new_stage()
scene = bpy.data.scenes["CH Stage"]
view_layer = scene.view_layers[0]
check("template", {"band-guitarist", "wide", "guitar", "Stage", "moving-light"} <= set(scene.objects.keys()), sorted(scene.objects.keys()))

with bpy.context.temp_override(scene=scene, view_layer=view_layer):
    bpy.ops.chstage.validate()
issues = [(i.severity, i.message, i.object_name) for i in scene.chstage.issues]
check("template validates without errors", not any(s == "ERROR" for s, _, _ in issues), issues)

# Set properties of each kind, as a creator does in the panels.
package = scene.chstage.package
package.package_id, package.name, package.description = "tester.addon-stage", "Add-on Test Stage", "Made by the headless test"
package.authors[0].name, package.authors[0].role = "Tester", "everything"
package.commercial = False
scene.objects["left"].active_material.chstage.blend = "CUTOUT"
scene.objects["left"].active_material.chstage.alpha_cutoff = 0.25
right = scene.objects["right"].active_material.chstage
right.blend, right.shading, right.color_scale, right.two_sided = "ADDITIVE", "unlit", 4.0, True
right.use_flipbook, right.flipbook_columns, right.flipbook_rows, right.flipbook_fps = True, 4, 2, 12
scene.objects["backdrop"].active_material.chstage.movie_slot = 3
scene.objects["backdrop"].chstage.light_group = "Band"
scene.objects["right"].chstage.visibility_group = "crowd-lighters"
scene.objects["right"].chstage.initially_visible = False
scene.objects["left"].chstage.script_object = "left_pillar"
scene.objects["left"].chstage.use_object_pivot = True
crowd = scene.chstage.crowd.add(); crowd.role, crowd.model, crowd.yaw = "crowd2", "crowd_male", 15
note = scene.chstage.camera_notes.add(); note.note, note.group = 77, "wide"
guitar = scene.objects["guitar"].chstage.shot
guitar.use_orbit_angle, guitar.orbit_angle = True, 30
guitar.waypoints.add().name = "guitarist_walk01"

with bpy.context.temp_override(scene=scene, view_layer=view_layer):
    bpy.ops.chstage.validate()
issues = [(i.severity, i.message, i.object_name) for i in scene.chstage.issues]
check("edited stage validates without errors", not any(s == "ERROR" for s, _, _ in issues), issues)

# Development folder: the sidecars and the GLB that Validate checks.
folder = output / "tester.addon-stage.chstage"
folder.mkdir(exist_ok=True)
with bpy.context.temp_override(scene=scene, view_layer=view_layer):
    files, _ = collect.sidecar_files(scene, collect.sidecars.Issues())
    exported = collect.export_stage_glb(scene, folder / "stage.glb")
for name, value in files.items(): (folder / name).write_text(json.dumps(value, indent=2) + "\n")
gltf = glb_json(folder / "stage.glb")
nodes = {n.get("name"): n.get("extras", {}) for n in gltf["nodes"]}
materials = {m.get("name"): m for m in gltf.get("materials", [])}
check("exported objects", set(exported) == {"floor", "backdrop", "left", "right", "moving-light"}, exported)
check("node extras", nodes["backdrop"] == {"gh3LightGroup": "Band"} and nodes["right"] == {"gh3VisibilityGroup": "crowd-lighters", "gh3InitiallyVisible": False}
      and nodes["moving-light"] == {"gh3FixtureId": "left-beam"} and nodes["left"]["gh3Object"] == "left_pillar" and nodes["left"]["gh3ObjectPivot"] == [-7.0, 2.0, 5.0], nodes)
check("material extras", materials["right"]["extras"] == {"gh3Shading": "unlit", "gh3Additive": True, "gh3ColorScale": 4.0,
      "gh3TextureAnimation": {"columns": 4, "rows": 2, "fps": 12.0, "offset": 0.0}} and materials["right"]["alphaMode"] == "BLEND"
      and materials["right"].get("doubleSided") is True and materials["left"]["alphaMode"] == "MASK" and materials["left"]["alphaCutoff"] == 0.25
      and materials["backdrop"]["extras"].get("gh3MovieSlot") == 3, {k: {f: v.get(f) for f in ("extras", "alphaMode", "alphaCutoff", "doubleSided")} for k, v in materials.items()})
check("no add-on data in extras", "chstage" not in json.dumps(gltf), [k for k in json.dumps(gltf).split('"') if "chstage" in k][:5])
check("sidecars", files["placements.json"]["band"]["singer"]["position"] == [0.0, 0.0, -0.5] and files["placements.json"]["crowd"][0]["model"] == "crowd_male"
      and set(files["cameras.json"]["groups"]) == {"wide", "guitarist", "default"} and files["cameras.json"]["shots"]["guitar"]["orbitAngle"] == 30.0
      and files["lighting.json"]["snapshots"]["fill"]["beams"]["left-beam"]["rotation"] == [0.707107, 0.0, 0.0, 0.707107]
      and files["cameras.json"]["notes"] == {"77": {"group": "wide"}}, {k: files[k] for k in ("placements.json", "cameras.json")})
guitar_point = files["cameras.json"]["shots"]["guitar"]["points"][0]
check("anchored camera point", guitar_point["anchor"] == "guitarist.root" and guitar_point["position"] == [-1.0, 1.5, -2.5]
      and abs(guitar_point["horizontalFov"] - 68) < 1e-3, guitar_point)
validator = sys.modules[module + ".vendor.package_format.validate"]
report = validator.Validator(folder).run()
check("bundled validator on the development folder", report.ok, report.errors)

# Errors must name the object, and the Select button must select it.
def validate():
    with bpy.context.temp_override(scene=scene, view_layer=view_layer):
        bpy.ops.chstage.validate()
    return [(i.severity, i.message, i.object_name) for i in scene.chstage.issues if i.severity == "ERROR"]


guitar.waypoints[0].name = "nowhere"
errors = validate()
check("validator error names the shot camera", any("nowhere" in m and o == "guitar" for _, m, o in errors), errors)
index = next(i for i, issue in enumerate(scene.chstage.issues) if "nowhere" in issue.message)
with bpy.context.temp_override(scene=scene, view_layer=view_layer):
    bpy.ops.chstage.select_issue(index=index)
check("select issue", view_layer.objects.active == scene.objects["guitar"] and scene.objects["guitar"].select_get(view_layer=view_layer),
      view_layer.objects.active)
guitar.waypoints[0].name = "guitarist_walk01"
scene.objects["wide"].chstage.shot.groups[0].name = "overview"
scene.objects["band-bassist"].chstage.band_role = "guitarist"
errors = validate()
check("add-on errors", any("group 'wide'" in m for _, m, _ in errors) and any("two band placements" in m and o == "band-bassist" for _, m, o in errors), errors)
scene.objects["wide"].chstage.shot.groups[0].name = "wide"
scene.objects["band-bassist"].chstage.band_role = "bassist"
scene.objects["right"].active_material.chstage.movie_slot = 3
scene.objects["right"].chstage.fixture_id = "no-such-beam"
errors = validate()
check("GLB error names the object", any("no-such-beam" in m and o == "right" for _, m, o in errors), errors)
scene.objects["right"].chstage.fixture_id = ""
check("valid again", not validate(), scene.chstage.issues[0].message if scene.chstage.issues else "")

bpy.ops.preferences.addon_disable(module=module)
check("disable", module not in bpy.context.preferences.addons and not hasattr(bpy.types.Scene, "chstage"))

(output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
failed = [c["check"] for c in summary["checks"] if not c["ok"]]
print(f"CHSTAGE-TEST RESULT {'FAIL ' + ', '.join(failed) if failed else 'PASS'} ({len(summary['checks'])} checks, Blender {bpy.app.version_string})")
if failed: sys.exit(1)
