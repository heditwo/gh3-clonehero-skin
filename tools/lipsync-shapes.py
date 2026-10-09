"""Measure the singer mouth shapes of the GH3 song facial clips and print the C# table of StageLipSync.

Usage: uv run --with numpy python tools/lipsync-shapes.py [assets/libraries/gh3-band-v1]

The 570 singer facial clips (Band_PlayFacialAnim) animate bones 68-97 of GH3_singer_male. For each mouth bone, the
script fits the local pose (rotation vector and translation relative to the bind pose) against the jaw opening
(rotation about the jaw Z axis) over all frames: the slope is the "open" shape per jaw degree. The first principal
component of the residuals is the mouth width, scaled to its 95th percentile ("round" when positive: the corners
move in; "wide" when negative).
"""

import gzip
import json
import struct
import sys
from pathlib import Path
from zlib import crc32

import numpy as np

MOUTH = ["Bone_Jaw", "Bone_Mouth_L", "Bone_Mouth_R", "Bone_Lip_Lower_Mid", "Bone_Lip_Upper_Mid", "Bone_Cheek_L", "Bone_Cheek_R",
         "Bone_Cheek_Upper_L", "Bone_Cheek_Upper_R", "Bone_Lip_Lower_Corner_L", "Bone_Lip_Lower_Corner_R", "Bone_Lip_Lower_L",
         "Bone_Lip_Lower_R", "Bone_Lip_Upper_Corner_L", "Bone_Lip_Upper_Corner_R", "Bone_Lip_Upper_L", "Bone_Lip_Upper_R", "Bone_Tongue"]
LIDS = ["Bone_Eyelid_Upper_L", "Bone_Eyelid_Upper_R"]


def key(text):
    return ~crc32(text.lower().encode()) & 0xFFFFFFFF


def qmul(a, b):
    x1, y1, z1, w1 = np.moveaxis(a, -1, 0)
    x2, y2, z2, w2 = np.moveaxis(b, -1, 0)
    return np.stack([w1 * x2 + x1 * w2 + y1 * z2 - z1 * y2, w1 * y2 - x1 * z2 + y1 * w2 + z1 * x2,
                     w1 * z2 + x1 * y2 - y1 * x2 + z1 * w2, w1 * w2 - x1 * x2 - y1 * y2 - z1 * z2], -1)


def rotation_vector(q):
    q = q * np.sign(q[..., 3:4] + 1e-12)
    s = np.linalg.norm(q[..., :3], axis=-1, keepdims=True)
    return np.degrees(q[..., :3] / np.maximum(s, 1e-9) * 2 * np.arctan2(s, q[..., 3:4]))


def read_clip(path):
    data = gzip.open(path).read()
    if data[:8] != b"GH3CLP01": raise ValueError(path)
    frames, count = struct.unpack_from("<ii", data, 12)
    tracks = [struct.unpack_from("<i", data, 24 + 8 * i)[0] for i in range(count)]
    poses = np.frombuffer(data, "<f4", frames * count * 7, 24 + 8 * count).reshape(frames, count, 7).astype(np.float64)
    return tracks, poses


def main():
    library = Path(sys.argv[1] if len(sys.argv) > 1 else "assets/libraries/gh3-band-v1")
    member = json.loads((library / "direction/performance.json").read_text())["members"]["singer"]
    bones = [int(b, 16) for b in member["bones"]]
    raw = (library / "band/singer/animation.bin").read_bytes()
    count = struct.unpack_from("<i", raw, 8)[0]
    bind = np.array([struct.unpack_from("<7f", raw, 20 + 32 * i) for i in range(count)])
    body = {c for refs in [*member["groups"].values(), *member["layers"].values()] for c in refs}
    facial = sorted(set(member["clips"]) - body)
    index = [bones.index(key(name)) for name in MOUTH + LIDS]
    inverse = bind[index, 3:] * np.array([-1, -1, -1, 1])
    columns = []
    for clip in facial:
        tracks, poses = read_clip(library / "direction/singer" / f"{clip}.bin.gz")
        sel = poses[:, [tracks.index(i) for i in index], :]
        rotation = rotation_vector(qmul(np.broadcast_to(inverse, sel[..., 3:].shape), sel[..., 3:]))
        columns.append(np.concatenate([rotation, sel[..., :3] - bind[index, :3]], -1))
    frames = np.concatenate(columns)
    jaw = frames[:, 0, 2]
    mouth = frames[:, :len(MOUTH)].reshape(len(frames), -1)
    design = np.stack([np.ones_like(jaw), jaw], 1)
    fit = np.linalg.lstsq(design, mouth, rcond=None)[0]
    residual = mouth - design @ fit
    scale = residual.std(0) + 1e-9
    width = np.linalg.svd((residual / scale)[::5], full_matrices=False)[2][0] * scale
    left, right = MOUTH.index("Bone_Mouth_L") * 6 + 5, MOUTH.index("Bone_Mouth_R") * 6 + 5
    if width[left] - width[right] < 0: width = -width
    score = residual @ width / (width @ width)
    width *= np.percentile(np.abs(score), 95)
    lids = frames[:, len(MOUTH):, 2]
    print(f"// {len(facial)} facial clips, {len(frames)} frames; jaw Z p5 {np.percentile(jaw, 5):.1f}, p50 {np.percentile(jaw, 50):.1f}, "
          f"p95 {np.percentile(jaw, 95):.1f}, max {jaw.max():.1f} degrees; upper lid Z p50 {np.percentile(lids, 50):.1f}, p99 {np.percentile(lids, 99):.1f}")
    for name, shape in [("Open", fit[1]), ("Round", width)]:
        print(f"    private static readonly float[] {name} =\n    {{")
        for i, bone in enumerate(MOUTH):
            values = shape[6 * i:6 * i + 6].copy()
            values[:3] = np.where(np.abs(values[:3]) < 0.005, 0, values[:3])
            values[3:] = np.where(np.abs(values[3:]) < 2e-6, 0, values[3:])
            text = ", ".join(f"{v:.4f}f" if j < 3 else f"{v:.6f}f" for j, v in enumerate(values)).replace("-0.0000f", "0.0000f").replace("-0.000000f", "0.000000f")
            print(f"        {text}, // {bone}")
        print("    };")


if __name__ == "__main__":
    main()
