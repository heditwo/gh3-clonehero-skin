#!/usr/bin/env python3
"""Export GH3's gameplay fonts as atlases and glyph tables: the message font (Note Streak!, Star Power Ready),
the note-streak counter digits (num_a7), and the green score digits (num_a9). Also exports the HUD sprites that
the gameplay HUD scripts draw next to that text."""

import json
import struct
from io import BytesIO
from pathlib import Path

from PIL import Image

from gh3_direction import Source
from gh3_formats import key

FONT_TYPE = 0x7E1ABC70
CHARACTER_TABLE = 0x14
GLYPHS = 0x200A4
FONTS = {"text_a6": "message-font", "num_a7": "counter-font", "num_a9": "score-font"}
# hud_glowburst_alert's light streak.
SPRITES = {"Char_Select_Hilite1": "hud-glowburst"}


def export(blob, output, name):
    count = struct.unpack_from(">H", blob, 0x12)[0]
    dds = bytearray(blob[blob.index(b"DDS "):])
    if dds[84:88] == b"DXT2": dds[84:88] = b"DXT3"
    if dds[84:88] == b"DXT4": dds[84:88] = b"DXT5"
    atlas = Image.open(BytesIO(bytes(dds))).convert("RGBA")
    glyphs = {}
    for code in range(32, 256):
        index = struct.unpack_from(">H", blob, CHARACTER_TABLE + 2 * code)[0]
        if code != 32 and index == 0: continue
        if index >= count: raise ValueError("Font glyph index is out of range")
        u0, v0, u1, v1, width, height, x, y, _ = struct.unpack_from(">9f", blob, GLYPHS + index * 36)
        glyphs[chr(code)] = {"uv": [u0, v0, u1, v1], "size": [width, height], "offset": [x, y]}
    # The space maps to glyph 0; GH3 draws nothing and advances by a narrow fixed width.
    glyphs[" "] = {"uv": [0, 0, 0, 0], "size": [14.0, 0.0], "offset": [0.0, 0.0]}
    atlas.save(output / f"{name}.png")
    (output / f"{name}.json").write_text(json.dumps({"version": 1, "lineHeight": struct.unpack_from(">f", blob, 0xC)[0],
        "baseline": struct.unpack_from(">I", blob, 0)[0], "atlas": list(atlas.size), "glyphs": glyphs}, indent=1) + "\n")
    print(f"Exported {len(glyphs)} glyphs to {name}")


def convert(data, output):
    output.mkdir(parents=True, exist_ok=True)
    names = {key(font): name for font, name in FONTS.items()}
    sprites = {key(sprite): name for sprite, name in SPRITES.items()}
    found, images = {}, {}
    for kind, name, blob in Source(data).load("ZONES/global.pak.xen"):
        if kind == FONT_TYPE and name in names: found[names[name]] = blob
        if kind == key(".img") and name in sprites: images[sprites[name]] = blob
    if len(found) != len(FONTS): raise ValueError(f"Missing GH3 fonts: {set(FONTS.values()) - set(found)}")
    if len(images) != len(SPRITES): raise ValueError(f"Missing GH3 HUD sprites: {set(SPRITES.values()) - set(images)}")
    for name, blob in found.items(): export(blob, output, name)
    for name, blob in images.items():
        Image.open(BytesIO(blob[blob.index(b"DDS "):])).convert("RGBA").save(output / f"{name}.png")
        print(f"Exported {name}")


if __name__ == "__main__":
    convert(Path.home() / "games/gh3-test-tools/game/DATA", Path(__file__).parent / "assets")
