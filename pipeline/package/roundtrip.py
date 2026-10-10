"""Round-trip check: convert packages back to the runtime inputs that the mod reads today and compare them.

- Venue: package -> source pack (stage-pack.json, GLBs, sidecars) -> `compile_stage` -> compare with the compiled pack.
- Textures: the packages hold GH3TEX02 textures (pipeline/package/texture.py). A texture is equal when the package
  texture is the encoder output of the PNG image of the compiled pack (compared as decoded pixels for venues).
- Library: package -> GH3BND02, GH3ANM01, stage.json, and textures of each model; GH3CLP01 clips; gzip PCM; GH3VID01
  movies; library JSON files -> compare with the current library folder. Clips must stay within the q48 error bound.

Usage: python -m pipeline.package.roundtrip [--packages DIR] [--stages DIR] [--library DIR] [--events DIR]
                                            [--venue ID ...] [--no-venues] [--no-library] [--json OUT]
"""

import argparse
import gzip
import json
import math
import tempfile
from collections import Counter
from io import BytesIO
from pathlib import Path

import numpy as np

from PIL import Image

from pipeline.package import q48, runtime, texture
from pipeline.package.build import EFFECT_FILES, EVENTS, OUTPUT, encode_texture, json_bytes
from pipeline.package.glb import COLUMNS, COMPONENTS, Glb
from pipeline.package.validate import Report, Source
from pipeline.paths import LIBRARY, STAGES
from pipeline.stagepack.gltf import SKINNED_GH3_VERTEX
from pipeline.stagepack.pack import compile_stage, write_json

STAGE_KEYS = ("version", "kind", "id", "name", "library", "model", "band", "waypoints", "cameras", "lighting", "effects", "actors", "scripts", "crowd")
# stage.json material fields that the runtime does not read; the GLB keeps the same data in gh3Shader.
UNREAD_MATERIAL_FIELDS = ("textures", "template", "geometryCutout")
ROTATION_BOUND = 0.0075
FLIP_POSITION = np.array([1, 1, -1])
FLIP_ROTATION = np.array([-1, -1, 1, 1])


def open_package(path):
    report = Report()
    source = Source(path, report)
    if report.errors: raise ValueError(f"{path}: {report.errors[0]}")
    return source


# The converter's texture cache (main() uses the one of --packages).
TEXTURE_CACHE = [OUTPUT/".texture-cache"]


def encoded(png):
    """GH3TEX02 bytes of a PNG image (the converter's encoder and texture cache)."""
    return encode_texture((png, False, str(TEXTURE_CACHE[0])))


def without_hashes(pack):
    """pack.json without the hashes of its sources and files: the package GLBs and the textures that they give are
    other bytes than the source pack's (GH3TEX02 images)."""
    return {**pack, "sources": sorted(pack.get("sources", {})), "files": sorted(pack.get("files", {}))}


def compare_trees(actual, expected):
    """Differences between two compiled stage folders: missing or extra files, JSON or byte differences. A texture PNG
    from a package is equal when its pixels are the decoded encoder output of the expected PNG."""
    a = {p.relative_to(actual).as_posix() for p in Path(actual).rglob("*") if p.is_file()}
    e = {p.relative_to(expected).as_posix() for p in Path(expected).rglob("*") if p.is_file()}
    result = {"files": len(e), "byteEqual": 0, "jsonEqual": 0, "packJsonEqual": 0, "textureEncoderEqual": 0, "missing": sorted(e - a), "extra": sorted(a - e), "different": []}
    for name in sorted(a & e):
        left, right = (Path(actual)/name).read_bytes(), (Path(expected)/name).read_bytes()
        if left == right: result["byteEqual"] += 1
        elif name.endswith(".json") and json.loads(left) == json.loads(right): result["jsonEqual"] += 1
        elif Path(name).name == "pack.json" and without_hashes(json.loads(left)) == without_hashes(json.loads(right)): result["packJsonEqual"] += 1
        elif "/textures/" in "/" + name and name.endswith(".png") and \
                np.array_equal(np.asarray(Image.open(BytesIO(left)).convert("RGBA")), texture.decode(encoded(right))):
            result["textureEncoderEqual"] += 1
        else: result["different"].append(name)
    return result


