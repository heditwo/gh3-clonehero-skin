import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys


video, chart, binary = map(Path, sys.argv[1:])
probe = json.loads(subprocess.check_output([
    "ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries",
    "stream=width,height,r_frame_rate,avg_frame_rate,nb_frames,duration", "-of", "json", str(video),
]))
manifest = {
    "video": str(video.resolve()),
    "video_sha256": hashlib.sha256(video.read_bytes()).hexdigest(),
    "stream": probe["streams"][0],
    "binary": str(binary.resolve()),
    "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
    "chart": str(chart.resolve()),
    "chart_sha256": {path.name: hashlib.sha256(path.read_bytes()).hexdigest()
                     for path in sorted(chart.iterdir()) if path.is_file()},
    "options": {name: value for name, value in sorted(os.environ.items())
                if name.startswith(("GH3_SKIN_", "GH3_STOCK_", "DOTNET_GC"))},
}
timing_path = Path(str(video) + ".frames.json")
if timing_path.is_file():
    timing = json.loads(timing_path.read_text())
    manifest["frame_timing"] = {
        "source": str(timing_path.resolve()),
        "sha256": hashlib.sha256(timing_path.read_bytes()).hexdigest(),
        "summary": {key: value for key, value in timing.items() if key != "frames"},
    }
stage_name = os.environ.get("GH3_SKIN_STAGE", "budokan")
stage_dir = binary.parent / "GH3Skin/assets/stages" / stage_name
if stage_name != "off" and (stage_dir / "stage.json").is_file():
    manifest["stage"] = {
        "name": stage_name,
        "files_sha256": {str(path.relative_to(stage_dir)): hashlib.sha256(path.read_bytes()).hexdigest()
                         for path in sorted(stage_dir.rglob("*")) if path.is_file()},
    }
    library_dir = stage_dir.parent / "events"
    if library_dir.is_dir():
        manifest["cue_library_sha256"] = {path.name: hashlib.sha256(path.read_bytes()).hexdigest()
                                          for path in sorted(library_dir.glob("*.json"))}
Path(str(video) + ".json").write_text(json.dumps(manifest, indent=2) + "\n")
