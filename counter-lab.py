import json
from argparse import ArgumentParser
from pathlib import Path
import subprocess

import numpy as np
from PIL import Image, ImageDraw


ROOT = Path(__file__).parent
ASSETS = ROOT / "assets"
FRAMES = Path("/tmp/opencode/gh3-counter-lab")
GH3_FRAMES = ROOT / "references" / "counter-lab" / "gh3"
SCALE = 1600 / 1920
BODY = (335, 925, 256, 128)
ICON_BACK = (371, 965, 76, 59)
ICON = (379.5, 967.5, 68, 66)
CROP = (330, 745, 505, 820)


def image(name):
    return Image.open(ASSETS / f"{name}.png").convert("RGBA")


def position(layout, tuning):
    x, y, width, height = layout
    x = 383 + (x - 383) * 1.06 + 20 + tuning["counterOffsetX"]
    y += tuning["counterOffsetY"]
    return round(x * SCALE), round((y - 66) * SCALE), round(width * 1.06 * SCALE), round(height * SCALE)


def slot_layout(slot, shift, reel_width, tuning):
    start = 426
    x = start + slot * 38 + (5 if slot == 0 else 2 if slot == 1 else 1)
    y = 974 if slot == 2 else 972
    width = 36 if slot == 1 else reel_width if slot == 2 else tuning["firstReelWidth"]
    height = 48 if slot == 2 else 45
    return x + (shift if slot == 2 else 0), y, width, height


def draw(canvas, name, layout, tuning, opacity=1, dy=0):
    x, y, width, height = position(layout, tuning)
    texture = image(name).resize((width, height), Image.Resampling.BILINEAR)
    if opacity != 1:
        texture.putalpha(texture.getchannel("A").point(lambda alpha: round(alpha * opacity)))
    canvas.alpha_composite(texture, (x, y + round(dy)))


def render(previous, combo, frame, model, shift, tuning, reel_width):
    canvas = Image.new("RGBA", (1600, 900), (0, 0, 0, 255))
    draw(canvas, "HUD_counter_body", BODY, tuning)
    draw(canvas, "HUD_counter_drum_icon_back", ICON_BACK, tuning)
    draw(canvas, "HUD_counter_drum_icon", ICON, tuning)
    if model != "current":
        backing = slot_layout(0, 0, reel_width, tuning)
        draw(canvas, "combo-reel-0", (backing[0], backing[1], 38, 50), tuning)
        for slot in range(2):
            x, y, width, _ = slot_layout(slot, shift, reel_width, tuning)
            draw(canvas, f"combo-reel-{slot}", (x, y, width, 1022 - y), tuning)
    for slot, digit in enumerate(f"{combo:03}"):
        layout = slot_layout(slot, shift, reel_width, tuning)
        tile = f"combo-2-extended-digit-{digit}" if slot == 2 and model != "current" else f"combo-{slot}-digit-{digit}"
        if frame < 0 or digit == f"{previous:03}"[slot]:
            displayed = f"{previous:03}"[slot] if frame < 0 else digit
            tile = f"combo-2-extended-digit-{displayed}" if slot == 2 and model != "current" else f"combo-{slot}-digit-{displayed}"
            if slot < 2 and model != "current":
                tile = f"combo-glyph-light-{displayed}"
            draw(canvas, tile, layout, tuning)
            continue
        if model == "current":
            if frame >= 4:
                draw(canvas, tile, layout, tuning)
                continue
            opacity = max(0, (frame / 4 - 0.5) * 2)
            offset = 0
        else:
            if frame >= len(tuning["rollAlpha"]):
                draw(canvas, f"combo-glyph-light-{digit}" if slot < 2 else tile, layout, tuning)
                continue
            opacity = tuning["rollAlpha"][frame]
            offset = tuning["rollOffset720"][frame] * 1600 / 1280
        reel = "combo-reel-2-extended" if slot == 2 and model != "current" else f"combo-reel-{slot}"
        if slot == 2 or model == "current":
            draw(canvas, reel, layout, tuning)
        glyph = f"combo-glyph-{'dark' if slot == 2 else 'light'}-{digit}"
        x, y, width, height = position(layout, tuning)
        face = Image.new("RGBA", (width, height))
        glyph_width = round(width * 35 / reel_width) if slot == 2 and model != "current" else width
        layer = image(glyph).resize((glyph_width, height), Image.Resampling.BILINEAR)
        layer.putalpha(layer.getchannel("A").point(lambda alpha: round(alpha * opacity)))
        layer = layer.transform((glyph_width, height), Image.Transform.AFFINE,
                                (1, 0, 0, 0, 1, -offset), Image.Resampling.BILINEAR)
        face.alpha_composite(layer)
        canvas.alpha_composite(face, (x, y))
    draw(canvas, "HUD_counter_frame", BODY, tuning)
    return canvas.crop(CROP).convert("RGB")


def reference_frame(kind, frame, skin_directory):
    directory = skin_directory if kind == "skin" else GH3_FRAMES
    source = Image.open(directory / f"{frame:04}.png").convert("RGB")
    if kind == "gh3":
        source = source.resize((1600, 900), Image.Resampling.BILINEAR)
        return source.crop((320, 723, 495, 798))
    return source.crop(CROP)


