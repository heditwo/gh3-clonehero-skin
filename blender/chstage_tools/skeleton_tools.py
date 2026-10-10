"""A character that keeps the skeleton of its model (docs/PACKAGE-FORMAT.md section 9.6, docs/BLENDER-ADDON.md section
1.17): Set Up Skeleton prepares any humanoid model (the transforms, the front, mirrored exports, the unit) and finds
the humanoid roles from the hierarchy and the bone positions (pipeline/package/humanoid.py); the map editor, the map
overlay, map presets, the MMD append rotations (followers), the face shape keys, Check Clips, the checks, the
character.json rows, and the clip preview: the GH3 clip plays on the GH3 rig of the profile (a helper) and the
retarget of the game moves the character's armature.

The export writes the deform bones of the armature (the glTF exporter option 'deform bones only') and the face shape
keys only; the GLB joint names are the Blender bone names.
"""

import json
import re

import bpy
import numpy as np
from bpy.props import BoolProperty, EnumProperty, StringProperty
from bpy.types import Operator, Panel, UIList
from bpy_extras.io_utils import ExportHelper, ImportHelper
from mathutils import Matrix

from . import character_tools
from .vendor.package_format import character, humanoid as humanoid_finder, q48, retarget_check, skeleton

CATEGORY = "CH Character"
# Blender world (Z up) to runtime (glTF with z negated): (x, y, z) -> (x, z, y). A reflection; it is its own inverse.
A = np.array([[1., 0, 0], [0, 0, 1], [0, 1, 0]])
FACE_PROPS = {slot: f"face_{slot}" for slot in skeleton.FACE_SLOTS}
PRESET_FORMAT = "chstage-humanoid-map/1"
SURE, CHECK = 0.8, 0.5
# Roles in the order of the map editor: the trunk, then each arm with its fingers, then each leg.
ORDER = (("hips", "spine", "chest", "upperChest", "neck", "head", "jaw", "leftEye", "rightEye")
         + tuple(f"{side}{part}" for side in ("left", "right") for part in
                 ("Shoulder", "UpperArm", "UpperArmTwist", "LowerArm", "LowerArmTwist", "Hand",
                  "ThumbMetacarpal", "ThumbProximal", "ThumbDistal", "IndexProximal", "IndexIntermediate", "IndexDistal",
                  "MiddleProximal", "MiddleIntermediate", "MiddleDistal", "RingProximal", "RingIntermediate", "RingDistal",
                  "LittleProximal", "LittleIntermediate", "LittleDistal"))
         + tuple(f"{side}{part}" for side in ("left", "right") for part in ("UpperLeg", "LowerLeg", "Foot", "Toes")))
# The clips that Check Clips plays (label parts, in order): idle, a stance change, solo, jump, kick, intro, walk.
CHECK_CLIPS = ("stance_a/idle/good/slow", "stance_a/transitions", "stance_b/idle", "stance_a/solo", "stance_a/jump", "stance_a/kick",
               "intro", "cycle_anim", "idle")


def props(scene): return character_tools.props(scene)


def own(scene):
    p = props(scene)
    return p.enabled and p.own_skeleton


def exported_bones(rig):
    """The bones that the export writes (deform bones), and the parent of each one: its nearest deform parent."""
    bones = [b for b in rig.data.bones if b.use_deform]
    names = [b.name for b in bones]
    index = {name: i for i, name in enumerate(names)}
    parents = []
    for bone in bones:
        parent = bone.parent
        while parent is not None and parent.name not in index: parent = parent.parent
        parents.append(index[parent.name] if parent is not None else -1)
    return bones, names, parents


def rest_rows(rig):
    """(names, parents, runtime bind rows, Blender rest matrices) of the exported bones. The rig has no transform."""
    bones, names, parents = exported_bones(rig)
    rest = np.array([np.array(b.matrix_local) for b in bones]).reshape(-1, 4, 4)
    world = np.tile(np.eye(4), (len(bones), 1, 1))
    world[:, :3, :3] = A @ rest[:, :3, :3] @ A
    world[:, :3, 3] = rest[:, :3, 3] @ A.T
    local = np.array([world[i] if p < 0 else np.linalg.inv(world[p]) @ world[i] for i, p in enumerate(parents)]).reshape(-1, 4, 4)
    rotation = local[:, :3, :3] / np.linalg.norm(local[:, :3, :3], axis=1, keepdims=True)
    bind = np.concatenate([local[:, :3, 3], character.matrix_quaternions(rotation)], -1)
    return names, parents, bind, rest


def model_meshes(scene, rig):
    """The meshes of a model: skinned to the armature (Armature modifier) or children of it."""
    return [o for o in scene.objects if o.type == "MESH" and not character_tools.is_helper(o)
            and (o.parent == rig or any(m.type == "ARMATURE" and m.object == rig for m in o.modifiers))]


def weight_counts(meshes):
    """The number of vertices with a weight on each vertex group name, over the meshes."""
    counts = {}
    for obj in meshes:
        groups = {g.index: g.name for g in obj.vertex_groups}
        for vertex in obj.data.vertices:
            for element in vertex.groups:
                if element.weight > 0.01 and element.group in groups:
                    name = groups[element.group]
                    counts[name] = counts.get(name, 0) + 1
    return counts


def weighted_bones(scene):
    rig = props(scene).rig
    return {name for name in weight_counts(character_tools.character_meshes(scene)) if rig is not None and name in rig.data.bones}


def mesh_points(meshes, limit=4000):
    """World positions of a sample of the mesh vertices."""
    parts = []
    for obj in meshes:
        count = len(obj.data.vertices)
        if not count: continue
        co = np.empty(count * 3)
        obj.data.vertices.foreach_get("co", co)
        world = np.array(obj.matrix_world)
        parts.append(co.reshape(-1, 3) @ world[:3, :3].T + world[:3, 3])
    if not parts: return np.zeros((0, 3))
    points = np.concatenate(parts)
    if len(points) > limit: points = points[np.random.default_rng(7).choice(len(points), limit, replace=False)]
    return points


def shape_key_names(scene):
    names = []
    for obj in character_tools.character_meshes(scene):
        if obj.data.shape_keys:
            names += [k.name for k in obj.data.shape_keys.key_blocks[1:] if k.name not in names]
    return names


def mmd_follower_rows(rig):
    """(bone, source, influence) of the MMD append rotations (MMD Tools pose bone mmd_bone)."""
    rows = []
    for pose_bone in rig.pose.bones:
        mmd = getattr(pose_bone, "mmd_bone", None)
        if mmd is None or not getattr(mmd, "has_additional_rotation", False) or not mmd.additional_transform_bone: continue
        rows.append((pose_bone.name, mmd.additional_transform_bone, float(mmd.additional_transform_influence)))
    return rows


def vrm_map(rig):
    """The humanoid map of the VRM Add-on for Blender (VRM 1.0 or 0.x), when the armature has one."""
    extension = getattr(rig.data, "vrm_addon_extension", None)
    if extension is None: return {}
    result = {}
    try:
        for role, bone in extension.vrm1.humanoid.human_bones.items():
            name = getattr(bone, "node", None) and bone.node.bone_name
            if name: result[role] = name
    except AttributeError:
        pass
    if not result:
        try:
            for human in extension.vrm0.humanoid.human_bones:
                if human.node.bone_name: result[human.bone] = human.node.bone_name
        except AttributeError:
            pass
    renames = {"leftThumbIntermediate": "leftThumbProximal", "rightThumbIntermediate": "rightThumbProximal"}
    return {renames.get(role, role): name for role, name in result.items() if renames.get(role, role) in skeleton.ROLES}


