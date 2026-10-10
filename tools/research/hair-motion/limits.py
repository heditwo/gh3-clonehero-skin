"""Havok ragdoll constraint angles (cone, plane, twist) of a live GH3 ragdoll against its limits."""

import json
import sys

import numpy as np
from hairlib import ragdolls

sys.path.insert(0, str(__import__("pathlib").Path(__file__).resolve().parents[3]))
from pipeline.gh3.havok import Ragdoll
from pipeline.library.hair import body_matrix, havok_matrix

run, rag_id, gx, gz = sys.argv[1], int(sys.argv[2], 16), float(sys.argv[3]), float(sys.argv[4])
b = json.load(open(f"{run}/bodies.json"))
z = np.load(f"{run}/bodies.npz")
t = z["t"]
m = z["m"]
r = Ragdoll(ragdolls()[rag_id])
pos = m[:, :, 12:15]
live = [i for i, x in enumerate(b) if x["addr"] >= 0x30000000 and pos[:, i].std(0).max() > 1e-3]
idx = {}
for i in live:
    if abs(np.median(pos[:, i, 0]) - gx) < 0.6 and abs(np.median(pos[:, i, 2]) - gz) < 0.6:
        idx.setdefault(b[i]["name"], i)
head = idx["Bone_Head"]
ch = np.r_[True, np.any(np.abs(np.diff(pos[:, head], axis=0)) > 0, axis=1)]
m = m[ch]
t = t[ch] - t[ch][0]
M = np.zeros((len(t), m.shape[1], 4, 4))
M[:, :, :, :] = m.reshape(len(t), m.shape[1], 4, 4).transpose(0, 1, 3, 2)
fileb = {r.field(p, c, "name"): body_matrix(r, p) for p, c in r.types.items() if c == "hkRigidBody"}


def ang(u, v):
    return np.degrees(np.arccos(np.clip(np.sum(u * v, -1), -1, 1)))


print(
    "%-12s %-6s | rest: cone plane twistref | cone lim | plane lim | live cone p50/p95/max | live plane p5/p95"
    % ("joint", "")
)
res = {}
for at, cls in r.types.items():
    if cls != "hkConstraintInstance":
        continue
    a, bb = r.field(at, cls, "entities")
    child = r.field(a, "hkRigidBody", "name")
    parent = r.field(bb, "hkRigidBody", "name")
    if "ACC" not in child:
        continue
    data = r.field(at, cls, "data")
    if r.types[data] != "hkRagdollConstraintData":
        print(child, r.types[data])
        continue
    atoms, ac = r.field(data, r.types[data], "atoms")
    tr, tc = r.field(atoms, ac, "transforms")
    TA, TB = havok_matrix(r.field(tr, tc, "transformA")), havok_matrix(r.field(tr, tc, "transformB"))
    lim = {}
    for f in ["twistLimit", "coneLimit", "planesLimit"]:
        la, lc = r.field(atoms, ac, f)
        lim[f] = dict(min=np.degrees(r.field(la, lc, "minAngle")), max=np.degrees(r.field(la, lc, "maxAngle")))
        for g in ["twistAxis", "refAxis", "twistAxisInA", "refAxisInB"]:
            try:
                lim[f][g] = r.unpack(
                    r.field(la, lc, g) if False else (la[0], la[1] + r.classes[lc]["fields"][g][2]), "B"
                )[0]
            except Exception:
                pass

    # Havok ragdoll: frame columns: 0 twist, 1 plane, 2 cone (hkRagdollConstraintData axis order: twist=0, plane=1, cone=2)
    def frames(CA, CB):
        FA = CA @ TA
        FB = CB @ TB
        return FA[..., :3, :3], FB[..., :3, :3]

    FA, FB = frames(fileb[child], fileb[parent])
    tA = FA[:, 0]
    tB = FB[:, 0]
    pB = FB[:, 1]
    rest_cone = ang(tA, tB)
    rest_plane = 90 - ang(tA, pB)
    if child in idx and parent in idx:
        A, B = M[:, idx[child]], M[:, idx[parent]]
        FA2, FB2 = A[:, :3, :3] @ TA[:3, :3], B[:, :3, :3] @ TB[:3, :3]
        cone = ang(FA2[:, :, 0], FB2[:, :, 0])
        plane = 90 - ang(FA2[:, :, 0], FB2[:, :, 1])
        res[child] = (cone, plane)
        live_s = "%5.1f %5.1f %5.1f | %6.1f %6.1f" % (
            np.median(cone),
            np.percentile(cone, 95),
            cone.max(),
            np.percentile(plane, 5),
            np.percentile(plane, 95),
        )
    else:
        live_s = "-"
    print(
        "%-12s %-12s %5.1f %5.1f | %5.1f | %5.1f..%5.1f | %s   axes cone=%s planes=%s"
        % (
            child,
            parent,
            rest_cone,
            rest_plane,
            lim["coneLimit"]["max"],
            lim["planesLimit"]["min"],
            lim["planesLimit"]["max"],
            live_s,
            lim["coneLimit"].get("twistAxisInA"),
            lim["planesLimit"].get("twistAxisInA"),
        )
    )
