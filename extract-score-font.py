from io import BytesIO
from pathlib import Path
from struct import unpack_from
from zipfile import ZipFile

from PIL import Image


archive = Path.home() / "games/BetterGH3-FULLPACKAGE_v1.0.2.zip"
root = "Guitar Hero III/DATA/ZONES/global."
with ZipFile(archive) as game:
    pak = game.read(root + "pak.xen")
    pab = game.read(root + "pab.xen")

for entry, name, dimensions in (
    (25, "gh3-counter-font.png", (256, 32)),
    (27, "gh3-score-font.png", (1024, 128)),
):
    kind, offset, size = unpack_from(">III", pak, entry * 32)
    if kind != 0x7E1ABC70:
        raise ValueError(f"The GH3 font entry {entry} is not present")

    start = entry * 32 + offset - len(pak)
    font_data = pab[start:start + size]
    dds = bytearray(font_data[font_data.index(b"DDS "):])
    if dds[84:88] not in (b"DXT2", b"DXT5"):
        raise ValueError(f"Unexpected GH3 font texture format at entry {entry}")
    if dds[84:88] == b"DXT2":
        dds[84:88] = b"DXT3"

    font = Image.open(BytesIO(dds)).convert("RGBA")
    if font.size != dimensions:
        raise ValueError(f"Unexpected GH3 font dimensions at entry {entry}")
    output = Path(__file__).parent / "references" / name
    font.save(output)
    print(f"Extracted {output}")
