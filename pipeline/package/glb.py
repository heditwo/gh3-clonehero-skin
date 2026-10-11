"""Minimal GLB reading for package validation (no image decoding)."""

import json
import math
import struct

import numpy as np

GLB_MAGIC = 0x46546C67
JSON_CHUNK = 0x4E4F534A
BIN_CHUNK = 0x004E4942
COMPONENTS = {5120: "i1", 5121: "u1", 5122: "<i2", 5123: "<u2", 5125: "<u4", 5126: "<f4"}
COLUMNS = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT2": 4, "MAT3": 9, "MAT4": 16}


class GlbError(ValueError):
    pass


def split_glb(data, error=GlbError):
    """(glTF JSON, binary chunk) of GLB bytes; raises `error` when the data is not a glTF 2.0 GLB."""
    magic, version, length = struct.unpack_from("<3I", data)
    if magic != GLB_MAGIC or version != 2 or length != len(data):
        raise error("not a glTF 2.0 GLB")
    size, kind = struct.unpack_from("<2I", data, 12)
    if kind != JSON_CHUNK:
        raise error("the first GLB chunk is not JSON")
    gltf = json.loads(bytes(data[20 : 20 + size]).decode("utf-8"))
    binary, cursor = b"", 20 + size
    if cursor < len(data):
        size, kind = struct.unpack_from("<2I", data, cursor)
        if kind != BIN_CHUNK:
            raise error("the second GLB chunk is not binary")
        binary = bytes(data[cursor + 8 : cursor + 8 + size])
    return gltf, binary


def join_glb(gltf, binary, allow_nan=True):
    """GLB bytes. The JSON chunk is padded with spaces and the binary chunk with zeros (glTF 2.0, section 4.4)."""
    text = json.dumps(gltf, ensure_ascii=False, separators=(",", ":"), allow_nan=allow_nan).encode("utf-8")
    text += b" " * (-len(text) % 4)
    chunks = struct.pack("<2I", len(text), JSON_CHUNK) + text
    if binary:
        binary = bytes(binary) + b"\0" * (-len(binary) % 4)
        chunks += struct.pack("<2I", len(binary), BIN_CHUNK) + binary
    return struct.pack("<3I", GLB_MAGIC, 2, 12 + len(chunks)) + chunks


