"""Custom hair, skirt, and tail chains of a character (character.json bones, colliders, chains; docs/PACKAGE-FORMAT.md
section 9.5): presets, the rows of character.json from an exported character GLB, the mapping of MMD rigid bodies and
joints to the chain model, and collider fits. No bpy: the Blender add-on bundles this module (blender/build.py).

Coordinates: the GLB is glTF (right-handed, Y up). character.json uses the runtime coordinates of the mod: a vector in a
bone frame has z negated, a rotation has x and y negated. Blender world coordinates are Z up: glTF (x, y, z) is Blender
(x, -z, y).
"""

import math
import re

import numpy as np

from .character import Rig
from .glb import Glb
from .validate import bone_key

NAME = re.compile(r"[A-Za-z0-9_.-]{1,64}")
# glTF (Y up) to Blender (Z up).
AXES = np.array([[1.0, 0, 0], [0, 0, -1], [0, 1, 0]])
FLIP = np.array([1.0, 1, -1])
MAX_JOINTS = 64
MAX_COLLIDERS = 32
MAX_STIFFNESS = 5000.0
GRAVITY = 9.81

# Presets of the chain tool. Angles in degrees; friction for each kilogram of mass (N m / kg), so that a heavier joint
# keeps the same braking; damping per second (Havok angular damping, it slows the motion in the world); speed limit in
# radians per second (0: none; a curled chain of many short stiff joints, as a cable tail, can start to whirl without
# it). Stiffness (per second squared) pulls each joint toward its bind direction against its
# parent; the joint at the chain end gets (1 - falloff) of it. Drag (per second) slows the swing of a joint against its
# parent. Bend: the largest angle of the chain end from its bind direction, seen from the chain start (0: none). The
# values come from the naturalness bench (VERIFICATION.md, "Custom chains"): a 1.1 m twin tail hangs, trails the head,
# and settles in about 1 s after one swing, as the VRM spring bone does on the same motion.
PRESETS = {
    "hair": {
        "label": "Hair",
        "description": "A short hair strand or a bang: small swing, quick to settle",
        "mass": 1.0,
        "friction": 0.1,
        "damping": 3.0,
        "cone": 25.0,
        "planes": 0.0,
        "speed": 0.0,
        "radius": 0.01,
        "stiffness": 120.0,
        "falloff": 0.5,
        "drag": 15.0,
        "gravity": 1.0,
        "bend": 45.0,
    },
    "long_hair": {
        "label": "Long hair or twin tail",
        "description": "Long hair or a twin tail: hangs, trails the head, and settles",
        "mass": 1.0,
        "friction": 0.05,
        "damping": 4.0,
        "cone": 30.0,
        "planes": 0.0,
        "speed": 0.0,
        "radius": 0.03,
        "stiffness": 30.0,
        "falloff": 0.67,
        "drag": 15.0,
        "gravity": 1.0,
        "bend": 60.0,
    },
    "skirt": {
        "label": "Skirt",
        "description": "A skirt panel or a coat tail: keeps its shape, swings out, keeps off the legs with the colliders",
        "mass": 1.0,
        "friction": 0.05,
        "damping": 4.0,
        "cone": 35.0,
        "planes": 15.0,
        "speed": 0.0,
        "radius": 0.01,
        "stiffness": 120.0,
        "falloff": 0.5,
        "drag": 15.0,
        "gravity": 1.0,
        "bend": 45.0,
    },
    "tail": {
        "label": "Tail or cable",
        "description": "A tail, a cable, or a long ribbon: many joints, sways",
        "mass": 1.0,
        "friction": 0.1,
        "damping": 3.0,
        "cone": 15.0,
        "planes": 0.0,
        "speed": 8.0,
        "radius": 0.02,
        "stiffness": 80.0,
        "falloff": 0.6,
        "drag": 12.0,
        "gravity": 1.0,
        "bend": 70.0,
    },
    "stiff": {
        "label": "Stiff accessory",
        "description": "A stiff accessory (a pendant, a feather, a strap): very small swing",
        "mass": 3.0,
        "friction": 0.3,
        "damping": 5.0,
        "cone": 5.0,
        "planes": 0.0,
        "speed": 5.0,
        "radius": 0.0,
        "stiffness": 400.0,
        "falloff": 0.0,
        "drag": 20.0,
        "gravity": 1.0,
        "bend": 0.0,
    },
}
PRESET_ITEMS = [(key, row["label"], row["description"]) for key, row in PRESETS.items()]


