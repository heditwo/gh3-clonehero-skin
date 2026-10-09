"""Small GLB and PNG edits for package export and open. No bpy and no other module of the add-on, so the tests load it alone."""

import json
import struct
import zlib

GLB_MAGIC = 0x46546C67
JSON_CHUNK = 0x4E4F534A
BIN_CHUNK = 0x004E4942
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
# PNG chunks that Blender writes with the render time and date. They make the preview image different for each export.
PNG_VOLATILE = {b"tEXt", b"zTXt", b"iTXt", b"tIME"}


def read_glb(data):
    """(glTF JSON, binary chunk) of GLB bytes."""
    magic, version, length = struct.unpack_from("<3I", data)
    if magic != GLB_MAGIC or version != 2 or length != len(data): raise ValueError("not a glTF 2.0 GLB")
    size, kind = struct.unpack_from("<2I", data, 12)
    if kind != JSON_CHUNK: raise ValueError("the first GLB chunk is not JSON")
    gltf = json.loads(bytes(data[20:20 + size]).decode("utf-8"))
    binary = b""
    cursor = 20 + size
    if cursor < len(data):
        size, kind = struct.unpack_from("<2I", data, cursor)
        if kind != BIN_CHUNK: raise ValueError("the second GLB chunk is not binary")
        binary = bytes(data[cursor + 8:cursor + 8 + size])
    return gltf, binary


def write_glb(gltf, binary):
    """GLB bytes. The JSON chunk is padded with spaces and the binary chunk with zeros (glTF 2.0, section 4.4)."""
    text = json.dumps(gltf, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    text += b" " * (-len(text) % 4)
    chunks = struct.pack("<2I", len(text), JSON_CHUNK) + text
    if binary:
        binary = bytes(binary) + b"\0" * (-len(binary) % 4)
        chunks += struct.pack("<2I", len(binary), BIN_CHUNK) + binary
    return struct.pack("<3I", GLB_MAGIC, 2, 12 + len(chunks)) + chunks


def fill_texcoord_gaps(data):
    """Give primitives the missing TEXCOORD_n below their highest set, filled with zeros. Returns (GLB bytes, count).

    The Blender glTF importer reads TEXCOORD_0, TEXCOORD_1, ... only up to the first missing set. GH3 meshes can have
    TEXCOORD_3 (the mask UV) without TEXCOORD_1 and TEXCOORD_2, and the importer would drop it.
    """
    gltf, binary = read_glb(data)
    accessors = gltf.get("accessors", [])
    zeros = {}
    added = 0
    for mesh in gltf.get("meshes", []):
        for primitive in mesh.get("primitives", []):
            attributes = primitive.get("attributes", {})
            sets = [int(name[9:]) for name in attributes if name.startswith("TEXCOORD_") and name[9:].isdigit()]
            if not sets: continue
            count = accessors[attributes["POSITION"]]["count"]
            for index in range(max(sets)):
                if f"TEXCOORD_{index}" in attributes: continue
                if count not in zeros:
                    padding = -len(binary) % 4
                    binary += b"\0" * (padding + 8 * count)
                    gltf.setdefault("bufferViews", []).append({"buffer": 0, "byteOffset": len(binary) - 8 * count, "byteLength": 8 * count})
                    accessors.append({"bufferView": len(gltf["bufferViews"]) - 1, "componentType": 5126, "count": count, "type": "VEC2"})
                    zeros[count] = len(accessors) - 1
                attributes[f"TEXCOORD_{index}"] = zeros[count]
                added += 1
    if not added: return data, 0
    gltf["accessors"] = accessors
    gltf["buffers"][0]["byteLength"] = len(binary)
    return write_glb(gltf, binary), added


COMPONENT_SIZE = {5120: 1, 5121: 1, 5122: 2, 5123: 2, 5125: 4, 5126: 4}
COMPONENT_COUNT = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}
INDEX_TYPES = {5121: "<u1", 5123: "<u2", 5125: "<u4"}


def accessor_rows(gltf, binary, index):
    """The elements of an accessor as raw bytes: one row of bytes for each element."""
    import numpy as np
    accessor = gltf["accessors"][index]
    view = gltf["bufferViews"][accessor["bufferView"]]
    width = COMPONENT_SIZE[accessor["componentType"]] * COMPONENT_COUNT[accessor["type"]]
    stride = view.get("byteStride", width)
    start = view.get("byteOffset", 0) + accessor.get("byteOffset", 0)
    count = accessor["count"]
    data = np.frombuffer(binary, dtype=np.uint8, count=(count - 1) * stride + width, offset=start)
    return np.lib.stride_tricks.as_strided(data, shape=(count, width), strides=(stride, 1)).copy()


