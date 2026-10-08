#!/usr/bin/env python3
"""Export the original GH3 light housings used by the venue's lightshow."""

import argparse
import importlib.util
import json
from pathlib import Path
import struct

from gh3_direction import Source, vector
from gh3_formats import find_array, find_struct, key


def module(name):
    spec = importlib.util.spec_from_file_location(name.replace("-", "_"), Path(__file__).with_name(name + ".py"))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


stage = module("prepare-stage")
lighting = module("prepare-lighting")
MODELS = {
    key("GO_LightHousing01"): (0x8f01c57b, 0x9f37b8b8),
    key("GO_LightHousing01_SmallFlare01"): (0xb624f112, 0xa6128cd1),
    key("GO_TankHousing01"): (0xdc6d54b0, 0xcc5b2973),
}


def instances(nodes, snapshot):
    beams = {row[key("HousingChecksum")]: row for row in snapshot if key("HousingChecksum") in row}
    output = []
    for node in nodes:
        model = node.get(key("Type"))
        if model not in MODELS or key("CreatedAtStart") not in node.get(0, []):
            continue
        beam = beams[node[key("Name")]]
        fixed = key("IgnoreSnapshotPositions") in node.get(0, [])
        output.append({"id": f'{node[key("Name")]:08x}', "model": f"{model:08x}",
                       "position": vector(node[key("Pos")] if fixed else beam[key("Pos")]),
                       "rotation": lighting.beam_rotation(beam[key("Quat")]), "fixedPosition": fixed})
    return output


def convert(directory, destination):
    source = Source(directory)
    entries = source.load("ZONES/z_budokan.pak.xen")
    qbs = [b for k, _, b in entries if k in (key(".qb"), key(".nqb"))]
    nodes = next(a for b in qbs if (a := find_array(b, "z_budokan_gfx_NodeArray")) is not None)
    snapshots = next(a for b in qbs if (a := find_struct(b, "z_budokan_snapshots")) is not None)
    housings = instances(nodes, snapshots[key("Intro01")])
    global_entries = {(k, n): b for k, n, b in source.load("ZONES/global.pak.xen")}
    for model in sorted({h["model"] for h in housings}):
        geometry_id, texture_id = MODELS[int(model, 16)]
        folder = destination / model
        (folder / "textures").mkdir(parents=True, exist_ok=True)
        materials, meshes = stage.scene(global_entries[(key(".mdl"), geometry_id)])
        meshes = [mesh for mesh in meshes if materials[mesh[0]]["blend"] == 0]
        used = {mesh[0] for mesh in meshes}
        materials = {name: material for name, material in materials.items() if name in used}
        textures = dict(stage.textures(global_entries[(key(".tex"), texture_id)]))
        for material in materials.values():
            material["texture"] = stage.diffuse_texture(material, textures)
            if not material["texture"]:
                raise ValueError("Missing light housing texture")
            name = material["texture"]
            textures[name].save(folder / "textures" / f"{name}.png")
        with (folder / "meshes.bin").open("wb") as output:
            output.write(b"GH3STG03" + struct.pack("<I", len(meshes)))
            for material, count, vertices, faces in meshes:
                output.write(struct.pack("<III", int(material, 16), count, len(faces) * 3))
                output.write(vertices)
                for face in faces:
                    output.write(struct.pack("<3I", *face))
        (folder / "stage.json").write_text(json.dumps({"version": 1, "name": model,
            "cameraPosition": [0, 0, 0], "cameraTarget": [0, 0, 1], "materials": materials}, indent=2) + "\n")
    (destination / "fixtures.json").write_text(json.dumps({"version": 1, "housings": housings,
        "sources": source.hashes}, indent=2, allow_nan=False) + "\n")
    print(f"Exported {len(housings)} original light housings from {len({h['model'] for h in housings})} models")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data", type=Path, default=Path.home() / "games/gh3-test-tools/game/DATA")
    parser.add_argument("--output", type=Path, default=Path(__file__).parent / "assets/stages/budokan/fixtures")
    args = parser.parse_args()
    convert(args.data, args.output)
