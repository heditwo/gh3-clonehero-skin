"""Spike: size and error of quantized band clip encodings.

Run from the repository root:
PYTHONPATH=. uv run --with numpy --with brotli --with zstandard python tools/research/spike-clips/analyze.py \
    ~/games/gh3-test-tools/game/DATA assets/libraries/gh3-band-v1/direction /tmp/opencode/spike-clips
"""

import gzip
import json
import math
import pickle
import struct
import sys
from multiprocessing import Pool
from pathlib import Path

import brotli
import numpy as np
import zstandard

import pipeline.library.direction as direction
from pipeline.gh3.band_formats import animation, multiply_quaternions, sample_animation
from pipeline.gh3.direction import Source
from pipeline.gh3.formats import key

BINS = np.logspace(-7, 2, 1801)
SQRT1_2 = 1 / math.sqrt(2)
ENCODINGS = ["f32", "f32soa", "q48", "q32", "keys", "q48kr"]
ROT_TOLERANCE = 0.05
POS_TOLERANCE_CM = 0.02
COMPRESSORS = ["raw", "gzip9", "brotli11", "zstd19"]


def compress(data, method):
    if method == "raw":
        return data
    if method == "gzip3":
        return gzip.compress(data, 3, mtime=0)
    if method == "gzip9":
        return gzip.compress(data, 9, mtime=0)
    if method == "brotli11":
        return brotli.compress(data, quality=11, lgwin=24)
    if method == "zstd19":
        return zstandard.ZstdCompressor(level=19).compress(data)
    raise ValueError(method)


def normalize(q):
    return q / np.linalg.norm(q, axis=-1, keepdims=True)


def smallest_three(q, bits):
    """Quantize unit quaternions (..., 4) to (index, three ints)."""
    q = normalize(q)
    index = np.argmax(np.abs(q), axis=-1)
    sign = np.where(np.take_along_axis(q, index[..., None], -1) < 0, -1.0, 1.0)
    q = q * sign
    order = np.array([[1, 2, 3], [0, 2, 3], [0, 1, 3], [0, 1, 2]])[index]
    rest = np.take_along_axis(q, order, -1)
    top = (1 << bits) - 1
    ints = np.clip(np.round((rest / SQRT1_2 + 1) / 2 * top), 0, top).astype(np.int64)
    return index.astype(np.uint8), ints


def from_smallest_three(index, ints, bits):
    top = (1 << bits) - 1
    rest = (ints / top * 2 - 1) * SQRT1_2
    largest = np.sqrt(np.clip(1 - np.sum(rest * rest, axis=-1), 0, 1))
    q = np.empty(rest.shape[:-1] + (4,))
    order = np.array([[1, 2, 3], [0, 2, 3], [0, 1, 3], [0, 1, 2]])[index]
    np.put_along_axis(q, order, rest, -1)
    np.put_along_axis(q, index[..., None].astype(np.int64), largest[..., None], -1)
    return normalize(q)


def delta(values):
    values = values.astype(np.int64)
    out = values.copy()
    out[1:] = values[1:] - values[:-1]
    return (out & 0xFFFF).astype("<u2")


def encode_positions(p):
    """p: (n, 3). Returns bytes and the decoded values."""
    low, high = p.min(0), p.max(0)
    if np.all(high - low == 0) or len(p) == 1:
        return struct.pack("<3f", *p[0]), np.repeat(p[:1].astype(np.float32).astype(float), len(p), 0), True
    scale = (high - low) / 65535
    scale[scale == 0] = 1
    ints = np.round((p - low) / scale).astype(np.int64)
    low32, scale32 = low.astype(np.float32).astype(float), scale.astype(np.float32).astype(float)
    data = struct.pack("<6f", *low32, *scale32) + b"".join(delta(ints[:, c]).tobytes() for c in range(3))
    return data, low32 + ints * scale32, False


def encode_rotations(q, bits):
    index, ints = smallest_three(q, bits)
    decoded = from_smallest_three(index, ints, bits)
    if np.all(index == index[0]) and np.all(ints == ints[0]):
        exact = q[0].astype(np.float32).astype(float)
        return struct.pack("<4f", *exact), np.repeat(normalize(exact)[None], len(q), 0), True
    data = index.tobytes() + b"".join(delta(ints[:, c]).tobytes() for c in range(3))
    return data, decoded, False


