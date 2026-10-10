"""Convert the current source packs and the shared library into package format v1 (docs/PACKAGE-FORMAT.md).

Each package is written two times: as an unpacked development folder (`<output>/unpacked/<name>`) and as a zip
(`<output>/<name>`). The output is reproducible: sorted zip entries, fixed times and attributes, and stable JSON.

Usage: python -m pipeline.package.build [--packs DIR] [--library DIR] [--character-library DIR] [--events DIR]
                                        [--characters DIR] [--instruments DIR] [--output DIR] [--venue ID ...] [--no-venues]
                                        [--no-library] [--no-character-library] [--no-characters] [--no-instruments] [--jobs N]

The GLB images become pre-encoded GH3TEX02 textures (BC1, BC3, or RGBA32 with mipmaps, pipeline/package/texture.py). The
encoder keeps its results in <output>/.texture-cache, so a second run encodes only changed images.
"""

import argparse
import gzip
import hashlib
import json
import os
import shutil
from concurrent.futures import ProcessPoolExecutor
from io import BytesIO
from pathlib import Path

import numpy as np
from PIL import Image

from pipeline.package import decisions, q48, runtime, texture
from pipeline.package.container import file_list, json_bytes, write_zip
from pipeline.package.glb import Glb
from pipeline.paths import CHARACTER_LIBRARY, CHARACTERS, INSTRUMENTS, LIBRARY, PACKAGES, STAGE_PACKS, STAGES
from pipeline.stagepack.gltf import GlbWriter

OUTPUT = PACKAGES
EVENTS = STAGES/"events"
BROTLI_QUALITY = 11
BROTLI_WINDOW = 24
# Increment when the clip encoder output changes, so that the clip cache is not reused.
CLIP_ENCODER = "q48-1"
# Increment when the texture encoder output changes, so that the texture cache is not reused.
TEXTURE_ENCODER = "gh3tex02-1"
PLACEMENT_FIELDS = ("band", "waypoints", "effects", "crowd")
LIBRARY_ENTRY = {"catalog": "catalog.json", "band": "band/band.json", "performance": "direction/performance.json",
                 "hair": "band/hair.json", "crowd": "crowd/crowd.json", "handFire": "hand-fire/hand-fire.json",
                 "audio": "audio/audio.json", "scripts": "scripts.json", "songs": "songs/index.json"}
EFFECT_FILES = {"render/particle-materials.json": "effects/particle-materials.json",
                "render/tesla-material.json": "effects/tesla-material.json", "render/tesla-noise.png": "effects/tesla-noise.png"}


class PackageWriter:
    """Collects the files of one package in a staging folder and writes the manifest."""

    def __init__(self, folder):
        self.folder = Path(folder)
        if self.folder.exists(): shutil.rmtree(self.folder)
        self.folder.mkdir(parents=True)
        self.paths = set()

    def target(self, name):
        if name in self.paths or name == "manifest.json": raise ValueError(f"Package file written two times: {name}")
        self.paths.add(name)
        path = self.folder/name
        path.parent.mkdir(parents=True, exist_ok=True)
        return path

    def add_bytes(self, name, data):
        self.target(name).write_bytes(data)

    def add_json(self, name, value):
        self.add_bytes(name, json_bytes(value))

    def add_file(self, name, source):
        shutil.copyfile(source, self.target(name))

    def finish(self, manifest):
        files = file_list(self.folder, self.paths)
        manifest = {**manifest, "files": files}
        (self.folder/"manifest.json").write_bytes(json_bytes(manifest))
        return manifest


def publish(writer, output, name):
    """Move a finished staging folder to <output>/unpacked/<name> and write the zip <output>/<name>."""
    folder = output/"unpacked"/name
    if folder.exists(): shutil.rmtree(folder)
    writer.folder.rename(folder)
    write_zip(folder, output/name)
    return folder, output/name


def texture_index_references(glb_bytes):
    """Material extras that still use glTF texture indices (not permitted in format v1)."""
    found = 0
    for material in Glb(glb_bytes).list("materials"):
        extras = material.get("extras", {})
        shader = extras.get("gh3Shader", {})
        toon = extras.get("gh3Toon", {})
        found += sum(isinstance(ref, int) for ref in [extras.get("gh3MaskTexture"), extras.get("gh3LayerTexture"), *shader.get("textures", []),
                                                      *(toon.get(key) for key in ("shadeTexture", "shadeMaskTexture", "sphereTexture"))])
    return found