# Venues.
def venue_round_trip(package, expected):
    source = open_package(package)
    manifest = json.loads(source.read("manifest.json"))
    entry = manifest["entry"]
    placements = json.loads(source.read(entry["placements"]))
    library = next(r["id"] for r in manifest.get("requires", []) if r["id"].startswith("gh3-band-"))
    stage = {"version": 1, "kind": "stage", "id": manifest["id"], "name": manifest["name"], "library": library, "model": entry["model"],
             "cameras": entry["cameras"], **{key: placements[key] for key in ("band", "waypoints", "effects", "crowd") if key in placements}}
    for key in ("lighting", "scripts"):
        if key in entry: stage[key] = entry[key]
    if "actors" in entry: stage["actors"] = json.loads(source.read(entry["actors"]))["actors"]
    stage = {key: stage[key] for key in STAGE_KEYS if key in stage}
    with tempfile.TemporaryDirectory(prefix=".roundtrip-", dir=Path(package).parent) as temporary:
        folder = Path(temporary)/"source"
        for name in source.names:
            if name in ("manifest.json", entry["placements"], entry.get("actors")): continue
            (folder/name).parent.mkdir(parents=True, exist_ok=True)
            (folder/name).write_bytes(source.read(name))
        write_json(folder/"stage-pack.json", stage)
        compiled = Path(temporary)/"stages"/manifest["id"]
        compile_stage(folder, compiled)
        result = compare_trees(compiled, expected)
    result["ok"] = not (result["missing"] or result["extra"] or result["different"])
    return result


# Library models.
def raw_accessor(glb, index):
    accessor = glb.list("accessors")[index]
    dtype = np.dtype(COMPONENTS[accessor["componentType"]]); columns = COLUMNS[accessor["type"]]
    view = glb.list("bufferViews")[accessor["bufferView"]]
    stride = view.get("byteStride", columns * dtype.itemsize)
    return np.ndarray((accessor["count"], columns), dtype, glb.view(accessor["bufferView"]), accessor.get("byteOffset", 0), (stride, dtype.itemsize)).copy()


def runtime_material(glb, material):
    """stage.json material of a library model from its GLB material (inverse of GlbWriter.material for skinned models)."""
    images = glb.list("images"); textures = glb.list("textures")
    pbr = material.get("pbrMetallicRoughness", {}); extras = material.get("extras", {})
    shader = extras.get("gh3Shader")
    mode = material.get("alphaMode", "OPAQUE")
    result = {"blend": shader["blend"] if shader else 1 if extras.get("gh3Additive") else 2 if mode == "BLEND" else 0,
              "cutoff": material.get("alphaCutoff", 0.5) if mode == "MASK" else extras.get("gh3AlphaCutoff", 0),
              "doubleSided": material.get("doubleSided", False),
              "texture": images[textures[pbr["baseColorTexture"]["index"]]["source"]]["name"] if "baseColorTexture" in pbr else ""}
    if shader: result["gh3"] = {**shader, "textures": [reference[1:] for reference in shader["textures"]]}
    if "gh3Shading" in extras: result["shading"] = extras["gh3Shading"]
    if extras.get("gh3SortedTransparency"): result["sortedTransparency"] = True
    if "baseColorFactor" in pbr:
        scale = extras.get("gh3ColorScale", 1)
        result["color"] = [v * scale for v in pbr["baseColorFactor"][:3]] + [pbr["baseColorFactor"][3]]
    return result


