"""The GH3 ragdoll body shapes of a character library profile as hair bench colliders (body collision experiment, 2026-10-10).
usage: body_colliders.py <profile> <out.json>
Writes {"colliders": [{"bone", "a", "b", "radius"}], "radius": {joint bone: radius}, "parent": {joint bone: parent bone}}: the
capsules of the keyframed bodies (not the head: the bench keeps the head capsule) in the runtime frame of their bones,
and for each hair joint the half thickness of its box."""

import json
import sys

import numpy as np
from hairlib import Profile, S, key

from pipeline.library.hair import body_shape

pid, out = sys.argv[1], sys.argv[2]
prof = Profile(pid)
r = prof.rag
joints = {j["bone"] for j in prof.joints}
colliders, radius = [], {}
for p, c in r.types.items():
    if c != "hkRigidBody":
        continue
    nm = key(r.field(p, c, "name"))
    if nm not in prof.bones:
        continue
    bone = prof.bones.index(nm)
    shape, cls = body_shape(r, p)
    if bone in joints:
        if cls == "hkBoxShape":
            radius[bone] = float(min(r.field(shape, cls, "halfExtents")[1:3]))
        elif cls == "hkCapsuleShape":
            radius[bone] = float(r.field(shape, cls, "radius"))
        continue
    if bone == prof.head or cls != "hkCapsuleShape":
        continue
    # Havok shape vertices in the body frame (with a translate wrapper when present) -> GH3 world -> bone -> runtime.
    wrapper, wcls = r.field(p, "hkRigidBody", "collidable")
    top = r.field(wrapper, wcls, "shape")
    offset = np.zeros(3)
    if r.types[top] == "hkConvexTranslateShape":
        offset = np.array(r.field(top, "hkConvexTranslateShape", "translation")[:3])
    to_bone = np.linalg.inv(prof.world[bone]) @ prof.body[nm]
    ends = []
    for field in ("vertexA", "vertexB"):
        v = np.r_[np.array(r.field(shape, cls, field)[:3]) + offset, 1]
        ends.append(((to_bone @ v)[:3] @ S).tolist())
    colliders.append(
        {"bone": bone, "a": ends[0], "b": ends[1], "radius": float(r.field(shape, cls, "radius")), "name": f"{nm:08x}"}
    )
json.dump(
    {
        "colliders": colliders,
        "radius": {str(k): v for k, v in radius.items()},
        "parent": {str(j["bone"]): j["parent"] for j in prof.joints},
    },
    open(out, "w"),
    indent=1,
)
print(pid, len(colliders), "body capsules", len(radius), "joint radii")
