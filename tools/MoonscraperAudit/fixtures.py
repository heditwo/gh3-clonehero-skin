import argparse
import csv
import hashlib
import io
import json
import struct
from collections import defaultdict, deque
from pathlib import Path

import mido


def write_midi(path, tracks):
    midi = mido.MidiFile(type=1, ticks_per_beat=480)
    for name, events in tracks:
        track = mido.MidiTrack([mido.MetaMessage("track_name", name=name)])
        last = 0
        for tick, event in sorted(events, key=lambda item: item[0]):
            track.append(event.copy(time=tick - last))
            last = tick
        track.append(mido.MetaMessage("end_of_track"))
        midi.tracks.append(track)
    midi.save(path)


def note(start, length, number, velocity):
    return [
        (start, mido.Message("note_on", note=number, velocity=velocity)),
        (start + length, mido.Message("note_off", note=number, velocity=0)),
    ]


def create(root):
    if root.exists() and any(root.iterdir()):
        raise ValueError("Use an empty results folder so old output cannot affect the checks")
    root.mkdir(parents=True, exist_ok=True)
    tempo = ("TEMPO", [(0, mido.MetaMessage("set_tempo", tempo=500000))])
    write_midi(
        root / "input.mid",
        [
            tempo,
            ("CAMERAS", note(9600, 4800, 89, 75)),
            ("DRUMS", note(1920, 12, 70, 45) + note(3840, 240, 71, 75)),
            ("PART GUITAR", note(9600, 4800, 96, 100)),
            (
                "EVENTS",
                [(480, mido.MetaMessage("text", text="z_first")), (480, mido.MetaMessage("text", text="a_second"))],
            ),
        ],
    )
    write_midi(
        root / "commands.mid",
        [
            tempo,
            (
                "LIGHTSHOW",
                [
                    (960, mido.MetaMessage("text", text="lightshow_settime 0.25")),
                    (1920, mido.MetaMessage("text", text="z_first")),
                    (1920, mido.MetaMessage("text", text="a_second")),
                ],
            ),
            ("EVENTS", [(960, mido.MetaMessage("text", text="stage camera drummer"))]),
        ],
    )
    (root / "input.chart").write_text("""[Song]
{
  Name = "Preservation fixture"
  Resolution = 480
}
[SyncTrack]
{
  0 = B 120000
  0 = TS 4
}
[Events]
{
  480 = E "z_first"
  480 = E "a_second"
}
[CAMERAS]
{
  9600 = N 89 4800
  1920 = E z_first
  1920 = E a_second
}
[DRUMS]
{
  1920 = N 70 12
  3840 = N 71 240
}
[ExpertSingle]
{
  9600 = N 0 4800
}
""")


def midi_data(path):
    data = path.read_bytes()
    assert data[:8] == b"MThd\x00\x00\x00\x06"
    declared_count = struct.unpack_from(">H", data, 10)[0]
    tracks = []
    offset = 14
    while offset < len(data):
        assert data[offset : offset + 4] == b"MTrk"
        length = struct.unpack_from(">I", data, offset + 4)[0]
        end = offset + 8 + length
        assert end <= len(data)
        single_track = data[:10] + b"\x00\x01" + data[12:14] + data[offset:end]
        tracks.extend(mido.MidiFile(file=io.BytesIO(single_track)).tracks)
        offset = end
    result = {}
    for track in tracks:
        name = track.name
        tick = 0
        active = defaultdict(deque)
        rows = []
        for event in track:
            tick += event.time
            if event.type == "note_on" and event.velocity:
                active[event.channel, event.note].append((tick, event.velocity))
            elif event.type == "note_off" or event.type == "note_on" and not event.velocity:
                start, velocity = active[event.channel, event.note].popleft()
                rows.append(dict(type="note", tick=start, note=event.note, length=tick - start, velocity=velocity))
            elif event.type == "text":
                rows.append(dict(type="text", tick=tick, text=event.text))
        if rows:
            result[name] = rows
    return dict(declaredTrackCount=declared_count, actualTrackCount=len(tracks), tracks=result)


def model_data(path):
    with path.open() as stream:
        return list(csv.DictReader(stream, delimiter="\t"))