def model_runtime(data):
    """Runtime files of one library model GLB: meshes.bin bytes, rig and clips, stage.json materials, GH3TEX02 images."""
    glb = Glb(data)
    nodes = glb.list("nodes")
    joints = glb.list("skins")[0]["joints"]
    hierarchy = glb.parents()
    parents = [joints.index(hierarchy[j]) if hierarchy.get(j) in joints else -1 for j in joints]
    bind = np.array([[*np.array(nodes[j]["translation"]) * FLIP_POSITION, *np.array(nodes[j]["rotation"]) * FLIP_ROTATION] for j in joints], dtype=np.float32)
    clips, names = [], []
    for animation in glb.list("animations"):
        samplers = animation["samplers"]
        channels = {(c["target"]["node"], c["target"]["path"]): samplers[c["sampler"]] for c in animation["channels"]}
        times = raw_accessor(glb, samplers[0]["input"])[:, 0]
        poses = np.zeros((len(times), len(joints), 7), dtype=np.float32)
        for bone, joint in enumerate(joints):
            poses[:, bone, :3] = raw_accessor(glb, channels[joint, "translation"]["output"]) * FLIP_POSITION
            poses[:, bone, 3:] = raw_accessor(glb, channels[joint, "rotation"]["output"]) * FLIP_ROTATION
        clips.append((float(times[-1]), poses)); names.append(animation["name"])
    model = next(node for node in nodes if "mesh" in node)
    materials = glb.list("materials")
    parts = []
    for primitive in glb.list("meshes")[model["mesh"]]["primitives"]:
        attributes = primitive["attributes"]
        positions = raw_accessor(glb, attributes["POSITION"])
        vertices = np.zeros(len(positions), dtype=SKINNED_GH3_VERTEX)
        vertices["position"] = positions * FLIP_POSITION
        vertices["normal"] = raw_accessor(glb, attributes["NORMAL"]) * FLIP_POSITION
        uv = raw_accessor(glb, attributes["TEXCOORD_0"]); uv[:, 1] = np.float32(1) - uv[:, 1]
        vertices["uv"] = uv
        vertices["color"] = raw_accessor(glb, attributes["COLOR_0"])
        if "_GH3_UV0" in attributes:
            for i in range(6): vertices["gh3uv"][:, i] = raw_accessor(glb, attributes[f"_GH3_UV{i}"])
            vertices["gh3binormal"] = raw_accessor(glb, attributes["_GH3_BINORMAL"]) * FLIP_POSITION
            vertices["gh3tangent"] = raw_accessor(glb, attributes["_GH3_TANGENT"]) * FLIP_POSITION
        vertices["joints"] = raw_accessor(glb, attributes["JOINTS_0"])
        vertices["weights"] = raw_accessor(glb, attributes["WEIGHTS_0"])
        faces = raw_accessor(glb, primitive["indices"]).reshape(-1, 3)[:, [0, 2, 1]]
        parts.append((materials[primitive["material"]]["name"], len(vertices), vertices.tobytes(), faces))
    used = {material["name"]: runtime_material(glb, material) for material in materials}
    images = {image["name"]: bytes(glb.view(image["bufferView"])) for image in glb.list("images")}
    referenced = {m["texture"] for m in used.values()} | {t for m in used.values() for t in m.get("gh3", {}).get("textures", [])}
    stage = {"version": 1, "name": model.get("name", ""), "cameraPosition": [0, 0, 0], "cameraTarget": [0, 1, 0], "materials": used}
    return {"meshes": runtime.write_meshes(parts), "parents": parents, "bind": bind, "clips": clips, "clipNames": names,
            "stage": stage, "textures": {name: images[name] for name in referenced - {""}}}


def mesh_difference(actual, expected):
    """Largest difference of each vertex field between two GH3BND02 files with the same layout."""
    a, e = runtime.read_meshes(actual), runtime.read_meshes(expected)
    if len(a) != len(e) or any(x[:2] != y[:2] or not np.array_equal(x[3], y[3]) for x, y in zip(a, e)): return {"layout": "different"}
    result = {}
    for x, y in zip(a, e):
        left, right = np.frombuffer(x[2], SKINNED_GH3_VERTEX), np.frombuffer(y[2], SKINNED_GH3_VERTEX)
        for field in SKINNED_GH3_VERTEX.names:
            different = left[field] != right[field]
            if different.any():
                row = result.setdefault(field, {"values": 0, "max": 0.0})
                row["values"] += int(different.sum())
                row["max"] = max(row["max"], float(np.abs(left[field].astype(np.float64) - right[field]).max()))
    return result


