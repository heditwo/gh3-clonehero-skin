"""Custom hair, skirt, and tail chains and body colliders of a character (docs/PACKAGE-FORMAT.md section 9.5,
docs/BLENDER-ADDON.md section 1.16).

A chain is a list of bones that the creator adds to the profile rig. Each added bone with a joint (bone.chstage_joint)
belongs to a chain (scene.chstage_chains). A collider is a capsule mesh (object.chstage_collider) parented to a rig bone;
it is a helper and the GLB export skips it. Export writes character.json bones, colliders, and chains from the exported
GLB (vendor package_format.chains); Open makes the joints and the colliders again.
"""

import traceback

import bpy
import numpy as np
from bpy.props import (
    BoolProperty,
    CollectionProperty,
    EnumProperty,
    FloatProperty,
    IntProperty,
    PointerProperty,
    StringProperty,
)
from bpy.types import Operator, Panel, PropertyGroup, UIList
from mathutils import Matrix, Vector

from . import character_tools
from .vendor.package_format import chains as chain_format
from .vendor.package_format.profiles import bone_key

CATEGORY = "CH Character"
COLLIDER_SEGMENTS = 12
# GH3 body bones and the bone at the other end of their capsule, for Fit Body Colliders.
BODY_SEGMENTS = (
    ("hips", "bone_pelvis", ("bone_thigh_l", "bone_thigh_r")),
    ("stomach", "bone_stomach_lower", "bone_stomach_upper"),
    ("waist", "bone_stomach_upper", "bone_chest"),
    ("chest", "bone_chest", "bone_neck"),
    ("thigh_l", "bone_thigh_l", "bone_knee_l"),
    ("thigh_r", "bone_thigh_r", "bone_knee_r"),
    ("shin_l", "bone_knee_l", "bone_ankle_l"),
    ("shin_r", "bone_knee_r", "bone_ankle_r"),
    ("upper_arm_l", "bone_bicep_l", "bone_forearm_l"),
    ("upper_arm_r", "bone_bicep_r", "bone_forearm_r"),
    ("forearm_l", "bone_forearm_l", "bone_palm_l"),
    ("forearm_r", "bone_forearm_r", "bone_palm_r"),
)
DEFAULT_RADIUS = {
    "hips": 0.15,
    "stomach": 0.12,
    "waist": 0.12,
    "chest": 0.13,
    "thigh": 0.08,
    "shin": 0.06,
    "upper_arm": 0.05,
    "forearm": 0.045,
}


def bone_name(label):
    """The rig bone name of a GH3 bone name ('bone-<checksum>')."""
    return f"bone-{bone_key(label)}"


# Properties.
def joint_settings(joint):
    return {
        "mass": joint.mass,
        "friction": joint.friction,
        "damping": joint.damping,
        "cone": joint.cone,
        "planes": joint.planes,
        "speed": joint.speed,
        "radius": joint.radius,
        "stiffness": joint.stiffness,
        "drag": joint.drag,
        "gravity": joint.gravity,
    }


def apply_preset(joint, preset, position=0.0):
    """The preset values on a joint at `position` along its chain (0: the first joint, 1: the last joint)."""
    values = chain_format.PRESETS[preset]
    joint.mass, joint.friction, joint.damping, joint.cone = (
        values["mass"],
        values["friction"],
        values["damping"],
        values["cone"],
    )
    joint.planes, joint.speed, joint.radius = values["planes"], values["speed"], values["radius"]
    joint.stiffness, joint.drag, joint.gravity = (
        chain_format.preset_stiffness(preset, position),
        values["drag"],
        values["gravity"],
    )


def update_collider(self, context):
    obj = self.id_data
    if isinstance(obj, bpy.types.Object) and obj.type == "MESH" and self.enabled:
        capsule_mesh(obj.data, self.radius, self.length)


class CHSTAGE_PG_joint(PropertyGroup):
    """The physics of an added bone (a joint of a custom chain). Angles in degrees."""

    enabled: BoolProperty(name="Joint", description="The bone is a physics joint of a chain")
    chain: StringProperty(name="Chain", description="The chain of the joint")
    mass: FloatProperty(
        name="Mass",
        default=1.0,
        min=0.01,
        max=100,
        description="Mass of the joint (kg). In a chain, heavy joints pull the joints above them",
    )
    friction: FloatProperty(
        name="Friction",
        default=0.1,
        min=0,
        max=10,
        description="Friction torque of the joint for each kilogram (N m/kg): it stops small swings",
    )
    damping: FloatProperty(
        name="Damping", default=1.0, min=0, max=100, description="Angular damping (per second): it slows all swings"
    )
    cone: FloatProperty(
        name="Swing limit", default=15, min=0.5, max=90, description="Largest swing from the bind direction (degrees)"
    )
    planes: FloatProperty(
        name="Side limit",
        default=0,
        min=0,
        max=90,
        description="Largest swing toward the X axis of the bone (degrees). 0: only the swing limit",
    )
    speed: FloatProperty(
        name="Speed limit",
        default=0,
        min=0,
        max=1000,
        description="Largest turn speed (radians per second). 0: no limit",
    )
    radius: FloatProperty(name="Radius", default=0.02, min=0, max=0.5, description="Collision radius of the joint (m)")
    stiffness: FloatProperty(
        name="Stiffness",
        default=30,
        min=0,
        max=chain_format.MAX_STIFFNESS,
        description="Pull toward the bind direction against the parent bone (per second squared). 0: the joint only hangs",
    )
    drag: FloatProperty(
        name="Drag",
        default=15,
        min=0,
        max=100,
        description="Damping of the swing against the parent bone (per second): it stops the swings after a move",
    )
    gravity: FloatProperty(
        name="Gravity", default=1, min=0, max=2, description="Share of gravity (1: full gravity, 0: no gravity)"
    )


class CHSTAGE_PG_chain(PropertyGroup):
    preset: EnumProperty(name="Preset", items=chain_format.PRESET_ITEMS, default="long_hair")
    bend: FloatProperty(
        name="Bend limit",
        default=60,
        min=0,
        max=180,
        description="Largest angle of the chain end from its bind direction, seen from the chain start (degrees). 0: no limit",
    )
    all_colliders: BoolProperty(name="All colliders", default=True, description="The joints collide with all colliders")
    colliders: StringProperty(
        name="Colliders",
        description="Names of the colliders that the joints collide with (comma-separated), when not all",
    )


class CHSTAGE_PG_collider(PropertyGroup):
    enabled: BoolProperty(description="The object is a collider of the character chains")
    label: StringProperty(name="Name", description="The collider name in character.json (chains name their colliders)")
    radius: FloatProperty(name="Radius", default=0.08, min=0.001, max=5, update=update_collider)
    length: FloatProperty(
        name="Length",
        default=0.2,
        min=0,
        max=4,
        update=update_collider,
        description="Distance of the two capsule ends (0: a sphere)",
    )


# Collider objects.
def capsule_mesh(mesh, radius, length):
    """A low-poly capsule along local Z, centred on the origin."""
    import bmesh

    data = bmesh.new()
    quarter = COLLIDER_SEGMENTS // 4
    rings = [
        (-length / 2 - radius * np.cos(step / quarter * np.pi / 2), radius * np.sin(step / quarter * np.pi / 2))
        for step in range(quarter + 1)
    ]
    rings += [
        (length / 2 + radius * np.cos(step / quarter * np.pi / 2), radius * np.sin(step / quarter * np.pi / 2))
        for step in range(quarter, -1, -1)
    ]
    circle = np.linspace(0, 2 * np.pi, COLLIDER_SEGMENTS, endpoint=False)
    verts = [
        [data.verts.new((0, 0, z))] if r < 1e-6 else [data.verts.new((r * np.cos(a), r * np.sin(a), z)) for a in circle]
        for z, r in rings
    ]
    for low, high in zip(verts, verts[1:]):
        for i in range(COLLIDER_SEGMENTS):
            j = (i + 1) % COLLIDER_SEGMENTS
            if len(low) == 1 and len(high) == 1:
                break
            if len(low) == 1:
                data.faces.new((low[0], high[i], high[j]))
            elif len(high) == 1:
                data.faces.new((low[i], low[j], high[0]))
            elif high[i] != low[i]:
                data.faces.new((low[i], low[j], high[j], high[i]))
    data.to_mesh(mesh)
    data.free()
    mesh.update()