def preset_stiffness(preset, position):
    """The stiffness of a preset at `position` along the chain (0: the first joint, 1: the last joint)."""
    row = PRESETS[preset]
    return row["stiffness"] * (1 - row["falloff"] * min(max(position, 0.0), 1.0))


def runtime_bind(local):
    """[x, y, z, qx, qy, qz, qw] in runtime coordinates of a glTF local 4 x 4 matrix (unit scale)."""
    from .character import matrix_quaternions

    rotation = local[:3, :3] / np.linalg.norm(local[:3, :3], axis=0)
    q = matrix_quaternions(rotation[None])[0] * [-1, -1, 1, 1]
    return [float(v) for v in (*(local[:3, 3] * FLIP), *q)]


def blender_to_local(world, vector, point=False):
    """A Blender world vector (or point) in the runtime frame of a joint with this glTF world matrix."""
    gltf = AXES.T @ np.asarray(vector, dtype=float)
    inverse = np.linalg.inv(world)
    local = inverse[:3, :3] @ gltf + (inverse[:3, 3] if point else 0)
    return local * FLIP


def joint_row(bone, settings, length, direction, side_axis=None, parent=None):
    """A ragdoll joint row of character.json from chain tool settings (degrees). `direction` and `side_axis` are unit
    vectors in the runtime bone frame; the planes limit the swing toward the side axis."""
    cone = math.radians(min(max(settings["cone"], 0.5), 90.0))
    row = {"bone": bone}
    if parent:
        row["parent"] = parent
    row.update(
        {
            "length": round(float(length), 6),
            "direction": [round(float(v), 6) for v in direction],
            "radius": round(float(settings["radius"]), 4),
            "mass": round(float(settings["mass"]), 4),
            "friction": round(float(settings["friction"]) * float(settings["mass"]), 5),
            "angularDamping": round(float(settings["damping"]), 4),
            "maxAngularVelocity": round(float(settings["speed"]), 3),
            "limits": {"coneLimit": [round(-cone, 6), round(cone, 6)]},
        }
    )
    if settings.get("stiffness", 0) > 0:
        row["stiffness"] = round(min(float(settings["stiffness"]), MAX_STIFFNESS), 3)
    if settings.get("drag", 0) > 0:
        row["drag"] = round(float(settings["drag"]), 3)
    if abs(settings.get("gravity", 1.0) - 1) > 1e-6:
        row["gravity"] = round(min(max(float(settings["gravity"]), 0.0), 2.0), 4)
    planes = math.radians(min(max(settings.get("planes", 0.0), 0.0), 90.0))
    low, high = (
        (math.radians(v) for v in settings["planes_range"]) if settings.get("planes_range") else (-planes, planes)
    )
    if high > low and side_axis is not None:
        side = np.asarray(side_axis, dtype=float) - np.dot(side_axis, direction) * np.asarray(direction, dtype=float)
        if np.linalg.norm(side) > 1e-6:
            side = side / np.linalg.norm(side)
            row["limits"]["planesLimit"] = [round(max(low, -math.pi / 2), 6), round(min(high, math.pi / 2), 6)]
            row.update(
                {
                    "twistAxis": row["direction"],
                    "coneAxis": row["direction"],
                    "planeAxis": [round(float(v), 6) for v in side],
                }
            )
    return row


