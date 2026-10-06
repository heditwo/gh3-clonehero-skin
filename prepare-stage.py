#!/usr/bin/env python3
"""Convert a local GH3 PC venue to the skin's static mesh format."""

import argparse
from collections import Counter
from hashlib import sha256
from io import BytesIO
import json
import math
from pathlib import Path
import struct

from PIL import Image
from gh3_formats import find_array, key, pak_entries, values, word
from gh3_mesh_cutout import trim_prop_cutouts


def textures(data):
    if word(data, 0) != 0xFACECAA7:
        raise ValueError("Expected a GH3 PC texture dictionary")
    count = values(data, 6, "H")[0]
    table = word(data, 8)
    for index in range(count):
        header = table + 40 * index
        name = word(data, header + 4)
        start, size = values(data, header + 28, "II")
        image = Image.open(BytesIO(data[start:start + size])).convert("RGBA")
        yield f"{name:08x}", image


def triangles(indices, kind):
    if kind == 4:
        if len(indices) % 3:
            raise ValueError("Incomplete triangle")
        result = [tuple(indices[i:i + 3]) for i in range(0, len(indices), 3)]
    elif kind == 6:
        result, strip = [], []
        for index in indices:
            if index in (0x7FFF, 0xFFFF):
                strip.clear()
                continue
            strip.append(index)
            n = len(strip)
            if n >= 3:
                a, b, c = strip[-3:]
                result.append((a, b, c) if n % 2 else (a, c, b))
    else:
        raise ValueError(f"Unsupported face format {kind}")
    return [(a, c, b) for a, b, c in result if a != b and a != c and b != c]


def rotate(vector, angles):
    x, y, z = vector
    for axis, angle in enumerate(angles):
        c, s = math.cos(angle), math.sin(angle)
        if axis == 0:
            y, z = y * c - z * s, y * s + z * c
        elif axis == 1:
            x, z = x * c + z * s, -x * s + z * c
        else:
            x, y = x * c - y * s, x * s + y * c
    return x, y, z


def diffuse_texture(material, available):
    samples = material["textures"]
    if material["template"] in {"852105c8", "acd09a98", "6f3f03f3", "4ec03414", "4d7335f8"}:
        samples = samples[1:2]
    return next((name for name in samples if name in available), "")


