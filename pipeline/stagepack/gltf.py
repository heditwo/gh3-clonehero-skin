"""Read and write the glTF geometry used by public stage packs."""

import json
import struct
from io import BytesIO
from pathlib import Path

import numpy as np
from PIL import Image

REFLECTION = np.diag([1, 1, -1, 1])
VERTEX = np.dtype([("position", "<f4", 3), ("normal", "<f4", 3), ("uv", "<f4", 2), ("color", "u1", 4)])
STATIC_VERTEX = np.dtype(VERTEX.descr + [("lightmap", "<f4", 2), ("layer", "<f4", 2), ("mask", "<f4", 2),
                                          ("gh3uv", "<f4", (6, 2)), ("gh3binormal", "<f4", 3), ("gh3position1", "<f4", 3), ("gh3binormal1", "<f4", 3)])
# The original GH3 vertex streams that GH3 material templates read (see pipeline.gh3.scene.GH3_STREAMS).
GH3_STREAM_ATTRIBUTES = [f"_GH3_UV{i}" for i in range(6)] + ["_GH3_BINORMAL", "_GH3_POSITION1", "_GH3_BINORMAL1"]
SKINNED_VERTEX = np.dtype(VERTEX.descr + [("joints", "u1", 4), ("weights", "<f4", 4)])
# Skinned vertex with the GH3 streams (GH3BND02, see pipeline.gh3.scene.SKIN_STREAMS); skinned GLB primitives carry the
# tangent as _GH3_TANGENT and have no position 1 or binormal 1.
SKINNED_GH3_VERTEX = np.dtype(SKINNED_VERTEX.descr + [("gh3uv", "<f4", (6, 2)), ("gh3binormal", "<f4", 3), ("gh3tangent", "<f4", 3)])
SHADING = ("lit", "ambient", "unlit")


def quaternion_matrix(q):
    x, y, z, w = np.asarray(q, dtype=float) / np.linalg.norm(q)
    return np.array([[1-2*(y*y+z*z), 2*(x*y-z*w), 2*(x*z+y*w)],
                     [2*(x*y+z*w), 1-2*(x*x+z*z), 2*(y*z-x*w)],
                     [2*(x*z-y*w), 2*(y*z+x*w), 1-2*(x*x+y*y)]])


def matrix_quaternion(m):
    from math import sqrt
    m = np.asarray(m)[:3, :3]
    t = np.trace(m)
    if t > 0:
        s = sqrt(t+1)*2
        q = [(m[2, 1]-m[1, 2])/s, (m[0, 2]-m[2, 0])/s, (m[1, 0]-m[0, 1])/s, s/4]
    else:
        i = int(np.argmax(np.diag(m))); j = (i+1) % 3; k = (i+2) % 3
        s = sqrt(max(0, 1+m[i, i]-m[j, j]-m[k, k]))*2
        q = [0., 0., 0., (m[k, j]-m[j, k])/s]
        q[i] = s/4; q[j] = (m[j, i]+m[i, j])/s; q[k] = (m[k, i]+m[i, k])/s
    return (np.asarray(q) / np.linalg.norm(q)).tolist()


def transform_matrix(node):
    if "matrix" in node:
        m = np.asarray(node["matrix"], dtype=float).reshape(4, 4).T
    else:
        m = np.eye(4)
        m[:3, :3] = quaternion_matrix(node.get("rotation", [0, 0, 0, 1])) @ np.diag(node.get("scale", [1, 1, 1]))
        m[:3, 3] = node.get("translation", [0, 0, 0])
    if not np.isfinite(m).all() or abs(np.linalg.det(m[:3, :3])) < 1e-10:
        raise ValueError("Invalid glTF node transform")
    return m


