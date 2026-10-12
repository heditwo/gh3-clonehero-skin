"""The documentation images stay in full colour (docs/images/README.md).

A 256-colour palette made the small, saturated HUD parts of the game captures grey; this test stops such images."""

import re
import subprocess
import unittest
from pathlib import Path

from PIL import Image, JpegImagePlugin
from test_docs_links import markdown_files

ROOT = Path(__file__).resolve().parents[1]
IMAGES = ROOT / "docs" / "images"
SUFFIXES = {".png", ".jpg", ".jpeg", ".webp", ".gif"}
MIN_COLOURS = 257
MIN_JPEG_QUALITY = 85
MAX_BYTES = 1_500_000
MAX_WIDTH = 1920
# The luminance table of the JPEG standard (ITU T.81 K.1), the base of the libjpeg quality scale.
STANDARD_LUMINANCE = [
    16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55,
    14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
    18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92,
    49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
]  # fmt: skip


def image_files():
    names = (
        subprocess.run(
            ["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard", "docs/images"],
            cwd=ROOT,
            capture_output=True,
            check=True,
        )
        .stdout.decode()
        .split("\0")
    )
    return [ROOT / name for name in names if name and Path(name).suffix.lower() in SUFFIXES and (ROOT / name).is_file()]


def jpeg_quality(image):
    """The libjpeg quality that gives the luminance table of the file (approximate for custom tables)."""
    table = image.quantization[0]
    scale = 100 * sum(table) / sum(STANDARD_LUMINANCE)
    return (200 - scale) / 2 if scale <= 100 else 5000 / scale


class DocumentationImageTests(unittest.TestCase):
    def test_images_are_full_colour(self):
        problems = []
        for path in image_files():
            name = path.relative_to(ROOT)
            with Image.open(path) as image:
                if image.mode not in ("RGB", "RGBA"):
                    problems.append(f"{name}: mode {image.mode} (palette or grey); save it in full colour")
                    continue
                if image.getcolors(MIN_COLOURS - 1) is not None:
                    problems.append(f"{name}: fewer than {MIN_COLOURS} colours (a quantized image)")
                if image.width > MAX_WIDTH:
                    problems.append(f"{name}: {image.width} px wide (at most {MAX_WIDTH})")
                if image.format == "JPEG":
                    if JpegImagePlugin.get_sampling(image) != 0:
                        problems.append(f"{name}: chroma subsampling; use 4:4:4")
                    if jpeg_quality(image) < MIN_JPEG_QUALITY:
                        problems.append(
                            f"{name}: JPEG quality about {jpeg_quality(image):.0f} (at least {MIN_JPEG_QUALITY})"
                        )
            if path.stat().st_size > MAX_BYTES:
                problems.append(f"{name}: {path.stat().st_size} bytes; save game captures as JPEG")
        self.assertEqual(problems, [])

    def test_images_are_used(self):
        texts = [path.read_text(encoding="utf-8") for path in markdown_files()]
        texts.append((ROOT / "scripts" / "build-release.sh").read_text(encoding="utf-8"))
        used = {match for text in texts for match in re.findall(r"images/([\w./-]+)", text)}
        unused = [
            str(path.relative_to(ROOT)) for path in image_files() if path.relative_to(IMAGES).as_posix() not in used
        ]
        self.assertEqual(unused, [])


if __name__ == "__main__":
    unittest.main()
