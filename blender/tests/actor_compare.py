"""Compare the actor GLBs of two venue packages (the source and an export of the Blender actor models), and compile the
export with the pipeline venue compiler (compile_stage, which runs compile_actor for each actor).

Usage: python blender/tests/actor_compare.py SOURCE.chstage EXPORT.chstage [...]   (pairs; prints one JSON line each)

For each actor model: the meshes, the materials, and the joint world transforms in the bind pose (pipeline.package.compare,
without JOINTS_0 and WEIGHTS_0: Blender writes the joints in another order); the triangles by their corner positions; the inverse bind matrices by joint name;
the skinned vertices posed in the bind pose and in the first key of each clip (each exported vertex against the source
vertex at the same bind position, rounded to 0.01 mm); and each clip, sampled at the source key times: joint translation (m) and rotation (degrees). The
Blender export adds the 'Armature' node above the root joint (identity).
"""

import json
import sys
import tempfile
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
from pipeline.package import compare, roundtrip  # noqa: E402
from pipeline.package.source import Report, Source  # noqa: E402


def accessor(glb, index):
    return np.asarray(roundtrip.raw_accessor(glb, index), dtype=float)


def sample(times, values, at, rotation):
    """Values of a LINEAR channel at the times 'at' (rotations: normalized linear interpolation, shortest path)."""
    index = np.clip(np.searchsorted(times, at, side="right") - 1, 0, len(times) - 1)
    following = np.minimum(index + 1, len(times) - 1)
    span = np.where(following > index, times[following] - times[index], 1)
    weight = np.clip((at - times[index]) / span, 0, 1)[:, None]
    a, b = values[index], values[following]
    if rotation:
        b = np.where((np.sum(a * b, axis=1) < 0)[:, None], -b, b)
        mixed = a * (1 - weight) + b * weight
        return mixed / np.linalg.norm(mixed, axis=1)[:, None]
    return a * (1 - weight) + b * weight


def channels(glb):
    """{clip: {(joint name, path): (times, values)}}."""
    nodes = glb.list("nodes")
    result = {}
    for animation in glb.list("animations"):
        clip = result.setdefault(animation.get("name", ""), {})
        for channel in animation["channels"]:
            sampler = animation["samplers"][channel["sampler"]]
            name = nodes[channel["target"]["node"]].get("name")
            times = accessor(glb, sampler["input"]).reshape(-1)
            values = accessor(glb, sampler["output"])
            clip[(name, channel["target"]["path"])] = (times, values.reshape(len(times), -1))
    return result


def rest(glb):
    """{joint name: (translation, rotation)} of the skin joints."""
    nodes = glb.list("nodes")
    return {
        nodes[j].get("name"): (
            np.asarray(nodes[j].get("translation", [0, 0, 0]), float),
            np.asarray(nodes[j].get("rotation", [0, 0, 0, 1]), float),
        )
        for j in glb.json["skins"][0]["joints"]
    }


def angle(a, b):
    dot = np.abs(np.sum(a * b, axis=-1)) / (np.linalg.norm(a, axis=-1) * np.linalg.norm(b, axis=-1))
    return np.degrees(2 * np.arccos(np.clip(dot, -1, 1)))


