"""Texture spike: encode one PNG to BC1/BC3/BC5/BC7 with a full mip chain and time it.

Writes <out>/<format>.gh3tex files for the in-game probe (GH3_SKIN_SPIKE=textures) and prints timing and PSNR.
File layout: "GH3T", u32 version 1, i32 Unity TextureFormat, i32 width, i32 height, i32 mip count, then all mip
levels, largest first, each padded to whole 4x4 blocks (the layout that Texture2D.LoadRawTextureData expects).
"""

import argparse
import json
import statistics
import struct
import sys
import time
from pathlib import Path

import etcpak
import numpy as np
import texture2ddecoder
from PIL import Image

FORMATS = {
    # name: (Unity TextureFormat value, bytes per block, encoder)
    "bc1": (10, 8, etcpak.compress_bc1),
    "bc3": (12, 16, etcpak.compress_bc3),
    "bc5": (27, 16, etcpak.compress_bc5),
    "bc7": (25, 16, etcpak.compress_bc7),
}
DECODERS = {
    "bc1": texture2ddecoder.decode_bc1,
    "bc3": texture2ddecoder.decode_bc3,
    "bc5": texture2ddecoder.decode_bc5,
    "bc7": texture2ddecoder.decode_bc7,
}


def mip_chain(image: Image.Image) -> list[np.ndarray]:
    levels = [np.asarray(image, dtype=np.uint8)]
    while levels[-1].shape[0] > 1 or levels[-1].shape[1] > 1:
        previous = levels[-1]
        height, width = max(1, previous.shape[0] // 2), max(1, previous.shape[1] // 2)
        levels.append(np.asarray(Image.fromarray(previous).resize((width, height), Image.BOX), dtype=np.uint8))
    return levels


def pad_to_blocks(level: np.ndarray) -> np.ndarray:
    height, width = level.shape[:2]
    padded_height, padded_width = (height + 3) // 4 * 4, (width + 3) // 4 * 4
    if (padded_height, padded_width) == (height, width):
        return level
    return np.pad(level, ((0, padded_height - height), (0, padded_width - width), (0, 0)), mode="edge")


def encode(levels: list[np.ndarray], name: str) -> bytes:
    _, _, encoder = FORMATS[name]
    output = bytearray()
    for level in levels:
        block_level = pad_to_blocks(level)
        if name == "bc5":
            block_level = block_level.copy()
            block_level[..., 2] = 0
        height, width = block_level.shape[:2]
        output += encoder(np.ascontiguousarray(block_level).tobytes(), width, height)
    return bytes(output)


def psnr(name: str, data: bytes, level: np.ndarray) -> float:
    height, width = level.shape[:2]
    decoded = (
        np.frombuffer(
            DECODERS[name](data[: (width // 4) * (height // 4) * FORMATS[name][1]], width, height), dtype=np.uint8
        )
        .reshape(height, width, 4)[..., [2, 1, 0, 3]]
        .astype(np.float64)
    )
    channels = [0, 1] if name == "bc5" else [0, 1, 2]
    error = np.mean((decoded[..., channels] - level[..., channels].astype(np.float64)) ** 2)
    return float("inf") if error == 0 else 10 * np.log10(255**2 / error)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("png", type=Path)
    parser.add_argument("out", type=Path)
    parser.add_argument("--runs", type=int, default=3)
    arguments = parser.parse_args()
    arguments.out.mkdir(parents=True, exist_ok=True)
    # Unity raw texture data starts at the bottom row.
    image = Image.open(arguments.png).convert("RGBA").transpose(Image.FLIP_TOP_BOTTOM)
    started = time.perf_counter()
    levels = mip_chain(image)
    mip_seconds = time.perf_counter() - started
    rgba = b"".join(level.tobytes() for level in levels)
    (arguments.out / "rgba32.gh3tex").write_bytes(
        b"GH3T" + struct.pack("<iiiii", 1, 4, image.width, image.height, len(levels)) + rgba
    )
    report = {
        "source": str(arguments.png),
        "width": image.width,
        "height": image.height,
        "mips": len(levels),
        "mipChainSeconds": mip_seconds,
        "formats": {},
    }
    for name, (unity_format, _, _) in FORMATS.items():
        timings, cpu = [], []
        for _ in range(arguments.runs):
            started, started_cpu = time.perf_counter(), time.process_time()
            data = encode(levels, name)
            timings.append(time.perf_counter() - started)
            cpu.append(time.process_time() - started_cpu)
        (arguments.out / f"{name}.gh3tex").write_bytes(
            b"GH3T" + struct.pack("<iiiii", 1, unity_format, image.width, image.height, len(levels)) + data
        )
        report["formats"][name] = {
            "bytes": len(data),
            "encodeSeconds": timings,
            "cpuSeconds": cpu,
            "medianSeconds": statistics.median(timings),
            "psnrTopLevel": psnr(name, data, levels[0]),
        }
    json.dump(report, sys.stdout, indent=2)
    print()


if __name__ == "__main__":
    main()
