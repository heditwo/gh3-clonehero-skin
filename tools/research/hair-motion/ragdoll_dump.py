import math
import sys

from hairlib import ragdolls

sys.path.insert(0, ".")
from pipeline.gh3.formats import key
from pipeline.gh3.havok import Ragdoll

rags = ragdolls()
names = {}
for n in ["bone_head", "bone_neck", "bone_chest", "bone_pelvis"] + [f"Bone_ACC_{i:02d}" for i in range(1, 16)]:
    names[key(n)] = n


def raw(r, addr, cls, f):
    rec = r.classes[cls]
    while f not in rec["fields"]:
        cls = rec["parent"]
        rec = r.classes[cls]
    kind, length, off, mc = rec["fields"][f]
    return (addr[0], addr[1] + off), kind


def get(r, addr, cls, f):
    at, kind = raw(r, addr, cls, f)
    if kind in (24, 4, 3, 1):
        return r.unpack(at, "b" if kind == 3 else "B")[0]
    if kind == 7:
        return r.unpack(at, "i")[0]
    if kind == 22:
        return r.refs.get(at), r.unpack((at[0], at[1] + 4), "i")[0]
    return r.field(addr, cls, f)


r = Ragdoll(rags[int(sys.argv[1], 16)])
for a, c in r.types.items():
    if c == "hkWorldCinfo":
        for f in [
            "gravity",
            "solverTau",
            "solverDamp",
            "solverIterations",
            "solverMicrosteps",
            "collisionTolerance",
            "contactRestingVelocity",
            "expectedMaxLinearVelocity",
            "enableDeactivation",
            "simulationType",
        ]:
            print("world", f, get(r, a, c, f))
    if c == "hkPhysicsSystem":
        print(
            "system",
            r.field(a, c, "name"),
            get(r, a, c, "rigidBodies"),
            get(r, a, c, "constraints"),
            get(r, a, c, "actions"),
            get(r, a, c, "active"),
        )
print()
for a, c in r.types.items():
    if c != "hkRigidBody":
        continue
    nm = key(r.field(a, c, "name")) if False else r.field(a, c, "name")
    motion, mc = r.field(a, c, "motion")
    state, sc = r.field(motion, mc, "motionState")
    mat, matc = r.field(a, c, "material")
    inv = r.field(motion, mc, "inertiaAndMassInv")
    coll, cc = r.field(a, c, "collidable")
    bp, bpc = r.field(coll, cc, "broadPhaseHandle")
    print(
        f"{nm:>12} type={get(r, motion, mc, 'type')} m={1 / inv[3] if inv[3] else 0:.3f} inv={[round(x, 2) for x in inv[:3]]} ld={r.field(state, sc, 'linearDamping'):.3f} ad={r.field(state, sc, 'angularDamping'):.3f} maxV={r.field(state, sc, 'maxLinearVelocity'):.1f} maxW={r.field(state, sc, 'maxAngularVelocity'):.1f} fr={r.field(mat, matc, 'friction'):.2f} rest={r.field(mat, matc, 'restitution'):.2f} filt={get(r, bp, bpc, 'collisionFilterInfo'):#x} q={get(r, bp, bpc, 'objectQualityType')} deact={get(r, state, sc, 'deactivationClass')}"
    )
print()
for a, c in r.types.items():
    if c != "hkConstraintInstance":
        continue
    ea, eb = r.field(a, c, "entities")
    data = r.field(a, c, "data")
    dc = r.types[data]
    atoms, ac = r.field(data, dc, "atoms")
    out = [
        r.field(ea, "hkRigidBody", "name"),
        "<-",
        r.field(eb, "hkRigidBody", "name"),
        dc[2:12],
        "prio",
        get(r, a, c, "priority"),
    ]
    if dc == "hkRagdollConstraintData":
        m, mcl = r.field(atoms, ac, "ragdollMotors")
        out += ["motor", get(r, m, mcl, "isEnabled"), r.field(m, mcl, "motors")]
        fa, fc = r.field(atoms, ac, "angFriction")
        out += ["fric", get(r, fa, fc, "isEnabled"), round(r.field(fa, fc, "maxFrictionTorque"), 3)]
        for lim in ["twistLimit", "coneLimit", "planesLimit"]:
            la, lc = r.field(atoms, ac, lim)
            out += [
                lim[:5],
                get(r, la, lc, "isEnabled"),
                round(math.degrees(r.field(la, lc, "minAngle")), 1),
                round(math.degrees(r.field(la, lc, "maxAngle")), 1),
                "tau",
                r.field(la, lc, "angularLimitsTauFactor"),
            ]
    elif dc == "hkLimitedHingeConstraintData":
        m, mcl = r.field(atoms, ac, "angMotor")
        out += ["motor", get(r, m, mcl, "isEnabled"), r.field(m, mcl, "motor")]
        fa, fc = r.field(atoms, ac, "angFriction")
        out += ["fric", get(r, fa, fc, "isEnabled"), round(r.field(fa, fc, "maxFrictionTorque"), 3)]
        la, lc = r.field(atoms, ac, "angLimit")
        out += [
            "lim",
            round(math.degrees(r.field(la, lc, "minAngle")), 1),
            round(math.degrees(r.field(la, lc, "maxAngle")), 1),
            "tau",
            r.field(la, lc, "angularLimitsTauFactor"),
        ]
    print(*out)
