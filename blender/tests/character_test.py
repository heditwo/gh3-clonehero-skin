"""Headless test of the character tools. Run inside Blender (see blender/test.sh):

blender --background --factory-startup --python-exit-code 1 --python blender/tests/character_test.py -- ZIP OUTPUT LIBRARY

1. Round trip of the four GH3 band members of the library as .chchar packages (pipeline.package.character):
   Open .chchar, Export .chchar without edits, compare (PACKAGING-PLAN 9.6 tolerances), validator, reproducible bytes.
2. New Character from GH3 Rig (guitarist): rig, helpers, hair chains, clips.
3. A test character that is not GH3 data: a body made of Blender primitives at the rig bones, with a generated texture
   and hair cards. Transfer Weights, Assign to Hair Bones, Validate, Export (zip, folder, Custom/Characters), Open
   and export again.
4. Clip preview: the action of a q48 clip against the poses that the clip gives (numpy), and against the glTF
   importer animation of the same clip in the library model (body bones).
5. Errors that name the object (no weight, more than four influences, mesh transform).
6. EEVEE renders of the test character in two GH3 clips.
"""

import faulthandler
import json
import math
import sys
import time
import zipfile
from pathlib import Path

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Vector

faulthandler.enable()

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
from pipeline.package import character as character_format, compare  # noqa: E402

args = sys.argv[sys.argv.index("--") + 1:]
archive, output, library = Path(args[0]), Path(args[1]), Path(args[2])
output.mkdir(parents=True, exist_ok=True)
summary = {"blender": bpy.app.version_string, "checks": []}


def check(name, condition, detail=""):
    summary["checks"].append({"check": name, "ok": bool(condition), "detail": str(detail)[:600]})
    print(f"CHSTAGE-TEST {'PASS' if condition else 'FAIL'} {name} {detail}"[:800])


result = bpy.ops.extensions.package_install_files(filepath=str(archive), repo="user_default", enable_on_install=True)
module = "bl_ext.user_default.chstage_tools"
check("install", result == {"FINISHED"} and module in bpy.context.preferences.addons, result)
prefs = bpy.context.preferences.addons[module].preferences
prefs.library = str(library)
character_package = sys.modules[module + ".character_package"]
character_tools = sys.modules[module + ".character_tools"]
validator = sys.modules[module + ".vendor.package_format.validate"]
sidecars = sys.modules[module + ".sidecars"]


def rotation_angle(a, b):
    """Angle in degrees between two rotation matrices (chord form: acos of the trace loses precision near 0)."""
    return math.degrees(2 * math.asin(min(1.0, float(np.linalg.norm(np.asarray(a) - np.asarray(b))) / math.sqrt(8))))


def errors_of(items):
    return [(s, m, o) for s, m, o, _ in items if s == "ERROR"]


def operator_scene(scene, operator, **options):
    with bpy.context.temp_override(scene=scene, view_layer=scene.view_layers[0]):
        return operator(**options)


# 1. Round trip of the GH3 band members.
members = output / "members"
lib = character_format.Library(library)
summary["roundTrip"] = {}
for role in ("guitarist", "bassist", "singer", "drummer"):
    source = character_format.write_member(lib, role, members)
    report = validator.Validator(source, library).run()
    check(f"{role}: library member package is valid", report.ok and not report.warnings, report.errors + report.warnings)
    started = time.time()
    with bpy.context.temp_override(scene=bpy.context.scene):
        status = bpy.ops.chstage.open_character(filepath=str(source))
    scene = next(s for s in bpy.data.scenes if s.chstage.source_package == str(source.resolve()))
    opened = time.time() - started
    target = output / "roundtrip" / source.name
    started = time.time()
    status_export = operator_scene(scene, bpy.ops.chstage.export_character, filepath=str(target))
    exported = time.time() - started
    again = output / "roundtrip-again" / source.name
    operator_scene(scene, bpy.ops.chstage.export_character, filepath=str(again))
    differences = compare.compare_packages(source, target) if target.is_file() else {"failures": ["not written"], "stage": {"max": {}, "nodeTransform": {}}}
    exported_report = validator.Validator(target, library).run() if target.is_file() else None
    row = {"failures": differences["failures"], "max": differences["stage"]["max"], "nodeTransform": differences["stage"]["nodeTransform"],
           "validatorErrors": exported_report.errors if exported_report else None, "validatorWarnings": exported_report.warnings if exported_report else None,
           "sameBytes": again.is_file() and target.is_file() and again.read_bytes() == target.read_bytes(),
           "openSeconds": round(opened, 2), "exportSeconds": round(exported, 2), "sizes": [source.stat().st_size, target.stat().st_size if target.is_file() else 0],
           "helpers": sorted(o.name for o in scene.objects if character_tools.is_helper(o))}
    summary["roundTrip"][role] = row
    check(f"{role}: round trip (open, export, compare)", status == {"FINISHED"} and status_export == {"FINISHED"} and not differences["failures"]
          and exported_report is not None and exported_report.ok and not exported_report.warnings, row)
    check(f"{role}: second export gives the same bytes", row["sameBytes"])

# 2. New Character from GH3 Rig.
with bpy.context.temp_override(scene=bpy.context.scene):
    status = bpy.ops.chstage.new_character(profile="gh3-guitarist-axel-v1", library=str(library))
scene = next(s for s in bpy.data.scenes if s.name.startswith("CH Character (guitarist)"))
props = scene.chstage.character
rig = props.rig
hair_chains = json.loads(props.hair_chains)
check("new character: rig, helpers, clips", status == {"FINISHED"} and rig is not None and len(rig.data.bones) == 125 and props.reference is not None
      and props.attachment is not None and props.reference.chstage.marker == "PROXY" and len(props.clips) == 356 and len(hair_chains) == 5
      and not character_tools.character_meshes(scene), (len(rig.data.bones) if rig else None, len(props.clips), hair_chains))
issues = character_package.validate_scene(scene)
check("new character without a mesh: error names the rig", any("no mesh" in m and o == rig.name for _, m, o in errors_of(issues)), issues)


# 3. A test character from Blender primitives.
def bone_head(name):
    return Vector(rig.data.bones[f"bone-{character_format.gh3_key(name):08x}"].head_local)


def limb(bm, a, b, radius_a, radius_b, segments=12):
    """A closed cone from point a to point b (local Z of the cone goes from a to b)."""
    direction = b - a
    geometry = bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=segments, radius1=radius_a, radius2=radius_b, depth=direction.length)
    rotation = direction.normalized().to_track_quat("Z", "Y").to_matrix().to_4x4()
    bmesh.ops.transform(bm, matrix=Matrix.Translation((a + b) / 2) @ rotation, verts=geometry["verts"])


def ball(bm, center, radius, scale=(1, 1, 1)):
    geometry = bmesh.ops.create_uvsphere(bm, u_segments=16, v_segments=10, radius=radius)
    bmesh.ops.transform(bm, matrix=Matrix.Translation(center) @ Matrix.Diagonal((*scale, 1)), verts=geometry["verts"])


def test_texture(name, size=64):
    """A generated PNG texture: stripes and a gradient (no third-party art)."""
    image = bpy.data.images.new(name, size, size, alpha=True)
    y, x = np.mgrid[0:size, 0:size] / (size - 1)
    stripes = (np.floor(x * 8) % 2) * 0.25
    pixels = np.stack([0.35 + stripes, 0.45 + 0.4 * y, 0.85 - 0.3 * y, np.ones_like(x)], -1)
    image.pixels.foreach_set(pixels.astype(np.float32).reshape(-1))
    image.file_format = "PNG"
    image.pack()
    return image


def textured_material(name, image, alpha=False):
    material = bpy.data.materials.new(name)
    material.use_nodes = True
    tree = material.node_tree
    shader = next(n for n in tree.nodes if n.type == "BSDF_PRINCIPLED")
    texture = tree.nodes.new("ShaderNodeTexImage")
    texture.image = image
    tree.links.new(texture.outputs["Color"], shader.inputs["Base Color"])
    if alpha:
        tree.links.new(texture.outputs["Alpha"], shader.inputs["Alpha"])
        material.chstage.blend = "BLEND"
    return material


