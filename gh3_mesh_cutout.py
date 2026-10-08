"""Trim opaque prop meshes to the original texture alpha mask."""

from itertools import groupby
import math
from pathlib import Path
import struct

from PIL import Image


CUTOUT_TEMPLATES = {"4d7335f8", "f4e6db51"}


def alpha_region(image, cutoff):
    from shapely import box, union_all

    width, height = image.size
    alpha = image.getchannel("A").tobytes()
    rectangles = []
    for y in range(height):
        x = 0
        for visible, run in groupby(alpha[y * width:(y + 1) * width], lambda a: a / 255 >= cutoff):
            length = sum(1 for _ in run)
            if not visible:
                rectangles.append(box(x, y, x + length, y + 1))
            x += length
    return union_all(rectangles).simplify(0.35, preserve_topology=True)


def trim_mesh(mesh, image, cutoff, region=None):
    from shapely import Point, Polygon, constrained_delaunay_triangles, union_all
    from shapely.affinity import translate

    material, count, vertices, faces = mesh
    stride = len(vertices) // count
    if stride not in (36, 44, 56, 60) or len(vertices) != count * stride:
        raise ValueError("Invalid cutout mesh vertex layout")
    width, height = image.size
    region = alpha_region(image, cutoff) if region is None else region
    if region.is_empty:
        return mesh
    packed = [bytes(vertices[i * stride:(i + 1) * stride]) for i in range(count)]
    indices = {vertex: i for i, vertex in enumerate(packed)}
    result = []
    tiles = {}
    for face in faces:
        samples = [struct.unpack_from("<8f4B", packed[i]) for i in face]
        uv = [(v[6] * width, (1 - v[7]) * height) for v in samples]
        polygon = Polygon(uv)
        if polygon.area < 1e-10:
            if not region.covers(Point(uv[0][0] % width, uv[0][1] % height)):
                result.append(face)
            continue
        bounds = polygon.bounds
        tile = (math.floor(bounds[0] / width), math.floor(bounds[1] / height),
                math.floor(bounds[2] / width), math.floor(bounds[3] / height))
        if tile not in tiles:
            if (tile[2] - tile[0] + 1) * (tile[3] - tile[1] + 1) > 64:
                raise ValueError("Cutout UV range is too large")
            tiles[tile] = union_all([translate(region, x * width, y * height)
                                     for x in range(tile[0], tile[2] + 1) for y in range(tile[1], tile[3] + 1)])
        hidden = tiles[tile]
        if not polygon.intersects(hidden):
            result.append(face)
            continue
        visible = polygon.difference(hidden)
        if visible.equals(polygon):
            result.append(face)
            continue
        ax, ay = uv[0]
        bx, by = uv[1][0] - ax, uv[1][1] - ay
        cx, cy = uv[2][0] - ax, uv[2][1] - ay
        determinant = bx * cy - by * cx

        def vertex(x, y):
            b = ((x - ax) * cy - (y - ay) * cx) / determinant
            c = (bx * (y - ay) - by * (x - ax)) / determinant
            weights = (1 - b - c, b, c)
            values = [sum(w * row[i] for w, row in zip(weights, samples)) for i in range(12)]
            length = math.sqrt(sum(v * v for v in values[3:6]))
            if length > 1e-8:
                values[3:6] = [v / length for v in values[3:6]]
            data = struct.pack("<8f4B", *values[:8], *(round(max(0, min(255, v))) for v in values[8:]))
            if stride in (44, 60):
                channels = (stride - 36) // 4
                extra = [struct.unpack_from(f"<{channels}f", packed[i], 36) for i in face]
                data += struct.pack(f"<{channels}f", *(sum(w * row[k] for w, row in zip(weights, extra)) for k in range(channels)))
            if stride == 56:
                influences = {}
                for w, i in zip(weights, face):
                    bone_indices = struct.unpack_from("<4B", packed[i], 36)
                    bone_weights = struct.unpack_from("<4f", packed[i], 40)
                    for bone, amount in zip(bone_indices, bone_weights):
                        influences[bone] = influences.get(bone, 0) + max(0, w) * amount
                influences = sorted(((bone, weight) for bone, weight in influences.items() if weight > 1e-7), key=lambda p: -p[1])
                if len(influences) > 4 or not influences:
                    raise ValueError("Cutout interpolation exceeds the skin weight limit")
                total = sum(w for _, w in influences)
                data += struct.pack("<4B4f", *(i for i, _ in influences), *([0] * (4 - len(influences))),
                                    *(w / total for _, w in influences), *([0] * (4 - len(influences))))
            if data not in indices:
                indices[data] = len(packed)
                packed.append(data)
            return indices[data]

        for triangle in constrained_delaunay_triangles(visible).geoms:
            points = list(triangle.exterior.coords)[:3]
            a, b, c = points
            if ((b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])) * determinant < 0:
                points.reverse()
            result.append(tuple(vertex(x, y) for x, y in points))
    if len(packed) > 65535:
        raise ValueError("Cutout mesh exceeds the vertex limit")
    return material, len(packed), b"".join(packed), result


def trim_prop_cutouts(meshes, materials, texture_directory):
    masks = {}
    output = []
    for mesh in meshes:
        material = materials[mesh[0]]
        texture, cutoff = material["texture"], material["cutoff"]
        if material["template"] in CUTOUT_TEMPLATES and material["blend"] == 0 and cutoff > 0 and texture:
            key = texture, cutoff
            if key not in masks:
                with Image.open(Path(texture_directory) / f"{texture}.png") as source:
                    image = source.convert("RGBA")
                masks[key] = image, alpha_region(image, cutoff)
            image, region = masks[key]
            mesh = trim_mesh(mesh, image, cutoff, region)
            material["geometryCutout"] = True
        output.append(mesh)
    return output