def separate_faces(data):
    """Give some triangles their own vertex copies, so that the Blender importer keeps them and their neighbours:

    - A triangle that repeats the vertex set of an earlier triangle of its primitive. Blender cannot have two faces
      with the same vertices and drops the later one. GH3 meshes have back faces (the opposite winding) and copies.
    - A triangle with two corners at the same position. Blender cannot make the custom normal space of a vertex
      whose faces include such a triangle, and gives all faces at that vertex a default normal.

    The glTF exporter merges equal vertices again. Returns (GLB bytes, count of triangles).
    """
    import numpy as np
    gltf, binary = read_glb(data)
    binary = bytearray(binary)
    total = 0

    def add(rows, accessor):
        nonlocal binary
        binary += b"\0" * (-len(binary) % 4)
        gltf["bufferViews"].append({"buffer": 0, "byteOffset": len(binary), "byteLength": rows.size})
        binary += rows.tobytes()
        gltf["accessors"].append({**accessor, "bufferView": len(gltf["bufferViews"]) - 1, "count": len(rows)})
        gltf["accessors"][-1].pop("byteOffset", None)
        gltf["accessors"][-1].pop("sparse", None)
        return len(gltf["accessors"]) - 1

    for mesh in gltf.get("meshes", []):
        for primitive in mesh.get("primitives", []):
            if primitive.get("mode", 4) != 4 or "indices" not in primitive: continue
            accessor = gltf["accessors"][primitive["indices"]]
            faces = accessor_rows(gltf, bytes(binary), primitive["indices"]).view(INDEX_TYPES[accessor["componentType"]]).reshape(-1, 3).astype(np.int64)
            positions = accessor_rows(gltf, bytes(binary), primitive["attributes"]["POSITION"])
            corners = positions[faces]
            degenerate = np.flatnonzero((corners[:, 0] == corners[:, 1]).all(1) | (corners[:, 1] == corners[:, 2]).all(1)
                                        | (corners[:, 0] == corners[:, 2]).all(1))
            seen, duplicates = set(), set(degenerate.tolist())
            for number, face in enumerate(map(tuple, faces)):
                key = tuple(sorted(face))
                if key in seen: duplicates.add(number)
                else: seen.add(key)
            if not duplicates: continue
            duplicates = sorted(duplicates)
            count = len(positions)
            copies = faces[duplicates].reshape(-1)
            faces[duplicates] = np.arange(count, count + len(copies)).reshape(-1, 3)
            for name, index in list(primitive["attributes"].items()):
                rows = accessor_rows(gltf, bytes(binary), index)
                primitive["attributes"][name] = add(np.concatenate([rows, rows[copies]]), gltf["accessors"][index])
            indices = faces.reshape(-1).astype("<u4").view(np.uint8).reshape(-1, 4)
            primitive["indices"] = add(indices, {"componentType": 5125, "type": "SCALAR"})
            total += len(duplicates)
    if not total: return data, 0
    gltf["buffers"][0]["byteLength"] = len(binary)
    return write_glb(gltf, bytes(binary)), total


def normalize_normals(data):
    """Make NORMAL vectors unit length. Returns (GLB bytes, count of accessors).

    glTF requires unit normals, and Blender exports unit normals. Some GH3 meshes have longer or shorter normals; the
    Blender 4.2 importer turns their custom normals wrong (up to 38 degrees), while their directions are correct.
    """
    import numpy as np
    gltf, binary = read_glb(data)
    binary = bytearray(binary)
    changed = {}
    for mesh in gltf.get("meshes", []):
        for primitive in mesh.get("primitives", []):
            index = primitive.get("attributes", {}).get("NORMAL")
            if index is None: continue
            if index not in changed:
                accessor = gltf["accessors"][index]
                if accessor["componentType"] != 5126 or accessor["type"] != "VEC3" or "sparse" in accessor: changed[index] = index; continue
                normals = accessor_rows(gltf, bytes(binary), index).view("<f4").reshape(-1, 3).astype(np.float64)
                length = np.linalg.norm(normals, axis=1)
                if np.abs(length - 1).max(initial=0) <= 1e-4 or not length.all(): changed[index] = index; continue
                rows = (normals / length[:, None]).astype("<f4")
                binary += b"\0" * (-len(binary) % 4)
                gltf["bufferViews"].append({"buffer": 0, "byteOffset": len(binary), "byteLength": rows.nbytes})
                binary += rows.tobytes()
                copy = {key: value for key, value in accessor.items() if key not in ("byteOffset", "min", "max")}
                gltf["accessors"].append({**copy, "bufferView": len(gltf["bufferViews"]) - 1})
                changed[index] = len(gltf["accessors"]) - 1
            primitive["attributes"]["NORMAL"] = changed[index]
    count = sum(1 for old, new in changed.items() if old != new)
    if not count: return data, 0
    gltf["buffers"][0]["byteLength"] = len(binary)
    return write_glb(gltf, bytes(binary)), count


