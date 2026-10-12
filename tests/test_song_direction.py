"""Tests of pipeline.song_direction: the MIDI note reader, the note comparison with GH3, and the sidecar layout."""

import struct
import tempfile
import unittest
from pathlib import Path

import numpy as np

from pipeline import song_direction


def _track(name, events):
    body = b"\x00\xff\x03" + bytes([len(name)]) + name.encode() + b"".join(events) + b"\x00\xff\x2f\x00"
    return b"MTrk" + struct.pack(">I", len(body)) + body


def _midi(path, guitar):
    tempo = _track(
        "", [b"\x00\xff\x51\x03" + (500000).to_bytes(3, "big"), b"\x83\x60\xff\x51\x03" + (250000).to_bytes(3, "big")]
    )
    header = b"MThd" + struct.pack(">IHHH", 6, 1, 2, 480)
    path.write_bytes(header + tempo + _track("PART GUITAR", guitar))


class SongDirectionTests(unittest.TestCase):
    def test_midi_chords_follow_the_tempo_map(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "notes.mid"
            # Green + yellow at tick 0, red at tick 480 (0.5 s), then a tempo change; orange at tick 960 (0.75 s).
            _midi(
                path,
                [
                    b"\x00\x90\x60\x64",
                    b"\x00\x62\x64",
                    b"\x83\x60\x80\x60\x00",
                    b"\x00\x90\x61\x64",
                    b"\x83\x60\x64\x64",
                ],
            )
            self.assertEqual(song_direction.midi_guitar_chords(path), [(0.0, 5), (0.5, 2), (0.75, 16)])

    def test_compare_finds_the_offset_of_a_shifted_chart(self):
        gh3 = np.arange(400) * 0.25
        lanes = np.arange(400) % 5 + 1
        offset, matched, same = song_direction.compare(gh3 + 0.14, lanes, gh3, lanes)
        self.assertEqual((offset, matched, same), (0.14, 1.0, 1.0))
        other = np.random.default_rng(1).integers(1, 32, 400)
        _, matched, same = song_direction.compare(gh3 + 0.14, other, gh3, lanes)
        self.assertGreaterEqual(matched, song_direction.MATCHED_SHARE)
        self.assertLess(same, song_direction.MATCHED_SHARE)

    def test_sidecar_leaves_chart_timing_and_strums_to_the_chart(self):
        direction = {
            "sourceSong": "song",
            "events": [
                {
                    "time": 1,
                    "type": "gh3-camera",
                    "value": "85",
                    "duration": 2,
                    "role": "",
                    "count": 1,
                    "velocity": None,
                },
                {"time": 1, "type": "strum", "value": "1", "role": "guitarist"},
                {"time": 1, "type": "beat", "value": ""},
                {"time": 2, "type": "drum", "value": "36", "velocity": 90},
            ],
        }
        result = song_direction.sidecar(direction, 0.14, ("gh3.singer-female", "HiHat01", "gh3.dive"))
        self.assertEqual(
            {k: v for k, v in result.items() if k != "events"},
            {
                "version": 2,
                "library": "gh3-band-v1",
                "sourceSong": "song",
                "countoff": "HiHat01",
                "stage": "gh3.dive",
                "characters": {"singer": "gh3.singer-female"},
                "offsetSeconds": 0.14,
            },
        )
        self.assertEqual(
            result["events"],
            [
                {"time": 1, "type": "gh3-camera", "value": "85", "duration": 2},
                {"time": 2, "type": "drum", "value": "36", "velocity": 90},
            ],
        )


if __name__ == "__main__":
    unittest.main()
