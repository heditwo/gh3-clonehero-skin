"""Compare an original GH3 rig GLB with its Blender round trip. Prints one JSON row of measured differences."""

import hashlib
import json
import sys

import numpy as np
from scipy.spatial import cKDTree

from pipeline.stagepack.gltf import GlbReader, matrix_quaternion, quaternion_matrix, transform_matrix


def trs(node):
    m = np.eye(4)
    m[:3, :3] = quaternion_matrix(node.get("rotation", [0, 0, 0, 1])) @ np.diag(node.get("scale", [1, 1, 1]))
    m[:3, 3] = node.get("translation", [0, 0, 0])
    return m


class Rig:
    def __init__(self, path):
        self.glb = glb = GlbReader(path)
        nodes = glb.data["nodes"]
        self.parent = {c: i for i, n in enumerate(nodes) for c in n.get("children", [])}
        skin = glb.data["skins"][0]
        self.joints = skin["joints"]
        self.names = [nodes[j]["name"] for j in self.joints]
        self.ibm = glb.accessor(skin["inverseBindMatrices"]).reshape(-1, 4, 4).transpose(0, 2, 1).astype(float)
        self.local = {i: transform_matrix(n) for i, n in enumerate(nodes)}
        self.mesh_node = next(i for i, n in enumerate(nodes) if "skin" in n)
        self.extras = {n.get("name"): n.get("extras", {}) for n in nodes}
        positions, normals, uvs, weights, materials, attributes, used = [], [], [], [], [], set(), []
        self.streams = {}
        for primitive in glb.data["meshes"][nodes[self.mesh_node]["mesh"]]["primitives"]:
            a = primitive["attributes"]
            attributes |= set(a)
            p = glb.accessor(a["POSITION"]).astype(float)
            count = len(p)
            used.append(np.isin(np.arange(count), glb.accessor(primitive["indices"]).reshape(-1)))
            for name in [x for x in a if x == "COLOR_0" or x.startswith("_GH3")]:
                self.streams.setdefault(name, []).append(glb.accessor(a[name]).astype(float))
            positions.append(p)
            normals.append(glb.accessor(a["NORMAL"]))
            uvs.append(glb.accessor(a["TEXCOORD_0"]))
            joints = glb.accessor(a["JOINTS_0"]).astype(int)
            w = glb.accessor(a["WEIGHTS_0"]).astype(float)
            if "JOINTS_1" in a:
                joints = np.hstack([joints, glb.accessor(a["JOINTS_1"]).astype(int)])
                w = np.hstack([w, glb.accessor(a["WEIGHTS_1"])])
            weights.append((joints, w))
            materials += [glb.data["materials"][primitive["material"]]["name"]] * count
        self.positions = np.vstack(positions)
        self.normals = np.vstack(normals)
        self.uvs = np.vstack(uvs)
        self.materials = np.asarray(materials)
        self.attributes = sorted(attributes)
        self.used = np.concatenate(used)
        self.primitive_counts = [len(p) for p in positions]
        width = max(j.shape[1] for j, _ in weights)
        self.joint_index = np.vstack([np.pad(j, ((0, 0), (0, width - j.shape[1]))) for j, _ in weights])
        self.weights = np.vstack([np.pad(w, ((0, 0), (0, width - w.shape[1]))) for _, w in weights])
        self.material_extras = {m["name"]: sorted(m.get("extras", {})) for m in glb.data["materials"]}
        self.animations = glb.data.get("animations", [])

    def world(self, local=None):
        local = {**self.local, **(local or {})}
        cache = {}

        def w(i):
            if i not in cache:
                cache[i] = (w(self.parent[i]) if i in self.parent else np.eye(4)) @ local[i]
            return cache[i]

        return w

    def skin(self, local=None, ibm=None):
        w = self.world(local)
        ibm = self.ibm if ibm is None else ibm
        palette = np.array([w(j) @ ibm[k] for k, j in enumerate(self.joints)])
        homogeneous = np.column_stack([self.positions, np.ones(len(self.positions))])
        result = np.zeros((len(self.positions), 3))
        for c in range(self.weights.shape[1]):
            m = palette[self.joint_index[:, c]]
            result += self.weights[:, c, None] * np.einsum("nij,nj->ni", m, homogeneous)[:, :3]
        return result

    def named_weights(self, row):
        out = {}
        for j, w in zip(self.joint_index[row], self.weights[row]):
            if w > 0:
                out[self.names[j]] = out.get(self.names[j], 0) + w
        return out

    def sample(self, time):
        """Local joint matrices of the first clip at a time (LINEAR or STEP, as stored)."""
        nodes = self.glb.data["nodes"]
        track = self.animations[0]
        values = {j: dict(nodes[j]) for j in self.joints}
        for channel in track["channels"]:
            node = channel["target"]["node"]
            path = channel["target"]["path"]
            if node not in values:
                continue
            sampler = track["samplers"][channel["sampler"]]
            times = self.glb.accessor(sampler["input"]).reshape(-1)
            out = self.glb.accessor(sampler["output"]).astype(float)
            i = int(np.clip(np.searchsorted(times, time, side="right") - 1, 0, len(times) - 1))
            if sampler.get("interpolation", "LINEAR") == "STEP" or i == len(times) - 1:
                v = out[i]
            else:
                f = (time - times[i]) / (times[i + 1] - times[i])
                a, b = out[i], out[i + 1]
                if path == "rotation" and np.dot(a, b) < 0:
                    b = -b
                v = a * (1 - f) + b * f
                if path == "rotation":
                    v /= np.linalg.norm(v)
            values[node][path] = v.tolist()
        return {j: trs(values[j]) for j in self.joints}