def encode_texture(job):
    """GH3TEX02 bytes of one PNG image; reuse the cache entry of the same PNG bytes."""
    png, linear, cache = job
    cached = Path(cache)/f"{hashlib.sha256(png + f'{TEXTURE_ENCODER} {int(linear)}'.encode()).hexdigest()}.tex"
    if cached.is_file(): return cached.read_bytes()
    data = texture.encode(np.asarray(Image.open(BytesIO(png)).convert("RGBA")), linear)
    cached.parent.mkdir(parents=True, exist_ok=True)
    temporary = cached.with_name(cached.name + f".{os.getpid()}.tmp")
    temporary.write_bytes(data); os.replace(temporary, cached)
    return data


class TextureEncoder:
    """Encodes the PNG images of package GLBs in worker processes (pipeline/package/texture.py)."""

    def __init__(self, cache, jobs=2):
        self.cache, self.jobs, self.pool = str(cache), max(1, jobs), None
        self.count = 0

    def __call__(self, items):
        self.count += len(items)
        jobs = [(png, linear, self.cache) for png, linear in items]
        if self.jobs == 1 or len(jobs) == 1: return [encode_texture(job) for job in jobs]
        if self.pool is None: self.pool = ProcessPoolExecutor(max_workers=self.jobs)
        return list(self.pool.map(encode_texture, jobs))

    def glb(self, data):
        return texture.encode_glb(data, self)

    def close(self):
        if self.pool is not None: self.pool.shutdown()
        self.pool = None


# Venues.
def venue_manifest(stage, entry):
    gh3 = stage["id"].startswith("gh3.")
    manifest = {"format": "chstage/1.0", "id": stage["id"], "version": decisions.PACKAGE_VERSION, "name": stage["name"],
                "authors": decisions.GH3_VENUE_AUTHORS if gh3 else decisions.EXAMPLE_AUTHORS,
                "license": decisions.GH3_LICENSE if gh3 else decisions.EXAMPLE_LICENSE,
                "permissions": decisions.GH3_PERMISSIONS if gh3 else decisions.EXAMPLE_PERMISSIONS}
    if gh3: manifest["credits"] = [decisions.CONVERTER_CREDIT]
    manifest["requires"] = [{"id": stage["library"], "version": decisions.LIBRARY_VERSION}]
    manifest["entry"] = entry
    return manifest


def build_venue(source, output, textures=None):
    source = Path(source)
    textures = textures or TextureEncoder(Path(output)/".texture-cache", 1)
    stage = json.loads((source/"stage-pack.json").read_text())
    name = stage["id"] + ".chstage"
    writer = PackageWriter(output/"unpacked"/("." + name + ".tmp"))
    entry = {"model": stage["model"], "placements": "placements.json", "cameras": stage["cameras"]}
    models = [stage["model"], *(actor["model"] for actor in stage.get("actors", {}).values())]
    for model in models:
        data = (source/model).read_bytes()
        if texture_index_references(data): raise ValueError(f"{source/model} uses glTF texture indices in extras; export the source pack again")
        writer.add_bytes(model, textures.glb(data))
    writer.add_file(stage["cameras"], source/stage["cameras"])
    for key in ("lighting", "scripts"):
        if key in stage:
            entry[key] = stage[key]
            writer.add_file(stage[key], source/stage[key])
    writer.add_json("placements.json", {"version": 1, **{key: stage[key] for key in PLACEMENT_FIELDS if key in stage}})
    if "actors" in stage:
        entry["actors"] = "actors.json"
        writer.add_json("actors.json", {"version": 1, "actors": stage["actors"]})
    if "scripts" in stage:
        scripts = json.loads((source/stage["scripts"]).read_text())
        for file in sorted({*scripts.get("textures", {}).values(), *scripts.get("movies", {}).values()}):
            writer.add_file(file, source/file)
    entry = {key: entry[key] for key in ("model", "placements", "cameras", "lighting", "scripts", "actors") if key in entry}
    manifest = venue_manifest(stage, entry)
    preview = decisions.VENUE_PREVIEWS/(stage["id"] + ".png")
    if preview.is_file():
        writer.add_file("preview.png", preview)
        manifest["preview"] = "preview.png"
    writer.finish(manifest)
    return publish(writer, output, name)


