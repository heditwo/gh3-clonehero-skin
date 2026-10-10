"""Export original GH3 lighting, camera, and performance cues for Clone Hero."""

import argparse
import json
import os
import subprocess
from pathlib import Path

from pipeline import songtool
from pipeline.gh3.formats import find_struct, key, pak_entries


def extract(source, song_id):
    result = subprocess.run(
        ["dotnet", str(songtool.path()), "events-stdin", song_id],
        input=source,
        capture_output=True,
        env={**os.environ, "MISE_QUIET": "1"},
    )
    if result.returncode:
        raise ValueError(result.stderr.decode(errors="replace").strip())
    return json.loads(result.stdout)


def export_library(data_directory, destination):
    pak = (data_directory / "PAK/qb.pak.xen").read_bytes()
    pab = (data_directory / "PAK/qb.pab.xen").read_bytes()
    metadata = {}
    for _, _, data in pak_entries(pak + pab):
        if len(data) > 28 and data[8:12] == bytes.fromhex("1c080204"):
            for section in ("permanent_songlist_props", "download_songlist_props"):
                properties = find_struct(data, section)
                if properties:
                    metadata.update(
                        {
                            song[key("Name")]: song
                            for song in properties.values()
                            if isinstance(song, dict) and key("Name") in song
                        }
                    )
    destination.mkdir(parents=True, exist_ok=True)
    songs = []
    missing_metadata = []
    exported = 0
    pyro = 0
    for path in sorted((data_directory / "SONGS").glob("*_song.pak.xen")):
        song_id = path.name.removesuffix("_song.pak.xen")
        properties = metadata.get(song_id)
        result = extract(path.read_bytes(), song_id)
        filename = f"{song_id}.json"
        (destination / filename).write_text(json.dumps(result, separators=(",", ":"), allow_nan=False) + "\n")
        if properties is not None and key("title") in properties and key("artist") in properties:
            songs.append({"title": properties[key("title")], "artist": properties[key("artist")], "file": filename})
        else:
            missing_metadata.append({"song": song_id, "file": filename})
        exported += 1
        pyro += sum(event["type"] == "gh3-note" and event["value"] == "56" for event in result["events"])
    (destination / "index.json").write_text(json.dumps({"version": 1, "songs": songs}, indent=2) + "\n")
    (destination / "export-report.json").write_text(
        json.dumps({"exported": exported, "indexed": len(songs), "missingMetadata": missing_metadata}, indent=2) + "\n"
    )
    print(f"Exported {exported} original song direction tracks with {pyro} pyro triggers to {destination}")
    if missing_metadata:
        print(
            f"{len(missing_metadata)} exports need title/artist metadata for automatic lookup. See export-report.json."
        )


if __name__ == "__main__":
    parser = argparse.ArgumentParser(prog="python -m pipeline.events", description=__doc__)
    parser.add_argument("song_pak", type=Path, help="A song PAK, or the GH3 DATA folder with --library")
    parser.add_argument(
        "--library", action="store_true", help="Export all song direction tracks and a title/artist index"
    )
    parser.add_argument("--song-id", help="GH3 internal song name; defaults to the PAK filename prefix")
    parser.add_argument("--output", required=True, type=Path, help="stage-events.json in the Clone Hero song folder")
    parser.add_argument("--offset", type=float, default=0, help="Cue offset in seconds for an audio edit")
    args = parser.parse_args()
    if args.library:
        export_library(args.song_pak, args.output)
        raise SystemExit(0)
    song_id = args.song_id or args.song_pak.name.split("_song.")[0]
    result = extract(args.song_pak.read_bytes(), song_id)
    result["offsetSeconds"] = args.offset
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("x") as output:
        json.dump(result, output, indent=2, allow_nan=False)
        output.write("\n")
    pyro = sum(event["type"] == "gh3-note" and event["value"] == "56" for event in result["events"])
    print(f"Exported {len(result['events'])} cues, including {pyro} pyro triggers, to {args.output}")
    pending = sum(call["result"]["status"] != "implemented" for call in result["playback"])
    if pending:
        print(f"Retained {pending} script calls with partial or missing playback. See the output playback report.")
