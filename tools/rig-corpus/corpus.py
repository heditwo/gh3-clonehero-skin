"""Rig corpus run (docs/BLENDER-ADDON.md section 2.13): each model of corpus.json goes through the add-on flow headless
(New Character with the model's skeleton, import, Set Up Skeleton, Check Clips, Validate, optional Export and render),
and the automatic map is scored against the hand-made map of the model.

Run with tools/rig-corpus/run.sh (it installs the add-on into a temporary Blender user folder). Inside Blender:

blender --background --python tools/rig-corpus/corpus.py -- --models DIR --out DIR --library GH3_LIBRARY --characters CHAR_LIBRARY
    [--only id,id] [--export] [--render] [--draft]

--draft writes the automatic map of each model as a draft of its hand-made map (OUT/draft.json).
"""

import argparse
import json
import sys
import time
import traceback
from pathlib import Path

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Vector

HERE = Path(__file__).resolve().parent
args = argparse.ArgumentParser()
args.add_argument("--models", required=True)
args.add_argument("--out", required=True)
args.add_argument("--library", required=True)
args.add_argument("--characters", required=True)
args.add_argument("--only", default="")
args.add_argument("--export", action="store_true")
args.add_argument("--render", action="store_true")
args.add_argument("--draft", action="store_true")
args.add_argument("--save", action="store_true", help="save each scene as OUT/blend/<id>.blend")
args.add_argument("--check", action="store_true", help="exit 1 when a model without 'expect' has a wrong or missing role, an error, or fails")
args.add_argument("--corpus", default=str(HERE / "corpus.json"))
options = args.parse_args(sys.argv[sys.argv.index("--") + 1:])
MODELS, OUT = Path(options.models).expanduser(), Path(options.out)
OUT.mkdir(parents=True, exist_ok=True)
ADDON = "bl_ext.user_default.chstage_tools"
prefs = bpy.context.preferences.addons[ADDON].preferences
prefs.library, prefs.character_library = options.library, options.characters
skeleton_tools = sys.modules[ADDON + ".skeleton_tools"]
character_tools = sys.modules[ADDON + ".character_tools"]
character_package = sys.modules[ADDON + ".character_package"]
skeleton = sys.modules[ADDON + ".vendor.package_format.skeleton"]
corpus = json.loads(Path(options.corpus).read_text())


def log(*items):
    print("CORPUS", *items, flush=True)


# Import.
def import_file(path, collection, scene):
    before = set(bpy.data.objects)
    suffix = path.suffix.lower()
    view_layer = scene.view_layers[0]
    view_layer.active_layer_collection = next(c for c in view_layer.layer_collection.children if c.collection == collection)
    with bpy.context.temp_override(scene=scene, view_layer=view_layer):
        if suffix in (".glb", ".gltf", ".vrm"): bpy.ops.import_scene.gltf(filepath=str(path))
        elif suffix == ".fbx": bpy.ops.import_scene.fbx(filepath=str(path))
        elif suffix == ".pmx": bpy.ops.mmd_tools.import_model(filepath=str(path), scale=0.08, types={"MESH", "ARMATURE", "MORPHS"}, clean_model=True)
        else: raise ValueError(f"no importer for {path.name}")
    made = [o for o in bpy.data.objects if o not in before]
    for obj in [o for o in made if o.type == "MESH" and o.name.startswith("Icosphere") and not o.modifiers and not o.vertex_groups]:
        made.remove(obj)
        bpy.data.objects.remove(obj, do_unlink=True)
    for obj in made:
        if obj.name not in collection.objects:
            for other in list(obj.users_collection): other.objects.unlink(obj)
            collection.objects.link(obj)
    return made


