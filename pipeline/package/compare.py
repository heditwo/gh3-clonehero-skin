"""Compare two venue or character packages (for example a package and its Blender round trip): files, JSON, and the
model GLB (stage.glb, character.glb).

The GLB is compared by meaning, not by bytes: nodes by name (world transform and extras), materials by name
(glTF fields with their defaults, extras with texture references resolved to image bytes), and mesh primitives by
node and material name (triangles in a canonical order, every vertex attribute). Skin weights compare by joint name
(the joint order is free), and inverse bind matrices by joint name. A node that only one GLB has is ignored when it
has the identity transform and no mesh (Blender adds the armature node above the root joint). Relative imports
only, so a Blender test can use it from the repository.

Usage: python -m pipeline.package.compare A B [--json REPORT]
"""

import argparse
import hashlib
import json
import math
import re
import sys
from pathlib import Path

import numpy as np

from .glb import Glb, decompose
from .validate import Report, Source

# Tolerances: node transforms from docs/PACKAGING-PLAN.md section 9.6; vertex data from the float32 and 8-bit storage.
# NORMAL (degrees): Blender stores custom normals in its own encoding (measured up to 0.8 degrees on the GH3 venues).
# SKIN: the weight of each joint (by name) at each corner; inverseBind: PACKAGING-PLAN 9.6.
TOLERANCE = {"position": 1e-4, "rotation": 0.01, "scale": 1e-4, "POSITION": 1e-4, "NORMAL": 1.0, "TEXCOORD": 1e-5,
             "COLOR": 2.5 / 255, "_GH3": 1e-5, "json": 1e-5, "SKIN": 1e-3, "inverseBind": 1e-4}
ADDON_JSON = ("placements.json", "cameras.json", "lighting.json")
MATERIAL_DEFAULTS = {"alphaMode": "OPAQUE", "doubleSided": False, "baseColorFactor": [1, 1, 1, 1], "metallicFactor": 1, "roughnessFactor": 1,
                     "emissiveFactor": [0, 0, 0]}


def json_differences(a, b, path="", out=None):
    """Every difference of two JSON values: (path, a, b). Numbers compare as numbers (1 == 1.0)."""
    out = [] if out is None else out
    if isinstance(a, dict) and isinstance(b, dict):
        for key in a:
            if key not in b: out.append((f"{path}.{key}", a[key], "<missing>"))
            else: json_differences(a[key], b[key], f"{path}.{key}", out)
        for key in b:
            if key not in a: out.append((f"{path}.{key}", "<missing>", b[key]))
    elif isinstance(a, list) and isinstance(b, list):
        if path.lower().endswith("rotation") and len(a) == len(b) == 4 and all(isinstance(v, (int, float)) for v in a + b) \
                and sum(abs(x + y) for x, y in zip(a, b)) < sum(abs(x - y) for x, y in zip(a, b)):
            b = [-y for y in b]  # q and -q are the same rotation.
        if len(a) != len(b): out.append((f"{path}[]", f"{len(a)} items", f"{len(b)} items"))
        for index, (x, y) in enumerate(zip(a, b)): json_differences(x, y, f"{path}[{index}]", out)
    elif isinstance(a, bool) or isinstance(b, bool) or not isinstance(a, (int, float)) or not isinstance(b, (int, float)):
        if a != b or type(a) is bool and type(b) is not bool or type(b) is bool and type(a) is not bool: out.append((path, a, b))
    elif a != b:
        out.append((path, a, b))
    return out


NAMED = ("shots", "snapshots", "waypoints", "beams", "moods", "colors", "notes", "groups", "band", "effects", "definitions")


def numbers_close(a, b, tolerance=TOLERANCE["json"]):
    return isinstance(a, (int, float)) and isinstance(b, (int, float)) and not isinstance(a, bool) and not isinstance(b, bool) and abs(a - b) <= tolerance


def pattern(path):
    """The path of a difference without names and indices, to group differences."""
    path = re.sub(r"\[\d+\]", "[*]", path)
    return re.sub(r"\.(" + "|".join(NAMED) + r")\.[^.\[]+", r".\1.*", path)