def image_bytes(gltf, binary, index):
    view = gltf["bufferViews"][gltf["images"][index]["bufferView"]]
    start = view.get("byteOffset", 0)
    return binary[start:start + view["byteLength"]]


def strip_png_text(data):
    """PNG bytes without text and time chunks. Other bytes are returned without a change."""
    if not data.startswith(PNG_SIGNATURE): return data
    out = bytearray(PNG_SIGNATURE)
    cursor = len(PNG_SIGNATURE)
    while cursor + 8 <= len(data):
        length, kind = struct.unpack_from(">I4s", data, cursor)
        end = cursor + 12 + length
        if kind not in PNG_VOLATILE: out += data[cursor:end]
        cursor = end
    return bytes(out)


def png_chunk(kind, payload):
    return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", zlib.crc32(kind + payload) & 0xFFFFFFFF)


# Actor GLBs (PACKAGE-FORMAT 6.7 and 8.2).
UNIT_SCALE = 1e-4


def animation_names(data):
    gltf, _ = read_glb(data)
    return [a.get("name", "") for a in gltf.get("animations", [])]


def keep_animations(data, names):
    """GLB bytes with only the named animations, in the order of names. The binary chunk does not change."""
    gltf, binary = read_glb(data)
    by_name = {a.get("name", ""): a for a in gltf.get("animations", [])}
    kept = [by_name[name] for name in names if name in by_name]
    if len(kept) == len(gltf.get("animations", [])) and [a.get("name", "") for a in gltf.get("animations", [])] == list(names): return data
    if kept: gltf["animations"] = kept
    else: gltf.pop("animations", None)
    return write_glb(gltf, binary)


def rename_animations(data, names):
    """GLB bytes with the animations renamed (old name to new name)."""
    gltf, binary = read_glb(data)
    changed = False
    for animation in gltf.get("animations", []):
        new = names.get(animation.get("name"))
        if new and new != animation.get("name"):
            animation["name"] = new
            changed = True
    return write_glb(gltf, binary) if changed else data


def float_rows(gltf, binary, index):
    import numpy as np
    accessor = gltf["accessors"][index]
    rows = accessor_rows(gltf, binary, index)
    if accessor["componentType"] != 5126: raise ValueError("animation data is not float32")
    return rows.view("<f4").reshape(accessor["count"], COMPONENT_COUNT[accessor["type"]]).astype(np.float64)


def drop_unit_scale_channels(data):
    """Remove the scale channels whose keys are all within 1e-4 of 1 (Blender writes one for each joint; the importer
    drops them, PACKAGE-FORMAT 6.7). Returns (GLB bytes, dropped count, messages for other scale channels)."""
    import numpy as np
    gltf, binary = read_glb(data)
    dropped, problems = 0, []
    nodes = gltf.get("nodes", [])
    for animation in gltf.get("animations", []):
        channels = []
        for channel in animation["channels"]:
            if channel["target"].get("path") == "scale":
                values = float_rows(gltf, binary, animation["samplers"][channel["sampler"]]["output"])
                error = float(np.abs(values - 1).max(initial=0))
                if error <= UNIT_SCALE:
                    dropped += 1
                    continue
                node = channel["target"].get("node")
                problems.append(f"clip '{animation.get('name', '')}' scales the joint '{nodes[node].get('name', node) if node is not None else '?'}' "
                                f"(|scale - 1| = {error:.3g}); animate only translation and rotation")
            channels.append(channel)
        used = sorted({c["sampler"] for c in channels})
        renumber = {old: new for new, old in enumerate(used)}
        animation["samplers"] = [animation["samplers"][old] for old in used]
        for channel in channels: channel["sampler"] = renumber[channel["sampler"]]
        animation["channels"] = channels
    return (write_glb(gltf, binary) if dropped else data), dropped, problems