def report(root):
    if (root / "failure.txt").exists():
        raise RuntimeError((root / "failure.txt").read_text())
    assert (root / "complete.txt").exists(), "The editor probe did not complete"
    result = {
        "version": "1.5.13 Linux Universal",
        "runtime": (root / "complete.txt").read_text().strip(),
        "inputMidi": midi_data(root / "input.mid"),
        "outputMidi": midi_data(root / "output.mid"),
        "midiImportedModel": model_data(root / "midi-import.tsv"),
        "midiReimportedModel": model_data(root / "midi-reimport.tsv"),
        "chartImportedModel": model_data(root / "chart-import.tsv"),
        "chartReimportedModel": model_data(root / "chart-reimport.tsv"),
        "commandInputMidi": midi_data(root / "commands.mid"),
        "commandImportedModel": model_data(root / "commands-import.tsv"),
        "msceReimportedModel": model_data(root / "msce-reimport.tsv"),
        "chartExportReports": {path.name: path.read_text() for path in root.glob("*.report.txt")},
        "files": {
            path.name: hashlib.sha256(path.read_bytes()).hexdigest()
            for path in sorted(root.iterdir())
            if path.is_file() and path.suffix in (".mid", ".chart", ".msce", ".tsv")
        },
    }
    source = result["inputMidi"]["tracks"]
    output = result["outputMidi"]["tracks"]

    def model_note(model, track, number):
        return next(
            row
            for row in result[model]
            if row["track"] == track and row["type"] == "note" and int(row["note_or_text"]) == number
        )

    checks = {
        "cameraStartMovedFrom10To15Seconds": source["CAMERAS"][0]["tick"] == 9600
        and output["CAMERAS"][0]["tick"] == 14400
        and int(model_note("midiImportedModel", "CAMERAS", 89)["tick"]) == 14400,
        "cameraMovesAgainTo20SecondsOnReimport": int(model_note("midiReimportedModel", "CAMERAS", 89)["tick"]) == 19200,
        "shortDrumLength12TicksBecomesZeroOnImport": source["DRUMS"][0]["length"] == 12
        and int(model_note("midiImportedModel", "DRUMS", 70)["length"]) == 0,
        "velocities45And75Become100": [row["velocity"] for row in source["DRUMS"]] == [45, 75]
        and [row["velocity"] for row in output["DRUMS"]] == [100, 100],
        "equalTimeGlobalEventsReordered": [row["text"] for row in source["EVENTS"]] == ["z_first", "a_second"]
        and [row["note_or_text"] for row in result["midiImportedModel"] if row["track"] == "EVENTS"]
        == ["a_second", "z_first"],
        "equalTimeLocalEventsReordered": [
            row["note_or_text"]
            for row in result["chartReimportedModel"]
            if row["track"] == "CAMERAS" and row["type"] == "text"
        ]
        == ["a_second", "z_first"],
        "localEventSpaceBecomesUnderscoreOnChartExport": "960 = E lightshow_settime_0.25"
        in (root / "commands-export.chart").read_text(),
        "generalSaveSelectsMsceAndPreservesLocalEvent": "FileType=MoonscraperPropriety"
        in (root / "commands-save.chart.report.txt").read_text()
        and any(row["note_or_text"] == "lightshow_settime 0.25" for row in result["msceReimportedModel"]),
        "globalEventWithSpacesSurvivesChartExport": '960 = E "stage camera drummer"'
        in (root / "commands-export.chart").read_text(),
        "chartCameraTimingPreserved": (
            model_note("chartReimportedModel", "CAMERAS", 89)["tick"],
            model_note("chartReimportedModel", "CAMERAS", 89)["length"],
        )
        == ("9600", "4800"),
        "chartShortDrumTimingPreserved": (
            model_note("chartReimportedModel", "DRUMS", 70)["tick"],
            model_note("chartReimportedModel", "DRUMS", 70)["length"],
        )
        == ("1920", "12"),
        "playableGuitarTimingPreserved": (output["PART GUITAR"][0]["tick"], output["PART GUITAR"][0]["length"])
        == (9600, 4800),
        "emptyUnknownTrackProducesIncorrectMidiTrackCount": result["outputMidi"]["declaredTrackCount"] == 6
        and result["outputMidi"]["actualTrackCount"] == 5,
    }
    result["checks"] = checks
    instrumentation = root.parent / "instrumentation.json"
    if instrumentation.exists():
        result["instrumentation"] = json.loads(instrumentation.read_text())
    (root / "report.json").write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps(checks, indent=2))
    assert all(checks.values()), "A reproduction differs from the recorded 1.5.13 behavior; inspect report.json"


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=["create", "report"])
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    {"create": create, "report": report}[args.mode](args.directory.resolve())
