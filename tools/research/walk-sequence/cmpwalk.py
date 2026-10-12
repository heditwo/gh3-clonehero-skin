"""A GH3 walk (gh3walk.py output) against the mod walk (`[trace] walk` lines): pelvis path, timing, and joints by phase.

Usage: cmpwalk.py <gh3walk.py output> <Latest.log> <profile> <walk start, song s> <lowest offset> <highest offset>
"""

import re
import sys

import numpy as np

sys.path.insert(0, str(__import__("pathlib").Path(__file__).resolve().parents[1] / "hair-motion"))
import hairlib as H

from pipeline.gh3.formats import key

gh3_file, log, profile, walk_start = sys.argv[1], sys.argv[2], sys.argv[3], float(sys.argv[4])
prof = H.Profile(profile)
bi = {n: i for i, n in enumerate(prof.bones)}
S = np.diag([1.0, 1.0, -1.0])
g = np.load(gh3_file, allow_pickle=True)
gt, gp, grel = g["t"], g["pelvis"], g["rel"]
pairs = [tuple(p) for p in g["pairs"]]
LINE = re.compile(
    r"\[trace\] walk t=(\S+) role=guitarist clip=(\S+) clipTime=(\S+) root=(\S+),(\S+),(\S+) scale=(\S+) bones=(\S+)"
)
rows = []
for m in LINE.finditer(open(log, errors="replace").read()):
    t = float(m[1])
    x, z, yaw, scale = float(m[4]), float(m[5]), float(m[6]), float(m[7])
    bones = {}
    for item in m[8].split(";"):
        i, v = item.split(":")
        v = [float(a) for a in v.split(",")]
        bones[int(i)] = (np.array(v[:3]), H.qmat(np.array(v[3:])))
    rows.append((t, m[2], x, z, yaw, scale, bones))
walks = [
    (float(m[1]), float(m[2]))
    for m in re.finditer(
        r"GH3 band guitarist: walk at (\S+) s, .*? turn end (\S+) s", open(log, errors="replace").read()
    )
]
end = next(e for s0, e in walks if abs(s0 - walk_start) < 0.01)
rows = [r for r in rows if walk_start - 1 <= r[0] <= end + 1]
mt = np.array([r[0] for r in rows])
mclip = [r[1] for r in rows]


def yaw_matrix(deg):
    a = np.radians(deg)
    c, s = np.cos(a), np.sin(a)
    return np.array([[c, 0, s], [0, 1, 0], [-s, 0, c]])


pel = bi[key("Bone_Pelvis")]
mp, mrel, mR = [], [], []
for t, clip, x, z, yaw, scale, bones in rows:
    R = yaw_matrix(yaw)
    pos, rot = bones[pel]
    world = np.array([x, 0, z]) + R @ pos * scale
    mp.append(S @ world)
    rot_world = S @ (R @ rot) @ S
    mR.append(rot_world)
    mrel.append([S @ (bones[bi[key(a)]][1].T @ bones[bi[key(c)]][1]) @ S for a, c in pairs])
mp, mrel, mR = np.array(mp), np.array(mrel), np.array(mR)
rest = prof.world[pel][:3, :3]
fwd = rest.T @ np.array([0, 0, 1.0])


def heading(Rs):
    f = Rs @ fwd
    return np.degrees(np.arctan2(f[..., 0], f[..., 2]))


mh = heading(mR)
# GH3 pelvis world rotation is not stored; recompute heading from gh3walk (stored heading).
gh = g["heading"]


def ang(X, Y):
    D = np.swapaxes(X, -1, -2) @ Y
    return np.degrees(np.arccos(np.clip((np.trace(D, axis1=-2, axis2=-1) - 1) / 2, -1, 1)))


# Offset: song time = gh3 t + offset. Search by the pelvis path in the walk window.
win = (mt >= walk_start - 0.2) & (mt <= end)
best = None
for off in np.arange(
    float(sys.argv[5]) if len(sys.argv) > 5 else 0, float(sys.argv[6]) if len(sys.argv) > 6 else 200, 0.01
):
    gi = np.interp(mt[win], gt + off, np.arange(len(gt)))
    if gi.min() <= 0 or gi.max() >= len(gt) - 1:
        continue
    gpos = gp[np.round(gi).astype(int)]
    e = np.linalg.norm((gpos - mp[win])[:, [0, 2]], axis=1).mean()
    if best is None or e < best[0]:
        best = (e, off)
print(f"time offset (song = gh3 + {best[1]:.2f} s), mean pelvis path difference {best[0] * 100:.1f} cm")
off = best[1]
gi = np.clip(np.round(np.interp(mt, gt + off, np.arange(len(gt)))).astype(int), 0, len(gt) - 1)


def phase(i):
    c = mclip[i]
    return "walk:" + c if c != "-" else ("before" if mt[i] < walk_start else "after")


names = {}
for i in range(len(mt)):
    names.setdefault(phase(i), []).append(i)


def onset(tt, pp, start_guess):
    d = np.linalg.norm(pp[:, [0, 2]] - pp[np.searchsorted(tt, start_guess - 0.5), [0, 2]], axis=1)
    moving = np.flatnonzero(d > 0.1)
    moving = moving[tt[moving] > start_guess - 1]
    end = np.flatnonzero((d > d.max() - 0.02) & (tt > start_guess))
    return tt[moving[0]], tt[end[0]], d


m_on, m_off, md = onset(mt, mp, walk_start)
gsel = (gt + off > walk_start - 3) & (gt + off < walk_start + 8)
g_on, g_off, gd = onset(gt[gsel] + off, gp[gsel], walk_start)
print(
    f"pelvis moves 10 cm from its place at: GH3 {g_on:.3f} s, mod {m_on:.3f} s; reaches its end distance at GH3 {g_off:.3f} s, mod {m_off:.3f} s"
)
print(f"pelvis travel: GH3 {gd.max():.3f} m, mod {md.max():.3f} m")
order = sorted(names, key=lambda k: names[k][0])
print(
    f"{'phase':22s} {'n':>5s} {'pelvis cm (median/max)':>24s} {'joints deg (median/p90)':>24s} {'heading deg (median)':>21s}"
)
for k in order:
    idx = np.array(names[k])
    pe = np.linalg.norm((gp[gi[idx]] - mp[idx])[:, [0, 2]], axis=1) * 100
    je = ang(grel[gi[idx]], mrel[idx]).mean(1)
    he = (gh[gi[idx]] - mh[idx] + 180) % 360 - 180
    print(
        f"{k:22s} {len(idx):5d} {np.median(pe):11.1f} {pe.max():11.1f} {np.median(je):11.2f} {np.percentile(je, 90):11.2f} {np.median(he):20.1f}"
    )
after = np.flatnonzero(mt > m_off + 1.5)
if len(after):
    i = after[min(len(after) - 1, 30)]
    print(
        f"final pelvis (GH3 coords) at song {mt[i]:.2f}: GH3 ({gp[gi[i], 0]:.3f}, {gp[gi[i], 2]:.3f}) heading {gh[gi[i]]:.1f}; mod ({mp[i, 0]:.3f}, {mp[i, 2]:.3f}) heading {mh[i]:.1f}"
    )
np.savez(sys.argv[2] + ".cmp.npz", mt=mt, mp=mp, mh=mh, gt=gt + off, gp=gp, gh=gh)
