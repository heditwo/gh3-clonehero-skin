#!/usr/bin/env python3
"""Convert the owner's GH3 band models and original performance animations."""

import argparse
from hashlib import sha256
import importlib.util
import json
from pathlib import Path
import struct

import numpy as np
from gh3_band_formats import animation, multiply_quaternions, sample_animation, sample_track, skeleton, solve_guitar_arms
from gh3_formats import find_array, key, pak_entries
from gh3_mesh_cutout import trim_prop_cutouts
from gh3_hair_mesh import combine_hair

spec = importlib.util.spec_from_file_location("prepare_stage", Path(__file__).with_name("prepare-stage.py"))
stage = importlib.util.module_from_spec(spec)
spec.loader.exec_module(stage)

BAND = [
    ("singer", "Singer_Male", "GH3_singer_male", "Microphone001", "Vocalist",
     ["gh3_singer_male_med_singidle2", "gh3_singer_male_fast_singidle3"], "Singer_Male_Anims"),
    ("guitarist", "axel_1", "GH3_guitarist_axel", "SG_Black", "Guitarist",
     ["gh3_guit_axel_a_fst_idle1", "gh3_guit_axel_a_med_idle2"], "axel_1_anims"),
    ("bassist", "Bassist", "GH3_bassist", "tobB4", "Bassist",
     ["gh3_bassist_a_med_idle01", "gh3_bassist_a_fst_idle02"], "Bassist_anims"),
    ("drummer", "Drummer", "GH3_drummer", "Drum_Set001", "Drummer",
     ["gh3_drummer_med_idle_01", "gh3_drummer_fst_idle_01"], "Drummer_anims"),
]


