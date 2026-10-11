"""Convert the owner's GH3 crowd models and original cheering animations."""

import json

import numpy as np

from pipeline.gh3 import scene
from pipeline.gh3.band_formats import animation, sample_animation, skeleton
from pipeline.gh3.data import Source, scalar, script
from pipeline.gh3.formats import find_struct, key
from pipeline.library import direction
from pipeline.package import runtime

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
                    if not refs or any(type(ref) is not int for ref in refs):
                        raise ValueError("Invalid crowd animation references")
                    groups[f"{action}/{skill}/{speed}"] = [f"{ref:08x}" for ref in refs]
        members[f"crowd{index}"] = {"model": model, "groups": groups}
    return members


def write_model(directory, materials, meshes, images):
    (directory / "textures").mkdir(parents=True, exist_ok=True)
    for name, image in images.items():
        image.save(directory / "textures" / f"{name}.png")
    for material in materials.values():
        material["texture"] = scene.diffuse_texture(material, images)
    runtime.write_model(directory, directory.name, materials, meshes)


def convert(data_dir, output):
    # The model PAKs and the scripts have their own source hashes; crowd.json lists the PAKs first.
    pak_source = Source(data_dir)
    load = pak_source.load
    global_entries = {(kind, name): blob for kind, name, blob in load("ZONES/global.pak.xen")}
    rig = next(blob for (kind, name), blob in global_entries.items() if kind == key(".ske") and name == key("GH3_ped"))
    names, parents, positions, rotations = skeleton(rig)
    bind = np.concatenate((positions * [1, 1, -1], rotations * [1, 1, -1, 1]), axis=1)
    source = Source(data_dir)
    scripts = source.scripts()
    members = performance_data(script(scripts, "guitar_animation_data_crowd"))
    needed = {key("gh3_crowd_" + clip) for _, clips in MODELS for clip in clips}
    needed.update(int(ref, 16) for member in members.values() for refs in member["groups"].values() for ref in refs)
    clips = {
        name: blob for kind, name, blob in load("PAK/perm_anims.pak.xen") if kind == key(".ska") and name in needed
    }
    if clips.keys() != needed:
        raise ValueError("Missing original crowd animations")
    for model, clip_names in MODELS:
        directory = output / model
        materials, meshes, images, cubes = {}, [], {}, set()
        embedded = model in ("crowd_biker_01", "crowd_biker_02")
        archive = "ZONES/z_hell.pak.xen" if embedded else f"PAK/{model}.pak.xen"
        for kind, name, blob in load(archive):
            if embedded and name not in (
                key(f"models\\Real_Crowd\\{model}.skin"),
                key(f"models\\Real_Crowd\\{model}.tex"),
            ):
                continue
            if kind == key(".skin"):
                materials, meshes = scene.scene(blob, skinned=True)
            elif kind == key(".tex"):
                images.update(scene.textures(blob))
                cubes.update(scene.cube_names(blob))
        if not meshes:
            raise ValueError(f"Missing crowd model: {model}")
        scene.mark_cube_samplers(materials, cubes)
        write_model(directory, materials, meshes, images)
        start_clips = []
        for name in clip_names:
            clip = animation(clips[key("gh3_crowd_" + name)], len(names))
            _, times = runtime.sample_times(clip[0])
            p, q = sample_animation(clip, positions, rotations, times)
            packed = np.concatenate((p * [1, 1, -1], q * [1, 1, -1, 1]), axis=2)
            if not np.isfinite(packed).all():
                raise ValueError("Invalid crowd pose")
            start_clips.append((clip[0], packed))
        (directory / "animation.bin").write_bytes(runtime.write_animation(parents, bind, start_clips))
        print(f"{model}: {len(names)} bones, {sum(m[1] for m in meshes)} vertices, {len(clip_names)} clips")
    hands = []
    for side, hand in (("left", "HandL"), ("right", "HandR")):
        for kind in ("clap", "lighter", "rock", "fist"):
            stem = f"models\\Real_Crowd\\Crowd_{hand}_{kind}"
            materials, meshes = scene.scene(global_entries[key(".skin"), key(stem + ".skin")], skinned=True)
            write_model(
                output / "hands" / f"{side}-{kind}",
                materials,
                meshes,
                dict(scene.textures(global_entries[key(".tex"), key(stem + ".tex")])),
            )
            hands.append(f"{side}-{kind}")
    performance_clips = {
        ref: direction.write_clip(
            output / "clips" / f"{ref}.bin.gz", clips[int(ref, 16)], (names, parents, positions, rotations)
        )
        for ref in sorted({ref for member in members.values() for refs in member["groups"].values() for ref in refs})
    }
    constants = {}
    for filename, names_to_read in (
        ("guitar_animation_data", ["slow_tempo", "med_tempo", "fast_tempo"]),
        (
            "guitar_animation_data_crowd",
            [
                "Crowd_BlendTime_TempoChange",
                "Crowd_BlendTime_Bad",
                "Crowd_BlendTime_Special",
                "Crowd_BlendTime_Slow",
                "Crowd_BlendTime_Med",
                "Crowd_BlendTime_Fast",
            ],
        ),
    ):
        for name in names_to_read:
            value = scalar(script(scripts, filename), name, None)
            if value is None:
                value = next(
                    (value for blob in scripts.values() if (value := scalar(blob, name, None)) is not None), None
                )
            if value is None:
                raise ValueError(f"Missing crowd constant: {name}")
            constants[name] = value
    sources = {**pak_source.hashes, **source.hashes}
    (output / "crowd.json").write_text(
        json.dumps(
            {
                "version": 2,
                "models": [model for model, _ in MODELS],
                "members": members,
                "bones": [f"{name:08x}" for name in names],
                "hands": hands,
                "performanceClips": performance_clips,
                "constants": constants,
                "clips": {model: ["gh3_crowd_" + name for name in clips] for model, clips in MODELS},
                "sources": sources,
            },
            indent=2,
        )
        + "\n"
    )
    print(output)
