from pathlib import Path
from PIL import Image, ImageChops, ImageDraw

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

for name in ("HUD_score_body", "HUD_counter_drum_icon", "HUD_rock_body", "HUD_rock_BG_green", "HUD_rock_BG_red", "HUD_rock_BG_yellow", "HUD_rock_lights_all", "HUD_rock_lights_green", "HUD_rock_lights_red", "HUD_rock_lights_yellow", "HUD_rock_needle", "HUD_rock_tube", "HUD_rock_tube_glow_full", "HUD_rock_tube_glow_full_b", "HUD_score_nixie_1a", "HUD_score_nixie_2a", "HUD_score_nixie_3a", "HUD_score_nixie_4a"):
    Image.open(source / "hud-png" / f"{name}.png").convert("RGBA").save(target / f"{name}.png")
for value in range(1, 5):
    name = f"HUD_score_nixie_{value}a"
    original = Image.open(target / f"{name}.png").convert("RGBA")
    face = original.crop((34, 0, 107, 128)).resize((85, 128), Image.Resampling.BICUBIC)
    original.alpha_composite(face, (22, 0))
    original.save(target / f"{name}_wide.png")
for color in ("", "_blue", "_green", "_purple"):
    for state in range(3):
        name = f"HUD_score_light_{state}{color}"
        Image.open(source / "hud-png" / f"{name}.png").convert("RGBA").save(target / f"{name}.png")