def rigify(collection, scene):
    """A Rigify human rig (generated from the metarig) with a body of boxes: each deform bone moves its box."""
    import addon_utils
    addon_utils.enable("rigify", default_set=True)
    view_layer = scene.view_layers[0]
    view_layer.active_layer_collection = next(c for c in view_layer.layer_collection.children if c.collection == collection)
    with bpy.context.temp_override(scene=scene, view_layer=view_layer):
        bpy.ops.object.armature_human_metarig_add()
        meta = bpy.context.view_layer.objects.active
        bpy.ops.pose.rigify_generate()
    rig = next(o for o in bpy.data.objects if o.type == "ARMATURE" and o != meta and o.name not in ("metarig",) and o.data.bones.get("DEF-spine"))
    bpy.data.objects.remove(meta)
    mesh = bpy.data.meshes.new("RigifyBody")
    bm = bmesh.new()
    groups = []
    for bone in rig.data.bones:
        if not bone.use_deform: continue
        head, tail = np.array(rig.matrix_world @ bone.head_local), np.array(rig.matrix_world @ bone.tail_local)
        size = max(0.008, 0.2 * np.linalg.norm(tail - head))
        start = len(bm.verts)
        for end in (head, tail):
            for dx, dy in ((-1, -1), (1, -1), (1, 1), (-1, 1)): bm.verts.new(end + np.array([dx * size, dy * size, 0]))
        bm.verts.ensure_lookup_table()
        v = bm.verts[start:start + 8]
        for face in ((0, 1, 2, 3), (4, 7, 6, 5), (0, 4, 5, 1), (1, 5, 6, 2), (2, 6, 7, 3), (3, 7, 4, 0)):
            bm.faces.new([v[i] for i in face])
        groups.append((bone.name, list(range(start, start + 8))))
    bm.to_mesh(mesh)
    body = bpy.data.objects.new("RigifyBody", mesh)
    collection.objects.link(body)
    for name, ids in groups: body.vertex_groups.new(name=name).add(ids, 1.0, "REPLACE")
    body.parent = rig
    body.modifiers.new("Armature", "ARMATURE").object = rig
    return [rig, body]


# Synthetic variants: renames, transforms, mirrored exports, rest poses, and missing or extra bones.
def deform_names(rig):
    return [b.name for b in rig.data.bones if b.use_deform]


def merge_weights(meshes, source, target):
    for obj in meshes:
        a, b = obj.vertex_groups.get(source), obj.vertex_groups.get(target)
        if a is None: continue
        if b is None: b = obj.vertex_groups.new(name=target)
        for vertex in obj.data.vertices:
            for element in vertex.groups:
                if element.group == a.index and element.weight > 0: b.add([vertex.index], element.weight, "ADD")
        obj.vertex_groups.remove(a)


def edit(rig, scene):
    bpy.context.view_layer.objects.active = rig
    for obj in scene.objects: obj.select_set(obj == rig)
    bpy.ops.object.mode_set(mode="EDIT")
    return rig.data.edit_bones


def delete_bones(rig, scene, meshes, names, keep_weights_on):
    """Delete bones (their children go to the parent) and add their weights to another bone."""
    for name, target in zip(names, keep_weights_on): merge_weights(meshes, name, target)
    bones = edit(rig, scene)
    for name in names:
        if name not in bones: continue
        bone = bones[name]
        for child in list(bone.children): child.parent = bone.parent
        bones.remove(bone)
    bpy.ops.object.mode_set(mode="OBJECT")


