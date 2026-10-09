"""Headless add-on test. Run inside Blender (see blender/test.sh):

blender --background --factory-startup --python-exit-code 1 --python blender/tests/blender_test.py -- ZIP OUTPUT [RIG_GLB] [VENUE]

It installs and enables the extension zip, makes a stage from the template, sets properties, runs Validate, exports
the package, opens it again, and exports it again. With VENUE (a .chstage package, for example a GH3 venue), it also
opens, exports, and compares that package. OUTPUT receives the packages and a summary JSON file.
"""

import hashlib
import json
import struct
import sys
import zipfile
from pathlib import Path

import bpy

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
from pipeline.package import compare  # noqa: E402

args = sys.argv[sys.argv.index("--") + 1:]
archive, output = Path(args[0]), Path(args[1])
rig = Path(args[2]) if len(args) > 2 and args[2] else None
venue = Path(args[3]) if len(args) > 3 and args[3] else None
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
scene.objects["left"].chstage.object_pivot_mode = "ORIGIN"
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
package_export = sys.modules[module + ".package_export"]
with bpy.context.temp_override(scene=scene, view_layer=view_layer):
    files, _ = collect.sidecar_files(scene, collect.sidecars.Issues())
    exported, _ = collect.export_stage_glb(scene, folder / "stage.glb")
    package_export.build_files(scene, folder, collect.sidecars.Issues(), render_preview_image=False)
gltf = glb_json(folder / "stage.glb")
nodes = {n.get("name"): n.get("extras", {}) for n in gltf["nodes"]}
materials = {m.get("name"): m for m in gltf.get("materials", [])}
exported = [obj.name for obj in exported]
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
check("add-on errors", any("group 'wide'" in m for _, m, _ in errors) and any("two band placements" in m and o in ("band-bassist", "band-guitarist") for _, m, o in errors), errors)
scene.objects["wide"].chstage.shot.groups[0].name = "wide"
scene.objects["band-bassist"].chstage.band_role = "bassist"
scene.objects["right"].active_material.chstage.movie_slot = 3
scene.objects["right"].chstage.fixture_id = "no-such-beam"
errors = validate()
check("GLB error names the object", any("no-such-beam" in m and o == "right" for _, m, o in errors), errors)
scene.objects["right"].chstage.fixture_id = ""
check("valid again", not validate(), scene.chstage.issues[0].message if scene.chstage.issues else "")

# Export .chstage: zip, development folder, copy into a Custom/Stages folder, preview, lightmap, bone anchor.
image = bpy.data.images.new("tester-lightmap", 4, 4)
image.pixels = [0.5] * 64
floor = scene.objects["floor"]
floor.data.uv_layers.new(name="UVMap.001")
floor.active_material.chstage.lightmap = image
guitar_point = scene.objects["guitar"].chstage.point
guitar_point.anchor_part, guitar_point.anchor_bone = "bone", "c00e3395"
packages = output / "packages"
custom = output / "Custom" / "Stages"


def export(path, **options):
    with bpy.context.temp_override(scene=scene, view_layer=view_layer):
        result = bpy.ops.chstage.export_package(filepath=str(path), **options)
    return result, [(i.severity, i.message, i.object_name) for i in scene.chstage.issues]


result, issues = export(packages / "tester.addon-stage.chstage", write_folder=True, copy_to_game=True, custom_folder=str(custom))
target = packages / "tester.addon-stage.chstage"
check("export operator writes the package", result == {"FINISHED"} and target.is_file() and (packages / "unpacked" / target.name / "manifest.json").is_file()
      and (custom / target.name).read_bytes() == target.read_bytes(), (result, issues))
report = validator.validate(target)
check("exported package is valid", report.ok, report.errors + report.warnings)

with zipfile.ZipFile(target) as package_zip:
    names = package_zip.namelist()
    manifest = json.loads(package_zip.read("manifest.json"))
    stage_json = package_zip.read("stage.glb")
    cameras = json.loads(package_zip.read("cameras.json"))
check("package files", names[0] == "manifest.json" and {"stage.glb", "placements.json", "cameras.json", "lighting.json", "scripts.json"} <= set(names)
      and manifest["entry"].get("scripts") == "scripts.json" and set(manifest["files"]) == set(names) - {"manifest.json"}, names)
summary["preview"] = "preview.png" in names
check("preview image (render or warning)", "preview.png" in names and manifest.get("preview") == "preview.png"
      or any("preview" in m for _, m, _ in issues), issues)
length, = struct.unpack_from("<I", stage_json, 12)
gltf = json.loads(stage_json[20:20 + length])
floor_material = next(m for m in gltf["materials"] if m["name"] == "floor")
check("lightmap is the occlusion texture on TEXCOORD_1", floor_material.get("occlusionTexture", {}).get("texCoord") == 1
      and any(img.get("name") == "tester-lightmap" for img in gltf["images"]), floor_material)