def fill_roles(scene, mapping, confidence=None):
    p = props(scene)
    p.roles.clear()
    for role in ORDER:
        row = p.roles.add()
        row.role, row.bone = role, mapping.get(role, "")
        row.confidence = (confidence or {}).get(role, 1.0 if row.bone else 0.0)


def notes(scene):
    try:
        return json.loads(props(scene).map_notes or "[]")
    except ValueError:
        return []


# Finding the humanoid and preparing the model.
def find(scene, rig=None, meshes=None):
    """The humanoid of the armature (pipeline humanoid.find on the exported bones), with bone names."""
    rig = rig or props(scene).rig
    meshes = meshes if meshes is not None else model_meshes(scene, rig)
    bones, names, parents = exported_bones(rig)
    world = rig.matrix_world
    heads = [tuple(world @ b.head_local) for b in bones]
    tails = [tuple(world @ b.tail_local) for b in bones]
    counts = weight_counts(meshes)
    result = humanoid_finder.find(names, parents, heads, tails, [counts.get(n, 0) for n in names], mesh_points(meshes))
    result["names"] = names
    result["map"] = {role: names[bone] for role, bone in result["roles"].items()}
    return result


def _identity(matrix):
    return np.abs(np.array(matrix) - np.eye(4)).max() < 1e-6


def _apply(rig, meshes, matrix):
    """Move the armature bones and the mesh data (with shape keys) by a matrix; the objects keep no transform."""
    matrix = Matrix(np.asarray(matrix).tolist())
    rig.data.transform(matrix)
    for obj in meshes:
        obj.data.transform(matrix, shape_keys=True)
        if matrix.determinant() < 0 and hasattr(obj.data, "flip_normals"): obj.data.flip_normals()
        obj.data.update()


def rigid_parts(scene, rig):
    """Meshes that follow one bone (bone parent, no weights) become skinned to that bone. Returns their names."""
    made = []
    for obj in scene.objects:
        if obj.type != "MESH" or obj.parent != rig or obj.parent_type != "BONE" or not obj.parent_bone: continue
        bone, world = obj.parent_bone, obj.matrix_world.copy()
        obj.parent = None
        obj.matrix_world = world
        group = obj.vertex_groups.get(bone) or obj.vertex_groups.new(name=bone)
        group.add(list(range(len(obj.data.vertices))), 1.0, "REPLACE")
        if not any(m.type == "ARMATURE" and m.object == rig for m in obj.modifiers): obj.modifiers.new("Armature", "ARMATURE").object = rig
        obj.parent = rig
        obj.matrix_world = world
        made.append(obj.name)
    return made


def _bake_transforms(scene, rig):
    """The armature and its meshes without object transforms (the look stays). Returns the meshes."""
    meshes = model_meshes(scene, rig)
    for obj in meshes:
        if obj.data.users > 1: obj.data = obj.data.copy()
    worlds = {obj: obj.matrix_world.copy() for obj in meshes}
    rig_world = rig.matrix_world.copy()
    rig.parent = None
    if not _identity(rig_world): rig.data.transform(rig_world)
    rig.matrix_world = Matrix.Identity(4)
    for obj in meshes:
        if not _identity(worlds[obj]):
            obj.data.transform(worlds[obj], shape_keys=True)
            if worlds[obj].determinant() < 0 and hasattr(obj.data, "flip_normals"): obj.data.flip_normals()
        obj.parent = rig
        obj.parent_type = "OBJECT"
        obj.matrix_parent_inverse = Matrix.Identity(4)
        obj.matrix_basis = Matrix.Identity(4)
    return meshes


def join_armatures(scene, main, others):
    """One armature from several (a body and a head exported apart): bones with the same name are one bone; the other
    armatures move so that their shared bones meet the bones of the main armature (the mean offset); their other
    bones and their meshes go to the main armature. Returns (bones added, shared bone names)."""
    view_layer = scene.view_layers[0]
    if bpy.context.object is not None and bpy.context.object.mode != "OBJECT": bpy.ops.object.mode_set(mode="OBJECT")
    rows, shared_all = [], set()
    for rig in [main] + list(others):
        for pose_bone in rig.pose.bones:
            pose_bone.location, pose_bone.scale = (0, 0, 0), (1, 1, 1)
            pose_bone.rotation_quaternion, pose_bone.rotation_euler = (1, 0, 0, 0), (0, 0, 0)
        _bake_transforms(scene, rig)
    view_layer.update()
    added = 0
    for other in others:
        shared = [b.name for b in other.data.bones if b.name in main.data.bones]
        if not shared: raise ValueError(f"'{other.name}' has no bone with the name of a bone of '{main.name}': rename one shared bone (for example the neck)")
        offset = np.mean([np.array(main.data.bones[n].head_local) - np.array(other.data.bones[n].head_local) for n in shared], axis=0)
        move = Matrix.Translation(offset.tolist())
        meshes = model_meshes(scene, other)
        _apply(other, meshes, move)
        rows = [(b.name, tuple(b.head_local), tuple(b.tail_local), b.parent.name if b.parent else None, b.use_deform,
                 np.array(b.matrix_local)) for b in other.data.bones if b.name not in main.data.bones]
        view_layer.objects.active = main
        main.select_set(True)
        bpy.ops.object.mode_set(mode="EDIT")
        edit = main.data.edit_bones
        for name, head, tail, _, deform, matrix in rows:
            bone = edit.new(name)
            bone.matrix = Matrix(matrix.tolist())
            bone.head, bone.tail = head, tail
            bone.use_deform = deform
        for name, _, _, parent, _, _ in rows:
            if parent is not None and parent in edit: edit[name].parent = edit[parent]
        bpy.ops.object.mode_set(mode="OBJECT")
        for obj in meshes:
            for modifier in obj.modifiers:
                if modifier.type == "ARMATURE" and modifier.object == other: modifier.object = main
            obj.parent = main
            obj.matrix_parent_inverse = Matrix.Identity(4)
            obj.matrix_basis = Matrix.Identity(4)
        added += len(rows)
        shared_all.update(shared)
        bpy.data.objects.remove(other, do_unlink=True)
    view_layer.update()
    return added, sorted(shared_all)


def _on_segment(point, head, tail, tolerance):
    ab = tail - head
    t = float(np.dot(point - head, ab) / max(np.dot(ab, ab), 1e-12))
    return -0.01 <= t <= 1.01 and np.linalg.norm(point - (head + ab * t)) <= tolerance


def deform_parents(rig):
    """Deform bones whose parent is a control bone (Rigify DEF bones under ORG and MCH bones) get as parent the deform
    bone at the place of that control bone, so that the exported skeleton (deform bones only) is one tree. Returns the
    names of the bones that changed."""
    bones = rig.data.bones
    deform = [b for b in bones if b.use_deform]
    size = max(1e-6, float(np.ptp(np.array([b.head_local for b in deform]), axis=0).max())) if deform else 1.0
    plans = {}
    for bone in deform:
        below = {d.name for d in bone.children_recursive} | {bone.name}
        ancestor = bone.parent
        while ancestor is not None and not ancestor.use_deform:
            head, tail = np.array(ancestor.head_local), np.array(ancestor.tail_local)
            twins = [d for d in deform if d.name not in below and _on_segment(np.array(d.head_local), head, tail, 0.003 * size)
                     and _on_segment(np.array(d.tail_local), head, tail, 0.003 * size)]
            if twins:
                plans[bone.name] = min(twins, key=lambda d: np.linalg.norm(np.array(d.tail_local) - tail)).name
                break
            ancestor = ancestor.parent
    if not plans: return []
    view_layer = bpy.context.view_layer
    active = view_layer.objects.active
    view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode="EDIT")
    edit = rig.data.edit_bones
    for name, parent in plans.items(): edit[name].parent = edit[parent]
    bpy.ops.object.mode_set(mode="OBJECT")
    view_layer.objects.active = active
    return sorted(plans)


