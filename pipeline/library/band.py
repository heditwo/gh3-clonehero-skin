"""Convert the owner's GH3 band models and original performance animations."""

import json

import numpy as np

from pipeline.gh3.band_formats import (
    animation,
    multiply_quaternions,
    sample_animation,
    sample_track,
    skeleton,
    solve_guitar_arms,
)
from pipeline.gh3.data import Source
from pipeline.gh3.formats import find_array, is_qb_file, key
from pipeline.gh3.hair_mesh import combine_hair
from pipeline.gh3.mesh_cutout import trim_prop_cutouts
from pipeline.library.models import read_models
from pipeline.package import runtime

BAND = [
    (
        "singer",
        "Singer_Male",
        "GH3_singer_male",
        "Microphone001",
        "Vocalist",
        ["gh3_singer_male_med_singidle2", "gh3_singer_male_fast_singidle3"],
        "Singer_Male_Anims",
    ),
    (
        "guitarist",
        "axel_1",
        "GH3_guitarist_axel",
        "SG_Black",
        "Guitarist",
        ["gh3_guit_axel_a_fst_idle1", "gh3_guit_axel_a_med_idle2"],
        "axel_1_anims",
    ),
    (
        "bassist",
        "Bassist",
        "GH3_bassist",
        "tobB4",
        "Bassist",
        ["gh3_bassist_a_med_idle01", "gh3_bassist_a_fst_idle02"],
        "Bassist_anims",
    ),
    (
        "drummer",
        "Drummer",
        "GH3_drummer",
        "Drum_Set001",
        "Drummer",
        ["gh3_drummer_med_idle_01", "gh3_drummer_fst_idle_01"],
        "Drummer_anims",
    ),
]


# Overlays of the library start clips: the guitar hand clips (chord, fret, and strum layers of the GH3 arm tree) and
# the drummer hits, so the rest pose plays.
GUITAR_CHORD_CLIP = "gh3_hnd_guit_chord_med_e_d"
GUITAR_FRET_CLIP = "gh3_hnd_guit_fret_med_05_d"
STRUM_CLIPS = {"guitarist": "gh3_hnd_guit_strum_norm_s01_d", "bassist": "gh3_hnd_bass_strum_pick_01_d"}
SINGER_FACE_CLIP = "gh3_singer_male_face_idle_1"
# GH3_singer_male bones 68–97 are the face bones (the jaw is found by name).
SINGER_FACE_BONES = range(68, 98)
# (clip, period in seconds, phase in seconds) of the drum hits.
DRUM_HITS = [("test_drum_hithat_r", 0.5, 0), ("test_drum_hitsnare_l", 1, 0.5)]
NO_WAYPOINT = {key("Pos"): [0, 0, 0], key("Angles"): [0, 0, 0]}


def _start_clip(clip_data, role, names, parents, positions, rotations, hands, clips):
    """(duration, poses) of one start clip with the hand, face, or drum overlays of the role (Unity space)."""
    clip = animation(clip_data, len(names))
    duration = clip[0]
    frame_count, times = runtime.sample_times(duration)
    p, q = sample_animation(clip, positions, rotations, times)

    def overlay(layer, clock, additive=False):
        lp, lq = sample_animation(layer, positions, rotations, clock)
        for bone in layer[1]:
            q[:, bone] = multiply_quaternions(q[:, bone], lq[:, bone]) if additive else lq[:, bone]
        for bone in layer[2]:
            p[:, bone] = p[:, bone] + lp[:, bone] - positions[bone] if additive else lp[:, bone]

    if role in ("guitarist", "bassist"):
        chord = animation(hands[key(GUITAR_CHORD_CLIP)], len(names))
        fret = animation(hands[key(GUITAR_FRET_CLIP)], len(names))
        strum = animation(hands[key(STRUM_CLIPS[role])], len(names))
        overlay(chord, np.zeros(len(times)), True)
        overlay(fret, np.zeros(len(times)), True)
        overlay(strum, times % strum[0], True)
        solve_guitar_arms(parents, p, q)
    if role == "singer":
        face = animation(clips[key(SINGER_FACE_CLIP)], len(names))
        for bone, frames in face[1].items():
            if key("bone_jaw") == names[bone] or bone in SINGER_FACE_BONES:
                q[:, bone] = sample_track(frames, times % face[0], True)
    if role == "drummer":
        for hit_name, period, offset in DRUM_HITS:
            hit = animation(clips[key(hit_name)], len(names))
            overlay(hit, ((times + offset) % period) / period * hit[0])
    packed = np.concatenate((p * [1, 1, -1], q * [1, 1, -1, 1]), axis=2)
    if not np.isfinite(packed).all():
        raise ValueError("Invalid sampled pose")
    return duration, packed


def _budokan_nodes(source):
    nodes = next(
        array
        for _, _, data in source.load("ZONES/z_budokan.pak.xen")
        if is_qb_file(data)
        if (array := find_array(data, "z_budokan_NodeArray")) is not None
    )
    return {node[key("Name")]: node for node in nodes if key("Name") in node}


def convert(data_dir, output, shared=False):
    """Export the four band members (model folders with their start clips) and band.json.

    shared: the library build; the members stand at the origin (the venues place them), else at the Budokan waypoints.
    """
    source = Source(data_dir)
    rigs = {name: data for kind, name, data in source.load("ZONES/global.pak.xen") if kind == key(".ske")}
    hands = {name: data for kind, name, data in source.load("PAK/perm_anims.pak.xen") if kind == key(".ska")}
    nodes = {} if shared else _budokan_nodes(source)
    members = []
    for role, model, rig, instrument, waypoint, clip_names, anim_pak in BAND:
        directory = output / role
        (directory / "textures").mkdir(parents=True, exist_ok=True)
        names, parents, positions, rotations = skeleton(rigs[key(rig)])
        paks = [f"PAK/{asset}.pak.xen" for asset in [model, *([instrument] if instrument else [])]]
        materials, meshes = read_models(source, paks, directory / "textures", "Band material ID collision")
        if role in ("singer", "drummer"):
            meshes = trim_prop_cutouts(meshes, materials, directory / "textures")
        meshes = combine_hair(meshes, materials, directory / "textures")
        runtime.write_model(directory, model, materials, meshes)
        clips = {name: data for kind, name, data in source.load(f"PAK/{anim_pak}.pak.xen") if kind == key(".ska")}
        bind = np.concatenate((positions * [1, 1, -1], rotations * [1, 1, -1, 1]), axis=1)
        start_clips = [
            _start_clip(clips[key(clip_name)], role, names, parents, positions, rotations, hands, clips)
            for clip_name in clip_names
        ]
        (directory / "animation.bin").write_bytes(runtime.write_animation(parents, bind, start_clips))
        node = nodes.get(key(f"Z_Budokan_TRG_Waypoint_{waypoint}_Start"), NO_WAYPOINT)
        x, y, z = node[key("Pos")]
        members.append(
            {
                "role": role,
                "model": model,
                "instrument": instrument,
                "position": [x, y, -z],
                "yaw": -node[key("Angles")][1] * 180 / np.pi,
                "clips": clip_names,
                "bones": len(names),
                "vertices": sum(m[1] for m in meshes),
            }
        )
        print(f"{role}: {model}, {instrument or 'venue drum kit'}, {len(names)} bones, {len(meshes)} meshes")
    (output / "band.json").write_text(
        json.dumps({"version": 1, "members": members, "sources": source.hashes}, indent=2) + "\n"
    )
    print(output)
