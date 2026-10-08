#!/usr/bin/env python3
"""Export GH3's gameplay message font (Note Streak!, Star Power Ready) as an atlas and glyph table."""

import json
import struct
from io import BytesIO
from pathlib import Path

from PIL import Image

from gh3_direction import Source

FONT_TYPE = 0x7E1ABC70
FONT_NAME = 0xDBCE7067
CHARACTER_TABLE = 0x14
GLYPHS = 0x200A4


def convert(data, output):
    blob = next(b for k, n, b in Source(data).load("ZONES/global.pak.xen") if k == FONT_TYPE and n == FONT_NAME)
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
    output.mkdir(parents=True, exist_ok=True)
    atlas.save(output / "message-font.png")
    (output / "message-font.json").write_text(json.dumps({"version": 1, "lineHeight": struct.unpack_from(">f", blob, 0xC)[0],
        "atlas": list(atlas.size), "glyphs": glyphs}, indent=1) + "\n")
    print(f"Exported {len(glyphs)} message glyphs")


if __name__ == "__main__":
    convert(Path.home() / "games/gh3-test-tools/game/DATA", Path(__file__).parent / "assets")
