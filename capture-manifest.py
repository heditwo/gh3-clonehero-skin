import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys


video, chart, binary = map(Path, sys.argv[1:])
probe = json.loads(subprocess.check_output([
    "ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries",
    "stream=width,height,r_frame_rate,nb_frames,duration", "-of", "json", str(video),
]))
manifest = {
    "video": str(video.resolve()),
    "stream": probe["streams"][0],
    "binary": str(binary.resolve()),
    "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
    "chart": str(chart.resolve()),
    "chart_sha256": {path.name: hashlib.sha256(path.read_bytes()).hexdigest()
                     for path in sorted(chart.iterdir()) if path.is_file()},
    "options": {name: value for name, value in sorted(os.environ.items())
                if name.startswith(("GH3_SKIN_", "GH3_STOCK_"))},
}
Path(str(video) + ".json").write_text(json.dumps(manifest, indent=2) + "\n")