def model_scale(height, target=0.0):
    """The scale that gives a model of this height a plausible height in metres (target: the height to use)."""
    if height <= 0: return 1.0
    if target > 0: return target / height
    if 0.5 <= height <= 3.0: return 1.0
    for factor in (100.0, 0.01, 10.0, 0.1, 1000.0, 0.001):
        if 1.0 <= height * factor <= 2.2: return factor
    return 1.7 / height


def prepare(scene, rig, mirror="AUTO", height=0.0):
    """Make the model ready for the game: no object transforms (the armature and its meshes keep their look), meshes
    that follow one bone skinned to it, the model upright and looking toward -Y with its left side on +X, a mirrored
    export mirrored back (mirror AUTO: when the bone names say so), the feet on the floor at the origin, and a height
    in metres (height: the height to use, 0: find the unit). Returns (text of each change, the humanoid found)."""
    changes = []
    view_layer = scene.view_layers[0]
    if bpy.context.object is not None and bpy.context.object.mode != "OBJECT": bpy.ops.object.mode_set(mode="OBJECT")
    for name in rigid_parts(scene, rig): changes.append(f"'{name}' followed one bone: it is now skinned to that bone")
    view_layer.update()
    if rig.parent is not None: changes.append(f"the armature is no longer a child of '{rig.parent.name}'")
    if rig.animation_data is not None and rig.animation_data.action is not None: rig.animation_data.action = None
    for pose_bone in rig.pose.bones:
        pose_bone.location, pose_bone.scale = (0, 0, 0), (1, 1, 1)
        pose_bone.rotation_quaternion, pose_bone.rotation_euler = (1, 0, 0, 0), (0, 0, 0)
    moved = not _identity(rig.matrix_world) or any(not _identity(o.matrix_world) for o in model_meshes(scene, rig))
    meshes = _bake_transforms(scene, rig)
    if moved: changes.append("the object transforms (location, rotation, scale) are now in the bones and the meshes")
    repaired = deform_parents(rig)
    if repaired: changes.append(f"{len(repaired)} deform bones had a control bone as parent (for example '{repaired[0]}'): "
                                "they now have the deform bone at its place, so the exported skeleton is one tree")
    view_layer.update()
    found = find(scene, rig, meshes)
    frame = found["frame"]
    if frame is None: return changes, found
    left, forward, up = (np.array(frame[k]) for k in ("left", "forward", "up"))
    turn = np.array([[1.0, 0, 0], [0, -1.0, 0], [0, 0, 1.0]]) @ np.array([left, forward, up])
    if np.abs(turn - np.eye(3)).max() > 1e-6:
        changes.append("the model now stands on +Z and looks toward -Y (" + _axis_text(up) + " was up, " + _axis_text(forward) + " was the front)")
    flip = found["mirrored"] if mirror == "AUTO" else mirror == "ON"
    if flip:
        turn = np.diag([-1.0, 1, 1]) @ turn
        changes.append("the model was mirrored (its 'left' bones were on its right side): it is mirrored back")
    points = mesh_points(meshes, 20000) @ turn.T
    # Quantiles: a small part far away (a prop at the origin of its file) does not change the height.
    low, top = (np.quantile(points[:, 2], [0.002, 0.998]) if len(points) > 50 else (points[:, 2].min(), points[:, 2].max())) if len(points) else (0.0, frame["height"])
    tall = float(top - low)
    scale = model_scale(tall, height)
    if abs(scale - 1) > 1e-4: changes.append(f"the model was {tall:.3g} units tall: it is now {tall * scale:.2f} m tall")
    matrix = np.eye(4)
    matrix[:3, :3] = turn * scale
    hips = found["roles"].get("hips")
    centre = (np.array(rig.matrix_world @ rig.data.bones[found["names"][hips]].head_local) @ turn.T * scale) if hips is not None else np.zeros(3)
    floor = float(low) * scale
    matrix[:3, 3] = [-centre[0], -centre[1], -floor]
    if np.abs(matrix[:3, 3]).max() > 1e-3 * max(tall * scale, 1e-3):
        changes.append("the feet are on the floor and the hips above the origin")
    if not _identity(matrix): _apply(rig, meshes, matrix)
    view_layer.update()
    return changes, find(scene, rig, meshes)


def _axis_text(v):
    k = int(np.argmax(np.abs(v)))
    return ("-" if v[k] < 0 else "+") + "XYZ"[k] if abs(v[k]) > 0.99 else "(" + ", ".join(f"{x:.2f}" for x in v) + ")"


def auto_map(scene):
    """Fill the humanoid map, the followers, and the face shape keys from the armature of the scene. A VRM humanoid
    (VRM Add-on) is used as it is; else the roles come from the skeleton (humanoid.find)."""
    p = props(scene)
    rig = p.rig
    _, names, _ = exported_bones(rig)
    vrm = {role: bone for role, bone in vrm_map(rig).items() if bone in names}
    found = find(scene)
    if all(role in vrm for role in skeleton.REQUIRED):
        mapping, confidence = vrm, {role: 1.0 for role in vrm}
        found["notes"].insert(0, "the roles come from the VRM humanoid of the model")
    else:
        mapping, confidence = found["map"], found["confidence"]
    fill_roles(scene, mapping, confidence)
    rows = skeleton.mmd_followers(mmd_follower_rows(rig), names, mapping)
    bones, _, parents = exported_bones(rig)
    counts = weight_counts(model_meshes(scene, rig))
    rows += humanoid_finder.helper_followers(names, parents, [tuple(rig.matrix_world @ b.head_local) for b in bones],
                                             [counts.get(n, 0) for n in names], mapping, {r["bone"] for r in rows})
    p.followers = json.dumps(rows, ensure_ascii=False)
    face_map = skeleton.auto_face(shape_key_names(scene))
    for slot, prop in FACE_PROPS.items(): setattr(p, prop, face_map.get(slot, ""))
    p.map_notes = json.dumps(found["notes"], ensure_ascii=False)
    p.checks.clear()
    p.checked = False
    return mapping


def use_skeleton(scene, armature, prepare_model=True, mirror="AUTO", height=None):
    """The armature of the model becomes the character's rig (own skeleton): Set Up Skeleton prepares the model and
    maps the roles. Returns the map."""
    p = props(scene)
    if armature is None or armature.type != "ARMATURE": raise ValueError("select the armature of the model")
    if p.source_rig is not None and armature == p.source_rig: raise ValueError("select the armature of the model, not the GH3 rig")
    p.own_skeleton = True
    p.rig = armature
    changes = []
    if prepare_model:
        changes, _ = prepare(scene, armature, mirror, p.target_height if height is None else height)
    mapping = auto_map(scene)
    p.map_notes = json.dumps(changes + notes(scene), ensure_ascii=False)
    return mapping


def humanoid(scene):
    return {row.role: row.bone for row in props(scene).roles if row.bone}