# Library.
def skinned_model(folder, bones, clip_names=(), rig=None, name=None):
    """GLB bytes of one compiled GH3BND02 model with its GH3ANM01 rig and sampled clips (Unity space to glTF)."""
    parts = runtime.read_meshes((folder/"meshes.bin").read_bytes())
    if any(not len(faces) for _, _, _, faces in parts): raise ValueError(f"{folder}: a mesh part has no triangles")
    materials = json.loads((folder/"stage.json").read_text())["materials"]
    textures = {p.stem: p.read_bytes() for p in sorted((folder/"textures").glob("*.png"))}
    parents, bind, clips = rig or runtime.read_animation((folder/"animation.bin").read_bytes())
    if len(bones) != len(parents): raise ValueError(f"{folder}: bone names differ from the rig")
    # GlbWriter.actor takes GH3-space positions (z negated from Unity) and rotations whose conjugate is the glTF rotation.
    flip_position, flip_rotation = np.array([1, 1, -1]), np.array([1, 1, -1, 1])
    gh3_rig = ([int(b, 16) for b in bones], parents, bind[:, :3].astype(np.float64) * flip_position, bind[:, 3:].astype(np.float64) * flip_rotation)
    if len(clip_names) != len(clips): raise ValueError(f"{folder}: clip names differ from the clips")
    tracks = [(clip_name, float(duration), poses[..., :3].astype(np.float64) * flip_position, poses[..., 3:].astype(np.float64) * flip_rotation)
              for clip_name, (duration, poses) in zip(clip_names, clips)]
    writer = GlbWriter()
    writer.data["asset"]["generator"] = "GH3 Gameplay Skin package converter"
    writer.actor([(m, c, v, f.tolist()) for m, c, v, f in parts], materials, textures, gh3_rig, tracks)
    # Keep the materials that no mesh part uses, so that the package holds the whole stage.json.
    for material, definition in materials.items(): writer.material(material, definition, textures, layered=False, streams=True)
    if name: next(node for node in writer.data["nodes"] if "mesh" in node)["name"] = name
    return bytes(writer.to_bytes())


def encode_clip(job):
    """Encode one GH3CLP01 clip as q48 + Brotli; reuse the cache entry of the same source bytes."""
    import brotli
    source, target, cache = job
    data = Path(source).read_bytes()
    cached = Path(cache)/f"{hashlib.sha256(data + CLIP_ENCODER.encode()).hexdigest()}.q48.br"
    if cached.is_file():
        shutil.copyfile(cached, target)
        return Path(source).stat().st_size, cached.stat().st_size
    bones, duration, tracks, poses = runtime.read_clip(gzip.decompress(data))
    raw = q48.encode(poses[..., :3], poses[..., 3:], tracks, bones, duration)
    compressed = brotli.compress(raw, quality=BROTLI_QUALITY, lgwin=BROTLI_WINDOW)
    cached.parent.mkdir(parents=True, exist_ok=True)
    temporary = cached.with_name(cached.name + f".{os.getpid()}.tmp")
    temporary.write_bytes(compressed); os.replace(temporary, cached)
    Path(target).write_bytes(compressed)
    return len(data), len(compressed)


def library_manifest(entry):
    return {"format": "chlib/1.0", "id": "gh3-band-v1", "version": decisions.LIBRARY_VERSION, "name": "GH3 band library",
            "description": "The GH3 band, crowd, animation clips, stage sounds, Video Shoot movies, particle textures, and original song direction.",
            "authors": decisions.GH3_LIBRARY_AUTHORS, "license": decisions.GH3_LICENSE, "permissions": decisions.GH3_PERMISSIONS,
            "credits": [decisions.CONVERTER_CREDIT], "entry": entry}


