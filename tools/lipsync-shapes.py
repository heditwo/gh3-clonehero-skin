"""Measure the singer face poses of the GH3 song facial clips and write the C# tables of StageLipSync.

Usage: uv run --with numpy python tools/lipsync-shapes.py [--library assets/libraries/gh3-band-v1]
       [--output src/Stage/Band/StageLipSync.Shapes.cs]

The 570 singer facial clips (Band_PlayFacialAnim) animate bones 68-97 of GH3_singer_male. All face bones are children
of Bone_Head (head axes: x up, y forward, z to the singer's left). For each frame the script measures three mouth
features from the bone positions: the lip gap (upper against lower lip middle), the width (lower lip corners), and
the lip protrusion (forward motion of the lip middles, without the part that follows the lip gap). Each feature is
changed to its percentile rank. A viseme has a target rank for each feature; its pose is the mean pose of the
frames nearest to the target (all 18 mouth bones: rotation vector in degrees and translation, relative to the bind
pose). So each viseme is a real GH3 mouth pose, not a sum of independent components.

The brow, squint, and eye layers use the same method: the mean of the frames in the top 5 % of a feature (brow height,
lower lid lift) minus the mean of the middle frames. The eye statistics (look range, saccade intervals) are printed.
"""

import argparse
import gzip
import json
import struct
from pathlib import Path
from zlib import crc32

import numpy as np

MOUTH = [
    "Bone_Jaw",
    "Bone_Mouth_L",
    "Bone_Mouth_R",
    "Bone_Lip_Lower_Mid",
    "Bone_Lip_Upper_Mid",
    "Bone_Cheek_L",
    "Bone_Cheek_R",
    "Bone_Cheek_Upper_L",
    "Bone_Cheek_Upper_R",
    "Bone_Lip_Lower_Corner_L",
    "Bone_Lip_Lower_Corner_R",
    "Bone_Lip_Lower_L",
    "Bone_Lip_Lower_R",
    "Bone_Lip_Upper_Corner_L",
    "Bone_Lip_Upper_Corner_R",
    "Bone_Lip_Upper_L",
    "Bone_Lip_Upper_R",
    "Bone_Tongue",
]
BROWS = ["Bone_Brow_L", "Bone_Brow_R", "Bone_Brow_Mid", "Bone_Brow_Mid_L", "Bone_Brow_Mid_R"]
SQUINT = ["Bone_Eyelid_Lower_L", "Bone_Eyelid_Lower_R", "Bone_Cheek_Upper_L", "Bone_Cheek_Upper_R"]
EYES = ["Bone_Eye_L", "Bone_Eye_R"]
LIDS = ["Bone_Eyelid_Upper_L", "Bone_Eyelid_Upper_R"]
ALL = MOUTH + BROWS + ["Bone_Eyelid_Lower_L", "Bone_Eyelid_Lower_R"] + EYES + LIDS

# Viseme order of StageLipSync.Viseme (rest is the bind pose and has no row). Target percentile ranks of the lip gap,
# the width, and the protrusion. Tuned: by phonetic description (open, spread, rounded) against the GH3 frames.
VISEMES = {
    "AA": (95, 55, 45),
    "AH": (72, 50, 50),
    "EH": (78, 75, 40),
    "IY": (45, 85, 40),
    "OW": (82, 12, 85),
    "UW": (40, 4, 97),
    "ER": (45, 25, 75),
    "MBP": (1, 45, 50),
    "FV": (10, 55, 20),
    "TH": (30, 55, 45),
    "L": (55, 55, 45),
    "WR": (25, 8, 92),
    "SZ": (15, 65, 45),
    "SH": (30, 20, 88),
    "KG": (45, 55, 50),
}
NEIGHBORS = 400
LAYER_TOP = 95
LAYER_MIDDLE = (40, 60)
CLIP_FPS = 30
# A saccade: eye speed above 60 degrees per second after a frame below it.
SACCADE_SPEED = 60
ROUND_DEGREES = 0.005
ROUND_METRES = 2e-6


def key(text):
    return ~crc32(text.lower().encode()) & 0xFFFFFFFF


def qmul(a, b):
    x1, y1, z1, w1 = np.moveaxis(a, -1, 0)
    x2, y2, z2, w2 = np.moveaxis(b, -1, 0)
    return np.stack(
        [
            w1 * x2 + x1 * w2 + y1 * z2 - z1 * y2,
            w1 * y2 - x1 * z2 + y1 * w2 + z1 * x2,
            w1 * z2 + x1 * y2 - y1 * x2 + z1 * w2,
            w1 * w2 - x1 * x2 - y1 * y2 - z1 * z2,
        ],
        -1,
    )