def local_pose(glb, clip):
    """Local matrices of all nodes in the first key of a clip (None: the rest pose)."""
    nodes = glb.list("nodes")
    trs = [
        [
            np.asarray(n.get("translation", [0, 0, 0]), float),
            np.asarray(n.get("rotation", [0, 0, 0, 1]), float),
            np.asarray(n.get("scale", [1, 1, 1]), float),
        ]
        for n in nodes
    ]
    if clip is not None:
        for channel in clip["channels"]:
            sampler = clip["samplers"][channel["sampler"]]
            path = channel["target"]["path"]
            if path in ("translation", "rotation", "scale"):
                trs[channel["target"]["node"]][("translation", "rotation", "scale").index(path)] = accessor(
                    glb, sampler["output"]
                )[0]
    result = []
    for node, (t, r, s) in zip(nodes, trs):
        if "matrix" in node:
            result.append(np.asarray(node["matrix"], float).reshape(4, 4).T)
            continue
        x, y, z, w = r / np.linalg.norm(r)
        m = np.eye(4)
        m[:3, :3] = (
            np.array(
                [
                    [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                    [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                    [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
                ]
            )
            * s
        )
        m[:3, 3] = t
        result.append(m)
    return result


def posed(glb, clip):
    """{rounded bind position: posed position} of the skinned vertices in the first key of a clip."""
    nodes = glb.list("nodes")
    local = local_pose(glb, clip)
    parent = {c: i for i, n in enumerate(nodes) for c in n.get("children", [])}
    world = {}

    def world_of(i):
        if i not in world:
            world[i] = (world_of(parent[i]) if i in parent else np.eye(4)) @ local[i]
        return world[i]

    skin = glb.json["skins"][0]
    matrices = np.array([world_of(j) for j in skin["joints"]]) @ accessor(glb, skin["inverseBindMatrices"]).reshape(
        -1, 4, 4
    ).transpose(0, 2, 1)
    result = {}
    for node in nodes:
        if "mesh" not in node:
            continue
        for primitive in glb.list("meshes")[node["mesh"]]["primitives"]:
            attributes = primitive["attributes"]
            positions = accessor(glb, attributes["POSITION"])
            joints = accessor(glb, attributes["JOINTS_0"]).astype(int)
            weights = accessor(glb, attributes["WEIGHTS_0"])
            if glb.list("accessors")[attributes["WEIGHTS_0"]].get("normalized"):
                weights = (
                    weights / {5121: 255, 5123: 65535}[glb.list("accessors")[attributes["WEIGHTS_0"]]["componentType"]]
                )
            homogeneous = np.c_[positions, np.ones(len(positions))]
            moved = np.einsum("vk,vkij,vj->vi", weights, matrices[joints], homogeneous)[:, :3]
            for bind, value in zip(np.round(positions, 5), moved):
                result.setdefault(tuple(bind), value)
    return result


def triangle_keys(glb, step=1e-4, offset=0.0):
    """(key, corner normals) of each triangle: the corner positions on a 0.1 mm grid, rotated to the smallest corner
    (so the winding stays)."""
    result = []
    for node in glb.list("nodes"):
        if "mesh" not in node:
            continue
        data = compare.triangles(glb, glb.list("meshes")[node["mesh"]]["primitives"])
        for corners, normals in zip(np.round(data["POSITION"] / step + offset).astype(np.int64), data["NORMAL"]):
            rows = [tuple(r) for r in corners]
            k = rows.index(min(rows))
            result.append((tuple(rows[k:] + rows[:k]), np.roll(normals, -k, axis=0)))
    return result


def compare_triangles(a, b):
    """Triangles of the export without a source triangle at the same corners (0.1 mm; a corner near a grid boundary can
    round to the other cell, so a second grid shifted by half a step also counts), and the largest corner normal
    difference (degrees) of the triangles with area that occur one time in both."""
    ka, kb = triangle_keys(a), triangle_keys(b)
    sa, sb = triangle_keys(a, offset=0.5), triangle_keys(b, offset=0.5)
    first, second = {k for k, _ in ka}, {k for k, _ in sa}
    unmatched = sum(1 for (k1, _), (k2, _) in zip(kb, sb) if k1 not in first and k2 not in second)
    groups_a, groups_b = {}, {}
    for key, normals in ka:
        groups_a.setdefault(key, []).append(normals)
    for key, normals in kb:
        groups_b.setdefault(key, []).append(normals)
    worst = 0.0
    for key, normals in groups_a.items():
        # A triangle without area has no normal of its own (Blender gives it a default normal; compare.py skips it too).
        corners = np.asarray(key, dtype=float)
        if np.linalg.norm(np.cross(corners[1] - corners[0], corners[2] - corners[0])) == 0:
            continue
        if len(normals) == 1 and len(groups_b.get(key, [])) == 1:
            x, y = normals[0], groups_b[key][0]
            cos = np.sum(x * y, 1) / np.maximum(np.linalg.norm(x, axis=1) * np.linalg.norm(y, axis=1), 1e-12)
            worst = max(worst, float(np.degrees(np.arccos(np.clip(cos, -1, 1))).max()))
    return {"countA": len(ka), "countB": len(kb), "unmatchedB": unmatched, "normal": worst}


def compare_actor(data_a, source_a, data_b, source_b):
    a, b = compare.Glb(data_a), compare.Glb(data_b)
    report = {}
    glb = compare.compare_glb(a, source_a, b, source_b)
    worst = 0.0
    unmatched = matched = 0
    animations_a = {x.get("name"): x for x in a.list("animations")}
    for x in [None, *b.list("animations")]:
        pose_a, pose_b = posed(a, animations_a.get(x.get("name")) if x else None), posed(b, x)
        for key, value in pose_b.items():
            if key in pose_a:
                worst = max(worst, float(np.abs(pose_a[key] - value).max()))
                matched += 1
            else:
                unmatched += 1
    # A vertex at a rounding boundary of 0.01 mm has no partner; the count shows how many vertices the check covers.
    report["posedVertices"] = {"max": worst, "matched": matched, "unmatched": unmatched}
    # compare_glb pairs triangles by a 1 mm grid sort; coincident triangles (GH3 two-sided cards) can pair wrongly there.
    report["triangles"] = compare_triangles(a, b)
    report["meshes"] = {
        "max": {k: v for k, v in glb["max"].items() if k not in ("JOINTS_0", "WEIGHTS_0")},
        "triangles": glb["meshes"]["triangleCountDifferent"],
        "materials": glb["materials"]["different"],
        "materialsOnlyA": glb["materials"]["onlyA"],
        "nodesOnlyB": glb["nodes"]["onlyB"],
        "nodesOnlyA": glb["nodes"]["onlyA"],
        "jointWorld": glb["nodeTransform"],
    }
    rest_a, rest_b = rest(a), rest(b)
    report["joints"] = {
        "onlyA": sorted(set(rest_a) - set(rest_b)),
        "onlyB": sorted(set(rest_b) - set(rest_a)),
        "translation": max(
            (float(np.abs(rest_a[n][0] - rest_b[n][0]).max()) for n in rest_a if n in rest_b), default=0
        ),
        "rotation": max((float(angle(rest_a[n][1], rest_b[n][1])) for n in rest_a if n in rest_b), default=0),
    }

    def ibm(glb):
        return dict(
            zip(
                [glb.list("nodes")[j].get("name") for j in glb.json["skins"][0]["joints"]],
                accessor(glb, glb.json["skins"][0]["inverseBindMatrices"]).reshape(-1, 4, 4),
            )
        )

    ibm_a, ibm_b = ibm(a), ibm(b)
    report["inverseBind"] = max((float(np.abs(ibm_a[n] - ibm_b[n]).max()) for n in ibm_a if n in ibm_b), default=0)
    clips_a, clips_b = channels(a), channels(b)
    report["clips"] = {
        "countA": len(clips_a),
        "countB": len(clips_b),
        "onlyA": sorted(set(clips_a) - set(clips_b)),
        "onlyB": sorted(set(clips_b) - set(clips_a)),
        "translation": 0.0,
        "rotation": 0.0,
        "missingChannels": 0,
        "scaleChannelsB": 0,
    }
    for clip, tracks in clips_a.items():
        other = clips_b.get(clip, {})
        report["clips"]["scaleChannelsB"] += sum(1 for (_, path) in other if path == "scale")
        for (joint, path), (times, values) in tracks.items():
            if (joint, path) not in other:
                # A channel that Blender does not write keeps the rest value: count it when it moves the joint.
                fixed = rest_b.get(joint, (np.zeros(3), np.array([0, 0, 0, 1.0])))[0 if path == "translation" else 1]
                moved = np.abs(values - fixed).max() if path == "translation" else angle(values, fixed[None]).max()
                if moved > (1e-5 if path == "translation" else 1e-3):
                    report["clips"]["missingChannels"] += 1
                continue
            times_b, values_b = other[(joint, path)]
            got = sample(times_b, values_b, times, path == "rotation")
            if path == "translation":
                report["clips"]["translation"] = max(report["clips"]["translation"], float(np.abs(got - values).max()))
            elif path == "rotation":
                report["clips"]["rotation"] = max(report["clips"]["rotation"], float(angle(got, values).max()))
    return report


def compile_check(package):
    """Compile the package with the pipeline venue compiler (compile_stage, compile_actor for each actor)."""
    expected = ROOT / "assets" / "stages" / json.loads(Source(package, Report()).read("manifest.json"))["id"]
    try:
        with tempfile.TemporaryDirectory():
            result = roundtrip.venue_round_trip(package, expected)
    except (ValueError, OSError) as error:
        return {"compiled": False, "error": str(error)}
    actors = [d for d in result["different"] if d.startswith("actors/")]
    return {
        "compiled": True,
        "differentActorFiles": len(actors),
        "differentOther": [d for d in result["different"] if not d.startswith("actors/")][:8],
        "missing": result["missing"][:8],
        "extra": result["extra"][:8],
    }


def main(argv):
    for source_path, export_path in zip(argv[::2], argv[1::2]):
        report_a, report_b = Report(), Report()
        a, b = Source(Path(source_path), report_a), Source(Path(export_path), report_b)
        actors = json.loads(a.read(json.loads(a.read("manifest.json"))["entry"]["actors"]))["actors"]
        result = {"package": Path(source_path).name, "actors": {}}
        for name, actor in actors.items():
            result["actors"][name] = compare_actor(a.read(actor["model"]), a, b.read(actor["model"]), b)
        result["compile"] = compile_check(Path(export_path))
        print(json.dumps(result, default=lambda v: float(v) if isinstance(v, np.floating) else str(v)))


if __name__ == "__main__":
    main(sys.argv[1:])
