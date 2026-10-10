"""A character that keeps the skeleton of its model (docs/PACKAGE-FORMAT.md section 9.6, docs/BLENDER-ADDON.md section
1.17): the humanoid map from the bone names (MMD, VRM and VRoid, Mixamo, Unreal mannequin), the MMD append rotations
(followers), the face shape keys, the checks, the character.json rows, and the clip preview: the GH3 clip plays on the
GH3 rig of the profile (a helper) and the retarget of the game moves the character's armature.

The export writes the deform bones of the armature (the glTF exporter option 'deform bones only') and the face shape
keys only; the GLB joint names are the Blender bone names.
"""

import json

import bpy
import numpy as np
from bpy.props import StringProperty
from bpy.types import Operator, Panel, UIList

from . import character_tools
from .vendor.package_format import character, q48, skeleton

CATEGORY = "CH Character"
# Blender world (Z up) to runtime (glTF with z negated): (x, y, z) -> (x, z, y). A reflection; it is its own inverse.
A = np.array([[1., 0, 0], [0, 0, 1], [0, 1, 0]])
FACE_PROPS = {slot: f"face_{slot}" for slot in skeleton.FACE_SLOTS}


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


def weighted_bones(scene):
    rig = props(scene).rig
    names = set()
    for obj in character_tools.character_meshes(scene):
        groups = {g.index: g.name for g in obj.vertex_groups}
        for vertex in obj.data.vertices:
            for element in vertex.groups:
                if element.weight > 0 and element.group in groups: names.add(groups[element.group])
    return {name for name in names if rig is not None and name in rig.data.bones}


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


def fill_roles(scene, mapping):
    p = props(scene)
    p.roles.clear()
    for role in skeleton.ROLES:
        row = p.roles.add()
        row.role, row.bone = role, mapping.get(role, "")


def auto_map(scene):
    """Fill the humanoid map, the followers, and the face shape keys from the armature of the scene."""
    p = props(scene)
    rig = p.rig
    _, names, parents = exported_bones(rig)
    mapping = vrm_map(rig) or skeleton.auto_map(names, parents, deform=weighted_bones(scene))
    mapping = {role: bone for role, bone in mapping.items() if bone in names}
    fill_roles(scene, mapping)
    p.followers = json.dumps(skeleton.mmd_followers(mmd_follower_rows(rig), names, mapping), ensure_ascii=False)
    face = skeleton.auto_face(shape_key_names(scene))
    for slot, prop in FACE_PROPS.items(): setattr(p, prop, face.get(slot, ""))
    return mapping


def use_skeleton(scene, armature):
    """The armature of the model becomes the character's rig (own skeleton) with an automatic map."""
    p = props(scene)
    if armature is None or armature.type != "ARMATURE": raise ValueError("select the armature of the model")
    if p.source_rig is not None and armature == p.source_rig: raise ValueError("select the armature of the model, not the GH3 rig")
    p.own_skeleton = True
    p.rig = armature
    return auto_map(scene)


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


def check(scene, issues):
    """The checks of the skeleton (the rules of the game importer) before the export."""
    rig = props(scene).rig
    names, parents, bind, _ = rest_rows(rig)
    if len(names) > 255: issues.error(f"the armature has {len(names)} deform bones (limit 255): turn off Deform on helper bones", rig.name)
    errors, warnings, _ = skeleton.check_map(names, parents, bind, humanoid(scene), followers(scene), instrument_row(scene))
    for message in errors: issues.error(message, rig.name)
    for message in warnings: issues.warn(message, rig.name)
    keys = set(shape_key_names(scene))
    for slot, name in face(scene).items():
        if name not in keys: issues.error(f"face {slot}: no character mesh has the shape key '{name}'", rig.name)


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