check("bone anchor written as set", cameras["shots"]["guitar"]["points"][0]["anchor"] == "guitarist.bone.c00e3395"
      and any("bone" in m and o == "guitar" for s_, m, o in issues if s_ == "WARNING"), (cameras["shots"]["guitar"]["points"][0], issues))
result, _ = export(output / "again" / target.name)
check("export is reproducible", (output / "again" / target.name).read_bytes() == target.read_bytes(),
      result)
summary["packageSha256"] = hashlib.sha256(target.read_bytes()).hexdigest()

# Open .chstage, then export without edits: the same package.
# No view_layer override here: Blender 5.2.1 crashes after an operator that makes a scene under a view layer override.
with bpy.context.temp_override(scene=scene):
    result = bpy.ops.chstage.open_package(filepath=str(target))
opened = next(s for s in bpy.data.scenes if s.chstage.source_package == str(target.resolve()))
opened_layer = opened.view_layers[0]
# The names of the template scene are in use, so Blender gives the opened objects and materials other names.
point = next(o for o in opened.objects if o.type == "CAMERA" and o.chstage.shot.shot_name == "guitar").chstage.point
floor_object = next(o for o in opened.objects if o.name.startswith("floor"))
check("open package", result == {"FINISHED"} and opened.chstage.package.package_id == "tester.addon-stage"
      and point.anchor_role == "guitarist" and point.anchor_part == "bone" and point.anchor_bone == "c00e3395"
      and floor_object.active_material.chstage.lightmap is not None and opened.chstage.package.preview_mode in ("IMAGE", "NONE")
      and [f.path for f in opened.chstage.kept_files] == ["scripts.json"], [(o.name, o.type) for o in opened.objects][:40])
again = output / "reopened" / target.name
with bpy.context.temp_override(scene=opened, view_layer=opened_layer):
    result = bpy.ops.chstage.export_package(filepath=str(again))
differences = compare.compare_packages(target, again)
check("template round trip", result == {"FINISHED"} and not differences["failures"], differences["failures"])
computed = output / "computed" / target.name
with bpy.context.temp_override(scene=opened, view_layer=opened_layer):
    bpy.ops.chstage.export_package(filepath=str(computed), keep_source=False)
differences = compare.compare_packages(target, computed)
numbers = [r["maxNumeric"] for r in differences["json"].values()]
check("template round trip from the Blender data only", computed.is_file() and max(numbers) < 1e-5 and differences["stage"]["nodeTransform"]["position"] < 1e-5,
      differences["failures"])

# An edit after open changes only the edited row.
next(o for o in opened.objects if o.chstage.marker == "BAND" and o.chstage.band_role == "bassist").location.x += 1
opened.view_layers[0].update()
edited = output / "edited" / target.name
with bpy.context.temp_override(scene=opened, view_layer=opened_layer):
    bpy.ops.chstage.export_package(filepath=str(edited))
differences = compare.compare_packages(target, edited)
changed = [d for group in differences["json"]["placements.json"]["groups"].values() for d in group["examples"]]
check("edit after open", differences["json"]["cameras.json"]["equal"] and changed and all(".band.bassist.position" in d[0] for d in changed), changed)

if venue is not None and venue.is_file():
    with bpy.context.temp_override(scene=scene):
        result = bpy.ops.chstage.open_package(filepath=str(venue))
    opened = next(s for s in bpy.data.scenes if s.chstage.source_package == str(venue.resolve()))
    copy = output / "venue" / venue.name
    with bpy.context.temp_override(scene=opened, view_layer=opened.view_layers[0]):
        status = bpy.ops.chstage.export_package(filepath=str(copy))
    differences = compare.compare_packages(venue, copy) if copy.is_file() else {"failures": ["not written"], "stage": {"max": {}}}
    summary["venue"] = {"package": str(venue), "failures": differences["failures"], "max": differences["stage"]["max"]}
    check(f"{venue.name} round trip", status == {"FINISHED"} and not differences["failures"] and validator.validate(copy).ok,
          differences["failures"] or [(i.severity, i.message) for i in opened.chstage.issues][:5])

bpy.ops.preferences.addon_disable(module=module)
check("disable", module not in bpy.context.preferences.addons and not hasattr(bpy.types.Scene, "chstage"))

(output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
failed = [c["check"] for c in summary["checks"] if not c["ok"]]
print(f"CHSTAGE-TEST RESULT {'FAIL ' + ', '.join(failed) if failed else 'PASS'} ({len(summary['checks'])} checks, Blender {bpy.app.version_string})")
if failed: sys.exit(1)