def encode_dense(p, q, tracks, bones, duration, bits):
    frames = len(p)
    head = b"GH3CLQ01" + struct.pack("<BBHHf", bits, bones, frames, len(tracks), duration)
    flags, pos_blocks, rot_blocks = [], [], []
    dp, dq = np.zeros_like(p), np.tile([0.0, 0, 0, 1], p.shape[:2] + (1,))
    for t, (bone, mask) in enumerate(tracks):
        flag = 0
        if mask & 1:
            data, dp[:, t], const = encode_positions(p[:, t])
            pos_blocks.append(data)
            flag |= const
        if mask & 2:
            data, dq[:, t], const = encode_rotations(q[:, t], bits)
            rot_blocks.append(data)
            flag |= const << 1
        flags.append(struct.pack("<BBB", bone, mask, flag))
    return head + b"".join(flags) + b"".join(pos_blocks) + b"".join(rot_blocks), dp, dq


def encode_soa_f32(p, q, tracks, bones, duration):
    head = b"GH3CLS01" + struct.pack("<BHHf", bones, len(p), len(tracks), duration)
    body = [struct.pack("<BB", b, m) for b, m in tracks]
    for t, (_, mask) in enumerate(tracks):
        if mask & 1:
            body.append(p[:, t].astype("<f4").T.tobytes())
        if mask & 2:
            body.append(q[:, t].astype("<f4").T.tobytes())
    return head + b"".join(body)


def encode_keys(key_tracks, tracks, bones, duration, frames, magic=b"GH3CLK01"):
    """key_tracks: per track (pos_times, pos_values, rot_times, rot_values) in ticks of 1/60 s."""
    head = magic + struct.pack("<BHfH", bones, len(tracks), duration, frames)
    infos, blocks, decoded = [], [], []
    for (bone, mask), (pt, pv, rt, rv) in zip(tracks, key_tracks):
        track_blocks = []
        dpv = drv = None
        if mask & 1:
            data, dpv, const = encode_positions(pv)
            if const:
                pt, dpv = pt[:1], dpv[:1]
            if len(pt) > 1:
                track_blocks.append(delta(pt).tobytes())
            track_blocks.append(data)
        if mask & 2:
            data, drv, const = encode_rotations(rv, 15)
            if const:
                rt, drv = rt[:1], drv[:1]
            if len(rt) > 1:
                track_blocks.append(delta(rt).tobytes())
            track_blocks.append(data)
        pc = len(pt) if mask & 1 else 0
        rc = len(rt) if mask & 2 else 0
        infos.append(struct.pack("<BBHH", bone, mask, pc, rc))
        blocks += track_blocks
        decoded.append((pt, dpv, rt, drv))
    return head + b"".join(infos) + b"".join(blocks), decoded


def reduce_keys(values, quaternion):
    """Greedy key reduction: keep a frame only when linear interpolation from the last key misses the tolerance."""
    values = values.copy()
    if quaternion:
        for i in range(1, len(values)):
            if np.dot(values[i - 1], values[i]) < 0:
                values[i] *= -1
    n = len(values)

    def fits(i, j):
        t = ((np.arange(i + 1, j) - i) / (j - i))[:, None]
        guess = values[i] + (values[j] - values[i]) * t
        if quaternion:
            return angle(normalize(guess), values[i + 1 : j]).max() <= ROT_TOLERANCE
        return (np.linalg.norm(guess - values[i + 1 : j], axis=-1) * 100).max() <= POS_TOLERANCE_CM

    keys, i = [0], 0
    while i < n - 1:
        good, span = i + 1, 2
        while i + span <= n - 1 and fits(i, i + span):
            good, span = i + span, span * 2
        bad = min(i + span, n)
        while bad - good > 1:
            middle = (good + bad) // 2
            if middle <= n - 1 and fits(i, middle):
                good = middle
            else:
                bad = middle
        keys.append(good)
        i = good
    return np.array(keys)


