import sys
from pathlib import Path
from io import BytesIO
from struct import unpack_from
from zipfile import ZipFile
from zlib import crc32
import shutil
from PIL import Image

source = Path.home() / "games/gh3-skin-assets"
target = Path(__file__).parent / "assets"
target.mkdir(exist_ok=True)
Image.new("RGBA", (4, 4)).save(target / "clear.png")

notes = {
    "green": (26, 87),
    "red": (58, 85),
    "yellow": (51, 23),
    "blue": (60, 12),
    "orange": (78, 94),
    "active": (39, 27),
}
for lane, variants in notes.items():
    for variant, index in zip(("note", "glow"), variants):
        file = next((source / "global-gfx-png").glob(f"{index:03d}_*.png"))
        Image.open(file).convert("RGBA").save(target / f"{lane}-{variant}.png")

star_notes = {
    "green": (55, 38),
    "red": (31, 96),
    "yellow": (7, 97),
    "blue": (84, 24),
    "orange": (0, 1),
}
for lane, (regular, glow) in star_notes.items():
    for variant, index in (("star", regular), ("star-glow", glow)):
        sheet = Image.open(next((source / "global-gfx-png").glob(f"{index:03d}_*.png"))).convert("RGBA")
        for frame in range(16):
            sheet.crop(((frame % 4) * 128, (frame // 4) * 64, (frame % 4 + 1) * 128, (frame // 4 + 1) * 64)).save(target / f"{lane}-{variant}-{frame}.png")

def texture_images(bundle):
    count = unpack_from(">H", bundle, 6)[0]
    table = unpack_from(">I", bundle, 8)[0]
    images = {}
    for entry in range(count):
        header = table + entry * 40
        key = unpack_from(">I", bundle, header + 4)[0]
        offset, size = unpack_from(">II", bundle, header + 28)
        images[key] = bundle[offset:offset + size]
    return images


with ZipFile(Path.home() / "games/BetterGH3-FULLPACKAGE_v1.0.2.zip") as archive:
    pak = archive.read("Guitar Hero III/DATA/ZONES/_DONOTDELETE.pak.xen")
    pab = archive.read("Guitar Hero III/DATA/ZONES/_DONOTDELETE.pab.xen")
texture_type, offset, size = unpack_from(">III", pak, 2 * 32)
if texture_type != 0x8BFA5E8E:
    raise ValueError("The BetterGH3 tap texture bundle is not present")
start = 2 * 32 + offset - len(pak)
bundle = pab[start:start + size]
tap_images = texture_images(bundle)
for lane in ("green", "red", "yellow", "blue", "orange", "starpower"):
    for phrase in (False, True) if lane != "starpower" else (False,):
        name = f"images\\highway\\tap2d_{lane}{'_star' if phrase else ''}.img"
        image = Image.open(BytesIO(tap_images[~crc32(name.encode()) & 0xFFFFFFFF])).convert("RGBA")
        if image.size != ((512, 256) if phrase else (128, 64)):
            raise ValueError(f"Unexpected BetterGH3 tap dimensions: {name}")
        if phrase:
            for frame in range(16):
                image.crop(((frame % 4) * 128, (frame // 4) * 64, (frame % 4 + 1) * 128, (frame // 4 + 1) * 64)).save(target / f"{lane}-star-tap-{frame}.png")
        else:
            image.save(target / f"{'active' if lane == 'starpower' else lane}-tap.png")

with ZipFile(Path.home() / "games/gh3-test-tools/FastGH3_1.1.zip") as archive:
    pak = archive.read("DATA/ZONES/global.pak.xen")
    pab = archive.read("DATA/ZONES/global.pab.xen")
entry = next(entry for entry in range(0, len(pak) - 31, 32) if unpack_from(">I", pak, entry)[0] == 0x8BFA5E8E)
offset, size = unpack_from(">II", pak, entry + 4)
start = entry + offset - len(pak)
active_images = texture_images(pab[start:start + size])
for variant, source_name in (("star", "star2d_star"), ("star-glow", "star2d_star_hammer"), ("star-tap", "star2d_star_tap")):
    name = f"images\\highway\\{source_name}.img"
    sheet = Image.open(BytesIO(active_images[~crc32(name.encode()) & 0xFFFFFFFF])).convert("RGBA")
    if sheet.size != (512, 256):
        raise ValueError(f"Unexpected FastGH3 active phrase dimensions: {name}")
    for frame in range(16):
        sheet.crop(((frame % 4) * 128, (frame // 4) * 64, (frame % 4 + 1) * 128, (frame // 4 + 1) * 64)).save(target / f"active-{variant}-{frame}.png")

for name in ("HUD_score_body", "HUD_score_nixie_1a", "HUD_score_nixie_2a", "HUD_score_nixie_3a", "HUD_score_nixie_4a", "HUD_score_nixie_2b", "HUD_score_nixie_4b", "HUD_score_nixie_6b", "HUD_score_nixie_8b"):
    shutil.copyfile(source / "hud-runtime-dds" / f"{name}.dds", target / f"{name}.dds")

for name in ("HUD_counter_body", "HUD_counter_drum", "HUD_counter_drum_icon", "HUD_rock_body", "HUD_rock_BG_green", "HUD_rock_BG_red", "HUD_rock_BG_yellow", "HUD_rock_lights_all", "HUD_rock_lights_green", "HUD_rock_lights_red", "HUD_rock_lights_yellow", "HUD_rock_needle", "HUD_rock_tube", "HUD_rock_tube_glow_full", "HUD_rock_tube_glow_full_b"):
    Image.open(source / "hud-png" / f"{name}.png").convert("RGBA").save(target / f"{name}.png")
for color in ("", "_blue", "_green", "_purple"):
    for state in range(3):
        name = f"HUD_score_light_{state}{color}"
        Image.open(source / "hud-png" / f"{name}.png").convert("RGBA").save(target / f"{name}.png")

global_images = source / "global-gfx-png"
skull_highway = Image.open(next(global_images.glob("005_*.png"))).convert("RGBA")
skull_highway.save(target / "highway-skull.png")
Image.new("RGBA", skull_highway.size, (0, 0, 0, 255)).save(target / "highway-black.png")
# Vanilla GH3 shows the guitarist's own highway; Axel's is the bullet-belt board in axel_1.pak.
sys.path.insert(0, str(Path(__file__).parent))
import importlib.util as _util
from gh3_direction import Source as _Source
from gh3_formats import key as _key
_spec = _util.spec_from_file_location("prepare_stage", Path(__file__).with_name("prepare-stage.py"))
_stage = _util.module_from_spec(_spec); _spec.loader.exec_module(_stage)
_axel = _Source(Path.home() / "games/gh3-test-tools/game/DATA")
_boards = [image for kind, _, data in _axel.load("PAK/axel_1.pak.xen") if kind == _key(".tex")
           for name, image in _stage.textures(data) if name == "ed67bbe0"]
if len(_boards) != 1 or _boards[0].size != (512, 1024):
    raise ValueError("Axel's highway texture ed67bbe0 is missing from axel_1.pak.xen")
_boards[0].convert("RGBA").save(target / "highway.png")
rail_reference = Image.open(Path(__file__).parent / "gh3-fast-six-bulbs-1600x900.png").convert("RGB")
normal_reference = Image.open(Path(__file__).parent / "gh3-fast-autoplay-gameplay-1600x900.png").convert("RGB")
blue_rails = Image.new("RGBA", rail_reference.size)
gray_rails = Image.new("RGBA", rail_reference.size)
for y in range(380, 900):
    for side, center in enumerate((704 - 0.52 * (y - 400), 904 + 0.52 * (y - 400))):
        for x in range(max(0, int(center) - 16), min(1600, int(center) + 17)):
            sample_y = 784 if 785 <= y <= 800 else 816 if 801 <= y <= 815 else y
            sample_x = round(x + (-0.52 if side == 0 else 0.52) * (sample_y - y))
            red, green, blue = (normal_reference if side == 1 and 410 <= y <= 440 else rail_reference).getpixel((sample_x, sample_y))
            if side == 1 and 410 <= y <= 440:
                red, green, blue = round(red * 0.7), round(green * 0.9), min(255, round(blue * 1.25))
            if blue <= 25 or blue < red + 8 or blue < green - 20:
                continue
            alpha = max(red, green, blue)
            blue_rails.putpixel((x, y), (min(255, red * 255 // alpha), min(255, green * 255 // alpha), min(255, blue * 255 // alpha), alpha))
            neutral = min(255, round((red + green + blue) / 3 * 1.25))
            gray_rails.putpixel((x, y), (neutral, neutral, neutral, alpha))
blue_rails.save(target / "highway-edges-blue.png")
gray_rails.save(target / "highway-edges-normal.png")
for name, index in (("beatline", 79), ("beatline-beat", 90), ("beatline-half", 100), ("sustain", 53)):
    file = next(global_images.glob(f"{index:03d}_*.png"))
    image = Image.open(file).convert("RGBA")
    image.save(target / f"{name}.png")

flame_source = Path(__file__).parent / "reference-flames"
for frame in range(16):
    Image.open(flame_source / f"hit-{frame // 2:02d}.png").save(target / f"hit-{frame:02d}.png")
    Image.open(flame_source / f"held-{frame:02d}.png").save(target / f"sustain-flame-{frame:02d}.png")

spark = Image.open(next(global_images.glob("032_*.png"))).convert("RGBA")
for frame in range(16):
    size = round(48 + 80 * min(frame / 5, 1))
    glow = spark.resize((size, size), Image.Resampling.LANCZOS)
    brightness = max(0, min(1, (frame + 1) / 4, (16 - frame) / 11))
    glow.putalpha(glow.convert("RGB").convert("L").point(lambda value: round(value * brightness)))
    tile = Image.new("RGBA", (128, 128))
    tile.alpha_composite(glow, ((128 - size) // 2, (128 - size) // 2))
    tile.save(target / f"sp-spark-{frame:02d}.png")

print(f"Prepared {len(list(target.glob('*.png'))) + len(list(target.glob('*.dds')))} GH3 textures in {target}")
