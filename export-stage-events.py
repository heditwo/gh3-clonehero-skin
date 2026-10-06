#!/usr/bin/env python3
"""Export original GH3 lighting, camera, and performance cues for Clone Hero."""

import argparse
from collections import Counter
from hashlib import sha256
import json
from pathlib import Path

from gh3_formats import find_array, find_struct, key, pak_entries
from gh3_direction import performance_command


def extract(source, song_id):
    events = []
    unsupported = Counter()
    unsupported_performance = Counter()
    found = False
    for _, _, data in pak_entries(source):
        if len(data) < 28 or data[8:12] != bytes.fromhex("1c080204"):
            continue
        notes = find_array(data, song_id + "_lightshow_notes")
        if notes is not None:
            found = True
            for note in notes:
                if not isinstance(note, list) or len(note) not in (3, 4):
                    raise ValueError("Invalid GH3 lightshow note")
                time, pitch, length = note[:3]
                if time > 86400000 or pitch > 127 or length > 86400000:
                    raise ValueError("Invalid GH3 lightshow note values")
                events.append({"time": time / 1000, "type": "gh3-note", "value": str(pitch)})
        for script in find_array(data, song_id + "_lightshow") or []:
            name = script.get(key("scr"))
            parameters = script.get(key("params"), {})
            if name == key("LightShow_SetTime") or name == key("LightShow") and key("SetTime") in parameters.get(0, []):
                duration = parameters.get(key("time"))
                if not isinstance(duration, (int, float)) or not 0 <= duration <= 30:
                    raise ValueError("Invalid lightshow blend time")
                events.append({"time": script[key("time")] / 1000, "type": "blend", "value": str(duration)})
            else:
                unsupported[f"{name:08x}" if isinstance(name, int) else "unknown"] += 1
        for section, kind in [("cameras", "gh3-camera"), ("anim", "fret"), ("drums", "drum"), ("performance", "vocal"), ("crowd", "crowd")]:
            for row in find_array(data, song_id + "_" + section + "_notes") or []:
                if len(row) not in (3, 4) or row[0] > 86400000 or row[1] > 127 or row[2] > 86400000:
                    raise ValueError("Invalid GH3 direction note")
                events.append({"time": row[0] / 1000, "type": kind, "value": str(row[1]), "duration": row[2] / 1000})
        for section in ("performance", "anim"):
            for row in find_array(data, song_id + "_" + section) or []:
                event = performance_command(row)
                if event:
                    events.append(event)
                else:
                    unsupported_performance[f"{row.get(key('scr'), 0):08x}"] += 1
        for role, section in [("guitarist", "song_Expert"), ("bassist", "song_rhythm_Expert")]:
            notes = find_array(data, song_id + "_" + section) or []
            if len(notes) % 3:
                raise ValueError("Invalid GH3 instrument notes")
            for i in range(0, len(notes), 3):
                time, duration, mask = notes[i:i + 3]
                events.append({"time": time / 1000, "type": "strum", "role": role, "value": str(mask & 31), "duration": duration / 1000})
        for time in find_array(data, song_id + "_fretbars") or []:
            events.append({"time": time / 1000, "type": "beat"})
    if not found:
        raise ValueError(f"No lightshow found for {song_id}")
    return {
        "version": 1,
        "offsetSeconds": 0,
        "sourceSong": song_id,
        "sourceSha256": sha256(source).hexdigest(),
        "unmappedLightshowScripts": dict(unsupported),
        "unmappedPerformanceScripts": dict(unsupported_performance),
        "events": sorted(events, key=lambda event: event["time"]),
    }


def export_library(data_directory, destination):
    pak = (data_directory / "PAK/qb.pak.xen").read_bytes()
    pab = (data_directory / "PAK/qb.pab.xen").read_bytes()
    metadata = {}
    for _, _, data in pak_entries(pak + pab):
        if len(data) > 28 and data[8:12] == bytes.fromhex("1c080204"):
            for section in ("permanent_songlist_props", "download_songlist_props"):
                properties = find_struct(data, section)
                if properties:
                    metadata.update({song[key("Name")]: song for song in properties.values()
                                     if isinstance(song, dict) and key("Name") in song})
    destination.mkdir(parents=True, exist_ok=True)
    songs = []
    pyro = 0
    for song_id, properties in sorted(metadata.items()):
        path = data_directory / "SONGS" / f"{song_id}_song.pak.xen"
        if not path.is_file():
            continue
        result = extract(path.read_bytes(), song_id)
        filename = f"{song_id}.json"
        (destination / filename).write_text(json.dumps(result, separators=(",", ":"), allow_nan=False) + "\n")
        songs.append({"title": properties[key("title")], "artist": properties[key("artist")], "file": filename})
        pyro += sum(event["type"] == "gh3-note" and event["value"] == "56" for event in result["events"])
    (destination / "index.json").write_text(json.dumps({"version": 1, "songs": songs}, indent=2) + "\n")
    print(f"Exported {len(songs)} original song direction tracks with {pyro} pyro triggers to {destination}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("song_pak", type=Path, help="A song PAK, or the GH3 DATA folder with --library")
    parser.add_argument("--library", action="store_true", help="Export all song direction tracks and a title/artist index")
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
    if result["unmappedLightshowScripts"]:
        print(f"Unmapped lightshow scripts: {result['unmappedLightshowScripts']}")
