import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "assets"
GENERATED = {
    "stages",
    "stage-packs",
    "libraries",
    "characters",
    "instruments",
    "packages",
    "song-direction",
    "event-audit",
    "previews",
}


class AssetsLayoutTest(unittest.TestCase):
    def test_install_copies_every_skin_folder(self):
        folders = (
            re.search(r"^skin_folders=\(([^)]*)\)", (ROOT / "install.sh").read_text(), re.MULTILINE).group(1).split()
        )
        tracked = {path.name for path in ASSETS.iterdir() if path.is_dir()} - GENERATED
        self.assertEqual(tracked, set(folders))

    def test_readme_lists_every_folder(self):
        readme = (ASSETS / "README.md").read_text()
        for folder in {path.name for path in ASSETS.iterdir() if path.is_dir()} | GENERATED:
            self.assertIn(f"`{folder}/", readme)


if __name__ == "__main__":
    unittest.main()
