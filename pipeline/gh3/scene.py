"""Read GH3 PC scene (.scn, .skin) and texture (.tex) files: materials with their GH3 template records, vertex streams, and images.

GH3 PC stores these files big-endian; the DDS images inside them are little-endian. The record layouts below were
found by reading the GH3 PC files (no public specification).
"""

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
ANIMATED_TEXTURE_CONSTANTS = 4
# Original pixel and vertex shaders: (diffuse UV set, layer sampler, layer UV set, mask sampler, mask UV set, layer colour constant).
# D_3ChkBox_Pass0_Pass1 and D_N_S_4ChkBox_Pass1 blend a second texture over the diffuse by layer alpha x mask red x vertex alpha.
# D_3ChkBox_Pass1 and Wibble_Simple multiply the output alpha by the mask's red channel.
LAYERED_TEMPLATES = {
    "D_3ChkBox_Pass0_Pass1": (0, 1, 1, 2, 2, 1),
    "D_3ChkBox_Pass1": (1, None, None, 1, 2, None),
    "Wibble_Simple": (0, None, None, 1, 1, None),
    "D_N_S_4ChkBox_Pass1": (0, 3, 1, 4, 1, 4),
}
NOT_LAYERED = (0, None, None, None, None, None)
WIBBLE_SIMPLE = "d825b71c"
WIBBLE_SIMPLE_CONSTANTS = 11
# Wibble_Simple vertex constants that are UV distances (pivot and scroll); the others are angles and speeds.
WIBBLE_UV_CONSTANTS = (0, 1, 4, 5, 9, 10)
# Templates whose diffuse texture is the second sampler.
SECOND_SAMPLER_DIFFUSE = {"852105c8", "acd09a98", "6f3f03f3", "4ec03414", "4d7335f8"}
CROWD_TEMPLATE = "a17d40ed"
# The crowd atlas has 8 x 3 people; each person is one card of at most 6 vertices.
CROWD_COLUMNS, CROWD_ROWS = 8, 3
CROWD_CARD_VERTICES = 6
# Smallest base colour channel that the layer colour is divided by.
MIN_LAYER_BASE = 1e-3

# DDS header (Microsoft DirectDraw Surface reference).
DDS_MAGIC = b"DDS "
DDS_HEADER_SIZE = 128
DDS_HEIGHT, DDS_MIP_COUNT, DDS_PIXEL_FORMAT_FLAGS, DDS_CAPS2 = 12, 28, 80, 112
DDS_RGB = 0x40
DDS_CAPS2_CUBEMAP = 0x200
DDS_CAPS2_ALL_FACES = 0xFE00
CUBE_FACES = 6
# The cube map image is resampled to an equirectangular image of 4 x 2 face widths.
EQUIRECT_WIDTH_FACES, EQUIRECT_HEIGHT_FACES = 4, 2
# Sampler address mode of the equirectangular image: wrap U, clamp V (bit 1 of the GH3 address bits).
ADDRESS_CLAMP_V = 2

# Texture dictionary (.tex): magic, image count (u16 at 6), entry table offset (at 8); 40-byte entries.
TEX_MAGIC = 0xFACECAA7
TEX_COUNT, TEX_TABLE = 6, 8
TEX_ENTRY_SIZE = 40
TEX_ENTRY_NAME, TEX_ENTRY_DATA = 4, 28

# Index buffer primitive types and the strip restart indices.
PRIMITIVE_TRIANGLE_LIST = 4
PRIMITIVE_TRIANGLE_STRIP = 6
STRIP_RESTART = (0x7FFF, 0xFFFF)