def interp_keys(times, values, at, quaternion):
    values = values.copy()
    if quaternion:
        for i in range(1, len(values)):
            if np.dot(values[i - 1], values[i]) < 0:
                values[i] *= -1
    result = np.stack([np.interp(at, times, values[:, c]) for c in range(values.shape[1])], 1)
    return normalize(result) if quaternion else result


def slerp(a, b, t):
    dot = np.sum(a * b, -1, keepdims=True)
    b = np.where(dot < 0, -b, b)
    dot = np.abs(dot)
    theta = np.arccos(np.clip(dot, -1, 1))
    sin = np.sin(theta)
    small = sin < 1e-6
    wa = np.where(small, 1 - t, np.sin((1 - t) * theta) / np.where(small, 1, sin))
    wb = np.where(small, t, np.sin(t * theta) / np.where(small, 1, sin))
    return normalize(wa * a + wb * b)


def runtime_sample(p, q, duration, at):
    """PerformanceClip.Apply: frame = t / duration * (frames - 1), lerp positions, slerp rotations."""
    frames = len(p)
    frame = np.clip(at / duration * (frames - 1), 0, frames - 1)
    a = frame.astype(int)
    b = np.minimum(a + 1, frames - 1)
    w = (frame - a)[:, None, None]
    return p[a] + (p[b] - p[a]) * w, slerp(q[a], q[b], w)


def angle(a, b):
    return np.degrees(2 * np.arccos(np.clip(np.abs(np.sum(a * b, -1)), 0, 1)))


def rotate(q, v):
    u, w = q[..., :3], q[..., 3:]
    t = 2 * np.cross(u, v)
    return v + w * t + np.cross(u, t)


def effectors_fk(p, q, tracks, rig, effectors):
    """World positions (metres) of effector bones. p, q are clip channels in packed (z-flipped) space."""
    _, parents, bind_p, bind_q = rig
    n = len(p)
    needed = set()
    for e in effectors:
        while e >= 0 and e not in needed:
            needed.add(e)
            e = parents[e]
    local_p = {i: np.broadcast_to(bind_p[i], (n, 3)) for i in needed}
    local_q = {i: np.broadcast_to(normalize(np.asarray(bind_q[i], float)), (n, 4)) for i in needed}
    for t, (bone, mask) in enumerate(tracks):
        if bone not in needed:
            continue
        if mask & 1:
            local_p[bone] = p[:, t] * [1, 1, -1]
        if mask & 2:
            local_q[bone] = q[:, t] * [1, 1, -1, 1]
    world_p, world_q = {}, {}
    for i in sorted(needed, key=lambda b: depth(parents, b)):
        rotation = local_q[i] * [-1, -1, -1, 1]
        if parents[i] < 0:
            world_p[i], world_q[i] = local_p[i], rotation
        else:
            parent = parents[i]
            world_p[i] = world_p[parent] + rotate(world_q[parent], local_p[i])
            world_q[i] = multiply_quaternions(world_q[parent], rotation)
    return np.stack([world_p[e] for e in effectors], 1)


def depth(parents, bone):
    d = 0
    while parents[bone] >= 0:
        bone, d = parents[bone], d + 1
    return d


class Stats:
    def __init__(self):
        self.hist = {}
        self.max = {}
        self.bone_max = {}

    def add(self, name, values):
        values = np.asarray(values, float).ravel()
        if values.size == 0:
            return
        counts = np.bincount(np.searchsorted(BINS, values), minlength=len(BINS) + 1)
        self.hist[name] = self.hist.get(name, 0) + counts
        self.max[name] = max(self.max.get(name, 0.0), float(values.max()))

    def bone(self, name, bone, value):
        k = (name, bone)
        self.bone_max[k] = max(self.bone_max.get(k, 0.0), float(value))

    def merge(self, other):
        for k, v in other.hist.items():
            self.hist[k] = self.hist.get(k, 0) + v
        for k, v in other.max.items():
            self.max[k] = max(self.max.get(k, 0.0), v)
        for k, v in other.bone_max.items():
            self.bone_max[k] = max(self.bone_max.get(k, 0.0), v)


