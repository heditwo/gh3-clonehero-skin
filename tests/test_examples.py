"""The example packages in examples/ (docs/CREATOR-GUIDE.md): valid, CC0-1.0, small, and free of GH3 art.

examples/build.sh makes them in Blender; examples/check_examples.py checks the .blend files in Blender."""

import json
import unittest
import zipfile
from pathlib import Path

from pipeline.package.glb import Glb
from pipeline.package.validate import Validator

ROOT = Path(__file__).resolve().parents[1]
EXAMPLES = ROOT / "examples"
LIBRARY = ROOT / "assets" / "packages" / "gh3-band-v1.chlib"
PACKAGES = {"example.club.chstage": "example-club.blend", "example.guitarist.chchar": "example-guitarist.blend"}
MAX_BYTES = 2_000_000


class ExampleTests(unittest.TestCase):
    def test_packages_validate(self):
        for name in PACKAGES:
            with self.subTest(name=name):
                report = Validator(EXAMPLES / name).run()
                self.assertEqual(report.errors, [])
                # Without the library, the validator cannot check the character joints and says so.
                self.assertEqual([w for w in report.warnings if "no --library" not in w], [])

    @unittest.skipUnless(LIBRARY.is_file(), "no generated band library")
    def test_packages_validate_with_library(self):
        for name in PACKAGES:
            with self.subTest(name=name):
                report = Validator(EXAMPLES / name, LIBRARY).run()
                self.assertEqual((report.errors, report.warnings), ([], []))

    def test_manifests(self):
        for name in PACKAGES:
            with self.subTest(name=name), zipfile.ZipFile(EXAMPLES / name) as package:
                manifest = json.loads(package.read("manifest.json"))
                self.assertEqual(manifest["id"], name.rsplit(".", 1)[0])
                self.assertEqual(manifest["license"], "CC0-1.0")
                self.assertEqual(manifest["permissions"], {"redistribute": True, "modify": True, "commercial": True})
                self.assertIn("preview.png", package.namelist())

    def test_sizes_and_sources(self):
        for name, blend in PACKAGES.items():
            with self.subTest(name=name):
                self.assertLess((EXAMPLES / name).stat().st_size, MAX_BYTES)
                self.assertLess((EXAMPLES / blend).stat().st_size, MAX_BYTES)

    def test_stage_has_no_script_bank(self):
        # A script bank switches the mod to the GH3 script light scene; an empty bank makes the stage dark.
        with zipfile.ZipFile(EXAMPLES / "example.club.chstage") as package:
            self.assertNotIn("scripts.json", package.namelist())

    def test_images_are_generated(self):
        """All images come from examples/build_examples.py, by name."""
        expected = {"example.club.chstage": ("stage.glb", {"stage-wood", "club-floor", "brick-wall", "speaker-grille", "truss"}),
                    "example.guitarist.chchar": ("character.glb", {"body-color", "hair-color"})}
        for name, (model, images) in expected.items():
            with self.subTest(name=name), zipfile.ZipFile(EXAMPLES / name) as package:
                glb = Glb(package.read(model))
                self.assertEqual({image["name"] for image in glb.list("images")}, images)


if __name__ == "__main__":
    unittest.main()
