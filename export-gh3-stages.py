#!/usr/bin/env python3
"""Export original GH3 venues as public, editable stage packs, then import them."""

import argparse
from copy import deepcopy
from io import BytesIO
import importlib.util
import json
from pathlib import Path

import numpy as np
from PIL import Image

from gh3_direction import Source, camera_point, cameras, vector
from gh3_formats import find_array, find_struct, key, values, word, node_prefixes
from gh3_band_formats import animation, sample_animation, skeleton, remap_animation
from gh3_venue_scripts import script_bytes, literal_positions
from gh3_script_bank import definitions, references, dependencies
from prepare_stage_support import angles_matrix
from stage_gltf import GlbWriter, matrix_quaternion, quaternion_matrix
from stage_pack import load_json, write_json


def module(name):
    spec = importlib.util.spec_from_file_location(name.replace("-", "_"), Path(__file__).with_name(name+".py"))
    result = importlib.util.module_from_spec(spec); spec.loader.exec_module(result)
    return result


stage = module("prepare-stage")
lighting_export = module("prepare-lighting")
importer = module("import-stage")
VENUES = {
    "z_party": "Backyard Bash", "z_dive": "Mitch's Moose Lounge", "z_video": "Video Shoot",
    "z_artdeco": "Ye Olde Royal Odeon", "z_prison": "Shanker's Island", "z_wikker": "Desert Rock Tour",
    "z_budokan": "Kaiju Megadome", "z_hell": "Lou's Inferno", "z_soundcheck": "Soundcheck", "z_credits": "Credits",
}
HOUSINGS = ["GO_LightHousing01", "GO_LightHousing01_SmallFlare01", "GO_LightHousing02", "GO_LightHousing02_Small01",
            "GO_LightHousing02_SmallFlare01", "GO_BarnHousing01", "GO_TankHousing01"]
PROP_CLIPS = {
    "ven_artdeco_dragon": "ven_artdeco_dragon_idle", "ven_artdeco_knight1": "ven_artdeco_knight1_idle",
    "ven_artdeco_knight2": "ven_artdeco_knight2_idle", "ven_hell_hammer": "ven_hell_hammer_idle_regular",
    "sped_satanpuppet": "ven_hell_puppet_idle_regular", "ven_prison_gas": "ven_prison_gas_throw",
}


def texture_dictionary(blob):
    result = {}
    for i in range(values(blob, 6, "H")[0]):
        header = word(blob, 8)+i*40; start, size = values(blob, header+28, "II")
        result[f"{word(blob, header+4):08x}"] = blob[start:start+size]
    return result


def decode_textures(materials, texture_blobs):
    images = {}
    for name, material in materials.items():
        material["sourceMaterial"] = "0x" + name
        if "2c509be0" in material["textures"]: material["movieSlot"] = 0
        texture = stage.diffuse_texture(material, texture_blobs)
        material["texture"] = texture
        if material["template"] == "a17d40ed":
            material.update(crowd=True, blend=3, cutoff=0.3, shading="")
            material.pop("lightmap", None)
        if material["template"] in {"4d7335f8", "f4e6db51"} and material["blend"] == 0 and material["cutoff"]:
            material["geometryCutout"] = True
        if material.get("lightmap") not in texture_blobs: material.pop("lightmap", None)
        for name in (texture, material.get("lightmap")):
            if name and name not in images:
                images[name] = Image.open(BytesIO(texture_blobs[name])).convert("RGBA")
    return images


def label_group(value):
    return {key("Band"): "Band", key("Crowd"): "Crowd", key("None"): "Stage"}.get(value, f"group-{value:08x}")


def waypoint_map(zone, nodes):
    roles = {"guitarist": "Guitarist", "bassist": "Bassist", "singer": "Vocalist", "drummer": "Drummer"}
    ids = {}
    for role, source_name in roles.items():
        for suffix in ("start", "walk01", "walk02", "walk03"):
            ids[key(f"{zone}_trg_waypoint_{source_name}_{suffix}")] = role+"_"+suffix
    names = {}; positions = {}
    for node in nodes:
        if node.get(key("Class")) != key("Waypoint"): continue
        id = node[key("Name")]; name = ids.get(id, f"waypoint-{id:08x}")
        names[f"{id:08x}"] = name
        positions[name] = {"position": vector(node[key("Pos")]), "rotation": matrix_quaternion(angles_matrix(node.get(key("Angles"), [0, 0, 0])))}
    band = {role: positions.pop(role+"_start") for role in roles if role+"_start" in positions}
    return names, band, positions


