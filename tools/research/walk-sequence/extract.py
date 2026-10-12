"""Keep the leg and spine bodies of a GH3 body recording (gh3_session.py bodies.npz) without loading the whole array.

Usage: extract.py <session folder> <out.npz>
"""

import json
import sys
import zipfile

import numpy as np

run, out = sys.argv[1], sys.argv[2]
b = json.load(open(f"{run}/bodies.json"))
names = {
    "Bone_Pelvis",
    "Bone_Thigh_L",
    "Bone_Thigh_R",
    "Bone_Knee_L",
    "Bone_Knee_R",
    "Bone_Ankle_L",
    "Bone_Ankle_R",
    "Bone_Stomach_Lower",
    "Bone_Stomach_Upper",
    "Bone_Chest",
    "Bone_Neck",
    "Bone_Head",
}
z = zipfile.ZipFile(f"{run}/bodies.npz")
t = np.load(z.open("t.npy"))
f = z.open("m.npy")
version = np.lib.format.read_magic(f)
shape, fortran, dtype = (
    np.lib.format.read_array_header_1_0 if version == (1, 0) else np.lib.format.read_array_header_2_0
)(f)
assert not fortran
keep = [i for i, x in enumerate(b) if x["name"] in names]
row = shape[1] * shape[2] * dtype.itemsize
parts = []
for start in range(0, shape[0], 2000):
    n = min(2000, shape[0] - start)
    a = np.frombuffer(f.read(row * n), dtype).reshape(n, shape[1], shape[2])
    parts.append(a[:, keep].copy())
sub = np.concatenate(parts)
np.savez_compressed(out, t=t, m=sub, names=np.array([b[i]["name"] for i in keep]), index=np.array(keep))
print(len(keep), sub.shape)
