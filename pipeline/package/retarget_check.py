"""Checks of a retargeted performance (docs/BLENDER-ADDON.md section 2.13): the GH3 clips of a profile on a character's
own skeleton, with the retarget of the game (skeleton.Retarget). Each finding says what is wrong, where, and what to do.

Checks: a hand that does not reach its place on the instrument, a bone that flips between two frames, a knee or an
elbow that bends the other way than the GH3 one, a foot that slides while the GH3 foot stays, a forearm that twists
too far (candy wrapper), and mesh edges that stretch (when the mesh is given).

numpy only and relative imports, so the Blender add-on bundles it. Runtime coordinates (metres).
"""

import numpy as np

from .skeleton import qinv, qmul, qnorm, qrotate

HAND_MISS = 0.03
FLIP_DEGREES = 50
BEND_DEGREES = 100
SLIDE = 0.015
TWIST_DEGREES = 110
STRETCH = 2.0
STRETCH_SHARE = 0.01


def _angle(a, b):
    """Angle in degrees between unit quaternions (..., 4)."""
    dot = np.abs(np.sum(a * b, -1))
    return np.degrees(2 * np.arccos(np.clip(dot, -1, 1)))


def _bend(a, b, c):
    """The direction in which the middle joint b sticks out of the line a-c, and its distance (frames, 3), (frames)."""
    axis = c - a
    axis = axis / np.maximum(np.linalg.norm(axis, axis=-1, keepdims=True), 1e-9)
    off = (b - a) - axis * np.sum((b - a) * axis, -1, keepdims=True)
    size = np.linalg.norm(off, axis=-1)
    return off / np.maximum(size[..., None], 1e-9), size


def _twist(q, axis):
    """Signed twist angle in degrees of quaternions q (..., 4) about a unit axis (3)."""
    q = np.where(q[..., 3:4] < 0, -q, q)
    return np.degrees(2 * np.arctan2(np.sum(q[..., :3] * axis, -1), q[..., 3]))


def check_clip(retarget, label, source_t, source_q, names, roles, mesh=None):
    """Findings for one clip: dicts with kind, severity ('ERROR' or 'WARNING'), role, value, message, and fix. The arm
    IK strengths come from the GH3 hands (Retarget.arm_gains)."""
    right, left = retarget.arm_gains(source_t, source_q)
    retarget.apply(source_t, source_q, right, left)
    wp, wq, sp = retarget.wp, retarget.wq, retarget.source_p
    frames = wp.shape[0]
    role_of = {bone: role for role, bone in roles.items()}
    out = []

    def add(kind, severity, role, value, message, fix):
        out.append(
            {
                "kind": kind,
                "severity": severity,
                "role": role,
                "clip": label,
                "value": float(value),
                "message": message,
                "fix": fix,
            }
        )

    # Hands: the wrist against its goal (the GH3 hand place on the moved and scaled instrument) while the arm is on it.
    for end, goal, gain, _knuckle, _offset in retarget.goals:
        active = np.asarray(gain) * np.ones(frames) > 0.9
        if not active.any():
            continue
        miss = np.linalg.norm(wp[:, end] - goal, axis=-1)[active]
        worst = float(np.percentile(miss, 95))
        if worst > HAND_MISS:
            role = role_of.get(end, names[end])
            add(
                "hand",
                "WARNING",
                role,
                worst,
                f"{label}: the {role} is {worst * 100:.0f} cm from its place on the instrument",
                "The arm is too short for the instrument: set a smaller Instrument Scale, or move the instrument nearer with Instrument Offset. "
                "Also make sure that the UpperArm, LowerArm, and Hand roles are on the correct bones",
            )
    # Flips: a bone that turns much more than its GH3 bone between two frames.
    if frames > 2:
        for bone, source in enumerate(retarget.map):
            if source < 0:
                continue
            mine = _angle(wq[1:, bone], wq[:-1, bone])
            theirs = _angle(retarget.source_q[1:, source], retarget.source_q[:-1, source])
            jump = mine - theirs
            worst = float(jump.max())
            if worst > FLIP_DEGREES:
                role = role_of.get(bone, names[bone])
                add(
                    "flip",
                    "ERROR",
                    role,
                    worst,
                    f"{label}: '{names[bone]}' ({role}) flips {worst:.0f}° in one frame",
                    f"The {role} bone points in a direction that the GH3 bone cannot follow: make sure that the {role} role is on the correct bone, and "
                    "that the next role in the chain is on its child",
                )
    # Knees and elbows: the bend of the middle joint against the GH3 bend.
    chains = [(u, lo, e, "knee") for u, lo, e, *_ in retarget.legs] + [
        (u, lo, e, "elbow") for u, lo, e, *_ in retarget.arms
    ]
    for upper, lower, end, what in chains:
        su, sl, se = retarget.map[upper], retarget.map[lower], retarget.map[end]
        if min(su, sl, se) < 0:
            continue
        mine, size = _bend(wp[:, upper], wp[:, lower], wp[:, end])
        theirs, their_size = _bend(sp[:, su], sp[:, sl], sp[:, se])
        bent = (size > 0.02) & (their_size > 0.02)
        if not bent.any():
            continue
        angle = np.degrees(np.arccos(np.clip(np.sum(mine * theirs, -1), -1, 1)))[bent]
        wrong = float(np.mean(angle > BEND_DEGREES))
        if wrong > 0.1:
            role = role_of.get(lower, names[lower])
            add(
                "bend",
                "ERROR",
                role,
                wrong,
                f"{label}: the {what} ({role}) bends the other way in {wrong * 100:.0f} % of the bent frames",
                "The model may look toward +Y or its left and right may be swapped: click Set Up Skeleton again. Else make sure that the "
                f"{role} role is on the {what} bone",
            )
    # Feet: the target foot moves while the GH3 foot stays.
    for _upper, _lower, foot, source_foot, *_ in retarget.legs:
        if frames < 3:
            break
        theirs = np.linalg.norm(np.diff(sp[:, source_foot], axis=0), axis=-1)
        mine = np.linalg.norm(np.diff(wp[:, foot], axis=0), axis=-1)
        planted = theirs < 0.004
        if planted.sum() < 3:
            continue
        sliding = float(np.mean(mine[planted] > SLIDE))
        if sliding > 0.05:
            role = role_of.get(foot, names[foot])
            add(
                "slide",
                "WARNING",
                role,
                sliding,
                f"{label}: the {role} slides in {sliding * 100:.0f} % of the frames where the GH3 foot stays",
                "The leg cannot reach the GH3 foot place: make sure that the UpperLeg, LowerLeg, and Foot roles are on the hip, knee, and ankle bones",
            )
    # Twist: the hand against the forearm about the forearm axis (from the rest pose).
    for _upper, lower, hand, *_ in retarget.arms:
        rest = qnorm(qmul(qinv(retarget.rest_q[lower]), retarget.rest_q[hand]))
        axis = retarget.rest_p[hand] - retarget.rest_p[lower]
        axis = qrotate(qinv(retarget.rest_q[lower]), axis / max(np.linalg.norm(axis), 1e-9))
        twist_bone = next((t for lo, t, h, *_ in retarget.lower_twist if lo == lower and h == hand), -1)
        relative = qmul(qmul(qinv(wq[:, lower]), wq[:, hand]), qinv(rest))
        twist = np.abs(_twist(relative, axis))
        if twist_bone >= 0:
            twist = (
                np.abs(
                    _twist(
                        qmul(
                            qmul(qinv(wq[:, lower]), wq[:, twist_bone]),
                            qinv(qnorm(qmul(qinv(retarget.rest_q[lower]), retarget.rest_q[twist_bone]))),
                        ),
                        axis,
                    )
                )
                / 2
            )
        worst = float(twist.max())
        if worst > TWIST_DEGREES:
            role = role_of.get(lower, names[lower])
            add(
                "twist",
                "WARNING",
                role,
                worst,
                f"{label}: the {role} twists {worst:.0f}° (the wrist can look like a candy wrapper)",
                "Map a forearm twist bone to LowerArmTwist if the model has one, or add one half way along the forearm with weights on the "
                "forearm mesh",
            )
    if mesh is not None:
        out += _stretch(retarget, label, mesh, names)
    return out


