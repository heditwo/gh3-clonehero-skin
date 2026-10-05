#!/usr/bin/env python3
import argparse
import json
from pathlib import Path

import cv2
import numpy as np
from PIL import Image


def images(snapshot):
    return {image["name"]: image for image in snapshot["images"] if image["visible"]}


def opaque_mask(snapshot, visible, assets):
    mask = np.zeros((snapshot["height"], snapshot["width"]), np.uint8)
    for image in visible.values():
        if image["name"] in ("GH3 highwayEdges", "GH3 activeHighway", "GH3 readyFrame", "GH3 readyLightning"):
            continue
        texture = assets / (image["texture"] + ".png")
        alpha = np.array(Image.open(texture).convert("RGBA"))[:, :, 3] * image["color"][3]
        height, width = alpha.shape
        target = np.array([[x, snapshot["height"] - y] for x, y in image["points"]], np.float32)
        left, top = np.floor(target.min(axis=0)).astype(int)
        right, bottom = np.ceil(target.max(axis=0)).astype(int)
        left, top = max(0, left), max(0, top)
        right, bottom = min(mask.shape[1], right), min(mask.shape[0], bottom)
        if left >= right or top >= bottom:
            continue
        source = np.array([[0, height], [0, 0], [width, 0], [width, height]], np.float32)
        transform = cv2.getPerspectiveTransform(source, target - np.array([left, top], np.float32))
        warped = cv2.warpPerspective(alpha.astype(np.float32), transform, (right - left, bottom - top))
        mask[top:bottom, left:right] |= (warped > 254).astype(np.uint8)
    return cv2.erode(mask, np.ones((3, 3), np.uint8)).astype(bool)


def compare(before, after, assets):
    old = json.loads(before.read_text())
    new = json.loads(after.read_text())
    a, b = images(old), images(new)
    if a.keys() != b.keys():
        raise AssertionError(f"Visible images differ in {before.name}: {a.keys() ^ b.keys()}")
    maximum = 0
    for name in a:
        delta = float(np.max(np.abs(np.array(a[name]["points"]) - np.array(b[name]["points"]))))
        maximum = max(maximum, delta)
        if delta > 0.01:
            raise AssertionError(f"{before.name}: {name} moved by {delta:.4f} pixels")
        if not np.allclose(a[name]["color"], b[name]["color"], atol=0.0001):
            raise AssertionError(f"{before.name}: {name} changed color")
        if name != "GH3 activeHighway" and a[name]["texture"] != b[name]["texture"]:
            raise AssertionError(f"{before.name}: {name} changed texture")
    mask = opaque_mask(old, a, assets) & opaque_mask(new, b, assets)
    old_pixels = np.array(Image.open(before.with_suffix(".png")).convert("RGB"), dtype=np.int16)
    new_pixels = np.array(Image.open(after.with_suffix(".png")).convert("RGB"), dtype=np.int16)
    error = np.abs(old_pixels[mask] - new_pixels[mask])
    mean = float(error.mean())
    if mean > 0.25:
        raise AssertionError(f"{before.name}: opaque HUD mean RGB error is {mean:.4f}")
    return {"state": old["state"], "visible_images": len(a), "maximum_corner_error": maximum,
            "opaque_pixels": int(mask.sum()), "mean_rgb_error": mean,
            "changed_pixels": int(np.any(error, axis=1).sum())}


def main():
    parser = argparse.ArgumentParser(description="Compare captured HUD geometry and opaque pixels across a component refactor")
    parser.add_argument("before", type=Path)
    parser.add_argument("after", type=Path)
    parser.add_argument("--assets", type=Path, default=Path(__file__).parent / "assets")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    files = sorted(args.before.glob("state-*.json"))
    if not files:
        raise SystemExit("No captured HUD states")
    rows = [compare(file, args.after / file.name, args.assets) for file in files]
    report = {"states": rows, "maximum_corner_error": max(row["maximum_corner_error"] for row in rows),
              "maximum_mean_rgb_error": max(row["mean_rgb_error"] for row in rows)}
    args.output.write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
