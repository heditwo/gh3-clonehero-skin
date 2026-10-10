"""Count clips, bones, frames and bytes of the current GH3CLP01 clip library."""

import gzip
import json
import struct
import sys
from pathlib import Path

root = Path(sys.argv[1])
performance = json.loads((root / "performance.json").read_text())
rows = {}
for role in ["guitarist", "bassist", "drummer", "singer"]:
    files = sorted((root / role).glob("*.bin.gz"))
    stats = dict(
        clips=0, gz=0, raw=0, frames=0, tracks=0, rig=0, seconds=0.0, long=0, long_gz=0, max_frames=0, max_tracks=0
    )
    for path in files:
        with gzip.open(path, "rb") as stream:
            header = stream.read(24)
            magic, rig, frames, count, duration = header[:8], *struct.unpack("<IIIf", header[8:])
        stats["clips"] += 1
        stats["gz"] += path.stat().st_size
        stats["raw"] += 24 + count * 8 + frames * count * 28
        stats["frames"] += frames
        stats["tracks"] += count
        stats["rig"] = rig
        stats["seconds"] += duration
        stats["max_frames"] = max(stats["max_frames"], frames)
        stats["max_tracks"] = max(stats["max_tracks"], count)
        if duration > 30:
            stats["long"] += 1
            stats["long_gz"] += path.stat().st_size
    rows[role] = stats
print(json.dumps(rows, indent=1))