def character_rows(glb_data, profile_names, joints, colliders, chains, own=False):
    """character.json bones, colliders, and chains of an exported character GLB.

    profile_names: the joint names of the profile rig (the GLB joints that are not added bones).
    joints: {bone name: {"settings": preset values, "chain": chain name, "axis": Blender world bone vector (head to
    tail), "side": Blender world side axis of the bone (its X axis)}} for the added bones that are chain joints.
    colliders: [{"name", "bone", "radius", "a", "b"}] with the ends as Blender world points.
    chains: [{"name", "colliders": list of names or None, "bend": bend limit in degrees (0 or absent: none)}] in export order.
    own: a character with its own skeleton (section 9.6): the joints are skeleton joints, and there are no added bones.
    Returns (bones, colliders, chains) rows; an empty dictionary for each part that has no rows."""
    rig = Rig(Glb(glb_data))
    keys = {bone_key(name) for name in profile_names}
    index = {name: i for i, name in enumerate(rig.names)}
    depth = [0] * len(rig.names)
    for i in range(len(rig.names)):
        parent = rig.parents[i]
        while parent >= 0:
            depth[i] += 1
            parent = rig.parents[parent]
    # Parents first: the format needs an added parent bone and a parent joint before their children.
    added = sorted(
        (i for i, name in enumerate(rig.names) if own or bone_key(name) not in keys), key=lambda i: (depth[i], i)
    )
    bones = []
    for i in [] if own else added:
        parent = rig.parents[i]
        if parent < 0:
            raise ValueError(f"the added bone {rig.names[i]} has no parent in the rig")
        bones.append(
            {
                "name": rig.names[i],
                "parent": rig.names[parent],
                "bind": [round(v, 7) for v in runtime_bind(rig.local[i])],
            }
        )
    collider_rows = []
    for collider in colliders:
        world = rig.world[index[collider["bone"]]]
        row = {
            "name": collider["name"],
            "bone": collider["bone"],
            "radius": round(float(collider["radius"]), 5),
            "a": [round(float(v), 5) for v in blender_to_local(world, collider["a"], point=True)],
        }
        if collider.get("b") is not None and np.linalg.norm(np.subtract(collider["a"], collider["b"])) > 1e-6:
            row["b"] = [round(float(v), 5) for v in blender_to_local(world, collider["b"], point=True)]
        collider_rows.append(row)
    chain_rows = []
    order = [rig.names[i] for i in added]
    for chain in chains:
        rows = []
        for name in order:
            joint = joints.get(name)
            if joint is None or joint["chain"] != chain["name"]:
                continue
            world = rig.world[index[name]]
            axis = np.asarray(joint["axis"], dtype=float)
            direction = blender_to_local(world, axis / np.linalg.norm(axis))
            direction /= np.linalg.norm(direction)
            side = blender_to_local(world, joint["side"]) if joint.get("side") is not None else None
            rows.append(joint_row(name, joint["settings"], np.linalg.norm(axis), direction, side))
        if not rows:
            continue
        value = {"name": chain["name"]}
        if chain.get("colliders") is not None:
            value["colliders"] = list(chain["colliders"])
        if chain.get("bend", 0) > 0:
            value["bendLimit"] = round(math.radians(min(float(chain["bend"]), 180.0)), 6)
        value["joints"] = rows
        chain_rows.append(value)
    return bones, collider_rows, chain_rows


def chain_name(text, taken=()):
    """A chain or bone name that the format accepts (letters, digits, '_', '.', '-'), unique among `taken`."""
    base = re.sub(r"[^A-Za-z0-9_.-]+", "_", text).strip("_.-")[:56] or "chain"
    name, number = base, 2
    while name in taken:
        name = f"{base}_{number}"
        number += 1
    return name


def strand_points(points, root, count):
    """`count` + 1 joint points along a strand of points (a twin tail, a skirt panel, a tail), from the end nearest to
    `root` to the far end: the points are ordered along their main axis and the path is cut into equal lengths."""
    points = np.asarray(points, dtype=float)
    if len(points) < 2:
        raise ValueError("select at least two points along the strand")
    centre = points.mean(0)
    axis = np.linalg.svd(points - centre)[2][0]
    order = np.argsort((points - centre) @ axis)
    path = points[order]
    if np.linalg.norm(path[-1] - root) < np.linalg.norm(path[0] - root):
        path = path[::-1]
    # A smooth centre line: the mean of the points in each slice along the axis.
    slices = max(2, min(len(path), 4 * count))
    keys = np.linspace(0, len(path), slices + 1).astype(int)
    line = np.array([path[keys[i] : max(keys[i + 1], keys[i] + 1)].mean(0) for i in range(slices)])
    steps = np.r_[0, np.cumsum(np.linalg.norm(np.diff(line, axis=0), axis=1))]
    targets = np.linspace(0, steps[-1], count + 1)
    return np.array([[np.interp(t, steps, line[:, c]) for c in range(3)] for t in targets])


def segment_radius(points, a, b, share=0.5):
    """The capsule radius for a body segment a-b: the `share` quantile of the distances from the segment of the points
    that project onto it. None when no point does."""
    points = np.asarray(points, dtype=float)
    axis = np.asarray(b, dtype=float) - a
    length = float(axis @ axis)
    if length < 1e-10 or not len(points):
        return None
    t = (points - a) @ axis / length
    inner = (t > -0.05) & (t < 1.05)
    if not inner.any():
        return None
    return float(np.quantile(segment_distances(points[inner], a, b), share))


def segment_distances(points, a, b):
    """The distance of each point from the segment a-b."""
    points = np.asarray(points, dtype=float)
    axis = np.asarray(b, dtype=float) - a
    t = np.clip((points - a) @ axis / max(float(axis @ axis), 1e-12), 0, 1)
    return np.linalg.norm(points - (a + np.outer(t, axis)), axis=1)


