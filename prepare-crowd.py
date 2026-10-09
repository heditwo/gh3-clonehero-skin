#!/usr/bin/env python3
"""Convert the owner's GH3 crowd models and original cheering animations."""

import argparse
from hashlib import sha256
import importlib.util
import json
from pathlib import Path
import struct

import numpy as np
from gh3_band_formats import animation, sample_animation, skeleton
from gh3_formats import find_struct, key, pak_entries
from gh3_direction import Source, script, scalar

spec = importlib.util.spec_from_file_location("prepare_stage", Path(__file__).with_name("prepare-stage.py"))
stage = importlib.util.module_from_spec(spec)
spec.loader.exec_module(stage)

MODELS = [
    ("crowd_ped_01", ["bill_reg_fast1", "bill_good_slow1"]),
    ("crowd_ped_02", ["rick_regular_med1", "rick_good_slow1"]),
    ("crowd_ped_03", ["jerod_reg_med1", "jerod_good_slow2"]),
    ("crowd_ped_04", ["colin_reg_slow1", "colin_good_fast1"]),
    ("crowd_pedf_1", ["becki_reg_med1", "becki_good_fast1"]),
    ("crowd_pedf_2", ["genna_regular_med1", "genna_good_med1"]),
    ("crowd_prison_01", ["colin_reg_slow1", "colin_good_fast1"]),
    ("crowd_prison_02", ["jerod_reg_med1", "jerod_good_slow2"]),
    ("crowd_biker_01", ["colin_reg_slow1", "colin_good_fast1"]),
    ("crowd_biker_02", ["jerod_reg_med1", "jerod_good_slow2"]),
]

def performance_data(data):
    profiles = find_struct(data, "Crowd_Profiles")
    models = [name for name, _ in MODELS[:6]] + ["crowd_ped_03", "crowd_ped_04"]
    members = {}
    for index, model in enumerate(models, 1):
        profile = find_struct(data, profiles[key(f"crowd{index}")][key("anim_set")])
        groups = {}
        for action in ("idle", "special", "starpower"):
            for skill in ("bad", "normal", "good"):
                for speed in ("slow", "med", "fast"):
                    refs = profile[key(action)][key(skill)][key(speed)]
                    if not refs or any(type(ref) != int for ref in refs):
                        raise ValueError("Invalid crowd animation references")
                    groups[f"{action}/{skill}/{speed}"] = [f"{ref:08x}" for ref in refs]
        members[f"crowd{index}"] = {"model": model, "groups": groups}
    return members


def write_model(directory, materials, meshes, images):
    (directory / "textures").mkdir(parents=True, exist_ok=True)
    for name, image in images.items():
        image.save(directory / "textures" / f"{name}.png")
    for material in materials.values():
        material["texture"] = stage.diffuse_texture(material, images)
    with (directory / "meshes.bin").open("wb") as stream:
        stream.write(b"GH3BND02" + struct.pack("<I", len(meshes)))
        for material, count, vertices, faces in meshes:
            stream.write(struct.pack("<III", int(material, 16), count, len(faces) * 3))
            stream.write(vertices)
            for face in faces:
                stream.write(struct.pack("<3I", *face))
    (directory / "stage.json").write_text(json.dumps({
        "version": 1, "name": directory.name, "cameraPosition": [0, 0, 0], "cameraTarget": [0, 1, 0],
        "materials": materials,
    }, indent=2) + "\n")