def rotation_vector(q):
    q = q * np.sign(q[..., 3:4] + 1e-12)
    s = np.linalg.norm(q[..., :3], axis=-1, keepdims=True)
    return np.degrees(q[..., :3] / np.maximum(s, 1e-9) * 2 * np.arctan2(s, q[..., 3:4]))


def read_clip(path):
    with gzip.open(path) as file:
        data = file.read()
    if data[:8] != b"GH3CLP01":
        raise ValueError(path)
    frames, count = struct.unpack_from("<ii", data, 12)
    tracks = [struct.unpack_from("<i", data, 24 + 8 * i)[0] for i in range(count)]
    poses = np.frombuffer(data, "<f4", frames * count * 7, 24 + 8 * count).reshape(frames, count, 7).astype(np.float64)
    return tracks, poses


def load(library):
    """Return (frames (n, bones of ALL, 6) deltas from the bind pose, positions (n, bones, 3) in head space, clip lengths)."""
    member = json.loads((library / "direction/performance.json").read_text())["members"]["singer"]
    bones = [int(b, 16) for b in member["bones"]]
    raw = (library / "band/singer/animation.bin").read_bytes()
    count = struct.unpack_from("<i", raw, 8)[0]
    bind = np.array([struct.unpack_from("<7f", raw, 20 + 32 * i) for i in range(count)])
    body = {c for refs in [*member["groups"].values(), *member["layers"].values()] for c in refs}
    facial = sorted(set(member["clips"]) - body)
    index = [bones.index(key(name)) for name in ALL]
    inverse = bind[index, 3:] * np.array([-1, -1, -1, 1])
    deltas, positions, lengths = [], [], []
    for clip in facial:
        tracks, poses = read_clip(library / "direction/singer" / f"{clip}.bin.gz")
        sel = poses[:, [tracks.index(i) for i in index], :]
        rotation = rotation_vector(qmul(np.broadcast_to(inverse, sel[..., 3:].shape), sel[..., 3:]))
        deltas.append(np.concatenate([rotation, sel[..., :3] - bind[index, :3]], -1))
        positions.append(sel[..., :3])
        lengths.append(len(sel))
    return np.concatenate(deltas), np.concatenate(positions), np.array(lengths), len(facial)


def rank(values):
    return np.argsort(np.argsort(values)) * 100.0 / (len(values) - 1)


def mouth_features(positions):
    p = {name: positions[:, ALL.index(name)] for name in MOUTH}
    gap = p["Bone_Lip_Upper_Mid"][:, 0] - p["Bone_Lip_Lower_Mid"][:, 0]
    width = p["Bone_Lip_Lower_Corner_R"][:, 2] - p["Bone_Lip_Lower_Corner_L"][:, 2]
    forward = (p["Bone_Lip_Upper_Mid"][:, 1] + p["Bone_Lip_Lower_Mid"][:, 1]) / 2
    design = np.stack([np.ones_like(gap), gap], 1)
    protrusion = forward - design @ np.linalg.lstsq(design, forward, rcond=None)[0]
    return np.stack([rank(gap), rank(width), rank(protrusion)], 1), (gap, width, protrusion)


def layer(deltas, feature, bones):
    columns = [ALL.index(b) for b in bones]
    top = feature >= np.percentile(feature, LAYER_TOP)
    low, high = np.percentile(feature, LAYER_MIDDLE)
    middle = (feature >= low) & (feature <= high)
    return deltas[top][:, columns].mean(0) - deltas[middle][:, columns].mean(0)


def table(name, rows, labels, comment):
    lines = [f"    // {comment}", f"    private static readonly float[] {name} =", "    {"]
    for values, label in zip(rows, labels):
        values = values.copy()
        values[:3] = np.where(np.abs(values[:3]) < ROUND_DEGREES, 0, values[:3])
        values[3:] = np.where(np.abs(values[3:]) < ROUND_METRES, 0, values[3:])
        text = ", ".join(f"{v:.4f}f" if j < 3 else f"{v:.6f}f" for j, v in enumerate(values))
        lines.append(f"        {text.replace('-0.0000f', '0.0000f').replace('-0.000000f', '0.000000f')}, // {label}")
    lines.append("    };")
    return lines


