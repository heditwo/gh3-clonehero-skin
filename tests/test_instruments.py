import json
import os
import struct
import tempfile
import unittest
import zlib
from pathlib import Path

from pipeline import instruments
from pipeline.package import character, instrument
from pipeline.package.validate import Validator
from pipeline.paths import GH3_DATA

ROOT = Path(__file__).resolve().parents[1]
LIBRARY = ROOT/"assets"/"packages"/"gh3-band-v1.chlib"
DATA = Path(os.environ.get("GH3_DATA", GH3_DATA))


def png(width, height):
    rows = b"".join(b"\0" + b"\x80\x40\x20\xff" * width for _ in range(height))
    def chunk(kind, data): return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">2I5B", width, height, 8, 6, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b"")


class NameTests(unittest.TestCase):
    def test_ids_and_names(self):
        self.assertEqual(instruments.slug("Instrument_Guitar_Demonik"), "demonik")
        self.assertEqual(instruments.slug("Instrument_ES335_Red"), "es335-red")
        self.assertEqual(instruments.display_name("Gibson", "  Les Paul  ", "Standard", "Ebony"), "Gibson Les Paul Standard (Ebony)")
        self.assertEqual(instruments.display_name("Special", "Obliviaxe", "Lou's", "Special"), "Obliviaxe: Lou's")
        self.assertEqual(instruments.display_name("Special", "Star", "Apollo", "Deity Red"), "Star: Apollo (Deity Red)")


@unittest.skipUnless(LIBRARY.exists(), "no library package")
class InstrumentPackageTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.library = character.Library(LIBRARY)

    def write(self, root, glb, **options):
        path = Path(root)/"source.glb"
        path.write_bytes(glb)
        return instrument.write(path, Path(root)/"out", "test.guitar", "Test guitar", "guitar", **options)

    def test_library_guitar_is_a_valid_instrument(self):
        with tempfile.TemporaryDirectory() as root:
            highway = Path(root)/"highway.png"
            highway.write_bytes(png(512, 1024))
            target = self.write(root, self.library.model("guitarist"), split=True, highway=highway, labels={"brand": "Gibson"})
            report = Validator(target, LIBRARY).run()
            self.assertEqual((report.errors, report.warnings), ([], []))
            data = json.loads((Path(root)/"out/unpacked/test.guitar.chinst/instrument.json").read_text())
            self.assertEqual(data, {"version": 1, "library": "gh3-band-v1", "profile": "gh3-guitarist-axel-v1", "kind": "guitar",
                                    "highway": "highway.png", "brand": "Gibson"})

    def test_body_parts_are_rejected(self):
        with tempfile.TemporaryDirectory() as root:
            report = Validator(self.write(root, self.library.model("guitarist")), LIBRARY).run()
            self.assertTrue(any("follows the body joints" in e for e in report.errors), report.errors)

    def test_highway_checks(self):
        with tempfile.TemporaryDirectory() as root:
            highway = Path(root)/"highway.png"
            highway.write_bytes(png(4, 4))
            target = self.write(root, self.library.model("guitarist"), split=True, highway=highway)
            report = Validator(target, LIBRARY).run()
            self.assertEqual(report.errors, [])
            self.assertTrue(any("512 x 1024" in w for w in report.warnings), report.warnings)
            folder = Path(root)/"out/unpacked/test.guitar.chinst"
            (folder/"highway.png").unlink()
            manifest = json.loads((folder/"manifest.json").read_text())
            manifest["files"] = {}
            (folder/"manifest.json").write_text(json.dumps(manifest))
            report = Validator(folder, LIBRARY).run()
            self.assertTrue(any("highway file is missing" in e for e in report.errors), report.errors)

    def test_wrong_rig_is_rejected(self):
        with tempfile.TemporaryDirectory() as root:
            target = self.write(root, instrument.only_instrument(self.library.model("guitarist")), profile="gh3-bassist-v1")
            report = Validator(target, LIBRARY).run()
            self.assertTrue(any("differ from the profile" in e for e in report.errors), report.errors)


@unittest.skipUnless((DATA/"PAK/qb.pak.xen").exists(), "no GH3 data")
class InventoryTests(unittest.TestCase):
    def test_every_guitar_and_bass(self):
        _, scripts, rows = instruments.inventory(DATA)
        kinds = [r["kind"] for r in rows]
        self.assertEqual((kinds.count("guitar"), kinds.count("bass")), (88, 20))
        by_id = {r["id"]: r for r in rows}
        self.assertEqual((by_id["gh3.les-paul-black"]["store"], by_id["gh3.lp-vbrst"]["store"]), ("Free_Guitars", "Free_Basses"))
        self.assertEqual((by_id["gh3.demonik"]["name"], by_id["gh3.demonik"]["price"]), ("Obliviaxe: Lou's", 2000))
        self.assertEqual(by_id["gh3.tobias-b4"]["profile"], "gh3-bassist-v1")


if __name__ == "__main__":
    unittest.main()