class GlbWriter:
    def __init__(self):
        self.data = {"asset": {"version": "2.0", "generator": "GH3 stage pack tools"},
                     "scene": 0, "scenes": [{"nodes": []}], "nodes": [], "meshes": [], "materials": [],
                     "images": [], "textures": [], "accessors": [], "bufferViews": [], "buffers": []}
        self.binary = bytearray()
        self.materials = {}
        self.textures = {}

    def view(self, data):
        self.binary.extend(b"\0" * (-len(self.binary) % 4))
        index = len(self.data["bufferViews"])
        self.data["bufferViews"].append({"buffer": 0, "byteOffset": len(self.binary), "byteLength": len(data)})
        self.binary.extend(data)
        return index

    def accessor(self, values, kind, component=5126, normalized=False):
        values = np.asarray(values, dtype={5126: "<f4", 5125: "<u4", 5123: "<u2", 5121: "u1"}[component])
        index = len(self.data["accessors"])
        row = {"bufferView": self.view(values.tobytes()), "componentType": component, "count": len(values), "type": kind}
        if normalized: row["normalized"] = True
        if kind == "VEC3":
            row.update(min=values.min(0).tolist(), max=values.max(0).tolist())
        self.data["accessors"].append(row)
        return index

    def texture(self, image, name=None):
        """Embed a PIL image or PNG bytes once and return its texture index. Images get unique names so that extras can
        refer to them as '#<name>' (package format v1, section 6.6); equal images keep the first name."""
        if isinstance(image, (bytes, bytearray)): blob = bytes(image)
        else: raw = BytesIO(); image.save(raw, format="PNG"); blob = raw.getvalue()
        if blob not in self.textures:
            i = len(self.data["images"])
            names = {row.get("name") for row in self.data["images"]}
            base = unique = name or f"image-{i}"
            suffix = 0
            while unique in names:
                suffix += 1; unique = f"{base}-{suffix}"
            self.data["images"].append({"bufferView": self.view(blob), "mimeType": "image/png", "name": unique})
            self.data["textures"].append({"source": i})
            self.textures[blob] = i
        return self.textures[blob]

    def reference(self, image, name):
        """Texture reference for extras: '#<image name>' of the embedded image."""
        return "#" + self.data["images"][self.texture(image, name)]["name"]

    def material(self, name, definition, textures, layered=True, streams=None):
        if name in self.materials: return self.materials[name]
        gh3 = definition.get("gh3")
        row = {"name": name, "pbrMetallicRoughness": {"metallicFactor": 0, "roughnessFactor": 1}, "doubleSided": definition.get("doubleSided", True)}
        texture = definition.get("texture", "")
        if texture:
            row["pbrMetallicRoughness"]["baseColorTexture"] = {"index": self.texture(textures[texture], texture)}
        color = [float(v) for v in definition.get("color") or [1, 1, 1, 1]]
        scale = max(1.0, *color[:3])
        if color != [1, 1, 1, 1]:
            row["pbrMetallicRoughness"]["baseColorFactor"] = [v/scale for v in color[:3]] + [min(1.0, color[3])]
        if definition.get("lightmap"):
            row["occlusionTexture"] = {"index": self.texture(textures[definition["lightmap"]], definition["lightmap"]), "texCoord": 1}
        blend = definition.get("blend", 0)
        # GH3 applies the material alpha test (+232) to opaque and blended materials alike.
        cutoff = definition.get("cutoff", 0) if not definition.get("crowd") else 0
        row["alphaMode"] = "BLEND" if blend else "MASK" if cutoff else "OPAQUE"
        if row["alphaMode"] == "MASK": row["alphaCutoff"] = cutoff
        extras = {}
        if blend and cutoff: extras["gh3AlphaCutoff"] = cutoff
        if layered and definition.get("mask"): extras["gh3MaskTexture"] = self.reference(textures[definition["mask"]], definition["mask"])
        if layered and definition.get("layer"):
            extras["gh3LayerTexture"] = self.reference(textures[definition["layer"]["texture"]], definition["layer"]["texture"])
            extras["gh3LayerColor"] = [float(v) for v in definition["layer"]["color"]]
        if definition.get("uvWibble"): extras["gh3UvWibble"] = [float(v) for v in definition["uvWibble"]]
        if definition.get("sourceMaterial"): extras["gh3SourceMaterial"] = definition["sourceMaterial"]
        if "movieSlot" in definition: extras["gh3MovieSlot"] = definition["movieSlot"]
        if "textureAnimation" in definition: extras["gh3TextureAnimation"] = definition["textureAnimation"]
        if blend == 1: extras["gh3Additive"] = True
        if definition.get("crowd"): extras["gh3CrowdAtlas"] = True
        if definition.get("billboard"): extras["gh3Billboard"] = True
        if definition.get("crowdRole"): extras["gh3CrowdRole"] = definition["crowdRole"]
        if definition.get("sortedTransparency"): extras["gh3SortedTransparency"] = True
        if definition.get("shading"): extras["gh3Shading"] = definition["shading"]
        if scale > 1: extras["gh3ColorScale"] = scale
        if (layered if streams is None else streams) and gh3:
            # GH3 template, constant banks, samplers, and render state; the stage shader runs the template's own shaders.
            extras["gh3Shader"] = {**{k: gh3[k] for k in ("template", "vertexConstants", "pixelConstants", "addressing", "flags", "blend", "alphaRef", "drawOrder")},
                                   "textures": [self.reference(textures[t], t) if t in textures else "" for t in gh3["textures"]]}
        if extras: row["extras"] = extras
        index = len(self.data["materials"])
        self.data["materials"].append(row); self.materials[name] = index
        return index

    def mesh(self, name, parts, materials, textures):
        primitives = []
        for material, count, packed, faces in parts:
            if not faces: continue
            stride = len(packed) // count
            dtype = {36: VERTEX, STATIC_VERTEX.itemsize: STATIC_VERTEX, SKINNED_GH3_VERTEX.itemsize: SKINNED_GH3_VERTEX}.get(stride, SKINNED_VERTEX)
            vertices = np.frombuffer(packed, dtype=dtype)
            p = vertices["position"] * [1, 1, -1]; n = vertices["normal"] * [1, 1, -1]
            uv = vertices["uv"].copy(); uv[:, 1] = 1-uv[:, 1]
            attrs = {"POSITION": self.accessor(p, "VEC3"), "NORMAL": self.accessor(n, "VEC3"),
                     "TEXCOORD_0": self.accessor(uv, "VEC2"), "COLOR_0": self.accessor(vertices["color"], "VEC4", 5121, True)}
            layered = stride == STATIC_VERTEX.itemsize
            streams = stride in (STATIC_VERTEX.itemsize, SKINNED_GH3_VERTEX.itemsize) and materials[material].get("gh3")
            for field, attribute, used in (("lightmap", "TEXCOORD_1", materials[material].get("lightmap")),
                                           ("layer", "TEXCOORD_2", materials[material].get("layer")), ("mask", "TEXCOORD_3", materials[material].get("mask"))):
                if layered and used:
                    channel = vertices[field].copy(); channel[:, 1] = 1-channel[:, 1]
                    attrs[attribute] = self.accessor(channel, "VEC2")
            if streams:
                for i in range(6): attrs[f"_GH3_UV{i}"] = self.accessor(vertices["gh3uv"][:, i], "VEC2")
                attrs["_GH3_BINORMAL"] = self.accessor(vertices["gh3binormal"] * [1, 1, -1], "VEC3")
                if layered:
                    attrs["_GH3_POSITION1"] = self.accessor(vertices["gh3position1"] * [1, 1, -1], "VEC3")
                    attrs["_GH3_BINORMAL1"] = self.accessor(vertices["gh3binormal1"] * [1, 1, -1], "VEC3")
                else:
                    attrs["_GH3_TANGENT"] = self.accessor(vertices["gh3tangent"] * [1, 1, -1], "VEC3")
            if stride in (SKINNED_VERTEX.itemsize, SKINNED_GH3_VERTEX.itemsize):
                attrs.update(JOINTS_0=self.accessor(vertices["joints"], "VEC4", 5121), WEIGHTS_0=self.accessor(vertices["weights"], "VEC4"))
            indices = np.asarray(faces)[:, [0, 2, 1]].reshape(-1)
            primitives.append({"attributes": attrs, "indices": self.accessor(indices, "SCALAR", 5125),
                               "material": self.material(material, materials[material], textures, layered, bool(streams)), "mode": 4})
        if not primitives: return None
        index = len(self.data["meshes"])
        self.data["meshes"].append({"name": name, "primitives": primitives})
        return index

    def node(self, name, mesh=None, matrix=None, extras=None, parent=None):
        row = {"name": name}
        if mesh is not None: row["mesh"] = mesh
        if matrix is not None: row["matrix"] = (REFLECTION @ matrix @ REFLECTION).T.reshape(-1).tolist()
        if extras: row["extras"] = extras
        index = len(self.data["nodes"]); self.data["nodes"].append(row)
        (self.data["scenes"][0]["nodes"] if parent is None else self.data["nodes"][parent].setdefault("children", [])).append(index)
        return index

    def actor(self, parts, materials, textures, rig, clips):
        names, parents, positions, rotations = rig
        nodes = []
        worlds = {}
        def world(i):
            if i not in worlds:
                m = np.eye(4)
                m[:3, :3] = quaternion_matrix(rotations[i]*[-1, -1, -1, 1]); m[:3, 3] = positions[i]
                worlds[i] = world(parents[i]) @ m if parents[i] >= 0 else m
            return worlds[i]
        for i, name in enumerate(names):
            n = self.node(f"bone-{name:08x}")
            self.data["nodes"][n].update(translation=positions[i].tolist(), rotation=(rotations[i]*[-1, -1, -1, 1]).tolist())
            nodes.append(n)
        self.data["scenes"][0]["nodes"] = [nodes[i] for i,p in enumerate(parents) if p < 0]
        for i,p in enumerate(parents):
            if p >= 0: self.data["nodes"][nodes[p]].setdefault("children", []).append(nodes[i])
        mesh = self.node("model", self.mesh("model", parts, materials, textures))
        inverse = np.array([np.linalg.inv(world(i)).T.reshape(-1) for i in range(len(names))])
        self.data["skins"] = [{"joints": nodes, "inverseBindMatrices": self.accessor(inverse, "MAT4")}]
        self.data["nodes"][mesh]["skin"] = 0
        self.data["animations"] = []
        for name, duration, positions, rotations in clips:
            track = {"name": name, "samplers": [], "channels": []}
            times = self.accessor(np.linspace(0, duration, len(positions)), "SCALAR")
            for i in range(len(names)):
                for path, values, kind in [("translation", positions[:, i], "VEC3"), ("rotation", rotations[:, i]*[-1,-1,-1,1], "VEC4")]:
                    sampler = len(track["samplers"])
                    track["samplers"].append({"input": times, "output": self.accessor(values, kind), "interpolation": "LINEAR"})
                    track["channels"].append({"sampler": sampler, "target": {"node": nodes[i], "path": path}})
            self.data["animations"].append(track)

    def to_bytes(self):
        self.binary.extend(b"\0" * (-len(self.binary) % 4))
        self.data["buffers"] = [{"byteLength": len(self.binary)}]
        header = json.dumps(self.data, separators=(",", ":"), allow_nan=False).encode()
        header += b" " * (-len(header) % 4)
        return (struct.pack("<5I", 0x46546c67, 2, 28+len(header)+len(self.binary), len(header), 0x4e4f534a) +
                header + struct.pack("<2I", len(self.binary), 0x004e4942) + self.binary)

    def save(self, path):
        Path(path).write_bytes(self.to_bytes())