def followers(scene):
    try:
        rows = json.loads(props(scene).followers or "[]")
    except ValueError:
        return []
    names = set(exported_bones(props(scene).rig)[1]) if props(scene).rig else set()
    mapped = set(humanoid(scene).values())
    return [row for row in rows if row["bone"] in names and row["source"] in names and row["bone"] not in mapped]


def face(scene):
    p = props(scene)
    return {slot: getattr(p, prop) for slot, prop in FACE_PROPS.items() if getattr(p, prop)}


def instrument_row(scene):
    p = props(scene)
    row = {}
    if p.instrument_bone: row["bone"] = p.instrument_bone
    offset = np.array(p.instrument_offset)
    if np.abs(offset).max() > 1e-6: row["offset"] = [round(float(v), 5) for v in A @ offset]
    if p.instrument_scale >= 0.5: row["scale"] = round(float(p.instrument_scale), 4)
    return row or None


def skeleton_rows(scene):
    """character.json skeleton and face."""
    row = {"humanoid": humanoid(scene)}
    rows = followers(scene)
    if rows: row["followers"] = rows
    instrument = instrument_row(scene)
    if instrument: row["instrument"] = instrument
    return row, face(scene)


# Map editing.
SIDE_WORDS = (("Left", "Right"), ("left", "right"), ("LEFT", "RIGHT"), ("左", "右"), ("L", "R"), ("l", "r"))


def _side_pattern(word):
    if len(word) == 1 and word.isascii(): return rf"(?:(?<=[._\- :])|^){word}(?=$|[._\- 0-9])"
    return re.escape(word)


def mirror_name(name, names):
    """The bone of the other side by its name (Left/Right, .L/.R, _l/_r, 左/右, ...), or None."""
    for a, b in SIDE_WORDS:
        for x, y in ((a, b), (b, a)):
            other = re.sub(_side_pattern(x), y, name)
            if other != name and other in names: return other
    return None


def mirror_bone(rig, name, names):
    """The bone of the other side: by name, else the deform bone at the mirrored place (x to -x) within 2 cm."""
    by_name = mirror_name(name, names)
    if by_name is not None: return by_name
    head = np.array(rig.data.bones[name].head_local) * [-1, 1, 1]
    best, distance = None, 0.02
    for other in names:
        d = float(np.linalg.norm(np.array(rig.data.bones[other].head_local) - head))
        if d < distance and other != name: best, distance = other, d
    return best


def mirror_roles(scene, source="left"):
    """Copy the roles of one side to the other side. Returns the number of roles set."""
    p = props(scene)
    _, names, _ = exported_bones(p.rig)
    target = "right" if source == "left" else "left"
    rows = {row.role: row for row in p.roles}
    count = 0
    for role, row in rows.items():
        if not role.startswith(source) or not row.bone: continue
        other = mirror_bone(p.rig, row.bone, names)
        mirrored = rows.get(target + role[len(source):])
        if other is None or mirrored is None: continue
        mirrored.bone, mirrored.confidence = other, row.confidence
        count += 1
    return count


def save_preset(scene, path):
    data = {"format": PRESET_FORMAT, "roles": humanoid(scene), "followers": followers(scene), "face": face(scene)}
    instrument = instrument_row(scene)
    if instrument: data["instrument"] = instrument
    with open(path, "w", encoding="utf-8") as file:
        json.dump(data, file, ensure_ascii=False, indent=1)


def load_preset(scene, path):
    """Apply a map preset: the roles whose bones the armature has. Returns (roles set, roles whose bone is missing)."""
    p = props(scene)
    with open(path, encoding="utf-8") as file:
        data = json.load(file)
    if data.get("format") != PRESET_FORMAT: raise ValueError("the file is not a humanoid map preset")
    _, names, _ = exported_bones(p.rig)
    rows = {row.role: row for row in p.roles} if p.roles else {}
    if not rows:
        fill_roles(scene, {})
        rows = {row.role: row for row in p.roles}
    done, missing = 0, []
    for role, bone in data.get("roles", {}).items():
        if role not in rows: continue
        if bone in names: rows[role].bone, rows[role].confidence = bone, 1.0; done += 1
        else: missing.append(role)
    if "followers" in data: p.followers = json.dumps([r for r in data["followers"] if r["bone"] in names and r["source"] in names], ensure_ascii=False)
    for slot, name in data.get("face", {}).items():
        if slot in FACE_PROPS: setattr(p, FACE_PROPS[slot], name)
    return done, missing


def check(scene, issues):
    """The checks of the skeleton (the rules of the game importer) before the export, with what to do."""
    p = props(scene)
    rig = p.rig
    names, parents, bind, _ = rest_rows(rig)
    if len(names) > 255: issues.error(f"the armature has {len(names)} deform bones (limit 255): turn off Deform on helper bones", rig.name)
    mapping = humanoid(scene)
    missing = [r for r in skeleton.REQUIRED if r not in mapping]
    if missing:
        issues.error(f"the humanoid map has no bone for {', '.join(missing)}: in Skeleton, select the role, select the bone in the viewport, "
                     "and click Assign Active Bone (or click Set Up Skeleton)", rig.name)
    errors, warnings, _ = skeleton.check_map(names, parents, bind, mapping, followers(scene), instrument_row(scene))
    for message in errors:
        if "needs the roles" in message: continue
        if "upright" in message or "swapped" in message or "toes are behind" in message: message += ": click Set Up Skeleton to turn the model"
        if "must be a parent" in message: message += ": one of the two roles is on the wrong bone, or the bone has the wrong parent (Edit Mode)"
        issues.error(message, rig.name)
    for message in warnings: issues.warn(message, rig.name)
    for row in p.roles:
        if row.bone and row.confidence < CHECK and row.role in skeleton.REQUIRED:
            issues.warn(f"{row.role}: the automatic map is not sure of '{row.bone}': check it in Skeleton (Show Map), then click Assign Active Bone", rig.name)
    keys = set(shape_key_names(scene))
    for slot, name in face(scene).items():
        if name not in keys: issues.error(f"face {slot}: no character mesh has the shape key '{name}'", rig.name)
    for item in p.checks:
        if item.severity == "ERROR": issues.warn(f"Check Clips: {item.message}. {item.fix}", rig.name)


# Clip preview.
def _basis(rig, names, world_rt):
    """Pose bone locations and quaternions [w, x, y, z] (frames, exported bones) of runtime world matrices of the
    exported bones. A bone that the export does not write keeps its rest pose relative to its parent."""
    exported = {name: i for i, name in enumerate(names)}
    world = np.empty_like(world_rt)
    world[..., :3, :3] = A @ world_rt[..., :3, :3] @ A
    world[..., :3, 3] = world_rt[..., :3, 3] @ A.T
    world[..., 3, :] = [0, 0, 0, 1]
    frames = world.shape[0]
    matrices, basis = {}, np.empty((frames, len(names), 4, 4))
    for bone in sorted(rig.data.bones, key=lambda b: len(b.parent_recursive)):
        rest = np.array(bone.matrix_local)
        parent = bone.parent
        if bone.name in exported: matrices[bone.name] = world[:, exported[bone.name]]
        elif parent is None: matrices[bone.name] = np.broadcast_to(rest, (frames, 4, 4))
        else: matrices[bone.name] = matrices[parent.name] @ np.linalg.inv(np.array(parent.matrix_local)) @ rest
        if bone.name not in exported: continue
        relative = np.array(parent.matrix_local) @ np.linalg.inv(matrices[parent.name]) if parent is not None else np.eye(4)
        basis[:, exported[bone.name]] = np.linalg.inv(rest) @ relative @ matrices[bone.name]
    rotation = basis[..., :3, :3] / np.linalg.norm(basis[..., :3, :3], axis=-2, keepdims=True)
    q = np.roll(character.matrix_quaternions(rotation), 1, axis=-1)
    for frame in range(1, len(q)):
        flip = np.sum(q[frame] * q[frame - 1], -1) < 0
        q[frame, flip] *= -1
    return basis[..., :3, 3], q


