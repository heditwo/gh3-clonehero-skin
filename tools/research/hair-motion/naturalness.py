"""Naturalness of the custom chains of a character package (docs/VERIFICATION.md, "Custom chains").

    naturalness.py motions <dir>                     the test head motions for GH3_HAIR_BENCH_MOTION (turn, headbang, sway)
    naturalness.py measure <bench.bin> [motion]      the measures of each chain of a character bench output
    naturalness.py vrm <bench.bin> <out.bin> [--stiffness S] [--drag D] [--gravity G] [--fps F]
                                                     the same rest motion through the VRM spring bone (reference)

The bench (GH3_HAIR_BENCH=character:<package>) writes, for each frame, the time, the head pose, and for each custom
joint its start and end with physics and without (the bind shape on the animated rig); `<bench.bin>.json` has the
joint names, parents, and lengths. The measures are in the head frame: the angle of the chain end from its bind
direction (seen from the chain start), the frames with the chain end above the chain start, and the speed of the chain
end relative to the head. With a test motion, also the settle time and the swings after each stop.
"""

import json
import math
import sys
from pathlib import Path

import numpy as np

MOTIONS = {
    # name: (description, function of time -> (offset xyz, yaw, pitch, roll in degrees)), stop times
    "turn": "head turns 45 degrees in 0.2 s, holds, turns back",
    "headbang": "head nods +-20 degrees at 3 Hz for 2 s, then holds",
    "sway": "head tilts +-20 degrees sideways at 1.5 Hz for 2 s, then holds",
}
HOLD = 2.0
FPS = 120


def smooth(x):
    x = np.clip(x, 0, 1)
    return x * x * (3 - 2 * x)


def motion_rows(name):
    """(rows of t, dx, dy, dz, qx, qy, qz, qw; stop times)."""
    if name == "turn":
        times = np.arange(0, HOLD + 3.0 + 3.0, 1 / FPS)
        yaw = 45 * (smooth((times - HOLD) / 0.2) - smooth((times - HOLD - 3.0) / 0.2))
        offset, pitch, roll, stops = np.zeros((len(times), 3)), np.zeros(len(times)), np.zeros(len(times)), [HOLD + 0.2, HOLD + 3.2]
    elif name == "headbang":
        times = np.arange(0, HOLD + 2.0 + 3.0, 1 / FPS)
        active = (times >= HOLD) & (times < HOLD + 2.0)
        pitch = np.where(active, 20 * np.sin(2 * math.pi * 3 * (times - HOLD)), 0)
        yaw, roll, offset, stops = np.zeros(len(times)), np.zeros(len(times)), np.zeros((len(times), 3)), [HOLD + 2.0]
    elif name == "sway":
        times = np.arange(0, HOLD + 2.0 + 3.0, 1 / FPS)
        active = (times >= HOLD) & (times < HOLD + 2.0)
        roll = np.where(active, 20 * np.sin(2 * math.pi * 1.5 * (times - HOLD)), 0)
        yaw, pitch, offset, stops = np.zeros(len(times)), np.zeros(len(times)), np.zeros((len(times), 3)), [HOLD + 2.0]
    else:
        raise SystemExit(f"unknown motion {name}")
    rows = []
    for t, o, y, p, r in zip(times, offset, yaw, pitch, roll):
        qy = np.array([0, math.sin(math.radians(y) / 2), 0, math.cos(math.radians(y) / 2)])
        qx = np.array([math.sin(math.radians(p) / 2), 0, 0, math.cos(math.radians(p) / 2)])
        qz = np.array([0, 0, math.sin(math.radians(r) / 2), math.cos(math.radians(r) / 2)])
        rows.append([t, *o, *quat_mul(quat_mul(qy, qx), qz)])
    return np.array(rows, dtype=np.float32), stops


def quat_mul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return np.array([aw * bx + ax * bw + ay * bz - az * by, aw * by - ax * bz + ay * bw + az * bx,
                     aw * bz + ax * by - ay * bx + az * bw, aw * bw - ax * bx - ay * by - az * bz])


def rotate_inverse(q, v):
    """v (n x 3) turned by the inverse of the unit quaternions q (n x 4, x y z w)."""
    u, w = -q[:, :3], q[:, 3:4]
    t = 2 * np.cross(u, v)
    return v + w * t + np.cross(u, t)


def load(path):
    meta = json.loads(Path(f"{path}.json").read_text())
    data = np.fromfile(path, dtype=np.float32).reshape(-1, meta["row"])
    joints = data[:, 8:].reshape(len(data), -1, 4, 3)
    return meta, data[:, 0], data[:, 1:4], data[:, 4:8], joints


def chains(meta):
    """The joints of each chain from the start to the end (the longest path when a chain branches)."""
    rows = meta["joints"]
    children = {}
    for k, row in enumerate(rows): children.setdefault(row["parent"], []).append(k)

    def longest(k):
        best = [k]
        for child in children.get(k, []):
            path = [k] + longest(child)
            if len(path) > len(best): best = path
        return best

    return [longest(k) for k, row in enumerate(rows) if row["parent"] < 0]


def chain_name(meta, path):
    return meta["joints"][path[0]]["name"]


