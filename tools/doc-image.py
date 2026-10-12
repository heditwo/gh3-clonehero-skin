#!/usr/bin/env python3
"""Save a capture as a documentation image in full colour (docs/images/README.md): JPEG for game captures, PNG for UI screenshots.

Never reduce a documentation image to a colour palette: a 256-colour palette makes the small, saturated HUD parts grey."""

import argparse
from pathlib import Path

from PIL import Image

JPEG_QUALITY = 90


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path, help="A .jpg (game captures) or .png (UI screenshots) file")
    parser.add_argument("--crop", help="x,y,width,height in source pixels")
    parser.add_argument(
        "--width", type=int, default=0, help="Scale down to this width (no change if the image is smaller)"
    )
    args = parser.parse_args()
    image = Image.open(args.source).convert("RGB")
    if args.crop:
        x, y, width, height = (int(value) for value in args.crop.split(","))
        image = image.crop((x, y, x + width, y + height))
    if args.width and image.width > args.width:
        image = image.resize((args.width, round(image.height * args.width / image.width)), Image.Resampling.LANCZOS)
    suffix = args.output.suffix.lower()
    if suffix in (".jpg", ".jpeg"):
        # 4:4:4 chroma: with the default 4:2:0, the thin coloured HUD lines and gem rims bleed.
        image.save(args.output, quality=JPEG_QUALITY, subsampling=0, optimize=True, progressive=True)
    elif suffix == ".png":
        image.save(args.output, optimize=True)
    else:
        raise SystemExit("The output must be a .jpg or .png file")
    print(f"{args.output} {image.width}x{image.height} {args.output.stat().st_size} bytes")


if __name__ == "__main__":
    main()
