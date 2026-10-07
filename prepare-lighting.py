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


def export(directory, destination, venue="z_budokan"):
    source = Source(directory)
    entries = source.load(f"ZONES/{venue}.pak.xen")
    qbs = [b for k, n, b in entries if k in (key(".qb"), key(".nqb"))]
    nodes = next(a for b in qbs if (a := find_array(b, venue+"_gfx_NodeArray")) is not None)
    geometry_nodes = next((a for b in qbs if (a := find_array(b, venue+"_NodeArray")) is not None), [])
    volumes = {node[key("Name")]: node for node in [*nodes, *geometry_nodes] if key("LightVolumeType") in node}
    snapshots = next((a for b in qbs if (a := find_struct(b, venue+"_snapshots")) is not None), None)
    mapping = next((a for b in qbs if (a := find_struct(b, venue+"_lightshow_mapping")) is not None), None)
    scripts = source.scripts()
    common = scripts[key("scripts\\fx\\lightshow.qb")]
    exclusions = find_array(common, "LightShow_ColorOverrideExcludeLights")
    overrides = find_struct(common, "LightShow_ColorOverrides")
    light_ids = {n[key("Name")] for n in nodes if n.get(key("Class")) == key("LevelLight") and n.get(key("Type")) in {key(t) for t in ("Ambient", "Dir", "Point", "Spot")}}
    lights = []
    for node in nodes:
        if node.get(key("Name")) not in light_ids: continue
        light_type = next(t for t in ("Ambient", "Dir", "Point", "Spot") if key(t) == node[key("Type")])
        q = node[key("RotationQuat")]
        lights.append({"id": f"{node[key('Name')]:08x}", "object": f"0x{node[key('Name')]:08x}", "group": f"{node.get(key('LightGroup'), key('None')):08x}",
                       "type": light_type, "position": vector(node[key("Pos")]), "rotation": light_rotation(q),
                       "overrideColor": node[key("Name")] not in exclusions,
                       "hotspot": node.get(key("Hotspot"), 0), "field": node.get(key("Field"), 0)})
    converted = {}
    defaults = {}
    for light in lights:
        node = next(n for n in nodes if f'{n.get(key("Name"), 0):08x}' == light["id"])
        near = max(0, node.get(key("AttenStart"), 0))
        defaults[light["id"]] = {"color": node.get(key("LightColor"), [1, 1, 1]), "intensity": node.get(key("Intensity"), 1),
            "near": near, "far": max(near+0.001, node.get(key("AttenEnd"), 200)), "inherit": True}
    for snapshot, rows in (snapshots or {}).items():
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
        converted[f"{snapshot:08x}"] = {"lights": [values.get(l["id"], defaults[l["id"]]) for l in lights], "beams": beams}
    if not snapshots:
        settings = []
        for light in lights:
            node = next(n for n in nodes if f'{n.get(key("Name"), 0):08x}' == light["id"])
            near = node.get(key("AttenStart"), 0)
            far = max(near+0.001, node.get(key("AttenEnd"), 200))
            settings.append({"color": node.get(key("LightColor"), [1, 1, 1]), "intensity": node.get(key("Intensity"), 1),
                             "near": near, "far": far, "specular": node.get(key("SpecularIntensity"), 0)})
        beams = {}
        for node in nodes:
            if key("VolumeColor") not in node or key("CreatedAtStart") not in node.get(0, []): continue
            from stage_gltf import matrix_quaternion
            from prepare_stage_support import angles_matrix
            beams[f'{node[key("Name")]:08x}'] = {"position": vector(node[key("Pos")]),
                "rotation": matrix_quaternion(angles_matrix(node.get(key("Angles"), [0, 0, 0]))),
                "color": [v/255 for v in node[key("VolumeColor")]], "density": node.get(key("VolumeDensity"), 0),
                "startRadius": node.get(key("StartRadius"), 0.1), "endRadius": node.get(key("EndRadius"), 1), "range": node.get(key("Range"), 10)}
        converted["static"] = {"lights": settings, "beams": beams}
    beam_defaults = {name: value for snapshot in converted.values() for name, value in snapshot["beams"].items()}
    from stage_gltf import matrix_quaternion
    from prepare_stage_support import angles_matrix
    for id, node in volumes.items():
        if f"{id:08x}" in beam_defaults: continue
        value = {"position": vector(node[key("Pos")]), "rotation": matrix_quaternion(angles_matrix(node.get(key("Angles"), [0, 0, 0]))),
                 "color": [v/255 for v in node.get(key("VolumeColor"), [255, 255, 255])], "density": node.get(key("VolumeDensity"), 0),
                 "startRadius": node.get(key("StartRadius"), .1), "endRadius": node.get(key("EndRadius"), 1), "range": node.get(key("Range"), 10)}
        for snapshot in converted.values(): snapshot["beams"][f"{id:08x}"] = value
    for snapshot in converted.values():
        for name, value in beam_defaults.items():
            snapshot["beams"].setdefault(name, {**value, "density": 0, "inherit": True})
    moods = {}
    for mood in ["intro", "verse", "chorus", "solo", "blackout", "flare", "strobe"]:
        if mapping and snapshots:
            settings = mapping[key(mood)][key("medium")]
            refs = settings[key("snapshots")]
            if isinstance(refs, int):
                refs = next((a for b in qbs+list(scripts.values()) if (a := find_array(b, refs)) is not None), [refs])
            moods[mood] = [f"{s:08x}" for s in refs]
        else:
            moods[mood] = ["static"]
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
    parser.add_argument("--venue", default="z_budokan")
    args = parser.parse_args()
    export(args.data, args.output, args.venue)