def author_anchor(role, bone):
    if role == "world": return "world"
    return role+".root" if bone == "00000000" else role+".bone."+bone


def author_cameras(bank, waypoint_names, band):
    def group(name):
        return "default" if name == "cameras" else "guitarist" if name == "cameras_guitar_closeup" else name.removeprefix("cameras_").replace("_", "-")
    result = {"version": 1, "shots": {}, "groups": {}, "notes": {}}
    serialized = {}
    for array, shots in bank["arrays"].items():
        choices = []
        for shot in shots:
            if any(p["lockTo"] not in {"world", *band} or p["lookAt"] not in {"", "world", *band} for p in shot["points"]): continue
            entry = {"type": "normal" if shot["type"] == "00000000" else shot["type"], "control": shot["control"], "tag": shot["name"], "points": [],
                     "waypoints": [waypoint_names[n] for n in shot["nodes"] if n in waypoint_names]}
            for prop in ("orbitAngle", "stepTime", "stepCount", "stepDirection", "handCam", "disableOrbit1", "disableOrbit2", "angularSpeed", "heightEase", "times", "interpolation"):
                if prop in shot: entry[prop] = shot[prop]
            for point in shot["points"]:
                row = {"position": point["position"], "rotation": point["rotation"], "horizontalFov": point["fieldOfView"],
                       "anchor": author_anchor(point["lockTo"], point["lockBone"]), "screenOffset": point["screenOffset"]}
                if point["lookAt"]: row["lookAt"] = author_anchor(point["lookAt"], point["lookBone"])
                entry["points"].append(row)
            encoded = json.dumps(entry, sort_keys=True)
            if encoded not in serialized:
                name = f"shot-{len(serialized):03d}"
                serialized[encoded] = name; result["shots"][name] = entry
            choices.append(serialized[encoded])
        if choices: result["groups"][group(array)] = choices
    wide = [n for n in result["groups"].get("default", []) if result["shots"][n]["type"] == "stage" and not result["shots"][n]["waypoints"]]
    if not wide:
        start = band.get("guitarist", next(iter(band.values())))["position"]
        name = "wide-fallback"
        result["shots"][name] = {"type": "stage", "control": "TwoCam", "points": [{"anchor": "world",
            "position": [start[0], start[1]+3, start[2]-10], "lookAt": next(iter(band))+".head", "horizontalFov": 72}]}
        wide = [name]
    result["groups"]["wide"] = wide
    result["groups"].setdefault("default", wide)
    for pitch, selector in bank["notes"].items():
        if selector.get("array"):
            selected = group(selector["array"])
            result["groups"].setdefault(selected, wide)
            result["notes"][str(pitch)] = {"group": selected, "tag": selector.get("name", ""), "type": selector.get("type", "")}
        else: result["notes"][str(pitch)] = {"enable": selector.get("enable", True)}
    return result


def venue_camera_bank(source, scripts, qbs, zone):
    bank = cameras(source, scripts, zone)
    authored = next((rows for qb in qbs if (rows := find_array(qb,zone+"_cameras")) is not None), [])
    credits = next((r for r in authored if r.get(key("controlscript")) == key("CameraCuts_CreditsOrbit")), None)
    normal = next((rows for qb in qbs if (rows := find_array(qb,zone+"_cameras_normal")) is not None), [])
    if credits:
        point = {"position": [0,0,-14.735609], "rotation": [0,0,0,1], "fieldOfView": credits[key("params")][key("FOV")],
                 "lockTo": "guitarist", "lockBone": "00000000", "lookAt": "guitarist", "lookBone": "00000000", "screenOffset": [0,0]}
        shot = {"name": "credits-orbit", "type": "stage", "control": "OrbitContinuous", "points": [point], "nodes": [],
                "angularSpeed": -0.425/0.01668335, "heightEase": [-12,5,np.log(1.002)/0.01668335]}
        bank["unmappedControls"].remove(f'{key("CameraCuts_CreditsOrbit"):08x}')
    elif normal and not authored:
        shot = {"name": "source-normal", "type": "stage", "control": "TwoCam", "points": [camera_point(normal[0])], "nodes": []}
    else: return bank
    bank["arrays"] = {name: [shot] for name in {*bank["arrays"], "cameras"}}
    return bank


