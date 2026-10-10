"""Cut the Star Power ready banner and lightning frames from the owner's GH3 reference recording."""

import argparse
import json
import subprocess
from pathlib import Path

import numpy as np
from PIL import Image, ImageChops, ImageDraw, ImageFilter

from pipeline.paths import READY_ANIMATION, ROOT

parser = argparse.ArgumentParser(prog="python -m pipeline.hud.ready_animation", description=__doc__)
parser.add_argument(
    "--video",
    type=Path,
    default=ROOT / "gh3-long-star-power-1600x900.mp4",
    help="The owner's 1600 x 900 GH3 Star Power reference recording",
)
video = parser.parse_args().video
target = READY_ANIMATION
target.mkdir(parents=True, exist_ok=True)
width, height, top = 1600, 820, 30
frame_bytes = width * height * 3
lightning_left, lightning_top = 970, 370
lightning_width, lightning_height = 530, 480
lightning_frame_count = 51
reference_command = [
    "ffmpeg",
    "-hide_banner",
    "-loglevel",
    "error",
    "-nostdin",
    "-ss",
    "7.8",
    "-i",
    str(video),
    "-frames:v",
    "1",
    "-vf",
    f"crop={lightning_width}:{lightning_height}:{lightning_left}:{lightning_top}",
    "-pix_fmt",
    "rgb24",
    "-f",
    "rawvideo",
    "-",
]
reference = np.frombuffer(subprocess.run(reference_command, check=True, capture_output=True).stdout, dtype=np.uint8)
reference = reference.reshape(lightning_height, lightning_width, 3).astype(np.int16)
command = [
    "ffmpeg",
    "-hide_banner",
    "-loglevel",
    "error",
    "-nostdin",
    "-ss",
    "8.0",
    "-t",
    "2.1",
    "-i",
    str(video),
    "-vf",
    f"fps=60,crop={width}:{height}:0:{top}",
    "-pix_fmt",
    "rgb24",
    "-f",
    "rawvideo",
    "-",
]

frames = []
lightning_frames = []
with subprocess.Popen(command, stdout=subprocess.PIPE) as process:
    assert process.stdout is not None
    while data := process.stdout.read(frame_bytes):
        if len(data) != frame_bytes:
            raise RuntimeError("Incomplete GH3 animation frame")
        full_image = Image.frombytes("RGB", (width, height), data)
        image = full_image.crop((0, 0, width, 340))
        red, green, blue = image.split()
        intensity = ImageChops.lighter(ImageChops.lighter(red, green), blue)
        alpha = intensity.point(lambda value: 255 if value >= 18 else 0)
        ImageDraw.Draw(alpha).rectangle((0, 0, 3, height - 1), fill=0)
        bounds = alpha.getbbox()
        if bounds is None:
            bounds = (0, 0, 1, 1)
        rgba = image.convert("RGBA")
        rgba.putalpha(alpha)
        rgba.crop(bounds).save(target / f"ready-animation-{len(frames):03}.png", optimize=True)
        left, upper, right, lower = bounds
        frames.append({"x": left, "y": upper + top, "width": right - left, "height": lower - upper})
        if len(frames) <= lightning_frame_count:
            lightning = np.asarray(
                full_image.crop(
                    (
                        lightning_left,
                        lightning_top - top,
                        lightning_left + lightning_width,
                        lightning_top - top + lightning_height,
                    )
                )
            ).astype(np.int16)
            difference = np.max(np.maximum(lightning - reference, 0), axis=2)
            saturated = (
                (lightning[:, :, 2] > lightning[:, :, 0] + 12)
                & (lightning[:, :, 1] > lightning[:, :, 0] + 8)
                & (difference > 30)
            )
            nearby = (
                np.asarray(Image.fromarray((saturated * 255).astype(np.uint8)).filter(ImageFilter.MaxFilter(13))) > 0
            )
            mask = (difference > 24) & nearby
            row = np.arange(lightning_height)[:, None]
            column = np.arange(lightning_width)[None, :] + lightning_left
            mask &= (row < 180) | (column <= 1195 - (row - 180) * 0.5)
            if len(frames) > 30:
                mask[180:, :] = False
            opacity = (np.clip((difference - 19) * 18, 0, 255) * mask).astype(np.uint8)
            effect = Image.fromarray(np.dstack((lightning.astype(np.uint8), opacity)), "RGBA")
            bounds = effect.getbbox() or (0, 0, 1, 1)
            effect.crop(bounds).save(target / f"ready-lightning-{len(lightning_frames):03}.png", optimize=True)
            left, upper, right, lower = bounds
            lightning_frames.append(
                {"x": left + lightning_left, "y": upper + lightning_top, "width": right - left, "height": lower - upper}
            )
    if process.wait() != 0:
        raise RuntimeError("Could not decode GH3 animation")

(target / "ready-animation.json").write_text(
    json.dumps({"fps": 60, "frames": frames, "lightning": lightning_frames}) + "\n"
)
print(f"Prepared {len(frames)} GH3 animation frames and {len(lightning_frames)} lower-effect frames")
