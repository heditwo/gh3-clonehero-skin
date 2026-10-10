"""Read GH3 PC scene (.scn, .skin) and texture (.tex) files: materials with their GH3 template records, vertex streams, and images."""

import math
import struct
from collections import Counter
from io import BytesIO

import numpy as np
from PIL import Image

from pipeline.gh3 import materials as gh3_materials
from pipeline.gh3.formats import key, values, word

# AnimatedTexture, AnimatedTexture_Lit, and AnimatedTexture_Billboard share one vertex shader atlas step:
# c0 columns, c1 rows, c2 frames per second, c3 frame offset.
ANIMATED_TEXTURE_TEMPLATES = {"9893d156", "f76185ad", "9224d2a1"}
# Original pixel and vertex shaders: (diffuse UV set, layer sampler, layer UV set, mask sampler, mask UV set, layer colour constant).
# D_3ChkBox_Pass0_Pass1 and D_N_S_4ChkBox_Pass1 blend a second texture over the diffuse by layer alpha x mask red x vertex alpha.
# D_3ChkBox_Pass1 and Wibble_Simple multiply the output alpha by the mask's red channel.
LAYERED_TEMPLATES = {
    "D_3ChkBox_Pass0_Pass1": (0, 1, 1, 2, 2, 1),
    "D_3ChkBox_Pass1": (1, None, None, 1, 2, None),
    "Wibble_Simple": (0, None, None, 1, 1, None),
    "D_N_S_4ChkBox_Pass1": (0, 3, 1, 4, 1, 4),
}
WIBBLE_SIMPLE = "d825b71c"


def cube_map(blob):
    """GH3 environment maps are DDS cube maps (DDSCAPS2_CUBEMAP)."""
    return blob[:4] == b"DDS " and len(blob) >= 128 and struct.unpack_from("<I", blob, 112)[0] & 0x200 != 0


def cube_to_equirect(blob):
    """Resample an uncompressed DDS cube map to the latitude-longitude image that gh3_shader's gh3_cube() reads."""
    height, width = struct.unpack_from("<2I", blob, 12)
    flags, _, bits, *masks = struct.unpack_from("<7I", blob, 80)
    mips = max(1, struct.unpack_from("<I", blob, 28)[0])
    if width != height or bits != 32 or not flags & 0x40 or struct.unpack_from("<I", blob, 112)[0] & 0xFE00 != 0xFE00:
        raise ValueError("Unsupported GH3 cube map layout")
    pixels = np.frombuffer(blob, "<u4", offset=128)
    face_words = sum((width >> level) ** 2 for level in range(mips))

    def channel(mask, values):
        shift = (mask & -mask).bit_length() - 1
        return ((values & mask) >> shift).astype(np.uint8)

    faces = []
    for face in range(6):
        level0 = pixels[face * face_words : face * face_words + width * width].reshape(width, width)
        faces.append(np.stack([channel(mask, level0) for mask in masks], axis=-1))
    out_height = 2 * width
    out_width = 4 * width
    v, u = np.meshgrid(
        (np.arange(out_height) + 0.5) / out_height, (np.arange(out_width) + 0.5) / out_width, indexing="ij"
    )
    phi = (u - 0.5) * 2 * np.pi
    theta = v * np.pi
    x, y, z = np.sin(theta) * np.sin(phi), np.cos(theta), np.sin(theta) * np.cos(phi)
    ax, ay, az = np.abs(x), np.abs(y), np.abs(z)
    # D3D face selection and (sc, tc) per face: +X, -X, +Y, -Y, +Z, -Z.
    face = np.where(
        (ax >= ay) & (ax >= az), np.where(x > 0, 0, 1), np.where(ay >= az, np.where(y > 0, 2, 3), np.where(z > 0, 4, 5))
    )
    major = np.choose(face, [ax, ax, ay, ay, az, az])
    sc = np.choose(face, [-z, z, x, x, x, -x]) / major
    tc = np.choose(face, [-y, -y, z, -z, -y, -y]) / major
    column = np.clip(((sc + 1) / 2 * width).astype(int), 0, width - 1)
    row = np.clip(((tc + 1) / 2 * width).astype(int), 0, width - 1)
    image = np.zeros((out_height, out_width, 4), np.uint8)
    for index in range(6):
        selected = face == index
        image[selected] = faces[index][row[selected], column[selected]]
    return Image.fromarray(image, "RGBA")


