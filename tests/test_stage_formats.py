import struct
import unittest
from pathlib import Path

from pipeline import events
from pipeline.gh3.formats import find_array, key, pak_entries, qb_struct

ROOT = Path(__file__).resolve().parents[1]


def words(*values):
    return struct.pack(f">{len(values)}I", *values)


def song_fixture():
    data = bytearray(112)
    data[8:12] = bytes.fromhex("1c080204")
    data[28:48] = words(0x00200C00, key("fixture_lightshow_notes"), 0, 48, 0)
    data[48:68] = words(0x00010C00, 2, 60, 68, 92)
    data[68:92] = words(0x00010100, 3, 80, 45274, 56, 55)
    data[92:112] = words(0x00010100, 3, 104, 46000, 72)
    data.extend(words(43))
    data.extend(words(0x00200C00, key("fixture_fretbars"), 0, 136, 0))
    data.extend(words(0x00010000, 0, 0))
    data[4:8] = words(len(data))
    pak = words(key(".qb"), 64, len(data), 0, key("fixture"), 0, 0, 0)
    pak += words(key(".last"), 32 + len(data), 4, 0, 0, 0, 0, 0)
    return pak + data + bytes(4)


class StageFormatTests(unittest.TestCase):
    def test_original_lightshow_export_preserves_pyro_time(self):
        result = events.extract(song_fixture(), "fixture")
        self.assertEqual(
            [(e["time"], e["type"], e["value"], e["duration"], e["velocity"]) for e in result["events"]],
            [
                (45.274, "gh3-note", "56", 0.055, None),
                (46.0, "gh3-note", "72", 0.043, None),
            ],
        )
        self.assertEqual(result["playback"], [])

    def test_wrong_song_id_is_not_silently_exported(self):
        with self.assertRaisesRegex(ValueError, "Expected one native chart"):
            events.extract(song_fixture(), "another_song")

    def test_truncated_pak_entry_is_rejected(self):
        with self.assertRaises(ValueError):
            list(pak_entries(song_fixture()[:80]))

    def test_array_pointer_must_remain_in_file(self):
        data = bytearray(next(pak_entries(song_fixture()))[2])
        data[56:60] = words(0x7FFFFFFF)
        with self.assertRaises(ValueError):
            find_array(data, "fixture_lightshow_notes")

    def test_cyclic_struct_links_are_rejected(self):
        data = words(0x100, 8, 0x008D0000, key("Name"), key("object"), 8)
        with self.assertRaisesRegex(ValueError, "links"):
            qb_struct(data, 0)

    def test_unknown_note_pitch_is_rejected(self):
        pak = bytearray(song_fixture())
        pak[64 + 84 : 64 + 88] = words(256)
        with self.assertRaisesRegex(ValueError, "Invalid native lightshow note"):
            events.extract(pak, "fixture")


if __name__ == "__main__":
    unittest.main()
