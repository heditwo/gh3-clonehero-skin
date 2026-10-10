"""Compile a stage pack made in any glTF-compatible authoring tool."""

import argparse
from pathlib import Path

from pipeline.paths import STAGES
from pipeline.stagepack.pack import compile_stage, load_json, write_json


def install(source, stages):
    manifest = load_json(source / "stage-pack.json")
    result = compile_stage(source, stages / manifest["id"])
    index_path = stages / "index.json"
    index = load_json(index_path) if index_path.exists() else {"version": 1, "stages": []}
    index["stages"] = sorted(
        [row for row in index["stages"] if row["id"] != result["id"]] + [{"id": result["id"], "name": result["name"]}],
        key=lambda row: row["id"],
    )
    write_json(index_path, index)
    print(result)
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(prog="python -m pipeline.stagepack.install", description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("--output", type=Path, default=STAGES)
    args = parser.parse_args()
    install(args.source, args.output)
