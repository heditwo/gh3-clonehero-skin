"""Build the eight active Star Power highway frames from GH3's bolt textures and the owner's reference recording."""

import argparse
import subprocess
from pathlib import Path

import cv2
import numpy as np
from PIL import Image

from pipeline.hud.highway_overlay import warp
from pipeline.paths import ASSETS, GH3_SKIN_ASSETS, ROOT

parser = argparse.ArgumentParser(prog="python -m pipeline.hud.star_power", description=__doc__)
parser.add_argument("--video", type=Path, default=ROOT/"gh3-long-star-power-1600x900.mp4",
                    help="The owner's 1600 x 900 GH3 Star Power reference recording")
video = parser.parse_args().video
source = GH3_SKIN_ASSETS / "global-gfx-png"
target = ASSETS
bolts = np.array(Image.open(source / "010_1024x512_DXT1.png").convert("RGB"), dtype=np.float32)
glow = np.array(Image.open(source / "053_128x512_DXT5.png").convert("RGBA"), dtype=np.float32)
glow = glow[:, :, :3] * glow[:, :, 3:] / 255
y, x = np.mgrid[380:900, 0:1600].astype(np.float32)


def sample(image, u, v):
    return cv2.remap(image, u.astype(np.float32), v.astype(np.float32), cv2.INTER_LINEAR)


models = []
for frame in range(8):
    image = np.zeros((*x.shape, 3), dtype=np.float32)
    bolt = bolts[:, frame * 128:(frame + 1) * 128]
    for right in (False, True):
        angle = np.deg2rad(-27.5 if right else 27.5)
        anchor_x = 1187 if right else 421
        normal = (x - anchor_x) * np.cos(angle) + (y - 936) * np.sin(angle)
        along = -(x - anchor_x) * np.sin(angle) + (y - 936) * np.cos(angle)
        outside = normal > 0 if right else normal < 0
        halo = sample(glow, (normal - (68.75 if right else -68.75)) / (-1.25 if right else 1.25) + 63.5,
                      (along - 68.75) / 1.375 + 511.5)
        broad = sample(bolt, normal / -2.48 + 63.5, along / -1.106)
        core = sample(bolt, normal / (-0.62 if right else 0.62) + 63.5, along / -1.106)
        halo *= np.clip((y - 394) / 598, 0, 1)[:, :, None]
        broad *= np.clip((y - 415) / 515, 0, 1)[:, :, None]
        image += (halo + broad) * outside[:, :, None] + core * 0.8
    models.append(np.clip(image, 0, 255))

left = 704 - 0.52 * (y - 400)
right = 904 + 0.52 * (y - 400)
class_mask = (x < left - 22) & (x > left - 100) & (y > 450) & (y < 585)
templates = np.array([image[class_mask] for image in models])
samples = [[] for _ in models]
command = ["ffmpeg", "-hide_banner", "-loglevel", "error", "-nostdin", "-ss", "11", "-i",
           str(video), "-t", "8", "-vf", "fps=60,crop=1600:520:0:380",
           "-pix_fmt", "rgb24", "-f", "rawvideo", "-"]
with subprocess.Popen(command, stdout=subprocess.PIPE) as process:
    while data := process.stdout.read(1600 * 520 * 3):
        if len(data) != 1600 * 520 * 3:
            raise RuntimeError("Incomplete GH3 highway frame")
        image = np.frombuffer(data, np.uint8).reshape(520, 1600, 3)
        error = np.mean((templates - image[class_mask]) ** 2, axis=(1, 2))
        samples[int(np.argmin(error))].append(image)
    if process.wait() != 0:
        raise RuntimeError("Could not decode the GH3 highway reference")

visible = ((x < left + 6) & ((y < 583) | (x > 495) | (y > 820))) | (
    (x > right - 6) & ((y < 541) | (x < 1093) | (y > 805)))
visible &= (x > 315) | (y < 810)
weight = np.clip(cv2.distanceTransform(visible.astype(np.uint8), cv2.DIST_L2, 5) / 12, 0, 1)
rail = (np.abs(x - left) < 9) | (np.abs(x - right) < 9)
for frame, model in enumerate(models):
    if len(samples[frame]) < 30:
        raise RuntimeError(f"Too few reference samples for highway frame {frame}")
    reference = np.median(samples[frame], axis=0).astype(np.float32)
    support = cv2.dilate((model.max(axis=2) > 2).astype(np.uint8), np.ones((9, 9), np.uint8)) > 0
    reference *= (support | rail)[:, :, None]
    image = model * (1 - weight[:, :, None]) + reference * weight[:, :, None]
    alpha = image.max(axis=2)
    alpha[alpha < 2] = 0
    color = np.divide(image * 255, alpha[:, :, None], out=np.zeros_like(image), where=alpha[:, :, None] > 0)
    rgba = np.dstack((color, alpha)).round().astype(np.uint8)
    warp(Image.fromarray(rgba), 380).save(target / f"active-highway-{frame:02}.png", optimize=True)

print("Prepared eight GH3 active Star Power highway frames from the original textures and reference recording")