def apply_ops(model, rig, meshes, scene, truth):
    """Change the base model as the variant says and return its hand-made map."""
    truth = dict(truth)
    for op in model.get("ops", []):
        kind = op["op"]
        if kind == "drop_meshes":
            for obj in list(meshes):
                if any(part in obj.name for part in op["names"]):
                    meshes.remove(obj)
                    bpy.data.objects.remove(obj, do_unlink=True)
        elif kind == "fingers":
            drop = [r for r in list(truth) for f in op["drop"] if f in r and ("Thumb" in r or "Proximal" in r or "Intermediate" in r or "Distal" in r)]
            names = [truth.pop(r) for r in sorted(drop, key=lambda r: ("Distal" in r, "Intermediate" in r), reverse=True)]
            hands = [truth["leftHand"] if n.lower().find("left") >= 0 or n.endswith((".l", ".L", "_l")) else truth["rightHand"] for n in names]
            # Bones below the dropped finger bones (tips) go too.
            extra = [b.name for n in names if n in rig.data.bones for b in rig.data.bones[n].children_recursive if b.name not in names]
            delete_bones(rig, scene, meshes, names + extra, hands + [hands[0]] * len(extra))
        elif kind == "drop_neck":
            neck = truth.pop("neck")
            delete_bones(rig, scene, meshes, [neck], [truth["head"]])
        elif kind == "rename":
            table = op.get("table", {})
            names = deform_names(rig)
            order = []
            stack = [b for b in rig.data.bones if b.parent is None]
            while stack:
                bone = stack.pop(0)
                order.append(bone.name)
                stack = list(bone.children) + stack
            renames = {}
            if op.get("style") == "joints":
                renames = {name: f"joint{k + 1}" for k, name in enumerate(order)}
            else:
                by_role = {bone: role for role, bone in truth.items()}
                for k, name in enumerate(order):
                    role = by_role.get(name)
                    renames[name] = table.get(role, f"{op.get('other', 'bone')}{k:03d}") if role else f"{op.get('other', 'bone')}{k:03d}"
            for name, new in renames.items(): rig.data.bones[name].name = new + "\u0001"
            for bone in rig.data.bones: bone.name = bone.name.rstrip("\u0001")
            for obj in meshes:
                for group in obj.vertex_groups:
                    if group.name in renames: group.name = renames[group.name]
            truth = {role: renames.get(bone, bone) for role, bone in truth.items()}
        elif kind == "object":
            matrix = Matrix.LocRotScale(None, Matrix.Rotation(np.radians(op.get("rotate", [0, 0, 0])[0]), 4, "X").to_quaternion()
                                        @ Matrix.Rotation(np.radians(op.get("rotate", [0, 0, 0])[2]), 4, "Z").to_quaternion(),
                                        [op.get("scale", 1.0)] * 3)
            for obj in meshes:
                world = obj.matrix_world.copy()
                obj.parent = rig
                obj.matrix_world = world
            rig.matrix_world = matrix @ rig.matrix_world
        elif kind == "mirror":
            skeleton_tools._apply(rig, meshes, np.diag([-1.0, 1, 1, 1]))
        elif kind == "relax_arms":
            bpy.context.view_layer.objects.active = rig
            if rig.animation_data is not None: rig.animation_data.action = None
            for pose_bone in rig.pose.bones:
                pose_bone.location, pose_bone.scale = (0, 0, 0), (1, 1, 1)
                pose_bone.rotation_quaternion, pose_bone.rotation_euler = (1, 0, 0, 0), (0, 0, 0)
            bpy.context.view_layer.update()
            front = rig.matrix_world.to_3x3().inverted() @ Vector((0, 1.0, 0))
            for side, sign in (("left", 1), ("right", -1)):
                bone = rig.pose.bones[truth[side + "UpperArm"]]
                head = bone.head.copy()
                turn = Matrix.Translation(head) @ Matrix.Rotation(np.radians(op.get("degrees", 70)) * sign, 4, front.normalized()) @ Matrix.Translation(-head)
                bone.matrix = turn @ bone.matrix
                bpy.context.view_layer.update()
            bpy.context.view_layer.update()
            for obj in meshes:
                modifier = next(m for m in obj.modifiers if m.type == "ARMATURE")
                with bpy.context.temp_override(object=obj, active_object=obj):
                    bpy.ops.object.modifier_apply(modifier=modifier.name)
                obj.modifiers.new("Armature", "ARMATURE").object = rig
            for obj in scene.objects: obj.select_set(obj == rig)
            bpy.context.view_layer.objects.active = rig
            bpy.ops.object.mode_set(mode="POSE")
            bpy.ops.pose.armature_apply(selected=False)
            bpy.ops.object.mode_set(mode="OBJECT")
        elif kind == "split_spine":
            bones = edit(rig, scene)
            order = ["hips", "spine", "chest", "upperChest", "neck", "head"]
            for role in op.get("roles", ["spine", "chest"]):
                if role not in truth: continue
                above = next((truth[r] for r in order[order.index(role) + 1:] if r in truth), None)
                if above is not None: bones[truth[role]].tail = bones[above].head
                for bone in bones: bone.select = bone.select_head = bone.select_tail = False
                bones[truth[role]].select = bones[truth[role]].select_head = bones[truth[role]].select_tail = True
                bpy.ops.armature.subdivide(number_cuts=1)
            bpy.ops.object.mode_set(mode="OBJECT")
        elif kind == "helpers":
            bones = edit(rig, scene)
            for side in ("left", "right"):
                hand, foot, upper = bones[truth[side + "Hand"]], bones[truth[side + "Foot"]], bones[truth[side + "UpperArm"]]
                h = foot.head
                for name, head, tail, parent, deform in ((f"IK_hand_{side}", tuple(hand.head), tuple(hand.tail), None, False),
                                                         (f"ctrl_foot_{side}", tuple(foot.head), tuple(foot.tail), None, True),
                                                         (f"pole_knee_{side}", (h.x, h.y - 40 * op.get("unit", 0.01), h.z + 50 * op.get("unit", 0.01)),
                                                          (h.x, h.y - 40 * op.get("unit", 0.01), h.z + 60 * op.get("unit", 0.01)), None, False),
                                                         (f"{side}_upperarm_twist_01", tuple(upper.head.lerp(upper.tail, 0.5)), tuple(upper.tail), upper.name, True)):
                    bone = bones.new(name)
                    bone.head, bone.tail = tuple(head), tuple(tail)
                    if parent: bone.parent = bones[parent]
                    bone.use_deform = deform
            prop = bones.new("weapon_prop")
            hand = bones[truth["leftHand"]]
            prop.head, prop.tail = tuple(hand.head), tuple(hand.head + (bones[truth["leftHand"]].head - bones[truth["leftLowerArm"]].head) * 0.5)
            prop.parent = hand
            bpy.ops.object.mode_set(mode="OBJECT")
            mesh = bpy.data.meshes.new("Prop")
            bm = bmesh.new()
            bmesh.ops.create_cube(bm, size=0.04)
            bm.to_mesh(mesh)
            obj = bpy.data.objects.new("Prop", mesh)
            meshes[0].users_collection[0].objects.link(obj)
            obj.location = rig.data.bones["weapon_prop"].tail_local
            obj.vertex_groups.new(name="weapon_prop").add(list(range(len(mesh.vertices))), 1.0, "REPLACE")
            obj.parent = rig
            obj.modifiers.new("Armature", "ARMATURE").object = rig
            meshes.append(obj)
    return truth


