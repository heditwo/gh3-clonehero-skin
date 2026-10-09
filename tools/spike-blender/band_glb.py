"""Write a band member from the library (meshes.bin, animation.bin, stage.json) as a skinned GLB with the original rig."""

import json
import struct
import sys
from pathlib import Path

import numpy as np
from PIL import Image

from pipeline.stagepack.gltf import SKINNED_GH3_VERTEX, GlbWriter


def read_animation(path):
    data = Path(path).read_bytes()
    if data[:8] != b"GH3ANM01": raise ValueError("Expected GH3ANM01")
    bones, clips = struct.unpack_from("<II", data, 8)
    cursor = 16
    rows = np.array([struct.unpack_from("<i7f", data, cursor+i*32) for i in range(bones)]); cursor += bones*32
    parents = rows[:, 0].astype(int).tolist()
    # The library stores the Unity convention (z negated); GlbWriter.actor takes GH3 positions and quaternions.
    positions = rows[:, 1:4]*[1, 1, -1]; rotations = rows[:, 4:8]*[1, 1, -1, 1]
    result = []
    for _ in range(clips):
        duration, frames = struct.unpack_from("<fI", data, cursor); cursor += 8
        poses = np.frombuffer(data, "<f4", frames*bones*7, cursor).reshape(frames, bones, 7).astype(float); cursor += frames*bones*28
        result.append((duration, poses[..., :3]*[1, 1, -1], poses[..., 3:]*[1, 1, -1, 1]))
    return parents, positions, rotations, result


def read_meshes(path):
    data = Path(path).read_bytes()
    if data[:8] != b"GH3BND02": raise ValueError("Expected GH3BND02")
    count, = struct.unpack_from("<I", data, 8); cursor = 12; meshes = []
    for _ in range(count):
        material, vertices, indices = struct.unpack_from("<III", data, cursor); cursor += 12
        packed = data[cursor:cursor+vertices*SKINNED_GH3_VERTEX.itemsize]; cursor += len(packed)
        faces = np.frombuffer(data, "<u4", indices, cursor).reshape(-1, 3).tolist(); cursor += indices*4
        meshes.append((f"{material:08x}", vertices, packed, faces))
    return meshes


def main(library, role, output, seconds=2.0):
    folder = Path(library)/"band"/role
    bones = json.loads((Path(library)/"catalog.json").read_text())["members"][role]["bones"]
    parents, positions, rotations, clips = read_animation(folder/"animation.bin")
    if len(bones) != len(parents): raise ValueError("Catalog bone count differs from animation.bin")
    materials = json.loads((folder/"stage.json").read_text())["materials"]
    textures = {p.stem: Image.open(p).convert("RGBA") for p in (folder/"textures").glob("*.png")}
    duration, p, q = clips[0]
    frames = min(len(p), round(seconds*30)+1)
    writer = GlbWriter()
    writer.actor(read_meshes(folder/"meshes.bin"), materials, textures, ([int(b, 16) for b in bones], parents, positions, rotations),
                 [("clip0", (frames-1)/30, p[:frames], q[:frames])])
    # Probe extras: does Blender keep node extras on bones and on the skinned mesh node?
    for node in writer.data["nodes"]:
        node["extras"] = {"gh3Probe": node["name"], "gh3Role": role}
    writer.data["meshes"][0]["extras"] = {"gh3ProbeMesh": role}
    writer.save(output)


def tag(source, output):
    """Copy a venue actor GLB and add the same probe extras to its nodes and mesh."""
    data = Path(source).read_bytes()
    length, = struct.unpack_from("<I", data, 12)
    gltf = json.loads(data[20:20+length]); binary = data[20+length:]
    for node in gltf["nodes"]: node["extras"] = {**node.get("extras", {}), "gh3Probe": node.get("name", ""), "gh3Role": "venue"}
    for mesh in gltf["meshes"]: mesh["extras"] = {"gh3ProbeMesh": "venue"}
    header = json.dumps(gltf, separators=(",", ":")).encode(); header += b" "*(-len(header) % 4)
    Path(output).write_bytes(struct.pack("<5I", 0x46546c67, 2, 20+len(header)+len(binary), len(header), 0x4e4f534a)+header+binary)


if __name__ == "__main__":
    if sys.argv[1] == "tag": tag(*sys.argv[2:4])
    else: main(*sys.argv[1:4])