def measure(path, motion=None, quiet=False):
    meta, times, head, turn, joints = load(path)
    dt = float(np.median(np.diff(times)))
    stops = motion_rows(motion)[1] if motion else []
    results = []
    for chain in chains(meta):
        start = joints[:, chain[0], 0]
        tip, rest_tip = joints[:, chain[-1], 1], joints[:, chain[-1], 3]
        a, b = tip - start, rest_tip - start
        cos = np.einsum("ij,ij->i", a, b) / np.maximum(np.linalg.norm(a, axis=1) * np.linalg.norm(b, axis=1), 1e-9)
        angle = np.degrees(np.arccos(np.clip(cos, -1, 1)))
        local = rotate_inverse(turn, tip - head)
        speed = np.linalg.norm(np.diff(local, axis=0), axis=1) / dt
        skip = times[1:] > 1.0
        above = (tip[:, 1] > start[:, 1] + 0.01)[times > 1.0]
        row = {"chain": chain_name(meta, chain), "joints": len(chain), "length": round(sum(meta["joints"][k]["length"] for k in chain), 3),
               "angle": [round(float(np.percentile(angle[times > 1.0], q)), 1) for q in (50, 95)] + [round(float(angle[times > 1.0].max()), 1)],
               "above": round(float(above.mean()) * 100, 2),
               "speed": [round(float(np.percentile(speed[skip], q)), 2) for q in (50, 95)] + [round(float(speed[skip].max()), 2)]}
        if motion:
            # Before the motion: the hang under gravity (angle from the bind shape).
            row["hang"] = round(float(angle[np.searchsorted(times, HOLD) - 1]), 1)
            settles, swings = [], []
            for stop in stops:
                window = (times >= stop) & (times < stop + 2.8)
                seg, t = local[window], times[window]
                final = seg[-int(0.3 / dt):].mean(0)
                offset = np.linalg.norm(seg - final, axis=1)
                # Settled: the end stays within 1 cm of its final place.
                outside = np.nonzero(offset > 0.01)[0]
                settles.append(round(float(t[outside[-1]] - stop) if len(outside) else 0.0, 2))
                # Swings: the turns of the end along its main direction of motion, larger than 1 cm.
                centred = seg - final
                axis = np.linalg.svd(centred, full_matrices=False)[2][0]
                signal = centred @ axis
                count, sign = 0, 0
                for value in signal:
                    s = 1 if value > 0.01 else -1 if value < -0.01 else 0
                    if s and s != sign:
                        if sign: count += 1
                        sign = s
                swings.append(count)
            row["settle"], row["swings"] = settles, swings
        results.append(row)
    if not quiet:
        for row in results: print(json.dumps(row, ensure_ascii=False))
    return results


def vrm(path, out, stiffness=1.0, drag=0.4, gravity=0.0, fps=60.0):
    """The VRM spring bone (UniVRM VRMSpringBone, VRM 0.x; three-vrm is the same) with the bind shape of the bench as
    the rest shape, at `fps`. Writes a bench file with the VRM chain in place of the solver chain. No colliders."""
    meta, times, head, turn, joints = load(path)
    data = np.fromfile(path, dtype=np.float32).reshape(-1, meta["row"])
    step = 1 / fps
    frames = np.arange(0, times[-1], step)
    index = np.clip(np.searchsorted(times, frames), 0, len(times) - 1)
    rest = joints[index][:, :, 2:4]
    out_rows = data[index].copy()
    rows = meta["joints"]
    count = len(rows)
    tails = rest[0, :, 1].copy()
    previous = tails.copy()
    gravity_step = np.array([0, -1.0, 0]) * gravity * step
    for f in range(len(frames)):
        origin_rest, tail_rest = rest[f, :, 0], rest[f, :, 1]
        turns = [None] * count
        origins = np.zeros((count, 3))
        new_tails = np.zeros((count, 3))
        for k in range(count):
            parent = rows[k]["parent"]
            if parent < 0:
                turn_parent, origin = np.eye(3), origin_rest[k]
            else:
                turn_parent = turns[parent]
                origin = origins[parent] + turn_parent @ (origin_rest[k] - origin_rest[parent])
            axis = turn_parent @ (tail_rest[k] - origin_rest[k])
            length = np.linalg.norm(axis)
            axis = axis / length
            following = tails[k] + (tails[k] - previous[k]) * (1 - drag) + axis * stiffness * step + gravity_step
            direction = following - origin
            direction /= max(np.linalg.norm(direction), 1e-9)
            new_tails[k] = origin + direction * length
            turns[k] = from_to(axis, direction) @ turn_parent
            origins[k] = origin
        previous, tails = tails, new_tails
        joint = out_rows[f, 8:].reshape(count, 4, 3)
        joint[:, 0], joint[:, 1] = origins, tails
        out_rows[f, 8:] = joint.reshape(-1)
    out_rows[:, 0] = frames
    out_rows.astype(np.float32).tofile(out)
    Path(f"{out}.json").write_text(json.dumps(meta))


def from_to(a, b):
    v = np.cross(a, b)
    c = float(np.dot(a, b))
    if c < -0.999999: return -np.eye(3)
    k = np.array([[0, -v[2], v[1]], [v[2], 0, -v[0]], [-v[1], v[0], 0]])
    return np.eye(3) + k + k @ k / (1 + c)


def main(args):
    if args[0] == "motions":
        folder = Path(args[1])
        folder.mkdir(parents=True, exist_ok=True)
        for name in MOTIONS:
            rows, _ = motion_rows(name)
            rows.tofile(folder / f"{name}.bin")
            print(folder / f"{name}.bin", MOTIONS[name])
    elif args[0] == "measure":
        measure(args[1], args[2] if len(args) > 2 else None)
    elif args[0] == "vrm":
        options = dict(zip(args[3::2], args[4::2]))
        vrm(args[1], args[2], float(options.get("--stiffness", 1.0)), float(options.get("--drag", 0.4)), float(options.get("--gravity", 0.0)),
            float(options.get("--fps", 60)))
    else:
        raise SystemExit(__doc__)


if __name__ == "__main__":
    main(sys.argv[1:])
