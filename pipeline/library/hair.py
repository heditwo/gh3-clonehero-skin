"""Export the owner's GH3 hair joints, limits, and head collision capsules."""

import json

import numpy as np

from pipeline.gh3.band_formats import quaternion, rotation, skeleton
from pipeline.gh3.direction import Source
from pipeline.gh3.formats import find_array, key
from pipeline.gh3.havok import Ragdoll

MEMBERS = [
    ("singer", "GH3_singer_male", 0x8efb7275),
    ("guitarist", "GH3_guitarist_axel", 0xeaebb800),
    ("bassist", "GH3_bassist", 0xfd531c30),
    ("drummer", "GH3_drummer", 0xd6b5340d),
]
REFLECT = np.diag([1, 1, -1, 1])
JOINT_TYPES = {"hkRagdollConstraintData": "ragdoll", "hkLimitedHingeConstraintData": "hinge", "hkPrismaticConstraintData": "prismatic"}


def accessory_bones(scripts):
    return set(find_array(scripts[key("scripts\\game\\skeleton_mapping.qb")], "guitarist_accessory_bones"))


def havok_matrix(values):
    """A Havok hkTransform (16 floats, column-major) as a row-major 4 x 4 matrix."""
    return np.array(values).reshape(4, 4).T


def body_matrix(rag, body):
    motion, mc = rag.field(body, "hkRigidBody", "motion")
    state, sc = rag.field(motion, mc, "motionState")
    return havok_matrix(rag.field(state, sc, "transform"))


def body_shape(rag, body):
    """The collision shape of a rigid body without its translate wrappers: (address, class)."""
    shape, shape_class = rag.field(body, "hkRigidBody", "collidable")
    shape = rag.field(shape, shape_class, "shape")
    shape_class = rag.types[shape]
    if shape_class == "hkConvexTranslateShape":
        shape, shape_class = rag.field(shape, shape_class, "childShape")
        shape = rag.field(shape, shape_class, "childShape")
        shape_class = rag.types[shape]
    return shape, shape_class