def flat_capsule(points):
    """A capsule for a flat part with a body and a neck (a guitar): the half of the points along the main axis that is
    wider is the body. The capsule goes across the body (along the second axis) through its centre; its radius is half
    the thickness and 2 cm, at least a quarter of the body length, at most 0.1 m. Returns (a, b, radius)."""
    points = np.asarray(points, dtype=float)
    centre = points.mean(0)
    _, _, axes = np.linalg.svd(points - centre)
    along = (points - centre) @ axes[0]
    halves = [points[along <= np.median(along)], points[along > np.median(along)]]
    width = [np.ptp(h @ axes[1]) if len(h) else 0 for h in halves]
    body = halves[int(np.argmax(width))]
    centre = body.mean(0)
    local = body - centre
    length = np.quantile(local @ axes[0], 0.95) - np.quantile(local @ axes[0], 0.05)
    thickness = np.quantile(local @ axes[2], 0.95) - np.quantile(local @ axes[2], 0.05)
    radius = float(min(0.1, max(thickness / 2 + 0.02, length / 4)))
    low, high = np.quantile(local @ axes[1], 0.1) + radius, np.quantile(local @ axes[1], 0.9) - radius
    if high < low:
        low = high = (low + high) / 2
    return centre + axes[1] * low, centre + axes[1] * high, radius


# MMD rigid bodies and joints (MMD Tools: mmd_rigid, mmd_joint, Blender rigid body constraints).
MMD_SCALE = 0.08


def mmd_damping(value):
    """Havok angular damping (per second) of an MMD (Bullet) damping fraction: v *= (1 - d) each second."""
    return float(-math.log(max(1e-6, 1 - min(float(value), 0.999))))


def mmd_drag(stiffness, preset_drag):
    """The drag of a joint with an MMD spring: MMD (Bullet) drives its springs as damped motors, so a stiff joint gets a
    drag of 1.4 times the square root of its stiffness (a damping ratio of about 0.7), and at least the preset drag."""
    return float(min(max(preset_drag, 1.4 * math.sqrt(max(stiffness, 0.0))), 100.0))


def mmd_stiffness(spring, mass, length, scale=MMD_SCALE):
    """The stiffness (per second squared) of an MMD joint spring: the rotation spring constant (Bullet units) over the
    inertia of the rigid body about the joint (a rod of `mass` and `length` metres about its end, m L^2 / 3, in MMD
    units: metres / `scale`, the MMD Tools import scale)."""
    units = max(float(length) / scale, 1e-3)
    return float(min(max(float(spring), 0.0) / (max(float(mass), 1e-6) * units * units / 3), MAX_STIFFNESS))


# VRM spring bone (VRM 1.0 VRMC_springBone, VRM 0.x secondaryAnimation). VRM steps once in each rendered frame: the
# inertia keeps (1 - dragForce) of the speed, and stiffness and gravityPower move the joint end by a distance for each
# second. The mapping keeps the hang under gravity and the time to return to the bind direction at 60 frames a second;
# the hair bench gives the VRM tip angles and settle times within a few degrees and 0.1 s (VERIFICATION.md).
VRM_FPS = 60.0


def vrm_joint(stiffness, drag, gravity_power, gravity_dir, length):
    """Chain settings of a VRM spring joint of `length` metres: {"damping", "stiffness", "gravity", "drag"}."""
    drag = min(max(float(drag), 0.0), 0.99)
    length = max(float(length), 0.005)
    damping = -math.log(1 - drag) * VRM_FPS
    rate = damping / drag if drag > 1e-3 else VRM_FPS
    down = -float(np.asarray(gravity_dir, dtype=float)[1]) if gravity_dir is not None else 1.0
    return {
        "damping": min(damping, 100.0),
        "stiffness": min(rate * max(float(stiffness), 0.0) / length, MAX_STIFFNESS),
        "gravity": min(max(rate * max(float(gravity_power), 0.0) * max(down, 0.0) / GRAVITY, 0.0), 2.0),
        "drag": 0.0,
    }