def export_actors(source, scripts, global_entries, entries, nodes, directory, zone, graph, venue_values):
    actors = {}
    clips = None
    source_rigs = {count: skeleton(global_entries[key(".ske"), key(name)]) for count, name in
                   ((125, "GH3_guitar"), (75, "GH3_Ped_Dancer"), (28, "GH3_Ped"))}
    for node in nodes:
        model = node.get(key("Model"), "")
        stem = model.replace("\\", "/").split("/")[-1].split(".")[0].lower()
        diver = next((i for i in range(1, 129) if node.get(key("Name")) == key(f"{zone}_TRG_Ped_StageDive0{i}")), None)
        if node.get(key("Class")) != key("GameObject") or not model.lower().endswith(".skin") or model.lower().startswith("real_crowd"): continue
        profile = next((a for b in scripts.values() if (a := find_struct(b, node[key("Profile")])) is not None), None)
        if not profile: raise ValueError(f"Missing actor profile: {model}")
        creation = next((a for reference in profile[0] for b in scripts.values()
                         if (a := find_struct(b, reference)) is not None and key("skeletonname") in a), None)
        if not creation: raise ValueError(f"Missing actor rig: {model}")
        rig = skeleton(global_entries[key(".ske"), creation[key("skeletonname")]])
        def animation_rig(reference):
            count = values(clips[reference], 44, "H")[0]
            if count == len(rig[0]): return rig
            candidate = source_rigs.get(count)
            if candidate is not None and len(set(rig[0]) & set(candidate[0])) == min(len(rig[0]), count): return candidate
            return None
        geometry = next(b for k,n,b in entries if k == key(".skin") and n == key("models\\"+model))
        materials, meshes = stage.scene(geometry, skinned=True)
        textures = {}
        for k,n,b in entries:
            if k == key(".tex"): textures.update(texture_dictionary(b))
        images = decode_textures(materials, textures)
        if clips is None: clips = {n:b for k,n,b in source.load("PAK/perm_anims.pak.xen") if k == key(".ska")}
        if diver is not None:
            mapping = next(a for b in scripts.values() if (a := find_struct(b, "stagediver_anims")) is not None)
            selected = mapping.get(key(zone), mapping[key("default")])
        else:
            roots = [node.get(key("Profile"), 0), node.get(key("TriggerScript"), 0)]
            roots.extend(other.get(key("TriggerScript"), 0) for other in nodes if other.get(key("Model")) == model)
            roots.extend(int(name, 16) for name in venue_values if int(name, 16) != key(zone + "_NodeArray") and node[key("Name")] in graph.get(int(name, 16), []))
            selected = sorted(reference for reference in dependencies(graph, roots) if reference in clips and animation_rig(reference) is not None)
            if stem in PROP_CLIPS: selected = list(dict.fromkeys([key(PROP_CLIPS[stem]), *selected]))
            if not selected: raise ValueError(f"No original animations resolved for actor {model}: {node[key('Name')]:08x}")
        tracks = []
        for reference in dict.fromkeys(selected):
            if reference not in clips: raise ValueError(f"Missing actor animation: {reference:08x}")
            clip_rig = animation_rig(reference)
            if clip_rig is None: raise ValueError(f"Actor animation does not match its named skeleton: {reference:08x}")
            clip = animation(clips[reference],len(clip_rig[0]))
            if clip_rig is not rig: clip = remap_animation(clip, clip_rig[0], rig[0])
            duration = max(0.5,clip[0])
            positions, rotations = sample_animation(clip,rig[2],rig[3],np.linspace(0,duration,round(duration*60)+1))
            tracks.append((f"{reference:08x}",duration,positions,rotations))
        writer = GlbWriter(); writer.actor(meshes,materials,images,rig,tracks)
        name = f"stage-diver-{diver}" if diver is not None else stem if stem in PROP_CLIPS else f"actor-{node[key('Name')]:08x}"
        actor_path = directory/(name+".glb"); writer.save(actor_path)
        actors[name] = {"model": actor_path.name, "clip": tracks[0][0], "lightGroup": label_group(profile.get(key("LightGroup"),key("None"))),
                        "playback": "hold" if diver is not None or stem == "ven_prison_gas" else "loop",
                        "object": f"0x{node[key('Name')]:08x}", "script": f"0x{node[key('TriggerScript')]:08x}" if diver is None and node.get(key("TriggerScript")) else "",
                        "created": key("CreatedAtStart") in node.get(0, []), "initiallyVisible": key("CreatedAtStart") in node.get(0, []),
                        "scale": list(profile.get(key("scale"), [1, 1, 1])),
                        "animationEvents": f"0x{creation[key('animeventtablename')]:08x}" if creation.get(key("animeventtablename")) else "",
                       "transform": {"position": vector(node[key("Pos")]), "rotation": matrix_quaternion(angles_matrix(node.get(key("Angles"),[0,0,0])))}}
        if diver is not None:
            actors[name].update(role=f"stage-diver/{diver}", initiallyVisible=False, actions={"jump": [f"{ref:08x}" for ref in selected]})
    return actors