# Scene file: material count (u16 at 34), material table size (at 36, from 32), first material (at 48).
SCENE_MATERIAL_COUNT, SCENE_MATERIAL_TABLE_SIZE = 34, 36
SCENE_TABLE_BASE = 32
SCENE_FIRST_MATERIAL = 48
# The geometry follows the material table: after a 0xBABEFACE block, or else at the next 128-byte boundary.
GEOMETRY_TAG = 0xBABEFACE
GEOMETRY_ALIGNMENT = 128
GEOMETRY_MESH_COUNT, GEOMETRY_SECTOR_COUNT = 76, 108
GEOMETRY_SECTOR_TABLE, GEOMETRY_GEOM_TABLE, GEOMETRY_MESH_TABLE = 112, 116, 128
# Sector and geom records (96 bytes): the sector name checksum (+4); the first mesh and the mesh count of the geom (+72).
SECTOR_SIZE = 96
SECTOR_NAME = 4
GEOM_FIRST_MESH, GEOM_MESH_COUNT = 72, 76
# Mesh headers (128 bytes).
MESH_HEADER_SIZE = 128
MESH_MATERIAL, MESH_FLAGS, MESH_COUNTS, MESH_PRIMITIVE = 20, 28, 36, 40
MESH_VERTICES, MESH_STRIDE, MESH_INDICES, MESH_WEIGHTS = 64, 90, 92, 96
# Vertex and index buffers start 32 bytes after their offsets.
MESH_BUFFER_HEADER = 32
# Mesh vertex flags.
VERTEX_COLOR = 0x10
VERTEX_WEIGHTED = 0x80
VERTEX_TANGENT_PAD = 0x100
VERTEX_ONE_TANGENT = 0x400
VERTEX_TWO_TANGENTS = 0x800
VERTEX_PIVOT = 0x1000
VERTEX_COLOR_PAD = 0x4000
VERTEX_UV_SETS_FIRST_BIT, VERTEX_UV_SETS_END_BIT = 16, 24
VERTEX_UV_SETS = 4
VERTEX_TEXCOORD4 = 0x03000000
VERTEX_TEXCOORD5 = 0x0C000000
VERTEX_LIGHTMAP = 0x0F000000
# Weighted vertices: a 32-byte block header with three group counts (+20) for 1, 2, and 3 influences; each group has
# a 16-byte header (vertex count, four bone indices) and 56-byte rows (a 4 x 3 matrix of position, normal, binormal,
# and tangent, then two weights).
WEIGHT_BLOCK_HEADER, WEIGHT_GROUP_COUNTS = 32, 20
WEIGHT_GROUP_HEADER = 16
WEIGHT_ROW = 56
NO_SKIN_WEIGHTS = (0, 0, 0, 0, 1, 0, 0, 0)

# Material records: name checksum (+0), template (+160), vertex and pixel constant banks (+168, +176: count and offset of
# 16-byte constants), samplers (+184: count and offset), record size (+196), render flags (+204), draw order (+212), blend
# mode (+216), two-sided (+220), alpha reference (+232), and the address bits of each sampler (+234).
MATERIAL_TEMPLATE = 160
MATERIAL_VERTEX_CONSTANTS, MATERIAL_PIXEL_CONSTANTS, MATERIAL_SAMPLERS = 168, 176, 184
MATERIAL_SIZE, MATERIAL_FLAGS, MATERIAL_DRAW_ORDER = 196, 204, 212
MATERIAL_BLEND, MATERIAL_DOUBLE_SIDED, MATERIAL_ALPHA_REF, MATERIAL_ADDRESSING = 216, 220, 232, 234
MATERIAL_MIN_SIZE = 256
CONSTANT_SIZE = 16
# GH3's material bind uploads at most 16 pixel constants.
MAX_PIXEL_CONSTANTS = 16