def _stretch(retarget, label, mesh, names):
    """Mesh edges that get much longer than at rest (linear blend skinning of a sample of frames and edges)."""
    rest, joints, weights, edges = (np.asarray(mesh[k]) for k in ("positions", "joints", "weights", "edges"))
    if not len(edges):
        return []
    frames = retarget.wp.shape[0]
    pick = np.unique(np.linspace(0, frames - 1, min(frames, 8)).astype(int))
    rest_p, rest_q = retarget.rest_p, retarget.rest_q
    length = np.linalg.norm(rest[edges[:, 0]] - rest[edges[:, 1]], axis=-1)
    keep = length > 1e-5
    edges, length = edges[keep], length[keep]
    worst_ratio, worst_bone = 0.0, -1
    for f in pick:
        turn = qmul(retarget.wq[f], qinv(rest_q))
        moved = np.zeros_like(rest)
        for k in range(joints.shape[1]):
            j = joints[:, k]
            moved += weights[:, k : k + 1] * (retarget.wp[f][j] + qrotate(turn[j], rest - rest_p[j]))
        ratio = np.linalg.norm(moved[edges[:, 0]] - moved[edges[:, 1]], axis=-1) / length
        share = float(np.mean(ratio > STRETCH))
        if share > STRETCH_SHARE and share > worst_ratio:
            worst_ratio = share
            bad = edges[ratio > STRETCH].reshape(-1)
            worst_bone = int(np.bincount(joints[bad, 0], minlength=len(names)).argmax())
    if worst_bone < 0:
        return []
    mapped = retarget.map[worst_bone] >= 0
    fix = (
        "The weights of the stretched vertices mix bones that move apart: smooth the weights at this joint"
        if mapped
        else f"'{names[worst_bone]}' has no role, so it keeps its rest pose under its parent: give it a role if it is a body bone, or move its weights "
        "to the body bone next to it"
    )
    return [
        {
            "kind": "stretch",
            "severity": "WARNING",
            "role": names[worst_bone],
            "clip": label,
            "value": worst_ratio,
            "message": f"{label}: {worst_ratio * 100:.1f} % of the mesh edges near '{names[worst_bone]}' stretch to more than {STRETCH:.1f} times their length",
            "fix": fix,
        }
    ]


def summary(findings):
    """One finding per kind and role (the worst), errors first."""
    best = {}
    for f in findings:
        key = (f["kind"], f["role"])
        if key not in best or f["value"] > best[key]["value"]:
            best[key] = f
    return sorted(best.values(), key=lambda f: (f["severity"] != "ERROR", f["kind"], f["role"]))
