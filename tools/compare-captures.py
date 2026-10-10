import argparse
import json
import subprocess
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw


def frames(path, size=None, start=None, count=None):
    command = ["ffmpeg", "-v", "error"]
    if start is not None:
        command += ["-ss", str(start)]
    command += ["-i", str(path)]
    if size:
        command += ["-vf", f"fps=60,scale={size[0]}:{size[1]}"]
    if count:
        command += ["-frames:v", str(count)]
    command += ["-f", "rawvideo", "-pix_fmt", "rgb24", "-"]
    return subprocess.check_output(command)


def dimensions(path):
    result = json.loads(
        subprocess.check_output(
            [
                "ffprobe",
                "-v",
                "error",
                "-select_streams",
                "v:0",
                "-show_entries",
                "stream=width,height,r_frame_rate",
                "-of",
                "json",
                str(path),
            ]
        )
    )["streams"][0]
    if result["r_frame_rate"] != "60/1":
        raise ValueError("Comparison requires 60 fps videos")
    return result["width"], result["height"]


def main():
    parser = argparse.ArgumentParser(description="Align gameplay captures and save full-frame comparisons.")
    parser.add_argument("before", type=Path)
    parser.add_argument("after", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--times", default="2.2,4.2,6.8,7.3,7.8,8.4,9,11,15,20,26")
    args = parser.parse_args()
    width, height = dimensions(args.before)
    if dimensions(args.after) != (width, height):
        raise ValueError("Both videos must have the same dimensions")
    args.output.mkdir(parents=True, exist_ok=True)
    small_size = (200, 112)
    before = np.frombuffer(frames(args.before, small_size), np.uint8).reshape(-1, 112, 200, 3)
    after = np.frombuffer(frames(args.after, small_size), np.uint8).reshape(-1, 112, 200, 3)
    times = [float(value) for value in args.times.split(",")]
    indices = np.array([round(time * 60) for time in times])
    if indices.max() + 90 >= min(len(before), len(after)):
        raise ValueError("Sample times must leave 1.5 seconds at the end of both videos")
    offset = min(
        range(-60, 61),
        key=lambda shift: np.abs(
            before[indices, 45:103, 60:140].astype(np.int16) - after[indices + shift, 45:103, 60:140].astype(np.int16)
        ).mean(),
    )
    regions = {
        "full": (0, 0, 1600, 900),
        "score_counter": (260, 580, 490, 820),
        "rock_bulbs": (1080, 540, 1350, 810),
        "highway": (440, 370, 1170, 900),
        "ready_banner": (350, 100, 1250, 370),
    }
    report = {
        "before": str(args.before),
        "after": str(args.after),
        "offset_seconds": offset / 60,
        "note": "Encoded, independently timed captures; these metrics do not prove pixel identity.",
        "samples": [],
    }
    sheet = Image.new("RGB", (1600, len(times) * 470), "#151515")
    draw = ImageDraw.Draw(sheet)
    for row, (time, index) in enumerate(zip(times, indices)):
        candidates = range(index + offset - 6, index + offset + 7)
        target = before[index].astype(np.int16)
        match = min(candidates, key=lambda candidate: np.abs(target - after[candidate]).mean())
        a = np.frombuffer(frames(args.before, start=time, count=1), np.uint8).reshape(height, width, 3)
        b = np.frombuffer(frames(args.after, start=match / 60, count=1), np.uint8).reshape(height, width, 3)
        difference = np.abs(a.astype(np.int16) - b.astype(np.int16))
        sample = {"before_seconds": time, "after_seconds": match / 60, "regions": {}}
        for name, (x1, y1, x2, y2) in regions.items():
            xs = slice(round(x1 * width / 1600), round(x2 * width / 1600))
            ys = slice(round(y1 * height / 900), round(y2 * height / 900))
            delta = difference[ys, xs]
            visible = np.maximum(a[ys, xs], b[ys, xs]).max(axis=2) > 20
            sample["regions"][name] = {
                "mean_rgb_error": round(float(delta.mean()), 3),
                "visible_mean_rgb_error": round(float(delta[visible].mean()), 3) if visible.any() else 0,
                "pixels_over_20_percent": round(float((delta.max(axis=2) > 20).mean() * 100), 3),
            }
        pair = Image.new("RGB", (width * 2, height))
        pair.paste(Image.fromarray(a), (0, 0))
        pair.paste(Image.fromarray(b), (width, 0))
        pair.save(args.output / f"pair-{row:02}.png")
        Image.fromarray(np.minimum(difference * 4, 255).astype(np.uint8)).save(args.output / f"difference-{row:02}.png")
        sheet.paste(pair.resize((1600, 450)), (0, row * 470 + 20))
        draw.text((8, row * 470 + 4), f"Before {time:.3f}s", fill="white")
        draw.text((808, row * 470 + 4), f"After {match / 60:.3f}s", fill="white")
        report["samples"].append(sample)
    sheet.save(args.output / "overview.png")
    (args.output / "report.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