bm = bmesh.new()
pelvis, chest, neck, head = (bone_head(n) for n in ("bone_pelvis", "bone_chest", "bone_neck", "bone_head"))
limb(bm, pelvis + Vector((0, 0, -0.05)), neck, 0.17, 0.2, 16)
ball(bm, head + Vector((0, -0.02, 0.1)), 0.13, (0.9, 1.0, 1.15))
limb(bm, neck - Vector((0, 0, 0.05)), head + Vector((0, 0, 0.02)), 0.06, 0.055)
for side in ("l", "r"):
    bicep, forearm, palm = (bone_head(f"bone_{n}_{side}") for n in ("bicep", "forearm", "palm"))
    thigh, knee, ankle, toe = (bone_head(f"bone_{n}_{side}") for n in ("thigh", "knee", "ankle", "toe"))
    collar = bone_head(f"bone_collar_{side}")
    limb(bm, collar, bicep, 0.07, 0.065)
    ball(bm, bicep, 0.065)
    limb(bm, bicep, forearm + (forearm - bicep).normalized() * 0.03, 0.06, 0.05)
    limb(bm, forearm, palm + (palm - forearm).normalized() * 0.03, 0.05, 0.04)
    ball(bm, palm + (palm - forearm).normalized() * 0.06, 0.05, (1.0, 1.0, 1.0))
    limb(bm, thigh, knee + (knee - thigh).normalized() * 0.03, 0.09, 0.07)
    ball(bm, knee, 0.068)
    limb(bm, knee, ankle, 0.065, 0.045)
    limb(bm, ankle, toe + (toe - ankle).normalized() * 0.05, 0.05, 0.04)
mesh = bpy.data.meshes.new("TestBody")
bm.to_mesh(mesh)
bm.free()
mesh.uv_layers.new(name="UVMap")
for loop in mesh.loops:
    co = mesh.vertices[loop.vertex_index].co
    mesh.uv_layers[0].data[loop.index].uv = (math.atan2(co.y, co.x) / (2 * math.pi) + 0.5, co.z / 2)
body = bpy.data.objects.new("TestBody", mesh)
scene.collection.objects.link(body)
body.data.materials.append(textured_material("test_skin", test_texture("test_skin_color")))
# A transform that Transfer Weights applies.
body.location = (0.0, 0.0, 0.0)
body.scale = (1.0, 1.0, 1.0)

hair_mesh = bpy.data.meshes.new("TestHair")
bm = bmesh.new()
for index in range(6):
    angle = math.radians(-75 + index * 30)
    root = head + Vector((0.11 * math.sin(angle), 0.11 * math.cos(angle) * 0.6 + 0.04, 0.12))
    geometry = bmesh.ops.create_grid(bm, x_segments=1, y_segments=3, size=0.5)
    bmesh.ops.transform(bm, matrix=Matrix.Translation(root + Vector((0, 0.06, -0.13))) @ Matrix.Diagonal((0.08, 0.02, 0.28, 1)) @ Matrix.Rotation(math.radians(90), 4, "X"),
                        verts=geometry["verts"])
bm.to_mesh(hair_mesh)
bm.free()
hair_mesh.uv_layers.new(name="UVMap")
hair = bpy.data.objects.new("TestHair", hair_mesh)
scene.collection.objects.link(hair)
hair_material = textured_material("test_hair", test_texture("test_hair_color"), alpha=True)
hair.data.materials.append(hair_material)

view_layer = scene.view_layers[0]
for obj in scene.objects: obj.select_set(False, view_layer=view_layer)
body.select_set(True, view_layer=view_layer)
view_layer.objects.active = body
with bpy.context.temp_override(scene=scene, view_layer=view_layer, selected_objects=[body], active_object=body, object=body):
    status = bpy.ops.chstage.transfer_weights()
names = character_tools.profile_groups(rig)
weights = character_tools.vertex_weights(body, names)
check("transfer weights", status == {"FINISHED"} and body.parent == rig and (weights > 0).sum(1).max() <= 4 and (weights > 0).sum(1).min() >= 1
      and np.abs(weights.sum(1) - 1).max() < 1e-5 and any(m.type == "ARMATURE" and m.object == rig for m in body.modifiers),
      {"vertices": len(weights), "maxInfluences": int((weights > 0).sum(1).max()), "groups": len(body.vertex_groups)})
summary["testCharacter"] = {"bodyVertices": len(body.data.vertices), "bodyTriangles": sum(len(p.vertices) - 2 for p in body.data.polygons),
                            "weightedGroups": int((weights > 0).any(0).sum())}
with bpy.context.temp_override(scene=scene, view_layer=view_layer, object=hair, active_object=hair, selected_objects=[hair]):
    status = bpy.ops.chstage.assign_hair(selected_only=False)
    bpy.ops.chstage.hair_material(action="ADD")
hair_weights = character_tools.vertex_weights(hair, names)
hair_bones = {names.index(n) for chain in hair_chains for n in chain}
check("assign to hair bones", status == {"FINISHED"} and set(np.flatnonzero(hair_weights.any(0))) <= hair_bones and (hair_weights.sum(1) == 1).all()
      and len(set(np.flatnonzero(hair_weights.any(0)))) >= 3 and [m.material for m in props.hair_materials] == [hair_material],
      sorted(names[i] for i in np.flatnonzero(hair_weights.any(0))))

# Toon settings on the hair: a shade texture, an outline, and the hair layer. The EEVEE preview is on during the export.
toon_tools = sys.modules[module + ".toon"]
hair_props = hair_material.chstage
hair_props.shading, hair_props.hair_layer = "toon", "hair"
hair_props.toon_threshold, hair_props.toon_shade_image = 0.4, test_texture("test_hair_shade")
hair_props.use_outline, hair_props.outline_width = True, 0.002
hair.select_set(True, view_layer=view_layer)
view_layer.objects.active = hair
with bpy.context.temp_override(scene=scene, view_layer=view_layer, selected_objects=[hair], active_object=hair, object=hair, material=hair_material):
    preview = bpy.ops.chstage.toon_preview(enable=True, scope="ACTIVE")
    from_mmd = bpy.ops.chstage.toon_from_mmd(scope="ACTIVE")
preview_outputs = [n for n in hair_material.node_tree.nodes if n.type == "OUTPUT_MATERIAL" and n.get(toon_tools.PREVIEW)]
check("toon preview makes an active EEVEE output; Toon from MMD needs MMD data",
      preview == {"FINISHED"} and len(preview_outputs) == 1 and preview_outputs[0].is_active_output and from_mmd == {"CANCELLED"}, (preview, from_mmd))

package = scene.chstage.package
package.package_id, package.name, package.license = "tester.blocky-guitarist", "Blocky Guitarist", "CC0-1.0"
package.authors[0].name = "CH Stage Tools test"
with bpy.context.temp_override(scene=scene, view_layer=view_layer):
    bpy.ops.chstage.validate_character()
issues = [(i.severity, i.message, i.object_name) for i in scene.chstage.issues]
check("test character validates without errors", not any(s == "ERROR" for s, _, _ in issues), issues)
packages = output / "test-character"
custom = output / "Custom" / "Characters"
target = packages / "tester.blocky-guitarist.chchar"
status = operator_scene(scene, bpy.ops.chstage.export_character, filepath=str(target), write_folder=True, copy_to_game=True, custom_folder=str(custom))
report = validator.Validator(target, library).run() if target.is_file() else None
check("export test character (zip, folder, Custom/Characters)", status == {"FINISHED"} and report is not None and report.ok
      and (packages / "unpacked" / target.name / "manifest.json").is_file() and (custom / target.name).read_bytes() == target.read_bytes(),
      (report.errors + report.warnings) if report else [(i.severity, i.message) for i in scene.chstage.issues])
with zipfile.ZipFile(target) as package_zip:
    names_in_zip = package_zip.namelist()
    character_json = json.loads(package_zip.read("character.json"))
    glb = character_format.Glb(package_zip.read("character.glb"))
summary["testCharacter"].update(files=names_in_zip, character=character_json, size=target.stat().st_size, validatorWarnings=report.warnings if report else None)
check("test character package files", names_in_zip[0] == "manifest.json" and {"character.glb", "character.json"} <= set(names_in_zip)
      and character_json == {"version": 1, "library": "gh3-band-v1", "profile": "gh3-guitarist-axel-v1", "hairMaterials": ["test_hair"]}
      and len(glb.list("skins")[0]["joints"]) == 125 and all(i.get("mimeType") == "image/png" for i in glb.list("images")) and not glb.list("animations"),
      (names_in_zip, character_json))
