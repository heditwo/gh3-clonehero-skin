"""The walking guitarist of a GH3 body recording: the pelvis bone path and the leg and spine joint rotations.

Usage: gh3walk.py <extract.py output> <character library profile> <out.npz>
"""

import sys

import numpy as np

sys.path.insert(0, str(__import__("pathlib").Path(__file__).resolve().parents[1] / "hair-motion"))
import hairlib as H

from pipeline.gh3.formats import key

legs, profile, out = sys.argv[1], sys.argv[2], sys.argv[3]
prof = H.Profile(profile)
z = np.load(legs)
t, m, names = z["t"], z["m"], [str(n) for n in z["names"]]
pos = m[:, :, 12:15]
pel = [i for i, n in enumerate(names) if n == "Bone_Pelvis"]
travel = [np.linalg.norm(pos[:, i, [0, 2]] - pos[0, i, [0, 2]], axis=1).max() for i in pel]
p0 = pel[int(np.argmax(travel))]
P = pos[:, p0]
idx = {}
for i, n in enumerate(names):
    if pos[:, i].std(0).max() > 1e-3 and np.linalg.norm(pos[:, i] - P, axis=1).max() < 1.2:
        idx.setdefault(key(n), i)
ch = np.r_[True, np.any(np.abs(np.diff(P, axis=0)) > 0, axis=1)]
t = t[ch] - t[ch][0]
m = m[ch]
M = m.reshape(len(t), m.shape[1], 4, 4).transpose(0, 1, 3, 2)
bi = {n: i for i, n in enumerate(prof.bones)}


def world(name):
    k = key(name)
    return M[:, idx[k]] @ np.linalg.inv(prof.body[k]) @ prof.world[bi[k]]


pelvis = world("Bone_Pelvis")
# Model forward (+z of the object) expressed in the pelvis rest frame: heading = yaw of that axis in the world.
rest = prof.world[bi[key("Bone_Pelvis")]][:3, :3]
fwd_local = rest.T @ np.array([0, 0, 1.0])
f = pelvis[:, :3, :3] @ fwd_local
heading = np.degrees(np.arctan2(f[:, 0], f[:, 2]))
pairs = [
    ("Bone_Pelvis", "Bone_Thigh_L"),
    ("Bone_Pelvis", "Bone_Thigh_R"),
    ("Bone_Thigh_L", "Bone_Knee_L"),
    ("Bone_Thigh_R", "Bone_Knee_R"),
    ("Bone_Knee_L", "Bone_Ankle_L"),
    ("Bone_Knee_R", "Bone_Ankle_R"),
    ("Bone_Pelvis", "Bone_Stomach_Lower"),
    ("Bone_Stomach_Lower", "Bone_Stomach_Upper"),
    ("Bone_Stomach_Upper", "Bone_Chest"),
    ("Bone_Chest", "Bone_Neck"),
    ("Bone_Neck", "Bone_Head"),
]
pairs = [(a, c) for a, c in pairs if key(a) in idx and key(c) in idx]
W = {n: world(n) for n in {x for p in pairs for x in p}}
rel = np.stack([np.swapaxes(W[a][:, :3, :3], -1, -2) @ W[c][:, :3, :3] for a, c in pairs], 1)
np.savez_compressed(
    out, t=t, pelvis=pelvis[:, :3, 3], pelvis_world=pelvis, heading=heading, rel=rel, pairs=np.array(pairs)
)
d = np.linalg.norm(pelvis[:, [0, 2], 3] - pelvis[0, [0, 2], 3], axis=1)
sp = np.r_[0, np.linalg.norm(np.diff(pelvis[:, [0, 2], 3], axis=0), axis=1) / np.maximum(np.diff(t), 1e-4)]
print("samples", len(t), "pairs", len(pairs))
for k in range(0, len(t), max(1, len(t) // 400)):
    print(f"{t[k]:7.2f} x={pelvis[k, 0, 3]:7.3f} z={pelvis[k, 2, 3]:7.3f} heading={heading[k]:7.1f} speed={sp[k]:5.2f}")
