"""Extract GH3's note-hit, held-note, and Star Power bonus effect textures from global.pak.xen.

NoteFX, WhammyFXOn, and GuitarEvent_StarSequenceBonus draw these with the sys_* materials below (global_gfx.scn).
The script checks each material's texture, additive blend, and AnimatedTexture_UI constants before it writes the images.
"""

import argparse
from io import BytesIO
from pathlib import Path
from struct import unpack_from

from PIL import Image

from pipeline.gh3.data import Source
from pipeline.gh3.formats import key, values, word
from pipeline.paths import GH3_DATA, HIT_EFFECTS

# Material, texture, output, texture size, and (columns, rows, frames per second) for animated materials.
MATERIALS = [
    (
        "sys_note_hit01_sys_note_hit01",
        "tex\\models\\highway\\note_hit01.dds",
        "gh3-note-hit.png",
        (512, 1024),
        (4, 4, 60),
    ),
    ("sys_note_hit_blue_01_sys_note_hit_blue_01", 0x9E700D1C, "gh3-note-hit-star.png", (512, 1024), (4, 4, 60)),
    (
        "sys_whammy_hold01_sys_whammy_hold01",
        "tex\\models\\highway\\whammy_hold_01.dds",
        "gh3-whammy-hold.png",
        (512, 512),
        (4, 4, 30),
    ),
    (
        "sys_big_bolt01_sys_big_bolt01",
        "tex\\models\\highway\\big_lighning01.dds",
        "gh3-big-bolt.png",
        (1024, 512),
        (8, 1, 15),
    ),
    (
        "sys_particle_spark01_sys_particle_spark01",
        "tex\\models\\highway\\lil_spark.dds",
        "gh3-particle-spark.png",
        (32, 32),
        None,
    ),
    (
        "sys_particle_star01_sys_particle_star01",
        "tex\\models\\highway\\lil_star.dds",
        "gh3-particle-star.png",
        (64, 64),
        None,
    ),
    (
        "sys_particle_star02_sys_particle_star02",
        "tex\\models\\highway\\lil_star2.dds",
        "gh3-particle-star2.png",
        (64, 64),
        None,
    ),
]
ANIMATED_UI = key("AnimatedTexture_UI")
IMMEDIATE_UI = key("ImmediateMode_UI")


def materials(scene):
    count = values(scene, 34, "H")[0]
    offset = 48
    found = {}
    for _ in range(count):
        sample_count, sample_offset = values(scene, offset + 184, "II")
        constant_count, constant_offset = values(scene, offset + 168, "II")
        found[word(scene, offset)] = {
            "template": word(scene, offset + 160),
            "textures": values(scene, offset + sample_offset, f"{sample_count}I"),
            "blend": word(scene, offset + 216),
            "constants": [
                values(scene, offset + constant_offset + 16 * index, "f")[0] for index in range(constant_count)
            ],
        }
        offset += word(scene, offset + 196)
    return found


def textures(bundle):
    count, table = unpack_from(">H", bundle, 6)[0], unpack_from(">I", bundle, 8)[0]
    for index in range(count):
        header = table + 40 * index
        start, size = unpack_from(">II", bundle, header + 28)
        yield unpack_from(">I", bundle, header + 4)[0], bundle[start : start + size]


def convert(source, output):
    entries = source.load("ZONES/global.pak.xen")
    scene = next(data for kind, name, data in entries if kind == key(".scn") and len(data) > 10000)
    found = materials(scene)
    images = {}
    for kind, _, data in entries:
        if kind == key(".tex"):
            for name, image in textures(data):
                images.setdefault(name, image)
    output.mkdir(parents=True, exist_ok=True)
    for material_name, texture, file, size, animation in MATERIALS:
        material = found.get(key(material_name))
        texture_id = texture if isinstance(texture, int) else key(texture)
        if material is None or material["textures"] != (texture_id,) or material["blend"] != 1:
            raise ValueError(f"Unexpected GH3 effect material: {material_name}")
        if animation is None:
            if material["template"] != IMMEDIATE_UI:
                raise ValueError(f"{material_name} is not an ImmediateMode_UI material")
        elif (
            material["template"] != ANIMATED_UI
            or tuple(round(value) for value in material["constants"][:3]) != animation
        ):
            raise ValueError(f"{material_name} has an unexpected texture animation")
        image = Image.open(BytesIO(images[texture_id])).convert("RGBA")
        if image.size != size:
            raise ValueError(f"{file} has size {image.size}; expected {size}")
        image.save(output / file)
    print(f"Wrote {len(MATERIALS)} GH3 hit-effect textures to {output}")


def main():
    parser = argparse.ArgumentParser(prog="python -m pipeline.hud.hit_effects", description=__doc__)
    parser.add_argument("--data", type=Path, default=GH3_DATA)
    parser.add_argument("--output", type=Path, default=HIT_EFFECTS)
    arguments = parser.parse_args()
    convert(Source(arguments.data), arguments.output)


if __name__ == "__main__":
    main()