def summarize(differences, tolerance):
    """Group differences by pattern: count, numeric maximum, and examples."""
    groups = {}
    for path, a, b in differences:
        key = pattern(path)
        numeric = isinstance(a, (int, float)) and isinstance(b, (int, float)) and not isinstance(a, bool) and not isinstance(b, bool)
        kind = "missing in B" if b == "<missing>" else "added in B" if a == "<missing>" else "number" if numeric else "value"
        group = groups.setdefault(f"{key} ({kind})", {"count": 0, "max": 0.0, "examples": []})
        group["count"] += 1
        if numeric: group["max"] = max(group["max"], abs(a - b))
        if len(group["examples"]) < 3: group["examples"].append([path, a, b])
    for key, group in groups.items(): group["withinTolerance"] = key.endswith("(number)") and group["max"] <= tolerance
    return groups


def compare_json(a, b, tolerance=TOLERANCE["json"]):
    differences = json_differences(a, b)
    numeric = [abs(x - y) for _, x, y in differences if isinstance(x, (int, float)) and isinstance(y, (int, float))
               and not isinstance(x, bool) and not isinstance(y, bool)]
    return {"equal": not differences, "differences": len(differences), "maxNumeric": max(numeric, default=0.0),
            "onlyNumericWithinTolerance": bool(differences) and len(numeric) == len(differences) and max(numeric) <= tolerance,
            "groups": summarize(differences, tolerance)}


class Images:
    """Image bytes digests of a package GLB and its package files, for texture references."""

    def __init__(self, glb, source):
        self.glb, self.source = glb, source
        self.by_name = {}
        for index, image in enumerate(glb.list("images")):
            self.by_name[image.get("name", f"#{index}")] = index

    def digest_of_index(self, index):
        return hashlib.sha256(self.glb.view(self.glb.list("images")[index]["bufferView"])).hexdigest()[:16]

    def reference(self, ref):
        if not isinstance(ref, str) or not ref: return ref
        if ref.startswith("#"):
            index = self.by_name.get(ref[1:])
            return f"image:{self.digest_of_index(index)}" if index is not None else f"missing:{ref}"
        if ref in self.source.set: return f"image:{hashlib.sha256(self.source.read(ref)).hexdigest()[:16]}"
        return f"missing:{ref}"

    def texture(self, info):
        if not info: return None
        texture = self.glb.list("textures")[info["index"]]
        return {"image": f"image:{self.digest_of_index(texture['source'])}", "texCoord": info.get("texCoord", 0)}


def resolved_extras(extras, images):
    extras = json.loads(json.dumps(extras))
    for key in ("gh3MaskTexture", "gh3LayerTexture"):
        if key in extras: extras[key] = images.reference(extras[key])
    toon = extras.get("gh3Toon")
    if isinstance(toon, dict):
        for key in ("shadeTexture", "shadeMaskTexture", "sphereTexture"):
            if key in toon: toon[key] = images.reference(toon[key])
    shader = extras.get("gh3Shader")
    if isinstance(shader, dict) and isinstance(shader.get("textures"), list):
        shader["textures"] = [images.reference(ref) for ref in shader["textures"]]
    return extras


def material_view(material, images):
    pbr = material.get("pbrMetallicRoughness", {})
    view = {key: material.get(key, MATERIAL_DEFAULTS[key]) for key in ("alphaMode", "doubleSided", "emissiveFactor")}
    if view["alphaMode"] == "MASK": view["alphaCutoff"] = material.get("alphaCutoff", 0.5)
    for key in ("baseColorFactor", "metallicFactor", "roughnessFactor"): view[key] = pbr.get(key, MATERIAL_DEFAULTS[key])
    view["baseColorTexture"] = images.texture(pbr.get("baseColorTexture"))
    view["occlusionTexture"] = images.texture(material.get("occlusionTexture"))
    if material.get("occlusionTexture", {}).get("strength", 1) != 1: view["occlusionStrength"] = material["occlusionTexture"]["strength"]
    view["extensions"] = sorted(material.get("extensions", {}))
    view["extras"] = resolved_extras(material.get("extras", {}), images)
    return view


