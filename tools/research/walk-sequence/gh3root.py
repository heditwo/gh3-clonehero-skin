"""The GH3 object root (position, yaw) at song times of a body recording: the pelvis world pose times the inverse of
the pelvis model pose of the best clip frame of the set (by the joint angles).

Usage: gh3root.py <gh3walk.py output> <profile> <skeleton> <anim PAKs, comma> <song offset> <song times, comma> [scale]
"""

import sys

import numpy as np

sys.path.insert(0, str(__import__("pathlib").Path(__file__).resolve().parents[1] / "hair-motion"))
import hairlib as H

from pipeline.gh3.band_formats import animation, clip_bone_count, sample_animation, skeleton
from pipeline.gh3.data import Source
from pipeline.gh3.formats import key

walk, profile, skel, paks, off, times = (
    sys.argv[1],
    sys.argv[2],
    sys.argv[3],
    sys.argv[4].split(","),
    float(sys.argv[5]),
    [float(x) for x in sys.argv[6].split(",")],
)
scale = float(sys.argv[7]) if len(sys.argv) > 7 else 1.0
g = np.load(walk, allow_pickle=True)
gt, PW, grel = g["t"] + off, g["pelvis_world"], g["rel"]
pairs = [tuple(p) for p in g["pairs"]]
src = Source(H.DATA)
rigs = {n: b for k, n, b in src.load("ZONES/global.pak.xen") if k == key(".ske")}
A = skeleton(rigs[key(skel)])
n = len(A[0])
parents = A[1]
bank = {}
for pk in paks:
    for k, nm, b in src.load(f"PAK/{pk}.pak.xen"):
        if k == key(".ska") and clip_bone_count(b) == n:
            bank.setdefault(nm, b)
idx = {b: i for i, b in enumerate(A[0])}
pel = idx[key("Bone_Pelvis")]


def model(p, q):
    T = np.zeros(p.shape[:2] + (4, 4))
    T[..., 3, 3] = 1
    L = T.copy()
    L[..., :3, :3] = H.qmat(q * [-1, -1, -1, 1])
    L[..., :3, 3] = p
    for i in range(n):
        T[:, i] = T[:, parents[i]] @ L[:, i] if parents[i] >= 0 else L[:, i]
    return T


frames_rel, frames_pel, owner = [], [], []
for nm, b in bank.items():
    try:
        c = animation(b, n)
    except ValueError:
        continue
    if c[3] or not c[1]:
        continue
    tt = np.arange(0, max(c[0], 1 / 30), 1 / 30)
    p, q = sample_animation(c, A[2], A[3], tt)
    T = model(p, q)
    T[:, :, :3, 3] -= T[:, :1, :3, 3]  # motion extraction: the root bone translation moves the object
    frames_rel.append(
        np.stack([np.swapaxes(T[:, idx[key(a)], :3, :3], -1, -2) @ T[:, idx[key(c2)], :3, :3] for a, c2 in pairs], 1)
    )
    T[:, :, :3, 3] *= scale
    frames_pel.append(T[:, pel])
    owner += [(nm, x) for x in tt]
frames_rel = np.concatenate(frames_rel)
frames_pel = np.concatenate(frames_pel)


def ang(X, Y):
    D = np.swapaxes(X, -1, -2) @ Y
    return np.degrees(np.arccos(np.clip((np.trace(D, axis1=-2, axis2=-1) - 1) / 2, -1, 1)))


for t in times:
    j = int(np.argmin(abs(gt - t)))
    e = ang(frames_rel, grel[j][None]).mean(1)
    k = int(np.argmin(e))
    root = PW[j] @ np.linalg.inv(frames_pel[k])
    f = root[:3, 2]
    print(
        f"song {t:7.2f}: best clip {owner[k][0]:08x} at {owner[k][1]:.2f} (joint error {e[k]:.1f} deg); GH3 root ({root[0, 3]:.3f}, {root[2, 3]:.3f}) yaw {np.degrees(np.arctan2(f[0], f[2])):.1f}; runtime ({root[0, 3]:.3f}, {-root[2, 3]:.3f})"
    )
