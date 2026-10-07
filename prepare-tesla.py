#!/usr/bin/env python3
"""Export the original GH3 Tesla material and noise texture."""

import argparse
from hashlib import sha256
from io import BytesIO
import importlib.util
from pathlib import Path

from PIL import Image

from gh3_direction import Source
from gh3_formats import key, values, word
from stage_pack import load_json, write_json


def convert(data, output):
    spec = importlib.util.spec_from_file_location('stage', Path(__file__).with_name('prepare-stage.py'))
    stage = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(stage)
    entries = Source(data).load('ZONES/global.pak.xen')
    materials = []
    for kind, name, blob in entries:
        if kind != key('.scn'): continue
        material = stage.scene(blob)[0].get('01488227')
        if material is not None: materials.append(material)
    if len(materials) != 1 or materials[0]['template'] != 'd7287426' or materials[0]['textures'] != ['e936482c']:
        raise ValueError('The source Tesla material is unknown')
    images = []
    for kind, name, blob in entries:
        if kind != key('.tex'): continue
        for index in range(values(blob, 6, 'H')[0]):
            header = word(blob, 8) + index * 40
            if word(blob, header + 4) != 0xe936482c: continue
            start, size = values(blob, header + 28, 'II')
            images.append(blob[start:start+size])
    if len(images) != 1: raise ValueError('The source Tesla noise texture is missing or duplicated')
    output.mkdir(parents=True, exist_ok=True)
    image = Image.open(BytesIO(images[0])).convert('RGBA')
    texture = output/'tesla-noise.png'
    image.save(texture)
    write_json(output/'tesla-material.json', {'material': '01488227', 'shader': 'd7287426', 'texture': 'e936482c',
        'sourceSha256': sha256(images[0]).hexdigest(), 'imageSha256': sha256(texture.read_bytes()).hexdigest(), 'size': list(image.size)})
    library = output.parent/'library.json'
    if library.is_file():
        manifest = load_json(library)
        for path in (texture, output/'tesla-material.json'):
            manifest['files'][str(path.relative_to(library.parent))] = sha256(path.read_bytes()).hexdigest()
        write_json(library, manifest)
    print(f'Tesla noise texture: {image.size[0]} x {image.size[1]} pixels, {texture}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--data', type=Path, default=Path.home()/'games/gh3-test-tools/game/DATA')
    parser.add_argument('--output', type=Path, default=Path('assets/libraries/gh3-band-v1/render'))
    args = parser.parse_args()
    convert(args.data, args.output)