def build_library(library, events, output, jobs=2, textures=None):
    if decisions.AUDIO_FORMAT != "wav" or decisions.MOVIE_FORMAT != "png-frames":
        raise ValueError("Only WAV audio and PNG movie frames are implemented")
    library = Path(library)
    textures = textures or TextureEncoder(Path(output)/".texture-cache", jobs)
    name = "gh3-band-v1" + decisions.LIBRARY_EXTENSION
    writer = PackageWriter(output/"unpacked"/("." + name + ".tmp"))
    for path in ("catalog.json", "band/band.json", "band/hair.json", "direction/performance.json", "crowd/crowd.json", "hand-fire/hand-fire.json"):
        writer.add_file(path, library/path)
    for path in sorted((library/"hand-fire").glob("*.png")): writer.add_file(f"hand-fire/{path.name}", path)
    for path in sorted((library/"particle-textures").glob("*.png")): writer.add_file(f"particle-textures/{path.name}", path)
    for path, target in EFFECT_FILES.items(): writer.add_file(target, library/path)

    scripts = json.loads((library/"scripts.json").read_text())
    for movie, path in sorted(scripts.get("movies", {}).items()):
        width, height, rate, frames = runtime.read_movie((library/path).read_bytes())
        folder = f"movies/{Path(path).stem}"
        writer.add_json(f"{folder}/movie.json", {"version": 1, "width": width, "height": height, "frames": len(frames), "rate": rate})
        for index, frame in enumerate(frames): writer.add_bytes(f"{folder}/frames/{index:06d}.png", frame)
        scripts["movies"][movie] = f"{folder}/movie.json"
    writer.add_json("scripts.json", scripts)

    audio = json.loads((library/"audio/audio.json").read_text())
    for sample, row in audio["samples"].items():
        pcm = gzip.decompress((library/"audio"/f"{sample}.pcm.gz").read_bytes())
        if len(pcm) != row["frames"] * row["channels"] * 2: raise ValueError(f"Audio sample {sample} differs from audio.json")
        writer.add_bytes(f"audio/{sample}.wav", runtime.wav_from_pcm(pcm, row["rate"], row["channels"]))
        row["file"] = f"audio/{sample}.wav"
    writer.add_json("audio/audio.json", audio)

    performance = json.loads((library/"direction/performance.json").read_text())
    for member in json.loads((library/"band/band.json").read_text())["members"]:
        role = member["role"]
        writer.add_bytes(f"band/{role}/model.glb", textures.glb(skinned_model(library/"band"/role, performance["members"][role]["bones"], member["clips"], name=member["model"])))
    crowd = json.loads((library/"crowd/crowd.json").read_text())
    crowd_rig = None
    for model in crowd["models"]:
        rig = runtime.read_animation((library/"crowd"/model/"animation.bin").read_bytes())
        if crowd_rig is None: crowd_rig = rig
        elif rig[0] != crowd_rig[0] or not np.array_equal(rig[1], crowd_rig[1]): raise ValueError("Crowd models use different rigs")
        writer.add_bytes(f"crowd/{model}/model.glb", textures.glb(skinned_model(library/"crowd"/model, crowd["bones"], crowd["clips"][model], rig, name=model)))
    for hand in crowd["hands"]:
        writer.add_bytes(f"crowd/hands/{hand}/model.glb", textures.glb(skinned_model(library/"crowd/hands"/hand, crowd["bones"], (), (crowd_rig[0], crowd_rig[1], []), name=hand)))

    jobs_list = []
    for role, member in performance["members"].items():
        for clip in sorted(member["clips"]): jobs_list.append((library/"direction"/role/f"{clip}.bin.gz", f"direction/{role}/{clip}.q48.br"))
    for clip in sorted(crowd["performanceClips"]): jobs_list.append((library/"crowd/clips"/f"{clip}.bin.gz", f"crowd/clips/{clip}.q48.br"))
    cache = output/".clip-cache"
    tasks = [(str(source), str(writer.target(target)), str(cache)) for source, target in jobs_list]
    with ProcessPoolExecutor(max_workers=max(1, jobs)) as pool:
        sizes = list(pool.map(encode_clip, tasks, chunksize=8))

    events = Path(events)
    for path in sorted(events.glob("*.json")):
        if path.name != "export-report.json": writer.add_file(f"songs/{path.name}", path)
    writer.finish(library_manifest(LIBRARY_ENTRY))
    return publish(writer, output, name), sizes


# GH3 character library (pipeline.character_library).
CHARACTER_LIBRARY_ID = "gh3-characters-v1"