hair_row = next(m for m in glb.list("materials") if m.get("name") == "test_hair")
images = {i.get("name") for i in glb.list("images")}
toon_row = hair_row.get("extras", {}).get("gh3Toon", {})
check("toon extras in the GLB, the glTF base color stays", hair_row["extras"].get("gh3Shading") == "toon" and hair_row["extras"].get("gh3HairLayer") == "hair"
      and toon_row.get("threshold") == 0.4 and toon_row.get("outline", {}).get("width") == 0.002 and toon_row.get("shadeTexture") == "#test_hair_shade"
      and "test_hair_shade" in images and "baseColorTexture" in hair_row.get("pbrMetallicRoughness", {}) and preview_outputs[0].is_active_output, hair_row)
again = output / "test-character-again" / target.name
operator_scene(scene, bpy.ops.chstage.export_character, filepath=str(again))
check("test character export is reproducible", again.is_file() and again.read_bytes() == target.read_bytes())
with bpy.context.temp_override(scene=scene):
    bpy.ops.chstage.open_character(filepath=str(target))
reopened = next(s for s in bpy.data.scenes if s.chstage.source_package == str(target.resolve()))
copy = output / "test-character-reopened" / target.name
operator_scene(reopened, bpy.ops.chstage.export_character, filepath=str(copy))
differences = compare.compare_packages(target, copy) if copy.is_file() else {"failures": ["not written"], "stage": {"max": {}}}
summary["testCharacter"]["reopened"] = {"failures": differences["failures"], "max": differences["stage"]["max"]}
reopened_hair = next(m for m in bpy.data.materials if m.chstage.source_name == "test_hair" and m != hair_material)
check("test character round trip", not differences["failures"] and [m.material.name for m in reopened.chstage.character.hair_materials if m.material][:1] != []
      and reopened_hair.chstage.shading == "toon" and reopened_hair.chstage.toon_shade_image is not None and reopened_hair.chstage.use_outline,
      (differences["failures"], [(i.severity, i.message) for i in reopened.chstage.issues]))

# 4. Clip preview.
clips = {item.clip: item.label for item in props.clips}
start_clip = f"{character_format.gh3_key('gh3_guit_axel_a_fst_idle1'):08x}"
check("clip list labels", clips.get(start_clip) == "gh3_guit_axel_a_fst_idle1" and any(label.startswith("stance_a/jump/") for label in clips.values()),
      list(clips.items())[:5])
started = time.time()
with bpy.context.temp_override(scene=scene, view_layer=view_layer):
    status = bpy.ops.chstage.preview_clip(clip=start_clip)
convert_seconds = time.time() - started
action = rig.animation_data.action
rig_data = character_tools.profile_rig(character_tools.library(str(library)), "guitarist")
decoded = character_tools.q48.decode(character_tools.decompress(lib.clip("guitarist", start_clip)))
translations, quaternions = character_format.clip_local_poses(decoded, rig_data, "guitarist")
world = character_format.world_poses(rig_data, translations, quaternions)
axes = character_tools.AXES
expected = axes @ world @ np.linalg.inv(axes)
frames = decoded[0]["frames"]
worst_position = worst_rotation = 0.0
for frame in range(0, frames, 7):
    scene.frame_set(frame)
    for joint, name in enumerate(rig_data.names):
        pose = np.array(rig.pose.bones[name].matrix)
        target_matrix = expected[frame, joint]
        worst_position = max(worst_position, float(np.linalg.norm(pose[:3, 3] - target_matrix[:3, 3])))
        rest = np.array(rig.data.bones[name].matrix_local)
        correction = np.linalg.inv(axes @ rig_data.world[joint] @ np.linalg.inv(axes)) @ rest
        worst_rotation = max(worst_rotation, rotation_angle((target_matrix @ correction)[:3, :3], pose[:3, :3]))
summary["clipPreview"] = {"clip": start_clip, "frames": frames, "convertSeconds": round(convert_seconds, 2), "fps": scene.render.fps,
                          "actionVsClip": {"positionMm": worst_position * 1000, "rotationDeg": worst_rotation}}
check("clip preview: the action gives the clip poses", status == {"FINISHED"} and action is not None and scene.render.fps == 30
      and scene.frame_end == frames - 1 and worst_position < 1e-5 and worst_rotation < 0.01, summary["clipPreview"])

# The same clip as the glTF importer reads it from the library model (body bones; the model also has hand layers).
reference_scene = bpy.data.scenes.new("clip reference")
data = character_tools.glb_tools.keep_animations(lib.model("guitarist"), ["gh3_guit_axel_a_fst_idle1"])
new, *_ = character_tools.import_glb(reference_scene, data, reference_scene.collection)
reference_rig = next(o for o in new if o.type == "ARMATURE")
reference_action = reference_rig.animation_data.action if reference_rig.animation_data else None
# The library model clip has the older pipeline arm solve and hand layers; the preview has the game IK of arms and legs.
body_bones = ["bone_pelvis", "bone_stomach_lower", "bone_stomach_upper", "bone_chest", "bone_neck", "bone_head"]
worst_position = worst_rotation = 0.0
if reference_action is not None:
    first = int(reference_action.frame_range[0])
    for frame in range(0, frames - 1, 5):
        scene.frame_set(frame)
        reference_scene.frame_set(first + frame)
        for name in body_bones:
            bone = f"bone-{character_format.gh3_key(name):08x}"
            a, b = np.array(rig.pose.bones[bone].matrix), np.array(reference_rig.pose.bones[bone].matrix)
            worst_position = max(worst_position, float(np.linalg.norm(a[:3, 3] - b[:3, 3])))
            worst_rotation = max(worst_rotation, rotation_angle(a[:3, :3], b[:3, :3]))
summary["clipPreview"]["actionVsGltfImporter"] = {"bones": len(body_bones), "positionMm": worst_position * 1000, "rotationDeg": worst_rotation}
check("clip preview agrees with the glTF importer (body bones)", reference_action is not None and worst_position < 5e-4 and worst_rotation < 0.02,
      summary["clipPreview"]["actionVsGltfImporter"])
bpy.data.scenes.remove(reference_scene)

# 5. Errors that name the object.
broken = bpy.data.objects.new("BrokenMesh", bpy.data.meshes.new("BrokenMesh"))
bm = bmesh.new()
bmesh.ops.create_cube(bm, size=0.2)
bm.to_mesh(broken.data)
bm.free()
scene.collection.objects.link(broken)
broken.parent = rig
broken.modifiers.new("Armature", "ARMATURE").object = rig
broken.location = (0.5, 0, 1)
group = broken.vertex_groups.new(name=names[2])
group.add([0, 1, 2], 1.0, "REPLACE")
for index, name in enumerate(names[3:8]): broken.vertex_groups.new(name=name).add([3], 0.2, "REPLACE")
# A toon shader node group, as MMD and VRM importers make: the glTF exporter writes the material without its texture.
toon = bpy.data.materials.new("toon_shader")
toon.use_nodes = True
toon_group = bpy.data.node_groups.new("ToonShader", "ShaderNodeTree")
toon_group.interface.new_socket("Shader", in_out="OUTPUT", socket_type="NodeSocketShader")
group_output = toon_group.nodes.new("NodeGroupOutput")
toon_bsdf = toon_group.nodes.new("ShaderNodeBsdfToon")
toon_group.links.new(toon_bsdf.outputs[0], group_output.inputs[0])
group_node = toon.node_tree.nodes.new("ShaderNodeGroup")
group_node.node_tree = toon_group
toon.node_tree.links.new(group_node.outputs[0], next(n for n in toon.node_tree.nodes if n.type == "OUTPUT_MATERIAL").inputs["Surface"])
broken.data.materials.append(toon)
# A node group around a Principled BSDF: the exporter follows the group, so no warning.
wrapped = toon.copy()
wrapped.name = "wrapped_principled"
wrapped_group = toon_group.copy()
wrapped_group.links.new(wrapped_group.nodes.new("ShaderNodeBsdfPrincipled").outputs[0], next(n for n in wrapped_group.nodes if n.type == "GROUP_OUTPUT").inputs[0])
next(n for n in wrapped.node_tree.nodes if n.type == "GROUP").node_tree = wrapped_group
broken.data.materials.append(wrapped)
issues = character_package.validate_scene(scene)
surface_warnings = [(o, m) for s, m, o, _ in issues if s == "WARNING" and "does not read its surface node" in m]
check("unsupported surface node gives a warning", len(surface_warnings) == 1 and surface_warnings[0][0] == "BrokenMesh" and "toon_shader" in surface_warnings[0][1],
      surface_warnings)