def colliders(scene):
    rig = character_tools.props(scene).rig
    return sorted(
        [o for o in scene.objects if o.type == "MESH" and o.chstage_collider.enabled and o.parent == rig],
        key=lambda o: o.name,
    )


def collider_name(obj):
    return chain_format.chain_name(obj.chstage_collider.label or obj.name.removeprefix("Collider "))


def add_collider(scene, bone, a, b, radius, name=None):
    """A capsule collider from world point a to b on a rig bone. Returns the object."""
    rig = character_tools.props(scene).rig
    a, b = Vector(a), Vector(b)
    length = (b - a).length
    mesh = bpy.data.meshes.new("collider")
    obj = bpy.data.objects.new(f"Collider {name or bone}", mesh)
    collection = next(
        (c for c in scene.collection.children if c.name.startswith("Character Helpers")), scene.collection
    )
    collection.objects.link(obj)
    obj.chstage.marker = "PROXY"
    obj.display_type = "WIRE"
    obj.hide_render = True
    obj.chstage_collider.enabled = True
    obj.chstage_collider.label = chain_format.chain_name(name or bone)
    obj.chstage_collider.length = length
    obj.chstage_collider.radius = radius
    capsule_mesh(mesh, radius, length)
    axis = (b - a).normalized() if length > 1e-6 else Vector((0, 0, 1))
    matrix = Matrix.Translation((a + b) / 2) @ axis.to_track_quat("Z", "Y").to_matrix().to_4x4()
    obj.parent = rig
    obj.parent_type = "BONE"
    obj.parent_bone = bone
    # The ends are rest-pose points: the collider takes its place on the bone at the rest pose (export reads it there).
    position = rig.data.pose_position
    rig.data.pose_position = "REST"
    scene.view_layers[0].update()
    try:
        obj.matrix_world = matrix
    finally:
        rig.data.pose_position = position
        scene.view_layers[0].update()
    return obj


def collider_rows(scene):
    """The colliders as character_rows input, at the rest pose of the rig: name, bone, radius, and the two ends (world)."""
    rig = character_tools.props(scene).rig
    position = rig.data.pose_position
    rig.data.pose_position = "REST"
    scene.view_layers[0].update()
    try:
        rows, taken = [], set()
        for obj in colliders(scene):
            name = chain_format.chain_name(collider_name(obj), taken)
            taken.add(name)
            half = obj.chstage_collider.length / 2
            a, b = obj.matrix_world @ Vector((0, 0, -half)), obj.matrix_world @ Vector((0, 0, half))
            rows.append(
                {
                    "name": name,
                    "bone": obj.parent_bone,
                    "radius": obj.chstage_collider.radius * obj.matrix_world.to_scale()[0],
                    "a": list(a),
                    "b": list(b),
                    "object": obj.name,
                }
            )
        return rows
    finally:
        rig.data.pose_position = position
        scene.view_layers[0].update()


# Chains.
def chain_items(scene):
    return scene.chstage_chains


def chain_bones(rig, chain):
    """The joint bones of a chain, parents first."""
    return [bone for bone in rig.data.bones if bone.chstage_joint.enabled and bone.chstage_joint.chain == chain]


def added_bones(scene):
    """The rig bones that are not in the profile (none for a character with its own skeleton: its chain bones are
    skeleton bones)."""
    rig = character_tools.props(scene).rig
    if character_tools.props(scene).own_skeleton:
        return []
    try:
        profile = set(character_tools.scene_rig_data(scene).names)
    except (OSError, ValueError, KeyError):
        profile = {b.name for b in rig.data.bones if b.name.startswith("bone-")}
    return [bone for bone in rig.data.bones if bone.name not in profile]


def new_chain(scene, name, preset):
    chain = scene.chstage_chains.add()
    chain.name = chain_format.chain_name(name, {c.name for c in scene.chstage_chains if c != chain})
    chain.preset = preset
    chain.bend = chain_format.PRESETS[preset]["bend"]
    scene.chstage_chains_index = len(scene.chstage_chains) - 1
    return chain


def make_joints(scene, bones, chain, preset):
    """The bones (parents first) as joints of the chain with the preset values; the stiffness falls off along the chain."""
    for index, bone in enumerate(bones):
        joint = bone.chstage_joint
        joint.enabled, joint.chain = True, chain.name
        apply_preset(joint, preset, index / max(1, len(bones) - 1))
        if hasattr(bone, "color"):
            bone.color.palette = "THEME04"


class _Nothing:
    def __enter__(self):
        return None

    def __exit__(self, *error):
        return False


class edit_rig:
    """Edit Mode of the rig of a scene for a block (with the rig active, selected, and shown); the rig edit bones."""

    def __init__(self, scene):
        self.scene = scene
        self.rig = character_tools.props(scene).rig

    def __enter__(self):
        view_layer = self.scene.view_layers[0]
        self.active = view_layer.objects.active
        self.selected = [o for o in self.scene.objects if o.select_get(view_layer=view_layer)]
        self.hidden = self.rig.hide_get(view_layer=view_layer)
        if self.active is not None and self.active.mode != "OBJECT":
            with bpy.context.temp_override(
                scene=self.scene, view_layer=view_layer, active_object=self.active, object=self.active
            ):
                bpy.ops.object.mode_set(mode="OBJECT")
        self.rig.hide_set(False, view_layer=view_layer)
        for obj in self.selected:
            obj.select_set(False, view_layer=view_layer)
        self.rig.select_set(True, view_layer=view_layer)
        view_layer.objects.active = self.rig
        self.override = bpy.context.temp_override(
            scene=self.scene,
            view_layer=view_layer,
            active_object=self.rig,
            object=self.rig,
            selected_objects=[self.rig],
            selected_editable_objects=[self.rig],
        )
        self.override.__enter__()
        bpy.ops.object.mode_set(mode="EDIT")
        return self.rig.data.edit_bones

    def __exit__(self, *error):
        view_layer = self.scene.view_layers[0]
        try:
            bpy.ops.object.mode_set(mode="OBJECT")
        finally:
            self.override.__exit__(*error)
            self.rig.select_set(False, view_layer=view_layer)
            for obj in self.selected:
                obj.select_set(True, view_layer=view_layer)
            self.rig.hide_set(self.hidden, view_layer=view_layer)
            view_layer.objects.active = self.active
        return False


def create_bones(scene, points, parent, prefix):
    """New rig bones through the points (count + 1 points give count bones), the first one parented to `parent`.
    Returns their names."""
    rig = character_tools.props(scene).rig
    taken = {b.name for b in rig.data.bones}
    names = []
    inverse = rig.matrix_world.inverted()
    with edit_rig(scene) as edit:
        above = edit[parent]
        for index in range(len(points) - 1):
            name = chain_format.chain_name(f"{prefix}_{index + 1:02d}", taken)
            taken.add(name)
            bone = edit.new(name)
            bone.head, bone.tail = inverse @ Vector(points[index]), inverse @ Vector(points[index + 1])
            bone.roll = 0
            bone.parent = above
            bone.use_connect = index > 0
            above = bone
            names.append(name)
    return names