def convert(data_dir, output, shared=False):
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
        return list(pak_entries(data))

    rigs = {name: data for kind, name, data in load("ZONES/global.pak.xen") if kind == key(".ske")}
    hands = {name: data for kind, name, data in load("PAK/perm_anims.pak.xen") if kind == key(".ska")}
    nodes = {}
    if not shared:
        venue = load("ZONES/z_budokan.pak.xen")
        nodes = next(array for _, _, data in venue if len(data) > 28 and data[8:12] == bytes.fromhex("1c080204")
                     if (array := find_array(data, "z_budokan_NodeArray")) is not None)
        nodes = {node[key("Name")]: node for node in nodes if key("Name") in node}
    members = []
    for role, model, rig, instrument, waypoint, clip_names, anim_pak in BAND:
        directory = output / role
        (directory / "textures").mkdir(parents=True, exist_ok=True)
        names, parents, positions, rotations = skeleton(rigs[key(rig)])
        materials, meshes, available, cubes = {}, [], set(), set()
        for asset in [model, *([instrument] if instrument else [])]:
            for kind, _, data in load(f"PAK/{asset}.pak.xen"):
                if kind == key(".skin"):
                    mat, parts = stage.scene(data, skinned=True)
                    if any(materials[name] != mat[name] for name in materials.keys() & mat.keys()):
                        raise ValueError("Band material ID collision")
                    materials.update(mat)
                    meshes.extend(parts)
                elif kind == key(".tex"):
                    cubes.update(stage.cube_names(data))
                    for name, image in stage.textures(data):
                        image.save(directory / "textures" / f"{name}.png")
                        available.add(name)
        for mat in materials.values():
            mat["texture"] = stage.diffuse_texture(mat, available)
        stage.mark_cube_samplers(materials, cubes)
        if role in ("singer", "drummer"):
            meshes = trim_prop_cutouts(meshes, materials, directory / "textures")
        meshes = combine_hair(meshes, materials, directory / "textures")
        with (directory / "meshes.bin").open("wb") as stream:
            stream.write(b"GH3BND02" + struct.pack("<I", len(meshes)))
            for material, count, vertices, faces in meshes:
                stream.write(struct.pack("<III", int(material, 16), count, len(faces) * 3))
                stream.write(vertices)
                for face in faces:
                    stream.write(struct.pack("<3I", *face))
        (directory / "stage.json").write_text(json.dumps({
            "version": 1, "name": model, "cameraPosition": [0, 0, 0], "cameraTarget": [0, 1, 0],
            "materials": materials,
        }, indent=2) + "\n")
        clips = {name: data for kind, name, data in load(f"PAK/{anim_pak}.pak.xen") if kind == key(".ska")}
        with (directory / "animation.bin").open("wb") as stream:
            stream.write(b"GH3ANM01" + struct.pack("<II", len(names), len(clip_names)))
            for i, parent in enumerate(parents):
                stream.write(struct.pack("<i7f", parent, *(positions[i] * [1, 1, -1]),
                                         *(rotations[i] * [1, 1, -1, 1])))
            for clip_name in clip_names:
                clip = animation(clips[key(clip_name)], len(names))
                duration = clip[0]
                frame_count = round(duration * 30) + 1
                times = np.linspace(0, duration, frame_count)
                p, q = sample_animation(clip, positions, rotations, times)

                def overlay(layer, clock, additive=False):
                    lp, lq = sample_animation(layer, positions, rotations, clock)
                    for bone in layer[1]:
                        q[:, bone] = multiply_quaternions(q[:, bone], lq[:, bone]) if additive else lq[:, bone]
                    for bone in layer[2]:
                        p[:, bone] = p[:, bone] + lp[:, bone] - positions[bone] if additive else lp[:, bone]

                if role in ("guitarist", "bassist"):
                    chord = animation(hands[key("gh3_hnd_guit_chord_med_e_d")], len(names))
                    fret = animation(hands[key("gh3_hnd_guit_fret_med_05_d")], len(names))
                    strum_name = "gh3_hnd_guit_strum_norm_s01_d" if role == "guitarist" else "gh3_hnd_bass_strum_pick_01_d"
                    strum = animation(hands[key(strum_name)], len(names))
                    overlay(chord, np.zeros(len(times)), True)
                    overlay(fret, np.zeros(len(times)), True)
                    overlay(strum, times % strum[0], True)
                    solve_guitar_arms(parents, p, q)
                if role == "singer":
                    face = animation(clips[key("gh3_singer_male_face_idle_1")], len(names))
                    for bone, frames in face[1].items():
                        if key("bone_jaw") == names[bone] or 68 <= bone <= 97:
                            q[:, bone] = sample_track(frames, times % face[0], True)
                if role == "drummer":
                    for hit_name, period, offset in [("test_drum_hithat_r", 0.5, 0), ("test_drum_hitsnare_l", 1, 0.5)]:
                        hit = animation(clips[key(hit_name)], len(names))
                        overlay(hit, ((times + offset) % period) / period * hit[0])
                stream.write(struct.pack("<fI", duration, frame_count))
                packed = np.concatenate((p * [1, 1, -1], q * [1, 1, -1, 1]), axis=2)
                if not np.isfinite(packed).all():
                    raise ValueError("Invalid sampled pose")
                stream.write(packed.astype("<f4").tobytes())
        node = nodes.get(key(f"Z_Budokan_TRG_Waypoint_{waypoint}_Start"), {key("Pos"): [0, 0, 0], key("Angles"): [0, 0, 0]})
        x, y, z = node[key("Pos")]
        members.append({"role": role, "model": model, "instrument": instrument,
                        "position": [x, y, -z], "yaw": -node[key("Angles")][1] * 180 / np.pi,
                        "clips": clip_names, "bones": len(names), "vertices": sum(m[1] for m in meshes)})
        print(f"{role}: {model}, {instrument or 'venue drum kit'}, {len(names)} bones, {len(meshes)} meshes")
    (output / "band.json").write_text(json.dumps({"version": 1, "members": members, "sources": sources}, indent=2) + "\n")
    print(output)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data", type=Path, default=Path.home() / "games/gh3-test-tools/game/DATA")
    parser.add_argument("--output", type=Path, default=Path(__file__).parent / "assets/stages/budokan/band")
    args = parser.parse_args()
    convert(args.data, args.output)