def percentile(counts, fraction):
    cumulative = np.cumsum(counts)
    index = int(np.searchsorted(cumulative, fraction * cumulative[-1]))
    return float(BINS[min(index, len(BINS) - 1)])


def groups_of(rig):
    names, parents, _, _ = rig
    children = set(parents)

    def under(root):
        result = []
        for i in range(len(names)):
            j = parents[i]
            while j >= 0:
                if j == root:
                    result.append(i)
                    break
                j = parents[j]
        return result

    palm_l, palm_r, head = (names.index(key(n)) for n in ["Bone_Palm_L", "Bone_Palm_R", "Bone_Head"])
    fingers_l, fingers_r = under(palm_l), under(palm_r)
    tips_l = [i for i in fingers_l if i not in children]
    tips_r = [i for i in fingers_r if i not in children]
    effectors = [head, palm_l, palm_r, *tips_l, *tips_r]
    labels = ["head", "palm_L", "palm_R"] + ["tips_L"] * len(tips_l) + ["tips_R"] * len(tips_r)
    return set(fingers_l), set(fingers_r), effectors, labels


def record(stats, prefix, ref_p, ref_q, p, q, tracks, rig_groups, ref_eff, eff):
    fingers_l, fingers_r, _, labels = rig_groups
    for t, (bone, mask) in enumerate(tracks):
        if mask & 2:
            error = angle(ref_q[:, t], q[:, t])
            group = "fingers_L" if bone in fingers_l else "fingers_R" if bone in fingers_r else "body"
            stats.add(f"{prefix}/rot/{group}", error)
            stats.add(f"{prefix}/rot/all", error)
            stats.bone(f"{prefix}/rot", bone, error.max())
        if mask & 1:
            stats.add(f"{prefix}/pos_cm", np.linalg.norm(ref_p[:, t] - p[:, t], axis=-1) * 100)
    distance = np.linalg.norm(ref_eff - eff, axis=-1) * 100
    for label in set(labels):
        stats.add(f"{prefix}/eff/{label}", distance[:, [i for i, l in enumerate(labels) if l == label]])


