"""Build the shared GH3 band library used by every stage pack."""

import argparse
from hashlib import sha256
from pathlib import Path

from pipeline.gh3.direction import Source
from pipeline.library import (
    audio,
    band,
    crowd,
    direction,
    hair,
    hand_fire,
    scripts,
    tesla,
)
from pipeline.paths import GH3_DATA, LIBRARY
from pipeline.stagepack.pack import load_json, write_json


def manifest(output):
    write_json(
        output / "library.json",
        {
            "version": 1,
            "id": "gh3-band-v1",
            "files": {
                str(p.relative_to(output)): sha256(p.read_bytes()).hexdigest()
                for p in sorted(output.rglob("*"))
                if p.is_file() and p.name != "library.json"
            },
        },
    )


def prepare_models(data, output):
    """Rebuild only the band and crowd models (meshes, materials, textures), keeping every other library part."""
    band.convert(data, output / "band", shared=True)
    hair.convert(data, output / "band/hair.json")
    crowd.convert(data, output / "crowd")
    manifest(output)


def prepare_hair(data, output):
    """Rebuild only the hair physics (band/hair.json), keeping every other library part."""
    hair.convert(data, output / "band/hair.json")
    manifest(output)


def prepare(data, output):
    output.mkdir(parents=True, exist_ok=True)
    band.convert(data, output / "band", shared=True)
    direction.convert(data, output / "direction", shared=True)
    hair.convert(data, output / "band/hair.json")
    hand_fire.convert(Source(data), output / "hand-fire")
    crowd.convert(data, output / "crowd")
    audio.convert(data, output / "audio")
    scripts.convert(data, output / "scripts.json")
    tesla.convert(data, output / "render")
    performance = load_json(output / "direction/performance.json")
    write_json(
        output / "catalog.json",
        {
            "version": 1,
            "library": "gh3-band-v1",
            "members": {
                role: {
                    "bones": member["bones"],
                    "groups": member["groups"],
                    "layers": member["layers"],
                    "clips": member["clips"],
                }
                for role, member in performance["members"].items()
            },
        },
    )
    manifest(output)
    print(output)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(prog="python -m pipeline.library", description=__doc__)
    parser.add_argument("--data", type=Path, default=GH3_DATA)
    parser.add_argument("--output", type=Path, default=LIBRARY)
    parser.add_argument("--models-only", action="store_true", help="Rebuild only the band and crowd models")
    parser.add_argument("--hair-only", action="store_true", help="Rebuild only the hair physics")
    args = parser.parse_args()
    if args.models_only:
        prepare_models(args.data, args.output)
    elif args.hair_only:
        prepare_hair(args.data, args.output)
    else:
        prepare(args.data, args.output)