def _renamed(alternatives, old, new):
    """The alternatives of a base model with the bone names of a variant (by role)."""
    names = {old[r]: new[r] for r in old if r in new}
    return {role: [names.get(b, b) for b in bones] for role, bones in alternatives.items()}


def png_images(scene, folder):
    """Images that are not PNG become PNG files (what a creator does for the validator: packages use PNG only)."""
    folder.mkdir(parents=True, exist_ok=True)
    done = {}
    for obj in character_tools.character_meshes(scene):
        for slot in obj.material_slots:
            material = slot.material
            if material is None or not material.use_nodes: continue
            for node in material.node_tree.nodes:
                image = getattr(node, "image", None)
                if node.type != "TEX_IMAGE" or image is None or image.file_format == "PNG": continue
                if image.name not in done:
                    width, height = image.size
                    if not image.has_data or width == 0:
                        image.reload()
                        width, height = image.size
                    valid = bool(width and height and image.has_data)
                    made = bpy.data.images.new(f"{image.name}.png", width if valid else 4, height if valid else 4, alpha=True)
                    if valid:
                        pixels = np.empty(width * height * 4, dtype=np.float32)
                        image.pixels.foreach_get(pixels)
                        made.pixels.foreach_set(pixels)
                    else:
                        made.pixels.foreach_set(np.full(4 * 4 * 4, 0.6, dtype=np.float32))
                    path = folder / f"{len(done):03d}.png"
                    made.filepath_raw, made.file_format = str(path), "PNG"
                    made.save()
                    made.source = "FILE"
                    made.filepath = str(path)
                    made.reload()
                    made.colorspace_settings.name = image.colorspace_settings.name
                    made.pack()
                    done[image.name] = made
                node.image = done[image.name]
    return len(done)