def crowd_uvs(vertices, count, faces, seed, stride):
    parents = list(range(count))
    def root(index):
        while parents[index] != index:
            parents[index] = parents[parents[index]]
            index = parents[index]
        return index
    for face in faces:
        for vertex in face[1:]: parents[root(vertex)] = root(face[0])
    if any(size > 6 for size in Counter(root(v) for v in range(count)).values()):
        raise ValueError("Crowd meshes must use independent crowd cards")
    people = {}
    for vertex in range(count):
        person = people.setdefault(root(vertex), len(people))
        tile = (person+seed) % 24
        u, v = struct.unpack_from("<2f", vertices, vertex*stride+24)
        struct.pack_into("<2f", vertices, vertex*stride+24, (tile % 8+u)/8, 1-(tile//8+1-v)/3)


def scene(data, nodes=None, skinned=False, light_groups=None, mesh_nodes=None, crowd_cards=True):
    count = values(data, 34, "H")[0]
    end = 32 + word(data, 36)
    offset = 48
    materials = {}
    for _ in range(count):
        name = f"{word(data, offset):08x}"
        sample_count, sample_offset = values(data, offset + 184, "II")
        samples = values(data, offset + sample_offset, f"{sample_count}I")
        materials[name] = {
            "textures": [f"{sample:08x}" for sample in samples],
            "template": f"{word(data, offset + 160):08x}",
            "blend": word(data, offset + 216),
            "cutoff": data[offset + 232] / 255,
        }
        size = word(data, offset + 196)
        if size < 256:
            raise ValueError("Invalid material size")
        offset += size
    if offset != end:
        raise ValueError("Invalid material table size")
    base = end + 8 + word(data, end + 4) if word(data, end) == 0xBABEFACE else (end + 127) // 128 * 128
    mesh_count = word(data, base + 76)
    mesh_table = base + word(data, base + 128)
    placements = {}
    if nodes is not None:
        nodes = {node[key("Name")]: node for node in nodes if key("Name") in node}
        for index in range(word(data, base + 108)):
            sector = base + word(data, base + 112) + index * 96
            geom = base + word(data, base + 116) + index * 96
            node = nodes.get(word(data, sector + 4))
            for mesh in range(word(data, geom + 72), word(data, geom + 72) + word(data, geom + 76)):
                placements[mesh] = node
    meshes = []
    for index in range(mesh_count):
        node = placements.get(index)
        if node is not None and key("CreatedAtStart") not in node[0]:
            continue
        header = mesh_table + 128 * index
        material = f"{word(data, header + 20):08x}"
        flags = word(data, header + 28)
        index_count, vertex_count = values(data, header + 36, "HH")
        kind = word(data, header + 40)
        vertices_at = base + word(data, header + 64) + 32
        indices_at = base + word(data, header + 92) + 32
        stride = data[header + 90]
        weighted = bool(flags & 0x80)
        if weighted and not skinned:
            raise ValueError("Skinned meshes need a skeleton; expected static venue geometry")
        weights = []
        weighted_vertices = []
        if weighted:
            cursor = base + word(data, header + 96)
            groups = values(data, cursor + 20, "3I")
            cursor += 32
            for influences, group_count in enumerate(groups, 1):
                for _ in range(group_count):
                    count = word(data, cursor)
                    bones = values(data, cursor + 4, "4B")
                    cursor += 16
                    for _ in range(count):
                        row = values(data, cursor, "14f")
                        weighted_vertices.append(((row[0], row[4], row[8]), (row[1], row[5], row[9])))
                        amounts = (row[12], row[13], 1 - row[12] - row[13])[:influences]
                        amounts = [max(0, amount) for amount in amounts]
                        total = sum(amounts)
                        if total <= 0 or not math.isfinite(total):
                            raise ValueError("Invalid skin weights")
                        weights.append(struct.pack("<4B4f", *bones[:influences], *([0] * (4 - influences)),
                                                   *(amount / total for amount in amounts), *([0] * (4 - influences))))
                        cursor += 56
            if len(weights) != vertex_count:
                raise ValueError("Skin weight count does not match vertices")
        tangent_count = 2 if flags & 0x800 else 1 if flags & 0x400 else 0
        uv_count = sum(bool(flags & (1 << bit)) for bit in range(16, 24))
        vertex_data = bytearray()
        for vertex in range(vertex_count):
            start = vertices_at + vertex * stride
            if weighted:
                position, normal = weighted_vertices[vertex]
                cursor = start
            else:
                position = values(data, start, "3f")
                cursor = start + 12 + (12 if flags & 0x1000 else 0)
                normal = values(data, cursor, "3f")
                cursor += 12 + tangent_count * 12 + (4 if flags & 0x100 else 0)
            if node is not None and node.get(key("Class")) == key("LevelObject"):
                angles = node.get(key("Angles"), (0, 0, 0))
                position = tuple(a + b for a, b in zip(rotate(position, angles), node[key("Pos")]))
                normal = rotate(normal, angles)
            color = (255, 255, 255, 255)
            if flags & 0x10:
                a, r, g, b = values(data, cursor, "4B")
                color = (r, g, b, a)
                cursor += 4
            cursor += 4 if flags & 0x4000 and not weighted else 0
            uv = values(data, cursor, "2f") if uv_count else (0, 0)
            expected = cursor + uv_count * 8 + (8 if flags & 0x0C000000 else 0) + (8 if flags & 0x03000000 else 0)
            if expected != start + stride:
                raise ValueError(f"Unsupported vertex layout {flags:08x}: {expected - start} != {stride}")
            floats = (*position[:2], -position[2], *normal[:2], -normal[2], uv[0], 1 - uv[1])
            if not all(math.isfinite(value) for value in floats):
                raise ValueError("Non-finite vertex")
            vertex_data.extend(struct.pack("<8f4B", *floats, *color))
            if skinned:
                vertex_data.extend(weights[vertex] if weighted else struct.pack("<4B4f", 0, 0, 0, 0, 1, 0, 0, 0))
        faces = triangles(values(data, indices_at, f"{index_count}H"), kind)
        if any(max(face) >= vertex_count for face in faces):
            raise ValueError("Triangle index outside vertex buffer")
        if crowd_cards and materials[material]["template"] == "a17d40ed":
            crowd_uvs(vertex_data, vertex_count, faces, index, 56 if skinned else 36)
        if material not in materials:
            raise ValueError("Mesh has no material")
        meshes.append((material, vertex_count, vertex_data, faces))
        if mesh_nodes is not None:
            mesh_nodes.append(node)
        if light_groups is not None:
            light_groups.append(f"{(node or {}).get(key('LightGroup'), key('None')):08x}")
    return materials, meshes


def convert(pak_path, destination):
    source = pak_path.read_bytes()
    entries = list(pak_entries(source))
    geometry = max((data for kind, _, data in entries if kind == key(".scn")), key=len)
    texture_data = max((data for kind, _, data in entries if kind == key(".tex")), key=len)
    venue = pak_path.name.split(".")[0]
    nodes = next((array for _, _, data in entries
                  if len(data) > 28 and data[8:12] == bytes.fromhex("1c080204")
                  if (array := find_array(data, venue + "_NodeArray")) is not None), None)
    if nodes is None:
        raise ValueError("The venue has no node array")
    light_groups = []
    materials, meshes = scene(geometry, nodes, light_groups=light_groups)
    destination.mkdir(parents=True, exist_ok=True)
    texture_dir = destination / "textures"
    texture_dir.mkdir(exist_ok=True)
    available = set()
    for name, image in textures(texture_data):
        image.save(texture_dir / f"{name}.png")
        available.add(name)
    for material in materials.values():
        material["texture"] = diffuse_texture(material, available)
        if material["template"] == "a17d40ed":
            if "6085e166" not in available:
                raise ValueError("This venue needs a different crowd atlas")
            material["texture"] = "6085e166"
            material["cutoff"] = 0.3
            material["blend"] = 3
            material["crowd"] = True
    meshes = trim_prop_cutouts(meshes, materials, texture_dir)
    with (destination / "meshes.bin").open("wb") as output:
        output.write(b"GH3STG01" + struct.pack("<I", len(meshes)))
        for material, count, vertices, faces in meshes:
            output.write(struct.pack("<III", int(material, 16), count, len(faces) * 3))
            output.write(vertices)
            for face in faces:
                output.write(struct.pack("<3I", *face))
    manifest = {
        "version": 1,
        "name": "Kaiju Megadome" if "budokan" in pak_path.name else pak_path.stem,
        "source": pak_path.name,
        "sourceSha256": sha256(source).hexdigest(),
        "cameraPosition": [0, 4.5, -20],
        "cameraTarget": [0, 4, 3],
        "fieldOfView": 55,
        "pyroPositions": [[-7, 0.5, -1], [-4, 0.5, -1], [4, 0.5, -1], [7, 0.5, -1]],
        "materials": materials,
        "lightGroups": light_groups,
    }
    (destination / "stage.json").write_text(json.dumps(manifest, indent=2) + "\n")
    print(f"Converted {len(meshes)} meshes, {sum(m[1] for m in meshes)} vertices, {len(available)} textures")
    print(f"Material blend modes: {dict(Counter(m['blend'] for m in materials.values()))}")
    print(destination)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pak", type=Path, default=Path.home() / "games/gh3-test-tools/game/DATA/ZONES/z_budokan.pak.xen")
    parser.add_argument("--output", type=Path, default=Path(__file__).parent / "assets/stages/budokan")
    args = parser.parse_args()
    convert(args.pak, args.output)
