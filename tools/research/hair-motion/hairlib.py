"""Shared helpers: GH3 memory recordings and mod bench output, in one frame (GH3 model coordinates, bones)."""

import json
import sys

import numpy as np

sys.path.insert(0, str(__import__("pathlib").Path(__file__).resolve().parents[3]))
from pipeline.gh3.formats import key
from pipeline.gh3.havok import Ragdoll
from pipeline.library.hair import body_matrix, havok_matrix

REPO = __import__("pathlib").Path(__file__).resolve().parents[3]
WORK = __import__("os").environ.get("HAIR_MOTION_WORK", str(__import__("pathlib").Path.home() / ".cache/hair-motion"))
DATA = __import__("os").environ.get(
    "GH3_DATA", str(__import__("pathlib").Path.home() / "games/gh3-test-tools/game/DATA")
)


def ragdolls():
    """The GH3 ragdolls (.rag of ZONES/global.pak.xen) by checksum."""
    from pipeline.gh3.data import Source

    return {n: b for k, n, b in Source(DATA).load("ZONES/global.pak.xen") if k == key(".rag")}


S = np.diag([1.0, 1.0, -1.0])
LIB = str(REPO) + "/assets/libraries/gh3-characters-v1/characters.json"
_RAGS = {}


def qmat(q):
    x, y, z, w = q[..., 0], q[..., 1], q[..., 2], q[..., 3]
    return np.stack(
        [
            np.stack([1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)], -1),
            np.stack([2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)], -1),
            np.stack([2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)], -1),
        ],
        -2,
    )


def matq(m):
    # robust rotation matrix -> quaternion (x, y, z, w)
    out = np.zeros(m.shape[:-2] + (4,))
    tr = np.trace(m, axis1=-2, axis2=-1)
    w = np.sqrt(np.maximum(0, 1 + tr)) / 2
    x = np.sqrt(np.maximum(0, 1 + m[..., 0, 0] - m[..., 1, 1] - m[..., 2, 2])) / 2
    y = np.sqrt(np.maximum(0, 1 - m[..., 0, 0] + m[..., 1, 1] - m[..., 2, 2])) / 2
    z = np.sqrt(np.maximum(0, 1 - m[..., 0, 0] - m[..., 1, 1] + m[..., 2, 2])) / 2
    x = np.copysign(x, m[..., 2, 1] - m[..., 1, 2])
    y = np.copysign(y, m[..., 0, 2] - m[..., 2, 0])
    z = np.copysign(z, m[..., 1, 0] - m[..., 0, 1])
    out[..., 0], out[..., 1], out[..., 2], out[..., 3] = x, y, z, w
    return out / np.linalg.norm(out, axis=-1, keepdims=True)


class Profile:
    def __init__(self, pid):
        lib = json.load(open(LIB))
        self.p = lib["profiles"][pid]
        self.hair = lib["hair"][self.p["hair"]]
        self.bones = [int(b, 16) for b in self.p["bones"]]
        self.parents = self.p["parents"]
        bind = np.array(self.p["bind"])
        n = len(bind)
        W = np.zeros((n, 4, 4))
        for i in range(n):
            L = np.eye(4)
            L[:3, :3] = qmat(bind[i, 3:7])
            L[:3, 3] = bind[i, :3]
            W[i] = W[self.parents[i]] @ L if self.parents[i] >= 0 else L
        self.world_runtime = W
        G = W.copy()
        G[:, :3, :3] = S @ W[:, :3, :3] @ S
        G[:, :3, 3] = W[:, :3, 3] @ S
        self.world = G  # GH3 coordinates
        if not _RAGS:
            _RAGS.update(ragdolls())
        self.rag = Ragdoll(_RAGS[int(self.p["hair"], 16)])
        r = self.rag
        self.body = {key(r.field(p, c, "name")): body_matrix(r, p) for p, c in r.types.items() if c == "hkRigidBody"}
        self.joints = self.hair["joints"]
        self.head = self.hair["head"]
        self.cons = {}
        for at, cls in r.types.items():
            if cls != "hkConstraintInstance":
                continue
            a, b = r.field(at, cls, "entities")
            c, p = key(r.field(a, "hkRigidBody", "name")), key(r.field(b, "hkRigidBody", "name"))
            d = r.field(at, cls, "data")
            kind = r.types[d]
            atoms, ac = r.field(d, kind, "atoms")
            tr, tc = r.field(atoms, ac, "transforms")
            self.cons[c] = dict(
                parent=p,
                kind=kind,
                TA=havok_matrix(r.field(tr, tc, "transformA")),
                TB=havok_matrix(r.field(tr, tc, "transformB")),
            )

    def name(self, i):
        return self.bones[i]


