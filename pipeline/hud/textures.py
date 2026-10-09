"""Prepare the HUD, gem, sustain, and highway textures in assets/ from the owner's GH3, BetterGH3, and FastGH3 files."""

import shutil
from io import BytesIO
from pathlib import Path
from struct import unpack_from
from zipfile import ZipFile
from zlib import crc32

from PIL import Image, ImageChops, ImageFilter

from pipeline.gh3 import scene
from pipeline.gh3.direction import Source
from pipeline.gh3.formats import key
from pipeline.hud.highway_overlay import warp as warp_rails
from pipeline.paths import ASSETS, BEAT_LINES, COUNTER, GEMS, GH3_DATA, GH3_SKIN_ASSETS, HIGHWAY, HUD, ROCK_METER, SCORE, SUSTAINS

REFERENCES = Path(__file__).resolve().parent/"references"
source = GH3_SKIN_ASSETS
written = []


def output(folder, name):
    folder.mkdir(parents=True, exist_ok=True)
    written.append(folder / name)
    return folder / name


Image.new("RGBA", (4, 4)).save(output(HUD, "clear.png"))

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
        Image.open(file).convert("RGBA").save(output(GEMS / lane, f"{lane}-{variant}.png"))

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
            sheet.crop(((frame % 4) * 128, (frame // 4) * 64, (frame % 4 + 1) * 128, (frame // 4 + 1) * 64)).save(output(GEMS / lane, f"{lane}-{variant}-{frame}.png"))

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
                image.crop(((frame % 4) * 128, (frame // 4) * 64, (frame % 4 + 1) * 128, (frame // 4 + 1) * 64)).save(output(GEMS / lane, f"{lane}-star-tap-{frame}.png"))
        else:
            gem = "active" if lane == "starpower" else lane
            image.save(output(GEMS / gem, f"{gem}-tap.png"))

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
        sheet.crop(((frame % 4) * 128, (frame // 4) * 64, (frame % 4 + 1) * 128, (frame // 4 + 1) * 64)).save(output(GEMS / "active", f"active-{variant}-{frame}.png"))

for name in ("HUD_score_body", "HUD_score_nixie_1a", "HUD_score_nixie_2a", "HUD_score_nixie_3a", "HUD_score_nixie_4a", "HUD_score_nixie_2b", "HUD_score_nixie_4b", "HUD_score_nixie_6b", "HUD_score_nixie_8b"):
    shutil.copyfile(source / "hud-runtime-dds" / f"{name}.dds", output(SCORE, f"{name}.dds"))

for name in ("HUD_counter_body", "HUD_counter_drum", "HUD_counter_drum_icon", "HUD_rock_body", "HUD_rock_BG_green", "HUD_rock_BG_red", "HUD_rock_BG_yellow", "HUD_rock_lights_all", "HUD_rock_lights_green", "HUD_rock_lights_red", "HUD_rock_lights_yellow", "HUD_rock_needle", "HUD_rock_tube", "HUD_rock_tube_glow_full", "HUD_rock_tube_glow_full_b"):
    Image.open(source / "hud-png" / f"{name}.png").convert("RGBA").save(output(COUNTER if name.startswith("HUD_counter_") else ROCK_METER, f"{name}.png"))
# Streaks of five or more digits open the counter at x = 95 and repeat this 37-texel strip of the body. Its last
# columns fade into the columns that precede x = 95, so copies join each other and both body halves without a seam.
counter_body = Image.open(COUNTER / "HUD_counter_body.png")
counter_tile = counter_body.crop((95, 0, 132, counter_body.height))
for column in range(8):
    weight = (column + 1) / 9
    x = 37 - 8 + column
    counter_tile.paste(Image.blend(counter_body.crop((95 + x, 0, 96 + x, counter_body.height)),
        counter_body.crop((58 + x, 0, 59 + x, counter_body.height)), weight), (x, 0))
counter_tile.save(output(COUNTER, "HUD_counter_body_tile.png"))
for color in ("", "_blue", "_green", "_purple"):
    for state in range(3):
        name = f"HUD_score_light_{state}{color}"
        Image.open(source / "hud-png" / f"{name}.png").convert("RGBA").save(output(SCORE, f"{name}.png"))

global_images = source / "global-gfx-png"
skull_highway = Image.open(next(global_images.glob("005_*.png"))).convert("RGBA")
skull_highway.save(output(HIGHWAY, "highway-skull.png"))
# Vanilla GH3 shows the guitarist's own highway; Axel's is the bullet-belt board in axel_1.pak.
axel = Source(GH3_DATA)
boards = [image for kind, _, data in axel.load("PAK/axel_1.pak.xen") if kind == key(".tex")
          for name, image in scene.textures(data) if name == "ed67bbe0"]
if len(boards) != 1 or boards[0].size != (512, 1024):
    raise ValueError("Axel's highway texture ed67bbe0 is missing from axel_1.pak.xen")
boards[0].convert("RGBA").save(output(HIGHWAY, "highway.png"))
rail_reference = Image.open(REFERENCES / "gh3-fast-six-bulbs-1600x900.png").convert("RGB")
normal_reference = Image.open(REFERENCES / "gh3-fast-autoplay-gameplay-1600x900.png").convert("RGB")
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
warp_rails(blue_rails).save(output(HIGHWAY, "highway-edges-blue.png"))
warp_rails(gray_rails).save(output(HIGHWAY, "highway-edges-normal.png"))
for name, index in (("beatline", 79), ("beatline-beat", 90), ("beatline-half", 100), ("sustain", 53)):
    file = next(global_images.glob(f"{index:03d}_*.png"))
    image = Image.open(file).convert("RGBA")
    image.save(output(SUSTAINS if name == "sustain" else BEAT_LINES, f"{name}.png"))


def whammy_textures():
    # sys_whammy2d_<name> materials (WhammyBar_UI) in global.pak sample these 32 x 32 textures; red is not in global-gfx-png.
    keys = {"green": 0xb47133c1, "red": 0x6c97b6c6, "yellow": 0xec463e7a, "blue": 0x037e6d2c,
            "orange": 0xca034a7a, "star": 0xe78e1a35, "dead": 0x2a589f46}
    found = {}
    for kind, _, data in axel.load("ZONES/global.pak.xen"):
        if kind != key(".tex"):
            continue
        count, table = unpack_from(">H", data, 6)[0], unpack_from(">I", data, 8)[0]
        for index in range(count):
            name = unpack_from(">I", data, table + 40 * index + 4)[0]
            start, size = unpack_from(">II", data, table + 40 * index + 28)
            if name in keys.values() and name not in found:
                found[name] = Image.open(BytesIO(data[start:start + size])).convert("RGBA")
    if len(found) != len(keys) or any(image.size != (32, 32) for image in found.values()):
        raise ValueError("GH3 whammy textures are missing from global.pak.xen")
    return {variant: found[key] for variant, key in keys.items()}


def held_sustain(sprite, bar_width=30, sigma=1.2, colour_gain=1.8, white_gain=0.55):
    # GH3 adds a glow around a held bar. Held green, blue, and orange bars in matched GH3 frames (164.8-167.9 s and
    # 198.0-200.4 s, rows 600-690) fit bar + blur(1.8 * squared bar colour + 0.55 * white bar mask), with a Gaussian sigma
    # of 1.2 bar widths. Squaring the colour keeps the glow saturated, as in GH3 (blue and orange glows add little green).
    # The sprite is drawn with alpha blending, so the added glow is stored as colour over partial alpha.
    margin = round(3 * sigma * bar_width) - (sprite.width - bar_width) // 2
    size = (sprite.width + 2 * margin, sprite.height)
    alpha = Image.new("L", size)
    alpha.paste(sprite.getchannel("A"), (margin, 0))
    colour = Image.new("RGB", size)
    colour.paste(ImageChops.multiply(sprite.convert("RGB"), sprite.getchannel("A").convert("RGB")), (margin, 0))
    squared = Image.new("RGB", size)
    squared.paste(ImageChops.multiply(ImageChops.multiply(sprite.convert("RGB"), sprite.convert("RGB")),
                                      sprite.getchannel("A").convert("RGB")), (margin, 0))
    # Clone Hero squeezes the 73-pixel sprite to 0.13 units but keeps 100 pixels per unit vertically.
    radius = (sigma * bar_width, sigma * bar_width * 0.13 / 73 * 100)
    blurred_colour = squared.filter(ImageFilter.GaussianBlur(radius))
    blurred_alpha = alpha.filter(ImageFilter.GaussianBlur(radius))
    held = Image.new("RGBA", size)
    for y in range(size[1]):
        for x in range(size[0]):
            glow = blurred_alpha.getpixel((x, y)) * white_gain
            total = [min(255.0, base + colour_gain * blurred + glow)
                     for base, blurred in zip(colour.getpixel((x, y)), blurred_colour.getpixel((x, y)))]
            opacity = max(alpha.getpixel((x, y)), *total)
            if opacity > 0:
                held.putpixel((x, y), (*(round(value * 255 / opacity) for value in total), round(opacity)))
    return held


# Same layout as Clone Hero's spr_sustain_strip6 sprites (73 x 70, tube in columns 22-51, cap above the sliced border at
# row 38), so the native sliced renderer keeps its width and cap length. GH3 rows 12-23 hold the cap; row 28 is the body.
for variant, texture in whammy_textures().items():
    tube = Image.new("RGBA", (32, 70))
    tube.paste(texture.crop((0, 12, 32, 24)).resize((32, 24), Image.Resampling.NEAREST), (0, 14))
    body = texture.crop((0, 28, 32, 29))
    for y in range(38, 70):
        tube.paste(body, (0, y))
    sprite = Image.new("RGBA", (73, 70))
    sprite.paste(tube, (21, 0))
    sprite.save(output(SUSTAINS, f"sustain-{variant}.png"))
    if variant != "dead":
        held_sustain(sprite).save(output(SUSTAINS, f"sustain-held-{variant}.png"))

print(f"Prepared {len(written)} GH3 textures in {', '.join(sorted({str(path.parent.relative_to(ASSETS)) for path in written}))}")