def contact_sheet(shift, reel_width, tuning, output, skin_directory, skin_start):
    rows = 8
    scale = 2
    width, height = (CROP[2] - CROP[0]) * scale, (CROP[3] - CROP[1]) * scale
    labels = ("GH3", "Skin capture", "Old motion and layout", "Local tuned")
    sheet = Image.new("RGB", (width * len(labels), (height + 24) * rows), "#171717")
    draw_text = ImageDraw.Draw(sheet)
    previous_tuning = {**tuning, "firstReelWidth": 38, "counterOffsetX": 0, "counterOffsetY": 0}
    for row in range(rows):
        panels = (
            reference_frame("gh3", 890 + row, skin_directory),
            reference_frame("skin", skin_start + row, skin_directory),
            render(30, 31, row - 1, "current", shift, previous_tuning, 34),
            render(30, 31, row - 1, "bounce", shift, tuning, reel_width),
        )
        for column, panel in enumerate(panels):
            x = column * width
            y = row * (height + 24)
            sheet.paste(panel.resize((width, height), Image.Resampling.NEAREST), (x, y + 24))
            draw_text.text((x + 5, y + 5), f"{labels[column]} | frame {row}", fill="white")
    output.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(output)


def motion_profile(images, blank, box):
    x0, y0, x1, y1 = box
    base = np.asarray(blank.convert("RGB"), dtype=float)[y0:y1, x0:x1].mean(axis=2)
    results = []
    for image_data in images:
        face = np.asarray(image_data.convert("RGB"), dtype=float)[y0:y1, x0:x1].mean(axis=2)
        difference = np.maximum(0, base - face)
        ys, xs = np.where(difference > 20)
        center = float(np.average(ys, weights=difference[ys, xs])) + y0 if len(ys) else None
        results.append((float(difference.mean()), center))
    return results


def measure(tuning, reel_width, skin_directory, skin_start):
    gh3 = [Image.open(GH3_FRAMES / f"{n:04}.png") for n in range(891, 898)]
    skin = [Image.open(skin_directory / f"{n:04}.png") for n in range(skin_start + 1, skin_start + 8)]
    local = [render(30, 31, n, "bounce", 0, tuning, reel_width) for n in range(7)]
    reference = motion_profile(gh3, gh3[1], (355, 602, 370, 624))
    glyph_box = (442, 760, 462, 793)
    captured = motion_profile(skin, skin[1], glyph_box)
    preview = motion_profile(local, local[1], tuple(value - (CROP[0] if index % 2 == 0 else CROP[1])
                                                 for index, value in enumerate(glyph_box)))
    print("frame  GH3 ink  capture ink  local ink  GH3 centroid px  capture centroid px  local centroid px")
    for n, (gh3_value, captured_value, local_value) in enumerate(zip(reference, captured, preview)):
        def alpha(value, rest):
            return value[0] / rest[0] if rest[0] else 0

        def rise(value, rest, scale):
            return (value[1] - rest[1]) * scale if value[1] is not None else 0

        print(f"{n:>5}  {alpha(gh3_value, reference[-1]):>9.2f}  "
              f"{alpha(captured_value, captured[-1]):>13.2f}  {alpha(local_value, preview[-1]):>11.2f}  "
              f"{rise(gh3_value, reference[-1], 1.25):>11.2f}  "
              f"{rise(captured_value, captured[-1], 1):>15.2f}  "
              f"{rise(local_value, preview[-1], 1):>13.2f}")
    reference_face = np.asarray(Image.open(GH3_FRAMES / "0897.png").convert("RGB")
                                .resize((1600, 900), Image.Resampling.LANCZOS), dtype=float)[772].mean(axis=1)
    skin_face = np.asarray(skin[-1].convert("RGB"), dtype=float)[772].mean(axis=1)

    def dark_seam(face, left):
        first_dark = next(x for x in range(left, left + 40) if face[x] < 32)
        next_border = next(x for x in range(first_dark + 1, first_dark + 12) if face[x] >= 32)
        return next_border - first_dark

    print(f"Right-reel dark seam at 1600px: GH3 {dark_seam(reference_face, 467)}px, "
          f"capture {dark_seam(skin_face, 467)}px")


def main():
    parser = ArgumentParser(description="Render the GH3 note-streak counter without starting Clone Hero.")
    parser.add_argument("--tuning", type=Path, default=ROOT / "counter-tuning.json")
    parser.add_argument("--skin-video", type=Path, default=ROOT / "comparison-modified-0.8.66.mp4")
    parser.add_argument("--skin-frames", type=Path)
    parser.add_argument("--skin-start", type=int, default=585)
    parser.add_argument("--shift", type=float, default=0, help="Move the final reel in 1920px layout units.")
    parser.add_argument("--reel-width", type=float, help="Override the final reel width in 1920px layout units.")
    parser.add_argument("--measure", action="store_true", help="Print 030-to-031 frame measurements.")
    parser.add_argument("--output", type=Path, default=FRAMES / "counter-comparison.png")
    args = parser.parse_args()
    tuning = json.loads(args.tuning.read_text())
    reel_width = args.reel_width if args.reel_width is not None else tuning["lastReelWidth"]
    skin_frames = args.skin_frames or FRAMES / args.skin_video.stem
    if not (skin_frames / f"{args.skin_start:04}.png").exists() or (
        args.skin_video.stat().st_mtime > (skin_frames / f"{args.skin_start:04}.png").stat().st_mtime
    ):
        skin_frames.mkdir(parents=True, exist_ok=True)
        subprocess.run([
            "ffmpeg", "-hide_banner", "-loglevel", "error", "-nostdin", "-i", str(args.skin_video),
            "-vf", f"select='between(n,{args.skin_start},{args.skin_start + 7})'",
            "-fps_mode", "vfr", "-start_number", str(args.skin_start),
            "-y", str(skin_frames / "%04d.png"),
        ], check=True)
    contact_sheet(args.shift, reel_width, tuning, args.output, skin_frames, args.skin_start)
    if args.measure:
        measure(tuning, reel_width, skin_frames, args.skin_start)
    print(args.output)


if __name__ == "__main__":
    main()