def gh3_run(run, prof, gx, gz):
    """Bone worlds (GH3 coords) of the head and the hair joints from a GH3 memory run: t, {bone index: (n,4,4)}."""
    b = json.load(open(f"{run}/bodies.json"))
    z = np.load(f"{run}/bodies.npz")
    t = z["t"]
    m = z["m"]
    pos = m[:, :, 12:15]
    idx = {}
    for i, x in enumerate(b):
        if pos[:, i].std(0).max() < 1e-3:
            continue
        if abs(np.median(pos[:, i, 0]) - gx) < 0.6 and abs(np.median(pos[:, i, 2]) - gz) < 0.6:
            idx.setdefault(key(x["name"]), i)
    hb = prof.bones[prof.head]
    h = idx[hb]
    ch = np.r_[True, np.any(np.abs(np.diff(pos[:, h], axis=0)) > 0, axis=1)]
    t = t[ch] - t[ch][0]
    m = m[ch]
    M = m.reshape(len(t), m.shape[1], 4, 4).transpose(0, 1, 3, 2)
    out = {}
    for bi in [prof.head] + [j["bone"] for j in prof.joints]:
        nm = prof.bones[bi]
        if nm not in idx:
            continue
        out[bi] = M[:, idx[nm]] @ np.linalg.inv(prof.body[nm]) @ prof.world[bi]
    return t, out


def head_file(t, H, path):
    """Head bone path in runtime coordinates for the bench."""
    R = S @ H[:, :3, :3] @ S
    p = H[:, :3, 3] @ S
    q = matq(R)
    rows = np.concatenate([t[:, None], p, q], 1).astype(np.float32)
    rows.tofile(path)


def bench(path, prof):
    n = 1 + len(prof.joints)
    d = np.fromfile(path, np.float32).reshape(-1, 1 + 7 * n)
    t = d[:, 0].astype(float)
    out = {}
    for k, bi in enumerate([prof.head] + [j["bone"] for j in prof.joints]):
        v = d[:, 1 + 7 * k : 8 + 7 * k].astype(float)
        M = np.zeros((len(t), 4, 4))
        M[:, 3, 3] = 1
        M[:, :3, :3] = S @ qmat(v[:, 3:7]) @ S
        M[:, :3, 3] = v[:, :3] @ S
        out[bi] = M
    return t, out


def metrics(t, W, prof):
    """Per joint: Havok cone and plane angles (degrees) and the tip in the head frame (m)."""
    res = {}
    H = W[prof.head]
    onhead = {prof.head}
    for j in prof.joints:
        if j["parent"] in onhead:
            onhead.add(j["bone"])
    for j in prof.joints:
        c, p = j["bone"], j["parent"]
        if c not in W or p not in W or c not in onhead:
            continue
        nm = prof.bones[c]
        con = prof.cons.get(nm)
        C = W[c] @ np.linalg.inv(prof.world[c]) @ prof.body[nm]
        P = W[p] @ np.linalg.inv(prof.world[p]) @ prof.body[prof.bones[p]]
        r = {}
        if con and con["kind"] == "hkRagdollConstraintData":
            FA = C[:, :3, :3] @ con["TA"][:3, :3]
            FB = P[:, :3, :3] @ con["TB"][:3, :3]
            r["cone"] = np.degrees(np.arccos(np.clip(np.sum(FA[:, :, 0] * FB[:, :, 0], -1), -1, 1)))
            r["plane"] = np.degrees(np.arcsin(np.clip(np.sum(FA[:, :, 0] * FB[:, :, 1], -1), -1, 1)))
        tip = W[c][:, :3, 3] + j["length"] * W[c][:, :3, 0]
        r["tip"] = np.einsum("nji,nj->ni", H[:, :3, :3], tip - H[:, :3, 3])
        res[c] = r
    return res


def resample(t, x, tt):
    return np.stack([np.interp(tt, t, x[:, k]) for k in range(x.shape[1])], 1) if x.ndim == 2 else np.interp(tt, t, x)