class Glb:
    def __init__(self, data):
        if len(data) < 20:
            raise GlbError("File is too short for a GLB")
        magic, version, length = struct.unpack_from("<3I", data)
        if magic != GLB_MAGIC or version != 2:
            raise GlbError("Not a glTF 2.0 GLB")
        if length != len(data):
            raise GlbError("GLB length field differs from the file size")
        chunks = []
        cursor = 12
        while cursor < len(data):
            if cursor + 8 > len(data):
                raise GlbError("Truncated GLB chunk header")
            size, kind = struct.unpack_from("<2I", data, cursor)
            cursor += 8
            if cursor + size > len(data):
                raise GlbError("Truncated GLB chunk")
            chunks.append((kind, data[cursor : cursor + size]))
            cursor += size
        if not chunks or chunks[0][0] != JSON_CHUNK:
            raise GlbError("The first GLB chunk must be JSON")
        if len(chunks) > 2 or len(chunks) == 2 and chunks[1][0] != BIN_CHUNK:
            raise GlbError("A GLB has one JSON chunk and at most one binary chunk")

        def reject(value):
            raise GlbError(f"Non-finite JSON value in GLB: {value}")

        try:
            self.json = json.loads(bytes(chunks[0][1]).decode("utf-8"), parse_constant=reject)
        except (UnicodeDecodeError, json.JSONDecodeError) as error:
            raise GlbError(f"Invalid GLB JSON: {error}") from None
        if not isinstance(self.json, dict):
            raise GlbError("GLB JSON must be an object")
        self.binary = chunks[1][1] if len(chunks) == 2 else b""

    def list(self, name):
        value = self.json.get(name, [])
        if not isinstance(value, list):
            raise GlbError(f"glTF '{name}' must be an array")
        return value

    def view(self, index):
        views = self.list("bufferViews")
        if type(index) is not int or not 0 <= index < len(views):
            raise GlbError("Invalid buffer view index")
        view = views[index]
        start = view.get("byteOffset", 0)
        end = start + view.get("byteLength", -1)
        if view.get("buffer", 0) != 0 or start < 0 or end < start or end > len(self.binary):
            raise GlbError("Buffer view is outside the GLB binary chunk")
        return self.binary[start:end]

    def accessor(self, index):
        accessors = self.list("accessors")
        if type(index) is not int or not 0 <= index < len(accessors):
            raise GlbError("Invalid accessor index")
        accessor = accessors[index]
        if "sparse" in accessor:
            raise GlbError("Sparse accessors are not allowed")
        if accessor.get("componentType") not in COMPONENTS or accessor.get("type") not in COLUMNS:
            raise GlbError("Invalid accessor type")
        dtype = np.dtype(COMPONENTS[accessor["componentType"]])
        columns = COLUMNS[accessor["type"]]
        count = accessor.get("count", 0)
        if "bufferView" not in accessor:
            # glTF: an accessor without a buffer view holds zeros (Blender writes these for unchanged morph targets).
            if type(count) is not int or not 1 <= count <= 10_000_000:
                raise GlbError("Invalid accessor count")
            return np.zeros((count, columns), dtype=dtype)
        data = self.view(accessor["bufferView"])
        stride = self.list("bufferViews")[accessor["bufferView"]].get("byteStride", columns * dtype.itemsize)
        offset = accessor.get("byteOffset", 0)
        if (
            type(count) is not int
            or not 1 <= count <= 10_000_000
            or stride < columns * dtype.itemsize
            or offset < 0
            or offset + (count - 1) * stride + columns * dtype.itemsize > len(data)
        ):
            raise GlbError("Accessor range is outside its buffer view")
        values = np.ndarray(
            (count, columns), dtype=dtype, buffer=data, offset=offset, strides=(stride, dtype.itemsize)
        ).copy()
        if accessor.get("normalized") and dtype.kind in "iu":
            values = np.maximum(-1.0, values.astype(np.float64) / np.iinfo(dtype).max)
        if values.dtype.kind == "f" and not np.isfinite(values).all():
            raise GlbError("Accessor has non-finite values")
        return values

    def node_matrix(self, node):
        if "matrix" in node:
            matrix = np.asarray(node["matrix"], dtype=np.float64)
            if matrix.shape != (16,):
                raise GlbError("Invalid node matrix")
            return matrix.reshape(4, 4).T
        x, y, z, w = node.get("rotation", [0, 0, 0, 1])
        sx, sy, sz = node.get("scale", [1, 1, 1])
        rotation = np.array(
            [
                [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
            ]
        )
        matrix = np.eye(4)
        matrix[:3, :3] = rotation @ np.diag([sx, sy, sz])
        matrix[:3, 3] = node.get("translation", [0, 0, 0])
        return matrix

    def parents(self):
        parents = {}
        for index, node in enumerate(self.list("nodes")):
            for child in node.get("children", []):
                if child in parents:
                    raise GlbError("A glTF node has two parents")
                parents[child] = index
        return parents

    def world(self, index, parents, cache):
        if index not in cache:
            chain, current = [], index
            while current is not None:
                if len(chain) > 256 or current in chain:
                    raise GlbError("Invalid glTF node hierarchy")
                chain.append(current)
                current = parents.get(current)
            matrix = np.eye(4)
            for node in reversed(chain):
                matrix = matrix @ self.node_matrix(self.list("nodes")[node])
            cache[index] = matrix
        return cache[index]


def quaternion_angle(a, b):
    a = np.asarray(a, dtype=np.float64)
    b = np.asarray(b, dtype=np.float64)
    dot = abs(float(np.dot(a / np.linalg.norm(a), b / np.linalg.norm(b))))
    return math.degrees(2 * math.acos(min(1.0, dot)))


def decompose(matrix):
    """Return translation, rotation [x, y, z, w], and scale of an affine matrix without shear."""
    translation = matrix[:3, 3].copy()
    scale = np.linalg.norm(matrix[:3, :3], axis=0)
    rotation = matrix[:3, :3] / scale
    if np.linalg.det(rotation) < 0:
        scale[0] *= -1
        rotation[:, 0] *= -1
    m = rotation
    trace = np.trace(m)
    if trace > 0:
        s = math.sqrt(trace + 1) * 2
        q = [(m[2, 1] - m[1, 2]) / s, (m[0, 2] - m[2, 0]) / s, (m[1, 0] - m[0, 1]) / s, s / 4]
    else:
        i = int(np.argmax(np.diag(m)))
        j = (i + 1) % 3
        k = (i + 2) % 3
        s = math.sqrt(max(0.0, 1 + m[i, i] - m[j, j] - m[k, k])) * 2
        q = [0.0, 0.0, 0.0, (m[k, j] - m[j, k]) / s]
        q[i] = s / 4
        q[j] = (m[j, i] + m[i, j]) / s
        q[k] = (m[k, i] + m[i, k]) / s
    q = np.asarray(q)
    return translation, q / np.linalg.norm(q), scale