MUTED = "chstage_preview_muted"


def mute_constraints(rig):
    """The game has no Blender constraints (MMD IK and append rotation): the preview mutes them on the armature."""
    muted = json.loads(rig.get(MUTED, "[]"))
    for pose_bone in rig.pose.bones:
        for constraint in pose_bone.constraints:
            if not constraint.mute:
                constraint.mute = True
                muted.append([pose_bone.name, constraint.name])
    rig[MUTED] = json.dumps(muted, ensure_ascii=False)


def restore_constraints(rig):
    if rig is None or MUTED not in rig: return
    for bone, name in json.loads(rig[MUTED]):
        pose_bone = rig.pose.bones.get(bone)
        constraint = pose_bone.constraints.get(name) if pose_bone is not None else None
        if constraint is not None: constraint.mute = False
    del rig[MUTED]


def _world(parents, t, q):
    frames, count = t.shape[:2]
    world = np.zeros((frames, count, 4, 4))
    local = np.zeros_like(world)
    local[..., :3, :3] = skeleton.quaternion_matrices(q / np.linalg.norm(q, axis=-1, keepdims=True))
    local[..., :3, 3] = t
    local[..., 3, 3] = 1
    for joint in skeleton._order(parents):
        parent = parents[joint]
        world[:, joint] = local[:, joint] if parent < 0 else world[:, parent] @ local[:, joint]
    return world


def _keys(action, obj, bone_names, locations, rotations, frames):
    keys = np.arange(frames, dtype=np.float64)
    for joint, bone in enumerate(bone_names):
        pose_bone = obj.pose.bones[bone]
        pose_bone.rotation_mode = "QUATERNION"
        for path, values in (("location", locations[:, joint]), ("rotation_quaternion", rotations[:, joint])):
            for axis in range(values.shape[1]):
                curve = character_tools.new_fcurve(action, obj, f'pose.bones["{bone}"].{path}', axis, bone)
                curve.keyframe_points.add(frames)
                curve.keyframe_points.foreach_set("co", np.column_stack([keys, values[:, axis]]).reshape(-1))
                curve.keyframe_points.foreach_set("interpolation", [1] * frames)
                curve.update()


def retarget_for(scene):
    """The Retarget of the scene: the GH3 rig of the profile to the character's armature."""
    p = props(scene)
    role = character_tools.role_of(p.profile)
    clib, profile = character_tools.library_profile(scene)
    rig_data = clib.rig(profile) if clib is not None else character_tools.profile_rig(character_tools.library(character_tools.library_path(scene)), role)
    source_bind = np.concatenate([rig_data.local[:, :3, 3] * [1, 1, -1],
                                  character.matrix_quaternions(rig_data.local[:, :3, :3]) * [-1, -1, 1, 1]], -1)
    sources = [name.removeprefix("bone-") for name in rig_data.names]
    names, parents, bind, rest = rest_rows(p.rig)
    index = {name: i for i, name in enumerate(names)}
    rows = followers(scene)
    instrument = instrument_row(scene) or {}
    retarget = skeleton.Retarget(sources, rig_data.parents, source_bind, names, parents, bind,
                                 {role2: index[bone] for role2, bone in humanoid(scene).items() if bone in index},
                                 [(index[r["bone"]], index[r["source"]], r["weight"]) for r in rows],
                                 index.get(instrument.get("bone", ""), -1), instrument.get("offset", (0, 0, 0)), role, instrument.get("scale", 0.0))
    return retarget, rig_data, (names, parents, bind, rest)


def source_poses(scene, clip, rig_data):
    """The local poses of a GH3 clip on the GH3 rig of the profile, in runtime coordinates: (translations, rotations)."""
    p = props(scene)
    role = character_tools.role_of(p.profile)
    clib, profile = character_tools.library_profile(scene)
    if clib is not None:
        decoded, offsets = q48.decode(character_tools.decompress(clib.clip(clip))), clib.clip_offsets(profile)
    else:
        lib = character_tools.library(character_tools.library_path(scene))
        decoded, offsets = q48.decode(character_tools.decompress(lib.clip(role, clip))), None
    gltf_t, gltf_q = character.clip_local_poses(decoded, rig_data, role, offsets)
    return gltf_t * [1, 1, -1], gltf_q * [-1, -1, 1, 1]


def clip_action(scene, clip):
    """The clip preview of a character with its own skeleton: the GH3 clip (with the game IK) on the GH3 rig helper, and
    its retarget on the character's armature. Returns the action of the armature."""
    p = props(scene)
    role = character_tools.role_of(p.profile)
    _, profile = character_tools.library_profile(scene)
    retarget, rig_data, (names, parents, bind, rest) = retarget_for(scene)
    source_t, source_q = source_poses(scene, clip, rig_data)
    frames = source_t.shape[0]
    target_t, target_q, out_t, out_q = retarget.apply(source_t, source_q, *retarget.arm_gains(source_t, source_q))
    name = f"{profile or role} clip {clip} (retargeted)"
    for action in character_tools.clip_actions(): bpy.data.actions.remove(action)
    for obj, label in ((p.rig, name), (p.source_rig, f"{name} GH3")):
        if obj is None: continue
        obj.animation_data_create()
        action = bpy.data.actions.new(label)
        action[character_tools.PREVIEW] = label
        action["chstage_clip"] = clip
        obj.animation_data.action = action
        if obj == p.rig:
            mute_constraints(obj)
            locations, rotations = _basis(obj, names, _world(parents, target_t, target_q))
            _keys(action, obj, names, locations, rotations, frames)
        else:
            locations, rotations = character_tools.clip_basis(obj, rig_data, out_t * [1, 1, -1], out_q * [-1, -1, 1, 1])
            _keys(action, obj, rig_data.names, locations, rotations, frames)
            # The instrument takes the size of the character (the arm length ratio), as in the game.
            for root, mode in retarget.attachments:
                if mode == "chest": obj.pose.bones[rig_data.names[root]].scale = (retarget.instrument_scale,) * 3
    scene.render.fps, scene.render.fps_base = character_tools.CLIP_FPS, 1.0
    scene.frame_start, scene.frame_end = 0, max(1, frames - 1)
    scene.frame_set(0)
    p.clip = clip
    return p.rig.animation_data.action




# Check Clips.
def check_clip_ids(scene, limit=6):
    """The clips that Check Clips plays: one for each kind in CHECK_CLIPS that the profile has (idle, stance change,
    solo, jump, kick, intro, walk), else the first clips."""
    p = props(scene)
    chosen = []
    for part in CHECK_CLIPS:
        hit = next((c for c in p.clips if part in c.label and c.frames > 2 and c.clip not in [x[0] for x in chosen]), None)
        if hit is not None: chosen.append((hit.clip, hit.label))
        if len(chosen) >= limit: break
    for c in p.clips:
        if len(chosen) >= limit: break
        if c.frames > 2 and c.clip not in [x[0] for x in chosen]: chosen.append((c.clip, c.label))
    return chosen