def ragdoll_member(rag, rig, accessories, all_chains=False):
    """Hair joints and head capsule of one ragdoll on its skeleton (runtime coordinates).

    Without `all_chains` (the library band members): ragdoll joints in the chains below the head only. With it (the
    playable characters): every accessory chain (head, chest, pelvis, legs, arms), with the hinge and prismatic joints of
    the ragdoll as their own joint types."""
    names, parents, positions, rotations = rig
    world = {}

    def matrix(i):
        if i not in world:
            local = np.eye(4)
            local[:3, :3] = rotation(rotations[i] * [-1, -1, -1, 1])
            local[:3, 3] = positions[i]
            world[i] = matrix(parents[i]) @ local if parents[i] >= 0 else local
        return world[i]

    bodies = {key(rag.field(p, c, "name")): p for p, c in rag.types.items() if c == "hkRigidBody"}
    joints = {}
    for at, cls in rag.types.items():
        if cls != "hkConstraintInstance": continue
        a, b = rag.field(at, cls, "entities")
        child, parent = (key(rag.field(p, "hkRigidBody", "name")) for p in (a, b))
        if child not in accessories or child not in names or parent not in names: continue
        data = rag.field(at, cls, "data")
        kind = JOINT_TYPES.get(rag.types[data]) if all_chains else "ragdoll" if rag.types[data] == "hkRagdollConstraintData" else None
        if kind is None: raise ValueError("Unsupported hair joint")
        atoms, ac = rag.field(data, rag.types[data], "atoms")
        motion, mc = rag.field(a, "hkRigidBody", "motion")
        state, sc = rag.field(motion, mc, "motionState")
        joint = {"bone": names.index(child), "parent": names.index(parent)}
        if kind == "ragdoll":
            limits = {}
            for field in ["twistLimit", "coneLimit", "planesLimit"]:
                atom, atom_class = rag.field(atoms, ac, field)
                limits[field] = [rag.field(atom, atom_class, f) for f in ["minAngle", "maxAngle"]]
            joint["limits"] = limits
            atom, atom_class = rag.field(atoms, ac, "angFriction")
            friction = rag.field(atom, atom_class, "maxFrictionTorque")
        else:
            atom, atom_class = rag.field(atoms, ac, "transforms")
            frame_a = body_matrix(rag, a) @ havok_matrix(rag.field(atom, atom_class, "transformA"))
            frame_b = body_matrix(rag, b) @ havok_matrix(rag.field(atom, atom_class, "transformB"))
            if kind == "hinge":
                atom, atom_class = rag.field(atoms, ac, "angLimit")
                axis = rag.field(atom, atom_class, "limitAxis")
                low, high = (rag.field(atom, atom_class, f) for f in ("minAngle", "maxAngle"))
                # Havok measures the hinge angle between the frames; the runtime measures it from the bind pose.
                other = (axis + 1) % 3
                rest = float(np.arctan2(np.dot(np.cross(frame_a[:3, other], frame_b[:3, other]), frame_a[:3, axis]),
                                        np.dot(frame_a[:3, other], frame_b[:3, other])))
                friction_atom, friction_class = rag.field(atoms, ac, "angFriction")
                friction = rag.field(friction_atom, friction_class, "maxFrictionTorque")
            else:
                atom, atom_class = rag.field(atoms, ac, "linLimit")
                axis = rag.field(atom, atom_class, "axisIndex")
                low, high = (rag.field(atom, atom_class, f) for f in ("min", "max"))
                rest = float(np.dot(frame_a[:3, 3] - frame_b[:3, 3], frame_b[:3, axis]))
                friction_atom, friction_class = rag.field(atoms, ac, "friction")
                friction = rag.field(friction_atom, friction_class, "maxFrictionForce")
                joint["linearDamping"] = rag.field(state, sc, "linearDamping")
            direction = (REFLECT[:3, :3] @ np.linalg.inv(matrix(names.index(child)))[:3, :3] @ frame_b[:3, axis])
            joint["type"] = kind
            joint["axis"] = (direction / np.linalg.norm(direction)).tolist()
            joint["range"] = [float(low - rest), float(high - rest)]
        joint.update({"mass": 1 / rag.field(motion, mc, "inertiaAndMassInv")[3], "friction": friction,
                      "angularDamping": rag.field(state, sc, "angularDamping")})
        joints[child] = joint
    head = names.index(key("bone_head"))
    selected = []
    pending = dict(joints)
    selected_bones = {i for i in range(len(names)) if names[i] not in accessories} if all_chains else {head}
    while pending:
        ready = [k for k, j in pending.items() if j["parent"] in selected_bones]
        if not ready: break
        for name in ready:
            joint = pending.pop(name)
            selected.append(joint)
            selected_bones.add(joint["bone"])
    for joint in selected:
        i, parent = joint["bone"], joint["parent"]
        local = REFLECT @ np.linalg.inv(matrix(parent)) @ matrix(i) @ REFLECT
        joint["position"] = local[:3, 3].tolist()
        joint["rotation"] = quaternion(local).tolist()
        children = [j["bone"] for j in selected if j["parent"] == i]
        length = np.linalg.norm((np.linalg.inv(matrix(i)) @ matrix(children[0]))[:3, 3]) if children else 0
        if not children:
            shape, shape_class = body_shape(rag, bodies[names[i]])
            if shape_class == "hkBoxShape":
                length = 2 * rag.field(shape, shape_class, "halfExtents")[0]
            elif shape_class == "hkCapsuleShape" and all_chains:
                ends = [np.array(rag.field(shape, shape_class, f)[:3]) for f in ("vertexA", "vertexB")]
                length = np.linalg.norm(ends[1] - ends[0]) + 2 * rag.field(shape, shape_class, "radius")
            else:
                raise ValueError("Unsupported hair collision shape")
        if not 0.005 < length < 1: raise ValueError("Invalid hair segment length")
        joint["length"] = float(length)
        if all_chains:
            # The fields in the order of the library rows, then the joint type fields.
            order = ["bone", "parent", "limits", "mass", "friction", "angularDamping", "linearDamping", "position", "rotation", "length", "type", "axis", "range"]
            joint_copy = {field: joint[field] for field in order if field in joint}
            joint.clear(); joint.update(joint_copy)
    head_body = bodies[names[head]]
    shape, cls = rag.field(head_body, "hkRigidBody", "collidable")
    shape = rag.field(shape, cls, "shape")
    cls = rag.types[shape]
    if cls != "hkCapsuleShape": raise ValueError("Unsupported head collision shape")
    capsule = {"radius": rag.field(shape, cls, "radius")}
    for field, name in [("vertexA", "a"), ("vertexB", "b")]:
        v = np.array([*rag.field(shape, cls, field)[:3], 1])
        capsule[name] = (REFLECT @ np.linalg.inv(matrix(head)) @ body_matrix(rag, head_body) @ v)[:3].tolist()
    return {"head": head, "joints": selected, "capsule": capsule}


def convert(data_directory, output):
    source = Source(data_directory)
    entries = source.load("ZONES/global.pak.xen")
    rigs = {n: b for k, n, b in entries if k == key(".ske")}
    ragdolls = {n: b for k, n, b in entries if k == key(".rag")}
    accessories = accessory_bones(source.scripts())
    result = {"version": 1, "members": {}, "sources": source.hashes}
    for role, rig, filename in MEMBERS:
        member = ragdoll_member(Ragdoll(ragdolls[filename]), skeleton(rigs[key(rig)]), accessories)
        result["members"][role] = {**member, "ragdoll": f"{filename:08x}"}
        print(f"{role}: {len(member['joints'])} original hair joints")
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, indent=2, allow_nan=False) + "\n")
