#!/usr/bin/env python3
"""Export the owner's GH3 venue light groups and authored lighting snapshots."""

import argparse
import json
import math
from pathlib import Path

from gh3_direction import Source, vector
from gh3_formats import find_array, find_struct, key


def beam_rotation(xyz):
    x, y, z = xyz
    w = math.sqrt(max(0, 1 - x*x - y*y - z*z))
    return [v / math.sqrt(2) for v in (x - w, y + z, y - z, w + x)]


def light_rotation(xyzw):
    x, y, z, w = xyzw
    return [v / math.sqrt(2) for v in (w - x, z - y, z + y, w + x)]


def export(directory, destination):
    source = Source(directory)
    entries = source.load("ZONES/z_budokan.pak.xen")
    qbs = [b for k, n, b in entries if k in (key(".qb"), key(".nqb"))]
    nodes = next(a for b in qbs if (a := find_array(b, "z_budokan_gfx_NodeArray")) is not None)
    snapshots = next(a for b in qbs if (a := find_struct(b, "z_budokan_snapshots")) is not None)
    mapping = next(a for b in qbs if (a := find_struct(b, "z_budokan_lightshow_mapping")) is not None)
    scripts = source.scripts()
    common = scripts[key("scripts\\fx\\lightshow.qb")]
    exclusions = find_array(common, "LightShow_ColorOverrideExcludeLights")
    overrides = find_struct(common, "LightShow_ColorOverrides")
    light_ids = {r[key("LightChecksum")] for r in snapshots[key("Intro01")] if key("LightChecksum") in r}
    lights = []
    for node in nodes:
        if node.get(key("Name")) not in light_ids: continue
        light_type = next(t for t in ("Ambient", "Dir", "Point", "Spot") if key(t) == node[key("Type")])
        q = node[key("RotationQuat")]
        lights.append({"id": f"{node[key('Name')]:08x}", "group": f"{node.get(key('LightGroup'), key('None')):08x}",
                       "type": light_type, "position": vector(node[key("Pos")]), "rotation": light_rotation(q),
                       "overrideColor": node[key("Name")] not in exclusions,
                       "hotspot": node.get(key("Hotspot"), 0), "field": node.get(key("Field"), 0)})
    converted = {}
    for snapshot, rows in snapshots.items():
        if snapshot == 0: continue
        values, beams = {}, {}
        for row in rows:
            if key("LightChecksum") in row:
                values[f"{row[key('LightChecksum')]:08x}"] = {
                    "color": [row[key(c)] / 255 for c in ("r", "g", "b")], "intensity": row[key("Intensity")],
                    "specular": row.get(key("SpecularIntensity"), row[key("Intensity")]),
                    "near": row.get(key("FarAttenStart"), 0), "far": row.get(key("FarAttenEnd"), 200)}
            if key("HousingChecksum") in row:
                beams[f"{row[key('HousingChecksum')]:08x}"] = {
                    "position": vector(row[key("Pos")]), "rotation": beam_rotation(row[key("Quat")]),
                    "color": [row[key('VolumeColor' + c)] / 255 for c in ("Red", "Green", "Blue")],
                    "density": row.get(key("VolumeDensity"), 0), "startRadius": row[key("StartRadius")],
                    "endRadius": row[key("EndRadius")], "range": row[key("Range")]}
        converted[f"{snapshot:08x}"] = {"lights": [values[l["id"]] for l in lights], "beams": beams}
    moods = {}
    for mood in ["intro", "verse", "chorus", "solo", "blackout", "flare", "strobe"]:
        settings = mapping[key(mood)][key("medium")]
        moods[mood] = [f"{s:08x}" for s in settings[key("snapshots")]]
    output = {"version": 1, "lights": lights, "snapshots": converted, "moods": moods,
              "colors": {c: [v / 255 for v in overrides[key(c)]] for c in
                         ["red", "blue", "yellow", "white", "magenta", "green", "purple", "orange"]},
              "sources": source.hashes}
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(json.dumps(output, separators=(",", ":"), allow_nan=False) + "\n")
    print(f"Exported {len(lights)} lights, {len(converted)} snapshots, {len(next(iter(converted.values()))['beams'])} beam housings")
    print(destination)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data", type=Path, default=Path.home() / "games/gh3-test-tools/game/DATA")
    parser.add_argument("--output", type=Path, default=Path(__file__).parent / "assets/stages/budokan/lighting.json")
    args = parser.parse_args()
    export(args.data, args.output)
