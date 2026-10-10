"""Pre-encoded package textures (PACKAGE-FORMAT.md section 7) in the add-on: export encodes every PNG image to GH3TEX02
with the shared encoder (vendor/package_format/texture.py); open decodes GH3TEX02 to PNG for Blender.

Blender decodes the PNG files. The encoder runs on worker threads, one texture each: numpy releases the GIL in its
array operations.
"""

import os
import tempfile
import time
from concurrent.futures import ThreadPoolExecutor

import bpy
import numpy as np

from .vendor.package_format import texture

STATS = {"textures": 0, "seconds": 0.0}


def workers():
    return max(1, min(8, (os.cpu_count() or 2) - 1))


def png_rgba(data):
    """RGBA (H, W, 4) uint8 of PNG bytes, top row first (Blender stores the bottom row first)."""
    with tempfile.TemporaryDirectory(prefix="chstage-png-") as folder:
        path = os.path.join(folder, "image.png")
        with open(path, "wb") as stream:
            stream.write(data)
        image = bpy.data.images.load(path)
        try:
            # Raw values: no colour conversion and no premultiplied alpha.
            image.colorspace_settings.name = "Non-Color"
            image.alpha_mode = "STRAIGHT"
            width, height = image.size
            pixels = np.empty(width * height * 4, dtype=np.float32)
            image.pixels.foreach_get(pixels)
        finally:
            bpy.data.images.remove(image)
    return np.clip(np.round(pixels * 255), 0, 255).astype(np.uint8).reshape(height, width, 4)[::-1]


def encode_many(items):
    """GH3TEX02 bytes for [(PNG bytes, linear)] (texture.encode_glb callback)."""
    if not items:
        return []
    clock = time.perf_counter()
    with ThreadPoolExecutor(min(workers(), len(items))) as pool:
        # A PNG that Open made and that did not change gives its original texture without a Blender decode.
        kept = list(pool.map(lambda item: texture.unchanged_source(*item), items))
        jobs = [(png, png_rgba(png), linear) for (png, linear), source in zip(items, kept) if source is None]
        encoded = iter(pool.map(lambda job: texture.encode_png_image(*job), jobs))
        result = [source if source is not None else next(encoded) for source in kept]
    STATS["textures"] += len(items)
    STATS["seconds"] += time.perf_counter() - clock
    return result


def encode_glb(data):
    """GLB bytes with every PNG image pre-encoded."""
    return texture.encode_glb(data, encode_many)


def encode_png(data, linear=False):
    """GH3TEX02 bytes of PNG bytes (a texture file of the package)."""
    return encode_many([(data, linear)])[0]


def decode_glb(data):
    """GLB bytes with PNG images for the Blender importer."""
    return texture.decode_glb(data)


def png_of(data):
    """PNG bytes of a package texture file (GH3TEX02) or of a PNG file."""
    return texture.png_bytes(texture.decode(data), data) if data[:8] == texture.MAGIC else data
