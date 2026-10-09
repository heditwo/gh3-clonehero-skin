import json
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
FIXTURE = r'''#!/usr/bin/env python3
import json
import os
from pathlib import Path
import signal
import sys

state = Path(os.environ["FIXTURE_STATE"])
name = Path(sys.argv[0]).name
if name == "Xwayland":
    (state / "display-pid").write_text(str(os.getpid()))
    signal.pause()
elif name == "hyprctl":
    if "instances" in sys.argv:
        print('[{"instance":"test"}]')
    elif "clients" in sys.argv:
        path = state / "display-pid"
        print(json.dumps([{"pid": int(path.read_text()), "address": "0x123"}] if path.exists() else []))
elif name == "magick":
    print("0.1")
elif name == "run-gh3-skin.sh":
    os.chdir(os.environ["GH3_CLONE_HERO_DIR"])
    (state / "environment.json").write_text(json.dumps(dict(os.environ)))
    frames = Path(os.environ["GH3_SKIN_CAPTURE_DIR"])
    for index, value in enumerate(os.environ["GH3_SKIN_CAPTURE_TIMES"].split(',')):
        (frames / f"frame-{index:02}.png").write_bytes(value.encode())
    signal.pause()
'''


class CaptureContractTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="gh3-capture-test-", dir="/tmp/opencode")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.bin = self.root / "bin"
        self.bin.mkdir()
        self.game = self.root / "game"
        self.game.mkdir()
        self.frames = self.root / "frames"
        self.frames.mkdir()
        (self.root / "scripts").mkdir()
        shutil.copy(ROOT / "scripts/capture-preview.sh", self.root / "scripts/capture-preview.sh")
        for name in ("hyprctl", "Xwayland", "xrandr", "magick", "run-gh3-skin.sh"):
            path = (self.root if name.endswith(".sh") else self.bin) / name
            path.write_text(FIXTURE)
            path.chmod(0o755)
        self.env = {name: value for name, value in os.environ.items() if not name.startswith("GH3_")}
        self.env.update({
            "MISE_QUIET": "1", "PATH": str(self.bin) + os.pathsep + os.environ["PATH"],
            "FIXTURE_STATE": str(self.root), "GH3_CLONE_HERO_DIR": str(self.game),
            "GH3_SKIN_PREVIEW_SONG": str(ROOT / "charts/GH3 Gem Matrix"),
            "GH3_SKIN_CAPTURE_TIMES": "3.2,5.2,7.2,9.2,11.2,13.2,15.2,17.2,19.2",
            "GH3_SKIN_CAPTURE_DIR": str(self.frames),
            "GH3_SKIN_SCREENSHOT": str(self.root / "preview.png"),
        })

    def run_capture(self):
        return subprocess.run(["bash", str(self.root / "scripts/capture-preview.sh")], env=self.env,
                              capture_output=True, text=True, timeout=20)

    def test_supplied_times_and_output_folder_survive_capture(self):
        result = self.run_capture()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        environment = json.loads((self.root / "environment.json").read_text())
        self.assertEqual(environment["GH3_SKIN_CAPTURE_TIMES"], self.env["GH3_SKIN_CAPTURE_TIMES"])
        self.assertEqual(environment["GH3_SKIN_CAPTURE_DIR"], str(self.frames))
        self.assertEqual(len(list(self.frames.glob("frame-*.png"))), 9)
        self.assertTrue((self.root / "preview.png").is_file())

    def test_stale_frames_cannot_pass_as_a_new_capture(self):
        (self.frames / "frame-00.png").write_bytes(b"old")
        result = self.run_capture()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("must contain no", result.stderr)
        self.assertFalse((self.root / "environment.json").exists())


if __name__ == "__main__":
    unittest.main()
