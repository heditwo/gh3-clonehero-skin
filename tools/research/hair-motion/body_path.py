"""World paths of all keyframed body bones of a GH3 memory run, for the hair bench (body collision experiment).
usage: body_path.py <run> <profile> <gx> <gz> <out.bin>
Writes int32 bone count B, B int32 rig indices, then rows of float32 t and, for each bone, x, y, z, qx, qy, qz, qw in runtime
coordinates (the order of the rig indices)."""

import json, sys
import numpy as np
from hairlib import Profile, S, key, matq

run, pid, gx, gz, out = sys.argv[1], sys.argv[2], float(sys.argv[3]), float(sys.argv[4]), sys.argv[5]
prof = Profile(pid)
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
h = idx[prof.bones[prof.head]]
ch = np.r_[True, np.any(np.abs(np.diff(pos[:, h], axis=0)) > 0, axis=1)]
t = t[ch] - t[ch][0]
m = m[ch]
M = m.reshape(len(t), m.shape[1], 4, 4).transpose(0, 1, 3, 2)
hair = {j["bone"] for j in prof.joints}
bones = sorted(bi for bi, nm in enumerate(prof.bones) if nm in idx and bi not in hair and nm in prof.body)
rows = [t[:, None]]
for bi in bones:
    nm = prof.bones[bi]
    W = M[:, idx[nm]] @ np.linalg.inv(prof.body[nm]) @ prof.world[bi]
    R = S @ W[:, :3, :3] @ S
    p = W[:, :3, 3] @ S
    rows += [p, matq(R)]
with open(out, "wb") as f:
    np.array([len(bones), *bones], np.int32).tofile(f)
    np.concatenate(rows, 1).astype(np.float32).tofile(f)
print(pid, len(bones), "body bones,", len(t), "rows")