def eye_statistics(deltas, lengths):
    yaw = deltas[:, ALL.index("Bone_Eye_L"), 0]
    pitch = deltas[:, ALL.index("Bone_Eye_L"), 2]
    intervals, saccades = [], 0
    for start, count in zip(np.cumsum(np.r_[0, lengths[:-1]]), lengths):
        speed = np.hypot(np.diff(yaw[start : start + count]), np.diff(pitch[start : start + count])) * CLIP_FPS
        found = np.where((speed[1:] > SACCADE_SPEED) & (speed[:-1] <= SACCADE_SPEED))[0]
        saccades += len(found)
        intervals += list(np.diff(found) / CLIP_FPS)
    return {
        "yaw": np.percentile(yaw, [5, 50, 95]),
        "pitch": np.percentile(pitch, [5, 50, 95]),
        "saccades_per_minute": saccades / (lengths.sum() / CLIP_FPS) * 60,
        "interval": np.percentile(intervals, [10, 50, 90]),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--library", type=Path, default=Path("assets/libraries/gh3-band-v1"))
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    deltas, positions, lengths, clips = load(args.library)
    ranks, (gap, width, protrusion) = mouth_features(positions)
    mouth = deltas[:, : len(MOUTH)]
    rows, fit = [], []
    for name, target in VISEMES.items():
        distance = np.linalg.norm(ranks - np.array(target), axis=1)
        nearest = np.argpartition(distance, NEIGHBORS)[:NEIGHBORS]
        rows.append(mouth[nearest].mean(0))
        reached = ranks[nearest].mean(0)
        fit.append(f"{name} target {target} reached ({reached[0]:.0f}, {reached[1]:.0f}, {reached[2]:.0f})")
    brow_height = positions[:, [ALL.index(b) for b in BROWS], 0].mean(1)
    lower_lid = -deltas[:, [ALL.index("Bone_Eyelid_Lower_L"), ALL.index("Bone_Eyelid_Lower_R")], 2].mean(1)
    brow = layer(deltas, brow_height, BROWS)
    frown = layer(deltas, -brow_height, BROWS)
    squint = layer(deltas, lower_lid, SQUINT)
    eyes = eye_statistics(deltas, lengths)
    jaw = deltas[:, 0, 2]
    summary = (
        f"{clips} facial clips, {len(deltas)} frames; jaw Z p5 {np.percentile(jaw, 5):.1f}, p50 {np.percentile(jaw, 50):.1f}, "
        f"p95 {np.percentile(jaw, 95):.1f}, max {jaw.max():.1f} degrees"
    )
    print(summary)
    print("\n".join(fit))
    print(
        f"eye yaw p5/p50/p95 {np.round(eyes['yaw'], 1)}, pitch {np.round(eyes['pitch'], 1)} degrees; "
        f"saccades {eyes['saccades_per_minute']:.1f} per minute, interval p10/p50/p90 {np.round(eyes['interval'], 2)} s"
    )
    print(
        f"lip gap p5/p95 {np.percentile(gap, 5):.4f}/{np.percentile(gap, 95):.4f} m, width {np.ptp(np.percentile(width, [5, 95])):.4f} m"
    )
    print(f"brow height p5/p95 {np.percentile(brow_height, 5):.4f}/{np.percentile(brow_height, 95):.4f} m")
    if not args.output:
        return
    lines = [
        "namespace Gh3Skin;",
        "",
        "// Measured: the face poses of the GH3 singer facial clips (tools/lipsync-shapes.py over the 570 clips). Do not edit:",
        "// run the tool again.",
        "internal sealed partial class StageLipSync",
        "{",
        "    // Per bone: rotation vector (degrees, local axes, applied after the current rotation) and translation (metres),",
        f"    // relative to the bind pose. {summary}.",
    ]
    lines += table(
        "VisemePoses",
        [r for row in rows for r in row.reshape(len(MOUTH), 6)],
        [f"{name} {bone}" for name in VISEMES for bone in MOUTH],
        f"One block of {len(MOUTH)} MouthBones rows for each viseme after Rest: the mean of the {NEIGHBORS} frames nearest to the target.",
    )
    lines += table("BrowRaisePose", brow, BROWS, "BrowBones: the top 5 % brow height minus the middle frames.")
    lines += table("BrowLowerPose", frown, BROWS, "BrowBones: the lowest 5 % brow height minus the middle frames.")
    lines += table("SquintPose", squint, SQUINT, "SquintBones: the top 5 % lower lid lift minus the middle frames.")
    lines.append("}")
    args.output.write_text("\n".join(lines) + "\n")


if __name__ == "__main__":
    main()
