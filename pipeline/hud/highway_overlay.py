"""Move rail overlays cut from FastGH3 captures onto the rails of original GH3.

The rail images come from 1600 x 900 FastGH3 captures. Original GH3 draws its rails through
(800, 183.6) with a slope of 0.5102 (see src/HighwayView.cs); FastGH3 rails pass through
x = 704 and 904 at y = 400 with a slope of 0.52.
"""

import numpy as np
from PIL import Image

VANISHING_Y = 183.6
RAIL_SLOPE = 0.5102
CENTER_X = 800


def fastgh3_rails(y):
    return 704 - 0.52 * (y - 400), 904 + 0.52 * (y - 400)


def gh3_rails(y):
    half = RAIL_SLOPE * (y - VANISHING_Y)
    return CENTER_X - half, CENTER_X + half


def warp(image, top=0):
    """Return an RGBA image whose row r shows screen row top + r with the rails at GH3's positions."""
    source = np.asarray(image.convert("RGBA"), dtype=np.float32)
    height, width = source.shape[:2]
    result = np.zeros_like(source)
    x = np.arange(width, dtype=np.float32)
    for row in range(height):
        y = top + row
        old_left, old_right = fastgh3_rails(y)
        new_left, new_right = gh3_rails(y)
        weight = np.clip((x - new_left) / (new_right - new_left), 0, 1)
        sample = x + (old_left - new_left) * (1 - weight) + (old_right - new_right) * weight
        for channel in range(4):
            result[row, :, channel] = np.interp(sample, x, source[row, :, channel], left=0, right=0)
    return Image.fromarray(result.round().astype(np.uint8), "RGBA")
