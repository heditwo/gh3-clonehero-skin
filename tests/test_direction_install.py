"""The install scripts of the direction zips (tools/direction-install): they find the song folders by the chart SHA-256 in
nested folders, show the plan first, and never replace a file. The PowerShell script runs only when pwsh is on PATH or
GH3_PWSH names it."""

import hashlib
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SCRIPTS = ROOT / "tools" / "direction-install"
SIDECAR = "stage-events.json"
PWSH = os.environ.get("GH3_PWSH") or shutil.which("pwsh")


def _archive(folder, songs):
    """A direction archive in folder: one sidecar for each song, the chart index, and the scripts."""
    lines = []
    for name, chart in sorted(songs.items()):
        (folder / name).mkdir(parents=True)
        (folder / name / SIDECAR).write_text(f'{{"song": "{name}"}}\n', encoding="utf-8")
        lines.append(f"{hashlib.sha256(chart).hexdigest()}  {name}/notes.mid\n")
    (folder / "gh3-direction-charts.sha256").write_text("".join(lines), encoding="utf-8")
    for script in SCRIPTS.iterdir():
        shutil.copy(script, folder)


class DirectionInstallTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        charts = {
            name: f"chart of {name}".encode() for name in ("Alpha - One", "Blue Öyster - Two & Three", "Gamma - 4")
        }
        charts["Missing - Song"] = b"a chart that the player does not have"
        self.archive = self.root / "archive with space"
        _archive(self.archive, charts)
        self.songs = self.root / "Songs"
        # Nested as the Chorus Encore packs: Quickplay and Bonus subfolders, a doubly nested DLC folder, a renamed folder.
        self.targets = {
            "Alpha - One": self.songs / "Guitar Hero III" / "Quickplay" / "Alpha - One",
            "Blue Öyster - Two & Three": self.songs / "gh3dlc" / "gh3dlc" / "Renamed [folder]",
            "Gamma - 4": self.songs / "Guitar Hero III" / "Bonus" / "Gamma - 4",
        }
        for name, folder in self.targets.items():
            folder.mkdir(parents=True)
            (folder / ("NOTES.MID" if name == "Gamma - 4" else "notes.mid")).write_bytes(charts[name])
        (self.songs / "Other" / "Alpha - One").mkdir(parents=True)
        (self.songs / "Other" / "Alpha - One" / "notes.mid").write_bytes(b"another chart with the same folder name")
        self.kept = self.targets["Gamma - 4"] / SIDECAR
        self.kept.write_text("the player's own file\n", encoding="utf-8")

    def _check(self, run):
        before = sorted(p.relative_to(self.songs) for p in self.songs.rglob("*"))
        dry = run("dry")
        self.assertIn("2 to copy, 0 already there, 1 kept (another file), 1 songs not found", dry)
        self.assertIn("Not found:       Missing - Song", dry)
        self.assertEqual(sorted(p.relative_to(self.songs) for p in self.songs.rglob("*")), before)

        result = run("yes")
        self.assertIn("Copied 2 files", result)
        for name in ("Alpha - One", "Blue Öyster - Two & Three"):
            self.assertEqual((self.targets[name] / SIDECAR).read_bytes(), (self.archive / name / SIDECAR).read_bytes())
        self.assertEqual(self.kept.read_text(encoding="utf-8"), "the player's own file\n")
        self.assertFalse((self.songs / "Other" / "Alpha - One" / SIDECAR).exists())

        again = run("yes")
        self.assertIn("0 to copy, 2 already there, 1 kept (another file), 1 songs not found", again)

    def test_shell_script(self):
        def run(mode):
            flag = "--dry-run" if mode == "dry" else "--yes"
            script = self.archive / "install-direction.sh"
            result = subprocess.run(
                ["sh", str(script), str(self.songs), flag],
                capture_output=True,
                text=True,
                check=True,
                stdin=subprocess.DEVNULL,
            )
            return result.stdout

        self._check(run)

    def test_shell_script_asks_only_on_a_terminal(self):
        script = self.archive / "install-direction.sh"
        result = subprocess.run(
            ["sh", str(script), str(self.songs)], capture_output=True, text=True, check=True, stdin=subprocess.DEVNULL
        )
        self.assertIn("No file was copied. Add --yes to copy.", result.stdout)
        self.assertFalse((self.targets["Alpha - One"] / SIDECAR).exists())

    @unittest.skipUnless(PWSH, "no pwsh")
    def test_powershell_script(self):
        def run(mode):
            flag = "-DryRun" if mode == "dry" else "-Yes"
            script = self.archive / "install-direction.ps1"
            result = subprocess.run(
                [PWSH, "-NoProfile", "-NonInteractive", "-File", str(script), "-SongsDir", str(self.songs), flag],
                capture_output=True,
                text=True,
                check=True,
            )
            return result.stdout

        self._check(run)


if __name__ == "__main__":
    unittest.main()
