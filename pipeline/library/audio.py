"""Convert original stage sound samples and retain their sound-event definitions."""

import gzip
import json
import struct
import subprocess
import tempfile
import wave
from io import BytesIO
from pathlib import Path

from pipeline import songtool
from pipeline.gh3.data import Source, scalar
from pipeline.gh3.formats import find_struct, key
from pipeline.stagepack.pack import write_json


def audio_payload(blob):
    if not blob.startswith(b"FSB3"):
        return blob, None
    if len(blob) < 104:
        raise ValueError("Truncated FSB sample")
    count, headers, length = struct.unpack_from("<III", blob, 4)
    if count != 1 or headers < 80 or 24 + headers + length > len(blob):
        raise ValueError("Invalid FSB sample")
    samples, size, start, end, mode, rate = struct.unpack_from("<6I", blob, 56)
    if size > length or not rate or start > end or end >= samples:
        raise ValueError("Invalid FSB loop")
    return blob[24 + headers : 24 + headers + size], (start / rate, (end + 1) / rate)


def convert(data, output):
    source = Source(data)
    output.mkdir(parents=True, exist_ok=True)
    scripts = source.scripts()
    tool = songtool.path()
    definitions = {}
    samples = {}
    with tempfile.TemporaryDirectory(prefix="gh3-audio-", dir="/tmp/opencode") as temporary:
        temporary = Path(temporary)
        for name in (
            "global_sound_events",
            "sound_buss",
            "backgrounds_structs",
            "global_sound_logic",
            "backgrounds_logic",
            "guitar_notemaps",
        ):
            path = temporary / (name + ".qb")
            folder = "scripts\\guitar\\" if name == "guitar_notemaps" else "scripts\\game\\audio\\"
            path.write_bytes(scripts[key(folder + name + ".qb")])
            definitions.update(json.loads(subprocess.check_output(["dotnet", str(tool), "qb-data", str(path)])))
        entries = source.load("ZONES/global_sfx.pak.xen")
        for zone in (
            "party",
            "dive",
            "video",
            "artdeco",
            "prison",
            "wikker",
            "budokan",
            "hell",
            "soundcheck",
            "credits",
        ):
            entries.extend(source.load(f"ZONES/z_{zone}.pak.xen"))
        for kind, reference, blob in entries:
            if kind != key(".wav"):
                continue
            name = f"{reference:08x}"
            payload, loop = audio_payload(blob)
            raw = subprocess.check_output(
                [
                    "ffmpeg",
                    "-hide_banner",
                    "-loglevel",
                    "error",
                    "-f",
                    "mp3",
                    "-i",
                    "pipe:0",
                    "-f",
                    "wav",
                    "-acodec",
                    "pcm_s16le",
                    "pipe:1",
                ],
                input=payload,
            )
            with wave.open(BytesIO(raw)) as audio:
                rate = audio.getframerate()
                channels = audio.getnchannels()
                pcm = audio.readframes(audio.getnframes())
            if not pcm or channels not in (1, 2):
                raise ValueError("Invalid decoded stage sound")
            frames = len(pcm) // (2 * channels)
            with (
                (output / (name + ".pcm.gz")).open("wb") as file,
                gzip.GzipFile(fileobj=file, mode="wb", mtime=0) as compressed,
            ):
                compressed.write(pcm)
            samples[name] = {
                "rate": rate,
                "channels": channels,
                "frames": frames,
                "loopStart": 0 if loop is None else min(frames - 1, round(loop[0] * rate)),
                "loopEnd": frames if loop is None else min(frames, round(loop[1] * rate)),
                "loop": loop is not None,
            }
    countoffs = {}
    songlist = scripts.get(key("scripts\\guitar\\songlist.qb"))
    if songlist:
        props = find_struct(songlist, "permanent_songlist_props") or {}
        for song, row in props.items():
            if isinstance(row, dict) and key("countoff") in row:
                countoffs[f"{song:08x}"] = row[key("countoff")]
    constants = {
        name: next(scalar(qb, name, None) for qb in scripts.values() if scalar(qb, name, None) is not None)
        for name in ("CrowdLevelForSurges", "health_poor_medium", "health_medium_good")
    }
    write_json(
        output / "audio.json",
        {
            "version": 1,
            "definitions": definitions,
            "samples": samples,
            "countoffs": countoffs,
            "constants": constants,
            "sources": source.hashes,
        },
    )
    print(f"Converted {len(samples)} sound samples and retained {len(definitions)} sound definitions: {output}")
