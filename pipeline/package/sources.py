"""Dry conversion check: map current source packs and the current library to package format v1 and report every
field that does not map yet. Nothing is written; GLB files are validated as they are.

Usage: python -m pipeline.package.sources [--packs assets/stage-packs] [--library assets/libraries/gh3-band-v1] [--json OUT]
"""

import argparse
import json
from collections import Counter
from pathlib import Path

from pipeline.package.profiles import LIBRARY_ID
from pipeline.package.schema import Schemas
from pipeline.package.validate import Validator

PLACEMENT_FIELDS = ("band", "waypoints", "effects", "crowd")
MANIFEST_FIELDS = {
    "version",
    "kind",
    "id",
    "name",
    "library",
    "model",
    "cameras",
    "lighting",
    "scripts",
    "actors",
    *PLACEMENT_FIELDS,
}


def stage_files(folder):
    """Package path to file path or bytes for one source pack, with the JSON split of stage-pack.json."""
    source = json.loads((folder / "stage-pack.json").read_text())
    unmapped = sorted(set(source) - MANIFEST_FIELDS)
    files = {}
    entry = {"model": source["model"], "placements": "placements.json", "cameras": source["cameras"]}
    files[source["model"]] = folder / source["model"]
    files[source["cameras"]] = folder / source["cameras"]
    for key in ("lighting", "scripts"):
        if key in source:
            entry[key] = source[key]
            files[source[key]] = folder / source[key]
    placements = {"version": 1, **{key: source[key] for key in PLACEMENT_FIELDS if key in source}}
    files["placements.json"] = json.dumps(placements).encode()
    if source.get("actors"):
        entry["actors"] = "actors.json"
        files["actors.json"] = json.dumps({"version": 1, "actors": source["actors"]}).encode()
        for actor in source["actors"].values():
            files[actor["model"]] = folder / actor["model"]
    if "scripts" in source:
        scripts = json.loads((folder / source["scripts"]).read_text())
        for file in [*scripts.get("textures", {}).values(), *scripts.get("movies", {}).values()]:
            files[file] = folder / file
    manifest = {
        "format": "chstage/1.0",
        "id": source["id"],
        "version": "1.0.0",
        "name": source["name"],
        "authors": [{"name": "Neversoft (original venue)"}],
        "license": "LicenseRef-GH3-Derived",
        "permissions": {"redistribute": True, "modify": False, "commercial": False},
        "requires": [{"id": source["library"], "version": "1.0.0"}],
        "entry": entry,
        "files": {},
    }
    files["manifest.json"] = json.dumps(manifest).encode()
    dropped = sorted(
        p.name
        for p in folder.iterdir()
        if p.is_file()
        and p.name != "stage-pack.json"
        and not any(isinstance(v, Path) and v == p for v in files.values())
    )
    return files, unmapped, dropped


def summarize(messages):
    """Group messages that differ only in numbers and names."""
    import re

    counts = Counter(
        re.sub(r"\([^)]*\)|'[^']*'|(?<![A-Za-z0-9])\d+(?![A-Za-z0-9])", "#", message) for message in messages
    )
    return [f"{count} x {message}" for message, count in counts.most_common()]


def check_packs(packs, schemas):
    results = {}
    for folder in sorted(p.parent for p in Path(packs).glob("*/stage-pack.json")):
        files, unmapped, dropped = stage_files(folder)
        report = Validator(folder.name + ".chstage", schemas=schemas, files=files).run()
        results[folder.name] = {
            "errors": summarize(report.errors),
            "warnings": summarize(report.warnings),
            "unmappedManifestFields": unmapped,
            "droppedFiles": dropped,
        }
    return results


TARGETS = [
    (
        "band/*/meshes.bin + animation.bin + stage.json + textures/",
        "band/<role>/model.glb (skinned GLB, rig = profile)",
    ),
    ("crowd/<model>/..., crowd/hands/<name>/...", "crowd/<model>/model.glb, crowd/hands/<name>/model.glb"),
    ("direction/<role>/*.bin.gz, crowd/clips/*.bin.gz (GH3CLP01)", "*.q48.br (q48 + Brotli)"),
    ("audio/*.pcm.gz", "audio/<id>.wav (PCM) or the original MP3"),
    ("movies/*.gh3video (GH3VID01)", "movies/<name>/movie.json + frames/NNNNNN.png"),
    ("render/*.bundle, render/stage-templates.json", "not in the package (mod install, platform-specific)"),
    ("render/particle-materials.json, render/tesla-*", "effects/ (shader-independent data)"),
    ("library.json", "manifest.json files"),
    ("assets/stages/events/*.json (original song direction)", "songs/<song>.json + songs/index.json"),
]


def check_library(library, schemas):
    library = Path(library)
    inventory = Counter()
    for file in library.rglob("*"):
        if file.is_file():
            inventory[file.relative_to(library).parts[0] + "/*" + "".join(file.suffixes)] += 1
    scripts = json.loads((library / "scripts.json").read_text())
    errors = schemas.validate(scripts, "scripts.schema.json")
    unused_material_fields = Counter()
    for stage in library.rglob("stage.json"):
        for material in json.loads(stage.read_text())["materials"].values():
            for key in ("textures", "template"):
                if key in material:
                    unused_material_fields[key] += 1
    return {
        "id": LIBRARY_ID,
        "inventory": dict(sorted(inventory.items())),
        "scriptsSchemaErrors": summarize(errors),
        "rawMaterialFields": dict(unused_material_fields),
        "targets": [f"{a} -> {b}" for a, b in TARGETS],
    }


def main(argv=None):
    parser = argparse.ArgumentParser(prog="python -m pipeline.package.sources", description=__doc__.splitlines()[0])
    parser.add_argument("--packs", type=Path, default=Path("assets/stage-packs"))
    parser.add_argument("--library", type=Path, default=Path("assets/libraries/gh3-band-v1"))
    parser.add_argument("--json", type=Path)
    args = parser.parse_args(argv)
    schemas = Schemas()
    result = {"packs": check_packs(args.packs, schemas)}
    if args.library.is_dir():
        result["library"] = check_library(args.library, schemas)
    if args.json:
        args.json.write_text(json.dumps(result, indent=2) + "\n")
    for name, pack in result["packs"].items():
        print(
            f"{name}: {len(pack['errors'])} error kinds, {len(pack['warnings'])} warning kinds; dropped files {pack['droppedFiles']}"
        )
        for line in pack["errors"]:
            print(f"  error: {line}")
        for line in pack["warnings"]:
            print(f"  warning: {line}")
    if "library" in result:
        print(
            f"library: scripts.json schema errors {result['library']['scriptsSchemaErrors']}; raw material fields {result['library']['rawMaterialFields']}"
        )


if __name__ == "__main__":
    main()