def character_library_manifest():
    return {"format": "chlib/1.0", "id": CHARACTER_LIBRARY_ID, "version": decisions.LIBRARY_VERSION, "name": "GH3 character library",
            "description": "The rig profiles, animation sets, hair physics, and IK of the playable GH3 guitarists.",
            "authors": decisions.GH3_CHARACTER_LIBRARY_AUTHORS, "license": decisions.GH3_LICENSE, "permissions": decisions.GH3_PERMISSIONS,
            "credits": [decisions.CONVERTER_CREDIT], "requires": [{"id": "gh3-band-v1", "version": decisions.LIBRARY_VERSION}],
            "entry": {"characters": "characters.json"}}


def build_character_library(folder, output, jobs=2):
    folder = Path(folder)
    name = CHARACTER_LIBRARY_ID + decisions.LIBRARY_EXTENSION
    writer = PackageWriter(output/"unpacked"/("." + name + ".tmp"))
    library = json.loads((folder/"characters.json").read_text())
    library.pop("sources", None)
    writer.add_json("characters.json", library)
    clips = sorted({clip for animations in library["animations"].values() for clip in animations["clips"]})
    tasks = [(str(folder/"clips"/f"{clip}.bin.gz"), str(writer.target(f"clips/{clip}.q48.br")), str(output/".clip-cache")) for clip in clips]
    with ProcessPoolExecutor(max_workers=max(1, jobs)) as pool:
        sizes = list(pool.map(encode_clip, tasks, chunksize=8))
    writer.finish(character_library_manifest())
    return publish(writer, output, name), sizes


# GH3 characters (pipeline.characters).
LIBRARY_PROFILES = {"gh3-guitarist-axel-v1": "guitarist", "gh3-singer-male-v1": "singer"}


def profile_rig(profile, library, character_library):
    """(library ID, bone names, (parents, bind float32 Unity space)) of a rig profile: a role rig of the band library
    or a profile of the character library."""
    if profile in LIBRARY_PROFILES:
        role = LIBRARY_PROFILES[profile]
        parents, bind, _ = runtime.read_animation((library/"band"/role/"animation.bin").read_bytes())
        return "gh3-band-v1", json.loads((library/"direction/performance.json").read_text())["members"][role]["bones"], (parents, bind)
    row = json.loads((character_library/"characters.json").read_text())["profiles"][profile]
    return CHARACTER_LIBRARY_ID, row["bones"], (row["parents"], np.array(row["bind"], dtype=np.float32))


def character_glb(folder, library, profile, character_library=CHARACTER_LIBRARY, clips=False):
    """The character GLB of an exported GH3 character on the rig of its profile. With clips (band library profiles
    only), the GLB also has the start clips of the library member (for previews; packages have no animations)."""
    _, bones, (parents, bind) = profile_rig(profile, library, character_library)
    names, start = (), []
    if clips:
        role = LIBRARY_PROFILES[profile]
        start = runtime.read_animation((library/"band"/role/"animation.bin").read_bytes())[2]
        names = next(m for m in json.loads((library/"band/band.json").read_text())["members"] if m["role"] == role)["clips"]
    return skinned_model(folder, bones, names, (parents, bind, start), name="character")


def character_highway(info, instruments):
    """The GH3 highway of a guitarist character (pipeline.instruments: one image for each GH3 profile name), or None."""
    path = Path(instruments)/"inventory.json"
    if info.get("role") != "guitarist" or not path.is_file(): return None
    row = json.loads(path.read_text())["highways"].get(info.get("profileName"))
    return Path(instruments)/row["file"] if row else None