# Extended static vertex: the GH3 vertex streams that the original vertex shaders read, unchanged: UV sets 0–3,
# texcoord4/texcoord5 (the 0x03000000 and 0x0C000000 UV streams), binormal 0, position 1 (billboard pivot), and binormal 1.
# Positions and directions use the stage mesh convention (z negated); UVs keep D3D's top-down V.
GH3_STREAMS = struct.Struct("<12f3f3f3f")
# The static vertex: position, normal, UV, colour (8f4B), then lightmap, layer, and mask UVs (6f).
STATIC_BASE_STRIDE = 60
VERTEX_UV = 24
STATIC_STRIDE = STATIC_BASE_STRIDE + GH3_STREAMS.size
# Extended skinned vertex (GH3BND02): the 56-byte skinned vertex, then UV sets 0–3, texcoord4, texcoord5, binormal 0, and
# tangent 0. GH3 PC skins on the CPU; its weighted rows hold position, normal, binormal 0, and tangent 0 for each vertex.
SKIN_STREAMS = struct.Struct("<12f3f3f")
SKINNED_BASE_STRIDE = 56
SKINNED_STRIDE = SKINNED_BASE_STRIDE + SKIN_STREAMS.size
# Byte offset of binormal 0 in GH3_STREAMS (after the six UV pairs).
STREAMS_BINORMAL = 48


def cube_map(blob):
    """GH3 environment maps are DDS cube maps (DDSCAPS2_CUBEMAP)."""
    return (
        blob[:4] == DDS_MAGIC
        and len(blob) >= DDS_HEADER_SIZE
        and struct.unpack_from("<I", blob, DDS_CAPS2)[0] & DDS_CAPS2_CUBEMAP != 0
    )


def cube_to_equirect(blob):
    """Resample an uncompressed DDS cube map to the latitude-longitude image that gh3_shader's gh3_cube() reads."""
    height, width = struct.unpack_from("<2I", blob, DDS_HEIGHT)
    flags, _, bits, *masks = struct.unpack_from("<7I", blob, DDS_PIXEL_FORMAT_FLAGS)
    mips = max(1, struct.unpack_from("<I", blob, DDS_MIP_COUNT)[0])
    all_faces = struct.unpack_from("<I", blob, DDS_CAPS2)[0] & DDS_CAPS2_ALL_FACES == DDS_CAPS2_ALL_FACES
    if width != height or bits != 32 or not flags & DDS_RGB or not all_faces:
        raise ValueError("Unsupported GH3 cube map layout")
    pixels = np.frombuffer(blob, "<u4", offset=DDS_HEADER_SIZE)
    face_words = sum((width >> level) ** 2 for level in range(mips))

    def channel(mask, values):
        shift = (mask & -mask).bit_length() - 1
        return ((values & mask) >> shift).astype(np.uint8)

    faces = []
    for face in range(CUBE_FACES):
        level0 = pixels[face * face_words : face * face_words + width * width].reshape(width, width)
        faces.append(np.stack([channel(mask, level0) for mask in masks], axis=-1))
    out_height = EQUIRECT_HEIGHT_FACES * width
    out_width = EQUIRECT_WIDTH_FACES * width
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
    for index in range(CUBE_FACES):
        selected = face == index
        image[selected] = faces[index][row[selected], column[selected]]
    return Image.fromarray(image, "RGBA")


def mark_cube_samplers(materials, cubes):
    """The equirectangular image of a GH3 cube map wraps in U and clamps in V, whatever the cube sampler's address mode is."""
    for material in materials.values():
        gh3 = material.get("gh3", {})
        for slot, name in enumerate(gh3.get("textures", [])):
            if name in cubes:
                gh3["addressing"][slot] = ADDRESS_CLAMP_V


def _texture_entries(data):
    """(name checksum, image bytes) of each texture dictionary entry."""
    table = word(data, TEX_TABLE)
    for index in range(values(data, TEX_COUNT, "H")[0]):
        header = table + TEX_ENTRY_SIZE * index
        start, size = values(data, header + TEX_ENTRY_DATA, "II")
        yield word(data, header + TEX_ENTRY_NAME), data[start : start + size]


def cube_names(data):
    """Names of the cube maps in a GH3 texture dictionary."""
    return {f"{name:08x}" for name, blob in _texture_entries(data) if cube_map(blob)}