def mesh_sample(scene, names, edges_limit=12000):
    """Vertices (runtime coordinates), their four largest bone weights (exported bone indices), and a sample of edges,
    for the stretch check."""
    index = {name: i for i, name in enumerate(names)}
    positions, joints, weights, edges, offset = [], [], [], [], 0
    for obj in character_tools.character_meshes(scene):
        mesh = obj.data
        count = len(mesh.vertices)
        if not count: continue
        co = np.empty(count * 3)
        mesh.vertices.foreach_get("co", co)
        world = np.array(obj.matrix_world)
        positions.append((co.reshape(-1, 3) @ world[:3, :3].T + world[:3, 3]) @ A.T)
        groups = {g.index: index.get(g.name, -1) for g in obj.vertex_groups}
        j, w = np.zeros((count, 4), int), np.zeros((count, 4))
        for vertex in mesh.vertices:
            rows = sorted(((e.weight, groups.get(e.group, -1)) for e in vertex.groups if groups.get(e.group, -1) >= 0), reverse=True)[:4]
            for k, (weight, bone) in enumerate(rows): j[vertex.index, k], w[vertex.index, k] = bone, weight
        total = w.sum(1, keepdims=True)
        w = np.where(total > 0, w / np.maximum(total, 1e-9), 0)
        joints.append(j)
        weights.append(w)
        pairs = np.empty(len(mesh.edges) * 2, int)
        mesh.edges.foreach_get("vertices", pairs)
        edges.append(pairs.reshape(-1, 2) + offset)
        offset += count
    if not positions: return None
    edges = np.concatenate(edges)
    if len(edges) > edges_limit: edges = edges[np.random.default_rng(3).choice(len(edges), edges_limit, replace=False)]
    weights = np.concatenate(weights)
    edges = edges[(weights[edges[:, 0]].sum(1) > 0) & (weights[edges[:, 1]].sum(1) > 0)]
    return {"positions": np.concatenate(positions), "joints": np.concatenate(joints), "weights": weights, "edges": edges}


def run_checks(scene, limit=6, mesh=True):
    """Play the check clips with the retarget of the game and keep the findings (Check Clips). Returns the findings."""
    p = props(scene)
    retarget, rig_data, (names, parents, bind, rest) = retarget_for(scene)
    index = {name: i for i, name in enumerate(names)}
    roles = {role: index[bone] for role, bone in humanoid(scene).items() if bone in index}
    sample = mesh_sample(scene, names) if mesh else None
    findings = []
    for clip, label in check_clip_ids(scene, limit):
        source_t, source_q = source_poses(scene, clip, rig_data)
        findings += retarget_check.check_clip(retarget, label, source_t, source_q, names, roles, sample)
    findings = retarget_check.summary(findings)
    p.checks.clear()
    for f in findings:
        item = p.checks.add()
        item.severity, item.kind, item.role, item.clip, item.message, item.fix = f["severity"], f["kind"], f["role"], f["clip"], f["message"], f["fix"]
    p.checked = True
    return findings


# Map overlay: lines between the joints of the roles (green: sure, yellow: check, red: not sure).
_overlay = []
COLORS = {"sure": (0.2, 0.9, 0.3, 1.0), "check": (1.0, 0.8, 0.1, 1.0), "low": (1.0, 0.2, 0.2, 1.0)}


def _links(mapping):
    has = lambda r: r in mapping
    for role in mapping:
        following = skeleton.next_role(role, has)
        if following is not None and following in mapping: yield role, following
    for side in ("left", "right"):
        for a, b in (("Hand", "ThumbMetacarpal"), ("Hand", "ThumbProximal"), ("Hand", "IndexProximal"), ("Hand", "RingProximal"),
                     ("Hand", "LittleProximal"), ("hips", "UpperLeg"), ("chest", "Shoulder"), ("upperChest", "Shoulder")):
            first = a if a in ("hips", "chest", "upperChest") else side + a
            if first in mapping and side + b in mapping: yield first, side + b


def _draw_map():
    import gpu
    from gpu_extras.batch import batch_for_shader
    scene = bpy.context.scene
    if scene is None or not getattr(scene, "chstage", None) or not own(scene): return
    p = props(scene)
    if not p.show_map or p.rig is None or p.rig.name not in scene.objects: return
    rig = p.rig
    rows = {row.role: row for row in p.roles if row.bone and row.bone in rig.pose.bones}
    mapping = {role: row.bone for role, row in rows.items()}
    groups = {key: [] for key in COLORS}
    points = {key: [] for key in COLORS}

    def key(c): return "sure" if c >= SURE else "check" if c >= CHECK else "low"

    world = rig.matrix_world
    for a, b in _links(mapping):
        c = min(rows[a].confidence, rows[b].confidence)
        groups[key(c)] += [tuple(world @ rig.pose.bones[mapping[a]].head), tuple(world @ rig.pose.bones[mapping[b]].head)]
    for role, row in rows.items():
        points[key(row.confidence)].append(tuple(world @ rig.pose.bones[row.bone].head))
    shader = gpu.shader.from_builtin("UNIFORM_COLOR")
    gpu.state.depth_test_set("NONE")
    gpu.state.line_width_set(3.0)
    gpu.state.point_size_set(8.0)
    for name, color in COLORS.items():
        shader.uniform_float("color", color)
        if groups[name]: batch_for_shader(shader, "LINES", {"pos": groups[name]}).draw(shader)
        if points[name]: batch_for_shader(shader, "POINTS", {"pos": points[name]}).draw(shader)
    gpu.state.line_width_set(1.0)
    gpu.state.point_size_set(1.0)


def _draw_labels():
    import blf
    from bpy_extras.view3d_utils import location_3d_to_region_2d
    context = bpy.context
    scene = context.scene
    if scene is None or not getattr(scene, "chstage", None) or not own(scene): return
    p = props(scene)
    if not p.show_map or p.rig is None or p.rig.name not in scene.objects: return
    selected = p.roles[p.roles_index].role if 0 <= p.roles_index < len(p.roles) else ""
    for row in p.roles:
        if not row.bone or row.bone not in p.rig.pose.bones or (row.confidence >= SURE and row.role != selected): continue
        place = location_3d_to_region_2d(context.region, context.region_data, p.rig.matrix_world @ p.rig.pose.bones[row.bone].head)
        if place is None: continue
        color = COLORS["sure" if row.confidence >= SURE else "check" if row.confidence >= CHECK else "low"]
        blf.color(0, *color)
        blf.size(0, 13)
        blf.position(0, place.x + 6, place.y + 4, 0)
        blf.draw(0, row.role + (" <" if row.role == selected else ""))


def overlay(on):
    """Turn the map overlay on or off (all 3D views)."""
    global _overlay
    if on and not _overlay and not bpy.app.background:
        _overlay = [bpy.types.SpaceView3D.draw_handler_add(_draw_map, (), "WINDOW", "POST_VIEW"),
                    bpy.types.SpaceView3D.draw_handler_add(_draw_labels, (), "WINDOW", "POST_PIXEL")]
    elif not on and _overlay:
        for handle in _overlay: bpy.types.SpaceView3D.draw_handler_remove(handle, "WINDOW")
        _overlay = []
    for window in getattr(bpy.context.window_manager, "windows", []):
        for area in window.screen.areas:
            if area.type == "VIEW_3D": area.tag_redraw()