def build_character(folder, library, output, character_library=CHARACTER_LIBRARY, instruments=INSTRUMENTS, textures=None):
    folder, library, character_library = Path(folder), Path(library), Path(character_library)
    textures = textures or TextureEncoder(Path(output)/".texture-cache", 1)
    info = json.loads((folder/"character.json").read_text())
    library_id = profile_rig(info["profile"], library, character_library)[0]
    name = info["id"] + ".chchar"
    writer = PackageWriter(output/"unpacked"/("." + name + ".tmp"))
    writer.add_bytes("character.glb", textures.glb(character_glb(folder, library, info["profile"], character_library)))
    character = {"version": 1, "library": library_id, "profile": info["profile"]}
    highway = character_highway(info, instruments)
    if highway is not None:
        # GH3 create_guitarist: the player's highway is the highway of the character (sys_<profile name>_1_highway).
        writer.add_file("highway.png", highway)
        character["highway"] = "highway.png"
    writer.add_json("character.json", character)
    credits = [decisions.CONVERTER_CREDIT, f"GH3 model {info['model']}" + (f", instrument {info['instrumentModel']}" if info["instrumentInModel"] else "") + "."]
    description = f"{info['name']}, as GH3 offers it ({info['unlock']})." + (f" Instrument: {info['instrumentName']}." if info["instrumentInModel"] else "")
    requires = [{"id": "gh3-band-v1", "version": decisions.LIBRARY_VERSION}]
    if library_id != "gh3-band-v1": requires.append({"id": library_id, "version": decisions.LIBRARY_VERSION})
    manifest = {"format": "chchar/1.0", "id": info["id"], "version": decisions.PACKAGE_VERSION,
                "name": info["name"], "description": description, "authors": decisions.GH3_CHARACTER_AUTHORS, "license": decisions.GH3_LICENSE,
                "permissions": decisions.GH3_PERMISSIONS, "credits": credits, "requires": requires,
                "entry": {"model": "character.glb", "character": "character.json"}}
    writer.finish(manifest)
    return publish(writer, output, name)


# GH3 instruments (pipeline.instruments).
INSTRUMENT_PROFILES = {"gh3-guitarist-axel-v1": "guitarist", "gh3-bassist-v1": "bassist"}
STORE_LABELS = {"Free_Guitars": "a start guitar", "Bonus_Guitars": "a store guitar", "Bonus_Guitar_Finishes": "a store guitar finish",
                "Secret_Guitars": "a secret store guitar", "Free_Basses": "the start bass", "Bonus_Basses": "a store bass",
                "Bonus_Bass_Finishes": "a store bass finish", "Secret_Basses": "a secret store bass", "none": "not in the store"}


def instrument_glb(folder, library, profile):
    """The instrument GLB of an exported GH3 instrument, bound to the rig of a fixed band library profile."""
    role = INSTRUMENT_PROFILES[profile]
    parents, bind, _ = runtime.read_animation((library/"band"/role/"animation.bin").read_bytes())
    bones = json.loads((library/"direction/performance.json").read_text())["members"][role]["bones"]
    return skinned_model(folder, bones, (), (parents, bind, []), name="instrument")


def build_instrument(folder, library, output, textures=None):
    folder, library = Path(folder), Path(library)
    textures = textures or TextureEncoder(Path(output)/".texture-cache", 1)
    info = json.loads((folder/"instrument.json").read_text())
    name = info["id"] + ".chinst"
    writer = PackageWriter(output/"unpacked"/("." + name + ".tmp"))
    writer.add_bytes("instrument.glb", textures.glb(instrument_glb(folder, library, info["profile"])))
    instrument = {"version": 1, "library": "gh3-band-v1", "profile": info["profile"], "kind": info["kind"]}
    for field in ("brand", "model", "series", "finish"):
        if info[field]: instrument[field] = info[field]
    writer.add_json("instrument.json", instrument)
    offer = STORE_LABELS[info["store"]] + (f" for ${info['price']:,}" if info["price"] else "")
    description = (info["description"] + "\n\n" if info["description"] else "") + f"GH3 offers it as {offer}."
    manifest = {"format": "chinst/1.0", "id": info["id"], "version": decisions.PACKAGE_VERSION, "name": info["name"],
                "description": description[:4000], "authors": decisions.GH3_INSTRUMENT_AUTHORS, "license": decisions.GH3_LICENSE,
                "permissions": decisions.GH3_PERMISSIONS, "credits": [decisions.CONVERTER_CREDIT, f"GH3 instrument {info['descName']} ({info['pak']})."],
                "requires": [{"id": "gh3-band-v1", "version": decisions.LIBRARY_VERSION}],
                "entry": {"model": "instrument.glb", "instrument": "instrument.json"}}
    writer.finish(manifest)
    return publish(writer, output, name)


def tree_size(folder):
    return sum(p.stat().st_size for p in Path(folder).rglob("*") if p.is_file())


