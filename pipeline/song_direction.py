"""Song direction sidecars for a Clone Hero chart pack of GH3 songs: stage-events.json for each song folder of the pack.

`measure` compares the Expert guitar notes of each pack chart with the notes of every GH3 song (DATA/SONGS) and records
the GH3 song, the time offset, and the chart hashes in the pack file (pipeline/song_packs/<pack>.json). Songs whose
notes do not match one GH3 song are flagged, not guessed.

`export` writes the sidecars from the pack file, the GH3 direction of pipeline.events (assets/stages/events), and the
song data of GH3 (the singer, the count-off, and the career venue). It needs no pack download.
"""

import argparse
import hashlib
import json
import time
from pathlib import Path

import numpy as np

from pipeline.characters import slug
from pipeline.gh3.data import Source
from pipeline.gh3.formats import find_array, find_struct, is_qb_file, key, pak_entries
from pipeline.paths import ROOT, SONG_DIRECTION, STAGES
from pipeline.venues import VENUES, stage_id

PACKS = ROOT / "pipeline/song_packs"
EVENTS = STAGES / "events"
OUTPUT = SONG_DIRECTION
SIDECAR = "stage-events.json"
README = "README-GH3-DIRECTION.md"
PACK_RECORD = "gh3-direction-pack.json"
# The chart SHA-256 of each sidecar in sha256sum format; the install scripts of the direction zip read it.
CHART_INDEX = "gh3-direction-charts.sha256"
INSTALL_SCRIPT = "install-direction"
LIBRARY_ID = "gh3-band-v1"

# MIDI (Standard MIDI File 1.0): header and track chunks, meta events, and the Expert guitar notes of a Rock Band /
# Guitar Hero chart (PART GUITAR notes 96-100, green to orange).
MIDI_HEADER = b"MThd"
MIDI_TRACK = b"MTrk"
MIDI_META = 0xFF
MIDI_SYSEX = (0xF0, 0xF7)
MIDI_META_TRACK_NAME = 0x03
MIDI_META_TEMPO = 0x51
MIDI_NOTE_ON = 0x90
MIDI_STATUS_MASK = 0xF0
MIDI_PROGRAM_CHANGE = 0xC0
MIDI_CHANNEL_PRESSURE = 0xD0
MIDI_DEFAULT_TEMPO = 500000
MICROSECONDS = 1_000_000
EXPERT_GREEN = 96
EXPERT_ORANGE = 100
GUITAR_TRACK = "PART GUITAR"

# GH3 note arrays (<song>_song_<part>): rows of time (ms), length (ms), and fret mask; bits 0-4 are the lanes.
GH3_NOTE_ROW = 3
GH3_LANE_MASK = 0x1F
GH3_PARTS = ("song_expert", "song_guitarcoop_expert")
MILLISECONDS = 1000

# Engineering limits of the note comparison. Notes match when they are within one MIDI tick of 1/480 beat at 300 BPM
# plus the GH3 millisecond rounding (both are below 2 ms in the pack); a pack chart is the GH3 chart when nearly every
# note and lane matches. The offset search looks at the first notes, within the length of a GH3 intro.
MATCH_SECONDS = 0.004
MATCHED_SHARE = 0.99
OFFSET_NOTES = 200
OFFSET_WINDOW_SECONDS = 5.0
OFFSET_BIN_SECONDS = 0.001
OFFSET_CANDIDATES = 8
CANDIDATE_COUNT_RATIO = 0.2

# create_band (guitar_character.qb): the `singer` field of a song selects the vocalist profile. Other values (`Male`)
# give the band library singer, so the sidecar suggests nothing.
SINGER_PROFILES = {key("female"): "Singer_Female", key("bret"): "Singer_Bret"}
DEFAULT_COUNTOFF = "sticks_normal"
# The sidecar keeps the GH3 fretbars (`beat`): they set the band's tempo as in GH3 (the chart's tempo map gives the other
# timing; StageSongDirection). The strums stay too: GH3 selects the strum clip by the GH3 note length
# (short_strum_max_gem_length), and pack charts give a non-sustained note a length of one tick. A co-op chart has other
# guitar notes, so its strums come from the chart.
GESTURE_CUES = ("strum",)
SOLO_PART = "song_expert"
EVENT_DEFAULTS = {"duration": 0, "role": "", "immediate": False, "loop": False, "count": 1, "disableArms": 0}