errors = errors_of(issues)
check("errors name the object", any("no weight" in m and o == "BrokenMesh" for _, m, o in errors) and any("more than 4" in m and o == "BrokenMesh" for _, m, o in errors)
      and any("own transform" in m and o == "BrokenMesh" for _, m, o in errors), errors)
character_package.export_package(scene, output / "broken" / "broken.chchar")
check("an invalid character is not written", not (output / "broken" / "broken.chchar").exists())
with bpy.context.temp_override(scene=scene, view_layer=view_layer):
    bpy.ops.chstage.validate_character()
    index = next(i for i, issue in enumerate(scene.chstage.issues) if issue.object_name == "BrokenMesh")
    bpy.ops.chstage.select_issue(index=index)
check("select issue", view_layer.objects.active == broken)
bpy.data.objects.remove(broken, do_unlink=True)

# 6. EEVEE renders of the test character in two GH3 clips (the attachment shows in the render).
renders = output / "renders"
renders.mkdir(exist_ok=True)
engines = [e.identifier for e in bpy.types.RenderSettings.bl_rna.properties["engine"].enum_items]
scene.render.engine = "BLENDER_EEVEE_NEXT" if "BLENDER_EEVEE_NEXT" in engines else "BLENDER_EEVEE"
scene.render.resolution_x, scene.render.resolution_y, scene.render.resolution_percentage = 720, 960, 100
scene.render.image_settings.file_format = "PNG"
world_data = bpy.data.worlds.new("test-world")
world_data.color = (0.25, 0.27, 0.3)
if world_data.use_nodes if hasattr(world_data, "use_nodes") else False:
    background = world_data.node_tree.nodes.get("Background")
    if background is not None: background.inputs["Color"].default_value = (0.25, 0.27, 0.3, 1)
scene.world = world_data
sun = bpy.data.objects.new("test-sun", bpy.data.lights.new("test-sun", "SUN"))
sun.data.energy = 3.5
sun.rotation_euler = (math.radians(50), math.radians(10), math.radians(-30))
scene.collection.objects.link(sun)
props.attachment.hide_render = False
camera = scene.camera
camera.location = (0.9, -3.6, 1.15)
camera.rotation_euler = (math.radians(88), 0, math.radians(14))
jump = next(clip for clip, label in sorted(clips.items(), key=lambda item: item[1]) if label.startswith("stance_a/jump/good/fast"))
summary["renders"] = []
for name, clip, frame_share in (("idle", start_clip, 0.5), ("jump", jump, 0.45)):
    character_tools.clip_action(scene, clip)
    frame = int((scene.frame_end - scene.frame_start) * frame_share)
    scene.frame_set(frame)
    path = renders / f"test-character-{name}.png"
    scene.render.filepath = str(path)
    try:
        with bpy.context.temp_override(scene=scene):
            bpy.ops.render.render(write_still=True, scene=scene.name)
    except RuntimeError as error:
        print("render failed", error)
    summary["renders"].append({"file": str(path), "clip": clip, "label": clips[clip], "frame": frame, "engine": scene.render.engine})
    check(f"render {name}", path.is_file(), path)
# The GH3 body (reference helper) in the first clip: the preview IK puts its hands on the guitar as in the game.
character_tools.clip_action(scene, start_clip)
scene.frame_set(int((scene.frame_end - scene.frame_start) * 0.5))
for obj in character_tools.character_meshes(scene): obj.hide_render = True
props.reference.hide_render = False
props.reference.display_type = "TEXTURED"
path = renders / "gh3-body-idle.png"
scene.render.filepath = str(path)
with bpy.context.temp_override(scene=scene):
    bpy.ops.render.render(write_still=True, scene=scene.name)
summary["renders"].append({"file": str(path), "clip": start_clip, "label": clips[start_clip], "frame": scene.frame_current, "engine": scene.render.engine})
check("render GH3 body", path.is_file(), path)
for obj in character_tools.character_meshes(scene): obj.hide_render = False
props.reference.hide_render = True
character_tools.clear_pose(scene)
check("bind pose after clear", rig.animation_data is None or rig.animation_data.action is None)

# 8. Custom chains and colliders: a twin tail along a strand, fitted body colliders, export, open, MMD physics.
chain_tools = sys.modules[module + ".chain_tools"]
chain_format = sys.modules[module + ".vendor.package_format.chains"]
tail_mesh = bpy.data.meshes.new("TestTwinTail")
bm = bmesh.new()
tail_root, tail_end = head + Vector((0.14, 0.06, 0.12)), head + Vector((0.24, 0.14, -0.62))
limb(bm, tail_root, tail_end, 0.035, 0.012, segments=8)
bmesh.ops.subdivide_edges(bm, edges=[e for e in bm.edges if abs((e.verts[0].co - e.verts[1].co).normalized().dot((tail_end - tail_root).normalized())) > 0.9], cuts=10)
bm.to_mesh(tail_mesh)
bm.free()
tail_mesh.uv_layers.new(name="UVMap")
tail = bpy.data.objects.new("TestTwinTail", tail_mesh)
scene.collection.objects.link(tail)
tail.data.materials.append(hair_material)
for vertex in tail.data.vertices: vertex.select = True
for obj in scene.objects: obj.select_set(False, view_layer=view_layer)
tail.select_set(True, view_layer=view_layer)
view_layer.objects.active = tail
head_bone = f"bone-{character_format.gh3_key('bone_head'):08x}"
with bpy.context.temp_override(scene=scene, view_layer=view_layer, object=tail, active_object=tail, selected_objects=[tail]):
    status = bpy.ops.chstage.chain_from_strand(name="twin_tail_l", joints=4, parent=head_bone, preset="long_hair")
chain_bones = chain_tools.chain_bones(rig, "twin_tail_l")
tail_weights = character_tools.vertex_weights(tail, character_tools.profile_groups(rig))
check("chain along a strand: bones, joints, and weights", status == {"FINISHED"} and len(chain_bones) == 4 and chain_bones[0].parent.name == head_bone
      and all(b.chstage_joint.enabled and abs(b.chstage_joint.cone - 22) < 1e-4 for b in chain_bones) and np.abs(tail_weights.sum(1) - 1).max() < 1e-5
      and tail_weights[:, [character_tools.profile_groups(rig).index(b.name) for b in chain_bones]].sum() > 0.8 * len(tail_weights),
      [(b.name, b.parent.name if b.parent else None) for b in chain_bones])
with bpy.context.temp_override(scene=scene, view_layer=view_layer):
    status = bpy.ops.chstage.fit_colliders(guitar=True)
fitted = chain_tools.colliders(scene)
by_name = {chain_tools.collider_name(o): o for o in fitted}
check("fit body colliders", status == {"FINISHED"} and {"hips", "chest", "thigh_l", "shin_r", "head", "guitar"} <= set(by_name)
      and 0.04 < by_name["thigh_l"].chstage_collider.radius < 0.13 and 0.05 < by_name["head"].chstage_collider.radius < 0.151,
      {n: round(o.chstage_collider.radius, 3) for n, o in by_name.items()})
scene.chstage_chains[0].all_colliders = False
scene.chstage_chains[0].colliders = "chest, head, guitar, missing"
chain_target = output / "test-chains" / "tester.chains.chchar"
package.package_id = "tester.chains"
status = operator_scene(scene, bpy.ops.chstage.export_character, filepath=str(chain_target))
chain_report = validator.Validator(chain_target, library).run() if chain_target.is_file() else None
with zipfile.ZipFile(chain_target) if chain_target.is_file() else zipfile.ZipFile(target) as package_zip:
    chain_json = json.loads(package_zip.read("character.json"))
    chain_glb = package_zip.read("character.glb")
summary["chains"] = {"export": [(i.severity, i.message) for i in scene.chstage.issues], "validator": (chain_report.errors + chain_report.warnings) if chain_report else None,
                     "bones": chain_json.get("bones"), "chains": chain_json.get("chains"), "colliders": len(chain_json.get("colliders", []))}
