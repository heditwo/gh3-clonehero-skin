#!/usr/bin/env python3
"""Export the owner's GH3 hair joints, limits, and head collision capsules."""

import argparse
import json
from pathlib import Path

import numpy as np
from gh3_band_formats import quaternion, rotation, skeleton
from gh3_direction import Source
from gh3_formats import find_array, key
from gh3_havok import Ragdoll


MEMBERS = [
    ("singer", "GH3_singer_male", 0x8efb7275),
    ("guitarist", "GH3_guitarist_axel", 0xeaebb800),
    ("bassist", "GH3_bassist", 0xfd531c30),
    ("drummer", "GH3_drummer", 0xd6b5340d),
]


def convert(data_directory, output):
    source = Source(data_directory)
    entries = source.load("ZONES/global.pak.xen")
    rigs = {n: b for k, n, b in entries if k == key(".ske")}
    ragdolls = {n: b for k, n, b in entries if k == key(".rag")}
    scripts = source.scripts()
    mapping = scripts[key("scripts\\game\\skeleton_mapping.qb")]
    accessories = set(find_array(mapping, "guitarist_accessory_bones"))
    result = {"version": 1, "members": {}, "sources": source.hashes}
    reflect = np.diag([1, 1, -1, 1])
    for role, rig, filename in MEMBERS:
        names, parents, positions, rotations = skeleton(rigs[key(rig)])
        world = {}

        def matrix(i):
            if i not in world:
                local = np.eye(4)
                local[:3, :3] = rotation(rotations[i] * [-1, -1, -1, 1])
                local[:3, 3] = positions[i]
                world[i] = matrix(parents[i]) @ local if parents[i] >= 0 else local
            return world[i]

        rag = Ragdoll(ragdolls[filename])
        bodies = {key(rag.field(p, c, "name")): p for p, c in rag.types.items() if c == "hkRigidBody"}
        joints = {}
        for at, cls in rag.types.items():
            if cls != "hkConstraintInstance": continue
            a, b = rag.field(at, cls, "entities")
            child, parent = (key(rag.field(p, "hkRigidBody", "name")) for p in (a, b))
            if child not in accessories or child not in names or parent not in names: continue
            data = rag.field(at, cls, "data")
            if rag.types[data] != "hkRagdollConstraintData": raise ValueError("Unsupported hair joint")
            atoms, ac = rag.field(data, rag.types[data], "atoms")
            limits = {}
            for field in ["twistLimit", "coneLimit", "planesLimit"]:
                atom, atom_class = rag.field(atoms, ac, field)
                limits[field] = [rag.field(atom, atom_class, f) for f in ["minAngle", "maxAngle"]]
            atom, atom_class = rag.field(atoms, ac, "angFriction")
            friction = rag.field(atom, atom_class, "maxFrictionTorque")
            motion, mc = rag.field(a, "hkRigidBody", "motion")
            state, sc = rag.field(motion, mc, "motionState")
            joints[child] = {"bone": names.index(child), "parent": names.index(parent), "limits": limits,
                             "mass": 1 / rag.field(motion, mc, "inertiaAndMassInv")[3], "friction": friction,
                             "angularDamping": rag.field(state, sc, "angularDamping")}
        head = names.index(key("bone_head"))
        selected = []
        pending = dict(joints)
        selected_bones = {head}
        while pending:
            ready = [k for k, j in pending.items() if j["parent"] in selected_bones]
            if not ready: break
            for name in ready:
                joint = pending.pop(name)
                selected.append(joint)
                selected_bones.add(joint["bone"])
        for joint in selected:
            i, parent = joint["bone"], joint["parent"]
            local = reflect @ np.linalg.inv(matrix(parent)) @ matrix(i) @ reflect
            joint["position"] = local[:3, 3].tolist()
            joint["rotation"] = quaternion(local).tolist()
            children = [j["bone"] for j in selected if j["parent"] == i]
            length = np.linalg.norm((np.linalg.inv(matrix(i)) @ matrix(children[0]))[:3, 3]) if children else 0
            if not children:
                shape, shape_class = rag.field(bodies[names[i]], "hkRigidBody", "collidable")
                shape = rag.field(shape, shape_class, "shape")
                shape_class = rag.types[shape]
                if shape_class == "hkConvexTranslateShape":
                    shape, shape_class = rag.field(shape, shape_class, "childShape")
                    shape = rag.field(shape, shape_class, "childShape")
                    shape_class = rag.types[shape]
                if shape_class == "hkBoxShape":
                    length = 2 * rag.field(shape, shape_class, "halfExtents")[0]
                else:
                    raise ValueError("Unsupported hair collision shape")
            if not 0.005 < length < 1: raise ValueError("Invalid hair segment length")
            joint["length"] = float(length)
        head_body = bodies[names[head]]
        motion, mc = rag.field(head_body, "hkRigidBody", "motion")
        state, sc = rag.field(motion, mc, "motionState")
        body_matrix = np.array(rag.field(state, sc, "transform")).reshape(4, 4).T
        shape, cls = rag.field(head_body, "hkRigidBody", "collidable")
        shape = rag.field(shape, cls, "shape")
        cls = rag.types[shape]
        if cls != "hkCapsuleShape": raise ValueError("Unsupported head collision shape")
        capsule = {"radius": rag.field(shape, cls, "radius")}
        for field, name in [("vertexA", "a"), ("vertexB", "b")]:
            v = np.array([*rag.field(shape, cls, field)[:3], 1])
            capsule[name] = (reflect @ np.linalg.inv(matrix(head)) @ body_matrix @ v)[:3].tolist()
        result["members"][role] = {"head": head, "joints": selected, "capsule": capsule, "ragdoll": f"{filename:08x}"}
        print(f"{role}: {len(selected)} original hair joints")
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, indent=2, allow_nan=False) + "\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data", type=Path, default=Path.home() / "games/gh3-test-tools/game/DATA")
    parser.add_argument("--output", type=Path, default=Path(__file__).parent / "assets/stages/budokan/band/hair.json")
    args = parser.parse_args()
    convert(args.data, args.output)