def _variable(data, offset):
    value = 0
    for _ in range(4):
        byte = data[offset]
        offset += 1
        value = (value << 7) | (byte & 0x7F)
        if byte < 0x80:
            return value, offset
    raise ValueError("invalid MIDI variable length")


def _track_events(data):
    """(tick, kind, payload) of one MIDI track: ('meta', type, bytes) or ('note', status, note, velocity)."""
    offset, tick, running = 0, 0, 0
    while offset < len(data):
        delta, offset = _variable(data, offset)
        tick += delta
        status = data[offset]
        if status & 0x80:
            offset += 1
        else:
            status = running
        if status == MIDI_META:
            kind = data[offset]
            length, offset = _variable(data, offset + 1)
            yield tick, ("meta", kind, data[offset : offset + length])
            offset += length
            running = 0
        elif status in MIDI_SYSEX:
            length, offset = _variable(data, offset)
            offset += length
            running = 0
        else:
            running = status
            size = 1 if status & MIDI_STATUS_MASK in (MIDI_PROGRAM_CHANGE, MIDI_CHANNEL_PRESSURE) else 2
            values = data[offset : offset + size]
            offset += size
            yield tick, ("note", status & MIDI_STATUS_MASK, values[0], values[-1] if size == 2 else 0)


def midi_guitar_chords(path):
    """[(seconds, lane mask)] of the Expert guitar chords of a notes.mid, on the chart's tempo map."""
    data = path.read_bytes()
    if data[:4] != MIDI_HEADER:
        raise ValueError(f"{path}: not a MIDI file")
    header = int.from_bytes(data[4:8], "big")
    resolution = int.from_bytes(data[12:14], "big")
    offset = 8 + header
    tempos, chords = [], {}
    while offset < len(data):
        size = int.from_bytes(data[offset + 4 : offset + 8], "big")
        if data[offset : offset + 4] == MIDI_TRACK:
            events = list(_track_events(data[offset + 8 : offset + 8 + size]))
            names = [
                e[2].decode("utf-8", "replace").strip() for _, e in events if e[:2] == ("meta", MIDI_META_TRACK_NAME)
            ]
            for tick, event in events:
                if event[:2] == ("meta", MIDI_META_TEMPO):
                    tempos.append((tick, int.from_bytes(event[2], "big")))
            if names and names[0].upper() == GUITAR_TRACK:
                for tick, event in events:
                    if (
                        event[:2] == ("note", MIDI_NOTE_ON)
                        and event[3] > 0
                        and EXPERT_GREEN <= event[2] <= EXPERT_ORANGE
                    ):
                        chords[tick] = chords.get(tick, 0) | 1 << (event[2] - EXPERT_GREEN)
        offset += 8 + size
    tempos.sort()
    if not tempos or tempos[0][0] > 0:
        tempos.insert(0, (0, MIDI_DEFAULT_TEMPO))

    def seconds(tick):
        elapsed, previous, tempo = 0.0, 0, MIDI_DEFAULT_TEMPO
        for at, value in tempos:
            if at > tick:
                break
            elapsed += (at - previous) * tempo / resolution / MICROSECONDS
            previous, tempo = at, value
        return elapsed + (tick - previous) * tempo / resolution / MICROSECONDS

    return [(seconds(tick), mask) for tick, mask in sorted(chords.items())]


def gh3_notes(data_directory):
    """{(song, part): (times in s, lane masks)} of the GH3 Expert guitar parts of every song PAK."""
    result = {}
    for path in sorted((data_directory / "SONGS").glob("*_song.pak.xen")):
        song = path.name.removesuffix("_song.pak.xen")
        for _, _, data in pak_entries(path.read_bytes()):
            if not is_qb_file(data):
                continue
            for part in GH3_PARTS:
                rows = find_array(data, f"{song}_{part}")
                if rows:
                    notes = np.array(rows, dtype=np.int64).reshape(-1, GH3_NOTE_ROW)
                    result[song, part] = (notes[:, 0] / MILLISECONDS, notes[:, 2] & GH3_LANE_MASK)
    return result