def check_model(data, folder, clip_names, animated=True):
    rebuilt = model_runtime(data)
    expected_stage = json.loads((folder/"stage.json").read_text())
    for material in expected_stage["materials"].values():
        for field in UNREAD_MATERIAL_FIELDS: material.pop(field, None)
        # An absent color and white draw the same (StageAssets keeps the white default); glTF writes no white factor.
        if material.get("color") == [1, 1, 1, 1]: del material["color"]
    expected_meshes = (folder/"meshes.bin").read_bytes()
    result = {"meshesBinByteEqual": rebuilt["meshes"] == expected_meshes, "stageJsonEqual": rebuilt["stage"] == expected_stage,
              "texturesByteEqual": all(data == encoded((folder/"textures"/f"{n}.png").read_bytes()) for n, data in rebuilt["textures"].items()),
              "textures": len(rebuilt["textures"]), "unreferencedTextures": len(list((folder/"textures").glob("*.png"))) - len(rebuilt["textures"])}
    if not result["meshesBinByteEqual"]: result["meshDifference"] = mesh_difference(rebuilt["meshes"], expected_meshes)
    if animated:
        animation = runtime.write_animation(rebuilt["parents"], rebuilt["bind"], rebuilt["clips"])
        result["animationBinByteEqual"] = animation == (folder/"animation.bin").read_bytes() and rebuilt["clipNames"] == list(clip_names)
    result["ok"] = result["stageJsonEqual"] and result["texturesByteEqual"] and result.get("animationBinByteEqual", True) and (
        result["meshesBinByteEqual"] or set(result["meshDifference"]) <= {"uv"} and result["meshDifference"]["uv"]["max"] <= 6e-8)
    return result


def quaternion_error(a, b):
    """Rotation angle in degrees between quaternions; the chord form stays exact for tiny angles, unlike arccos."""
    a = a.astype(np.float64); b = b.astype(np.float64)
    a /= np.linalg.norm(a, axis=-1, keepdims=True); b /= np.linalg.norm(b, axis=-1, keepdims=True)
    b *= np.where(np.sum(a * b, -1, keepdims=True) < 0, -1.0, 1.0)
    return np.degrees(4 * np.arcsin(np.clip(np.linalg.norm(a - b, axis=-1) / 2, 0, 1)))


def check_clip(package_data, source_data):
    """Decode a q48 clip, write it as GH3CLP01 again, and compare it with the current clip."""
    import brotli
    header, positions, rotations = q48.decode(brotli.decompress(package_data))
    rebuilt = runtime.write_clip(header["bones"], header["duration"], header["tracks"], np.concatenate((positions, rotations), -1))
    bones, duration, tracks, poses = runtime.read_clip(rebuilt)
    source_bones, source_duration, source_tracks, source = runtime.read_clip(gzip.decompress(source_data))
    if (bones, duration, [tuple(t) for t in tracks]) != (source_bones, source_duration, [tuple(t) for t in source_tracks]) or poses.shape != source.shape:
        return {"ok": False, "header": "different"}
    masks = np.array([mask for _, mask in tracks])
    rotation = quaternion_error(poses[:, masks & 2 > 0, 3:], source[:, masks & 2 > 0, 3:])
    moved = source[:, masks & 1 > 0, :3].astype(np.float64)
    if moved.size:
        steps = (moved.max(0) - moved.min(0)) / 65535
        allowed = steps / 2 + np.abs(moved).max(0) * 2.4e-7 + 1e-9
        position_ratio = float((np.abs(poses[:, masks & 1 > 0, :3] - moved) / allowed).max())
        position_error = float(np.abs(poses[:, masks & 1 > 0, :3] - moved).max())
    else: position_ratio = position_error = 0.0
    rotation_error = float(rotation.max()) if rotation.size else 0.0
    return {"ok": rotation_error <= ROTATION_BOUND and position_ratio <= 1, "rotation": rotation_error, "position": position_error, "positionRatio": position_ratio}