def clip_action(scene, clip):
    """The clip preview of a character with its own skeleton: the GH3 clip (with the game IK) on the GH3 rig helper, and
    its retarget on the character's armature. Returns the action of the armature."""
    p = props(scene)
    role = character_tools.role_of(p.profile)
    clib, profile = character_tools.library_profile(scene)
    if clib is not None:
        decoded, offsets = q48.decode(character_tools.decompress(clib.clip(clip))), clib.clip_offsets(profile)
    else:
        lib = character_tools.library(character_tools.library_path(scene))
        decoded, offsets = q48.decode(character_tools.decompress(lib.clip(role, clip))), None
    retarget, rig_data, (names, parents, bind, rest) = retarget_for(scene)
    frames = decoded[0]["frames"]
    gltf_t, gltf_q = character.clip_local_poses(decoded, rig_data, role, offsets)
    source_t, source_q = gltf_t * [1, 1, -1], gltf_q * [-1, -1, 1, 1]
    target_t, target_q, out_t, out_q = retarget.apply(source_t, source_q)
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


# Operators and panel.
class CHSTAGE_OT_use_skeleton(Operator):
    """Use the selected armature (the skeleton of your model) for the character: the game retargets the GH3 clips to it. Maps the humanoid roles from the bone names (MMD, VRM, Mixamo, Unreal) and finds the blink and mouth shape keys"""
    bl_idname = "chstage.use_skeleton"
    bl_label = "Use Selected Armature"
    bl_options = {"REGISTER", "UNDO"}

    @classmethod
    def poll(cls, context):
        return context.scene is not None and props(context.scene).enabled and context.object is not None and context.object.type == "ARMATURE"

    def execute(self, context):
        try:
            mapping = use_skeleton(context.scene, context.object)
        except ValueError as error:
            self.report({"WARNING"}, str(error))
            return {"CANCELLED"}
        missing = [r for r in skeleton.REQUIRED if r not in mapping]
        self.report({"WARNING" if missing else "INFO"}, f"Mapped {len(mapping)} roles" + (f"; set {', '.join(missing)}" if missing else ""))
        return {"FINISHED"}


class CHSTAGE_OT_auto_map(Operator):
    """Map the humanoid roles, the followers, and the face shape keys again from the names"""
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


class CHSTAGE_UL_roles(UIList):
    def draw_item(self, context, layout, data, item, icon, active_data, active_property, index=0, flt_flag=0):
        rig = data.rig
        row = layout.row(align=True)
        required = item.role in skeleton.REQUIRED
        row.label(text=item.role, icon="ERROR" if required and not item.bone else "BONE_DATA")
        if rig is not None: row.prop_search(item, "bone", rig.data, "bones", text="")
        else: row.prop(item, "bone", text="")

    def filter_items(self, context, data, propname):
        rows = getattr(data, propname)
        text = self.filter_name.lower()
        return [self.bitflag_filter_item if not text or text in f"{r.role} {r.bone}".lower() else 0 for r in rows], []


class CHSTAGE_PT_character_skeleton(Panel):
    bl_label = "Skeleton"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = CATEGORY

    @classmethod
    def poll(cls, context):
        return context.scene is not None and props(context.scene).enabled and props(context.scene).own_skeleton

    def draw(self, context):
        layout = self.layout
        scene = context.scene
        p = props(scene)
        layout.label(text="The game retargets the GH3 clips to this armature", icon="INFO")
        row = layout.row(align=True)
        row.operator("chstage.use_skeleton", icon="ARMATURE_DATA")
        row.operator("chstage.auto_map", icon="FILE_REFRESH")
        if p.rig is None:
            layout.label(text="Select the armature of your model, then click Use Selected Armature", icon="ERROR")
            return
        layout.label(text=f"Armature: {p.rig.name}")
        layout.template_list("CHSTAGE_UL_roles", "", p, "roles", p, "roles_index", rows=8)
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


CLASSES = (CHSTAGE_OT_use_skeleton, CHSTAGE_OT_auto_map, CHSTAGE_UL_roles, CHSTAGE_PT_character_skeleton)


def register():
    for cls in CLASSES: bpy.utils.register_class(cls)


def unregister():
    for cls in reversed(CLASSES): bpy.utils.unregister_class(cls)