def compare(pack_times, pack_lanes, gh3_times, gh3_lanes):
    """(offset in s, share of pack notes with a GH3 note at the offset, share with the same lanes). offset = pack - GH3."""
    differences = []
    for at in pack_times[:OFFSET_NOTES]:
        low, high = np.searchsorted(gh3_times, [at - OFFSET_WINDOW_SECONDS, at + OFFSET_WINDOW_SECONDS])
        differences.append(at - gh3_times[low:high])
    if not differences or not sum(len(d) for d in differences):
        return 0.0, 0.0, 0.0
    differences = np.concatenate(differences)
    bins = np.round(differences / OFFSET_BIN_SECONDS).astype(np.int64)
    values, counts = np.unique(bins, return_counts=True)
    # Repeated riffs put many differences on whole beats too, so the most frequent bins are only candidates: the one
    # that matches the most notes and lanes over the whole song wins.
    results = []
    for candidate in values[np.argsort(counts)[-OFFSET_CANDIDATES:]] * OFFSET_BIN_SECONDS:
        near = differences[np.abs(differences - candidate) <= MATCH_SECONDS]
        offset = float(np.median(near))
        shifted = gh3_times + offset
        index = np.clip(np.searchsorted(shifted, pack_times), 1, len(shifted) - 1)
        before = np.abs(shifted[index - 1] - pack_times) <= np.abs(shifted[index] - pack_times)
        nearest = np.where(before, index - 1, index)
        hit = np.abs(shifted[nearest] - pack_times) <= MATCH_SECONDS
        lanes = hit & (gh3_lanes[nearest] == pack_lanes)
        results.append((float(lanes.mean()), float(hit.mean()), round(offset, 3) + 0.0))
    same, matched, offset = max(results)
    return offset, matched, same


def _sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _ini_value(path, name):
    for line in path.read_text(encoding="utf-8-sig", errors="replace").splitlines():
        field, _, value = line.partition("=")
        if field.strip().lower() == name:
            return value.strip()
    return None


def measure_folder(folder, notes, listed, dates):
    """The pack file entry of one song folder: the GH3 song and part, the offset, the scores, and the status."""
    entry = {"folder": folder.name}
    chart = folder / "notes.mid"
    ini = folder / "song.ini"
    if not chart.exists():
        return entry | {"status": "flagged", "reason": "no notes.mid (other chart formats are not measured)"}
    entry |= {"chart": chart.name, "chartSha256": _sha256(chart)}
    if (folder.name, chart.name) in dates:
        entry["chartDate"] = dates[folder.name, chart.name]
    if ini.exists():
        entry["iniSha256"] = _sha256(ini)
    delay = float(_ini_value(ini, "delay") or 0) if ini.exists() else 0.0
    chords = midi_guitar_chords(chart)
    if not chords:
        return entry | {"status": "flagged", "reason": "no Expert guitar notes"}
    times = np.array([time for time, _ in chords])
    lanes = np.array([mask for _, mask in chords])
    scores = []
    for (song, part), (gh3_times, gh3_lanes) in notes.items():
        if abs(len(gh3_times) - len(times)) > CANDIDATE_COUNT_RATIO * len(times):
            continue
        offset, matched, same = compare(times, lanes, gh3_times, gh3_lanes)
        scores.append((min(matched, same), song, part, offset, matched, same))
    scores.sort(reverse=True)
    if not scores or scores[0][0] < MATCHED_SHARE:
        best = f" (best {scores[0][1]} {scores[0][0]:.3f})" if scores else ""
        return entry | {"status": "flagged", "reason": "the notes match no GH3 song" + best}
    good = [score for score in scores if score[0] >= MATCHED_SHARE]
    # GH3 has test copies of some songs (mutetest has the notes of Cult of Personality): a song of the GH3 song lists
    # wins over songs that are not in them.
    if len({score[1] for score in good}) > 1 and len({score[1] for score in good if score[1] in listed}) == 1:
        good = [score for score in good if score[1] in listed]
    _, song, part, offset, matched, same = good[0]
    entry |= {
        "song": song,
        "part": part,
        "offsetSeconds": offset,
        "notes": len(times),
        "matched": round(matched, 4),
        "lanes": round(same, 4),
    }
    others = sorted({score[1] for score in scores if score[0] >= MATCHED_SHARE and score[1] != song})
    if others:
        entry["alsoMatches"] = others
    if len({score[1] for score in good}) > 1:
        return entry | {"status": "flagged", "reason": "the notes match more than one GH3 song"}
    if delay:
        return entry | {"status": "flagged", "reason": f"song.ini delay {delay} ms is not measured"}
    return entry | {"status": "matched"}