def assign_chain(scene, obj, names, vertices, parent=None):
    """Weights of the vertices along the chain bones: a vertex follows the bone that it projects on; near a joint the
    weight blends with the bone above (or the chain parent), so the strand bends smoothly."""
    rig = character_tools.props(scene).rig
    character_tools.fit_to_rig(obj, rig)
    groups = character_tools.profile_groups(rig)
    world = np.array(rig.matrix_world)
    heads = [world[:3, :3] @ np.array(rig.data.bones[n].head_local) + world[:3, 3] for n in names]
    tails = [world[:3, :3] @ np.array(rig.data.bones[n].tail_local) + world[:3, 3] for n in names]
    matrix = np.array(obj.matrix_world)
    weights = np.zeros((len(vertices), len(groups)))
    columns = [groups.index(n) for n in names]
    above = groups.index(parent) if parent in groups else None
    for row, index in enumerate(vertices):
        point = np.array(obj.data.vertices[index].co) @ matrix[:3, :3].T + matrix[:3, 3]
        best = None
        for bone, (head, tail) in enumerate(zip(heads, tails)):
            axis = tail - head
            t = float(np.clip((point - head) @ axis / max(axis @ axis, 1e-12), 0, 1))
            distance = np.linalg.norm(point - head - t * axis)
            if best is None or distance < best[0]:
                best = (distance, bone, t)
        _, bone, t = best
        upper = columns[bone - 1] if bone > 0 else above
        share = max(0.0, 0.5 - t) if upper is not None else 0.0
        weights[row, columns[bone]] += 1 - share
        if share > 0:
            weights[row, upper] += share
    character_tools.set_vertex_weights(obj, groups, weights, vertices)
    return len(vertices)


