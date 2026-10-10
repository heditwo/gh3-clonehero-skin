#!/usr/bin/env python3
"""Build a labeled comparison sheet from matched frame folders. Each column is LABEL=GLOB."""

import argparse
import glob
from pathlib import Path

from PIL import Image, ImageDraw


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    parser.add_argument("columns", nargs="+", help="LABEL=GLOB; every glob must match the same number of frames")
    parser.add_argument("--width", type=int, default=480)
    parser.add_argument("--rows", help="Comma-separated row labels")
    args = parser.parse_args()
    columns = []
    for column in args.columns:
        label, pattern = column.split("=", 1)
        columns.append((label, sorted(glob.glob(pattern))))
    count = len(columns[0][1])
    if count == 0 or any(len(files) != count for _, files in columns):
        raise SystemExit("Every column needs the same, nonzero number of frames")
    labels = args.rows.split(",") if args.rows else [str(i) for i in range(count)]
    width = args.width
    height = width * 9 // 16
    header = 22
    side = 60
    sheet = Image.new("RGB", (side + width * len(columns), header + height * count), (16, 18, 24))
    draw = ImageDraw.Draw(sheet)
    for c, (label, files) in enumerate(columns):
        draw.text((side + c * width + 6, 5), label, fill=(235, 235, 235))
        for r, file in enumerate(files):
            image = Image.open(file).convert("RGB").resize((width, height), Image.LANCZOS)
            sheet.paste(image, (side + c * width, header + r * height))
    for r, label in enumerate(labels):
        draw.text((6, header + r * height + height // 2 - 6), label, fill=(235, 235, 235))
    sheet.save(args.output, quality=88)
    print(args.output)


if __name__ == "__main__":
    main()