def measure(pack_path, charts, data_directory):
    """Measure every song folder of a downloaded pack and write the results into the pack file."""
    pack = json.loads(pack_path.read_text())
    notes = gh3_notes(Path(data_directory))
    folders = sorted((p for p in charts.iterdir() if p.is_dir()), key=lambda p: p.name)
    listed = set(song_data(Path(data_directory)))
    dates = {}
    listing = charts / "listing.json"
    if listing.exists():
        for name, folder in json.loads(listing.read_text())["folders"].items():
            dates |= {(name, file["name"]): file["modified"] for file in folder["files"]}
        pack["source"]["listed"] = time.strftime("%Y-%m-%d", time.gmtime(listing.stat().st_mtime))
    pack["songs"] = [measure_folder(folder, notes, listed, dates) for folder in folders]
    pack_path.write_text(json.dumps(pack, indent=2, ensure_ascii=False) + "\n")
    for entry in pack["songs"]:
        result = f"{entry['song']} {entry['offsetSeconds']:+.4f} s" if "song" in entry else ""
        print(f"{entry['status']:8} {entry['folder']}: {result} {entry.get('reason', '')}".rstrip())
    flagged = sum(entry["status"] != "matched" for entry in pack["songs"])
    print(f"{len(pack['songs']) - flagged} matched, {flagged} flagged; wrote {pack_path}")


def song_data(data_directory):
    """{song: (singer character ID or None, count-off, career stage ID or None)} from the GH3 song list and career."""
    source = Source(data_directory)
    properties, career = {}, {}
    for kind, _, data in source.load("PAK/qb.pak.xen"):
        if kind != key(".qb"):
            continue
        for section in ("permanent_songlist_props", "download_songlist_props"):
            for row in (find_struct(data, section) or {}).values():
                if isinstance(row, dict) and isinstance(row.get(key("Name")), str):
                    properties[row[key("Name")].lower()] = row
        for tier in (find_struct(data, "GH3_Career_Songs") or {}).values():
            if isinstance(tier, dict) and key("level") in tier:
                for song in tier[key("songs")]:
                    career[song] = tier[key("level")]
    levels = {key(f"load_{zone}"): stage_id(zone) for zone in VENUES}
    result = {}
    for song, row in properties.items():
        profile = SINGER_PROFILES.get(row.get(key("singer")))
        result[song] = (
            f"gh3.{slug(profile)}" if profile else None,
            row.get(key("countoff")) if isinstance(row.get(key("countoff")), str) else DEFAULT_COUNTOFF,
            levels.get(career.get(key(song))),
        )
    return result


def _event(cue, offset):
    event = {"time": round(cue["time"] + offset, 6), "type": cue["type"], "value": cue["value"]}
    event |= {name: cue[name] for name, default in EVENT_DEFAULTS.items() if cue.get(name, default) != default}
    if cue.get("velocity") is not None:
        event["velocity"] = cue["velocity"]
    return event


def sidecar(direction, offset, data, part=SOLO_PART):
    """The stage-events.json (version 2) of one pack song from its GH3 direction export; part: the GH3 notes of the chart."""
    singer, countoff, stage = data
    result = {
        "version": 2,
        "library": LIBRARY_ID,
        "sourceSong": direction["sourceSong"],
        "countoff": countoff,
    }
    if stage:
        result["stage"] = stage
    if singer:
        result["characters"] = {"singer": singer}
    # The GH3 times are on the GH3 note clock; the file offset moves them to the pack chart (offset = pack - GH3).
    result["offsetSeconds"] = offset
    left_out = () if part == SOLO_PART else GESTURE_CUES
    result["events"] = [_event(cue, 0) for cue in direction["events"] if cue["type"] not in left_out]
    return result