def body_bone(scene, label):
    """The rig bone of a GH3 body bone name: 'bone-<checksum>' on a GH3 rig, the bone of the humanoid role on a skeleton."""
    p = character_tools.props(scene)
    if not p.own_skeleton:
        return bone_name(label)
    roles = {row.role: row.bone for row in p.roles if row.bone}
    spine = [r for r in ("spine", "chest", "upperChest") if r in roles]
    by_label = {
        "bone_pelvis": "hips",
        "bone_neck": "neck",
        "bone_head": "head",
        "bone_stomach_lower": spine[0] if spine else "spine",
        "bone_stomach_upper": spine[len(spine) // 2] if spine else "spine",
        "bone_chest": spine[-1] if spine else "spine",
    }
    for side, s in (("left", "l"), ("right", "r")):
        for part, gh3 in (
            ("UpperLeg", "thigh"),
            ("LowerLeg", "knee"),
            ("Foot", "ankle"),
            ("UpperArm", "bicep"),
            ("LowerArm", "forearm"),
            ("Hand", "palm"),
        ):
            by_label[f"bone_{gh3}_{s}"] = side + part
    role = by_label.get(label)
    return roles.get(role, "") if role else ""


def nearest_body_bone(scene, point):
    """The body bone of the profile rig (not a hair joint, not a face bone) whose head is nearest to the point."""
    rig = character_tools.props(scene).rig
    if character_tools.props(scene).own_skeleton:
        world = np.array(rig.matrix_world)
        bones = [row.bone for row in character_tools.props(scene).roles if row.bone in rig.data.bones]
        return min(
            bones,
            key=lambda name: float(
                np.linalg.norm(world[:3, :3] @ np.array(rig.data.bones[name].head_local) + world[:3, 3] - point)
            ),
            default=None,
        )
    hair = set(character_tools.hair_bones(scene))
    head = rig.data.bones.get(bone_name("bone_head"))
    world = np.array(rig.matrix_world)
    best = None
    for bone in rig.data.bones:
        if not bone.name.startswith("bone-") or bone.name in hair:
            continue
        if head is not None and bone != head and bone.parent_recursive and head in bone.parent_recursive:
            continue
        position = world[:3, :3] @ np.array(bone.head_local) + world[:3, 3]
        distance = float(np.linalg.norm(position - point))
        if best is None or distance < best[0]:
            best = (distance, bone.name)
    return best[1] if best else None


def selected_points(context):
    """World positions of the selected vertices of the active mesh (Edit Mode or Object Mode), or the points of a
    selected curve object; and the mesh and its selected vertex indices (None for a curve)."""
    obj = context.object
    if obj is not None and obj.type == "MESH":
        if obj.mode == "EDIT":
            obj.update_from_editmode()
        indices = [v.index for v in obj.data.vertices if v.select]
        matrix = np.array(obj.matrix_world)
        points = (
            np.array([np.array(obj.data.vertices[i].co) for i in indices]).reshape(-1, 3) @ matrix[:3, :3].T
            + matrix[:3, 3]
        )
        return points, obj, indices
    if obj is not None and obj.type == "CURVE":
        matrix = np.array(obj.matrix_world)
        points = [
            np.array(p.co[:3])
            for spline in obj.data.splines
            for p in (spline.bezier_points if spline.type == "BEZIER" else spline.points)
        ]
        return np.array(points) @ matrix[:3, :3].T + matrix[:3, 3], None, None
    return np.zeros((0, 3)), None, None


# Export and open.
def export_rows(scene, glb_data):
    """character.json bones, colliders, and chains of the exported GLB. Empty lists without added bones."""
    rig = character_tools.props(scene).rig
    own = character_tools.props(scene).own_skeleton
    added = (
        {bone.name for bone in rig.data.bones if bone.chstage_joint.enabled}
        if own
        else {bone.name for bone in added_bones(scene)}
    )
    if not added and not colliders(scene):
        return [], [], []
    profile = [b.name for b in rig.data.bones if b.name not in added]
    world = rig.matrix_world
    joints = {}
    for bone in rig.data.bones:
        if bone.name not in added or not bone.chstage_joint.enabled:
            continue
        axis = world.to_3x3() @ joint_vector(bone)
        side = world.to_3x3() @ bone.matrix_local.to_3x3() @ Vector((1, 0, 0))
        joints[bone.name] = {
            "settings": joint_settings(bone.chstage_joint),
            "chain": bone.chstage_joint.chain,
            "axis": list(axis),
            "side": list(side),
        }
    rows = collider_rows(scene)
    names = {row["name"] for row in rows}
    chain_list = []
    for chain in scene.chstage_chains:
        wanted = None if chain.all_colliders else [n.strip() for n in chain.colliders.split(",") if n.strip() in names]
        chain_list.append({"name": chain.name, "colliders": wanted, "bend": chain.bend})
    return chain_format.character_rows(glb_data, profile, joints, rows, chain_list, own=own)


def joint_vector(bone):
    """The joint of a chain bone (armature space, from its head): to the head of the next joint of its chain below it,
    else to the head of its only child bone, else to its tail. The bone tails of an imported rig (FBX, glTF) can point
    away from the chain; the next joint can hang from a helper bone (an offset bone of a game rig)."""
    chain = bone.chstage_joint.chain
    queue = list(bone.children)
    while queue:
        child = queue.pop(0)
        if child.chstage_joint.enabled:
            if child.chstage_joint.chain == chain and (child.head_local - bone.head_local).length > 0.005:
                return child.head_local - bone.head_local
            continue
        queue.extend(child.children)
    if (
        len(bone.children) == 1
        and not bone.children[0].chstage_joint.enabled
        and (bone.children[0].head_local - bone.head_local).length > 0.005
    ):
        return bone.children[0].head_local - bone.head_local
    return bone.tail_local - bone.head_local


def check(scene, issues):
    """Add-on checks of the chains: names of the added bones, joints outside a chain, joint limits of the format."""
    rig = character_tools.props(scene).rig
    if character_tools.props(scene).own_skeleton:
        check_own(scene, issues)
        return
    added = added_bones(scene)
    chains = {c.name for c in scene.chstage_chains}
    for bone in added:
        if not chain_format.NAME.fullmatch(bone.name):
            issues.error(
                f"added bone '{bone.name}': use only letters, digits, '_', '.', and '-' (at most 64)", rig.name
            )
        if bone.parent is None:
            issues.error(f"added bone '{bone.name}' has no parent bone: parent it to a bone of the rig", rig.name)
        joint = bone.chstage_joint
        if joint.enabled and joint.chain not in chains:
            issues.error(f"bone '{bone.name}' is a joint of the chain '{joint.chain}', which does not exist", rig.name)
    for bone in rig.data.bones:
        if bone.chstage_joint.enabled and bone not in added:
            issues.error(f"bone '{bone.name}' of the profile cannot be a chain joint; add a new bone", rig.name)
    count = sum(1 for bone in added if bone.chstage_joint.enabled)
    if count > chain_format.MAX_JOINTS:
        issues.error(f"the chains have {count} joints (limit {chain_format.MAX_JOINTS})", rig.name)
    if len(colliders(scene)) > chain_format.MAX_COLLIDERS:
        issues.error(
            f"the character has {len(colliders(scene))} colliders (limit {chain_format.MAX_COLLIDERS})", rig.name
        )
    for obj in colliders(scene):
        if obj.parent_type != "BONE" or obj.parent_bone not in rig.data.bones:
            issues.error(f"collider '{obj.name}' must have a rig bone as parent", obj.name)
        elif rig.data.bones[obj.parent_bone].chstage_joint.enabled:
            issues.error(f"collider '{obj.name}' is on a chain bone; put colliders on body bones", obj.name)


def check_own(scene, issues):
    """The chains of a character with its own skeleton: the joints are deform bones without a humanoid role (and not
    parents of one); colliders are on other bones."""
    from . import skeleton_tools

    rig = character_tools.props(scene).rig
    names, parents, bind, _ = skeleton_tools.rest_rows(rig)
    _, _, allowed = skeleton_tools.skeleton.check_map(
        names, parents, bind, skeleton_tools.humanoid(scene), skeleton_tools.followers(scene)
    )
    chains = {c.name for c in scene.chstage_chains}
    joints = [bone for bone in rig.data.bones if bone.chstage_joint.enabled]
    for bone in joints:
        if bone.name not in allowed:
            issues.error(
                f"bone '{bone.name}' cannot be a chain joint: it has a humanoid role, is a parent of one, is a follower, or does not deform",
                rig.name,
            )
        if bone.chstage_joint.chain not in chains:
            issues.error(
                f"bone '{bone.name}' is a joint of the chain '{bone.chstage_joint.chain}', which does not exist",
                rig.name,
            )
    if len(joints) > chain_format.MAX_JOINTS:
        issues.error(f"the chains have {len(joints)} joints (limit {chain_format.MAX_JOINTS})", rig.name)
    if len(colliders(scene)) > chain_format.MAX_COLLIDERS:
        issues.error(
            f"the character has {len(colliders(scene))} colliders (limit {chain_format.MAX_COLLIDERS})", rig.name
        )
    for obj in colliders(scene):
        if obj.parent_type != "BONE" or obj.parent_bone not in rig.data.bones:
            issues.error(f"collider '{obj.name}' must have a rig bone as parent", obj.name)
        elif rig.data.bones[obj.parent_bone].chstage_joint.enabled:
            issues.error(f"collider '{obj.name}' is on a chain bone; put colliders on body bones", obj.name)


def open_rows(scene, character, glb_data=None):
    """Make the chains and the colliders of an opened character.json (the GLB import made the added bones)."""
    rig = character_tools.props(scene).rig
    if rig is None:
        return
    by_key = {bone_key(b.name): b for b in rig.data.bones}
    lengths = {bone_key(j["bone"]): j["length"] for row in character.get("chains", []) for j in row["joints"]}
    if lengths:
        # The glTF import gives an end bone some length; the joint length of character.json is the bone length.
        with edit_rig(scene) as edit:
            for bone in edit:
                if bone_key(bone.name) in lengths:
                    bone.length = lengths[bone_key(bone.name)]
        by_key = {bone_key(b.name): b for b in rig.data.bones}
    for row in character.get("chains", []):
        chain = new_chain(scene, row["name"], "long_hair")
        chain.bend = float(np.degrees(row.get("bendLimit", 0.0)))
        chain.all_colliders = "colliders" not in row
        chain.colliders = ", ".join(row.get("colliders", []))
        for joint in row["joints"]:
            bone = by_key.get(bone_key(joint["bone"]))
            if bone is None:
                continue
            settings = bone.chstage_joint
            settings.enabled, settings.chain = True, chain.name
            settings.mass, settings.damping = joint["mass"], joint["angularDamping"]
            settings.friction = joint["friction"] / max(joint["mass"], 1e-6)
            settings.speed, settings.radius = joint.get("maxAngularVelocity", 0), joint.get("radius", 0)
            settings.stiffness, settings.drag, settings.gravity = (
                joint.get("stiffness", 0),
                joint.get("drag", 0),
                joint.get("gravity", 1),
            )
            if "limits" in joint:
                settings.cone = float(np.degrees(joint["limits"]["coneLimit"][1]))
                planes = joint["limits"].get("planesLimit")
                settings.planes = float(np.degrees(max(abs(planes[0]), abs(planes[1])))) if planes else 0
            if hasattr(bone, "color"):
                bone.color.palette = "THEME04"
    frames = {}
    if glb_data is not None:
        from .vendor.package_format.character import Rig
        from .vendor.package_format.glb import Glb

        glb_rig = Rig(Glb(glb_data))
        frames = {bone_key(name): glb_rig.world[i] for i, name in enumerate(glb_rig.names)}
    for row in character.get("colliders", []):
        bone = by_key.get(bone_key(row["bone"]))
        frame = frames.get(bone_key(row["bone"]))
        if bone is None or frame is None:
            continue
        # The collider ends are in the runtime frame of the GLB joint: to glTF world, then to Blender world.
        ends = [
            chain_format.AXES @ (frame @ np.r_[np.array(row[key]) * chain_format.FLIP, 1])[:3]
            for key in ("a", "b")
            if key in row
        ]
        add_collider(scene, bone.name, ends[0], ends[-1], row["radius"], row["name"])


# MMD Tools physics.
def mmd_rows(armature, rigid_bodies, joints, names=None):
    """The dynamic MMD rigid bodies of the bones in `names` (all when None) as chain bones: MMD bone name, parent MMD
    bone name, world head and tail, mass, damping, radius, and the limits of the joint that holds it; and the static
    bodies as colliders (MMD bone name, world ends, radius). MMD Tools data: rigid body objects (mmd_rigid: bone, type,
    shape, size) with Blender rigid bodies, and joint objects with rigid body constraints."""
    world = np.array(armature.matrix_world)
    bones = armature.data.bones
    by_object = {}
    statics = []
    for obj in rigid_bodies:
        rigid = obj.mmd_rigid
        if not rigid.bone or rigid.bone not in bones:
            continue
        scale = float(np.mean(obj.matrix_world.to_scale()))
        matrix = np.array(obj.matrix_world)
        size = np.array(rigid.size) * scale
        if rigid.shape == "SPHERE":
            radius, half = size[0], 0.0
        elif rigid.shape == "CAPSULE":
            radius, half = size[0], size[1] / 2
        else:
            radius, half = float(np.mean(sorted(size)[:2])), float(max(size))
        if int(rigid.type) == 0:
            axis = (
                matrix[:3, :3] @ ([0, 1, 0] if rigid.shape != "BOX" else np.eye(3)[int(np.argmax(rigid.size))]) / scale
            )
            centre = matrix[:3, 3]
            statics.append(
                {"bone": rigid.bone, "a": centre - axis * half, "b": centre + axis * half, "radius": float(radius)}
            )
            continue
        if names is not None and rigid.bone not in names:
            continue
        body = obj.rigid_body
        # Bullet damps the speed of the body (linear) and its turn (angular) in the world: the larger one is the damping.
        damping = max(body.linear_damping, body.angular_damping) if body else 0.5
        by_object[obj.name] = {
            "bone": rigid.bone,
            "mass": float(body.mass) if body else 1.0,
            "damping": chain_format.mmd_damping(damping),
            "radius": float(min(radius, 0.1)),
        }
    rows = {}
    for row in by_object.values():
        bone = bones[row["bone"]]
        head = world[:3, :3] @ np.array(bone.head_local) + world[:3, 3]
        tail = world[:3, :3] @ np.array(bone.tail_local) + world[:3, 3]
        rows[row["bone"]] = {
            **row,
            "head": head,
            "tail": tail,
            "parent": bone.parent.name if bone.parent else None,
            "cone": None,
        }
    for obj in joints:
        constraint = obj.rigid_body_constraint
        if constraint is None or constraint.object2 is None or constraint.object2.name not in by_object:
            continue
        row = rows[by_object[constraint.object2.name]["bone"]]
        limits = [
            constraint.limit_ang_x_lower,
            constraint.limit_ang_x_upper,
            constraint.limit_ang_y_lower,
            constraint.limit_ang_y_upper,
            constraint.limit_ang_z_lower,
            constraint.limit_ang_z_upper,
        ]
        frame = np.array(obj.matrix_world.to_3x3().normalized())
        row["cone"], row["planes"], row["plane_axis"] = chain_format.mmd_limits(
            row["tail"] - row["head"], frame, limits
        )
        row["spring"] = max(
            (abs(v) for v in getattr(getattr(obj, "mmd_joint", None), "spring_angular", (0, 0, 0))), default=0.0
        )
    return rows, statics


def import_mmd(
    scene,
    armature,
    rigid_bodies,
    joints,
    names=None,
    parent=None,
    preset="long_hair",
    with_colliders=False,
    prefix="mmd",
):
    """Chains from the dynamic MMD rigid bodies: a new rig bone for each one at its MMD bone (world), the MMD masses,
    damping, and joint limits, and a chain for each root. A body without a turning joint gives a bone without a joint
    (it follows its parent). When the rig is the MMD armature itself (a character with its own skeleton), the MMD bones
    become the joints and no bone is added. Returns {MMD bone name: rig bone name} and the colliders made."""
    rows, statics = mmd_rows(armature, rigid_bodies, joints, names)
    rig = character_tools.props(scene).rig
    taken = {b.name for b in rig.data.bones}
    made = {}
    english = {b.name: (getattr(getattr(b, "mmd_bone", None), "name_e", "") or "") for b in armature.pose.bones}
    order = sorted(rows, key=lambda n: len(armature.data.bones[n].parent_recursive))
    inverse = rig.matrix_world.inverted()
    in_place = (
        rig == armature or all(name in rig.data.bones for name in order) and character_tools.props(scene).own_skeleton
    )
    targets = {} if in_place else {name: parent or nearest_body_bone(scene, rows[name]["head"]) for name in order}
    if in_place:
        made = {name: name for name in order}
    with edit_rig(scene) if not in_place else _Nothing() as edit:
        for index, name in enumerate(order if not in_place else []):
            row = rows[name]
            label = (
                english.get(name)
                if chain_format.NAME.fullmatch(english.get(name) or "-") and english.get(name)
                else f"{index + 1:02d}"
            )
            new = chain_format.chain_name(f"{prefix}_{label}", taken)
            taken.add(new)
            bone = edit.new(new)
            bone.head, bone.tail = inverse @ Vector(row["head"]), inverse @ Vector(row["tail"])
            above = row["parent"]
            while above is not None and above not in rows:
                above = armature.data.bones[above].parent.name if armature.data.bones[above].parent else None
            bone.parent = edit[made[above]] if above is not None else edit[targets[name]]
            made[name] = new
    root_of = {}
    for name in order:
        above = rows[name]["parent"]
        while above is not None and above not in rows:
            above = armature.data.bones[above].parent.name if armature.data.bones[above].parent else None
        root_of[name] = name if above is None else root_of[above]
    chains = {}
    for name in order:
        row = rows[name]
        if not row["cone"]:
            continue
        root = root_of[name]
        if root not in chains:
            # A chain on MMD bones of the skeleton gets an ASCII name from the prefix and the English MMD name.
            label = (
                english.get(root)
                if english.get(root) and chain_format.NAME.fullmatch(english.get(root))
                else f"{order.index(root) + 1:02d}"
            )
            chains[root] = new_chain(scene, made[root] if not in_place else f"{prefix}_{label}", preset)
        bone = rig.data.bones[made[name]]
        make_joints(scene, [bone], chains[root], preset)
        joint = bone.chstage_joint
        joint.mass = min(max(row["mass"], 0.05), 100)
        joint.damping = min(row["damping"], 100)
        # The MMD values: the rotation limits, and the spring that holds the shape (the stiffness and its drag); the
        # friction, the speed limit, and the chain bend limit come from the preset (MMD has none).
        joint.cone = float(np.degrees(row["cone"]))
        joint.stiffness = chain_format.mmd_stiffness(
            row.get("spring", 0.0), row["mass"], np.linalg.norm(row["tail"] - row["head"])
        )
        joint.drag = chain_format.mmd_drag(joint.stiffness, chain_format.PRESETS[preset]["drag"])
        joint.radius = row["radius"]
        if row.get("planes"):
            low, high = row["planes"]
            joint.planes = min(float(np.degrees(max(abs(low), abs(high)))), joint.cone)
    made_colliders = []
    if with_colliders:
        for index, body in enumerate(statics):
            target = nearest_body_bone(scene, (body["a"] + body["b"]) / 2)
            if target is None:
                continue
            made_colliders.append(
                add_collider(scene, target, body["a"], body["b"], body["radius"], f"mmd_{index + 1:02d}")
            )
    return made, made_colliders


# VRM spring bones.
def gltf_document(path):
    """The JSON of a .vrm or .glb file."""
    import json
    import struct

    with open(path, "rb") as file:
        header = file.read(20)
        if header[:4] != b"glTF":
            raise ValueError(f"{path} is not a binary glTF (.vrm, .glb) file")
        length = struct.unpack("<I", header[12:16])[0]
        return json.loads(file.read(length))


def gltf_worlds(document):
    """The glTF world matrix of each node of a glTF JSON."""
    nodes = document.get("nodes", [])
    parent = {child: i for i, node in enumerate(nodes) for child in node.get("children", [])}
    locals_ = []
    for node in nodes:
        if "matrix" in node:
            locals_.append(np.array(node["matrix"], dtype=float).reshape(4, 4).T)
            continue
        x, y, z, w = node.get("rotation", [0, 0, 0, 1])
        matrix = np.eye(4)
        matrix[:3, :3] = np.array(
            [
                [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
            ]
        ) * node.get("scale", [1, 1, 1])
        matrix[:3, 3] = node.get("translation", [0, 0, 0])
        locals_.append(matrix)
    worlds = [None] * len(nodes)

    def world(i):
        if worlds[i] is None:
            worlds[i] = (world(parent[i]) if i in parent else np.eye(4)) @ locals_[i]
        return worlds[i]

    return [world(i) for i in range(len(nodes))]


def similarity(source, target):
    """Scale, rotation, and translation that move the source points onto the target points (least squares)."""
    source, target = np.asarray(source, dtype=float), np.asarray(target, dtype=float)
    a, b = source.mean(0), target.mean(0)
    u, sigma, vt = np.linalg.svd((target - b).T @ (source - a))
    d = np.eye(3)
    d[2, 2] = np.sign(np.linalg.det(u @ vt))
    rotation = u @ d @ vt
    scale = float(np.trace(np.diag(sigma) @ d) / max(((source - a) ** 2).sum(), 1e-12))
    return scale, rotation, b - scale * rotation @ a


def import_vrm(scene, path, preset="long_hair", with_colliders=True):
    """Chains from the spring bones of a VRM file (VRM 1.0 or 0.x) on the bones of the rig with the node names (a
    character with its own skeleton from the VRM import): the VRM stiffness, drag, gravity, and hit radius for each
    joint (chain_format.vrm_joint), the bend limit of the preset, and the VRM colliders on their bones. Returns the
    chains and the colliders made."""
    document = gltf_document(path)
    springs, vrm_colliders = chain_format.vrm_springs(document)
    if not springs:
        raise ValueError("the file has no VRM spring bones")
    rig = character_tools.props(scene).rig
    names = [node.get("name", "") for node in document.get("nodes", [])]
    worlds = gltf_worlds(document)
    # The VRM import can turn and scale the model: the bones of both give the transform from glTF to Blender world.
    world = np.array(rig.matrix_world)
    pairs = [
        (chain_format.AXES @ worlds[i][:3, 3], world[:3, :3] @ np.array(rig.data.bones[n].head_local) + world[:3, 3])
        for i, n in enumerate(names)
        if n in rig.data.bones
    ]
    if len(pairs) < 3:
        raise ValueError(
            "the rig does not have the bones of the VRM file (use the armature of the VRM import as the skeleton)"
        )
    scale, rotation, offset = similarity([p[0] for p in pairs], [p[1] for p in pairs])
    made_colliders, collider_names = [], {}
    if with_colliders:
        for index, collider in enumerate(vrm_colliders):
            bone = names[collider["node"]]
            if bone not in rig.data.bones or rig.data.bones[bone].chstage_joint.enabled:
                continue
            ends = [
                worlds[collider["node"]] @ np.r_[np.asarray(p, dtype=float), 1]
                for p in (collider["offset"], collider["tail"] or collider["offset"])
            ]
            a, b = (scale * rotation @ (chain_format.AXES @ e[:3]) + offset for e in ends)
            obj = add_collider(scene, bone, a, b, max(collider["radius"] * scale, 0.001), f"vrm_{index + 1:02d}")
            made_colliders.append(obj)
            collider_names[index] = obj.chstage_collider.label
    made = []
    taken = {c.name for c in scene.chstage_chains}
    for spring in springs:
        bones = [names[j["node"]] for j in spring["joints"]]
        joints = [(j, rig.data.bones[n]) for j, n in zip(spring["joints"][:-1], bones[:-1]) if n in rig.data.bones]
        if not joints:
            continue
        chain = new_chain(scene, chain_format.chain_name(spring["name"], taken), preset)
        taken.add(chain.name)
        make_joints(scene, [bone for _, bone in joints], chain, preset)
        for row, bone in joints:
            joint = bone.chstage_joint
            values = chain_format.vrm_joint(
                row["stiffness"],
                row["drag"],
                row["gravity_power"],
                row["gravity_dir"],
                joint_vector(bone).length * rig.matrix_world.to_scale()[0] / max(scale, 1e-6),
            )
            joint.damping, joint.stiffness, joint.gravity, joint.drag = (
                values["damping"],
                values["stiffness"],
                values["gravity"],
                values["drag"],
            )
            # VRM has no swing limits and no friction.
            joint.cone, joint.planes, joint.friction, joint.speed = 90.0, 0.0, 0.0, 0.0
            joint.radius = min(row["radius"] * scale, 0.5)
        wanted = [collider_names[c] for c in spring["colliders"] if c in collider_names]
        chain.all_colliders, chain.colliders = False, ", ".join(wanted)
        made.append(chain)
    return made, made_colliders


def fit_body_colliders(scene, meshes, guitar=True, replace=True):
    """Capsules on the GH3 body bones, fitted to the character meshes (the vertices that follow each part), a capsule
    on the guitar body from the attachment preview, and the head capsule of the profile hair. Returns the colliders."""
    p = character_tools.props(scene)
    rig = p.rig
    if replace:
        for obj in colliders(scene):
            if obj.get("chstage_fitted"):
                bpy.data.objects.remove(obj, do_unlink=True)
    names = character_tools.profile_groups(rig)
    chain = {b.name for b in rig.data.bones if b.chstage_joint.enabled}
    world = np.array(rig.matrix_world)

    def head(label):
        bone = rig.data.bones.get(body_bone(scene, label))
        return None if bone is None else world[:3, :3] @ np.array(bone.head_local) + world[:3, 3]

    points, dominant = [], []
    for obj in meshes:
        weights = character_tools.vertex_weights(obj, names)
        matrix = np.array(obj.matrix_world)
        co = np.array([np.array(v.co) for v in obj.data.vertices]).reshape(-1, 3) @ matrix[:3, :3].T + matrix[:3, 3]
        keep = weights.sum(1) > 0
        points.append(co[keep])
        dominant.append(np.array(names)[weights[keep].argmax(1)])
    points = np.vstack(points) if points else np.zeros((0, 3))
    dominant = np.concatenate(dominant) if dominant else np.zeros(0, dtype=str)
    segments = []
    for label, start, end in BODY_SEGMENTS:
        if not isinstance(end, tuple) and body_bone(scene, start) == body_bone(scene, end):
            continue
        a = head(start)
        b = np.mean([head(e) for e in end], axis=0) if isinstance(end, tuple) else head(end)
        if a is None or b is None or (isinstance(end, tuple) and any(head(e) is None for e in end)):
            continue
        if isinstance(end, tuple):
            # The hips: a capsule across the thigh joints, at the height between the pelvis and the thighs.
            left, right = head(end[0]), head(end[1])
            centre = (a + (left + right) / 2) / 2
            a, b = centre + (left - right) / 2 * 0.6, centre - (left - right) / 2 * 0.6
        segments.append((label, start, a, b))
    # Each body vertex (not on a chain) belongs to the nearest segment; the radius comes from these vertices.
    body = points[~np.isin(dominant, list(chain))]
    nearest = (
        np.argmin([chain_format.segment_distances(body, a, b) for _, _, a, b in segments], axis=0)
        if segments and len(body)
        else np.zeros(0, int)
    )
    made = []
    for index, (label, start, a, b) in enumerate(segments):
        radius = chain_format.segment_radius(body[nearest == index], a, b)
        if radius is None:
            radius = DEFAULT_RADIUS[label.removesuffix("_l").removesuffix("_r")]
        if label.startswith("thigh"):
            # The thigh capsule starts below the hip joint, so that a skirt or a hip flap that starts at the hip is
            # outside it at rest.
            a = a + (b - a) / np.linalg.norm(b - a) * min(radius, 0.4 * np.linalg.norm(b - a))
        made.append(add_collider(scene, body_bone(scene, start), a, b, radius, label))
    hair = None
    clib, profile = character_tools.library_profile(scene)
    try:
        hair = clib.hair(profile) if clib is not None else None
    except (OSError, ValueError, KeyError):
        hair = None
    head_bone = rig.data.bones.get(body_bone(scene, "bone_head"))
    if head_bone is not None:
        # A sphere at the centre of the head vertices (the vertices that follow the head bone or a face bone).
        hair = set(character_tools.hair_bones(scene))
        face_bones = [head_bone.name] + [
            b.name for b in head_bone.children_recursive if b.name not in chain and b.name not in hair
        ]
        face = points[np.isin(dominant, face_bones)]
        centre = face.mean(0) if len(face) else head("bone_head")
        radius = float(np.quantile(np.linalg.norm(face - centre, axis=1), 0.5)) if len(face) else 0.1
        made.append(add_collider(scene, head_bone.name, centre, centre, min(max(radius, 0.06), 0.15), "head"))
    # The instrument of a character with its own skeleton hangs on the GH3 rig helper, not on the armature: no guitar capsule.
    if guitar and p.attachment is not None and not p.own_skeleton:
        guitar_bone = rig.data.bones.get(bone_name("bone_guitar_body"))
        if guitar_bone is not None:
            matrix = np.array(p.attachment.matrix_world)
            co = (
                np.array([np.array(v.co) for v in p.attachment.data.vertices]).reshape(-1, 3) @ matrix[:3, :3].T
                + matrix[:3, 3]
            )
            if len(co) > 10:
                a, b, radius = chain_format.flat_capsule(co)
                made.append(add_collider(scene, guitar_bone.name, a, b, radius, "guitar"))
    for obj in made:
        obj["chstage_fitted"] = True
    return made


# Operators.
def character_rig(context):
    scene = context.scene
    return scene is not None and scene.chstage.character.enabled and scene.chstage.character.rig is not None


class CHSTAGE_OT_chain_from_bones(Operator):
    """Make a chain of the selected added bones of the rig (Pose Mode): each bone becomes a joint with the preset values"""

    bl_idname = "chstage.chain_from_bones"
    bl_label = "Chain from Selected Bones"
    bl_options = {"REGISTER", "UNDO"}

    preset: EnumProperty(name="Preset", items=chain_format.PRESET_ITEMS, default="long_hair")
    name: StringProperty(name="Chain name")

    @classmethod
    def poll(cls, context):
        return character_rig(context)

    def execute(self, context):
        scene = context.scene
        rig = scene.chstage.character.rig
        added = {b.name for b in added_bones(scene)}
        bones = [b for b in rig.data.bones if b.select and b.name in added]
        if not bones:
            self.report(
                {"WARNING"},
                "Select bones that you added to the rig (Pose Mode); the bones of the profile cannot be joints",
            )
            return {"CANCELLED"}
        chain = new_chain(scene, self.name or bones[0].name, self.preset)
        make_joints(scene, bones, chain, self.preset)
        self.report({"INFO"}, f"Chain '{chain.name}' with {len(bones)} joints")
        return {"FINISHED"}


class CHSTAGE_OT_chain_from_strand(Operator):
    """Make new bones along the selected vertices of a mesh (a twin tail, a skirt panel, a tail) or along a curve, as a chain, and give the selected vertices weights on it"""

    bl_idname = "chstage.chain_from_strand"
    bl_label = "Chain along Strand"
    bl_options = {"REGISTER", "UNDO"}

    preset: EnumProperty(name="Preset", items=chain_format.PRESET_ITEMS, default="long_hair")
    name: StringProperty(name="Chain name", default="strand")
    joints: IntProperty(name="Joints", default=5, min=1, max=16)
    parent: StringProperty(
        name="Parent bone",
        description="Rig bone of the chain root (for example bone-<checksum> of bone_head). Empty: the nearest body bone",
    )
    weights: BoolProperty(
        name="Weights", default=True, description="Give the selected vertices weights on the new bones"
    )

    @classmethod
    def poll(cls, context):
        return character_rig(context) and context.object is not None and context.object.type in ("MESH", "CURVE")

    def execute(self, context):
        scene = context.scene
        points, obj, vertices = selected_points(context)
        mode = context.object.mode
        if len(points) < 2:
            self.report({"WARNING"}, "Select the vertices of the strand (Edit Mode) or a curve")
            return {"CANCELLED"}
        if mode == "EDIT":
            bpy.ops.object.mode_set(mode="OBJECT")
        try:
            rig = scene.chstage.character.rig
            parent = self.parent or nearest_body_bone(scene, points.mean(0))
            if parent not in rig.data.bones:
                self.report({"WARNING"}, f"The rig has no bone {parent}")
                return {"CANCELLED"}
            world = np.array(rig.matrix_world)
            root = world[:3, :3] @ np.array(rig.data.bones[parent].head_local) + world[:3, 3]
            path = chain_format.strand_points(points, root, self.joints)
            prefix = chain_format.chain_name(self.name, {c.name for c in scene.chstage_chains})
            names = create_bones(scene, path, parent, prefix)
            chain = new_chain(scene, prefix, self.preset)
            make_joints(scene, [rig.data.bones[n] for n in names], chain, self.preset)
            if self.weights and obj is not None and vertices:
                assign_chain(scene, obj, names, vertices, parent)
        except (ValueError, KeyError) as error:
            self.report({"WARNING"}, str(error))
            return {"CANCELLED"}
        finally:
            if mode == "EDIT" and context.object is not None:
                bpy.ops.object.mode_set(mode="EDIT")
        self.report({"INFO"}, f"Chain '{chain.name}': {len(names)} bones on {parent}")
        return {"FINISHED"}


class CHSTAGE_OT_chain_assign(Operator):
    """Give the selected vertices (Edit Mode) weights on the bones of the active chain, blended near the joints"""

    bl_idname = "chstage.chain_assign"
    bl_label = "Assign to Chain"
    bl_options = {"REGISTER", "UNDO"}

    @classmethod
    def poll(cls, context):
        return (
            character_rig(context)
            and context.object is not None
            and context.object.type == "MESH"
            and len(context.scene.chstage_chains) > 0
        )

    def execute(self, context):
        scene = context.scene
        obj = context.object
        chain = scene.chstage_chains[min(scene.chstage_chains_index, len(scene.chstage_chains) - 1)]
        rig = scene.chstage.character.rig
        bones = chain_bones(rig, chain.name)
        mode = obj.mode
        if mode == "EDIT":
            bpy.ops.object.mode_set(mode="OBJECT")
        try:
            vertices = [v.index for v in obj.data.vertices if v.select]
            if not bones or not vertices:
                self.report({"WARNING"}, "Select vertices, and a chain with joints")
                return {"CANCELLED"}
            parent = bones[0].parent.name if bones[0].parent else None
            count = assign_chain(scene, obj, [b.name for b in bones], vertices, parent)
        finally:
            if mode == "EDIT":
                bpy.ops.object.mode_set(mode="EDIT")
        self.report({"INFO"}, f"{count} vertices on chain '{chain.name}'")
        return {"FINISHED"}


class CHSTAGE_OT_chain_preset(Operator):
    """Set the values of the preset on all joints of the active chain"""

    bl_idname = "chstage.chain_preset"
    bl_label = "Apply Preset"
    bl_options = {"REGISTER", "UNDO", "INTERNAL"}

    def execute(self, context):
        scene = context.scene
        if not scene.chstage_chains:
            return {"CANCELLED"}
        chain = scene.chstage_chains[min(scene.chstage_chains_index, len(scene.chstage_chains) - 1)]
        bones = chain_bones(scene.chstage.character.rig, chain.name)
        for index, bone in enumerate(bones):
            apply_preset(bone.chstage_joint, chain.preset, index / max(1, len(bones) - 1))
        chain.bend = chain_format.PRESETS[chain.preset]["bend"]
        return {"FINISHED"}


class CHSTAGE_OT_chain_remove(Operator):
    """Remove the active chain: its bones stay in the rig without physics (they follow their parents)"""

    bl_idname = "chstage.chain_remove"
    bl_label = "Remove Chain"
    bl_options = {"REGISTER", "UNDO", "INTERNAL"}

    def execute(self, context):
        scene = context.scene
        if not scene.chstage_chains:
            return {"CANCELLED"}
        index = min(scene.chstage_chains_index, len(scene.chstage_chains) - 1)
        for bone in chain_bones(scene.chstage.character.rig, scene.chstage_chains[index].name):
            bone.chstage_joint.enabled = False
        scene.chstage_chains.remove(index)
        scene.chstage_chains_index = max(0, index - 1)
        return {"FINISHED"}


class CHSTAGE_OT_add_collider(Operator):
    """Add a capsule collider on the active bone of the rig (Pose Mode). Move and turn it in the viewport; set its radius and length in the panel"""

    bl_idname = "chstage.add_collider"
    bl_label = "Add Collider"
    bl_options = {"REGISTER", "UNDO"}

    radius: FloatProperty(name="Radius", default=0.08, min=0.001, max=1)

    @classmethod
    def poll(cls, context):
        return character_rig(context)

    def execute(self, context):
        scene = context.scene
        rig = scene.chstage.character.rig
        bone = rig.data.bones.active
        if bone is None or bone.chstage_joint.enabled:
            self.report({"WARNING"}, "Make a body bone of the rig active (Pose Mode)")
            return {"CANCELLED"}
        world = rig.matrix_world
        a, b = world @ bone.head_local, world @ bone.tail_local
        add_collider(scene, bone.name, a, b, self.radius, character_tools_label(bone.name))
        return {"FINISHED"}


def character_tools_label(name):
    from .vendor.package_format import character

    return chain_format.chain_name(
        character.bone_label(name.removeprefix("bone-")) if name.startswith("bone-") else name
    )


class CHSTAGE_OT_fit_colliders(Operator):
    """Fit capsule colliders to the body of the character meshes (hips, stomach, chest, legs, arms, head) and to the guitar of the attachment preview"""

    bl_idname = "chstage.fit_colliders"
    bl_label = "Fit Body Colliders"
    bl_options = {"REGISTER", "UNDO"}

    guitar: BoolProperty(
        name="Guitar", default=True, description="Also a capsule on the guitar body (from the attachment preview)"
    )

    @classmethod
    def poll(cls, context):
        return character_rig(context)

    def execute(self, context):
        scene = context.scene
        made = fit_body_colliders(scene, character_tools.character_meshes(scene), self.guitar)
        self.report({"INFO"}, f"{len(made)} colliders")
        return {"FINISHED"}


class CHSTAGE_OT_import_mmd_physics(Operator):
    """Make chains from the MMD rigid bodies and joints of an MMD Tools model in the scene: a new bone for each dynamic body, with the MMD mass, damping, rotation limits, and spring (stiffness). Fit the MMD model to the rig first"""

    bl_idname = "chstage.import_mmd_physics"
    bl_label = "Chains from MMD Physics"
    bl_options = {"REGISTER", "UNDO"}

    armature: StringProperty(name="MMD armature", description="The armature object of the MMD Tools model")
    bones: StringProperty(
        name="MMD bones", description="Only the bodies of these MMD bones (comma-separated). Empty: all dynamic bodies"
    )
    parent: StringProperty(
        name="Parent bone", description="Rig bone of the chain roots. Empty: the nearest body bone of each root"
    )
    preset: EnumProperty(
        name="Preset",
        items=chain_format.PRESET_ITEMS,
        default="long_hair",
        description="Values that MMD does not have (friction, speed limit, the smallest drag, the bend limit)",
    )
    colliders: BoolProperty(
        name="Colliders",
        default=False,
        description="Also make colliders from the static bodies (on the nearest body bone)",
    )
    prefix: StringProperty(
        name="Bone names",
        default="mmd",
        description="The new bones get this name, '_', and the English MMD name or a number",
    )

    @classmethod
    def poll(cls, context):
        return character_rig(context)

    def execute(self, context):
        scene = context.scene
        armature = (
            bpy.data.objects.get(self.armature)
            if self.armature
            else next(
                (
                    o
                    for o in scene.objects
                    if o.type == "ARMATURE" and o != scene.chstage.character.rig and getattr(o, "mmd_type", "") != ""
                ),
                None,
            )
        )
        if armature is None or armature.type != "ARMATURE":
            self.report({"WARNING"}, "No MMD armature: import the model with MMD Tools (with physics) first")
            return {"CANCELLED"}
        objects = [o for o in bpy.data.objects if getattr(o, "mmd_type", "") in ("RIGID_BODY", "JOINT")]
        rigid = [o for o in objects if o.mmd_type == "RIGID_BODY"]
        joint_objects = [o for o in objects if o.mmd_type == "JOINT"]
        names = {n.strip() for n in self.bones.split(",") if n.strip()} or None
        try:
            made, made_colliders = import_mmd(
                scene,
                armature,
                rigid,
                joint_objects,
                names,
                self.parent or None,
                self.preset,
                self.colliders,
                chain_format.chain_name(self.prefix or "mmd"),
            )
        except (ValueError, KeyError) as error:
            traceback.print_exc()
            self.report({"WARNING"}, str(error))
            return {"CANCELLED"}
        self.report({"INFO"}, f"{len(made)} bones, {len(made_colliders)} colliders")
        return {"FINISHED"}


class CHSTAGE_OT_import_vrm_springs(Operator):
    """Make chains from the spring bones of a VRM file (VRM 1.0 or 0.x) on the bones of the rig with the same names (use the armature of the VRM import as the skeleton): the VRM stiffness, drag, gravity, hit radius, and colliders"""

    bl_idname = "chstage.import_vrm_springs"
    bl_label = "Chains from VRM Spring Bones"
    bl_options = {"REGISTER", "UNDO"}

    filepath: StringProperty(subtype="FILE_PATH")
    filter_glob: StringProperty(default="*.vrm;*.glb", options={"HIDDEN"})
    preset: EnumProperty(
        name="Preset",
        items=chain_format.PRESET_ITEMS,
        default="long_hair",
        description="The bend limit of the chains (VRM has none)",
    )
    colliders: BoolProperty(name="Colliders", default=True, description="Also make the VRM colliders")

    @classmethod
    def poll(cls, context):
        return character_rig(context)

    def invoke(self, context, event):
        context.window_manager.fileselect_add(self)
        return {"RUNNING_MODAL"}

    def execute(self, context):
        try:
            made, made_colliders = import_vrm(
                context.scene, bpy.path.abspath(self.filepath), self.preset, self.colliders
            )
        except (OSError, ValueError, KeyError, IndexError) as error:
            traceback.print_exc()
            self.report({"WARNING"}, str(error))
            return {"CANCELLED"}
        self.report({"INFO"}, f"{len(made)} chains, {len(made_colliders)} colliders")
        return {"FINISHED"}


class CHSTAGE_UL_chains(UIList):
    def draw_item(self, context, layout, data, item, icon, active_data, active_property, index=0, flt_flag=0):
        rig = context.scene.chstage.character.rig
        count = len(chain_bones(rig, item.name)) if rig is not None else 0
        row = layout.row(align=True)
        row.prop(item, "name", text="", emboss=False, icon="CON_SPLINEIK")
        row.label(text=f"{count} joints")


class CHSTAGE_PT_character_chains(Panel):
    bl_label = "Chains and Colliders"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = CATEGORY
    bl_options = {"DEFAULT_CLOSED"}

    @classmethod
    def poll(cls, context):
        return character_rig(context)

    def draw(self, context):
        layout = self.layout
        scene = context.scene
        rig = scene.chstage.character.rig
        layout.label(text="Make a chain")
        column = layout.column(align=True)
        column.operator("chstage.chain_from_strand", icon="CURVE_PATH")
        column.operator("chstage.chain_from_bones", icon="BONE_DATA")
        column.operator("chstage.import_mmd_physics", icon="PHYSICS")
        column.operator("chstage.import_vrm_springs", icon="PHYSICS")
        layout.template_list("CHSTAGE_UL_chains", "", scene, "chstage_chains", scene, "chstage_chains_index", rows=3)
        if scene.chstage_chains:
            chain = scene.chstage_chains[min(scene.chstage_chains_index, len(scene.chstage_chains) - 1)]
            box = layout.box()
            row = box.row(align=True)
            row.prop(chain, "preset", text="")
            row.operator("chstage.chain_preset", text="Apply")
            row.operator("chstage.chain_remove", text="", icon="X")
            box.prop(chain, "bend")
            box.prop(chain, "all_colliders")
            if not chain.all_colliders:
                box.prop(chain, "colliders")
            box.operator("chstage.chain_assign", icon="GROUP_VERTEX")
        bone = rig.data.bones.active
        if bone is not None and bone.chstage_joint.enabled:
            box = layout.box()
            box.label(text=f"Joint {bone.name} ({bone.chstage_joint.chain})", icon="BONE_DATA")
            joint = bone.chstage_joint
            for field in (
                "stiffness",
                "drag",
                "gravity",
                "cone",
                "planes",
                "mass",
                "friction",
                "damping",
                "speed",
                "radius",
            ):
                box.prop(joint, field)
        layout.label(text=f"Colliders ({len(colliders(scene))})")
        row = layout.row(align=True)
        row.operator("chstage.fit_colliders", icon="MESH_CAPSULE")
        row.operator("chstage.add_collider", icon="ADD")
        obj = context.object
        if obj is not None and obj.chstage_collider.enabled:
            box = layout.box()
            box.label(text=f"{obj.name} on {obj.parent_bone}", icon="MESH_CAPSULE")
            box.prop(obj.chstage_collider, "label")
            box.prop(obj.chstage_collider, "radius")
            box.prop(obj.chstage_collider, "length")


CLASSES = (
    CHSTAGE_PG_joint,
    CHSTAGE_PG_chain,
    CHSTAGE_PG_collider,
    CHSTAGE_OT_chain_from_bones,
    CHSTAGE_OT_chain_from_strand,
    CHSTAGE_OT_chain_assign,
    CHSTAGE_OT_chain_preset,
    CHSTAGE_OT_chain_remove,
    CHSTAGE_OT_add_collider,
    CHSTAGE_OT_fit_colliders,
    CHSTAGE_OT_import_mmd_physics,
    CHSTAGE_OT_import_vrm_springs,
    CHSTAGE_UL_chains,
    CHSTAGE_PT_character_chains,
)


def register():
    for cls in CLASSES:
        bpy.utils.register_class(cls)
    bpy.types.Bone.chstage_joint = PointerProperty(type=CHSTAGE_PG_joint)
    bpy.types.Object.chstage_collider = PointerProperty(type=CHSTAGE_PG_collider)
    bpy.types.Scene.chstage_chains = CollectionProperty(type=CHSTAGE_PG_chain)
    bpy.types.Scene.chstage_chains_index = IntProperty()


def unregister():
    del bpy.types.Scene.chstage_chains_index
    del bpy.types.Scene.chstage_chains
    del bpy.types.Object.chstage_collider
    del bpy.types.Bone.chstage_joint
    for cls in reversed(CLASSES):
        bpy.utils.unregister_class(cls)
