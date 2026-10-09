"""Spike: write one compiled GH3 band member (GH3BND02 + GH3ANM01 bind pose) as a skinned GLB.

uv run --with pillow --with numpy python -m pipeline.spike.character_glb --role guitarist --output /tmp/opencode/spike-skinned/guitarist.glb
"""

import argparse
import json
import struct
import time
from pathlib import Path

import numpy as np
from PIL import Image

from pipeline.stagepack.gltf import REFLECTION, SKINNED_GH3_VERTEX, GlbReader, GlbWriter

LIBRARY = Path(__file__).resolve().parents[2]/"assets"/"libraries"/"gh3-band-v1"


def read_meshes(path):
    data = path.read_bytes()
    if data[:8] != b"GH3BND02": raise ValueError("Expected GH3BND02 meshes")
    count, = struct.unpack_from("<I", data, 8); cursor = 12; parts = []
    for _ in range(count):
        material, vertices, indices = struct.unpack_from("<III", data, cursor); cursor += 12
        size = vertices*SKINNED_GH3_VERTEX.itemsize
        packed = data[cursor:cursor+size]; cursor += size
        faces = np.frombuffer(data, "<u4", indices, cursor).reshape(-1, 3); cursor += indices*4
        parts.append((f"{material:08x}", vertices, packed, faces.tolist()))
    if cursor != len(data): raise ValueError("Extra GH3BND02 data")
    return parts


def read_bind(path):
    data = path.read_bytes()
    if data[:8] != b"GH3ANM01": raise ValueError("Expected GH3ANM01 animation")
    count, = struct.unpack_from("<I", data, 8)
    rows = [struct.unpack_from("<i7f", data, 16+i*32) for i in range(count)]
    parents = [r[0] for r in rows]
    positions = np.array([r[1:4] for r in rows])
    rotations = np.array([r[4:8] for r in rows])
    return parents, positions, rotations


def export(role, output):
    folder = LIBRARY/"band"/role
    parts = read_meshes(folder/"meshes.bin")
    materials = json.loads((folder/"stage.json").read_text())["materials"]
    textures = {p.stem: Image.open(p).convert("RGBA") for p in (folder/"textures").glob("*.png")}
    parents, positions, rotations = read_bind(folder/"animation.bin")
    names = [int(n, 16) for n in json.loads((LIBRARY/"direction"/"performance.json").read_text())["members"][role]["bones"]]
    if len(names) != len(parents): raise ValueError("Bone names differ from the skeleton")
    # animation.bin holds the Unity-space bind pose (z negated); GlbWriter.actor takes GH3 space with conjugated rotations.
    rig = (names, parents, positions*[1, 1, -1], rotations*[1, 1, -1, 1])
    writer = GlbWriter()
    writer.actor(parts, materials, textures, rig, [])
    writer.save(output)
    return parts


def check(parts, output):
    glb = GlbReader(output)
    nodes = {i: m for i, _, m in glb.nodes()}
    skin = glb.data["skins"][0]
    inverse = glb.accessor(skin["inverseBindMatrices"]).reshape(-1, 4, 4).transpose(0, 2, 1)
    ibm_error = max(float(np.abs(nodes[j] @ REFLECTION @ inverse[i] @ REFLECTION - np.eye(4)).max()) for i, j in enumerate(skin["joints"]))
    primitives = glb.data["meshes"][0]["primitives"]
    position_error = 0.0; weight_error = 0.0; vertices = 0
    for (material, count, packed, faces), primitive in zip([p for p in parts if p[3]], primitives):
        original = np.frombuffer(packed, dtype=SKINNED_GH3_VERTEX)
        loaded, _ = glb.primitive(primitive, np.eye(4))
        position_error = max(position_error, float(np.abs(loaded["position"]-original["position"]).max()))
        weight_error = max(weight_error, float(np.abs(glb.accessor(primitive["attributes"]["WEIGHTS_0"])-original["weights"]).max()))
        vertices += count
    images = sum(glb.data["bufferViews"][i["bufferView"]]["byteLength"] for i in glb.data["images"])
    return {"primitives": len(primitives), "vertices": vertices, "joints": len(skin["joints"]), "images": len(glb.data["images"]),
            "imageBytes": images, "jsonBytes": len(json.dumps(glb.data, separators=(",", ":"))), "binaryBytes": len(glb.binary),
            "glbBytes": Path(output).stat().st_size, "maxInverseBindError": ibm_error, "maxPositionError": position_error,
            "maxWeightError": weight_error}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--role", default="guitarist")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    start = time.perf_counter()
    parts = export(args.role, args.output)
    report = {"exportSeconds": round(time.perf_counter()-start, 3), **check(parts, args.output)}
    folder = LIBRARY/"band"/args.role
    report["meshesBinBytes"] = (folder/"meshes.bin").stat().st_size
    report["textureFolderBytes"] = sum(p.stat().st_size for p in (folder/"textures").glob("*.png"))
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