def library_round_trip(package, library, events):
    source = open_package(package)
    library = Path(library)
    read = source.read
    result = {"json": {}, "models": {}, "clips": {}, "audio": {}, "movies": {}, "files": {}, "notCarried": {}}

    def same_bytes(name, path):
        result["json" if name.endswith(".json") else "files"][name] = read(name) == Path(path).read_bytes()

    for name in ("catalog.json", "band/band.json", "band/hair.json", "direction/performance.json", "crowd/crowd.json", "hand-fire/hand-fire.json"):
        same_bytes(name, library/name)
    for path, name in EFFECT_FILES.items(): same_bytes(name, library/path)
    pngs = [n for n in source.names if n.startswith(("particle-textures/", "hand-fire/")) and n.endswith(".png")]
    result["files"]["particle and hand-fire textures"] = all(read(n) == (library/n).read_bytes() for n in pngs) and \
        len(pngs) == len(list((library/"particle-textures").glob("*.png"))) + len(list((library/"hand-fire").glob("*.png")))

    scripts = json.loads(read("scripts.json"))
    expected_scripts = json.loads((library/"scripts.json").read_text())
    for movie, path in scripts.get("movies", {}).items():
        folder = path.rsplit("/", 1)[0]
        info = json.loads(read(path))
        frames = [read(f"{folder}/frames/{i:06d}.png") for i in range(info["frames"])]
        original = expected_scripts["movies"][movie]
        result["movies"][movie] = runtime.write_movie(info["width"], info["height"], info["rate"], frames) == (library/original).read_bytes()
        scripts["movies"][movie] = original
    result["json"]["scripts.json"] = scripts == expected_scripts

    audio = json.loads(read("audio/audio.json"))
    equal = 0
    for sample, row in audio["samples"].items():
        pcm, rate, channels = runtime.pcm_from_wav(read(row.pop("file")))
        stream = BytesIO()
        with gzip.GzipFile(filename=f"{sample}.pcm", mode="wb", fileobj=stream, mtime=0) as compressed: compressed.write(pcm)
        expected = (library/"audio"/f"{sample}.pcm.gz").read_bytes()
        equal += (rate, channels) == (row["rate"], row["channels"]) and (stream.getvalue() == expected or pcm == gzip.decompress(expected))
        result["audio"].setdefault("gzipByteEqual", 0)
        result["audio"]["gzipByteEqual"] += stream.getvalue() == expected
    result["audio"].update(samples=len(audio["samples"]), pcmEqual=equal)
    result["json"]["audio/audio.json"] = audio == json.loads((library/"audio/audio.json").read_text())

    band = json.loads(read("band/band.json"))
    for member in band["members"]:
        result["models"][f"band/{member['role']}"] = check_model(read(f"band/{member['role']}/model.glb"), library/"band"/member["role"], member["clips"])
    crowd = json.loads(read("crowd/crowd.json"))
    for model in crowd["models"]:
        result["models"][f"crowd/{model}"] = check_model(read(f"crowd/{model}/model.glb"), library/"crowd"/model, crowd["clips"][model])
    for hand in crowd["hands"]:
        result["models"][f"crowd/hands/{hand}"] = check_model(read(f"crowd/hands/{hand}/model.glb"), library/"crowd/hands"/hand, (), animated=False)

    performance = json.loads(read("direction/performance.json"))
    clips = [(f"direction/{role}/{clip}.q48.br", library/"direction"/role/f"{clip}.bin.gz") for role, member in performance["members"].items() for clip in member["clips"]]
    clips += [(f"crowd/clips/{clip}.q48.br", library/"crowd/clips"/f"{clip}.bin.gz") for clip in crowd["performanceClips"]]
    rows = [check_clip(read(name), path.read_bytes()) for name, path in clips]
    result["clips"] = {"count": len(rows), "ok": sum(r["ok"] for r in rows), "maxRotationDegrees": max(r.get("rotation", math.inf) for r in rows),
                       "maxPositionMetres": max(r.get("position", math.inf) for r in rows), "maxPositionStepRatio": max(r.get("positionRatio", math.inf) for r in rows)}
    current = sorted(p.relative_to(library).as_posix() for p in library.rglob("*.bin.gz"))
    result["clips"]["currentClipFiles"] = len(current)

    songs = sorted(n for n in source.names if n.startswith("songs/"))
    result["files"]["songs"] = all(read(n) == (Path(events)/n.split("/", 1)[1]).read_bytes() for n in songs) and \
        len(songs) == len([p for p in Path(events).glob("*.json") if p.name != "export-report.json"])

    kinds = Counter()
    for path in sorted(library.rglob("*")):
        if not path.is_file(): continue
        name = path.relative_to(library).as_posix()
        if name.startswith("render/") and name not in EFFECT_FILES: kinds["render shader bundles and stage-templates.json (mod installation)"] += 1
        elif name == "library.json": kinds["library.json (replaced by manifest.json files)"] += 1
        elif name.endswith(".gh3video.json"): kinds["movie conversion stamps (pipeline cache)"] += 1
    result["notCarried"] = dict(kinds)
    result["notCarried"]["unreferenced model textures (the runtime loads textures by material reference)"] = sum(m["unreferencedTextures"] for m in result["models"].values())
    result["notCarried"]["stage.json material fields textures, template, geometryCutout (not read by the runtime)"] = sum(
        sum(field in material for field in UNREAD_MATERIAL_FIELDS) for folder in [*library.glob("band/*"), *library.glob("crowd/*"), *library.glob("crowd/hands/*")]
        if (folder/"stage.json").is_file() for material in json.loads((folder/"stage.json").read_text())["materials"].values())
    result["ok"] = (all(result["json"].values()) and all(result["files"].values()) and all(result["movies"].values()) and
                    result["audio"]["pcmEqual"] == result["audio"]["samples"] and all(m["ok"] for m in result["models"].values()) and
                    result["clips"]["ok"] == result["clips"]["count"] == result["clips"]["currentClipFiles"])
    return result