def triangles(glb, primitives, skin=None):
    """(attribute name to per-corner array (T, 3, k)) of triangle-list primitives (Blender exports one primitive for
    each material, so the primitives of one material in one mesh are compared together). skin: (joint names of the
    GLB skin, column of each joint name); JOINTS_0 and WEIGHTS_0 then become SKIN, the weight of each joint name."""
    parts = []
    for primitive in primitives:
        indices = glb.accessor(primitive["indices"]).reshape(-1) if "indices" in primitive else np.arange(glb.accessor(primitive["attributes"]["POSITION"]).shape[0])
        corners = indices.reshape(-1, 3)
        attributes = dict(primitive["attributes"])
        part = {}
        if skin is not None and "JOINTS_0" in attributes and "WEIGHTS_0" in attributes:
            names, columns = skin
            joints = glb.accessor(attributes.pop("JOINTS_0")).astype(np.int64)
            weights = glb.accessor(attributes.pop("WEIGHTS_0")).astype(np.float64)
            dense = np.zeros((len(joints), len(columns)))
            mapped = np.array([columns[name] for name in names])[joints]
            np.add.at(dense, (np.repeat(np.arange(len(joints)), joints.shape[1]), mapped.reshape(-1)), weights.reshape(-1))
            part["SKIN"] = dense[corners]
        part.update({name: glb.accessor(index).astype(np.float64)[corners] for name, index in attributes.items()})
        parts.append(part)
    names = set.intersection(*(set(part) for part in parts))
    return {name: np.concatenate([part[name] for part in parts]) for name in names}


def canonical(data):
    """Rotate each triangle to start at its smallest corner (keeps the winding) and sort the triangles."""
    position = data["POSITION"]
    grid = np.round(position / 1e-3).astype(np.int64)
    # Smallest corner by (x, y, z) grid position.
    first = np.zeros(len(position), dtype=np.int64)
    for corner in (1, 2):
        a, b = grid[np.arange(len(grid)), first], grid[:, corner]
        smaller = (b[:, 0] < a[:, 0]) | ((b[:, 0] == a[:, 0]) & ((b[:, 1] < a[:, 1]) | ((b[:, 1] == a[:, 1]) & (b[:, 2] < a[:, 2]))))
        first = np.where(smaller, corner, first)
    rotation = (first[:, None] + np.arange(3)[None, :]) % 3
    rows = np.arange(len(position))[:, None]
    rotated = {name: values[rows, rotation] for name, values in data.items()}
    grid = grid[rows, rotation]
    columns = [grid[:, c, axis] for c in (2, 1, 0) for axis in (2, 1, 0)]
    if "NORMAL" in rotated: columns = [np.round(rotated["NORMAL"][:, 0, axis] * 100).astype(np.int64) for axis in (2, 1, 0)] + columns
    if "TEXCOORD_0" in rotated: columns = [np.round(rotated["TEXCOORD_0"][:, 0, axis] * 1000).astype(np.int64) for axis in (1, 0)] + columns
    sort = np.lexsort(columns)
    return {name: values[sort] for name, values in rotated.items()}


def attribute_tolerance(name):
    for prefix in ("TEXCOORD", "COLOR", "_GH3"):
        if name.startswith(prefix): return TOLERANCE[prefix]
    return TOLERANCE.get(name, 1e-5)


def attribute_error(name, a, b, visible=None):
    if name == "NORMAL":
        # Normals of triangles without area do not show. Blender gives them a default normal.
        if visible is not None: a, b = a[visible], b[visible]
        a = a / np.maximum(np.linalg.norm(a, axis=-1, keepdims=True), 1e-12)
        b = b / np.maximum(np.linalg.norm(b, axis=-1, keepdims=True), 1e-12)
        return float(np.degrees(np.arccos(np.clip(np.sum(a * b, axis=-1), -1, 1))).max(initial=0))
    if a.shape[-1] != b.shape[-1]:
        width = min(a.shape[-1], b.shape[-1])
        extra = a[..., width:] if a.shape[-1] > width else b[..., width:]
        # A missing alpha channel is 1.
        error = float(np.abs(extra - 1).max(initial=0)) if name.startswith("COLOR") else math.inf
        return max(error, float(np.abs(a[..., :width] - b[..., :width]).max(initial=0)))
    return float(np.abs(a - b).max(initial=0))