def vrm_springs(document):
    """The springs of the glTF JSON of a VRM file: [{"name", "joints": [{"node", "stiffness", "drag", "gravity_power",
    "gravity_dir", "radius"}], "colliders": [collider indices]}] and the colliders [{"node", "offset", "tail" or None,
    "radius"}] (glTF node frames). VRM 1.0 lists every joint of a spring (the last one is the end); VRM 0.x lists the
    roots of a bone group: each root and the nodes below it make a spring."""
    extensions = document.get("extensions", {})
    nodes = document.get("nodes", [])
    springs, colliders = [], []
    if "VRMC_springBone" in extensions:
        data = extensions["VRMC_springBone"]
        for row in data.get("colliders", []):
            shape = row.get("shape", {})
            if "sphere" in shape:
                colliders.append(
                    {
                        "node": row["node"],
                        "offset": shape["sphere"].get("offset", [0, 0, 0]),
                        "tail": None,
                        "radius": shape["sphere"].get("radius", 0),
                    }
                )
            elif "capsule" in shape:
                colliders.append(
                    {
                        "node": row["node"],
                        "offset": shape["capsule"].get("offset", [0, 0, 0]),
                        "tail": shape["capsule"].get("tail", [0, 0, 0]),
                        "radius": shape["capsule"].get("radius", 0),
                    }
                )
        groups = [group.get("colliders", []) for group in data.get("colliderGroups", [])]
        for index, row in enumerate(data.get("springs", [])):
            joints = [
                {
                    "node": joint["node"],
                    "stiffness": joint.get("stiffness", 1.0),
                    "drag": joint.get("dragForce", 0.5),
                    "gravity_power": joint.get("gravityPower", 0.0),
                    "gravity_dir": joint.get("gravityDir", [0, -1, 0]),
                    "radius": joint.get("hitRadius", 0.0),
                }
                for joint in row.get("joints", [])
            ]
            springs.append(
                {
                    "name": row.get("name") or f"spring_{index + 1:02d}",
                    "joints": joints,
                    "colliders": sorted(
                        {c for g in row.get("colliderGroups", []) if g < len(groups) for c in groups[g]}
                    ),
                }
            )
        return springs, colliders
    secondary = extensions.get("VRM", {}).get("secondaryAnimation", {})
    groups = []
    for row in secondary.get("colliderGroups", []):
        indices = []
        for collider in row.get("colliders", []):
            offset = collider.get("offset", {})
            # VRM 0.x stores the offsets in Unity coordinates (z negated from glTF, as three-vrm reads them).
            colliders.append(
                {
                    "node": row["node"],
                    "offset": [offset.get("x", 0), offset.get("y", 0), -offset.get("z", 0)],
                    "tail": None,
                    "radius": collider.get("radius", 0),
                }
            )
            indices.append(len(colliders) - 1)
        groups.append(indices)
    for index, row in enumerate(secondary.get("boneGroups", [])):
        direction = row.get("gravityDir", {"x": 0, "y": -1, "z": 0})
        settings = {
            "stiffness": row.get("stiffiness", row.get("stiffness", 1.0)),
            "drag": row.get("dragForce", 0.4),
            "gravity_power": row.get("gravityPower", 0.0),
            "gravity_dir": [direction.get("x", 0), direction.get("y", -1), direction.get("z", 0)],
            "radius": row.get("hitRadius", 0.02),
        }
        for number, root in enumerate(row.get("bones", [])):
            path = [root]
            while nodes[path[-1]].get("children"):
                path.append(nodes[path[-1]]["children"][0])
            springs.append(
                {
                    "name": f"{row.get('comment') or 'spring'}_{index + 1:02d}_{number + 1:02d}",
                    "joints": [dict(settings, node=n) for n in path],
                    "colliders": sorted(
                        {c for g in row.get("colliderGroups", []) if g < len(groups) for c in groups[g]}
                    ),
                }
            )
    return springs, colliders


def mmd_limits(direction, frame, limits):
    """The cone and the planes of an MMD joint for a bone that points along `direction` (world). `frame` is the 3 x 3
    world rotation of the joint; `limits` are the rotation limits (x lower, x upper, y lower, y upper, z lower, z upper,
    radians). Returns (cone, planes or None, plane axis in world or None); cone 0 when the joint does not turn."""
    direction = np.asarray(direction, dtype=float) / np.linalg.norm(direction)
    frame = np.asarray(frame, dtype=float)
    twist = int(np.argmax(np.abs(frame.T @ direction)))
    swings = [axis for axis in range(3) if axis != twist]
    ranges = {axis: (float(limits[2 * axis]), float(limits[2 * axis + 1])) for axis in swings}
    spans = {axis: max(abs(low), abs(high)) for axis, (low, high) in ranges.items()}
    wide, narrow = sorted(swings, key=lambda axis: -spans[axis])
    cone = min(spans[wide], math.pi / 2)
    if cone < math.radians(0.5):
        return 0.0, None, None
    if spans[narrow] >= 0.8 * spans[wide]:
        return cone, None, None
    # A turn about the narrow axis moves the twist axis toward cross(narrow axis, twist axis).
    plane = np.cross(frame[:, narrow], direction)
    low, high = ranges[narrow]
    return cone, (max(low, -math.pi / 2), min(high, math.pi / 2)), plane / np.linalg.norm(plane)
