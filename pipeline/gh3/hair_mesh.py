"""Combine hair cards so their triangles can draw in camera-depth order."""

import math
import struct
from pathlib import Path

from PIL import Image

from pipeline.gh3.formats import key


def combine_hair(meshes, materials, texture_directory):
    selected = [m for m in meshes if materials[m[0]]["template"] == "6f3f03f3" and materials[m[0]]["blend"] == 3]
    if not selected:
        return meshes
    stride = len(selected[0][2]) // selected[0][1]
    if stride not in (56, 128):
        raise ValueError("Invalid hair vertex layout")
    names = sorted({materials[m[0]]["texture"] for m in selected})
    transforms = {}
    directory = Path(texture_directory)
    material_id = selected[0][0]
    material = materials[material_id]
    if len(names) > 1:
        tiles = []
        for name in names:
            uv = [struct.unpack_from("<2f", data, i * stride + 24)
                  for key, count, data, _ in selected if materials[key]["texture"] == name for i in range(count)]
            low = [math.floor(min(v[k] for v in uv)) for k in range(2)]
            high = [math.floor(max(v[k] for v in uv)) + 1 for k in range(2)]
            with Image.open(directory / f"{name}.png") as source:
                image = source.convert("RGBA")
            width, height = image.size
            if (high[0] - low[0]) * (high[1] - low[1]) > 16:
                raise ValueError("Hair atlas UV range is too large")
            pad = 16
            tile = Image.new("RGBA", ((high[0] - low[0]) * width + pad * 2, (high[1] - low[1]) * height + pad * 2))
            for y in range(-1, high[1] - low[1] + 1):
                for x in range(-1, high[0] - low[0] + 1):
                    tile.paste(image, (pad + x * width, pad + y * height))
            tiles.append((name, tile, low, high, width, height, pad))
        atlas_width = 1 << (max(t[1].width for t in tiles) - 1).bit_length()
        atlas_height = 1 << (sum(t[1].height for t in tiles) - 1).bit_length()
        atlas = Image.new("RGBA", (atlas_width, atlas_height))
        top = 0
        for name, tile, low, high, width, height, pad in tiles:
            atlas.paste(tile, (0, top))
            transforms[name] = (width / atlas_width, height / atlas_height,
                                (pad - low[0] * width) / atlas_width,
                                1 - (top + pad + high[1] * height) / atlas_height)
            top += tile.height
        texture = f'{key("gh3_sorted_hair_" + "_".join(names)):08x}'
        if texture in names:
            raise ValueError("Hair atlas texture ID collision")
        atlas.save(directory / f"{texture}.png")
    else:
        texture = names[0]
    vertices, faces, count = bytearray(), [], 0
    for part_material, size, data, triangles in selected:
        if len(data) != size * stride:
            raise ValueError("Invalid hair vertex layout")
        packed = bytearray(data)
        name = materials[part_material]["texture"]
        if name in transforms:
            sx, sy, ox, oy = transforms[name]
            for i in range(size):
                u, v = struct.unpack_from("<2f", packed, i * stride + 24)
                struct.pack_into("<2f", packed, i * stride + 24, u * sx + ox, v * sy + oy)
                if stride == 128:
                    # HairShader samples its diffuse (s1) with UV set 1, stored with D3D's top-down V.
                    u, v = struct.unpack_from("<2f", packed, i * stride + 64)
                    struct.pack_into("<2f", packed, i * stride + 64, u * sx + ox, 1 - ((1 - v) * sy + oy))
        vertices.extend(packed)
        faces.extend(tuple(v + count for v in face) for face in triangles)
        count += size
    if count > 65535:
        raise ValueError("Combined hair exceeds the vertex limit")
    material["texture"] = texture
    if "gh3" in material and len(material["gh3"]["textures"]) > 1:
        material["gh3"]["textures"][1] = texture
    material["sortedTransparency"] = True
    selected_ids = {m[0] for m in selected}
    return [m for m in meshes if m[0] not in selected_ids] + [(material_id, count, bytes(vertices), faces)]
