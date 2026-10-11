"""Convert ordinary or original movie files into seekable PNG frame banks."""

import argparse
import json
import struct
import subprocess
import tempfile
from fractions import Fraction
from hashlib import sha256
from pathlib import Path

from pipeline.package.runtime import MOVIE_HEADER

MAX_SIZE = 4096
MAX_FRAME_RATE = 240
MAX_FRAMES = 100000
MAX_SECONDS = 86400


def convert(source, output):
    """Write a GH3VID01 frame bank (PNG frames) of a movie; skip it when its stamp has the same source hash."""
    source = Path(source)
    output = Path(output)
    digest = sha256(source.read_bytes()).hexdigest()
    stamp = output.with_suffix(output.suffix + ".json")
    if output.is_file() and stamp.is_file() and json.loads(stamp.read_text()).get("sourceSha256") == digest:
        return
    probe = json.loads(
        subprocess.run(
            ["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_streams", "-of", "json", str(source)],
            capture_output=True,
            text=True,
            check=True,
        ).stdout
    )["streams"][0]
    width, height = probe["width"], probe["height"]
    rate = Fraction(probe["avg_frame_rate"])
    if not 1 <= width <= MAX_SIZE or not 1 <= height <= MAX_SIZE or not 0 < rate <= MAX_FRAME_RATE:
        raise ValueError("Unsupported movie dimensions or frame rate")
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="movie-", dir=output.parent) as temporary:
        folder = Path(temporary)
        subprocess.run(
            [
                "ffmpeg",
                "-v",
                "error",
                "-i",
                str(source),
                "-map",
                "0:v:0",
                "-an",
                "-fps_mode",
                "passthrough",
                "-start_number",
                "0",
                str(folder / "%06d.png"),
            ],
            check=True,
        )
        frames = sorted(folder.glob("*.png"))
        if not 1 <= len(frames) <= MAX_FRAMES or len(frames) / float(rate) > MAX_SECONDS:
            raise ValueError("Movie exceeds its frame limit")
        offsets = [MOVIE_HEADER.size + (len(frames) + 1) * 8]
        for frame in frames:
            offsets.append(offsets[-1] + frame.stat().st_size)
        bank = folder / "movie.gh3video"
        with bank.open("wb") as stream:
            stream.write(MOVIE_HEADER.pack(b"GH3VID01", width, height, len(frames), rate.numerator, rate.denominator))
            stream.write(struct.pack(f"<{len(offsets)}Q", *offsets))
            for frame in frames:
                stream.write(frame.read_bytes())
        bank.replace(output)
    stamp.write_text(
        json.dumps(
            {
                "sourceSha256": digest,
                "frames": len(frames),
                "width": width,
                "height": height,
                "rate": [rate.numerator, rate.denominator],
                "sha256": sha256(output.read_bytes()).hexdigest(),
            },
            indent=2,
        )
        + "\n"
    )
    print(f"Converted {source.name}: {len(frames)} frames")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(prog="python -m pipeline.library.movies", description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    convert(args.source, args.output)