def main(argv=None):
    parser = argparse.ArgumentParser(prog="python -m pipeline.package.roundtrip", description=__doc__.splitlines()[0])
    parser.add_argument("--packages", type=Path, default=OUTPUT)
    parser.add_argument("--stages", type=Path, default=STAGES, help="compiled packs to compare with")
    parser.add_argument("--library", type=Path, default=LIBRARY)
    parser.add_argument("--events", type=Path, default=EVENTS)
    parser.add_argument("--venue", action="append")
    parser.add_argument("--no-venues", action="store_true")
    parser.add_argument("--no-library", action="store_true")
    parser.add_argument("--json", type=Path)
    args = parser.parse_args(argv)
    TEXTURE_CACHE[0] = args.packages/".texture-cache"
    report = {"venues": {}, "library": None}
    if not args.no_venues:
        for package in sorted(args.packages.glob("*.chstage")):
            identifier = package.name.removesuffix(".chstage")
            if args.venue and identifier not in args.venue: continue
            report["venues"][identifier] = row = venue_round_trip(package, args.stages/identifier)
            print(f"{identifier}: {'equal' if row['ok'] else 'DIFFERENT'} ({row['byteEqual']} byte-equal, {row['jsonEqual']} JSON-equal, {row['packJsonEqual']} pack.json equal without hashes, "
                  f"{row['textureEncoderEqual']} textures equal to the encoder output, of {row['files']}; "
                  f"different {row['different']}, missing {row['missing']}, extra {row['extra']})")
    if not args.no_library:
        package = next(args.packages.glob("gh3-band-v1.ch*"))
        report["library"] = library_round_trip(package, args.library, args.events)
        print(json.dumps(report["library"], indent=1))
    if args.json: args.json.write_bytes(json_bytes(report))
    failed = any(not row["ok"] for row in report["venues"].values()) or (report["library"] is not None and not report["library"]["ok"])
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