def convert(data_dir, output):
    sources = {}

    def load(relative):
        path = data_dir / relative
        data = path.read_bytes()
        sources[relative] = sha256(data).hexdigest()
        pab = path.with_name(path.name.replace(".pak.", ".pab."))
        if pab.exists():
            extra = pab.read_bytes()
            sources[str(pab.relative_to(data_dir))] = sha256(extra).hexdigest()
            data += extra
        return pak_entries(data)

    global_entries = {(kind, name): blob for kind, name, blob in load("ZONES/global.pak.xen")}
    rig = next(blob for (kind, name), blob in global_entries.items()
               if kind == key(".ske") and name == key("GH3_ped"))
    names, parents, positions, rotations = skeleton(rig)
    source = Source(data_dir)
    scripts = source.scripts()
    members = performance_data(script(scripts, "guitar_animation_data_crowd"))
    needed = {key("gh3_crowd_" + clip) for _, clips in MODELS for clip in clips}
    needed.update(int(ref, 16) for member in members.values() for refs in member["groups"].values() for ref in refs)
    clips = {name: blob for kind, name, blob in load("PAK/perm_anims.pak.xen")
             if kind == key(".ska") and name in needed}
    if clips.keys() != needed:
        raise ValueError("Missing original crowd animations")
    for model, clip_names in MODELS:
        directory = output / model
        materials, meshes, images, cubes = {}, [], {}, set()
        embedded = model in ("crowd_biker_01", "crowd_biker_02")
        archive = "ZONES/z_hell.pak.xen" if embedded else f"PAK/{model}.pak.xen"
        for kind, name, blob in load(archive):
            if embedded and name not in (key(f"models\\Real_Crowd\\{model}.skin"), key(f"models\\Real_Crowd\\{model}.tex")):
                continue
            if kind == key(".skin"):
                materials, meshes = stage.scene(blob, skinned=True)
            elif kind == key(".tex"):
                images.update(stage.textures(blob))
                cubes.update(stage.cube_names(blob))
        if not meshes:
            raise ValueError(f"Missing crowd model: {model}")
        stage.mark_cube_samplers(materials, cubes)
        write_model(directory, materials, meshes, images)
        with (directory / "animation.bin").open("wb") as stream:
            stream.write(b"GH3ANM01" + struct.pack("<II", len(names), len(clip_names)))
            for i, parent in enumerate(parents):
                stream.write(struct.pack("<i7f", parent, *(positions[i] * [1, 1, -1]),
                                         *(rotations[i] * [1, 1, -1, 1])))
            for name in clip_names:
                clip = animation(clips[key("gh3_crowd_" + name)], len(names))
                frames = round(clip[0] * 30) + 1
                p, q = sample_animation(clip, positions, rotations, np.linspace(0, clip[0], frames))
                packed = np.concatenate((p * [1, 1, -1], q * [1, 1, -1, 1]), axis=2)
                if not np.isfinite(packed).all():
                    raise ValueError("Invalid crowd pose")
                stream.write(struct.pack("<fI", clip[0], frames))
                stream.write(packed.astype("<f4").tobytes())
        print(f"{model}: {len(names)} bones, {sum(m[1] for m in meshes)} vertices, {len(clip_names)} clips")
    hands = []
    for side, hand in (("left", "HandL"), ("right", "HandR")):
        for kind in ("clap", "lighter", "rock", "fist"):
            stem = f"models\\Real_Crowd\\Crowd_{hand}_{kind}"
            materials, meshes = stage.scene(global_entries[key(".skin"), key(stem + ".skin")], skinned=True)
            write_model(output / "hands" / f"{side}-{kind}", materials, meshes,
                        dict(stage.textures(global_entries[key(".tex"), key(stem + ".tex")])))
            hands.append(f"{side}-{kind}")
    spec = importlib.util.spec_from_file_location("prepare_direction", Path(__file__).with_name("prepare-direction.py"))
    direction = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(direction)
    performance_clips = {ref: direction.write_clip(output / "clips" / f"{ref}.bin.gz", clips[int(ref, 16)],
        (names, parents, positions, rotations)) for ref in sorted({ref for member in members.values() for refs in member["groups"].values() for ref in refs})}
    constants = {}
    for filename, names_to_read in (("guitar_animation_data", ["slow_tempo", "med_tempo", "fast_tempo"]),
        ("guitar_animation_data_crowd", ["Crowd_BlendTime_TempoChange", "Crowd_BlendTime_Bad", "Crowd_BlendTime_Special",
                                      "Crowd_BlendTime_Slow", "Crowd_BlendTime_Med", "Crowd_BlendTime_Fast"])):
        for name in names_to_read:
            value = scalar(script(scripts, filename), name, None)
            if value is None:
                value = next((value for blob in scripts.values() if (value := scalar(blob, name, None)) is not None), None)
            if value is None: raise ValueError(f"Missing crowd constant: {name}")
            constants[name] = value
    sources.update(source.hashes)
    (output / "crowd.json").write_text(json.dumps({
        "version": 2, "models": [model for model, _ in MODELS], "members": members,
        "bones": [f"{name:08x}" for name in names], "hands": hands, "performanceClips": performance_clips, "constants": constants,
        "clips": {model: ["gh3_crowd_" + name for name in clips] for model, clips in MODELS},
        "sources": sources,
    }, indent=2) + "\n")
    print(output)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data", type=Path, default=Path.home() / "games/gh3-test-tools/game/DATA")
    parser.add_argument("--output", type=Path, default=Path(__file__).parent / "assets/libraries/gh3-band-v1/crowd")
    args = parser.parse_args()
    convert(args.data, args.output)