joints = chain_json.get("chains", [{}])[0].get("joints", [])
check("export chains: valid package with bones, chain, and colliders", status == {"FINISHED"} and chain_report is not None and chain_report.ok
      and [b["name"] for b in chain_json.get("bones", [])] == [b.name for b in chain_bones] and len(joints) == 4
      and chain_json["chains"][0]["colliders"] == ["chest", "head", "guitar"] and len(chain_json.get("colliders", [])) == len(fitted)
      and all(abs(np.linalg.norm(j["direction"]) - 1) < 1e-4 for j in joints) and b"chstage" not in chain_glb,
      {k: summary["chains"][k] for k in ("export", "validator", "colliders")} | {"fitted": len(fitted), "chstage": chain_glb.count(b"chstage"),
                                                                                 "chainColliders": chain_json.get("chains", [{}])[0].get("colliders")})
# The joint direction points to the next joint, in the runtime frame of the bone (glTF z negated).
bind = {b["name"]: b["bind"] for b in chain_json.get("bones", [])}
aligned = [np.linalg.norm(np.array(bind[joints[i + 1]["bone"]][:3]) - np.array(joints[i]["direction"]) * joints[i]["length"]) for i in range(len(joints) - 1)] if len(joints) == 4 else [1]
check("export chains: each joint ends at the next joint", max(aligned) < 1e-4 and not any("does not start" in w for w in (chain_report.warnings if chain_report else [])), aligned)
with bpy.context.temp_override(scene=scene):
    bpy.ops.chstage.open_character(filepath=str(chain_target))
chain_opened = next((s for s in bpy.data.scenes if s.chstage.source_package == str(chain_target.resolve())), None)
chain_copy = output / "test-chains-reopened" / chain_target.name
status = operator_scene(chain_opened, bpy.ops.chstage.export_character, filepath=str(chain_copy)) if chain_opened else None
with zipfile.ZipFile(chain_copy) if chain_copy.is_file() else zipfile.ZipFile(target) as package_zip:
    copy_json = json.loads(package_zip.read("character.json"))


def close(a, b, tolerance=2e-4):
    if isinstance(a, dict): return isinstance(b, dict) and a.keys() == b.keys() and all(close(a[k], b[k], tolerance) for k in a)
    if isinstance(a, list): return isinstance(b, list) and len(a) == len(b) and all(close(x, y, tolerance) for x, y in zip(a, b))
    if isinstance(a, (int, float)) and isinstance(b, (int, float)): return abs(a - b) <= tolerance * max(1, abs(a))
    return a == b


check("open and export chains again", status == {"FINISHED"} and all(close(chain_json.get(k), copy_json.get(k)) for k in ("bones", "colliders", "chains")),
      {"colliders": [len(chain_json.get("colliders", [])), len(copy_json.get("colliders", []))], **{k: copy_json.get(k) for k in ("chains",)}})
# MMD physics: an MMD-like armature (a static root and two dynamic bodies), as MMD Tools gives it.
from types import SimpleNamespace  # noqa: E402
mmd = bpy.data.objects.new("MMDArm", bpy.data.armatures.new("MMDArm"))
scene.collection.objects.link(mmd)
for obj in scene.objects: obj.select_set(False, view_layer=view_layer)
mmd.select_set(True, view_layer=view_layer)
view_layer.objects.active = mmd
with bpy.context.temp_override(scene=scene, view_layer=view_layer, active_object=mmd, object=mmd, selected_objects=[mmd], selected_editable_objects=[mmd]):
    bpy.ops.object.mode_set(mode="EDIT")
    previous = None
    for index, (top, bottom) in enumerate(((0.0, -0.1), (-0.1, -0.35), (-0.35, -0.6))):
        bone = mmd.data.edit_bones.new(f"hair{index + 1}")
        bone.head, bone.tail = head + Vector((-0.15, 0.05, 0.1 + top)), head + Vector((-0.15, 0.05, 0.1 + bottom))
        bone.parent = previous
        previous = bone
    bpy.ops.object.mode_set(mode="OBJECT")
bodies = [SimpleNamespace(name=f"rb{i}", matrix_world=Matrix.Translation(head + Vector((-0.15, 0.05, 0.1 - 0.1 * i))),
                          mmd_rigid=SimpleNamespace(bone=f"hair{i + 1}", type=str(0 if i == 0 else 1), shape="CAPSULE", size=(0.03, 0.2, 0)),
                          rigid_body=SimpleNamespace(mass=0.5, angular_damping=0.9)) for i in range(3)]
limits = SimpleNamespace(limit_ang_x_lower=-0.5, limit_ang_x_upper=0.5, limit_ang_y_lower=-0.1, limit_ang_y_upper=0.2, limit_ang_z_lower=-0.05, limit_ang_z_upper=0.05)
mmd_joints = [SimpleNamespace(name=f"j{i}", matrix_world=Matrix.Identity(4), rigid_body_constraint=SimpleNamespace(object1=bodies[i - 1], object2=bodies[i], **vars(limits)))
              for i in (1, 2)]
made, _ = chain_tools.import_mmd(scene, mmd, bodies, mmd_joints, parent=head_bone)
mmd_bones = [rig.data.bones[made[n]] for n in ("hair2", "hair3") if n in made]
check("chains from MMD physics", len(made) == 2 and len(mmd_bones) == 2 and mmd_bones[0].parent.name == head_bone and mmd_bones[1].parent == mmd_bones[0]
      and all(b.chstage_joint.enabled for b in mmd_bones) and abs(mmd_bones[0].chstage_joint.cone - math.degrees(0.5)) < 1e-3
      and abs(mmd_bones[0].chstage_joint.damping - chain_format.mmd_damping(0.9)) < 1e-4 and abs(mmd_bones[0].chstage_joint.mass - 0.5) < 1e-6,
      {n: (b.parent.name, round(b.chstage_joint.cone, 3), round(b.chstage_joint.planes, 3)) for n, b in zip(("hair2", "hair3"), mmd_bones)})

