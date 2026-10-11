"""Trim opaque prop meshes to the original texture alpha mask."""

import math
import struct
from itertools import groupby
from pathlib import Path

from PIL import Image

CUTOUT_TEMPLATES = {"4d7335f8", "f4e6db51"}
# Stage vertex layouts: the 36-byte base vertex (position, normal, UV, colour), static layouts with float channels after
# it (44, 60, 144), and skinned layouts with 4 bone indices and weights after it (56) and the GH3 streams (128).
BASE_VERTEX = struct.Struct("<8f4B")
VERTEX_STRIDES = (36, 44, 56, 60, 128, 144)
FLOAT_CHANNEL_STRIDES = (44, 60, 144)
SKINNED_STRIDES = (56, 128)
SKINNED_STREAMS_STRIDE = 128
SKIN_WEIGHTS_AT, SKIN_STREAMS_AT, SKIN_STREAM_FLOATS = 40, 56, 18
MAX_INFLUENCES = 4
MIN_INFLUENCE = 1e-7
MIN_NORMAL_LENGTH = 1e-8
MIN_UV_AREA = 1e-10
# Largest UV area of one triangle, in texture repeats.
MAX_UV_TILES = 64
MAX_VERTICES = 65535
# Outline simplification of the alpha mask, in texels (tuned: small enough to keep the cut-out shape).
MASK_TOLERANCE = 0.35


def alpha_region(image, cutoff):
    from shapely import box, union_all

    width, height = image.size
    alpha = image.getchannel("A").tobytes()
    rectangles = []
    for y in range(height):
        x = 0
        for visible, run in groupby(alpha[y * width : (y + 1) * width], lambda a: a / 255 >= cutoff):
            length = sum(1 for _ in run)
            if not visible:
                rectangles.append(box(x, y, x + length, y + 1))
            x += length
    return union_all(rectangles).simplify(MASK_TOLERANCE, preserve_topology=True)


def trim_mesh(mesh, image, cutoff, region=None):
    from shapely import Point, Polygon, constrained_delaunay_triangles, union_all
    from shapely.affinity import translate

    material, count, vertices, faces = mesh
    stride = len(vertices) // count
    if stride not in VERTEX_STRIDES or len(vertices) != count * stride:
        raise ValueError("Invalid cutout mesh vertex layout")
    width, height = image.size
    region = alpha_region(image, cutoff) if region is None else region
    if region.is_empty:
        return mesh
    packed = [bytes(vertices[i * stride : (i + 1) * stride]) for i in range(count)]
    indices = {vertex: i for i, vertex in enumerate(packed)}
    result = []
    tiles = {}
    for face in faces:
        samples = [BASE_VERTEX.unpack_from(packed[i]) for i in face]
        uv = [(v[6] * width, (1 - v[7]) * height) for v in samples]
        polygon = Polygon(uv)
        if polygon.area < MIN_UV_AREA:
            if not region.covers(Point(uv[0][0] % width, uv[0][1] % height)):
                result.append(face)
            continue
        bounds = polygon.bounds
        tile = (
            math.floor(bounds[0] / width),
            math.floor(bounds[1] / height),
            math.floor(bounds[2] / width),
            math.floor(bounds[3] / height),
        )
        if tile not in tiles:
            if (tile[2] - tile[0] + 1) * (tile[3] - tile[1] + 1) > MAX_UV_TILES:
                raise ValueError("Cutout UV range is too large")
            tiles[tile] = union_all(
                [
                    translate(region, x * width, y * height)
                    for x in range(tile[0], tile[2] + 1)
                    for y in range(tile[1], tile[3] + 1)
                ]
            )
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
            data = _interpolate(packed, face, samples, (1 - b - c, b, c), stride)
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
    if len(packed) > MAX_VERTICES:
        raise ValueError("Cutout mesh exceeds the vertex limit")
    return material, len(packed), b"".join(packed), result


def _interpolate(packed, face, samples, weights, stride):
    """The vertex bytes at barycentric `weights` of a triangle: values blended, normal renormalized, skin weights merged."""
    values = [sum(w * row[i] for w, row in zip(weights, samples)) for i in range(12)]
    length = math.sqrt(sum(v * v for v in values[3:6]))
    if length > MIN_NORMAL_LENGTH:
        values[3:6] = [v / length for v in values[3:6]]
    data = BASE_VERTEX.pack(*values[:8], *(round(max(0, min(255, v))) for v in values[8:]))
    if stride in FLOAT_CHANNEL_STRIDES:
        channels = (stride - BASE_VERTEX.size) // 4
        extra = [struct.unpack_from(f"<{channels}f", packed[i], BASE_VERTEX.size) for i in face]
        data += struct.pack(
            f"<{channels}f", *(sum(w * row[k] for w, row in zip(weights, extra)) for k in range(channels))
        )
    if stride in SKINNED_STRIDES:
        influences = {}
        for w, i in zip(weights, face):
            bone_indices = struct.unpack_from("<4B", packed[i], BASE_VERTEX.size)
            bone_weights = struct.unpack_from("<4f", packed[i], SKIN_WEIGHTS_AT)
            for bone, amount in zip(bone_indices, bone_weights):
                influences[bone] = influences.get(bone, 0) + max(0, w) * amount
        influences = sorted(
            ((bone, weight) for bone, weight in influences.items() if weight > MIN_INFLUENCE), key=lambda p: -p[1]
        )
        if len(influences) > MAX_INFLUENCES or not influences:
            raise ValueError("Cutout interpolation exceeds the skin weight limit")
        total = sum(w for _, w in influences)
        data += struct.pack(
            "<4B4f",
            *(i for i, _ in influences),
            *([0] * (MAX_INFLUENCES - len(influences))),
            *(w / total for _, w in influences),
            *([0] * (MAX_INFLUENCES - len(influences))),
        )
    if stride == SKINNED_STREAMS_STRIDE:
        extra = [struct.unpack_from(f"<{SKIN_STREAM_FLOATS}f", packed[i], SKIN_STREAMS_AT) for i in face]
        data += struct.pack(
            f"<{SKIN_STREAM_FLOATS}f",
            *(sum(w * row[k] for w, row in zip(weights, extra)) for k in range(SKIN_STREAM_FLOATS)),
        )
    return data


def trim_prop_cutouts(meshes, materials, texture_directory):
    """Cut the alpha-tested prop meshes to their texture mask, so the props draw without alpha test."""
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