def analyze(job):
    role, ref, song, data, rig, source_rig, rotations_only, current_path, out_dir = job
    names, parents, positions, rotations = rig
    source_names, _, source_positions, source_rotations = source_rig or rig
    clip = animation(data, len(source_names))
    duration = max(clip[0], 1 / 60)
    frames = max(2, round(duration * 30) + 1)
    mapping = [
        (i, names.index(source_names[i])) for i in sorted(clip[1].keys() | clip[2].keys()) if source_names[i] in names
    ]
    tracks = [
        (target, (1 if s in clip[2] and not rotations_only else 0) | (2 if s in clip[1] else 0))
        for s, target in mapping
    ]

    def convert(p, q):
        for s, target in mapping:
            if source_rig is not None:
                p[:, s] += positions[target] - source_positions[s]
                inverse = source_rotations[s] * [-1, -1, -1, 1]
                q[:, s] = multiply_quaternions(multiply_quaternions(q[:, s], inverse), rotations[target])
        idx = [s for s, _ in mapping]
        return p[:, idx] * [1, 1, -1], q[:, idx] * [1, 1, -1, 1]

    with gzip.open(current_path, "rb") as stream:
        raw_current = stream.read()
    _, bones, cframes, count, cduration = struct.unpack_from("<8sIIIf", raw_current)
    stored = [struct.unpack_from("<II", raw_current, 24 + 8 * i) for i in range(count)]
    if cframes != frames or stored != tracks:
        raise ValueError(f"{role}/{ref}: current clip does not match the source")
    packed = np.frombuffer(raw_current, "<f4", offset=24 + 8 * count).reshape(frames, count, 7).astype(float)
    cur_p, cur_q = packed[..., :3], normalize(packed[..., 3:])

    times60 = np.append(np.arange(0, math.floor(duration * 60 + 1e-6) + 1) / 60, duration)
    times60 = np.unique(np.clip(times60, 0, duration))
    gt_p, gt_q = convert(*sample_animation(clip, source_positions, source_rotations, times60))
    gt_q = normalize(gt_q)

    # Original keys, converted into packed space; translation times are checked for whole ticks.
    key_tracks, fractional = [], 0
    absolute = clip[3]
    for s, target in mapping:
        entry = []
        for channel in (2, 1):
            keys = clip[channel].get(s, [])
            if not keys:
                entry += [np.zeros(0, int), None]
                continue
            ticks = np.array([k[0] * 60 for k in keys])
            fractional += int(np.any(np.abs(ticks - np.round(ticks)) > 1e-3))
            values = np.array([k[1] for k in keys], float)
            if channel == 2:
                if not absolute:
                    values = source_positions[s] + values
                if source_rig is not None:
                    values = values + positions[target] - source_positions[s]
                values = values * [1, 1, -1]
            else:
                if source_rig is not None:
                    inverse = source_rotations[s] * [-1, -1, -1, 1]
                    values = multiply_quaternions(multiply_quaternions(values, inverse), rotations[target])
                values = normalize(values * [1, 1, -1, 1])
            entry += [np.round(ticks).astype(int), values]
        key_tracks.append(tuple(entry))

    rig_groups = groups_of(rig)
    effectors = rig_groups[2]
    stats = Stats()
    sizes = {}
    raw = {"f32": raw_current, "f32soa": encode_soa_f32(cur_p, cur_q, tracks, bones, duration)}
    decoded = {}
    for name, bits in [("q48", 15), ("q32", 10)]:
        raw[name], dp, dq = encode_dense(cur_p, cur_q, tracks, bones, duration, bits)
        decoded[name] = (dp, dq)
    raw["keys"], key_decoded = encode_keys(key_tracks, tracks, bones, duration, frames)
    frame_times = np.linspace(0, duration, frames)

    def dense_from_keys(at, keyed):
        p = np.repeat(cur_p[:1] * 0, len(at), 0)
        q = np.tile([0.0, 0, 0, 1], (len(at), len(tracks), 1))
        for t, ((bone, mask), (pt, pv, rt, rv)) in enumerate(zip(tracks, keyed)):
            if mask & 1 and pv is not None:
                p[:, t] = interp_keys(pt / 60, pv, at, False)
            if mask & 2 and rv is not None:
                q[:, t] = interp_keys(rt / 60, rv, at, True)
        return p, q

    decoded["keys"] = dense_from_keys(frame_times, key_decoded)
    q48p, q48q = decoded["q48"]
    reduced = []
    for t, (bone, mask) in enumerate(tracks):
        entry = [np.zeros(0, int), None, np.zeros(0, int), None]
        if mask & 1:
            k = reduce_keys(q48p[:, t], False)
            entry[0:2] = [k * 2, q48p[k, t]]
        if mask & 2:
            k = reduce_keys(q48q[:, t], True)
            entry[2:4] = [k * 2, q48q[k, t]]
        reduced.append(tuple(entry))
    # Key times are 30 fps frame indices; times * 2 maps them onto the 60 Hz tick grid of encode_keys.
    raw["q48kr"], reduced_decoded = encode_keys(reduced, tracks, bones, duration, frames, b"GH3CLR01")
    scaled = [
        (pt * duration / (frames - 1) * 30, pv, rt * duration / (frames - 1) * 30, rv)
        for pt, pv, rt, rv in reduced_decoded
    ]
    decoded["q48kr"] = dense_from_keys(frame_times, scaled)
    for name, data in raw.items():
        sizes[name] = {c: len(compress(data, c)) for c in COMPRESSORS}
    sizes["f32"]["file"] = Path(current_path).stat().st_size

    # Quantization alone: variant against the current clip at its own frames.
    cur_eff = effectors_fk(cur_p, cur_q, tracks, rig, effectors)
    for name in ["q48", "q32", "keys", "q48kr"]:
        p, q = decoded[name]
        record(
            stats,
            f"vsCurrent/{name}",
            cur_p,
            cur_q,
            p,
            q,
            tracks,
            rig_groups,
            cur_eff,
            effectors_fk(p, q, tracks, rig, effectors),
        )

    # Everything against the original GH3 keys, sampled at 60 Hz with the runtime interpolation.
    gt_eff = effectors_fk(gt_p, gt_q, tracks, rig, effectors)
    candidates = {"f32": (cur_p, cur_q), **decoded}
    for name, (p, q) in candidates.items():
        sp, sq = runtime_sample(p, q, duration, times60)
        record(
            stats,
            f"vsGH3/{name}",
            gt_p,
            gt_q,
            sp,
            sq,
            tracks,
            rig_groups,
            gt_eff,
            effectors_fk(sp, sq, tracks, rig, effectors),
        )
    kp, kq = dense_from_keys(times60, key_decoded)
    record(
        stats,
        "vsGH3/keys-direct",
        gt_p,
        gt_q,
        kp,
        kq,
        tracks,
        rig_groups,
        gt_eff,
        effectors_fk(kp, kq, tracks, rig, effectors),
    )

    for name in ["q48", "q32", "keys", "q48kr"]:
        for method in ["gzip9", "brotli11"]:
            path = out_dir / "variants" / f"{name}-{method}" / role / f"{ref}.bin"
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(compress(raw[name], method))
    rot_keys = sum(len(k[2]) for k, (_, m) in zip(key_tracks, tracks) if m & 2)
    pos_keys = sum(len(k[0]) for k, (_, m) in zip(key_tracks, tracks) if m & 1)
    info = {
        "role": role,
        "ref": ref,
        "song": song,
        "frames": frames,
        "tracks": len(tracks),
        "duration": duration,
        "rotTracks": sum(1 for _, m in tracks if m & 2),
        "posTracks": sum(1 for _, m in tracks if m & 1),
        "rotKeys": rot_keys,
        "posKeys": pos_keys,
        "reducedRotKeys": sum(len(r[2]) for r in reduced),
        "reducedPosKeys": sum(len(r[0]) for r in reduced),
        "fractionalTimes": fractional,
        "ska": len(data),
        "sizes": sizes,
    }
    return info, stats