# 7. A character library profile: Midori of gh3-characters-v1, with the GH3 character packages next to it.
character_library = library.parent / "gh3-characters-v1.chlib"
midori_package = library.parent / "gh3.midori.outfit1.style1.chchar"
if character_library.exists() and midori_package.exists():
    midori = "gh3-guitarist-midori-v1"
    clib = character_format.CharacterLibrary(character_library)
    with bpy.context.temp_override(scene=bpy.context.scene):
        status = bpy.ops.chstage.new_character(profile=midori)
    scene11 = next(s for s in bpy.data.scenes if s.chstage.character.library_profile == midori)
    p11 = scene11.chstage.character
    rig11 = p11.rig
    expected_rig = clib.rig(midori)
    check("character library: new character on a character library profile", status == {"FINISHED"} and rig11 is not None and set(rig11.data.bones.keys()) == set(expected_rig.names)
          and p11.reference is not None and p11.attachment is not None and len(json.loads(p11.hair_chains)) == len(clib.hair_chains(midori))
          and len(p11.clips) > 100 and [r.package_id for r in scene11.chstage.package.requires] == ["gh3-band-v1", "gh3-characters-v1"]
          and scene11.chstage.package.min_mod_version == "",
          (p11.library_id, len(p11.clips), [r.package_id for r in scene11.chstage.package.requires]))
    # Rig joints in Blender against the profile bind of characters.json.
    axes = character_tools.AXES
    worst = max(float(np.linalg.norm(np.array(rig11.data.bones[name].head_local) - (axes @ expected_rig.world[i])[:3, 3]))
                for i, name in enumerate(expected_rig.names))
    check("character library: rig joints at the profile bind pose", worst < 1e-5, worst)
    # A creator mesh: a copy of the GH3 body without its weights.
    body11 = p11.reference.copy()
    body11.data = p11.reference.data.copy()
    for key in list(body11.keys()): del body11[key]
    body11.chstage.marker = "NONE"
    body11.vertex_groups.clear()
    body11.modifiers.clear()
    body11.parent = None
    body11.name = "MidoriEdit"
    body11.display_type = "TEXTURED"
    scene11.collection.objects.link(body11)
    body11.hide_select = False
    view11 = scene11.view_layers[0]
    for obj in scene11.objects: obj.select_set(False, view_layer=view11)
    body11.select_set(True, view_layer=view11)
    view11.objects.active = body11
    with bpy.context.temp_override(scene=scene11, view_layer=view11, selected_objects=[body11], active_object=body11, object=body11):
        status = bpy.ops.chstage.transfer_weights(exclude_hair=False)
    scene11.chstage.package.package_id, scene11.chstage.package.name = "tester.midori-edit", "Midori edit"
    target11 = output / "test-character-library" / "tester.midori-edit.chchar"
    status_export = operator_scene(scene11, bpy.ops.chstage.export_character, filepath=str(target11))
    report11 = validator.Validator(target11, library, character_library=character_library).run() if target11.is_file() else None
    with zipfile.ZipFile(target11) if target11.is_file() else zipfile.ZipFile(midori_package) as package_zip:
        manifest11 = json.loads(package_zip.read("manifest.json"))
        character11 = json.loads(package_zip.read("character.json"))
    summary["characterLibrary"] = {"export": [(i.severity, i.message) for i in scene11.chstage.issues], "validator": (report11.errors + report11.warnings) if report11 else None,
                                   "manifest": {k: manifest11.get(k) for k in ("format", "requires", "minModVersion")}, "character": character11}
    check("character library: transfer weights and export a valid package on a character library profile", status == {"FINISHED"} and status_export == {"FINISHED"} and report11 is not None
          and report11.ok and not report11.warnings and manifest11["format"] == "chchar/1.0" and "minModVersion" not in manifest11
          and character11["library"] == "gh3-characters-v1" and character11["profile"] == midori, summary["characterLibrary"])
    # The clip preview of a character library clip (the clip rig offsets and the game IK).
    clip11 = next(item.clip for item in p11.clips if item.label.startswith("stance_a/idle"))
    with bpy.context.temp_override(scene=scene11, view_layer=view11):
        status = bpy.ops.chstage.preview_clip(clip=clip11)
    decoded = character_tools.q48.decode(character_tools.decompress(clib.clip(clip11)))
    translations, quaternions = character_format.clip_local_poses(decoded, expected_rig, "guitarist", clib.clip_offsets(midori))
    expected = axes @ character_format.world_poses(expected_rig, translations, quaternions) @ np.linalg.inv(axes)
    worst = 0.0
    for frame in range(0, decoded[0]["frames"], 9):
        scene11.frame_set(frame)
        for joint, name in enumerate(expected_rig.names):
            worst = max(worst, float(np.linalg.norm(np.array(rig11.pose.bones[name].matrix)[:3, 3] - expected[frame, joint, :3, 3])))
    check("character library: clip preview gives the profile clip poses", status == {"FINISHED"} and worst < 1e-5, worst)
    character_tools.clear_pose(scene11)
    # Open a GH3 character package on a character library profile and export it without edits.
    with bpy.context.temp_override(scene=bpy.context.scene):
        status = bpy.ops.chstage.open_character(filepath=str(midori_package))
    opened11 = next((s for s in bpy.data.scenes if s.chstage.source_package == str(midori_package.resolve())), None)
    copy11 = output / "roundtrip-character-library" / midori_package.name
    status_export = operator_scene(opened11, bpy.ops.chstage.export_character, filepath=str(copy11)) if opened11 else None
    report11 = validator.Validator(copy11, library, character_library=character_library).run() if copy11.is_file() else None
    # The GH3 character GLBs keep materials that no mesh uses (Blender drops them), and have coincident cards that the
    # attribute pairing of the compare tool matches wrongly. Compare the triangles of each material by their corners.
    def corner_triangles(path):
        with zipfile.ZipFile(path) as package_zip:
            glb = character_format.Glb(package_zip.read("character.glb"))
        materials, result = glb.list("materials"), {}
        for node in glb.list("nodes"):
            for primitive in glb.list("meshes")[node["mesh"]]["primitives"] if "mesh" in node else []:
                corners = glb.accessor(primitive["attributes"]["POSITION"])[glb.accessor(primitive["indices"]).reshape(-1, 3)]
                result.setdefault(materials[primitive["material"]].get("name"), []).append(corners)
        return {name: np.concatenate(parts) for name, parts in result.items()}

    worst, counts_equal = 0.0, False
    if copy11.is_file():
        source_triangles, copy_triangles = corner_triangles(midori_package), corner_triangles(copy11)
        counts_equal = {k: len(v) for k, v in source_triangles.items()} == {k: len(v) for k, v in copy_triangles.items()}
        for name, corners in source_triangles.items() if counts_equal else ():
            other = copy_triangles[name]
            centers = other.mean(1)
            for triangle in corners:
                near = np.argsort(np.linalg.norm(centers - triangle.mean(0), axis=1))[:8]
                worst = max(worst, min(np.abs(np.roll(other[k], r, 0) - triangle).max() for k in near for r in range(3)))
    differences = compare.compare_packages(midori_package, copy11) if copy11.is_file() else {"stage": {}}
    joints = differences["stage"].get("nodeTransform", {})
    check("character library: open a GH3 character package and export it without edits", status == {"FINISHED"} and opened11 is not None
          and opened11.chstage.character.library_profile == midori and status_export == {"FINISHED"} and counts_equal and worst < 1e-5
          and joints.get("position", 1) < 1e-4 and joints.get("rotation", 1) < 0.01 and report11 is not None and report11.ok,
          {"triangleCorners": worst, "joints": joints, "validator": report11.errors if report11 else None})
else:
    print(f"CHSTAGE-TEST SKIP character library: no {character_library.name} or {midori_package.name} next to the library")

# 8. A character on its own skeleton (PACKAGE-FORMAT.md section 9.6): an armature with Mixamo names, other proportions, a
# T-pose, and bone rolls, a primitive body with the shape keys Blink and A; Use Selected Armature, Validate, Export, the
# retargeted clip preview, and Open.
skeleton_tools = sys.modules[module + ".skeleton_tools"]
with bpy.context.temp_override(scene=bpy.context.scene):
    status = bpy.ops.chstage.new_character(profile="gh3-guitarist-axel-v1", library=str(library), own_skeleton=True)
own_scene = next(s for s in bpy.data.scenes if s.chstage.character.own_skeleton)
own = own_scene.chstage.character
check("own skeleton: new character keeps the GH3 rig as the clip source", status == {"FINISHED"} and own.own_skeleton and own.rig is None
      and own.source_rig is not None and len(own.clips) == 356, (own.own_skeleton, own.source_rig))
source = own.source_rig
check("own skeleton: the GH3 rig helper is visible, in front, octahedral, and named", source.visible_get() and own.show_source_rig
      and source.show_in_front and source.data.display_type == "OCTAHEDRAL" and source.name.startswith(character_tools.SOURCE_NAME),
      (source.name, source.visible_get(), source.data.display_type))


def gh3_head(name):
    return Vector(source.data.bones[f"bone-{character_format.gh3_key(name):08x}"].head_local)


SCALE = 0.85
joints = {"mixamorig:Hips": ("bone_pelvis", None), "mixamorig:Spine": ("bone_stomach_lower", "mixamorig:Hips"),
          "mixamorig:Spine1": ("bone_stomach_upper", "mixamorig:Spine"), "mixamorig:Spine2": ("bone_chest", "mixamorig:Spine1"),
          "mixamorig:Neck": ("bone_neck", "mixamorig:Spine2"), "mixamorig:Head": ("bone_head", "mixamorig:Neck")}
for side, s in (("Left", "l"), ("Right", "r")):
    joints.update({f"mixamorig:{side}Shoulder": (f"bone_collar_{s}", "mixamorig:Spine2"), f"mixamorig:{side}Arm": (f"bone_bicep_{s}", f"mixamorig:{side}Shoulder"),
                   f"mixamorig:{side}ForeArm": (f"bone_forearm_{s}", f"mixamorig:{side}Arm"), f"mixamorig:{side}Hand": (f"bone_palm_{s}", f"mixamorig:{side}ForeArm"),
                   f"mixamorig:{side}UpLeg": (f"bone_thigh_{s}", "mixamorig:Hips"), f"mixamorig:{side}Leg": (f"bone_knee_{s}", f"mixamorig:{side}UpLeg"),
                   f"mixamorig:{side}Foot": (f"bone_ankle_{s}", f"mixamorig:{side}Leg"), f"mixamorig:{side}ToeBase": (f"bone_toe_{s}", f"mixamorig:{side}Foot")})
heads = {name: gh3_head(gh3) * SCALE for name, (gh3, _) in joints.items()}
for side in ("Left", "Right"):
    # A T-pose: the arm turns up about the shoulder to the horizontal.
    shoulder = heads[f"mixamorig:{side}Arm"]
    for part in ("ForeArm", "Hand"):
        offset = heads[f"mixamorig:{side}{part}"] - shoulder
        heads[f"mixamorig:{side}{part}"] = shoulder + Vector((math.copysign(offset.length, offset.x), 0, 0))