def compare_primitive(glb_a, prim_a, glb_b, prim_b, skins=(None, None)):
    a, b = triangles(glb_a, prim_a, skins[0]), triangles(glb_b, prim_b, skins[1])
    result = {"triangles": [len(a["POSITION"]), len(b["POSITION"])], "attributes": {}, "onlyA": sorted(set(a) - set(b)), "onlyB": {}}
    for name in sorted(set(b) - set(a)):
        result["onlyB"][name] = "all zero" if not np.any(b[name]) else "data"
    if len(a["POSITION"]) != len(b["POSITION"]): return result
    a, b = canonical(a), canonical(b)
    corners = a["POSITION"]
    visible = np.linalg.norm(np.cross(corners[:, 1] - corners[:, 0], corners[:, 2] - corners[:, 0]), axis=-1) > 1e-12
    result["withoutArea"] = int((~visible).sum())
    for name in sorted(set(a) & set(b)):
        result["attributes"][name] = attribute_error(name, a[name], b[name], visible)
    if "NORMAL" in a and "NORMAL" in b:
        x, y = (n[visible] / np.maximum(np.linalg.norm(n[visible], axis=-1, keepdims=True), 1e-12) for n in (a["NORMAL"], b["NORMAL"]))
        result["normalCorners"] = [int((np.degrees(np.arccos(np.clip(np.sum(x * y, axis=-1), -1, 1))) > 1).sum()), int(x.shape[0] * 3)]
    return result


def node_names(glb):
    return {node.get("name"): index for index, node in enumerate(glb.list("nodes"))}


def skin_names(glb):
    skins = glb.list("skins")
    return [glb.list("nodes")[j].get("name", "") for j in skins[0]["joints"]] if skins else None


def inverse_binds(glb):
    skin = glb.list("skins")[0]
    matrices = glb.accessor(skin["inverseBindMatrices"]).reshape(-1, 4, 4).transpose(0, 2, 1)
    return dict(zip(skin_names(glb), matrices))


