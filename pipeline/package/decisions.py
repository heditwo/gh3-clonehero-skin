"""Package choices that wait for an owner decision (docs/PACKAGE-FORMAT.md, section 17.1).

The converter (`pipeline.package.build`) reads every open choice from this module only. Change a value here and run
the `packages` step of scripts/regenerate.sh again.
"""

import zipfile
from pathlib import Path

# O1: zip method for each file suffix. STORE for files that are compressed already; DEFLATE for the rest, also GLB and the
# pre-encoded textures (2026-10-10, PACKAGE-FORMAT.md section 2.3: the 193 GH3 packages are 1,133 MB instead of 1,906 MB;
# the importer inflates them in about 1 s more for all packages).
STORED_SUFFIXES = {".png", ".br", ".ogg", ".mp3"}
# Level 6: 3 times faster than 9 for the pre-encoded textures, 0.1-0.7 % larger.
DEFLATE_LEVEL = 6


def zip_method(path):
    suffix = "." + path.rsplit(".", 1)[-1].lower() if "." in path else ""
    return zipfile.ZIP_STORED if suffix in STORED_SUFFIXES else zipfile.ZIP_DEFLATED


# O2: license and permissions of the GH3-derived packages (the ten venues and the library).
GH3_LICENSE = "LicenseRef-GH3-Derived"
GH3_PERMISSIONS = {"redistribute": True, "modify": True, "commercial": False}
GH3_VENUE_AUTHORS = [{"name": "Neversoft", "role": "original venue"}]
GH3_LIBRARY_AUTHORS = [{"name": "Neversoft", "role": "original band, crowd, animations, and audio"}]
GH3_CHARACTER_AUTHORS = [{"name": "Neversoft", "role": "original band member"}]
GH3_INSTRUMENT_AUTHORS = [{"name": "Neversoft", "role": "original instrument"}]
GH3_CHARACTER_LIBRARY_AUTHORS = [{"name": "Neversoft", "role": "original skeletons, animations, and ragdolls"}]
CONVERTER_CREDIT = "Converted from the GH3 PC data by the GH3 Gameplay Skin pipeline."

# The example stage is made by the pipeline from boxes, not from GH3 data.
EXAMPLE_LICENSE = "CC0-1.0"
EXAMPLE_PERMISSIONS = {"redistribute": True, "modify": True, "commercial": True}
EXAMPLE_AUTHORS = [{"name": "GH3 Gameplay Skin", "role": "example stage"}]

# O3: file extension of the library package.
LIBRARY_EXTENSION = ".chlib"

# O4: library audio. "wav" wraps the current decoded PCM (16-bit, from the GH3 MP3 data) in a WAV header without
# re-encoding, so the samples and loop points stay identical.
AUDIO_FORMAT = "wav"

# O5: library movies. "png-frames" keeps the current PNG frames of each GH3VID01 bank (lossless).
MOVIE_FORMAT = "png-frames"

# Venue preview images (manifest `preview`): `<id>.png` in this folder, 512 × 288, made from an in-game capture
# (docs/PIPELINE.md, venue previews). A venue without an image gets no preview.
VENUE_PREVIEWS = Path(__file__).resolve().parents[2]/"assets"/"previews"
PREVIEW_SIZE = (512, 288)

PACKAGE_VERSION = "1.0.0"
LIBRARY_VERSION = "1.0.0"