class GlbReader:
    def __init__(self, path, root=None):
        # Package paths in texture references are relative to the pack root.
        self.root = Path(root) if root is not None else Path(path).parent
        data = Path(path).read_bytes()
        if len(data) < 28 or struct.unpack_from("<3I", data) != (0x46546c67, 2, len(data)):
            raise ValueError("Expected a glTF 2.0 GLB")
        chunks = {}; cursor = 12
        while cursor < len(data):
            length, kind = struct.unpack_from("<2I", data, cursor); cursor += 8
            if cursor+length > len(data) or kind in chunks: raise ValueError("Invalid GLB chunks")
            chunks[kind] = data[cursor:cursor+length]; cursor += length
        self.data = json.loads(chunks[0x4e4f534a]); self.binary = chunks[0x004e4942]
        if self.data.get("extensionsRequired"): raise ValueError("Bake required glTF extensions before stage import")
        buffers = self.data.get("buffers", [])
        if len(buffers) != 1 or "uri" in buffers[0] or buffers[0]["byteLength"] > len(self.binary):
            raise ValueError("Stage GLB must contain one embedded buffer")

    def view(self, index):
        v = self.data["bufferViews"][index]; start = v.get("byteOffset", 0); end = start+v["byteLength"]
        if v.get("buffer", 0) != 0 or start < 0 or end > len(self.binary): raise ValueError("Invalid glTF buffer view")
        return self.binary[start:end]

    def accessor(self, index):
        a = self.data["accessors"][index]
        if "sparse" in a: raise ValueError("Bake sparse glTF accessors before stage import")
        dtype = np.dtype({5120: "i1", 5121: "u1", 5122: "<i2", 5123: "<u2", 5125: "<u4", 5126: "<f4"}[a["componentType"]])
        columns = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}[a["type"]]
        v = self.data["bufferViews"][a["bufferView"]]; data = self.view(a["bufferView"])
        offset = a.get("byteOffset", 0); stride = v.get("byteStride", columns*dtype.itemsize); count = a["count"]
        if not 1 <= count <= 10000000 or stride < columns*dtype.itemsize or offset < 0 or offset+(count-1)*stride+columns*dtype.itemsize > len(data):
            raise ValueError("Invalid glTF accessor range")
        output = np.ndarray((count, columns), dtype=dtype, buffer=data, offset=offset, strides=(stride, dtype.itemsize)).copy()
        if a.get("normalized") and a["componentType"] != 5126:
            output = np.maximum(-1, output.astype(float) / np.iinfo(dtype).max)
        if not np.isfinite(output).all(): raise ValueError("Non-finite glTF accessor")
        return output

    def named_texture(self, name):
        """Index of the first texture whose image has this name, or None."""
        images = self.data.get("images", [])
        return next((i for i, texture in enumerate(self.data.get("textures", []))
                     if 0 <= texture.get("source", -1) < len(images) and images[texture["source"]].get("name") == name), None)

    def named_image(self, name):
        """Index of the first image with this name, or None."""
        return next((i for i, image in enumerate(self.data.get("images", [])) if image.get("name") == name), None)

    def image(self, texture):
        return self.image_at(self.data["textures"][texture]["source"])

    def image_at(self, index):
        row = self.data["images"][index]
        if "bufferView" not in row: raise ValueError("Stage textures must be embedded in the GLB")
        return Image.open(BytesIO(self.view(row["bufferView"]))).convert("RGBA")

    def nodes(self):
        visited = set()
        def visit(index, parent, depth):
            if index in visited or depth > 128: raise ValueError("Invalid glTF scene hierarchy")
            visited.add(index); node = self.data["nodes"][index]
            world = parent @ transform_matrix(node)
            yield index, node, REFLECTION @ world @ REFLECTION
            for child in node.get("children", []): yield from visit(child, world, depth+1)
        for node in self.data["scenes"][self.data.get("scene", 0)]["nodes"]:
            yield from visit(node, np.eye(4), 0)

    def primitive(self, primitive, world):
        if primitive.get("mode", 4) != 4: raise ValueError("Triangulate stage meshes before export")
        if primitive.get("targets"): raise ValueError("Bake morph targets before stage import")
        attrs = primitive["attributes"]
        if "NORMAL" not in attrs: raise ValueError("Export stage vertex normals")
        positions = self.accessor(attrs["POSITION"]); normals = self.accessor(attrs["NORMAL"])
        count = len(positions)
        if positions.shape != (count, 3) or normals.shape != (count, 3): raise ValueError("Invalid glTF vertex attributes")
        transform = world @ REFLECTION
        positions = positions @ transform[:3, :3].T + transform[:3, 3]
        normals = normals @ np.linalg.inv(transform[:3, :3])
        lengths = np.linalg.norm(normals, axis=1); normals /= np.maximum(lengths, 1e-10)[:, None]
        uv, lightmap, layer, mask = (self.accessor(attrs[f"TEXCOORD_{i}"]) if f"TEXCOORD_{i}" in attrs else np.zeros((count, 2)) for i in range(4))
        color = self.accessor(attrs["COLOR_0"]) if "COLOR_0" in attrs else np.ones((count, 4))
        if color.shape == (count, 3): color = np.column_stack((color, np.ones(count)))
        if any(channel.shape != (count, 2) for channel in (uv, lightmap, layer, mask)) or color.shape != (count, 4): raise ValueError("Invalid glTF UV or color count")
        indices = self.accessor(primitive["indices"]).reshape(-1) if "indices" in primitive else np.arange(count)
        if indices.dtype.kind not in "ui" or len(indices) % 3 or np.any(indices < 0) or np.any(indices >= count): raise ValueError("Invalid glTF triangles")
        faces = indices.reshape(-1, 3)
        if np.linalg.det(transform[:3, :3]) < 0: faces = faces[:, [0, 2, 1]]
        material = self.data.get("materials", [])[primitive["material"]] if "material" in primitive else {}
        # A GH3 template applies its own colour constant; its vertex colours stay as GH3 stores them.
        if "gh3Shader" not in material.get("extras", {}):
            color = color * material.get("pbrMetallicRoughness", {}).get("baseColorFactor", [1, 1, 1, 1])
        packed = np.zeros(count, dtype=STATIC_VERTEX)
        packed["position"] = positions; packed["normal"] = normals
        if all(name in attrs for name in GH3_STREAM_ATTRIBUTES[:7]):
            for i in range(6):
                channel = self.accessor(attrs[f"_GH3_UV{i}"])
                if channel.shape != (count, 2): raise ValueError("Invalid GH3 UV stream")
                packed["gh3uv"][:, i] = channel
            binormals = self.accessor(attrs["_GH3_BINORMAL"])
            pivots = self.accessor(attrs["_GH3_POSITION1"]) if "_GH3_POSITION1" in attrs else positions @ np.linalg.inv(transform[:3, :3]).T
            binormals1 = self.accessor(attrs["_GH3_BINORMAL1"]) if "_GH3_BINORMAL1" in attrs else np.zeros((count, 3))
            if binormals.shape != (count, 3) or pivots.shape != (count, 3) or binormals1.shape != (count, 3): raise ValueError("Invalid GH3 vertex stream")
            packed["gh3binormal"] = binormals @ transform[:3, :3].T
            packed["gh3binormal1"] = binormals1 @ transform[:3, :3].T
            packed["gh3position1"] = pivots @ transform[:3, :3].T + transform[:3, 3] if "_GH3_POSITION1" in attrs else positions
        packed["uv"] = uv; packed["uv"][:, 1] = 1-uv[:, 1]
        packed["lightmap"] = lightmap; packed["lightmap"][:, 1] = 1-lightmap[:, 1]
        packed["layer"] = layer; packed["layer"][:, 1] = 1-layer[:, 1]
        packed["mask"] = mask; packed["mask"][:, 1] = 1-mask[:, 1]
        packed["color"] = np.round(np.clip(color, 0, 1)*255).astype("u1")
        return packed, faces