def compare_glb(glb_a, source_a, glb_b, source_b):
    images_a, images_b = Images(glb_a, source_a), Images(glb_b, source_b)
    report = {"nodes": {}, "materials": {}, "primitives": {}, "max": {}, "failures": []}
    nodes_a, nodes_b = node_names(glb_a), node_names(glb_b)
    parents_a, parents_b, cache_a, cache_b = glb_a.parents(), glb_b.parents(), {}, {}

    def identity_only(glb, nodes, parents, cache, names):
        return sorted((n for n in names if "mesh" not in glb.list("nodes")[nodes[n]] and np.abs(glb.world(nodes[n], parents, cache) - np.eye(4)).max() <= TOLERANCE["position"]), key=str)

    only_a, only_b = set(nodes_a) - set(nodes_b), set(nodes_b) - set(nodes_a)
    ignored = identity_only(glb_a, nodes_a, parents_a, cache_a, only_a) + identity_only(glb_b, nodes_b, parents_b, cache_b, only_b)
    report["nodes"] = {"countA": len(nodes_a), "countB": len(nodes_b), "onlyA": sorted(only_a - set(ignored), key=str),
                       "onlyB": sorted(only_b - set(ignored), key=str), "identityOnly": ignored, "extrasDifferent": []}
    joints_a, joints_b = skin_names(glb_a), skin_names(glb_b)
    skins = (None, None)
    if joints_a is not None and joints_b is not None:
        columns = {name: index for index, name in enumerate(sorted(set(joints_a) | set(joints_b)))}
        skins = ((joints_a, columns), (joints_b, columns))
        if set(joints_a) != set(joints_b): report["failures"].append(f"skin joints differ: {sorted(set(joints_a) ^ set(joints_b))[:5]}")
        binds_a, binds_b = inverse_binds(glb_a), inverse_binds(glb_b)
        report["max"]["inverseBind"] = max((float(np.abs(binds_a[n] - binds_b[n]).max()) for n in set(binds_a) & set(binds_b)), default=0.0)
    worst = {"position": 0.0, "rotation": 0.0, "scale": 0.0}
    node_differences = []
    for name in sorted(set(nodes_a) & set(nodes_b), key=str):
        ia, ib = nodes_a[name], nodes_b[name]
        ta, ra, sa = decompose(glb_a.world(ia, parents_a, cache_a))
        tb, rb, sb = decompose(glb_b.world(ib, parents_b, cache_b))
        worst["position"] = max(worst["position"], float(np.abs(ta - tb).max()))
        worst["rotation"] = max(worst["rotation"], math.degrees(4 * math.asin(min(1.0, np.linalg.norm(ra - rb if np.dot(ra, rb) >= 0 else ra + rb) / 2))))
        worst["scale"] = max(worst["scale"], float(np.abs(sa - sb).max()))
        node_a, node_b = glb_a.list("nodes")[ia], glb_b.list("nodes")[ib]
        differences = json_differences(node_a.get("extras", {}), node_b.get("extras", {}))
        if differences:
            report["nodes"]["extrasDifferent"].append(name)
            node_differences += differences
        if ("mesh" in node_a) != ("mesh" in node_b): report["failures"].append(f"node {name}: mesh only in one GLB"); continue
        if "mesh" not in node_a: continue
        material_names = lambda glb, prim: glb.list("materials")[prim["material"]].get("name") if "material" in prim else None
        prims_a, prims_b = {}, {}
        for p in glb_a.list("meshes")[node_a["mesh"]]["primitives"]: prims_a.setdefault(material_names(glb_a, p), []).append(p)
        for p in glb_b.list("meshes")[node_b["mesh"]]["primitives"]: prims_b.setdefault(material_names(glb_b, p), []).append(p)
        if set(prims_a) != set(prims_b): report["failures"].append(f"node {name}: primitive materials {sorted(prims_a, key=str)} / {sorted(prims_b, key=str)}")
        for material in set(prims_a) & set(prims_b):
            report["primitives"][f"{name}/{material}"] = compare_primitive(glb_a, prims_a[material], glb_b, prims_b[material], skins)
    report["nodeTransform"] = worst
    report["nodes"]["extrasGroups"] = summarize(node_differences, TOLERANCE["json"])
    for key, limit in (("position", TOLERANCE["position"]), ("rotation", TOLERANCE["rotation"]), ("scale", TOLERANCE["scale"])):
        if worst[key] > limit: report["failures"].append(f"node {key} difference {worst[key]:.3g} > {limit}")
    if report["nodes"]["onlyA"] or report["nodes"]["onlyB"]: report["failures"].append("node names differ")
    if report["nodes"]["extrasDifferent"]: report["failures"].append(f"{len(report['nodes']['extrasDifferent'])} nodes have different extras")

    materials_a = {m.get("name"): m for m in glb_a.list("materials")}
    materials_b = {m.get("name"): m for m in glb_b.list("materials")}
    report["materials"] = {"countA": len(materials_a), "countB": len(materials_b), "onlyA": sorted(set(materials_a) - set(materials_b), key=str),
                           "onlyB": sorted(set(materials_b) - set(materials_a), key=str), "different": {}}
    for name in sorted(set(materials_a) & set(materials_b), key=str):
        view_a, view_b = material_view(materials_a[name], images_a), material_view(materials_b[name], images_b)
        differences = [d for d in json_differences(view_a, view_b) if not numbers_close(d[1], d[2])]
        if differences: report["materials"]["different"][name] = [[p, x, y] for p, x, y in differences[:6]]
    if report["materials"]["onlyA"] or report["materials"]["different"]: report["failures"].append("materials differ")

    counts = {"primitives": len(report["primitives"]), "triangleCountDifferent": [], "attributesOnlyA": {}, "attributesOnlyB": {},
              "trianglesWithoutArea": sum(p.get("withoutArea", 0) for p in report["primitives"].values()),
              "normalCornersOver1Degree": [sum(p.get("normalCorners", [0, 0])[i] for p in report["primitives"].values()) for i in (0, 1)]}
    for key, primitive in report["primitives"].items():
        if primitive["triangles"][0] != primitive["triangles"][1]: counts["triangleCountDifferent"].append([key, *primitive["triangles"]])
        for name in primitive["onlyA"]: counts["attributesOnlyA"][name] = counts["attributesOnlyA"].get(name, 0) + 1
        for name, kind in primitive["onlyB"].items():
            label = f"{name} ({kind})"
            counts["attributesOnlyB"][label] = counts["attributesOnlyB"].get(label, 0) + 1
        for name, error in primitive["attributes"].items():
            report["max"][name] = max(report["max"].get(name, 0.0), error)
    report["meshes"] = counts
    for name, error in report["max"].items():
        if error > attribute_tolerance(name): report["failures"].append(f"{name} difference {error:.3g} > {attribute_tolerance(name)}")
    if counts["triangleCountDifferent"]: report["failures"].append(f"{len(counts['triangleCountDifferent'])} primitives have a different triangle count")
    if counts["attributesOnlyA"]: report["failures"].append(f"attributes lost: {counts['attributesOnlyA']}")
    if any(not label.endswith("(all zero)") for label in counts["attributesOnlyB"]): report["failures"].append(f"attributes added: {counts['attributesOnlyB']}")
    del report["primitives"]
    images = {"countA": len(glb_a.list("images")), "countB": len(glb_b.list("images"))}
    digests_a = {images_a.digest_of_index(i) for i in range(images["countA"])}
    digests_b = {images_b.digest_of_index(i) for i in range(images["countB"])}
    files_b = {hashlib.sha256(source_b.read(n)).hexdigest()[:16] for n in source_b.names if n.startswith("textures/")}
    images["missingInB"] = len(digests_a - digests_b - files_b)
    images["movedToFiles"] = len((digests_a - digests_b) & files_b)
    report["images"] = images
    return report


