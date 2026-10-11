"""Measure the resting face of the Project SEKAI 3D MVs: the blend shape weights of the face clips when no mouth shape is on.

Usage: uv run --with numpy python tools/research/neutral-face/sekai_face_rest.py SHAPE_NAMES.txt FACE.anim...

The face clips are the `live_pv/timeline/<mv>/character/face_<mv>_01.anim` files of the sekai.best asset archive (Unity
AnimationClip as JSON). SHAPE_NAMES.txt has the blend shape channel names of the face FBX, one for each line. The clips
bind each curve by the CRC-32 of "blendShape.<deformer>.<shape>"; the deformer names are not in the archive, so the
script finds the CRC state of each prefix family (mouth_, eyeblow_, eye_, look_) by the vote of all curves and names.
A rest frame: all vowel shapes below 2 and eye_close below 10 (eyes open, mouth closed).
"""

import collections
import json
import struct
import sys

import numpy as np

CRC_POLYNOMIAL = 0xEDB88320
VOWELS = {f"mouth_{v}{n}" for v in "aiueo" for n in ("", "2", "3")}
FAMILIES = ("mouth_", "eyeblow_", "eye_", "look_")
REST_VOWEL = 2
REST_EYE_CLOSE = 10
SAMPLE_RATE = 30
MINIMUM_REST_FRAMES = 30


def _table():
    rows = []
    for i in range(256):
        c = i
        for _ in range(8):
            c = (c >> 1) ^ CRC_POLYNOMIAL if c & 1 else c >> 1
        rows.append(c)
    return rows


TABLE = _table()
TOP = {row >> 24: i for i, row in enumerate(TABLE)}


def unappend(crc, suffix):
    """The CRC-32 of a text whose CRC-32 with `suffix` appended is `crc`."""
    r = crc ^ 0xFFFFFFFF
    for b in reversed(suffix):
        i = TOP[r >> 24]
        r = ((r ^ TABLE[i]) << 8 | (i ^ b)) & 0xFFFFFFFF
    return r ^ 0xFFFFFFFF


def family_prefixes(clips, names):
    """The CRC state of the binding prefix of each name family, by the vote over all curves."""
    result = {}
    for family in FAMILIES:
        votes = collections.Counter()
        for attributes in clips:
            votes.update(unappend(t, ("." + n).encode()) for t in attributes for n in names if n.startswith(family))
        result[family] = votes.most_common(1)[0][0]
    return result


def curve_names(attributes, names, prefixes):
    def family(name):
        return next(prefixes[f] for f in FAMILIES if name.startswith(f))

    found = []
    for t in attributes:
        hits = [n for n in names if unappend(t, ("." + n).encode()) == family(n)]
        found.append(hits[0] if len(hits) == 1 else None)
    return found


def _float(bits):
    return struct.unpack("<f", struct.pack("<I", bits))[0]


def sample(clip):
    """(times, values (frames, curves)) of the streamed, dense, and constant curves of a clip."""
    data = clip["m_MuscleClip"]["m_Clip"]["data"]
    stream = data["m_StreamedClip"]
    words, frames, i = stream["data"], [], 0
    while i < len(words):
        time, count = _float(words[i]), words[i + 1]
        i += 2
        keys = []
        for _ in range(count):
            keys.append((words[i], [_float(w) for w in words[i + 1 : i + 5]]))
            i += 5
        frames.append((time, keys))
    end = max(t for t, _ in frames if np.isfinite(t))
    times = np.arange(0, end, 1 / SAMPLE_RATE)
    values = np.zeros((len(times), stream["curveCount"]))
    current, k = {}, 0
    for row, t in enumerate(times):
        while k < len(frames) and frames[k][0] <= t:
            for index, coefficients in frames[k][1]:
                current[index] = (frames[k][0], coefficients)
            k += 1
        for index, (start, c) in current.items():
            d = t - start
            values[row, index] = ((c[0] * d + c[1]) * d + c[2]) * d + c[3]
    dense, constant = data["m_DenseClip"], data["m_ConstantClip"]["data"]
    extra = [values]
    if dense["m_CurveCount"]:
        table = np.array(dense["m_SampleArray"], dtype=float).reshape(-1, dense["m_CurveCount"])
        rows = np.clip(((times - dense["m_BeginTime"]) * dense["m_SampleRate"]).astype(int), 0, len(table) - 1)
        extra.append(table[rows])
    if constant:
        extra.append(np.tile(np.array(constant, dtype=float), (len(times), 1)))
    return times, np.hstack(extra)


def main():
    names = sorted({line.strip() for line in open(sys.argv[1], encoding="utf-8") if line.strip()})
    clips = [json.load(open(path)) for path in sys.argv[2:]]
    attributes = [[b["attribute"] for b in c["m_ClipBindingConstant"]["genericBindings"]] for c in clips]
    prefixes = family_prefixes(attributes, names)
    pooled, per_clip = collections.defaultdict(list), collections.defaultdict(list)
    for clip, binding in zip(clips, attributes):
        labels = curve_names(binding, names, prefixes)
        if None in labels:
            continue
        _, values = sample(clip)
        column = {label: values[:, i] for i, label in enumerate(labels)}
        vowel = np.max([column[label] for label in labels if label in VOWELS], axis=0)
        rest = (vowel < REST_VOWEL) & (column.get("eye_close", np.zeros(len(vowel))) < REST_EYE_CLOSE)
        if rest.sum() < MINIMUM_REST_FRAMES:
            continue
        for label in labels:
            if label not in VOWELS:
                pooled[label].append(column[label][rest])
                per_clip[label].append(float(np.median(column[label][rest])))
    print(f"clips {len(clips)}, with rest frames {len(per_clip.get('mouth_happy', []))}")
    print("shape              median of clip medians   pooled mean   clips > 5")
    for label in sorted(per_clip, key=lambda k: -np.median(per_clip[k])):
        x = np.array(per_clip[label])
        print(f"{label:18s} {np.median(x):8.1f} {np.concatenate(pooled[label]).mean():18.1f} {np.mean(x > 5):11.2f}")


if __name__ == "__main__":
    main()