def export(source, scripts, global_entries, zone, directory, global_references=None):
    directory.mkdir(parents=True, exist_ok=True)
    entries = source.load(f"ZONES/{zone}.pak.xen")
    qbs = [b for k, n, b in entries if k in (key(".qb"), key(".nqb"))]
    script_values = definitions(qbs)
    startup = [zone + "_Setup"] if f"{key(zone + '_Setup'):08x}" in script_values else []
    def array(suffix): return next((a for b in qbs if (a := find_array(b, zone+suffix)) is not None), [])
    nodes = array("_NodeArray"); gfx = array("_gfx_NodeArray")
    volumes = list({node[key("Name")]: node for node in [*gfx, *nodes] if key("LightVolumeType") in node}.values())
    prefixes = {}
    for kind, _, blob in entries:
        if kind == key(".pfx"):
            for name, members in node_prefixes(blob, {key(zone+"_NodeArray"): nodes, key(zone+"_gfx_NodeArray"): gfx}).items():
                prefixes.setdefault(f"0x{name:08x}", []).extend(f"0x{member:08x}" for member in members)
    write_json(directory/"scripts.json", {"version": 1, "definitions": script_values, "startup": startup, "prefixes": prefixes,
        "nodes": [name for name in [zone+"_NodeArray", zone+"_gfx_NodeArray"] if f"{key(name):08x}" in script_values],
        "lightVolumes": {f"0x{node[key('Name')]:08x}": "beam-"+f"{node[key('Name')]:08x}" for node in volumes},
        "performanceChanged": zone + "_performance_changed" if f"{key(zone + '_performance_changed'):08x}" in script_values else ""})
    waypoint_names, band, waypoints = waypoint_map(zone, nodes)
    if not band: raise ValueError(f"Venue has no performer starts: {zone}")
    added_roles = []
    for role, offset in {"guitarist": [2,0,0], "bassist": [-3,0,0], "singer": [0,0,-1], "drummer": [0,0,3]}.items():
        if role in band: continue
        origin = next(iter(band.values()))["position"]
        band[role] = {"position": [a+b for a,b in zip(origin,offset)], "rotation": [0,0,0,1]}
        added_roles.append(role)
    writer = GlbWriter(); primary = max((b for k,n,b in entries if k == key(".scn")), key=len)
    groups = []; mesh_nodes = []
    lighter_ids = {key(f"{zone}_LIGHTER_Geo{i:02}") for i in range(15)}
    scenery_ids = {n[key("Name")] for n in nodes if n.get(key("Class")) in (key("LevelGeometry"), key("LevelObject"))} - lighter_ids
    materials, meshes = stage.scene(primary, nodes, light_groups=groups, mesh_nodes=mesh_nodes,
                                   include_nodes=scenery_ids | (lighter_ids if zone != "z_artdeco" else set()))
    # Venue posters and billboards sample the global ad textures, which GH3 streams from global_ad_tex.pak.
    textures = {name: blob for k,n,b in source.load("PAK/global_ad_tex.pak.xen") if k == key(".tex") for name, blob in texture_dictionary(b).items()}
    for k,n,b in entries:
        if k == key(".tex"): textures.update(texture_dictionary(b))
    used = {m[0] for m in meshes}; materials = {n:m for n,m in materials.items() if n in used}
    images = decode_textures(materials, textures)
    exported_objects = set()
    for i, (mesh, group_id, node) in enumerate(zip(meshes, groups, mesh_nodes)):
        name = f"scenery-{i:04d}"
        index = writer.mesh(name, [mesh], materials, images)
        extras = {"gh3LightGroup": label_group(int(group_id, 16))}
        if node is not None:
            exported_objects.add(node[key("Name")])
            extras.update(gh3Object=f"0x{node[key('Name')]:08x}", gh3ObjectPivot=vector(node.get(key("Pos"), [0, 0, 0])),
                          gh3ObjectRotation=matrix_quaternion(angles_matrix(node.get(key("Angles"), [0, 0, 0]))),
                          gh3ObjectCreated=key("CreatedAtStart") in node.get(0, []) or node[key("Name")] in lighter_ids,
                          gh3ObjectScript=f"0x{node[key('TriggerScript')]:08x}" if node.get(key("TriggerScript")) else "")
        if node is not None and node.get(key("Name")) in lighter_ids:
            extras.update(gh3VisibilityGroup="crowd-lighters", gh3InitiallyVisible=zone == "z_artdeco")
        if index is not None: writer.node(name, index, extras=extras)
    for node in nodes:
        if node[key("Name")] in exported_objects or not (node.get(key("Class")) in (key("LevelGeometry"), key("LevelObject"), key("Waypoint")) or
            node.get(key("Class")) == key("GameObject") and node.get(key("Model"), "").lower() == "none"): continue
        writer.node(f"object-{node[key('Name')]:08x}", extras={
            "gh3Object": f"0x{node[key('Name')]:08x}", "gh3ObjectPivot": vector(node.get(key("Pos"), [0, 0, 0])),
            "gh3ObjectRotation": matrix_quaternion(angles_matrix(node.get(key("Angles"), [0, 0, 0]))),
            "gh3ObjectCreated": key("CreatedAtStart") in node.get(0, []),
            "gh3ObjectScript": f"0x{node[key('TriggerScript')]:08x}" if node.get(key("TriggerScript")) else ""})
    temporary_lighting = directory/"lighting.json"
    lighting_export.export(source.directory, temporary_lighting, zone)
    lights = load_json(temporary_lighting); lights.pop("sources", None)
    for light in lights["lights"]:
        light["id"] = "light-"+light["id"]; light["group"] = label_group(int(light["group"], 16))
    for snapshot in lights["snapshots"].values():
        snapshot["beams"] = {"beam-"+n:v for n,v in snapshot["beams"].items()}
    write_json(temporary_lighting, lights)
    initial = lights["snapshots"][lights["moods"]["intro"][0]]["beams"]
    models = {}; fixture_count = 0; fixtures = set()
    for node in volumes:
        model = next((m for m in HOUSINGS if key(m) == node.get(key("Type"))), None)
        if not model or key("CreatedAtStart") not in node.get(0, []): continue
        beam = "beam-"+f'{node[key("Name")]:08x}'
        if beam not in initial: continue
        if model not in models:
            stem = "models\\lighthousings\\"+model+"\\"+model
            geometry = global_entries[(key(".mdl"), key(stem+".mdl"))]
            texture_blob = global_entries[(key(".tex"), key(stem+".tex"))]
            mat, parts = stage.scene(geometry)
            parts = [p for p in parts if mat[p[0]]["blend"] in (0, 1)]
            mat = {n:v for n,v in mat.items() if n in {p[0] for p in parts}}
            images = decode_textures(mat, texture_dictionary(texture_blob))
            renamed = {model+"-"+n:v for n,v in mat.items()}
            parts = [(model+"-"+n,c,v,f) for n,c,v,f in parts]
            models[model] = writer.mesh(model, parts, renamed, images)
        matrix = np.eye(4); matrix[:3, :3] = quaternion_matrix(initial[beam]["rotation"])
        fixed = key("IgnoreSnapshotPositions") in node.get(0, [])
        matrix[:3, 3] = vector(node[key("Pos")]) if fixed else initial[beam]["position"]
        writer.node(beam, models[model], matrix, {"gh3FixtureId": beam, "gh3FixtureFixedPosition": fixed, "gh3FixtureObject": f"0x{node[key('Name')]:08x}"})
        fixtures.add(node[key("Name")])
        fixture_count += 1
    for node in volumes:
        if node[key("Name")] in fixtures or node[key("Name")] in exported_objects: continue
        writer.node(f"volume-{node[key('Name')]:08x}", extras={
            "gh3Object": f"0x{node[key('Name')]:08x}", "gh3ObjectPivot": vector(node.get(key("Pos"), [0, 0, 0])),
            "gh3ObjectRotation": matrix_quaternion(angles_matrix(node.get(key("Angles"), [0, 0, 0]))),
            "gh3ObjectCreated": key("CreatedAtStart") in node.get(0, []),
            "gh3ObjectScript": f"0x{node[key('TriggerScript')]:08x}" if node.get(key("TriggerScript")) else ""})
    bank = venue_camera_bank(source, scripts, qbs, zone)
    for node in gfx:
        if node.get(key("Class")) != key("LevelLight"): continue
        writer.node(f"light-object-{node[key('Name')]:08x}", extras={
            "gh3Object": f"0x{node[key('Name')]:08x}", "gh3ObjectPivot": vector(node.get(key("Pos"), [0, 0, 0])),
            "gh3ObjectRotation": matrix_quaternion(angles_matrix(node.get(key("Angles"), [0, 0, 0]))),
            "gh3ObjectCreated": key("CreatedAtStart") in node.get(0, []),
            "gh3ObjectScript": f"0x{node[key('TriggerScript')]:08x}" if node.get(key("TriggerScript")) else ""})
    write_json(directory/"cameras.json", author_cameras(bank, waypoint_names, band))
    effects = {}
    pyro_script = next((blob for qb in qbs if (blob := script_bytes(qb, zone+"_pyro")) is not None), None)
    if pyro_script:
        effects["pyro"] = [{"position": vector(p), "rotation": [0, 0, 0, 1]} for p in literal_positions(pyro_script,qbs)]
        for kind in ("sparks", "fireworks", "smoke"): effects[kind] = deepcopy(effects["pyro"])
    for node in gfx:
        reference = node.get(key("ScriptReference"))
        for effect, suffixes in {"pyro": ("GoodFlames01", "Pyro01", "GoodPyro01"), "sparks": ("GoodSparks01",), "smoke": ("GoodSmoke01", "PipeSmoke01")}.items():
            if reference in {key("GP_"+zone[2:]+"_"+suffix) for suffix in suffixes}:
                effects.setdefault(effect, []).append({"position": vector(node[key("Pos")]), "rotation": matrix_quaternion(angles_matrix(node.get(key("Angles"), [0,0,0])))})
    writer.save(directory/"stage.glb")
    graph = (global_references if global_references is not None else references(scripts.values())) | references(qbs)
    actors = export_actors(source, scripts, global_entries, entries, nodes, directory, zone, graph, script_values)
    manifest = {"version": 1, "kind": "stage", "id": "gh3."+zone[2:], "name": VENUES[zone], "library": "gh3-band-v1",
                "model": "stage.glb", "band": band, "waypoints": waypoints, "cameras": "cameras.json", "lighting": "lighting.json", "effects": effects, "actors": actors,
                "scripts": "scripts.json"}
    crowd_models = next((rows for qb in scripts.values() if (rows := find_array(qb, zone+"_crowd_models")) is not None), [])
    if crowd_models:
        roles = {key(f"crowd{i}"): f"crowd{i}" for i in range(1, 9)}
        manifest["crowd"] = [{"role": roles[row[key("name")]],
            "model": row[key("Model")].replace("\\", "/").split("/")[-1].split(".")[0].lower(),
            "yaw": -signed_angle(row.get(key("roty"), 0))} for row in crowd_models if key("remap_only") not in row.get(0, [])]
    write_json(directory/"stage-pack.json", manifest)
    report = {"zone": zone, "sourceSha256": source.hashes[f"ZONES/{zone}.pak.xen"], "meshes": len(meshes), "fixtures": fixture_count, "addedBandPlacements": added_roles,
              "unmappedCameras": bank["unmappedControls"], "modelNodes": [{"name": f'{n[key("Name")]:08x}', "model": n.get(key("Model")),
                  "profile": f'{n.get(key("Profile"), 0):08x}', "created": key("CreatedAtStart") in n.get(0, [])}
                  for n in nodes if n.get(key("Class")) == key("GameObject") and n.get(key("Model"), "none") != "none"]}
    write_json(directory/"source-report.json", report)
    return report


def signed_angle(value):
    return value - 0x100000000 if isinstance(value, int) and value > 0x7fffffff else value


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data", type=Path, default=Path.home()/"games/gh3-test-tools/game/DATA")
    parser.add_argument("--output", type=Path, default=Path("assets/stage-packs"))
    parser.add_argument("--compiled", type=Path, default=Path("assets/stages"))
    parser.add_argument("--venue", choices=sorted(VENUES), action="append")
    args = parser.parse_args(); source = Source(args.data); scripts = source.scripts()
    global_entries = {(k,n):b for k,n,b in source.load("ZONES/global.pak.xen")}
    global_references = references(scripts.values())
    reports = []
    for zone in args.venue or VENUES:
        folder = args.output/("gh3."+zone[2:])
        report = export(source, scripts, global_entries, zone, folder, global_references)
        report["compiled"] = importer.install(folder, args.compiled); reports.append(report)
    write_json(args.output/"conversion-report.json", {"version": 1, "venues": reports})


if __name__ == "__main__":
    main()
