"""Pack a guitar or a bass as an instrument package (.chinst, docs/PACKAGE-FORMAT.md section 9.4).

The GLB must be skinned to a fixed GH3 rig profile (the guitarist or the bassist of gh3-band-v1), for example a Blender
export of the guitarist template. With --split, only the mesh parts that follow the instrument joints stay (the rule of
the game, BandAttachments): a creator can export the whole template with the body and keep only the guitar.

numpy only and relative imports, so the Blender add-on can bundle it (blender/build.py). PNG images of the GLB become
pre-encoded textures (section 7); that step needs Pillow.

Usage: python -m pipeline.package.instrument GLB --id ID --name NAME --kind guitar|bass --output DIR
           [--profile PROFILE] [--highway PNG] [--preview PNG] [--author NAME] [--license SPDX] [--split]
           [--brand TEXT] [--model TEXT] [--series TEXT] [--finish TEXT] [--library LIBRARY]
"""

import argparse
import sys
from pathlib import Path

from . import container, decisions, texture
from .character import Rig, keep_primitives, primitive_kinds
from .glb import Glb
from .validate import LIBRARY_ID, validate

FORMAT = "chinst/1.0"
MODEL_FILE = "instrument.glb"
INSTRUMENT_FILE = "instrument.json"
HIGHWAY_FILE = "highway.png"
PREVIEW_FILE = "preview.png"
KIND_PROFILES = {"guitar": "gh3-guitarist-axel-v1", "bass": "gh3-bassist-v1"}


def instrument_json(kind, profile=None, highway=False, **labels):
    value = {"version": 1, "library": LIBRARY_ID, "profile": profile or KIND_PROFILES[kind], "kind": kind}
    if highway:
        value["highway"] = HIGHWAY_FILE
    value.update({k: v for k, v in labels.items() if v})
    return value


def only_instrument(data):
    """GLB bytes with only the primitives that follow the joints outside the body."""
    glb = Glb(data)
    kinds = primitive_kinds(glb, Rig(glb), ())
    if not any(kind == "attachment" for kind in kinds.values()):
        raise ValueError("the GLB has no mesh part on the instrument joints")
    return keep_primitives(data, lambda m, p: kinds[m, p] == "attachment")


def write(
    glb,
    output,
    package_id,
    name,
    kind,
    profile=None,
    highway=None,
    preview=None,
    authors=None,
    license=None,
    permissions=None,
    split=False,
    labels=None,
):
    """Write <output>/<id>.chinst (zip) and <output>/unpacked/<id>.chinst (folder). Returns the zip path."""
    data = Path(glb).read_bytes()
    if split:
        data = only_instrument(data)
    data = texture.encode_glb(data, texture.encode_pngs)
    files = {
        MODEL_FILE: data,
        INSTRUMENT_FILE: container.json_bytes(instrument_json(kind, profile, highway is not None, **(labels or {}))),
    }
    if highway is not None:
        files[HIGHWAY_FILE] = Path(highway).read_bytes()
    if preview is not None:
        files[PREVIEW_FILE] = Path(preview).read_bytes()
    manifest = {
        "format": FORMAT,
        "id": package_id,
        "version": decisions.PACKAGE_VERSION,
        "name": name,
        "authors": authors or [{"name": "Unknown"}],
        "license": license or "LicenseRef-Unknown",
        "permissions": permissions or {"redistribute": False, "modify": False, "commercial": False},
        "requires": [{"id": LIBRARY_ID, "version": decisions.LIBRARY_VERSION}],
        "entry": {"model": MODEL_FILE, "instrument": INSTRUMENT_FILE},
        "files": {},
    }
    if preview is not None:
        manifest["preview"] = PREVIEW_FILE
    folder = Path(output) / "unpacked" / f"{package_id}.chinst"
    folder.mkdir(parents=True, exist_ok=True)
    for path in folder.iterdir():
        if path.is_file():
            path.unlink()
    for file, content in files.items():
        (folder / file).write_bytes(content)
    manifest["files"] = container.file_list(folder, files)
    (folder / "manifest.json").write_bytes(container.json_bytes(manifest))
    target = Path(output) / f"{package_id}.chinst"
    container.write_zip(folder, target)
    return target


def main(argv=None):
    parser = argparse.ArgumentParser(prog="python -m pipeline.package.instrument", description=__doc__.splitlines()[0])
    parser.add_argument("glb", type=Path)
    parser.add_argument("--id", required=True)
    parser.add_argument("--name", required=True)
    parser.add_argument("--kind", required=True, choices=sorted(KIND_PROFILES))
    parser.add_argument(
        "--profile",
        choices=sorted(set(KIND_PROFILES.values())),
        help="the rig profile of the GLB (default: the profile of the kind)",
    )
    parser.add_argument("--highway", type=Path, help="highway image (PNG, 512 x 1024)")
    parser.add_argument("--preview", type=Path, help="preview image (PNG, at most 1024 x 1024)")
    parser.add_argument("--author", action="append", help="author name (repeatable)")
    parser.add_argument("--license", help="SPDX license expression")
    parser.add_argument("--split", action="store_true", help="keep only the mesh parts on the instrument joints")
    for label in ("brand", "model", "series", "finish"):
        parser.add_argument(f"--{label}")
    parser.add_argument("--library", type=Path, help="band library (gh3-band-v1.chlib or folder) for the rig check")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args(argv)
    target = write(
        args.glb,
        args.output,
        args.id,
        args.name,
        args.kind,
        args.profile,
        args.highway,
        args.preview,
        [{"name": n} for n in args.author] if args.author else None,
        args.license,
        split=args.split,
        labels={k: getattr(args, k) for k in ("brand", "model", "series", "finish")},
    )
    report = validate(target, args.library)
    print(
        f"{target}: {'valid' if report.ok else 'INVALID'} ({len(report.errors)} errors, {len(report.warnings)} warnings)"
    )
    for message in report.errors:
        print(f"  error: {message}")
    for message in report.warnings:
        print(f"  warning: {message}")
    return 0 if report.ok else 1


if __name__ == "__main__":
    sys.exit(main())
