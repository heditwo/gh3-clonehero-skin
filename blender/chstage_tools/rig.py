"""GH3 rig poses for camera bone anchors: the joint frames of a library band model (band/<role>/model.glb) in its first
clip, relative to the performer root, in Unity runtime coordinates. No bpy; the tests use it without Blender.

The game places a camera with the anchor '<role>.head' (and the other bone anchors) relative to the animated bone
(StageBand.Anchor: bone world position and rotation). The add-on shows it relative to the bone in the first frame of
the first clip of the library model, the pose that the proxy rig in Blender has.
"""

import numpy as np

from . import glb_tools
from .sidecars import quaternion_matrix

# glTF (right-handed) to Unity runtime (left-handed): negate Z.
FLIP = np.diag([1., 1, -1])


def accessor(gltf, binary, index):
    item = gltf["accessors"][index]
    view = gltf["bufferViews"][item["bufferView"]]
    count = glb_tools.COMPONENT_COUNT[item["type"]]
    dtype = {5126: "<f4", 5125: "<u4", 5123: "<u2", 5121: "<u1", 5122: "<i2", 5120: "<i1"}[item["componentType"]]
    size = np.dtype(dtype).itemsize * count
    stride = view.get("byteStride", size)
    start = view.get("byteOffset", 0) + item.get("byteOffset", 0)
    rows = np.lib.stride_tricks.as_strided(np.frombuffer(binary, np.uint8, offset=start, count=(item["count"] - 1) * stride + size),
                                           shape=(item["count"], size), strides=(stride, 1))
    return rows.copy().view(dtype).reshape(item["count"], count).astype(float)


def trs(node):
    if "matrix" in node: return np.asarray(node["matrix"], dtype=float).reshape(4, 4).T
    t = node.get("translation", [0, 0, 0]); r = node.get("rotation", [0, 0, 0, 1]); s = node.get("scale", [1, 1, 1])
    return compose(t, r, s)


def compose(t, r, s):
    m = np.eye(4)
    m[:3, :3] = quaternion_matrix(r) * np.asarray(s, dtype=float)
    m[:3, 3] = t
    return m


def first_pose(gltf, binary, clip=None):
    """Local TRS of each node in the first key of a clip (default: the first animation). Returns {node index: (t, r, s)}."""
    nodes = gltf.get("nodes", [])
    pose = {i: [n.get("translation", [0, 0, 0]), n.get("rotation", [0, 0, 0, 1]), n.get("scale", [1, 1, 1])] for i, n in enumerate(nodes) if "matrix" not in n}
    animations = gltf.get("animations", [])
    animation = next((a for a in animations if a.get("name") == clip), animations[0] if animations else None)
    if animation is None: return pose
    for channel in animation["channels"]:
        node, path = channel["target"].get("node"), channel["target"]["path"]
        if node not in pose or path not in ("translation", "rotation", "scale"): continue
        values = accessor(gltf, binary, animation["samplers"][channel["sampler"]]["output"])
        pose[node][("translation", "rotation", "scale").index(path)] = list(values[0])
    return pose


def joint_frames(data, clip=None):
    """Runtime frames of the named nodes of a GLB in the first key of a clip, relative to the GLB scene root (the
    performer root): {name: (position, 3x3 rotation)}. Scale is removed."""
    gltf, binary = glb_tools.read_glb(data)
    nodes = gltf.get("nodes", [])
    pose = first_pose(gltf, binary, clip)
    frames = {}

    def visit(index, parent):
        node = nodes[index]
        local = compose(*pose[index]) if index in pose else trs(node)
        world = parent @ local
        if node.get("name"):
            rotation = world[:3, :3] / np.linalg.norm(world[:3, :3], axis=0)
            frames[node["name"]] = (FLIP @ world[:3, 3], FLIP @ rotation @ FLIP)
        for child in node.get("children", []): visit(child, world)

    scene = gltf.get("scenes", [{}])[gltf.get("scene", 0)] if gltf.get("scenes") else {"nodes": list(range(len(nodes)))}
    for root in scene.get("nodes", []): visit(root, np.eye(4))
    return frames


def anchor_frames(data, clip=None):
    """The frames of the anchor bones that a camera can use: {joint name: [position (3), rotation (9, row order)]} for
    every 'bone-*' joint. JSON data, so the scene can store it."""
    return {name: [*map(float, position), *map(float, rotation.reshape(-1))]
            for name, (position, rotation) in joint_frames(data, clip).items() if name.startswith("bone-")}


def frame_of(stored):
    """(position, rotation) of a stored anchor frame."""
    values = np.asarray(stored, dtype=float)
    return values[:3], values[3:12].reshape(3, 3)