score_frame = Image.open(target / "HUD_score_body.png").convert("RGBA")
score_frame_mask = Image.new("L", score_frame.size)
ImageDraw.Draw(score_frame_mask).rectangle((35, 0, 255, 255), fill=255)
score_frame.putalpha(ImageChops.multiply(score_frame.getchannel("A"), score_frame_mask))
score_frame.save(target / "HUD_score_frame.png")
ring_mask = Image.new("L", score_frame.size)
ring_draw = ImageDraw.Draw(ring_mask)
ring_draw.ellipse((65, 77, 203, 217), fill=255)
ring_draw.ellipse((79, 96, 191, 205), fill=0)
score_ring = score_frame.copy()
score_ring.putalpha(ImageChops.multiply(score_ring.getchannel("A"), ring_mask))
score_ring.save(target / "HUD_score_ring_front.png")
icon_back = Image.new("RGBA", (64, 64), (29, 24, 23, 255))
ImageDraw.Draw(icon_back).rectangle((0, 0, 19, 63), fill=(0, 0, 0, 0))
icon_back.save(target / "HUD_counter_drum_icon_back.png")
icon = Image.open(target / "HUD_counter_drum_icon.png").convert("RGBA")
icon.putalpha(icon.getchannel("A").point(lambda value: 0 if value <= 191 else value))
for y in range(icon.height):
    for x in range(icon.width):
        red, green, blue, alpha = icon.getpixel((x, y))
        if alpha:
            icon.putpixel((x, y), (round(red * 0.95), round(green * 0.98), min(255, round((blue + red // 6) * 0.95) + 4), alpha))
icon.save(target / "HUD_counter_drum_icon.png")
counter_body = Image.open(source / "hud-png/HUD_counter_body.png").convert("RGBA")
frame_mask = Image.new("L", counter_body.size)
frame_draw = ImageDraw.Draw(frame_mask)
frame_draw.rectangle((48, 33, 222, 109), fill=255)
frame_draw.rectangle((61, 48, 210, 94), fill=0)
counter_back = counter_body.copy()
counter_back.putalpha(ImageChops.multiply(counter_body.getchannel("A"), ImageChops.invert(frame_mask)))
ImageDraw.Draw(counter_back).rectangle((0, 110, 255, 127), fill=(0, 0, 0, 0))
counter_back.save(target / "HUD_counter_body.png")
counter_frame = counter_body.copy()
counter_frame.putalpha(ImageChops.multiply(counter_body.getchannel("A"), frame_mask))
counter_frame.save(target / "HUD_counter_frame.png")
Image.open(source / "hud-png/HUD_score_body.png").convert("RGBA").crop((68, 132, 116, 175)).save(target / "HUD_score_x.png")

global_images = source / "global-gfx-png"
skull_highway = Image.open(next(global_images.glob("005_*.png"))).convert("RGBA")
skull_highway.save(target / "highway-skull.png")
Image.new("RGBA", skull_highway.size, (0, 0, 0, 255)).save(target / "highway.png")
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

references = Path(__file__).parent / "references"
score_font = Image.open(references / "gh3-score-font.png").convert("RGBA")
for digit in range(10):
    x, y = (839 + digit * 24, 0) if digit < 7 else ((digit - 7) * 24, 35)
    score_font.crop((x, y, x + 24, y + 30)).save(target / f"score-digit-{digit}.png")

counter_font = Image.open(references / "gh3-counter-font.png").convert("RGBA")
counter_drum = Image.open(source / "hud-png/HUD_counter_drum.png").convert("RGBA")


def shade_reel(image, levels):
    pixels = []
    for y in range(image.height):
        for (start, first), (end, second) in zip(levels, levels[1:]):
            if y <= end:
                brightness = first + (second - first) * (y - start) / (end - start)
                break
        for x in range(image.width):
            red, green, blue, alpha = image.getpixel((x, y))
            pixels.append((min(255, round(red * brightness)), min(255, round(green * brightness)), min(255, round(blue * brightness)), alpha))
    shaded = Image.new("RGBA", image.size)
    shaded.putdata(pixels)
    return shaded


def fade_reel_edges(image, levels):
    alpha = image.getchannel("A")
    for y in range(image.height):
        for (start, first), (end, second) in zip(levels, levels[1:]):
            if y <= end:
                opacity = first + (second - first) * (y - start) / (end - start)
                break
        for x in range(image.width):
            alpha.putpixel((x, y), round(alpha.getpixel((x, y)) * opacity))
    image.putalpha(alpha)
    return image


dark_levels = ((0, 0.5), (8, 0.55), (14, 0.65), (20, 0.7), (26, 0.78), (33, 0.92), (38, 1.15), (50, 1.25))
white_levels = ((0, 0.95), (8, 1.05), (20, 1.0), (26, 0.97), (33, 0.98), (38, 1.05), (50, 1.45))
reels = []
for slot in range(4):
    background_x = 54 + slot * 37 if slot < 2 else 165
    reel = counter_drum.crop((background_x, 9, background_x + 37, 54))
    reel = reel.resize((38, 50), Image.Resampling.LANCZOS)
    reel = shade_reel(reel, dark_levels if slot < 2 else white_levels)
    if slot >= 2:
        reel = fade_reel_edges(reel, ((0, 0.27), (4, 1.0), (37, 1.0), (42, 0.75), (50, 0.68)))
    reel.save(target / f"combo-reel-{slot}.png")
    reels.append(reel)
dark_reel = counter_drum.crop((128, 9, 165, 54)).resize((38, 50), Image.Resampling.LANCZOS)
dark_reel = shade_reel(dark_reel, dark_levels)
dark_reel.save(target / "combo-reel-2-dark.png")
extended_reel = reels[2].resize((42, 50), Image.Resampling.LANCZOS)
extended_reel.save(target / "combo-reel-2-extended.png")
for digit in range(10):
    x = 21 if digit == 0 else 42 if digit == 1 else 53 + (digit - 2) * 21
    width = 10 if digit == 1 else 20
    glyph = counter_font.crop((x, 0, x + width, 29))
    light_width = 9 if digit == 1 else 17
    light_glyph = glyph.resize((light_width, 31), Image.Resampling.LANCZOS)
    light_ink = Image.new("RGBA", light_glyph.size, (205, 206, 210, 0))
    light_ink.putalpha(light_glyph.getchannel("A"))
    light = Image.new("RGBA", (38, 50))
    light.alpha_composite(light_ink, (7 + (17 - light_width) // 2, 13))
    light.save(target / f"combo-glyph-light-{digit}.png")
    dark_width = 11 if digit == 1 else 21
    dark_glyph = glyph.resize((dark_width, 32), Image.Resampling.LANCZOS)
    dark_ink = Image.new("RGBA", dark_glyph.size, (56, 56, 80, 0))
    dark_ink.putalpha(dark_glyph.getchannel("A"))
    dark = Image.new("RGBA", (38, 50))
    dark.alpha_composite(dark_ink, (8 + (21 - dark_width) // 2, 8))
    dark.save(target / f"combo-glyph-dark-{digit}.png")
    extended_tile = extended_reel.copy()
    extended_tile.alpha_composite(dark)
    extended_tile.save(target / f"combo-2-extended-digit-{digit}.png")
    for slot in range(4):
        tile = reels[slot].copy()
        tile.alpha_composite(light if slot < 2 else dark)
        tile.save(target / f"combo-{slot}-digit-{digit}.png")
        if slot == 2:
            tile = dark_reel.copy()
            tile.alpha_composite(light)
            tile.save(target / f"combo-2-dark-digit-{digit}.png")

print(f"Prepared {len(list(target.glob('*.png')))} GH3 textures in {target}")