armature = bpy.data.armatures.new("Mixamo")
model_rig = bpy.data.objects.new("Mixamo Rig", armature)
own_scene.collection.objects.link(model_rig)
own_view = own_scene.view_layers[0]
own_view.objects.active = model_rig
with bpy.context.temp_override(scene=own_scene, view_layer=own_view, active_object=model_rig, object=model_rig, selected_objects=[model_rig]):
    bpy.ops.object.mode_set(mode="EDIT")
    random = np.random.default_rng(7)
    for name, (gh3, parent) in joints.items():
        bone = armature.edit_bones.new(name)
        bone.head = heads[name]
        children = [heads[n] for n, (_, p) in joints.items() if p == name]
        bone.tail = children[0] if len(children) == 1 else heads[name] + Vector((0, 0, 0.08))
        if (bone.tail - bone.head).length < 1e-3: bone.tail = bone.head + Vector((0, 0, 0.08))
        bone.roll = float(random.uniform(-math.pi, math.pi))
    for name, (_, parent) in joints.items():
        if parent: armature.edit_bones[name].parent = armature.edit_bones[parent]
    bpy.ops.object.mode_set(mode="OBJECT")
bm = bmesh.new()
segments = [(n, p) for n, (_, p) in joints.items() if p]
for name, parent in segments: limb(bm, heads[parent], heads[name], 0.05, 0.045, 8)
ball(bm, heads["mixamorig:Head"] + Vector((0, 0, 0.1)), 0.11)
own_mesh = bpy.data.meshes.new("OwnBody")
bm.to_mesh(own_mesh)
bm.free()
own_body = bpy.data.objects.new("OwnBody", own_mesh)
own_scene.collection.objects.link(own_body)
own_body.parent = model_rig
own_body.modifiers.new("Armature", "ARMATURE").object = model_rig
groups = {name: own_body.vertex_groups.new(name=name) for name in joints}
points = {name: (heads[name], heads[name] if not [n for n, (_, p) in joints.items() if p == name] else (heads[name] + armature.bones[name].tail_local) / 2)
          for name in joints}
for vertex in own_mesh.vertices:
    nearest = min(joints, key=lambda n: (vertex.co - points[n][1]).length)
    groups[nearest].add([vertex.index], 1.0, "REPLACE")
own_body.shape_key_add(name="Basis")
for key_name, lift in (("Blink", -0.01), ("A", 0.02)):
    key = own_body.shape_key_add(name=key_name)
    for vertex in own_mesh.vertices:
        if (vertex.co - heads["mixamorig:Head"]).length < 0.25: key.data[vertex.index].co = vertex.co + Vector((0, 0, lift))
own_body.data.materials.append(textured_material("own_skin", test_texture("own_skin_color")))
for obj in own_scene.objects: obj.select_set(obj == model_rig, view_layer=own_view)
own_view.objects.active = model_rig
with bpy.context.temp_override(scene=own_scene, view_layer=own_view, active_object=model_rig, object=model_rig, selected_objects=[model_rig]):
    status = bpy.ops.chstage.use_skeleton()
mapped = skeleton_tools.humanoid(own_scene)
check("own skeleton: Use Selected Armature maps the Mixamo names and the shape keys", status == {"FINISHED"} and own.rig == model_rig
      and mapped.get("hips") == "mixamorig:Hips" and mapped.get("upperChest") == "mixamorig:Spine2" and mapped.get("leftLowerLeg") == "mixamorig:LeftLeg"
      and mapped.get("rightToes") == "mixamorig:RightToeBase" and skeleton_tools.face(own_scene) == {"blink": "Blink", "a": "A"}, (mapped, skeleton_tools.face(own_scene)))
issues = character_package.validate_scene(own_scene)
check("own skeleton: validates without errors", not errors_of(issues), errors_of(issues))
own_package = output / "own" / "tester.own-skeleton.chchar"
own_scene.chstage.package.package_id, own_scene.chstage.package.name = "tester.own-skeleton", "Own skeleton test"
own_scene.chstage.package.preview_mode = scene.chstage.package.preview_mode
status = operator_scene(own_scene, bpy.ops.chstage.export_character, filepath=str(own_package))
own_report = validator.Validator(own_package, library=library).run() if own_package.is_file() else None
own_json, own_glb = {}, None
if own_package.is_file():
    with zipfile.ZipFile(own_package) as package:
        own_json, own_glb = json.loads(package.read("character.json")), character_format.Glb(package.read("character.glb"))
targets = sorted({name for mesh in own_glb.list("meshes") for name in (mesh.get("extras") or {}).get("targetNames", [])}) if own_glb else []
check("own skeleton: export writes the skeleton, the face, and the face morph targets only", status == {"FINISHED"} and own_report is not None and own_report.ok
      and own_json.get("skeleton", {}).get("humanoid", {}).get("hips") == "mixamorig:Hips" and own_json.get("face") == {"blink": "Blink", "a": "A"}
      and targets == ["A", "Blink"] and "bones" not in own_json, (own_report.errors if own_report else None, targets, own_json.get("face")))

# The retargeted clip preview: the GH3 clip on the GH3 rig helper, the retarget on the armature. A spine bone points along
# its GH3 bone (the rest-pose correction), and the instrument scale follows the arm length.
with bpy.context.temp_override(scene=own_scene, view_layer=own_view):
    status = bpy.ops.chstage.preview_clip(clip=start_clip)
worst = 0.0
pairs = (("mixamorig:Spine", "mixamorig:Spine1", "bone_stomach_lower", "bone_stomach_upper"), ("mixamorig:Neck", "mixamorig:Head", "bone_neck", "bone_head"),
         ("mixamorig:LeftShoulder", "mixamorig:LeftArm", "bone_collar_l", "bone_bicep_l"))
moved = []
head_bone = f"bone-{character_format.gh3_key('bone_head'):08x}"
for frame in (0, 10, 20):
    own_scene.frame_set(frame)
    moved.append((Vector(model_rig.pose.bones["mixamorig:Head"].head), Vector(source.pose.bones[head_bone].head)))
    for a, b, ga, gb in pairs:
        ours = Vector(model_rig.pose.bones[b].head) - Vector(model_rig.pose.bones[a].head)
        name_a, name_b = (f"bone-{character_format.gh3_key(n):08x}" for n in (ga, gb))
        theirs = Vector(source.pose.bones[name_b].head) - Vector(source.pose.bones[name_a].head)
        worst = max(worst, math.degrees(ours.angle(theirs)))
check("own skeleton: the retargeted preview follows the GH3 bones (rest-pose correction, T-pose, bone rolls)", status == {"FINISHED"}
      and model_rig.animation_data.action is not None and source.animation_data.action is not None and worst < 0.5
      and min((moved[0][k] - moved[2][k]).length for k in (0, 1)) > 0.005, (worst, [(moved[0][k] - moved[2][k]).length for k in (0, 1)]))


# The GH3 rig helper has the size of the model (hip height ratio, object scale only): the retarget reads the library rig,
# so the armature poses and the instrument place are the same as with a GH3 rig helper at GH3 size, also when it is hidden.
def hip_height(obj, names):
    return sum((obj.matrix_world @ obj.data.bones[n].head_local).z for n in names) / len(names)


def preview_rows(frames=(0, 10, 20)):
    rows = []
    guitar = f"bone-{character_format.gh3_key('bone_guitar_body'):08x}"
    with bpy.context.temp_override(scene=own_scene, view_layer=own_view):
        bpy.ops.chstage.preview_clip(clip=start_clip)
    for frame in frames:
        own_scene.frame_set(frame)
        evaluated = own.attachment.evaluated_get(own_view.depsgraph)
        mesh = evaluated.to_mesh()
        guitar_points = np.array([tuple(evaluated.matrix_world @ v.co) for v in mesh.vertices[:200]])
        evaluated.to_mesh_clear()
        rows.append(([np.array(b.matrix) for b in model_rig.pose.bones], np.array(source.matrix_world @ source.pose.bones[guitar].head), guitar_points))
    return rows


def gh3_size(scene):
    source.matrix_world = Matrix.Identity(4)
    return 1.0