def readme(pack, written, flagged):
    """The README of the direction zip of a pack (ASD-STE100)."""
    source = pack["source"]
    dates = sorted({entry["chartDate"] for entry in pack["songs"] if "chartDate" in entry})
    lines = [
        f'# GH3 direction for the chart pack "{pack["name"]}"',
        "",
        "This archive is for the GH3 Gameplay Skin, a mod for Clone Hero. It contains one file, `stage-events.json`, for "
        f'each of {written} song folders of the chart pack "{pack["name"]}". The file gives the mod the original GH3 '
        "direction of the song: the camera cuts, the light show, the band performance, the singer's facial animation, "
        "the drummer, the crowd, the GH3 venue, and the GH3 singer of the song.",
        "",
        "The archive does not contain charts, audio, or song.ini files. It does not change the files of your chart pack.",
        "",
        "## Target pack",
        "",
        f"- Pack: {source['index']}.",
        f"- Folder: {source['folder']} (Drive folder ID `{source['driveFolderId']}`), listed on {source.get('listed', '?')}.",
        f"- Chart dates in the pack (M/D/YY, as Google Drive shows them): {', '.join(dates)}." if dates else None,
        f"- `{PACK_RECORD}` gives the SHA-256 of each chart that the files fit (`chartSha256`), and the time offset of "
        f"each song. `{CHART_INDEX}` gives the same SHA-256 values in the format of `sha256sum`.",
        "",
        "## Install with the script",
        "",
        "Each `stage-events.json` must go into the folder that has the matching chart (`notes.mid`). The script finds "
        "these folders for you: it calculates the SHA-256 of each `notes.mid` in your songs folder and in all its "
        "subfolders, and it compares them with the SHA-256 values of this archive. The folder names and the folder "
        "depth do not matter.",
        "",
        "1. Extract this archive into a new folder. Do not extract it into your songs folder.",
        f"2. On Windows: double-click `{INSTALL_SCRIPT}.bat` and type your Clone Hero songs folder, or drag the songs "
        f"folder onto `{INSTALL_SCRIPT}.bat`. On Linux or macOS: open a terminal in the extracted folder and type "
        f'`sh {INSTALL_SCRIPT}.sh "<songs folder>"`.',
        "3. The script shows the list of the files that it will copy, and the files that are already there. Then it "
        "asks you to confirm. Type `y` to copy the files.",
        "4. In Clone Hero, scan the songs again. Play a song of the pack.",
        "",
        "The script never replaces a file. To see the list only, add `--dry-run` (Linux, macOS) or `-DryRun` "
        "(Windows). To copy without the question, add `--yes` or `-Yes`. The Linux script needs only `sh`, `find`, "
        "and `sha256sum` (or `shasum` on macOS).",
        "",
        "## Install by hand",
        "",
        "1. Find the song folders of the chart pack in your Clone Hero songs folder. Each song has one folder, for "
        "example `Killswitch Engage - My Curse`. The song folders can be in subfolders, for example "
        "`Guitar Hero III/Quickplay/<song>` and `Guitar Hero III/Bonus/<song>`, or in two folders with the same "
        "name, for example `gh3dlc/gh3dlc/<song>`.",
        "2. For each song folder of this archive, copy its `stage-events.json` into your song folder with the same "
        "name. If all song folders are in one folder, you can extract the archive into that folder and accept to "
        "merge the folders.",
        "3. In Clone Hero, scan the songs again.",
        "",
        f"If you renamed a song folder, use the script, or compare the SHA-256 of its `notes.mid` with `{PACK_RECORD}`.",
        "",
        "## Remove",
        "",
        "Delete `stage-events.json` from the song folders. The songs then get the mod's generated direction.",
        "",
        "## Notes",
        "",
        '- The mod uses the venue and the singer of the file only when "Songs can select their stage and band" is on '
        "in the mod's picker (F6), and only for the roles that you did not select.",
        "- A chart of another version of the pack can have other note times. Then the direction does not align.",
    ]
    if flagged:
        lines.append(f"- These song folders have no file, because their notes do not match GH3: {', '.join(flagged)}.")
    return "\n".join(line for line in lines if line is not None) + "\n"