# Operators and panel.
def _active_bone(context, rig):
    if context.mode == "EDIT_ARMATURE" and rig.data.edit_bones.active is not None: return rig.data.edit_bones.active.name
    return rig.data.bones.active.name if rig.data.bones.active is not None else None


class CHSTAGE_OT_use_skeleton(Operator):
    """Set up the selected armature (the skeleton of your model) for the character: apply the transforms, turn the model to stand on +Z and look toward -Y, mirror a mirrored export back, put the feet on the floor, fix the unit, and find the humanoid roles from the bones (any names). The game retargets the GH3 clips to it"""
    bl_idname = "chstage.use_skeleton"
    bl_label = "Set Up Skeleton"
    bl_options = {"REGISTER", "UNDO"}

    prepare_model: BoolProperty(name="Prepare the model", default=True,
                                description="Apply the transforms, turn the model, put the feet on the floor, and fix the unit. Off: only find the roles")
    mirror: EnumProperty(name="Mirror", default="AUTO", items=[("AUTO", "Auto", "Mirror when the bone names say left where the body is right"),
                                                               ("ON", "Mirror", "Mirror the model in X"), ("OFF", "Do not mirror", "")])

    @classmethod
    def poll(cls, context):
        return context.scene is not None and props(context.scene).enabled and context.object is not None and context.object.type == "ARMATURE"

    def execute(self, context):
        try:
            mapping = use_skeleton(context.scene, context.object, self.prepare_model, self.mirror)
        except ValueError as error:
            self.report({"WARNING"}, str(error))
            return {"CANCELLED"}
        missing = [r for r in skeleton.REQUIRED if r not in mapping]
        unsure = [row.role for row in props(context.scene).roles if row.bone and row.confidence < SURE]
        text = f"Mapped {len(mapping)} roles"
        if missing: text += f"; not found: {', '.join(missing)}"
        if unsure: text += f"; check: {', '.join(unsure[:6])}"
        self.report({"WARNING" if missing else "INFO"}, text)
        props(context.scene).show_map = True
        return {"FINISHED"}


class CHSTAGE_OT_join_armatures(Operator):
    """Join the selected armatures into the active one (a body and a head that were exported apart): bones with the same name are one bone, and the other armatures move so that these bones meet"""
    bl_idname = "chstage.join_armatures"
    bl_label = "Join Armatures"
    bl_options = {"REGISTER", "UNDO"}

    @classmethod
    def poll(cls, context):
        return (context.scene is not None and props(context.scene).enabled and context.object is not None and context.object.type == "ARMATURE"
                and sum(1 for o in context.selected_objects if o.type == "ARMATURE") > 1)

    def execute(self, context):
        others = [o for o in context.selected_objects if o.type == "ARMATURE" and o != context.object]
        try:
            added, shared = join_armatures(context.scene, context.object, others)
        except ValueError as error:
            self.report({"WARNING"}, str(error))
            return {"CANCELLED"}
        self.report({"INFO"}, f"Added {added} bones; shared bones: {', '.join(shared[:6])}")
        return {"FINISHED"}


class CHSTAGE_OT_auto_map(Operator):
    """Find the humanoid roles again from the bones (the model stays as it is), and the followers and face shape keys"""
    bl_idname = "chstage.auto_map"
    bl_label = "Auto Map"
    bl_options = {"REGISTER", "UNDO"}

    @classmethod
    def poll(cls, context):
        return context.scene is not None and own(context.scene) and props(context.scene).rig is not None

    def execute(self, context):
        mapping = auto_map(context.scene)
        self.report({"INFO"}, f"Mapped {len(mapping)} roles")
        return {"FINISHED"}


class CHSTAGE_OT_role_edit(Operator):
    """Edit the selected role of the humanoid map"""
    bl_idname = "chstage.role_edit"
    bl_label = "Edit Role"
    bl_options = {"REGISTER", "UNDO", "INTERNAL"}

    action: EnumProperty(items=[("ASSIGN", "Assign Active Bone", "Give the selected role to the active bone (click the bone in Pose Mode or Edit Mode)"),
                                ("SELECT", "Select Bone", "Make the bone of the selected role the active bone"),
                                ("CLEAR", "Clear", "The selected role has no bone"),
                                ("LEFT", "Mirror Left to Right", "Give the right roles the bones on the other side of the left roles"),
                                ("RIGHT", "Mirror Right to Left", "Give the left roles the bones on the other side of the right roles")])

    @classmethod
    def poll(cls, context):
        return context.scene is not None and own(context.scene) and props(context.scene).rig is not None

    def execute(self, context):
        p = props(context.scene)
        rig = p.rig
        if self.action in ("LEFT", "RIGHT"):
            count = mirror_roles(context.scene, self.action.lower())
            self.report({"INFO"}, f"Mirrored {count} roles")
            return {"FINISHED"}
        if not 0 <= p.roles_index < len(p.roles):
            self.report({"WARNING"}, "Select a role in the list")
            return {"CANCELLED"}
        row = p.roles[p.roles_index]
        if self.action == "CLEAR":
            row.bone, row.confidence = "", 0.0
        elif self.action == "SELECT":
            if row.bone not in rig.data.bones: return {"CANCELLED"}
            rig.data.bones.active = rig.data.bones[row.bone]
            for bone in rig.data.bones: bone.select = bone.name == row.bone
        else:
            name = _active_bone(context, rig)
            if name is None:
                self.report({"WARNING"}, "Click a bone of the armature first (Pose Mode or Edit Mode)")
                return {"CANCELLED"}
            if not rig.data.bones[name].use_deform:
                self.report({"WARNING"}, f"'{name}' does not deform; the export writes deform bones only: turn on Deform in its Bone properties")
                return {"CANCELLED"}
            for other in p.roles:
                if other.bone == name and other != row: other.bone, other.confidence = "", 0.0
            row.bone, row.confidence = name, 1.0
            if p.roles_index + 1 < len(p.roles): p.roles_index += 1
        return {"FINISHED"}


class CHSTAGE_OT_map_preset(Operator, ExportHelper):
    """Save the humanoid map, the followers, and the face shape keys as a preset file (to use it for other models of the same rig family)"""
    bl_idname = "chstage.save_map_preset"
    bl_label = "Save Map Preset"
    filename_ext = ".json"
    filter_glob: StringProperty(default="*.json", options={"HIDDEN"})

    @classmethod
    def poll(cls, context):
        return context.scene is not None and own(context.scene) and props(context.scene).rig is not None

    def execute(self, context):
        save_preset(context.scene, self.filepath)
        self.report({"INFO"}, f"Saved {bpy.path.basename(self.filepath)}")
        return {"FINISHED"}


class CHSTAGE_OT_load_map_preset(Operator, ImportHelper):
    """Load a humanoid map preset: the roles whose bones this armature has"""
    bl_idname = "chstage.load_map_preset"
    bl_label = "Load Map Preset"
    filename_ext = ".json"
    filter_glob: StringProperty(default="*.json", options={"HIDDEN"})

    @classmethod
    def poll(cls, context):
        return context.scene is not None and own(context.scene) and props(context.scene).rig is not None

    def execute(self, context):
        try:
            done, missing = load_preset(context.scene, self.filepath)
        except (OSError, ValueError, KeyError) as error:
            self.report({"WARNING"}, f"No preset: {error}")
            return {"CANCELLED"}
        self.report({"WARNING" if missing else "INFO"}, f"Set {done} roles" + (f"; no bone for {', '.join(missing)}" if missing else ""))
        return {"FINISHED"}