gh3_legs = [f"bone-{character_format.gh3_key(n):08x}" for n in ("bone_thigh_l", "bone_thigh_r")]
model_hips = hip_height(model_rig, ["mixamorig:LeftUpLeg", "mixamorig:RightUpLeg"])
scaled_hips = hip_height(source, gh3_legs)
fitted = preview_rows()
scale = source.matrix_world.to_scale().x
source.hide_set(True, view_layer=own_view)
fit = skeleton_tools.fit_source_rig
skeleton_tools.fit_source_rig = gh3_size
unscaled = preview_rows()
skeleton_tools.fit_source_rig = fit
pose_change = max(np.abs(a - b).max() for f, u in zip(fitted, unscaled) for a, b in zip(f[0], u[0]))
guitar_change = max(max(np.linalg.norm(f[1] - u[1]), np.abs(f[2] - u[2]).max()) for f, u in zip(fitted, unscaled))
check("own skeleton: the GH3 rig helper has the hip height of the model (object scale only)", abs(scaled_hips - model_hips) < 0.01 and abs(scale - SCALE) < 0.05,
      (scaled_hips, model_hips, scale))
check("own skeleton: the preview poses the armature and places the instrument as with the GH3 rig at GH3 size, also when the helper is hidden",
      pose_change < 1e-5 and guitar_change < 1e-4 and not own.show_source_rig and np.abs(unscaled[0][2] - unscaled[2][2]).max() > 0.01,
      (pose_change, guitar_change))
own.show_source_rig = True
check("own skeleton: Show GH3 Rig is the eye of the outliner", source.visible_get(view_layer=own_view) and not source.hide_get(view_layer=own_view))
skeleton_tools.fit_source_rig(own_scene)
character_tools.clear_pose(own_scene)
opened, opened_issues = character_package.open_package(own_package, library=str(library)) if own_package.is_file() else (None, [])
check("own skeleton: Open restores the skeleton, the map, the face, and the GH3 rig helper at the size of the model", opened is not None and opened.chstage.character.own_skeleton
      and skeleton_tools.humanoid(opened) == own_json.get("skeleton", {}).get("humanoid") and skeleton_tools.face(opened) == own_json.get("face")
      and opened.chstage.character.source_rig is not None and opened.chstage.character.source_rig.visible_get()
      and abs(opened.chstage.character.source_rig.matrix_world.to_scale().x - SCALE) < 0.05, [m for s, m, *_ in opened_issues if s == "ERROR"])

# 9. Any humanoid (BLENDER-ADDON.md section 2.13): the same model with bone names that say nothing (joint1, joint2, ...),
# turned to look toward +X, in centimetres (object scale 100), a helper bone without weights. Set Up Skeleton turns and
# scales the model, finds the same roles, Check Clips, the map editor (Assign, Mirror, Clear), and a map preset.
with bpy.context.temp_override(scene=bpy.context.scene):
    status = bpy.ops.chstage.new_character(profile="gh3-guitarist-axel-v1", library=str(library), own_skeleton=True)
any_scene = next(s for s in bpy.data.scenes if s.chstage.character.own_skeleton and s.chstage.character.rig is None)
any_p = any_scene.chstage.character
any_view = any_scene.view_layers[0]
any_rig = model_rig.copy()
any_rig.data = model_rig.data.copy()
any_body = own_body.copy()
any_body.data = own_body.data.copy()
for obj in (any_rig, any_body): any_scene.collection.objects.link(obj)
any_body.parent = any_rig
any_body.modifiers["Armature"].object = any_rig
renames = {bone.name: f"joint{k + 1}" for k, bone in enumerate(any_rig.data.bones)}
for bone in any_rig.data.bones: bone.name = renames[bone.name]
for group in any_body.vertex_groups: group.name = renames.get(group.name, group.name)
with bpy.context.temp_override(scene=any_scene, view_layer=any_view, active_object=any_rig, object=any_rig, selected_objects=[any_rig]):
    any_view.objects.active = any_rig
    bpy.ops.object.mode_set(mode="EDIT")
    helper = any_rig.data.edit_bones.new("ik_target")
    helper.head, helper.tail = (0.6, -0.4, 1.0), (0.6, -0.4, 1.1)
    bpy.ops.object.mode_set(mode="OBJECT")
any_rig.matrix_world = Matrix.Rotation(math.radians(90), 4, "Z") @ Matrix.Scale(100, 4)
any_body.matrix_parent_inverse = Matrix.Identity(4)
any_view.update()
for obj in any_scene.objects: obj.select_set(obj == any_rig, view_layer=any_view)
any_view.objects.active = any_rig
with bpy.context.temp_override(scene=any_scene, view_layer=any_view, active_object=any_rig, object=any_rig, selected_objects=[any_rig]):
    status = bpy.ops.chstage.use_skeleton()
any_map = skeleton_tools.humanoid(any_scene)
expected = {role: renames[bone] for role, bone in mapped.items()}
wrong = {role: (any_map.get(role), bone) for role, bone in expected.items() if any_map.get(role) != bone}
bones = any_rig.data.bones
toe, ankle = bones[renames["mixamorig:LeftToeBase"]].head_local, bones[renames["mixamorig:LeftFoot"]].head_local
check("any humanoid: Set Up Skeleton turns and scales the model and finds the roles without names", status == {"FINISHED"} and not wrong
      and np.abs(np.array(any_rig.matrix_world) - np.eye(4)).max() < 1e-6 and toe.y < ankle.y and bones[renames["mixamorig:LeftArm"]].head_local.x > 0
      and 1.0 < bones[renames["mixamorig:Head"]].head_local.z < 2.0, (wrong, skeleton_tools.notes(any_scene)))
any_hips = any_rig.pose.bones[renames["mixamorig:Hips"]]
any_source = any_p.source_rig
check("any humanoid: after Set Up Skeleton the pose (map overlay) and the GH3 rig helper have the new bone places",
      (Vector(any_hips.head) - any_hips.bone.head_local).length < 1e-4 and any_source.visible_get()
      and abs(hip_height(any_source, gh3_legs) - hip_height(any_rig, [renames["mixamorig:LeftUpLeg"], renames["mixamorig:RightUpLeg"]])) < 0.01,
      (tuple(any_hips.head), tuple(any_hips.bone.head_local)))
rows = {row.role: k for k, row in enumerate(any_p.roles)}
any_p.roles_index = rows["leftHand"]
any_rig.data.bones.active = any_rig.data.bones[renames["mixamorig:LeftHand"]]
for row in any_p.roles:
    if row.role.startswith("right"): row.bone = ""
with bpy.context.temp_override(scene=any_scene, view_layer=any_view):
    clear = bpy.ops.chstage.role_edit(action="CLEAR")
    assign = bpy.ops.chstage.role_edit(action="ASSIGN")
    mirror = bpy.ops.chstage.role_edit(action="LEFT")
restored = skeleton_tools.humanoid(any_scene)
check("any humanoid: Clear, Assign Active Bone, and Mirror Left to Right edit the map", {clear.pop(), assign.pop(), mirror.pop()} == {"FINISHED"}
      and {r: b for r, b in restored.items()} == {r: b for r, b in any_map.items()}, {r: (restored.get(r), b) for r, b in any_map.items() if restored.get(r) != b})
preset = output / "any-map.json"
skeleton_tools.save_preset(any_scene, preset)
for row in any_p.roles: row.bone = ""
done, missing_roles = skeleton_tools.load_preset(any_scene, preset)
check("any humanoid: a map preset saves and loads the map", done == len(any_map) and not missing_roles and skeleton_tools.humanoid(any_scene) == any_map, (done, missing_roles))
findings = skeleton_tools.run_checks(any_scene, limit=3)
check("any humanoid: Check Clips finds no error in the clips", not [f for f in findings if f["severity"] == "ERROR"], findings)
issues = character_package.validate_scene(any_scene)
check("any humanoid: validates without errors", not errors_of(issues), errors_of(issues))

bpy.ops.preferences.addon_disable(module=module)
check("disable", module not in bpy.context.preferences.addons and not hasattr(bpy.types.Scene, "chstage"))
(output / "character-summary.json").write_text(json.dumps(summary, indent=2) + "\n")
failed = [c["check"] for c in summary["checks"] if not c["ok"]]
print(f"CHSTAGE-TEST RESULT {'FAIL ' + ', '.join(failed) if failed else 'PASS'} ({len(summary['checks'])} checks, Blender {bpy.app.version_string})")
if failed: sys.exit(1)
