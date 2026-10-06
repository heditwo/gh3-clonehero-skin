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
from gh3_formats import key, pak_entries

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
]


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

    rig = next(blob for kind, name, blob in load("ZONES/global.pak.xen")
               if kind == key(".ske") and name == key("GH3_ped"))
    names, parents, positions, rotations = skeleton(rig)
    needed = {key("gh3_crowd_" + clip) for _, clips in MODELS for clip in clips}
    clips = {name: blob for kind, name, blob in load("PAK/perm_anims.pak.xen")
             if kind == key(".ska") and name in needed}
    if clips.keys() != needed:
        raise ValueError("Missing original crowd animations")
    for model, clip_names in MODELS:
        directory = output / model
        (directory / "textures").mkdir(parents=True, exist_ok=True)
        materials, meshes, available = {}, [], set()
        for kind, _, blob in load(f"PAK/{model}.pak.xen"):
            if kind == key(".skin"):
                materials, meshes = stage.scene(blob, skinned=True)
            elif kind == key(".tex"):
                for name, image in stage.textures(blob):
                    image.save(directory / "textures" / f"{name}.png")
                    available.add(name)
        if not meshes:
            raise ValueError(f"Missing crowd model: {model}")
        for material in materials.values():
            material["texture"] = stage.diffuse_texture(material, available)
        with (directory / "meshes.bin").open("wb") as stream:
            stream.write(b"GH3BND01" + struct.pack("<I", len(meshes)))
            for material, count, vertices, faces in meshes:
                stream.write(struct.pack("<III", int(material, 16), count, len(faces) * 3))
                stream.write(vertices)
                for face in faces:
                    stream.write(struct.pack("<3I", *face))
        (directory / "stage.json").write_text(json.dumps({
            "version": 1, "name": model, "cameraPosition": [0, 0, 0], "cameraTarget": [0, 1, 0],
            "materials": materials,
        }, indent=2) + "\n")
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
    (output / "crowd.json").write_text(json.dumps({
        "version": 1, "models": [model for model, _ in MODELS],
        "clips": {model: ["gh3_crowd_" + name for name in clips] for model, clips in MODELS},
        "sources": sources,
    }, indent=2) + "\n")
    print(output)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data", type=Path, default=Path.home() / "games/gh3-test-tools/game/DATA")
    parser.add_argument("--output", type=Path, default=Path(__file__).parent / "assets/stages/budokan/crowd")
    args = parser.parse_args()
    convert(args.data, args.output)
