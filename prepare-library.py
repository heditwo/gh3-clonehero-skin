#!/usr/bin/env python3
"""Build the shared GH3 band library used by every stage pack."""

import argparse
from hashlib import sha256
import importlib.util
from pathlib import Path
import shutil

from stage_pack import load_json, write_json
from gh3_direction import Source


def module(name):
    spec = importlib.util.spec_from_file_location(name.replace("-", "_"), Path(__file__).with_name(name+".py"))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


def prepare(data, output, existing=None):
    output.mkdir(parents=True, exist_ok=True)
    if existing:
        for name in ("band", "direction", "hand-fire"):
            shutil.copytree(existing/name, output/name, dirs_exist_ok=True)
        band = load_json(output/"band/band.json")
        for member in band["members"]: member.update(position=[0, 0, 0], yaw=0)
        band.get("sources", {}).pop("ZONES/z_budokan.pak.xen", None)
        write_json(output/"band/band.json", band)
        performance = load_json(output/"direction/performance.json")
        performance.pop("waypoints", None)
        performance.get("sources", {}).pop("ZONES/z_budokan.pak.xen", None)
        write_json(output/"direction/performance.json", performance)
        (output/"direction/cameras.json").unlink(missing_ok=True)
    else:
        module("prepare-band").convert(data, output/"band", shared=True)
        module("prepare-direction").convert(data, output/"direction", shared=True)
        module("prepare-hair").convert(data, output/"band/hair.json")
        module("prepare-hand-fire").convert(Source(data), output/"hand-fire")
    module("prepare-crowd").convert(data, output/"crowd")
    module("prepare-stage-audio").convert(data, output/"audio")
    module("prepare-stage-scripts").convert(data, output/"scripts.json")
    module("prepare-tesla").convert(data, output/"render")
    performance = load_json(output/"direction/performance.json")
    write_json(output/"catalog.json", {"version": 1, "library": "gh3-band-v1", "members": {
        role: {"bones": member["bones"], "groups": member["groups"], "layers": member["layers"], "clips": member["clips"]}
        for role, member in performance["members"].items()}})
    write_json(output/"library.json", {"version": 1, "id": "gh3-band-v1", "files": {
        str(p.relative_to(output)): sha256(p.read_bytes()).hexdigest() for p in sorted(output.rglob("*")) if p.is_file() and p.name != "library.json"}})
    print(output)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data", type=Path, default=Path.home()/"games/gh3-test-tools/game/DATA")
    parser.add_argument("--output", type=Path, default=Path("assets/libraries/gh3-band-v1"))
    parser.add_argument("--existing", type=Path, help="Reuse a complete generated band, direction, and hand-fire set; rebuild the crowd")
    args = parser.parse_args()
    prepare(args.data, args.output, args.existing)