def _append(gltf, binary, payload, accessor=None):
    """Add bytes to a bytearray binary chunk as a new buffer view (and an accessor copy). Returns the accessor or view index."""
    binary += b"\0" * (-len(binary) % 4)
    gltf.setdefault("bufferViews", []).append({"buffer": 0, "byteOffset": len(binary), "byteLength": len(payload)})
    binary += payload
    view = len(gltf["bufferViews"]) - 1
    if accessor is None: return view
    copy = {key: value for key, value in accessor.items() if key not in ("byteOffset", "sparse")}
    copy["bufferView"] = view
    gltf.setdefault("accessors", []).append(copy)
    return len(gltf["accessors"]) - 1


def merge_animations(data, source, names):
    """Add the named animations of the source GLB to a GLB. Channels find their target node by node name.
    Returns (GLB bytes, messages for channels whose node is missing)."""
    gltf, binary = read_glb(data)
    binary = bytearray(binary)
    source_gltf, source_binary = read_glb(source)
    target_nodes = {node.get("name"): index for index, node in enumerate(gltf.get("nodes", [])) if node.get("name")}
    source_nodes = source_gltf.get("nodes", [])
    existing = {a.get("name", "") for a in gltf.get("animations", [])}
    problems = []
    for animation in source_gltf.get("animations", []):
        name = animation.get("name", "")
        if name not in names or name in existing: continue
        copies, samplers, channels = {}, [], []
        for channel in animation["channels"]:
            node_name = source_nodes[channel["target"]["node"]].get("name")
            if node_name not in target_nodes:
                problems.append(f"clip '{name}': the model has no joint '{node_name}'")
                continue
            sampler = animation["samplers"][channel["sampler"]]
            new = {**sampler}
            for key in ("input", "output"):
                index = sampler[key]
                if index not in copies:
                    copies[index] = _append(gltf, binary, accessor_rows(source_gltf, source_binary, index).tobytes(), source_gltf["accessors"][index])
                new[key] = copies[index]
            samplers.append(new)
            channels.append({"sampler": len(samplers) - 1, "target": {"node": target_nodes[node_name], "path": channel["target"]["path"]}})
        if channels: gltf.setdefault("animations", []).append({"name": name, "channels": channels, "samplers": samplers})
    if gltf.get("buffers"): gltf["buffers"][0]["byteLength"] = len(binary)
    return write_glb(gltf, bytes(binary)), problems


def image_bytes_by_name(data):
    """{image name: PNG bytes} of the named images of a GLB."""
    gltf, binary = read_glb(data)
    return {image["name"]: bytes(image_bytes(gltf, binary, index)) for index, image in enumerate(gltf.get("images", []))
            if image.get("name") and "bufferView" in image}


def restore_names(data, materials=(), nodes=()):
    """Give exported images, materials, and nodes their package names again. Blender renames 'name' to 'name.001' when
    the name is in use in the .blend file; texture references '#name' then have no image. materials, nodes: the names
    of the package GLB."""
    import re
    gltf, binary = read_glb(data)
    changed = False
    for kind, wanted in (("images", texture_reference_names(gltf)), ("materials", set(materials)), ("nodes", set(nodes))):
        names = {item.get("name") for item in gltf.get(kind, [])}
        for item in gltf.get(kind, []):
            base = re.sub(r"(\.\d{3,})+$", "", item.get("name", ""))
            if base != item.get("name") and base in wanted and base not in names:
                item["name"] = base
                names.add(base)
                changed = True
    return write_glb(gltf, binary) if changed else data


def add_images(data, images):
    """Embed PNG images with the given names (texture references '#<name>' to images that a material socket does not use).
    Each image also gets a glTF texture: the pipeline compiler finds '#<name>' through the textures."""
    if not images: return data
    gltf, binary = read_glb(data)
    binary = bytearray(binary)
    present = {image.get("name") for image in gltf.get("images", [])}
    for name, payload in sorted(images.items()):
        if name in present: continue
        view = _append(gltf, binary, payload)
        gltf.setdefault("images", []).append({"name": name, "mimeType": "image/png", "bufferView": view})
        gltf.setdefault("textures", []).append({"source": len(gltf["images"]) - 1})
    if not gltf.get("buffers"): gltf["buffers"] = [{}]
    gltf["buffers"][0]["byteLength"] = len(binary)
    return write_glb(gltf, bytes(binary))


def texture_reference_names(gltf):
    """Image names of the '#<name>' texture references in the material extras of a glTF document."""
    names = set()

    def visit(value):
        if isinstance(value, str) and value.startswith("#"): names.add(value[1:])
        elif isinstance(value, list):
            for item in value: visit(item)
        elif isinstance(value, dict):
            for key, item in value.items():
                if key in ("gh3MaskTexture", "gh3LayerTexture", "gh3Shader", "textures"): visit(item)

    for material in gltf.get("materials", []): visit(material.get("extras", {}))
    return names
