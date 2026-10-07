#!/usr/bin/env python3
"""Convert ordinary or original movie files into seekable PNG frame banks."""

import argparse
from fractions import Fraction
from hashlib import sha256
import json
from pathlib import Path
import struct
import subprocess
import tempfile


def convert(source, output):
    source = Path(source); output = Path(output)
    digest = sha256(source.read_bytes()).hexdigest()
    stamp = output.with_suffix(output.suffix+".json")
    if output.is_file() and stamp.is_file() and json.loads(stamp.read_text()).get("sourceSha256") == digest: return
    probe = json.loads(subprocess.run(["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_streams", "-of", "json", str(source)],
        capture_output=True, text=True, check=True).stdout)["streams"][0]
    width, height = probe["width"], probe["height"]; rate = Fraction(probe["avg_frame_rate"])
    if not 1 <= width <= 4096 or not 1 <= height <= 4096 or not 0 < rate <= 240: raise ValueError("Unsupported movie dimensions or frame rate")
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="movie-", dir=output.parent) as temporary:
        folder = Path(temporary)
        subprocess.run(["ffmpeg", "-v", "error", "-i", str(source), "-map", "0:v:0", "-an", "-fps_mode", "passthrough", "-start_number", "0",
            str(folder/"%06d.png")], check=True)
        frames = sorted(folder.glob("*.png"))
        if not 1 <= len(frames) <= 100000 or len(frames)/float(rate) > 86400: raise ValueError("Movie exceeds its frame limit")
        offsets = [28 + (len(frames)+1)*8]
        for frame in frames: offsets.append(offsets[-1]+frame.stat().st_size)
        bank = folder/"movie.gh3video"
        with bank.open("wb") as stream:
            stream.write(b"GH3VID01" + struct.pack("<5I", width, height, len(frames), rate.numerator, rate.denominator))
            stream.write(struct.pack(f"<{len(offsets)}Q", *offsets))
            for frame in frames: stream.write(frame.read_bytes())
        bank.replace(output)
    stamp.write_text(json.dumps({"sourceSha256": digest, "frames": len(frames), "width": width, "height": height,
        "rate": [rate.numerator, rate.denominator], "sha256": sha256(output.read_bytes()).hexdigest()}, indent=2)+"\n")
    print(f"Converted {source.name}: {len(frames)} frames")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    convert(args.source, args.output)
