"""Run the existing stage-pack actor compiler (pipeline.stagepack.actor.compile_actor) on GLBs; print pass or the error.

With --tolerant, first drop the material extras that hold glTF texture indices (Blender renumbers textures; package format
v1 forbids them), to show what else the compiler would reject. The compiler drops unit scale channels itself.
"""

import json
import shutil
import struct
import sys
import tempfile
from pathlib import Path

import numpy as np

from pipeline.stagepack.actor import compile_actor
from pipeline.stagepack.gltf import GlbReader
from pipeline.stagepack.pack import import_materials, split_mesh, write_meshes


def tolerant(path, output):
    glb = GlbReader(path)
    data = glb.data
    scale_error = 0.0
    for track in data.get("animations", []):
        kept = []
        for channel in track["channels"]:
            if channel["target"]["path"] == "scale":
                scale_error = max(
                    scale_error, float(np.abs(glb.accessor(track["samplers"][channel["sampler"]]["output"]) - 1).max())
                )
            kept.append(channel)
        track["channels"] = kept
    for material in data.get("materials", []):
        for name in ("gh3Shader", "gh3MaskTexture", "gh3LayerTexture"):
            material.get("extras", {}).pop(name, None)
    header = json.dumps(data, separators=(",", ":")).encode()
    header += b" " * (-len(header) % 4)
    binary = glb.binary + b"\0" * (-len(glb.binary) % 4)
    Path(output).write_bytes(
        struct.pack("<5I", 0x46546C67, 2, 28 + len(header) + len(binary), len(header), 0x4E4F534A)
        + header
        + struct.pack("<2I", len(binary), 0x004E4942)
        + binary
    )
    return scale_error


arguments = sys.argv[1:]
relaxed = arguments[0] == "--tolerant"
for path in arguments[relaxed:]:
    work = Path(tempfile.mkdtemp(dir="/tmp/opencode/spike-blender"))
    folder = work / "actor"
    note = ""
    try:
        source = Path(path)
        if relaxed:
            source = work / "input.glb"
            note = f" (dropped texture-index extras; max |scale-1| {tolerant(path, source):.2g})"
        clips = compile_actor(source, folder, import_materials, write_meshes, split_mesh)
        print(f"{Path(path).name}: pass, {len(clips)} clips{note}")
    except Exception as error:
        print(f"{Path(path).name}: FAIL {type(error).__name__}: {error}{note}")
    finally:
        shutil.rmtree(work)