def angle(a, b):
    # Quaternion form: arccos of the matrix trace loses precision near zero (float32 noise reads as 0.03 degrees).
    x, y, z, w = matrix_quaternion(a[:3, :3].T @ b[:3, :3])
    return float(np.degrees(2 * np.arctan2(np.linalg.norm([x, y, z]), abs(w))))


def main(original_path, roundtrip_path):
    a, b = Rig(original_path), Rig(roundtrip_path)
    row = {"file": roundtrip_path.rsplit("/", 1)[-1]}
    row["joints"] = [len(a.joints), len(b.joints)]
    common = [n for n in a.names if n in b.names]
    row["missingJoints"] = len(a.names) - len(common)
    ai = {n: a.joints[k] for k, n in enumerate(a.names)}
    bi = {n: b.joints[k] for k, n in enumerate(b.names)}
    pa = lambda r, n: r.glb.data["nodes"][r.parent[n]]["name"] if n in r.parent else None
    row["parentMismatch"] = sum(
        pa(a, ai[n]) != pa(b, bi[n]) for n in common if pa(a, ai[n]) in a.names or pa(b, bi[n]) in b.names
    )
    row["jointOrderSame"] = a.names == b.names
    wa, wb = a.world(), b.world()
    local_t, local_r, world_t, world_r, scale = [], [], [], [], []
    for n in common:
        la, lb = a.local[ai[n]], b.local[bi[n]]
        if pa(a, ai[n]) in a.names:
            local_t.append(np.linalg.norm(la[:3, 3] - lb[:3, 3]) * 1000)
            local_r.append(angle(la, lb))
        world_t.append(np.linalg.norm(wa(ai[n])[:3, 3] - wb(bi[n])[:3, 3]) * 1000)
        world_r.append(angle(wa(ai[n]), wb(bi[n])))
        scale.append(float(np.abs(np.linalg.norm(lb[:3, :3], axis=0) - 1).max()))
    row["localTranslationMm"] = max(local_t)
    row["localRotationDeg"] = max(local_r)
    row["worldTranslationMm"] = max(world_t)
    row["worldRotationDeg"] = max(world_r)
    row["scaleError"] = max(scale)
    ka = {n: k for k, n in enumerate(a.names)}
    kb = {n: k for k, n in enumerate(b.names)}
    row["ibmMaxAbs"] = float(max(np.abs(a.ibm[ka[n]] - b.ibm[kb[n]]).max() for n in common))
    # The runtime check in compile_actor: joint world @ IBM must equal the skinned mesh node world.
    mesh_world = b.world()(b.mesh_node)
    row["rigIbmConsistency"] = float(max(np.abs(wb(j) @ b.ibm[k] - mesh_world).max() for k, j in enumerate(b.joints)))
    row["vertices"] = [len(a.positions), len(b.positions)]
    row["primitives"] = [len(a.primitive_counts), len(b.primitive_counts)]
    row["vertexOrderSame"] = len(a.positions) == len(b.positions) and bool(
        np.allclose(a.positions, b.positions, atol=1e-5)
    )
    # Match vertices by bind-pose skinned position and UV (Blender can reorder and split or merge vertices).
    bind_a, bind_b = a.skin(), b.skin()
    row["bindSkinVsMeshMm"] = float(np.linalg.norm(bind_b - b.positions, axis=1).max() * 1000)
    tree = cKDTree(np.hstack([bind_b, b.uvs * 0.01, b.normals * 0.001]))
    keys_a = np.hstack([bind_a, a.uvs * 0.01, a.normals * 0.001])
    _, match = tree.query(keys_a)
    # Blender drops vertices that no triangle uses; compare only the used ones.
    row["unusedVertices"] = [int((~a.used).sum()), int((~b.used).sum())]
    keep = a.used
    a.positions, a.normals, a.uvs, a.materials, a.joint_index, a.weights = (
        x[keep] for x in (a.positions, a.normals, a.uvs, a.materials, a.joint_index, a.weights)
    )
    bind_a, match, keys_a = bind_a[keep], match[keep], keys_a[keep]
    row["matchPositionMm"] = float(np.linalg.norm(bind_a - bind_b[match], axis=1).max() * 1000)
    row["matchUv"] = float(np.abs(a.uvs - b.uvs[match]).max())
    cos = np.clip(
        np.sum(a.normals * b.normals[match], 1)
        / np.linalg.norm(a.normals, axis=1)
        / np.linalg.norm(b.normals[match], axis=1),
        -1,
        1,
    )
    row["matchNormalDeg"] = float(np.degrees(np.arccos(cos)).max())
    row["materialMismatch"] = int(
        sum(
            a.materials[i] not in b.materials[tree.query_ball_point(keys_a[i], 1e-5) or [match[i]]]
            for i in np.where(a.materials != b.materials[match])[0]
        )
    )
    counts, sums, diffs = [], [], []
    for i in range(len(a.positions)):
        x, y = a.named_weights(i), b.named_weights(match[i])
        counts.append(len(x) != len(y))
        sums.append(abs(sum(y.values()) - 1))
        diffs.append(max(abs(x.get(k, 0) - y.get(k, 0)) for k in x.keys() | y.keys()))
    row["weightCountChanged"] = int(sum(counts))
    row["weightSumError"] = float(max(sums))
    row["weightMaxDiff"] = float(max(diffs))
    # Vertex error from the IBM differences alone: skin the round-trip mesh with the original IBMs instead of its own.
    if not row["missingJoints"]:
        row["ibmVertexErrorMm"] = float(
            np.linalg.norm(b.skin(ibm=np.array([a.ibm[ka[n]] for n in b.names])) - bind_b, axis=1).max() * 1000
        )
    row["materials"] = [len(a.material_extras), len(b.material_extras)]
    row["materialNamesSame"] = sorted(a.material_extras) == sorted(b.material_extras)
    row["materialExtrasKept"] = (
        f"{sum(bool(b.material_extras.get(m)) for m in a.material_extras if a.material_extras[m])}/{sum(bool(v) for v in a.material_extras.values())}"
    )
    row["materialExtrasKeys"] = sorted({k for v in b.material_extras.values() for k in v})
    row["nodeExtrasKept"] = (
        f"{sum('gh3Probe' in b.extras.get(n, {}) for n in a.names)}/{len(a.names)} joints, mesh node {'gh3Probe' in b.extras.get(b.glb.data['nodes'][b.mesh_node]['name'], {})}"
    )
    # Attribute values on matched vertices; COLOR_0 as 0..1 after normalization.
    row["streamMaxDiff"] = {}
    for name, parts in a.streams.items():
        if name not in b.streams or sum(map(len, b.streams[name])) != len(b.positions):
            continue
        x = np.vstack(parts)[keep]
        y = np.vstack(b.streams[name])[match]
        if name == "COLOR_0" and x.shape[1] == 4 and y.shape[1] == 3:
            y = np.column_stack([y, np.ones(len(y))])
        if x.shape != y.shape:
            row["streamMaxDiff"][name] = f"shape {x.shape[1]} -> {y.shape[1]}"
            continue
        error = np.abs(x - y).max(1)
        every = np.vstack(b.streams[name])
        # Several vertices can share position, UV, and normal and differ only in this stream; take the best candidate.
        for i in np.where(error > 1e-6)[0]:
            error[i] = min(np.abs(every[c] - x[i]).max() for c in tree.query_ball_point(keys_a[i], 1e-5) or [match[i]])
        row["streamMaxDiff"][name] = float(error.max())
    row["customAttributes"] = [x for x in b.attributes if x.startswith("_")]
    row["droppedAttributes"] = [x for x in a.attributes if x not in b.attributes]
    # Pose test 1: the runtime plays the original GH3 clip (joint local TRS by name) on the round-trip rig.
    # Pose test 2: the round-trip GLB plays its own exported clip (Blender resamples it at the scene frame rate).
    a_times = a.glb.accessor(a.animations[0]["samplers"][0]["input"]).reshape(-1)
    original, exported = [], []
    for time in a_times[:: max(1, len(a_times) // 30)]:
        pose = a.sample(time)
        posed_a = a.skin(pose)
        original.append(
            np.linalg.norm(posed_a - b.skin({bi[n]: pose[ai[n]] for n in common})[match], axis=1).max() * 100
        )
        if b.animations:
            exported.append(np.linalg.norm(posed_a - b.skin(b.sample(time))[match], axis=1).max() * 100)
    row["poseOriginalClipCm"] = float(max(original))
    row["poseExportedClipCm"] = float(max(exported)) if exported else None
    if b.animations:
        times = b.glb.accessor(
            max(b.animations[0]["samplers"], key=lambda s: b.glb.data["accessors"][s["input"]]["count"])["input"]
        ).reshape(-1)
        row["exportedClip"] = {
            "channels": len(b.animations[0]["channels"]),
            "keys": len(times),
            "end": round(float(times[-1]), 4),
            "paths": sorted({c["target"]["path"] for c in b.animations[0]["channels"]}),
        }
    row["originalClip"] = {
        "channels": len(a.animations[0]["channels"]),
        "keys": len(a_times),
        "end": round(float(a_times[-1]), 4),
    }
    row["animations"] = [len(a.animations), len(b.animations)]
    row["images"] = [len(a.glb.data.get("images", [])), len(b.glb.data.get("images", []))]

    # Extras that hold glTF texture indices (gh3Shader.textures, gh3MaskTexture, gh3LayerTexture) must still point at the same image.
    def image_hash(rig, texture):
        if not isinstance(texture, int) or not 0 <= texture < len(rig.glb.data.get("textures", [])):
            return None
        return hashlib.sha1(rig.glb.image(texture).tobytes()).hexdigest()

    checked = wrong = 0
    bm = {m["name"]: m.get("extras", {}) for m in b.glb.data["materials"]}
    for m in a.glb.data["materials"]:
        x, y = m.get("extras", {}), bm.get(m["name"], {})
        if not y:
            continue
        pairs = list(zip(x.get("gh3Shader", {}).get("textures", []), y.get("gh3Shader", {}).get("textures", [])))
        pairs += [(x[k], y.get(k)) for k in ("gh3MaskTexture", "gh3LayerTexture") if k in x]
        for i, j in pairs:
            if i < 0:
                continue
            checked += 1
            wrong += image_hash(a, i) != image_hash(b, j)
    row["extraTextureRefsWrong"] = f"{wrong}/{checked}"
    row["extensionsUsed"] = b.glb.data.get("extensionsUsed", [])
    row["extraMeshNodes"] = [n.get("name") for n in b.glb.data["nodes"] if "mesh" in n and "skin" not in n]
    print(json.dumps(row))


if __name__ == "__main__":
    main(*sys.argv[1:3])