def compare_packages(path_a, path_b):
    report_a, report_b = Report(), Report()
    a, b = Source(path_a, report_a), Source(path_b, report_b)
    manifest_a, manifest_b = json.loads(a.read("manifest.json")), json.loads(b.read("manifest.json"))
    result = {"a": str(path_a), "b": str(path_b), "files": {"onlyA": sorted(a.set - b.set), "onlyB": sorted(b.set - a.set)}, "json": {}, "binary": {}}
    strip = lambda manifest: {k: v for k, v in manifest.items() if k not in ("files", "preview")}
    result["manifest"] = compare_json(strip(manifest_a), strip(manifest_b))
    model_a, model_b = manifest_a["entry"]["model"], manifest_b["entry"]["model"]
    for name in sorted(a.set & b.set):
        if name in ("manifest.json", model_a): continue
        data_a, data_b = a.read(name), b.read(name)
        if name.endswith(".json"):
            result["json"][name] = compare_json(json.loads(data_a), json.loads(data_b))
            result["json"][name]["bytesEqual"] = data_a == data_b
        else:
            result["binary"][name] = "equal" if data_a == data_b else "different"
    result["stage"] = compare_glb(Glb(a.read(model_a)), a, Glb(b.read(model_b)), b)
    failures = [f"{model_a}: {f}" for f in result["stage"]["failures"]]
    failures += [f"{name}: {r['differences']} differences" for name, r in result["json"].items() if not r["equal"]]
    failures += [f"{name}: bytes differ" for name, value in result["binary"].items() if value != "equal"]
    if not result["manifest"]["equal"]: failures.append("manifest.json differs")
    failures += [f"only in A: {name}" for name in result["files"]["onlyA"]]
    result["failures"] = failures
    return result


def main(argv=None):
    parser = argparse.ArgumentParser(prog="python -m pipeline.package.compare", description=__doc__.splitlines()[0])
    parser.add_argument("a", type=Path)
    parser.add_argument("b", type=Path)
    parser.add_argument("--json", type=Path, help="write the full report")
    args = parser.parse_args(argv)
    report = compare_packages(args.a, args.b)
    if args.json: args.json.write_text(json.dumps(report, indent=2) + "\n")
    print(f"{args.a} -> {args.b}: {'same' if not report['failures'] else 'DIFFERENT'}")
    for failure in report["failures"]: print(f"  {failure}")
    stage = report["stage"]
    print(f"  nodes {stage['nodes']['countA']}/{stage['nodes']['countB']}, transform {stage['nodeTransform']}")
    print(f"  attributes max: { {k: float(f'{v:.3g}') for k, v in stage['max'].items()} }")
    return 1 if report["failures"] else 0


if __name__ == "__main__":
    sys.exit(main())