def score(found, truth, alternatives):
    """Hand-made map against the automatic map: (correct, wrong roles, missing roles, extra roles)."""
    correct, wrong, missing = 0, [], []
    for role, bone in truth.items():
        got = found.get(role)
        if got == bone or got in alternatives.get(role, []): correct += 1
        elif got is None: missing.append(role)
        else: wrong.append(f"{role}: {got} (not {bone})")
    extra = [f"{role}: {bone}" for role, bone in found.items() if role not in truth and bone not in truth.values()]
    return correct, wrong, missing, extra


def render(scene, path, clip_frame):
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.render.resolution_x, scene.render.resolution_y = 480, 640
    scene.render.film_transparent = False
    scene.frame_set(clip_frame)
    camera = scene.camera
    if camera is not None:
        camera.location = (0.0, -4.0, 1.0)
        camera.rotation_euler = (np.radians(90), 0, 0)
        camera.data.lens = 45
    scene.render.filepath = str(path)
    bpy.ops.render.render(write_still=True)


def run(model):
    started = time.time()
    bpy.ops.wm.read_homefile(use_empty=True)
    profile = model.get("profile", "gh3-guitarist-midori-v1")
    result = bpy.ops.chstage.new_character(profile=profile, library=options.library, character_library=options.characters, own_skeleton=True)
    scene = next(s for s in bpy.data.scenes if s.chstage.character.enabled)
    for other in [s for s in bpy.data.scenes if s != scene]: bpy.data.scenes.remove(other)
    if bpy.context.window is not None: bpy.context.window.scene = scene
    p = scene.chstage.character
    collection = next(c for c in scene.collection.children if c.name.startswith("Character") and "Helpers" not in c.name)
    made = []
    for name in model["files"]:
        made += rigify(collection, scene) if name == "RIGIFY" else import_file(MODELS / name, collection, scene)
    rigs = [o for o in made if o.type == "ARMATURE"]
    rigs.sort(key=lambda r: -len(r.data.bones))
    view_layer = scene.view_layers[0]
    if len(rigs) > 1 and model.get("join"):
        for obj in scene.objects: obj.select_set(obj in rigs)
        view_layer.objects.active = rigs[0]
        with bpy.context.temp_override(scene=scene, view_layer=view_layer, active_object=rigs[0], object=rigs[0], selected_objects=rigs):
            log(model["id"], "join", bpy.ops.chstage.join_armatures())
    rig = rigs[0]
    meshes = skeleton_tools.model_meshes(scene, rig)
    base = next((m for m in corpus["models"] if m["id"] == model.get("base")), {})
    truth = apply_ops(model, rig, meshes, scene, model.get("truth") or base.get("truth", {}))
    for obj in scene.objects: obj.select_set(obj == rig)
    view_layer.objects.active = rig
    p.target_height = model.get("height", 0.0)
    with bpy.context.temp_override(scene=scene, view_layer=view_layer, active_object=rig, object=rig, selected_objects=[rig]):
        status = bpy.ops.chstage.use_skeleton()
    found = skeleton_tools.humanoid(scene)
    confidence = {row.role: round(row.confidence, 2) for row in p.roles if row.bone}
    row = {"id": model["id"], "kind": model.get("kind", ""), "status": list(status), "notes": skeleton_tools.notes(scene), "roles": len(found)}
    if options.draft:
        row["draft"] = found
        row["confidence"] = confidence
    alternatives = model["alternatives"] if "alternatives" in model else _renamed(base.get("alternatives", {}), base.get("truth", {}), truth)
    correct, wrong, missing, extra = score(found, truth, alternatives)
    row.update(truthRoles=len(truth), correct=correct, wrong=wrong, missing=missing, extra=extra, fixes=len(wrong) + len(missing),
               accuracy=round(correct / max(1, len(truth)), 4), required=[r for r in skeleton.REQUIRED if r not in found],
               unsure=sorted(r for r, c in confidence.items() if c < skeleton_tools.SURE))
    if truth and (wrong or missing):
        # The manual fixes: the hand-made map in the editor (what a creator does after the guided flow).
        rows = {r.role: r for r in p.roles}
        for role in list(found):
            if role not in truth and role in rows: rows[role].bone = ""
        for role, bone in truth.items():
            if role in rows: rows[role].bone, rows[role].confidence = bone, 1.0
    for obj in skeleton_tools.model_meshes(scene, rig): character_tools.limit_and_normalize(obj, rig)
    row["pngImages"] = png_images(scene, OUT / "textures" / model["id"])
    height = float(np.ptp(skeleton_tools.mesh_points(skeleton_tools.model_meshes(scene, rig), 20000)[:, 2]))
    row["height"] = round(height, 3)
    if all(r in skeleton_tools.humanoid(scene) for r in skeleton.REQUIRED):
        try:
            findings = skeleton_tools.run_checks(scene)
            row["checks"] = [f"{f['severity']} {f['kind']} {f['role']}: {f['message']}" for f in findings]
        except Exception as error:
            row["checks"] = [f"check failed: {error}"]
            traceback.print_exc()
    issues = character_package.validate_scene(scene)
    row["errors"] = [m for s, m, *_ in issues if s == "ERROR"]
    row["warnings"] = [m for s, m, *_ in issues if s != "ERROR"]
    if options.render and not row["errors"]:
        for helper in (p.attachment, p.source_rig):
            if helper is not None: helper.hide_render = helper == p.source_rig
        clips = skeleton_tools.check_clip_ids(scene, 2)
        for k, (clip, label) in enumerate(clips[:2]):
            skeleton_tools.clip_action(scene, clip)
            render(scene, OUT / "renders" / f"{model['id']}-{k}.png", scene.frame_end // 2)
    if options.export and not row["errors"]:
        package = scene.chstage.package
        package.package_id, package.name, package.version = f"corpus.{model['id']}", model.get("name", model["id"]), "1.0.0"
        package.license = model.get("license_id", "LicenseRef-Test")
        package.redistribute, package.modify, package.commercial = False, False, False
        path = OUT / "packages" / f"corpus.{model['id']}.chchar"
        path.parent.mkdir(exist_ok=True)
        with bpy.context.temp_override(scene=scene, view_layer=view_layer):
            row["export"] = list(bpy.ops.chstage.export_character(filepath=str(path)))
        row["package"] = str(path)
    if options.save:
        (OUT / "blend").mkdir(exist_ok=True)
        bpy.ops.wm.save_as_mainfile(filepath=str(OUT / "blend" / f"{model['id']}.blend"))
    row["seconds"] = round(time.time() - started, 1)
    return row


rows = []
only = {x for x in options.only.split(",") if x}
for model in corpus["models"]:
    if only and model["id"] not in only: continue
    if any(name != "RIGIFY" and not (MODELS / name).exists() for name in model["files"]) or \
            (model.get("needs") == "mmd_tools" and not any(m.endswith("mmd_tools") for m in bpy.context.preferences.addons.keys())):
        log(model["id"], "SKIP (no model file)")
        rows.append({"id": model["id"], "skipped": True})
        continue
    try:
        row = run(model)
    except Exception as error:
        traceback.print_exc()
        row = {"id": model["id"], "failed": f"{type(error).__name__}: {error}"}
    rows.append(row)
    log(model["id"], json.dumps({k: v for k, v in row.items() if k not in ("draft", "confidence")}, ensure_ascii=False))
(OUT / "results.json").write_text(json.dumps(rows, ensure_ascii=False, indent=1))
if options.draft:
    (OUT / "draft.json").write_text(json.dumps({r["id"]: r.get("draft", {}) for r in rows if "draft" in r}, ensure_ascii=False, indent=1))
scored = [r for r in rows if "accuracy" in r and r.get("truthRoles")]
expected = {m["id"]: m.get("expect") for m in corpus["models"]}
bad = [r["id"] for r in rows if not r.get("skipped") and expected.get(r["id"]) is None
       and ("failed" in r or r.get("fixes") or r.get("required") or r.get("errors") or any(c.startswith("ERROR") for c in r.get("checks", [])))]
log("SUMMARY", f"{len(scored)} models scored, mean accuracy {np.mean([r['accuracy'] for r in scored]) if scored else 0:.3f}, "
    f"zero fixes {sum(1 for r in scored if r['fixes'] == 0)}, failed {sum(1 for r in rows if 'failed' in r)}, skipped {sum(1 for r in rows if r.get('skipped'))}"
    + (f", not as expected: {', '.join(bad)}" if bad else ""))
if options.check and bad: sys.exit(1)
