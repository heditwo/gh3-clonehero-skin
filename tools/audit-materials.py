#!/usr/bin/env python3
"""Report how each compiled venue material is drawn: by its translated GH3 template or by the approximate surface model.
Usage: audit-materials.py [--stages assets/stages] [--json out.json] [venue...]"""

import argparse
from collections import Counter, defaultdict
import json
from pathlib import Path
import struct

ROOT = Path(__file__).resolve().parent.parent


def used_materials(path):
    """Material ids that compiled meshes reference (the importer also lists an unused default material)."""
    data = path.read_bytes()
    stride = {b"GH3STG01": 36, b"GH3STG02": 44, b"GH3STG03": 60, b"GH3STG04": 144, b"GH3BND01": 56, b"GH3BND02": 128}[data[:8]]
    count = struct.unpack_from("<I", data, 8)[0]; offset = 12; used = set()
    for _ in range(count):
        material, vertices, indices = struct.unpack_from("<3I", data, offset)
        used.add(f"{material:08x}"); offset += 12 + vertices*stride + indices*4
    return used


def classify(material, templates, header):
    gh3 = material.get("gh3")
    if header in (b"GH3STG01", b"GH3STG02", b"GH3STG03", b"GH3BND01"):
        return f"fallback: {header.decode()} mesh has no GH3 vertex streams (re-export)"
    if gh3 is None:
        return "fallback: no GH3 material record"
    if gh3["template"] not in templates["templates"]:
        return "fallback: " + templates["untranslated"].get(gh3["template"], f"template {gh3['template']} not in the stage shader")
    return "template"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--stages", type=Path, default=ROOT/"assets/stages")
    parser.add_argument("--templates", type=Path, default=ROOT/"assets/libraries/gh3-band-v1/render/stage-templates.json")
    parser.add_argument("--library", type=Path, default=ROOT/"assets/libraries/gh3-band-v1")
    parser.add_argument("--json", type=Path)
    parser.add_argument("venue", nargs="*")
    args = parser.parse_args()
    templates = json.loads(args.templates.read_text())
    venues = args.venue or sorted(p.name.removeprefix("gh3.") for p in args.stages.glob("gh3.*") if (p/"stage.json").exists())
    rows = []

    def add(venue, part, path):
        header = (path.parent/"meshes.bin").read_bytes()[:8]
        used = used_materials(path.parent/"meshes.bin")
        for key, material in json.loads(path.read_text())["materials"].items():
            if key not in used: continue
            gh3 = material.get("gh3") or {}
            rows.append({"venue": venue, "part": part, "material": key, "source": material.get("source", material.get("template", "")),
                         "template": templates["templates"].get(gh3.get("template"), {}).get("name", gh3.get("template", "")),
                         "path": classify(material, templates, header)})

    # The shared band and crowd library (all four performers and instruments, crowd peds and their hands).
    for path in sorted(args.library.glob("band/*/stage.json")): add("band", path.parent.name, path)
    for path in sorted(args.library.glob("crowd/*/stage.json")) + sorted(args.library.glob("crowd/hands/*/stage.json")):
        add("crowd-peds", path.parent.name, path)
    for venue in venues:
        folder = args.stages/f"gh3.{venue}"
        add(venue, "scenery", folder/"stage.json")
        for path in sorted(folder.glob("actors/*/stage.json")): add(venue, "actor " + path.parent.name, path)
    summary = defaultdict(Counter)
    for row in rows: summary[row["venue"]][row["path"]] += 1
    for venue in ["band", "crowd-peds", *venues]:
        counts = summary[venue]
        print(f"{venue:11s} " + ", ".join(f"{n} {path}" for path, n in sorted(counts.items())))
    total = Counter(row["path"] for row in rows)
    print("total      " + ", ".join(f"{n} {path}" for path, n in sorted(total.items())))
    used = Counter(row["template"] for row in rows if row["path"] == "template")
    print("templates  " + ", ".join(f"{name} {n}" for name, n in used.most_common()))
    for checksum, row in templates["templates"].items():
        for note in row.get("approximations", []): print(f"approximation: {row['name']}: {note}")
    fallbacks = [row for row in rows if row["path"].startswith("fallback")]
    for row in fallbacks: print(f"FALLBACK {row['venue']} {row['part']} {row['material']} {row['source']}: {row['path']}")
    if args.json: args.json.write_text(json.dumps(rows, indent=1) + "\n")
    return 1 if fallbacks else 0


if __name__ == "__main__":
    raise SystemExit(main())