def collect(data_directory, current, out_dir):
    jobs, state = {}, {"pak": ""}
    original_load = Source.load

    def load(self, relative):
        state["pak"] = relative
        return original_load(self, relative)

    def write_clip(path, data, rig, source_rig=None, rotations_only=False):
        role, ref = path.parent.name, path.name.split(".")[0]
        song = state["pak"].split("/")[-1].split("_song.")[0] if state["pak"].startswith("SONGS/") else ""
        jobs[(role, ref)] = (
            role,
            ref,
            song,
            bytes(data),
            rig,
            source_rig,
            rotations_only,
            str(current / role / f"{ref}.bin.gz"),
            out_dir,
        )
        return {"duration": 1.0, "frames": 2, "rootDistance": 0.0}

    Source.load = load
    direction.write_clip = write_clip
    direction.convert(data_directory, out_dir / "convert-scratch")
    return list(jobs.values())


def main():
    data_directory, current, out_dir = Path(sys.argv[1]), Path(sys.argv[2]), Path(sys.argv[3])
    workers = int(sys.argv[4]) if len(sys.argv) > 4 else 3
    jobs = collect(data_directory, current, out_dir)
    jobs.sort(key=lambda j: -len(j[3]))
    if len(sys.argv) > 5:
        jobs = jobs[:: int(sys.argv[5])]
    print(f"{len(jobs)} clips", flush=True)
    infos, totals = [], {}
    with Pool(workers, maxtasksperchild=40) as pool:
        for i, (info, stats) in enumerate(pool.imap_unordered(analyze, jobs)):
            infos.append(info)
            totals.setdefault(info["role"], Stats()).merge(stats)
            if i % 50 == 0:
                print(i, info["role"], info["ref"], flush=True)
    (out_dir / "clips.json").write_text(json.dumps(infos))
    with (out_dir / "stats.pickle").open("wb") as f:
        pickle.dump({r: (s.hist, s.max, s.bone_max) for r, s in totals.items()}, f)
    print("done")


if __name__ == "__main__":
    main()