def chart_index(pack):
    """`<chart SHA-256>  <song folder>/<chart>` for each matched song, sorted by folder (the sha256sum format)."""
    entries = sorted((entry for entry in pack["songs"] if entry["status"] == "matched"), key=lambda e: e["folder"])
    return "".join(f"{entry['chartSha256']}  {entry['folder']}/{entry['chart']}\n" for entry in entries)


def export(pack_path, events, data_directory, output):
    """Write the sidecar of every matched song of the pack into <output>/<pack id>/<song folder>/stage-events.json."""
    pack = json.loads(pack_path.read_text())
    data = song_data(Path(data_directory))
    destination = output / pack["id"]
    written = 0
    for entry in pack["songs"]:
        if entry["status"] != "matched":
            continue
        direction = json.loads((events / f"{entry['song']}.json").read_text())
        song = data.get(entry["song"], (None, DEFAULT_COUNTOFF, None))
        folder = destination / entry["folder"]
        folder.mkdir(parents=True, exist_ok=True)
        text = json.dumps(
            sidecar(direction, entry["offsetSeconds"], song, entry["part"]), separators=(",", ":"), ensure_ascii=False
        )
        (folder / SIDECAR).write_text(text + "\n")
        written += 1
    flagged = [entry["folder"] for entry in pack["songs"] if entry["status"] != "matched"]
    (destination / README).write_text(readme(pack, written, flagged))
    (destination / PACK_RECORD).write_text(json.dumps(pack, indent=2, ensure_ascii=False) + "\n")
    (destination / CHART_INDEX).write_text(chart_index(pack))
    print(f"Wrote {written} sidecars to {destination}")
    if flagged:
        print(f"{len(flagged)} flagged songs have no sidecar: {', '.join(flagged)}")


def export_song(song, offset, events, data_directory, output):
    """Write the sidecar of one GH3 song for one chart folder (a chart that is not in a pack file)."""
    direction = json.loads((events / f"{song}.json").read_text())
    data = song_data(Path(data_directory)).get(song, (None, DEFAULT_COUNTOFF, None))
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("x") as file:
        file.write(json.dumps(sidecar(direction, offset, data), separators=(",", ":"), ensure_ascii=False) + "\n")
    print(f"Wrote {output} from {song} with offset {offset} s")


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    commands = parser.add_subparsers(dest="command", required=True)
    measured = commands.add_parser("measure", help="Compare a downloaded pack with GH3 and update the pack file")
    measured.add_argument("--pack", required=True, type=Path, help="pipeline/song_packs/<pack>.json")
    measured.add_argument(
        "--charts", required=True, type=Path, help="The pack's song folders (tools/fetch-song-pack.py)"
    )
    measured.add_argument("--data", required=True, type=Path, help="The GH3 DATA folder")
    exported = commands.add_parser("export", help="Write the sidecars of the matched songs of a pack")
    exported.add_argument("--pack", type=Path, action="append", help="Pack files; default all of pipeline/song_packs")
    exported.add_argument("--events", type=Path, default=EVENTS, help="The GH3 direction export of pipeline.events")
    exported.add_argument("--data", required=True, type=Path, help="The GH3 DATA folder")
    exported.add_argument("--output", type=Path, default=OUTPUT)
    single = commands.add_parser("song", help="Write the sidecar of one GH3 song for one chart folder")
    single.add_argument("--song", required=True, help="GH3 song ID (the name of DATA/SONGS/<song>_song.pak.xen)")
    single.add_argument("--offset", type=float, default=0.0, help="Seconds from the GH3 notes to the chart notes")
    single.add_argument("--events", type=Path, default=EVENTS, help="The GH3 direction export of pipeline.events")
    single.add_argument("--data", required=True, type=Path, help="The GH3 DATA folder")
    single.add_argument("--output", required=True, type=Path, help="<chart folder>/stage-events.json (not replaced)")
    args = parser.parse_args()
    if args.command == "song":
        export_song(args.song, args.offset, args.events, args.data, args.output)
        return
    if args.command == "measure":
        measure(args.pack, args.charts, args.data)
        return
    for pack in args.pack or sorted(PACKS.glob("*.json")):
        export(pack, args.events, args.data, args.output)


if __name__ == "__main__":
    main()
