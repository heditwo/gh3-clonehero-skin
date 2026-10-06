import importlib.util
from pathlib import Path
import struct
import unittest

from gh3_formats import find_array, key, pak_entries, qb_struct


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("export_stage_events", ROOT / "export-stage-events.py")
EXPORT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(EXPORT)


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
    pak = words(key(".qb"), 64, len(data), 0, key("fixture"), 0, 0, 0)
    pak += words(key(".last"), 32 + len(data), 4, 0, 0, 0, 0, 0)
    return pak + data + bytes(4)


class StageFormatTests(unittest.TestCase):
    def test_original_lightshow_export_preserves_pyro_time(self):
        result = EXPORT.extract(song_fixture(), "fixture")
        self.assertEqual(result["events"], [
            {"time": 45.274, "type": "gh3-note", "value": "56"},
            {"time": 46.0, "type": "gh3-note", "value": "72"},
        ])
        self.assertEqual(result["unmappedLightshowScripts"], {})

    def test_wrong_song_id_is_not_silently_exported(self):
        with self.assertRaisesRegex(ValueError, "No lightshow"):
            EXPORT.extract(song_fixture(), "another_song")

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
        pak[64 + 84:64 + 88] = words(256)
        with self.assertRaisesRegex(ValueError, "note values"):
            EXPORT.extract(pak, "fixture")


if __name__ == "__main__":
    unittest.main()