def mark_cube_samplers(materials, cubes):
    """The equirectangular image of a GH3 cube map wraps in U and clamps in V, whatever the cube sampler's address mode is."""
    for material in materials.values():
        gh3 = material.get("gh3", {})
        for slot, name in enumerate(gh3.get("textures", [])):
            if name in cubes:
                gh3["addressing"][slot] = 2


def cube_names(data):
    """Names of the cube maps in a GH3 texture dictionary."""
    table = word(data, 8)
    result = set()
    for index in range(values(data, 6, "H")[0]):
        start, size = values(data, table + 40 * index + 28, "II")
        if cube_map(data[start : start + size]):
            result.add(f"{word(data, table + 40 * index + 4):08x}")
    return result


def textures(data):
    if word(data, 0) != 0xFACECAA7:
        raise ValueError("Expected a GH3 PC texture dictionary")
    count = values(data, 6, "H")[0]
    table = word(data, 8)
    for index in range(count):
        header = table + 40 * index
        name = word(data, header + 4)
        start, size = values(data, header + 28, "II")
        blob = data[start : start + size]
        image = cube_to_equirect(blob) if cube_map(blob) else Image.open(BytesIO(blob)).convert("RGBA")
        yield f"{name:08x}", image


def triangles(indices, kind):
    if kind == 4:
        if len(indices) % 3:
            raise ValueError("Incomplete triangle")
        result = [tuple(indices[i : i + 3]) for i in range(0, len(indices), 3)]
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


SECOND_SAMPLER_DIFFUSE = {"852105c8", "acd09a98", "6f3f03f3", "4ec03414", "4d7335f8"}