def part_sizes(folder):
    """Bytes for each top-level part of an unpacked package."""
    sizes = {}
    for path in Path(folder).rglob("*"):
        if path.is_file():
            relative = path.relative_to(folder)
            part = relative.parts[0] if len(relative.parts) > 1 else relative.name
            sizes[part] = sizes.get(part, 0) + path.stat().st_size
    return dict(sorted(sizes.items()))


def main(argv=None):
    parser = argparse.ArgumentParser(prog="python -m pipeline.package.build", description=__doc__.splitlines()[0])
    parser.add_argument("--packs", type=Path, default=STAGE_PACKS)
    parser.add_argument("--library", type=Path, default=LIBRARY)
    parser.add_argument("--character-library", type=Path, default=CHARACTER_LIBRARY)
    parser.add_argument("--events", type=Path, default=EVENTS)
    parser.add_argument("--characters", type=Path, default=CHARACTERS)
    parser.add_argument("--instruments", type=Path, default=INSTRUMENTS)
    parser.add_argument("--output", type=Path, default=OUTPUT)
    parser.add_argument("--venue", action="append", help="Convert only this source pack ID (repeatable)")
    parser.add_argument("--no-venues", action="store_true")
    parser.add_argument("--no-library", action="store_true")
    parser.add_argument("--no-character-library", action="store_true")
    parser.add_argument("--no-characters", action="store_true")
    parser.add_argument("--no-instruments", action="store_true")
    parser.add_argument("--jobs", type=int, default=2, help="Clip and texture encoder processes (each uses about 0.1 GB, 0.3 GB for a 4096 x 4096 texture)")
    args = parser.parse_args(argv)
    (args.output/"unpacked").mkdir(parents=True, exist_ok=True)
    report = {"venues": {}, "library": None, "characterLibrary": None, "characters": {}, "instruments": {}}
    textures = TextureEncoder(args.output/".texture-cache", args.jobs)
    if not args.no_venues:
        for source in sorted(p.parent for p in args.packs.glob("*/stage-pack.json")):
            if args.venue and source.name not in args.venue: continue
            folder, archive = build_venue(source, args.output, textures)
            report["venues"][source.name] = {"folder": tree_size(folder), "zip": archive.stat().st_size}
            print(f"{archive}: folder {tree_size(folder):,} bytes, zip {archive.stat().st_size:,} bytes")
    if not args.no_library:
        (folder, archive), clips = build_library(args.library, args.events, args.output, args.jobs, textures)
        report["library"] = {"folder": tree_size(folder), "zip": archive.stat().st_size, "parts": part_sizes(folder),
                             "clips": {"count": len(clips), "sourceBytes": sum(a for a, _ in clips), "packageBytes": sum(b for _, b in clips)}}
        print(f"{archive}: folder {tree_size(folder):,} bytes, zip {archive.stat().st_size:,} bytes")
    if not args.no_character_library:
        (folder, archive), clips = build_character_library(args.character_library, args.output, args.jobs)
        report["characterLibrary"] = {"folder": tree_size(folder), "zip": archive.stat().st_size, "parts": part_sizes(folder),
                                      "clips": {"count": len(clips), "sourceBytes": sum(a for a, _ in clips), "packageBytes": sum(b for _, b in clips)}}
        print(f"{archive}: folder {tree_size(folder):,} bytes, zip {archive.stat().st_size:,} bytes")
    if not args.no_characters:
        for source in sorted(p.parent for p in args.characters.glob("*/character.json")):
            folder, archive = build_character(source, args.library, args.output, args.character_library, args.instruments, textures)
            report["characters"][source.name] = {"folder": tree_size(folder), "zip": archive.stat().st_size}
        total = sum(row["zip"] for row in report["characters"].values())
        print(f"{len(report['characters'])} characters: zip {total:,} bytes")
    if not args.no_instruments:
        for source in sorted(p.parent for p in args.instruments.glob("*/instrument.json")):
            folder, archive = build_instrument(source, args.library, args.output, textures)
            report["instruments"][source.name] = {"folder": tree_size(folder), "zip": archive.stat().st_size}
        total = sum(row["zip"] for row in report["instruments"].values())
        print(f"{len(report['instruments'])} instruments: zip {total:,} bytes")
    textures.close()
    (args.output/"build-report.json").write_bytes(json_bytes(report))


if __name__ == "__main__":
    main()