class CHSTAGE_OT_check_clips(Operator):
    """Play GH3 clips (idle, stance change, solo, jump, kick, intro, walk) with the retarget of the game and find problems: a hand far from the instrument, a bone that flips, a knee or elbow that bends the other way, a sliding foot, a twisted forearm, stretched mesh"""
    bl_idname = "chstage.check_clips"
    bl_label = "Check Clips"
    bl_options = {"REGISTER"}

    @classmethod
    def poll(cls, context):
        return context.scene is not None and own(context.scene) and props(context.scene).rig is not None and len(props(context.scene).clips) > 0

    def execute(self, context):
        missing = [r for r in skeleton.REQUIRED if r not in humanoid(context.scene)]
        if missing:
            self.report({"WARNING"}, f"Set the roles {', '.join(missing)} first")
            return {"CANCELLED"}
        try:
            findings = run_checks(context.scene)
        except (OSError, ValueError, KeyError) as error:
            self.report({"WARNING"}, f"No check: {error}")
            return {"CANCELLED"}
        errors = sum(1 for f in findings if f["severity"] == "ERROR")
        self.report({"WARNING" if findings else "INFO"}, f"{errors} errors, {len(findings) - errors} warnings" if findings else "No problems in the clips")
        return {"FINISHED"}


class CHSTAGE_UL_roles(UIList):
    def draw_item(self, context, layout, data, item, icon, active_data, active_property, index=0, flt_flag=0):
        rig = data.rig
        row = layout.row(align=True)
        required = item.role in skeleton.REQUIRED
        if not item.bone:
            row.alert = required
            mark = "ERROR" if required else "DOT"
        else:
            row.alert = item.confidence < CHECK
            mark = "CHECKMARK" if item.confidence >= SURE else "QUESTION" if item.confidence >= CHECK else "ERROR"
        row.label(text=item.role, icon=mark)
        if rig is not None: row.prop_search(item, "bone", rig.data, "bones", text="")
        else: row.prop(item, "bone", text="")

    def filter_items(self, context, data, propname):
        rows = getattr(data, propname)
        text = self.filter_name.lower()
        return [self.bitflag_filter_item if not text or text in f"{r.role} {r.bone}".lower() else 0 for r in rows], []


class CHSTAGE_UL_checks(UIList):
    def draw_item(self, context, layout, data, item, icon, active_data, active_property, index=0, flt_flag=0):
        layout.label(text=item.message, icon="ERROR" if item.severity == "ERROR" else "INFO")


class CHSTAGE_PT_character_skeleton(Panel):
    bl_label = "Skeleton"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = CATEGORY

    @classmethod
    def poll(cls, context):
        return context.scene is not None and props(context.scene).enabled and props(context.scene).own_skeleton

    def draw(self, context):
        import textwrap
        layout = self.layout
        scene = context.scene
        p = props(scene)
        width = max(20, int(context.region.width / (7 * context.preferences.view.ui_scale)))
        box = layout.box()
        box.label(text="1. Import your model, select its armature", icon="IMPORT")
        row = box.row(align=True)
        row.operator("chstage.use_skeleton", icon="ARMATURE_DATA")
        row.prop(p, "target_height")
        box.operator("chstage.join_armatures", icon="AUTOMERGE_ON")
        if p.rig is None:
            box.label(text="Select the armature of your model, then click Set Up Skeleton", icon="ERROR")
            return
        box.label(text=f"Armature: {p.rig.name}")
        for line in notes(scene)[:8]:
            for k, part in enumerate(textwrap.wrap(line, width)): box.label(text=part, icon="DOT" if k == 0 else "BLANK1")
        box = layout.box()
        mapping = humanoid(scene)
        missing = [r for r in skeleton.REQUIRED if r not in mapping]
        unsure = sum(1 for row in p.roles if row.bone and row.confidence < SURE)
        box.label(text="2. Check the map" + (f": {len(missing)} roles missing" if missing else f": {unsure} roles to check" if unsure else ": all roles sure"),
                  icon="ERROR" if missing else "QUESTION" if unsure else "CHECKMARK")
        row = box.row(align=True)
        row.prop(p, "show_map", toggle=True, icon="HIDE_OFF" if p.show_map else "HIDE_ON")
        row.operator("chstage.auto_map", icon="FILE_REFRESH")
        box.template_list("CHSTAGE_UL_roles", "", p, "roles", p, "roles_index", rows=8)
        row = box.row(align=True)
        row.operator("chstage.role_edit", text="Assign Active Bone", icon="BONE_DATA").action = "ASSIGN"
        row.operator("chstage.role_edit", text="", icon="RESTRICT_SELECT_OFF").action = "SELECT"
        row.operator("chstage.role_edit", text="", icon="X").action = "CLEAR"
        row = box.row(align=True)
        row.operator("chstage.role_edit", text="Left to Right", icon="MOD_MIRROR").action = "LEFT"
        row.operator("chstage.role_edit", text="Right to Left", icon="MOD_MIRROR").action = "RIGHT"
        row = box.row(align=True)
        row.operator("chstage.save_map_preset", text="Save Preset", icon="EXPORT")
        row.operator("chstage.load_map_preset", text="Load Preset", icon="IMPORT")
        box = layout.box()
        box.label(text="3. Preview and check the clips", icon="PLAY")
        row = box.row(align=True)
        for clip, label in check_clip_ids(scene, 4):
            row.operator("chstage.preview_clip", text=label.split("/")[0].replace("stance_", "stance ")[:12]).clip = clip
        box.operator("chstage.check_clips", icon="VIEWZOOM")
        if p.checked and not p.checks: box.label(text="No problems in the check clips", icon="CHECKMARK")
        if p.checks:
            box.template_list("CHSTAGE_UL_checks", "", p, "checks", p, "checks_index", rows=3)
            if 0 <= p.checks_index < len(p.checks):
                item = p.checks[p.checks_index]
                for line in textwrap.wrap(item.fix, width): box.label(text=line)
        try:
            count = len(json.loads(p.followers or "[]"))
        except ValueError:
            count = 0
        layout.label(text=f"Followers (MMD append rotation): {count}", icon="CONSTRAINT_BONE")
        box = layout.box()
        box.label(text="Face shape keys (blink and mouth)")
        meshes = [o for o in character_tools.character_meshes(scene) if o.data.shape_keys]
        for slot, prop in FACE_PROPS.items():
            if meshes: box.prop_search(p, prop, meshes[0].data.shape_keys, "key_blocks", text=slot)
            else: box.prop(p, prop, text=slot)
        box = layout.box()
        box.label(text="Instrument")
        box.prop_search(p, "instrument_bone", p.rig.data, "bones")
        box.prop(p, "instrument_offset")
        box.prop(p, "instrument_scale")


CLASSES = (CHSTAGE_OT_use_skeleton, CHSTAGE_OT_join_armatures, CHSTAGE_OT_auto_map, CHSTAGE_OT_role_edit, CHSTAGE_OT_map_preset, CHSTAGE_OT_load_map_preset,
           CHSTAGE_OT_check_clips, CHSTAGE_UL_roles, CHSTAGE_UL_checks, CHSTAGE_PT_character_skeleton)


def register():
    for cls in CLASSES: bpy.utils.register_class(cls)


def unregister():
    overlay(False)
    for cls in reversed(CLASSES): bpy.utils.unregister_class(cls)
