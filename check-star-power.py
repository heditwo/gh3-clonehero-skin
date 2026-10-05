#!/usr/bin/env python3
import argparse
import json
import subprocess
from pathlib import Path

import numpy as np


def frames(path, start, duration):
    command = ["ffmpeg", "-hide_banner", "-loglevel", "error", "-nostdin", "-ss", str(start), "-i", str(path),
               "-t", str(duration), "-vf", "fps=60,scale=1600:900,crop=1008:520:300:380",
               "-pix_fmt", "rgb24", "-f", "rawvideo", "-"]
    with subprocess.Popen(command, stdout=subprocess.PIPE) as process:
        while data := process.stdout.read(1008 * 520 * 3):
            if len(data) != 1008 * 520 * 3:
                raise RuntimeError("Incomplete video frame")
            yield np.frombuffer(data, np.uint8).reshape(520, 1008, 3)
        if process.wait() != 0:
            raise RuntimeError(f"Could not decode {path}")


def main():
    parser = argparse.ArgumentParser(description="Compare active highway effects with GH3 reference frames")
    parser.add_argument("reference", type=Path)
    parser.add_argument("capture", type=Path)
    parser.add_argument("--capture-start", type=float, required=True)
    parser.add_argument("--reference-start", type=float, default=20)
    parser.add_argument("--duration", type=float, default=3)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    y, x = np.mgrid[380:900, 300:1308]
    left = 704 - 0.52 * (y - 400)
    right = 904 + 0.52 * (y - 400)
    mask = (((x < left - 10) & (x > left - 110) & ((y < 570) | (x > 510) | (y > 835))) |
            ((x > right + 10) & (x < right + 110) & ((y < 525) | (x < 1078) | (y > 820)))) & (y > 440)
    references = np.array([frame[mask] for frame in frames(args.reference, args.reference_start, args.duration)], dtype=np.float32)
    records = []
    for index, frame in enumerate(frames(args.capture, args.capture_start, args.duration)):
        pixels = frame[mask].astype(np.float32)
        errors = np.mean(np.abs(references - pixels), axis=(1, 2))
        matched = int(np.argmin(errors))
        reference = references[matched]
        bright_reference = reference.max(axis=1) > 100
        bright_capture = pixels.max(axis=1) > 100
        records.append({"frame": index, "reference_frame": matched, "mean_rgb_error": float(errors[matched]),
                        "bright_pixel_overlap": float(np.sum(bright_reference & bright_capture) /
                                                      max(1, np.sum(bright_reference | bright_capture)))})
    if not records:
        raise RuntimeError("No capture frames to compare")
    summary = {"frames": len(records), "pixels_per_frame": int(mask.sum()),
               "mean_rgb_error": float(np.mean([row["mean_rgb_error"] for row in records])),
               "maximum_frame_rgb_error": max(row["mean_rgb_error"] for row in records),
               "mean_bright_pixel_overlap": float(np.mean([row["bright_pixel_overlap"] for row in records])),
               "reference_start": args.reference_start, "capture_start": args.capture_start,
               "phase_independent": True}
    args.output.write_text(json.dumps({"summary": summary, "frames": records}, indent=2) + "\n")
    print(json.dumps(summary, indent=2))
    if summary["mean_rgb_error"] > 8 or summary["mean_bright_pixel_overlap"] < 0.85:
        raise SystemExit("Highway effects differ from the GH3 reference")


if __name__ == "__main__":
    main()