def diffuse_texture(material, available):
    samples = material["textures"]
    if material["template"] in SECOND_SAMPLER_DIFFUSE:
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
        for vertex in face[1:]:
            parents[root(vertex)] = root(face[0])
    if any(size > 6 for size in Counter(root(v) for v in range(count)).values()):
        raise ValueError("Crowd meshes must use independent crowd cards")
    people = {}
    for vertex in range(count):
        person = people.setdefault(root(vertex), len(people))
        tile = (person + seed) % 24
        u, v = struct.unpack_from("<2f", vertices, vertex * stride + 24)
        struct.pack_into("<2f", vertices, vertex * stride + 24, (tile % 8 + u) / 8, 1 - (tile // 8 + 1 - v) / 3)


# Extended static vertex: the GH3 vertex streams that the original vertex shaders read, unchanged: UV sets 0–3,
# texcoord4/texcoord5 (the 0x03000000 and 0x0C000000 UV streams), binormal 0, position 1 (billboard pivot), and binormal 1.
# Positions and directions use the stage mesh convention (z negated); UVs keep D3D's top-down V.
GH3_STREAMS = struct.Struct("<12f3f3f3f")
STATIC_STRIDE = 60 + GH3_STREAMS.size
# Extended skinned vertex (GH3BND02): the 56-byte skinned vertex, then UV sets 0–3, texcoord4, texcoord5, binormal 0, and
# tangent 0. GH3 PC skins on the CPU; its weighted rows hold position, normal, binormal 0, and tangent 0 for each vertex.
SKIN_STREAMS = struct.Struct("<12f3f3f")
SKINNED_STRIDE = 56 + SKIN_STREAMS.size


def material_record(data, offset, samples):
    """The GH3 material data that selects and drives its template: constant banks, samplers, and render state."""

    def bank(at):
        count, start = values(data, offset + at, "II")
        return [list(values(data, offset + start + i * 16, "4f")) for i in range(count)]

    return {
        "template": f"{word(data, offset + 160):08x}",
        "vertexConstants": bank(168),
        "pixelConstants": bank(176),
        "textures": [f"{sample:08x}" for sample in samples],
        # Per-sampler address bits (+234): bit 0 clamps U, bit 1 clamps V.
        "addressing": list(data[offset + 234 : offset + 234 + len(samples)]),
        # Render state, as GH3's material bind applies it: flags (+204), blend mode (+216), alpha reference (+232).
        "flags": word(data, offset + 204),
        "blend": word(data, offset + 216),
        "alphaRef": data[offset + 232],
        "drawOrder": values(data, offset + 212, "f")[0],
    }


def place_vertices(vertices, count, angles=(0, 0, 0), origin=(0, 0, 0), scale=(1, 1, 1)):
    """Rotate, scale, and move stage-convention vertices (z negated), including the GH3 position-1 and binormal streams."""
    vertices = bytearray(vertices)
    stride = len(vertices) // count

    def point(x, y, z):
        p = rotate((x * scale[0], y * scale[1], -z * scale[2]), angles)
        return p[0] + origin[0], p[1] + origin[1], -(p[2] + origin[2])

    def direction(x, y, z):
        d = rotate((x, y, -z), angles)
        return d[0], d[1], -d[2]

    for vertex in range(count):
        at = vertex * stride
        x, y, z, nx, ny, nz = struct.unpack_from("<6f", vertices, at)
        struct.pack_into("<6f", vertices, at, *point(x, y, z), *direction(nx, ny, nz))
        if stride == STATIC_STRIDE:
            streams = at + 60 + 48
            b = struct.unpack_from("<3f", vertices, streams)
            p = struct.unpack_from("<3f", vertices, streams + 12)
            b1 = struct.unpack_from("<3f", vertices, streams + 24)
            struct.pack_into("<9f", vertices, streams, *direction(*b), *point(*p), *direction(*b1))
    return bytes(vertices)


def scene(data, nodes=None, skinned=False, light_groups=None, mesh_nodes=None, crowd_cards=True, include_nodes=()):
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
            "doubleSided": word(data, offset + 220) != 0,
            "gh3": material_record(data, offset, samples),
        }
        template = gh3_materials.templates().get(materials[name]["template"])
        if template is not None:
            constant_count, constant_offset = values(data, offset + 176, "II")
            constants = [values(data, offset + constant_offset + i * 16, "4f") for i in range(min(constant_count, 16))]
            slot = gh3_materials.lightmap_slot(template, sample_count)
            materials[name]["shading"] = template["shading"]
            if template["diffuseConstant"] is not None and template["diffuseConstant"] < len(constants):
                materials[name]["color"] = [max(0.0, v) for v in constants[template["diffuseConstant"]]]
            if slot is not None:
                materials[name]["lightmap"] = materials[name]["textures"][slot]

            def uv_scale(sampler):
                entry = template["uvScales"].get(sampler)
                return constants[entry[0]][entry[1]] if entry and entry[0] < len(constants) else 1.0

            layers = LAYERED_TEMPLATES.get(template["name"], (0, None, None, None, None, None))
            scales = [
                uv_scale(1 if materials[name]["template"] in SECOND_SAMPLER_DIFFUSE else 0),
                uv_scale(layers[1]) if layers[1] is not None else 1.0,
                uv_scale(layers[3]) if layers[3] is not None else 1.0,
            ]
            if scales != [1, 1, 1]:
                materials[name]["uvScale"] = scales
            if template["name"] in LAYERED_TEMPLATES:
                textures = materials[name]["textures"]
                diffuse_set, layer, layer_set, mask, mask_set, layer_constant = layers
                materials[name]["uvSets"] = (diffuse_set, layer_set, mask_set)
                materials[name]["mask"] = textures[mask]
                if layer is not None:
                    if len(constants) <= layer_constant:
                        raise ValueError("Layered material has no layer colour")
                    base = materials[name].get("color", [1, 1, 1, 1])
                    # The diffuse constant is baked into vertex colours; the layer uses lighting x vertex colour x its own constant.
                    materials[name]["layer"] = {
                        "texture": textures[layer],
                        "color": [c / max(b, 1e-3) for c, b in zip(constants[layer_constant], base)],
                    }
        if materials[name]["template"] in ANIMATED_TEXTURE_TEMPLATES:
            constant_count, constant_offset = values(data, offset + 168, "II")
            if constant_count != 4:
                raise ValueError("Animated texture has an invalid vertex constant bank")
            columns, rows, fps, frame_offset = [
                values(data, offset + constant_offset + i * 16, "f")[0] for i in range(4)
            ]
            materials[name]["textureAnimation"] = {
                "columns": int(columns),
                "rows": int(rows),
                "fps": fps,
                "offset": frame_offset,
            }
        if materials[name]["template"] == WIBBLE_SIMPLE:
            constant_count, constant_offset = values(data, offset + 168, "II")
            if constant_count != 11:
                raise ValueError("Wibble_Simple has an invalid vertex constant bank")
            motion = [values(data, offset + constant_offset + i * 16, "f")[0] for i in range(11)]
            # The pixel shader scales the UV after the vertex shader's turn and scroll, so the pivot and scroll scale with it.
            scale = materials[name].get("uvScale", [1, 1, 1])[0]
            materials[name]["uvWibble"] = [v * scale if i in (0, 1, 4, 5, 9, 10) else v for i, v in enumerate(motion)]
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
    unlightmapped = set()
    for index in range(mesh_count):
        node = placements.get(index)
        if node is not None and key("CreatedAtStart") not in node[0] and node.get(key("Name")) not in include_nodes:
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
                        weighted_vertices.append(
                            (
                                (row[0], row[4], row[8]),
                                (row[1], row[5], row[9]),
                                (row[2], row[6], row[10]),
                                (row[3], row[7], row[11]),
                            )
                        )
                        amounts = (row[12], row[13], 1 - row[12] - row[13])[:influences]
                        amounts = [max(0, amount) for amount in amounts]
                        total = sum(amounts)
                        if total <= 0 or not math.isfinite(total):
                            raise ValueError("Invalid skin weights")
                        weights.append(
                            struct.pack(
                                "<4B4f",
                                *bones[:influences],
                                *([0] * (4 - influences)),
                                *(amount / total for amount in amounts),
                                *([0] * (4 - influences)),
                            )
                        )
                        cursor += 56
            if len(weights) != vertex_count:
                raise ValueError("Skin weight count does not match vertices")
        tangent_count = 2 if flags & 0x800 else 1 if flags & 0x400 else 0
        uv_count = sum(bool(flags & (1 << bit)) for bit in range(16, 24))
        vertex_data = bytearray()
        for vertex in range(vertex_count):
            start = vertices_at + vertex * stride
            tangent = (0, 0, 0)
            if weighted:
                position, normal, binormal, tangent = weighted_vertices[vertex]
                pivot, binormal1 = position, (0, 0, 0)
                cursor = start
            else:
                position = values(data, start, "3f")
                pivot = values(data, start + 12, "3f") if flags & 0x1000 else position
                cursor = start + 12 + (12 if flags & 0x1000 else 0)
                normal = values(data, cursor, "3f")
                binormal = values(data, cursor + 12, "3f") if tangent_count else (0, 0, 0)
                binormal1 = values(data, cursor + 24, "3f") if tangent_count > 1 else (0, 0, 0)
                cursor += 12 + tangent_count * 12 + (4 if flags & 0x100 else 0)
            if node is not None and node.get(key("Class")) == key("LevelObject"):
                angles = node.get(key("Angles"), (0, 0, 0))
                position = tuple(a + b for a, b in zip(rotate(position, angles), node[key("Pos")]))
                normal = rotate(normal, angles)
                if not weighted:
                    pivot = tuple(a + b for a, b in zip(rotate(pivot, angles), node[key("Pos")]))
                    binormal = rotate(binormal, angles)
                    binormal1 = rotate(binormal1, angles)
            color = (255, 255, 255, 255)
            if flags & 0x10:
                a, r, g, b = values(data, cursor, "4B")
                color = (r, g, b, a)
                cursor += 4
            cursor += 4 if flags & 0x4000 and not weighted else 0
            sets = {}
            for uv_set in range(4):
                if flags & (3 << (16 + 2 * uv_set)):
                    sets[uv_set] = values(data, cursor + len(sets) * 8, "2f")
            if len(sets) != uv_count:
                raise ValueError(f"Unsupported UV set flags {flags:08x}")
            uv_sets = materials[material].get("uvSets")
            uv_scale = materials[material].get("uvScale", (1, 1, 1))
            uv = sets.get(uv_sets[0], (0, 0)) if uv_sets else next(iter(sets.values()), (0, 0))
            layer_uv = sets.get(uv_sets[1], (0, 0)) if uv_sets and uv_sets[1] is not None else (0, 0)
            mask_uv = sets.get(uv_sets[2], (0, 0)) if uv_sets else (0, 0)
            uv, layer_uv, mask_uv = ((u * scale, v * scale) for (u, v), scale in zip((uv, layer_uv, mask_uv), uv_scale))
            extra = cursor + uv_count * 8
            lightmap = values(data, extra, "2f") if flags & 0x0F000000 else (0, 0)
            expected = extra + (8 if flags & 0x0C000000 else 0) + (8 if flags & 0x03000000 else 0)
            streams = [sets.get(n, (0, 0)) for n in range(4)] + [(0, 0), (0, 0)]
            if flags & 0x0C000000:
                streams[5] = lightmap
            if flags & 0x03000000:
                streams[4] = values(data, extra + (8 if flags & 0x0C000000 else 0), "2f")
            if expected != start + stride:
                raise ValueError(f"Unsupported vertex layout {flags:08x}: {expected - start} != {stride}")
            floats = (*position[:2], -position[2], *normal[:2], -normal[2], uv[0], 1 - uv[1])
            if not all(math.isfinite(value) for value in floats + lightmap):
                raise ValueError("Non-finite vertex")
            # One Backyard Bash layer material (ca426c7d) stores NaN in its second UV set; sample its layer at the origin.
            layer_uv = layer_uv if all(map(math.isfinite, layer_uv)) else (0, 0)
            mask_uv = mask_uv if all(map(math.isfinite, mask_uv)) else (0, 0)
            vertex_data.extend(struct.pack("<8f4B", *floats, *color))
            if not skinned:
                vertex_data.extend(
                    struct.pack(
                        "<6f", lightmap[0], 1 - lightmap[1], layer_uv[0], 1 - layer_uv[1], mask_uv[0], 1 - mask_uv[1]
                    )
                )
                raw = (
                    *(c for uv_pair in streams for c in uv_pair),
                    binormal[0],
                    binormal[1],
                    -binormal[2],
                    pivot[0],
                    pivot[1],
                    -pivot[2],
                    binormal1[0],
                    binormal1[1],
                    -binormal1[2],
                )
                vertex_data.extend(GH3_STREAMS.pack(*(v if math.isfinite(v) else 0.0 for v in raw)))
            if skinned:
                vertex_data.extend(weights[vertex] if weighted else struct.pack("<4B4f", 0, 0, 0, 0, 1, 0, 0, 0))
                raw = (
                    *(c for uv_pair in streams for c in uv_pair),
                    binormal[0],
                    binormal[1],
                    -binormal[2],
                    tangent[0],
                    tangent[1],
                    -tangent[2],
                )
                vertex_data.extend(SKIN_STREAMS.pack(*(v if math.isfinite(v) else 0.0 for v in raw)))
        faces = triangles(values(data, indices_at, f"{index_count}H"), kind)
        if any(max(face) >= vertex_count for face in faces):
            raise ValueError("Triangle index outside vertex buffer")
        if crowd_cards and materials[material]["template"] == "a17d40ed":
            crowd_uvs(vertex_data, vertex_count, faces, index, SKINNED_STRIDE if skinned else STATIC_STRIDE)
        if material not in materials:
            raise ValueError("Mesh has no material")
        if skinned or not flags & 0x0F000000:
            unlightmapped.add(material)
        meshes.append((material, vertex_count, vertex_data, faces))
        if mesh_nodes is not None:
            mesh_nodes.append(node)
        if light_groups is not None:
            light_groups.append(f"{(node or {}).get(key('LightGroup'), key('None')):08x}")
    for material in unlightmapped:
        materials[material].pop("lightmap", None)
    return materials, meshes


def mesh_sectors(data):
    """Map each mesh index of a GH3 scene to the checksum name of its sector."""
    end = 32 + word(data, 36)
    base = end + 8 + word(data, end + 4) if word(data, end) == 0xBABEFACE else (end + 127) // 128 * 128
    result = {}
    for index in range(word(data, base + 108)):
        sector = word(data, base + word(data, base + 112) + index * 96 + 4)
        geom = base + word(data, base + 116) + index * 96
        for mesh in range(word(data, geom + 72), word(data, geom + 72) + word(data, geom + 76)):
            result[mesh] = sector
    return result
