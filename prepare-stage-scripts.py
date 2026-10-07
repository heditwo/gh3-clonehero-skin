#!/usr/bin/env python3
"""Convert the owner's original global script library into typed JSON."""

import argparse
from pathlib import Path
from io import BytesIO
from PIL import Image
import importlib.util
import numpy as np

from gh3_direction import Source
from gh3_script_bank import definitions, references
from gh3_formats import key
from stage_pack import write_json


def decode_image(blob, texture):
    start = blob.find(b"DDS ")
    if start < 0: raise ValueError(f"Script image has no DDS: {texture:08x}")
    data = blob[start:]
    premultiplied = data[84:88] in (b"DXT2", b"DXT4")
    if premultiplied: data = data[:84] + (b"DXT3" if data[84:88] == b"DXT2" else b"DXT5") + data[88:]
    image = Image.open(BytesIO(data)).convert("RGBA")
    if premultiplied:
        pixels = np.asarray(image).astype(np.float32)
        pixels[:, :, :3] *= 255 / np.maximum(pixels[:, :, 3:4], 1)
        image = Image.fromarray(np.clip(np.rint(pixels), 0, 255).astype(np.uint8))
    return image


def convert(data, output):
    source = Source(data)
    scripts = source.scripts()
    bank = definitions(scripts.values())
    output.parent.mkdir(parents=True, exist_ok=True)
    images = {name: blob for kind, name, blob in source.load("ZONES/global.pak.xen") if kind == key(".img")}
    textures = {}
    def scan(value):
        if value[0] == 10:
            for name, item in value[1]:
                if name == f"0x{key('DiffuseTexture'):08x}" and item[0] == 13:
                    texture = int(item[1], 16)
                    if texture not in images: continue
                    file = f"particle-textures/{texture:08x}.png"
                    (output.parent/file).parent.mkdir(parents=True, exist_ok=True)
                    decode_image(images[texture], texture).save(output.parent/file)
                    textures[f"0x{texture:08x}"] = file
                scan(item)
        elif value[0] == 12:
            for item in value[1]: scan(item)
    for value in bank.values():
        if value[0] == 10 and any(name == f"0x{key('QuickMaterial'):08x}" for name, _ in value[1]): scan(value)
    referenced = {reference for items in references(scripts.values()).values() for reference in items}
    for texture in sorted(referenced & images.keys()):
        name = f"0x{texture:08x}"
        if name in textures: continue
        file = f"particle-textures/{texture:08x}.png"
        (output.parent/file).parent.mkdir(parents=True, exist_ok=True)
        decode_image(images[texture], texture).save(output.parent/file)
        textures[name] = file
    spec = importlib.util.spec_from_file_location("stage_movies", Path(__file__).with_name("prepare-stage-movies.py"))
    movies = importlib.util.module_from_spec(spec); spec.loader.exec_module(movies)
    movie_files = {}
    for path in sorted((Path(data)/"MOVIES/BIK").glob("video_shoot_*_xen.bik.xen")):
        name = path.name.removesuffix(".bik.xen"); file = f"movies/{name}.gh3video"
        movies.convert(path, output.parent/file)
        movie_files[name] = file
    write_json(output, {"version": 1, "definitions": bank, "startup": [], "textures": textures, "movies": movie_files})
    print(f"Retained {len(bank)} typed global definitions: {output}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data", type=Path, default=Path.home()/"games/gh3-test-tools/game/DATA")
    parser.add_argument("--output", type=Path, default=Path("assets/libraries/gh3-band-v1/scripts.json"))
    args = parser.parse_args()
    convert(args.data, args.output)