def textures(data):
    if word(data, 0) != TEX_MAGIC:
        raise ValueError("Expected a GH3 PC texture dictionary")
    for name, blob in _texture_entries(data):
        image = cube_to_equirect(blob) if cube_map(blob) else Image.open(BytesIO(blob)).convert("RGBA")
        yield f"{name:08x}", image


def triangles(indices, kind):
    if kind == PRIMITIVE_TRIANGLE_LIST:
        if len(indices) % 3:
            raise ValueError("Incomplete triangle")
        result = [tuple(indices[i : i + 3]) for i in range(0, len(indices), 3)]
    elif kind == PRIMITIVE_TRIANGLE_STRIP:
        result, strip = [], []
        for index in indices:
            if index in STRIP_RESTART:
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
    if material["template"] in SECOND_SAMPLER_DIFFUSE:
        samples = samples[1:2]
    return next((name for name in samples if name in available), "")


def crowd_uvs(vertices, count, faces, seed, stride):
    """Give each crowd card (connected vertices) one person of the crowd atlas, by its order and the mesh seed."""
    parents = list(range(count))

    def root(index):
        while parents[index] != index:
            parents[index] = parents[parents[index]]
            index = parents[index]
        return index

    for face in faces:
        for vertex in face[1:]:
            parents[root(vertex)] = root(face[0])
    if any(size > CROWD_CARD_VERTICES for size in Counter(root(v) for v in range(count)).values()):
        raise ValueError("Crowd meshes must use independent crowd cards")
    people = {}
    for vertex in range(count):
        person = people.setdefault(root(vertex), len(people))
        tile = (person + seed) % (CROWD_COLUMNS * CROWD_ROWS)
        u, v = struct.unpack_from("<2f", vertices, vertex * stride + VERTEX_UV)
        struct.pack_into(
            "<2f",
            vertices,
            vertex * stride + VERTEX_UV,
            (tile % CROWD_COLUMNS + u) / CROWD_COLUMNS,
            1 - (tile // CROWD_COLUMNS + 1 - v) / CROWD_ROWS,
        )


def material_record(data, offset, samples):
    """The GH3 material data that selects and drives its template: constant banks, samplers, and render state."""

    def bank(at):
        count, start = values(data, offset + at, "II")
        return [list(values(data, offset + start + i * CONSTANT_SIZE, "4f")) for i in range(count)]

    return {
        "template": f"{word(data, offset + MATERIAL_TEMPLATE):08x}",
        "vertexConstants": bank(MATERIAL_VERTEX_CONSTANTS),
        "pixelConstants": bank(MATERIAL_PIXEL_CONSTANTS),
        "textures": [f"{sample:08x}" for sample in samples],
        # Per-sampler address bits: bit 0 clamps U, bit 1 clamps V.
        "addressing": list(data[offset + MATERIAL_ADDRESSING : offset + MATERIAL_ADDRESSING + len(samples)]),
        # Render state, as GH3's material bind applies it.
        "flags": word(data, offset + MATERIAL_FLAGS),
        "blend": word(data, offset + MATERIAL_BLEND),
        "alphaRef": data[offset + MATERIAL_ALPHA_REF],
        "drawOrder": values(data, offset + MATERIAL_DRAW_ORDER, "f")[0],
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
            streams = at + STATIC_BASE_STRIDE + STREAMS_BINORMAL
            b = struct.unpack_from("<3f", vertices, streams)
            p = struct.unpack_from("<3f", vertices, streams + 12)
            b1 = struct.unpack_from("<3f", vertices, streams + 24)
            struct.pack_into("<9f", vertices, streams, *direction(*b), *point(*p), *direction(*b1))
    return bytes(vertices)


def _constants(data, offset, bank, limit=None):
    """The 16-byte constants of a material bank, as 4-tuples (at most `limit`)."""
    count, start = values(data, offset + bank, "II")
    if limit is not None:
        count = min(count, limit)
    return [values(data, offset + start + i * CONSTANT_SIZE, "4f") for i in range(count)]


def _first_components(data, offset, bank, count, message):
    """The x component of each constant of a bank with exactly `count` constants."""
    constant_count, constant_offset = values(data, offset + bank, "II")
    if constant_count != count:
        raise ValueError(message)
    return [values(data, offset + constant_offset + i * CONSTANT_SIZE, "f")[0] for i in range(count)]


def _template_fields(material, template, constants, sample_count):
    """Shading, colour, lightmap, UV scales, and layers of a material with a known GH3 template."""
    slot = gh3_materials.lightmap_slot(template, sample_count)
    material["shading"] = template["shading"]
    if template["diffuseConstant"] is not None and template["diffuseConstant"] < len(constants):
        material["color"] = [max(0.0, v) for v in constants[template["diffuseConstant"]]]
    if slot is not None:
        material["lightmap"] = material["textures"][slot]

    def uv_scale(sampler):
        entry = template["uvScales"].get(sampler)
        return constants[entry[0]][entry[1]] if entry and entry[0] < len(constants) else 1.0

    layers = LAYERED_TEMPLATES.get(template["name"], NOT_LAYERED)
    scales = [
        uv_scale(1 if material["template"] in SECOND_SAMPLER_DIFFUSE else 0),
        uv_scale(layers[1]) if layers[1] is not None else 1.0,
        uv_scale(layers[3]) if layers[3] is not None else 1.0,
    ]
    if scales != [1, 1, 1]:
        material["uvScale"] = scales
    if template["name"] not in LAYERED_TEMPLATES:
        return
    textures = material["textures"]
    diffuse_set, layer, layer_set, mask, mask_set, layer_constant = layers
    material["uvSets"] = (diffuse_set, layer_set, mask_set)
    material["mask"] = textures[mask]
    if layer is not None:
        if len(constants) <= layer_constant:
            raise ValueError("Layered material has no layer colour")
        base = material.get("color", [1, 1, 1, 1])
        # The diffuse constant is baked into vertex colours; the layer uses lighting x vertex colour x its own constant.
        material["layer"] = {
            "texture": textures[layer],
            "color": [c / max(b, MIN_LAYER_BASE) for c, b in zip(constants[layer_constant], base)],
        }


def _material(data, offset):
    """(name, material) of the material record at `offset`."""
    name = f"{word(data, offset):08x}"
    sample_count, sample_offset = values(data, offset + MATERIAL_SAMPLERS, "II")
    samples = values(data, offset + sample_offset, f"{sample_count}I")
    material = {
        "textures": [f"{sample:08x}" for sample in samples],
        "template": f"{word(data, offset + MATERIAL_TEMPLATE):08x}",
        "blend": word(data, offset + MATERIAL_BLEND),
        "cutoff": data[offset + MATERIAL_ALPHA_REF] / 255,
        "doubleSided": word(data, offset + MATERIAL_DOUBLE_SIDED) != 0,
        "gh3": material_record(data, offset, samples),
    }
    template = gh3_materials.templates().get(material["template"])
    if template is not None:
        constants = _constants(data, offset, MATERIAL_PIXEL_CONSTANTS, MAX_PIXEL_CONSTANTS)
        _template_fields(material, template, constants, sample_count)
    if material["template"] in ANIMATED_TEXTURE_TEMPLATES:
        columns, rows, fps, frame_offset = _first_components(
            data,
            offset,
            MATERIAL_VERTEX_CONSTANTS,
            ANIMATED_TEXTURE_CONSTANTS,
            "Animated texture has an invalid vertex constant bank",
        )
        material["textureAnimation"] = {
            "columns": int(columns),
            "rows": int(rows),
            "fps": fps,
            "offset": frame_offset,
        }
    if material["template"] == WIBBLE_SIMPLE:
        motion = _first_components(
            data,
            offset,
            MATERIAL_VERTEX_CONSTANTS,
            WIBBLE_SIMPLE_CONSTANTS,
            "Wibble_Simple has an invalid vertex constant bank",
        )
        # The pixel shader scales the UV after the vertex shader's turn and scroll, so the pivot and scroll scale with it.
        scale = material.get("uvScale", [1, 1, 1])[0]
        material["uvWibble"] = [v * scale if i in WIBBLE_UV_CONSTANTS else v for i, v in enumerate(motion)]
    return name, material


def _materials(data):
    """The material table of a scene: ({name: material}, offset of the table end)."""
    count = values(data, SCENE_MATERIAL_COUNT, "H")[0]
    end = SCENE_TABLE_BASE + word(data, SCENE_MATERIAL_TABLE_SIZE)
    offset = SCENE_FIRST_MATERIAL
    materials = {}
    for _ in range(count):
        name, material = _material(data, offset)
        materials[name] = material
        size = word(data, offset + MATERIAL_SIZE)
        if size < MATERIAL_MIN_SIZE:
            raise ValueError("Invalid material size")
        offset += size
    if offset != end:
        raise ValueError("Invalid material table size")
    return materials, end


def _geometry_base(data, end):
    if word(data, end) == GEOMETRY_TAG:
        return end + 8 + word(data, end + 4)
    return (end + GEOMETRY_ALIGNMENT - 1) // GEOMETRY_ALIGNMENT * GEOMETRY_ALIGNMENT


def _sectors(data, base):
    """(sector name checksum, mesh indices) of each sector of the geometry block."""
    for index in range(word(data, base + GEOMETRY_SECTOR_COUNT)):
        sector = base + word(data, base + GEOMETRY_SECTOR_TABLE) + index * SECTOR_SIZE
        geom = base + word(data, base + GEOMETRY_GEOM_TABLE) + index * SECTOR_SIZE
        first = word(data, geom + GEOM_FIRST_MESH)
        yield word(data, sector + SECTOR_NAME), range(first, first + word(data, geom + GEOM_MESH_COUNT))


def _weighted_vertices(data, base, header, vertex_count):
    """The skin weights (packed 4B4f) and (position, normal, binormal, tangent) rows of a weighted mesh."""
    weights = []
    rows = []
    cursor = base + word(data, header + MESH_WEIGHTS)
    groups = values(data, cursor + WEIGHT_GROUP_COUNTS, "3I")
    cursor += WEIGHT_BLOCK_HEADER
    for influences, group_count in enumerate(groups, 1):
        for _ in range(group_count):
            count = word(data, cursor)
            bones = values(data, cursor + 4, "4B")
            cursor += WEIGHT_GROUP_HEADER
            for _ in range(count):
                row = values(data, cursor, "14f")
                rows.append(
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
                cursor += WEIGHT_ROW
    if len(weights) != vertex_count:
        raise ValueError("Skin weight count does not match vertices")
    return weights, rows


def _uv_set_flags(uv_set):
    return 3 << (VERTEX_UV_SETS_FIRST_BIT + 2 * uv_set)


def _mesh_vertices(data, start_at, stride, vertex_count, flags, node, material, skinned, weighted):
    """The stage vertex bytes of a mesh: static (STATIC_STRIDE) or skinned (SKINNED_STRIDE) layout."""
    weights, weighted_rows = weighted or ([], [])
    tangent_count = 2 if flags & VERTEX_TWO_TANGENTS else 1 if flags & VERTEX_ONE_TANGENT else 0
    uv_count = sum(bool(flags & (1 << bit)) for bit in range(VERTEX_UV_SETS_FIRST_BIT, VERTEX_UV_SETS_END_BIT))
    vertex_data = bytearray()
    for vertex in range(vertex_count):
        start = start_at + vertex * stride
        tangent = (0, 0, 0)
        if weighted:
            position, normal, binormal, tangent = weighted_rows[vertex]
            pivot, binormal1 = position, (0, 0, 0)
            cursor = start
        else:
            position = values(data, start, "3f")
            pivot = values(data, start + 12, "3f") if flags & VERTEX_PIVOT else position
            cursor = start + 12 + (12 if flags & VERTEX_PIVOT else 0)
            normal = values(data, cursor, "3f")
            binormal = values(data, cursor + 12, "3f") if tangent_count else (0, 0, 0)
            binormal1 = values(data, cursor + 24, "3f") if tangent_count > 1 else (0, 0, 0)
            cursor += 12 + tangent_count * 12 + (4 if flags & VERTEX_TANGENT_PAD else 0)
        if node is not None and node.get(key("Class")) == key("LevelObject"):
            angles = node.get(key("Angles"), (0, 0, 0))
            position = tuple(a + b for a, b in zip(rotate(position, angles), node[key("Pos")]))
            normal = rotate(normal, angles)
            if not weighted:
                pivot = tuple(a + b for a, b in zip(rotate(pivot, angles), node[key("Pos")]))
                binormal = rotate(binormal, angles)
                binormal1 = rotate(binormal1, angles)
        color = (255, 255, 255, 255)
        if flags & VERTEX_COLOR:
            a, r, g, b = values(data, cursor, "4B")
            color = (r, g, b, a)
            cursor += 4
        cursor += 4 if flags & VERTEX_COLOR_PAD and not weighted else 0
        sets = {}
        for uv_set in range(VERTEX_UV_SETS):
            if flags & _uv_set_flags(uv_set):
                sets[uv_set] = values(data, cursor + len(sets) * 8, "2f")
        if len(sets) != uv_count:
            raise ValueError(f"Unsupported UV set flags {flags:08x}")
        uv_sets = material.get("uvSets")
        uv_scale = material.get("uvScale", (1, 1, 1))
        uv = sets.get(uv_sets[0], (0, 0)) if uv_sets else next(iter(sets.values()), (0, 0))
        layer_uv = sets.get(uv_sets[1], (0, 0)) if uv_sets and uv_sets[1] is not None else (0, 0)
        mask_uv = sets.get(uv_sets[2], (0, 0)) if uv_sets else (0, 0)
        uv, layer_uv, mask_uv = ((u * scale, v * scale) for (u, v), scale in zip((uv, layer_uv, mask_uv), uv_scale))
        extra = cursor + uv_count * 8
        lightmap = values(data, extra, "2f") if flags & VERTEX_LIGHTMAP else (0, 0)
        expected = extra + (8 if flags & VERTEX_TEXCOORD5 else 0) + (8 if flags & VERTEX_TEXCOORD4 else 0)
        streams = [sets.get(n, (0, 0)) for n in range(VERTEX_UV_SETS)] + [(0, 0), (0, 0)]
        if flags & VERTEX_TEXCOORD5:
            streams[5] = lightmap
        if flags & VERTEX_TEXCOORD4:
            streams[4] = values(data, extra + (8 if flags & VERTEX_TEXCOORD5 else 0), "2f")
        if expected != start + stride:
            raise ValueError(f"Unsupported vertex layout {flags:08x}: {expected - start} != {stride}")
        floats = (*position[:2], -position[2], *normal[:2], -normal[2], uv[0], 1 - uv[1])
        if not all(math.isfinite(value) for value in floats + lightmap):
            raise ValueError("Non-finite vertex")
        # One Backyard Bash layer material (ca426c7d) stores NaN in its second UV set; sample its layer at the origin.
        layer_uv = layer_uv if all(map(math.isfinite, layer_uv)) else (0, 0)
        mask_uv = mask_uv if all(map(math.isfinite, mask_uv)) else (0, 0)
        vertex_data.extend(struct.pack("<8f4B", *floats, *color))
        uv_streams = tuple(c for uv_pair in streams for c in uv_pair)
        if not skinned:
            vertex_data.extend(
                struct.pack(
                    "<6f", lightmap[0], 1 - lightmap[1], layer_uv[0], 1 - layer_uv[1], mask_uv[0], 1 - mask_uv[1]
                )
            )
            raw = (*uv_streams, *_flip_z(binormal), *_flip_z(pivot), *_flip_z(binormal1))
            vertex_data.extend(GH3_STREAMS.pack(*(v if math.isfinite(v) else 0.0 for v in raw)))
        if skinned:
            vertex_data.extend(weights[vertex] if weighted else struct.pack("<4B4f", *NO_SKIN_WEIGHTS))
            raw = (*uv_streams, *_flip_z(binormal), *_flip_z(tangent))
            vertex_data.extend(SKIN_STREAMS.pack(*(v if math.isfinite(v) else 0.0 for v in raw)))
    return vertex_data


def _flip_z(vector):
    return vector[0], vector[1], -vector[2]


def scene(data, nodes=None, skinned=False, light_groups=None, mesh_nodes=None, crowd_cards=True, include_nodes=()):
    """(materials, meshes) of a GH3 scene; each mesh is (material name, vertex count, vertex bytes, triangles).

    nodes: the node array of the zone; meshes of nodes that are not created at start are left out unless their name is
    in include_nodes. light_groups and mesh_nodes, when given, get the light group and the node of each mesh.
    """
    materials, end = _materials(data)
    base = _geometry_base(data, end)
    mesh_count = word(data, base + GEOMETRY_MESH_COUNT)
    mesh_table = base + word(data, base + GEOMETRY_MESH_TABLE)
    placements = {}
    if nodes is not None:
        nodes = {node[key("Name")]: node for node in nodes if key("Name") in node}
        for sector, meshes_of_sector in _sectors(data, base):
            node = nodes.get(sector)
            for mesh in meshes_of_sector:
                placements[mesh] = node
    meshes = []
    unlightmapped = set()
    for index in range(mesh_count):
        node = placements.get(index)
        if node is not None and key("CreatedAtStart") not in node[0] and node.get(key("Name")) not in include_nodes:
            continue
        header = mesh_table + MESH_HEADER_SIZE * index
        material = f"{word(data, header + MESH_MATERIAL):08x}"
        flags = word(data, header + MESH_FLAGS)
        index_count, vertex_count = values(data, header + MESH_COUNTS, "HH")
        kind = word(data, header + MESH_PRIMITIVE)
        vertices_at = base + word(data, header + MESH_VERTICES) + MESH_BUFFER_HEADER
        indices_at = base + word(data, header + MESH_INDICES) + MESH_BUFFER_HEADER
        stride = data[header + MESH_STRIDE]
        weighted = bool(flags & VERTEX_WEIGHTED)
        if weighted and not skinned:
            raise ValueError("Skinned meshes need a skeleton; expected static venue geometry")
        weights = _weighted_vertices(data, base, header, vertex_count) if weighted else None
        vertex_data = (
            _mesh_vertices(data, vertices_at, stride, vertex_count, flags, node, materials[material], skinned, weights)
            if vertex_count
            else bytearray()
        )
        faces = triangles(values(data, indices_at, f"{index_count}H"), kind)
        if any(max(face) >= vertex_count for face in faces):
            raise ValueError("Triangle index outside vertex buffer")
        if crowd_cards and materials[material]["template"] == CROWD_TEMPLATE:
            crowd_uvs(vertex_data, vertex_count, faces, index, SKINNED_STRIDE if skinned else STATIC_STRIDE)
        if material not in materials:
            raise ValueError("Mesh has no material")
        if skinned or not flags & VERTEX_LIGHTMAP:
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
    end = SCENE_TABLE_BASE + word(data, SCENE_MATERIAL_TABLE_SIZE)
    base = _geometry_base(data, end)
    return {mesh: sector for sector, meshes in _sectors(data, base) for mesh in meshes}
