"""Compile public stage manifests and GLB assets into the shared runtime format."""

from copy import deepcopy
from hashlib import sha256
import json
import math
from pathlib import Path
import re
import shutil
import struct
import tempfile

import numpy as np
from PIL import Image

from gh3_formats import key
from gh3_mesh_cutout import alpha_region, trim_mesh
from stage_gltf import GlbReader, SHADING, STATIC_VERTEX, matrix_quaternion


ROLES = {"guitarist", "bassist", "singer", "drummer"}
CONTROLS = {"TwoCam", "HandCam", "Orbit", "OrbitAndMorphTwoCam", "OrbitContinuous", "Keyframes", "Pan", "ThreePos", "FourPos", "SpecialStepMove"}
EFFECTS = {"pyro", "sparks", "fireworks", "smoke"}


def load_json(path):
    def reject(value): raise ValueError(f"Non-finite JSON value: {value}")
    return json.loads(Path(path).read_text(), parse_constant=reject)


def write_json(path, value):
    Path(path).write_text(json.dumps(value, indent=2, allow_nan=False) + "\n")


def fields(value, allowed, required=()):
    if not isinstance(value, dict) or set(value)-set(allowed) or set(required)-set(value):
        raise ValueError(f"Invalid fields: expected {sorted(required)}; allowed {sorted(allowed)}; found {list(value) if isinstance(value, dict) else value}")


def vector(value, size=3):
    if not isinstance(value, list) or len(value) != size or any(type(v) not in (int, float) or not math.isfinite(v) for v in value):
        raise ValueError(f"Expected a finite {size}-element vector")
    return value


def number(value, minimum, maximum):
    if type(value) not in (int, float) or not math.isfinite(value) or not minimum <= value <= maximum:
        raise ValueError(f"Expected a number from {minimum} to {maximum}")
    return value


def rotation(value):
    q = vector(value, 4)
    if abs(sum(v*v for v in q)-1) > 0.001: raise ValueError("Rotation must have unit length")
    return q


def transform(value):
    fields(value, {"position", "rotation"}, {"position", "rotation"})
    vector(value["position"]); rotation(value["rotation"])
    return value


def relative(root, name):
    if not isinstance(name, str) or not name or "\\" in name or Path(name).is_absolute(): raise ValueError("Invalid pack-relative path")
    root = Path(root).resolve(); path = (root/name).resolve()
    if not path.is_relative_to(root) or not path.is_file(): raise ValueError(f"Missing pack file: {name}")
    return path


def anchor(name, roles):
    if name == "world": return "world", "00000000"
    role, _, part = name.partition(".")
    if role not in roles: raise ValueError(f"Camera refers to absent performer: {name}")
    bones = {"root": "00000000", "head": f'{key("bone_head"):08x}', "hand_left": f'{key("Bone_Palm_L"):08x}', "hand_right": f'{key("Bone_Palm_R"):08x}'}
    if part in bones: return role, bones[part]
    if part.startswith("bone."):
        bone = part[5:]
        return role, bone if re.fullmatch(r"[0-9a-f]{8}", bone) else f"{key(bone):08x}"
    raise ValueError(f"Invalid actor anchor: {name}")


def camera_array(group):
    return "cameras" if group == "default" else "cameras_guitar_closeup" if group == "guitarist" else "cameras_"+group.replace("-", "_")


def compile_cameras(data, band, waypoints):
    fields(data, {"version", "shots", "groups", "notes"}, {"version", "shots", "groups"})
    if type(data["version"]) != int or data["version"] != 1 or not data["groups"].get("wide"): raise ValueError("A stage needs a wide camera group")
    shots = {}
    for name, shot in data["shots"].items():
        fields(shot, {"type", "control", "points", "orbitAngle", "stepTime", "stepCount", "stepDirection", "handCam", "disableOrbit1", "disableOrbit2", "waypoints", "tag", "angularSpeed", "heightEase", "times", "interpolation"}, {"type", "control", "points"})
        keyframes = shot["control"] == "Keyframes"
        if shot["control"] not in CONTROLS or not 1 <= len(shot["points"]) <= (64 if keyframes else 4): raise ValueError(f"Invalid camera control: {name}")
        count = {"ThreePos": 3, "FourPos": 4}.get(shot["control"])
        if count and len(shot["points"]) != count or not count and not keyframes and len(shot["points"]) > 2: raise ValueError(f"Invalid camera point count: {name}")
        if keyframes:
            times = shot.get("times", [])
            if len(times) != len(shot["points"]) or len(times) < 2 or times[0] != 0: raise ValueError("Keyframe times must start at zero and match the camera points")
            for time in times: number(time,0,86400)
            if any(b <= a for a,b in zip(times,times[1:])): raise ValueError("Keyframe times must increase")
        if shot.get("interpolation", "smooth") not in ("linear", "smooth"): raise ValueError("Invalid camera interpolation")
        for prop in ("handCam", "disableOrbit1", "disableOrbit2"):
            if prop in shot and type(shot[prop]) != bool: raise ValueError(f"Invalid camera flag: {prop}")
        number(shot.get("orbitAngle", 0), -36000, 36000)
        number(shot.get("stepTime", 6), 0.001, 86400)
        number(shot.get("stepCount", 6), 0, 10000)
        vector(shot.get("stepDirection", [0,0,0]))
        number(shot.get("angularSpeed",0),-3600,3600)
        ease = vector(shot.get("heightEase", [0,0,0]))
        number(ease[2],0,100)
        if shot["control"] == "OrbitContinuous":
            if len(shot["points"]) != 1 or "lookAt" not in shot["points"][0]: raise ValueError("Continuous orbit needs one point and a target")
        if any(n not in waypoints for n in shot.get("waypoints", [])): raise ValueError(f"Unknown camera waypoint: {name}")
        row = {"name": shot.get("tag", name), "type": shot["type"], "control": shot["control"], "points": [], "nodes": shot.get("waypoints", [])}
        for prop in ("orbitAngle", "stepTime", "stepCount", "stepDirection", "handCam", "disableOrbit1", "disableOrbit2", "angularSpeed", "heightEase", "times", "interpolation"):
            if prop in shot: row[prop] = shot[prop]
        for p in shot["points"]:
            fields(p, {"position", "rotation", "horizontalFov", "anchor", "lookAt", "screenOffset"}, {"position", "horizontalFov", "anchor"})
            role, bone = anchor(p["anchor"], band)
            look, look_bone = anchor(p["lookAt"], band) if "lookAt" in p else ("", "00000000")
            if not look and "rotation" not in p: raise ValueError("Camera point needs rotation or lookAt")
            q = rotation(p.get("rotation", [0, 0, 0, 1]))
            number(p["horizontalFov"], 0, 170)
            if p["horizontalFov"] in (0,170): raise ValueError("Camera FOV must be greater than zero and less than 170")
            row["points"].append({"position": vector(p["position"]), "rotation": q, "fieldOfView": p["horizontalFov"],
                                  "lockTo": role, "lockBone": bone, "lookAt": look, "lookBone": look_bone,
                                  "screenOffset": vector(p.get("screenOffset", [0, 0]), 2)})
        shots[name] = row
    arrays = {}
    for group, names in data["groups"].items():
        if not names or any(n not in shots for n in names): raise ValueError(f"Invalid camera group: {group}")
        arrays[camera_array(group)] = [shots[n] for n in names]
    if not any(not shot["nodes"] for shot in arrays["cameras_wide"]): raise ValueError("Wide group needs an unrestricted shot")
    arrays.setdefault("cameras", arrays["cameras_wide"])
    for group in ["guitarist", "bassist", "singer", "drummer", "orbit"]:
        arrays.setdefault(camera_array(group), arrays["cameras_wide"])
    notes = {}
    for pitch, selector in data.get("notes", {}).items():
        fields(selector, {"group", "tag", "type", "enable"})
        if not str(pitch).isdigit() or not 0 <= int(pitch) <= 127: raise ValueError("Invalid camera note")
        group = selector.get("group")
        notes[pitch] = {"array": camera_array(group) if group else "", "name": selector.get("tag", ""),
                        "type": selector.get("type", ""), "enable": selector.get("enable", True)}
        if group and camera_array(group) not in arrays: raise ValueError(f"Missing note-selected camera group: {group}")
    return {"version": 1, "arrays": arrays, "notes": notes}


def compile_lighting(data):
    data = deepcopy(data)
    fields(data, {"version", "lights", "snapshots", "moods", "colors"}, {"version", "lights", "snapshots", "moods", "colors"})
    if type(data["version"]) != int or data["version"] != 1 or not data["snapshots"] or not data["lights"]: raise ValueError("Invalid lighting bank")
    for light in data["lights"]:
        fields(light, {"id", "object", "group", "type", "position", "rotation", "overrideColor", "hotspot", "field"}, {"id", "group", "type", "position", "rotation"})
        if "object" in light and (not isinstance(light["object"], str) or not 1 <= len(light["object"]) <= 256): raise ValueError("Invalid light object identity")
        if light["type"] not in ("Ambient", "Dir", "Point", "Spot"): raise ValueError("Unsupported light type")
        vector(light["position"]); rotation(light["rotation"])
        if type(light.get("overrideColor", False)) != bool: raise ValueError("Invalid light color override flag")
        number(light.get("hotspot",0),0,180); number(light.get("field",0),0,180)
        light["group"] = f'{key(light["group"]):08x}'
        light["id"] = f'{key(light["id"]):08x}'
    if len({l["id"] for l in data["lights"]}) != len(data["lights"]) or len(data["lights"]) > 128: raise ValueError("Duplicate or excess lights")
    beam_ids = None
    for snapshot in data["snapshots"].values():
        fields(snapshot, {"lights", "beams"}, {"lights", "beams"})
        if len(snapshot["lights"]) != len(data["lights"]): raise ValueError("Snapshot light count differs")
        if beam_ids is not None and set(snapshot["beams"]) != beam_ids: raise ValueError("Snapshot beam IDs differ")
        beam_ids = set(snapshot["beams"])
        for light in snapshot["lights"]:
            fields(light, {"color", "intensity", "near", "far", "specular", "inherit"}, {"color", "intensity", "near", "far"})
            vector(light["color"]); number(light["intensity"],-100,100)
            number(light["near"],0,100000); number(light["far"],light["near"],100001)
            if type(light.get("inherit",False)) != bool: raise ValueError("Invalid light inheritance flag")
        for beam in snapshot["beams"].values():
            fields(beam, {"position", "rotation", "color", "density", "startRadius", "endRadius", "range", "inherit"}, {"position", "rotation", "color", "density", "startRadius", "endRadius", "range"})
            vector(beam["position"]); rotation(beam["rotation"]); vector(beam["color"])
            for prop in ("density", "startRadius", "endRadius", "range"): number(beam[prop],0,100000)
            if type(beam.get("inherit",False)) != bool: raise ValueError("Invalid beam inheritance flag")
        snapshot["beams"] = {f"{key(k):08x}": v for k, v in snapshot["beams"].items()}
    for mood in ("intro", "blackout"):
        if not data["moods"].get(mood): raise ValueError(f"Missing lighting mood: {mood}")
    for mood in ("verse", "chorus", "solo", "flare", "strobe"):
        data["moods"].setdefault(mood, data["moods"]["intro"])
    if any(n not in data["snapshots"] for rows in data["moods"].values() for n in rows): raise ValueError("Unknown lighting snapshot")
    for color in data["colors"].values(): vector(color)
    return data


def basic_lighting():
    settings = [{"color": [1, 1, 1], "intensity": 0.65, "near": 0, "far": 200}] * 2
    return {"version": 1, "lights": [{"id": g, "group": g, "type": "Ambient", "position": [0, 0, 0], "rotation": [0, 0, 0, 1]} for g in ("Stage", "Band")],
            "snapshots": {"fill": {"lights": settings, "beams": {}}}, "moods": {"intro": ["fill"], "blackout": ["fill"]}, "colors": {}}


def split_mesh(material, packed, faces):
    for start in range(0, len(faces), 20000):
        section = faces[start:start+20000]
        used, indices = np.unique(section.reshape(-1), return_inverse=True)
        yield material, len(used), packed[used].tobytes(), indices.reshape(-1, 3).tolist()


def write_meshes(folder, meshes, materials, groups=None, skinned=False):
    folder.mkdir(parents=True, exist_ok=True)
    with (folder/"meshes.bin").open("wb") as stream:
        stream.write((b"GH3BND02" if skinned else b"GH3STG04") + struct.pack("<I", len(meshes)))
        for material, count, vertices, faces in meshes:
            stream.write(struct.pack("<III", int(material, 16), count, len(faces)*3)); stream.write(vertices)
            stream.write(np.asarray(faces, dtype="<u4").tobytes())
    result = {"version": 1, "name": folder.name, "cameraPosition": [0, 3, -10], "cameraTarget": [0, 1, 0],
              "fieldOfView": 55, "materials": materials, "lightGroups": groups or []}
    write_json(folder/"stage.json", result)
    return result


def gh3_shader_material(glb, data, texture_dir, images):
    """Validate a GH3 template material: its template checksum, constant banks, sampler textures, and render state."""
    fields(data, {"template", "vertexConstants", "pixelConstants", "textures", "addressing", "flags", "blend", "alphaRef", "drawOrder"},
           {"template", "vertexConstants", "pixelConstants", "textures", "addressing", "flags", "blend", "alphaRef", "drawOrder"})
    if not isinstance(data["template"], str) or not re.fullmatch(r"[0-9a-f]{8}", data["template"]): raise ValueError("Invalid GH3 template")
    banks = {}
    for bank, limit in (("vertexConstants", 64), ("pixelConstants", 64)):
        rows = data[bank]
        if not isinstance(rows, list) or len(rows) > limit: raise ValueError("Invalid GH3 constant bank")
        banks[bank] = [[float(number(v, -1e9, 1e9)) for v in vector(row, 4)] for row in rows]
    textures = data["textures"]; addressing = data["addressing"]
    if not isinstance(textures, list) or len(textures) > 16 or not isinstance(addressing, list) or len(addressing) != len(textures):
        raise ValueError("Invalid GH3 sampler list")
    names = []
    for index in textures:
        if type(index) != int or not -1 <= index < len(glb.data.get("textures", [])): raise ValueError("Invalid GH3 sampler texture")
        if index < 0: names.append(""); continue
        image = f"{key('texture-'+str(index)):08x}"
        if image not in images:
            images[image] = glb.image(index); images[image].save(texture_dir/(image+".png"))
        names.append(image)
    if any(type(v) != int or not 0 <= v <= 255 for v in addressing): raise ValueError("Invalid GH3 sampler addressing")
    for field, maximum in (("flags", 0xFFFFFFFF), ("blend", 12), ("alphaRef", 255)):
        if type(data[field]) != int or not 0 <= data[field] <= maximum: raise ValueError(f"Invalid GH3 render state: {field}")
    return {"template": data["template"], **banks, "textures": names, "addressing": addressing, "flags": data["flags"],
            "blend": data["blend"], "alphaRef": data["alphaRef"], "drawOrder": float(number(data["drawOrder"], -1e6, 1e6))}


def import_materials(glb, folder):
    texture_dir = folder/"textures"; texture_dir.mkdir(parents=True)
    materials = {}; images = {}; material_ids = {}
    for i, material in enumerate(glb.data.get("materials", []) + [{}]):
        name = f"{key('material-'+str(i)):08x}"
        pbr = material.get("pbrMetallicRoughness", {}); extras = material.get("extras", {})
        texture = ""
        if "baseColorTexture" in pbr:
            info = pbr["baseColorTexture"]
            if info.get("texCoord", 0) != 0: raise ValueError("Stage materials use TEXCOORD_0")
            texture = f"{key('texture-'+str(info['index'])):08x}"
            if texture not in images:
                images[texture] = glb.image(info["index"]); images[texture].save(texture_dir/(texture+".png"))
        mode = material.get("alphaMode", "OPAQUE")
        if mode not in ("OPAQUE", "MASK", "BLEND"): raise ValueError("Unsupported material alpha mode")
        materials[name] = {"texture": texture, "blend": 1 if extras.get("gh3Additive") else 2 if mode == "BLEND" else 0,
                           "cutoff": float(material.get("alphaCutoff", 0.5)) if mode == "MASK" else float(number(extras.get("gh3AlphaCutoff", 0), 0, 1)) if mode == "BLEND" else 0,
                            "crowd": bool(extras.get("gh3CrowdAtlas")), "sortedTransparency": bool(extras.get("gh3SortedTransparency"))}
        shading = extras.get("gh3Shading", "")
        if shading not in ("", *SHADING): raise ValueError("Unknown material shading mode")
        materials[name]["shading"] = shading
        lightmap = ""
        if "occlusionTexture" in material:
            info = material["occlusionTexture"]
            if info.get("texCoord", 0) != 1: raise ValueError("Stage lightmaps use TEXCOORD_1")
            lightmap = f"{key('texture-'+str(info['index'])):08x}"
            if lightmap not in images:
                images[lightmap] = glb.image(info["index"]); images[lightmap].save(texture_dir/(lightmap+".png"))
        materials[name]["lightmap"] = lightmap
        materials[name]["doubleSided"] = bool(material.get("doubleSided", False))
        scale = extras.get("gh3ColorScale", 1)
        if type(scale) not in (int, float) or not math.isfinite(scale) or not 1 <= scale <= 1000: raise ValueError("Invalid material color scale")
        materials[name]["colorScale"] = float(scale)
        source_material = extras.get("gh3SourceMaterial", material.get("name", "material-"+str(i)))
        if not isinstance(source_material, str) or not source_material or len(source_material) > 256: raise ValueError("Invalid material identity")
        materials[name]["source"] = source_material
        movie_slot = extras.get("gh3MovieSlot", -1)
        if type(movie_slot) != int or movie_slot < -1 or movie_slot > 31: raise ValueError("Invalid movie texture slot")
        materials[name]["movieSlot"] = movie_slot
        if "gh3TextureAnimation" in extras:
            animation = extras["gh3TextureAnimation"]
            fields(animation, {"columns", "rows", "fps", "offset"}, {"columns", "rows", "fps", "offset"})
            if any(type(animation[field]) != int or not 1 <= animation[field] <= 256 for field in ("columns", "rows")):
                raise ValueError("Invalid texture atlas dimensions")
            if any(type(animation[field]) not in (int, float) or not math.isfinite(animation[field]) or abs(animation[field]) > 100000 for field in ("fps", "offset")):
                raise ValueError("Invalid texture animation rate or offset")
            materials[name]["animation"] = animation
        for field, extra in (("mask", "gh3MaskTexture"), ("layer", "gh3LayerTexture")):
            if extra not in extras: continue
            index = extras[extra]
            if type(index) != int or not 0 <= index < len(glb.data.get("textures", [])): raise ValueError("Invalid material layer texture")
            image = f"{key('texture-'+str(index)):08x}"
            if image not in images:
                images[image] = glb.image(index); images[image].save(texture_dir/(image+".png"))
            materials[name][field] = image
        if "layer" in materials[name]:
            materials[name]["layerColor"] = [float(number(v, 0, 1000)) for v in vector(extras.get("gh3LayerColor"), 4)]
        if "gh3UvWibble" in extras:
            materials[name]["uvWibble"] = [float(number(v, -1000, 1000)) for v in vector(extras["gh3UvWibble"], 11)]
        if "gh3Shader" in extras:
            materials[name]["gh3"] = gh3_shader_material(glb, extras["gh3Shader"], texture_dir, images)
            # Vertex colours of template materials exclude the base colour; the fallback surface model applies it instead.
            materials[name]["color"] = [float(number(v, 0, 1)) for v in vector(pbr.get("baseColorFactor", [1, 1, 1, 1]), 4)]
        if materials[name]["crowd"]: materials[name].update(blend=3, cutoff=0.3)
        if extras.get("gh3Billboard"): materials[name]["billboard"] = True
        role = extras.get("gh3CrowdRole", "")
        if role and not re.fullmatch(r"crowd[1-8]", role): raise ValueError("Invalid crowd card role")
        if role: materials[name]["crowdRole"] = role
        material_ids[i] = name
    return materials, material_ids, images


def compile_stage(source, destination):
    source = Path(source).resolve(); manifest = load_json(source/"stage-pack.json")
    destination = Path(destination).resolve()
    if source.is_relative_to(destination): raise ValueError("The compiled pack cannot replace its source")
    fields(manifest, {"version", "kind", "id", "name", "library", "model", "band", "waypoints", "cameras", "lighting", "effects", "actors", "crowd", "scripts"},
           {"version", "kind", "id", "name", "library", "model", "band", "cameras"})
    if type(manifest["version"]) != int or manifest["version"] != 1 or manifest["kind"] != "stage" or manifest["library"] != "gh3-band-v1" or not isinstance(manifest["id"],str) or not re.fullmatch(r"[a-z0-9][a-z0-9.-]{0,95}", manifest["id"]):
        raise ValueError("Invalid stage pack identity or version")
    if not isinstance(manifest["name"],str) or not manifest["name"].strip(): raise ValueError("A stage needs a display name")
    if not manifest["band"] or set(manifest["band"]) - ROLES: raise ValueError("A stage needs one to four known performer placements")
    for placement in manifest["band"].values(): transform(placement)
    crowd = manifest.get("crowd", [])
    if not isinstance(crowd, list) or len(crowd) > 8: raise ValueError("Invalid crowd members")
    roles = set()
    for member in crowd:
        fields(member, {"role", "model", "yaw"}, {"role", "model", "yaw"})
        if not isinstance(member["role"], str) or not re.fullmatch(r"crowd[1-8]", member["role"]) or member["role"] in roles:
            raise ValueError("Invalid or repeated crowd role")
        if not isinstance(member["model"], str) or not re.fullmatch(r"[a-zA-Z0-9_]{1,64}", member["model"]):
            raise ValueError("Invalid crowd model")
        number(member["yaw"], -360, 360)
        roles.add(member["role"])
    waypoints = {role+"_start": placement for role, placement in manifest["band"].items()}
    for name, placement in manifest.get("waypoints", {}).items():
        if name in waypoints: raise ValueError("Stage cannot replace a reserved start waypoint")
        waypoints[name] = transform(placement)
    effects = manifest.get("effects", {})
    if set(effects)-EFFECTS: raise ValueError("Unknown stage effect")
    for placements in effects.values():
        if len(placements) > 64: raise ValueError("Too many effect emitters")
        for placement in placements: transform(placement)
    inputs = [source/"stage-pack.json", relative(source, manifest["model"]), relative(source, manifest["cameras"])]
    cameras = compile_cameras(load_json(inputs[2]), manifest["band"], waypoints)
    catalog_path = destination.parent.parent/"libraries"/manifest["library"]/"catalog.json"
    if catalog_path.exists():
        catalog = load_json(catalog_path)["members"]
        for shots in cameras["arrays"].values():
            for shot in shots:
                for point in shot["points"]:
                    for role, bone in ((point["lockTo"],point["lockBone"]), (point["lookAt"],point["lookBone"])):
                        if role not in ("", "world") and bone != "00000000" and bone not in catalog[role]["bones"]:
                            raise ValueError(f"Camera anchor is not in the shared rig: {role}/{bone}")
    if "lighting" in manifest:
        inputs.append(relative(source, manifest["lighting"])); lighting = compile_lighting(load_json(inputs[-1]))
    else: lighting = compile_lighting(basic_lighting())
    glb = GlbReader(inputs[1])
    destination.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".stage-import-", dir=destination.parent) as temporary:
        folder = Path(temporary)
        texture_dir = folder/"textures"
        materials, material_ids, images = import_materials(glb, folder)
        if glb.data.get("animations"): raise ValueError("Put stage animations in an actor GLB")
        default = len(material_ids)-1
        meshes = []; groups = []; visibility = []; hidden = set(); visibility_defaults = {}; fixtures = []; fixture_models = {}; masks = {}
        object_ids = []; objects = {}
        for node_index, node, world in glb.nodes():
            if "skin" in node: raise ValueError("Skinned stage objects require an actor definition")
            extras = node.get("extras", {}); fixture = extras.get("gh3FixtureId")
            object_name = extras.get("gh3Object", "")
            if not isinstance(object_name, str) or len(object_name) > 128: raise ValueError("Invalid scenery object name")
            object_id = object_name[2:].lower() if re.fullmatch(r"0x[0-9a-fA-F]{8}", object_name) else f"{key(object_name):08x}" if object_name else ""
            if object_name:
                pivot = extras.get("gh3ObjectPivot", [0, 0, 0])
                vector(pivot)
                orientation = rotation(extras.get("gh3ObjectRotation", [0, 0, 0, 1]))
                created = extras.get("gh3ObjectCreated", True); script = extras.get("gh3ObjectScript", "")
                if type(created) != bool or not isinstance(script, str) or len(script) > 256: raise ValueError("Invalid scenery object startup")
                placement = {"position": pivot, "rotation": orientation, "created": created, "script": script}
                if objects.setdefault(object_id, placement) != placement: raise ValueError("A scenery object must have one pivot")
            if "mesh" not in node: continue
            visibility_group = extras.get("gh3VisibilityGroup", "")
            initially_visible = extras.get("gh3InitiallyVisible", True)
            if not isinstance(visibility_group, str) or len(visibility_group) > 128 or type(initially_visible) != bool:
                raise ValueError("Invalid scenery visibility group")
            if not initially_visible and not visibility_group: raise ValueError("Hidden scenery needs a visibility group")
            if visibility_group:
                if fixture: raise ValueError("Visibility groups apply to scenery, not fixtures")
                if visibility_defaults.setdefault(visibility_group, initially_visible) != initially_visible:
                    raise ValueError("A visibility group must have one initial state")
                if not initially_visible: hidden.add(visibility_group)
            geometry_transform = world
            fixture_rotation = None
            if fixture:
                scale = np.linalg.norm(world[:3,:3], axis=0)
                orientation = world[:3,:3] / scale
                if np.linalg.det(orientation) < 0: scale[2] *= -1; orientation[:,2] *= -1
                if not np.allclose(orientation.T @ orientation, np.eye(3), atol=1e-5): raise ValueError("Apply fixture shear before export")
                geometry_transform = np.diag([*scale,1])
                fixture_rotation = matrix_quaternion(orientation)
            local_meshes = []
            for primitive in glb.data["meshes"][node["mesh"]]["primitives"]:
                material = material_ids[primitive.get("material", default)]
                packed, faces = glb.primitive(primitive, geometry_transform)
                for part in split_mesh(material, packed, faces):
                    mat = materials[material]
                    # The stage surface shader discards below the cutoff; only the unshaded particle-shader fallback needs trimmed geometry.
                    if mat["cutoff"] and mat["texture"] and not mat["crowd"] and not mat["shading"] and not mat.get("gh3"):
                        pair = mat["texture"], mat["cutoff"]
                        if pair not in masks: masks[pair] = alpha_region(images[pair[0]], pair[1])
                        part = trim_mesh(part, images[pair[0]], pair[1], masks[pair])
                    if part[3]: local_meshes.extend(split_mesh(part[0], np.frombuffer(part[2],dtype=STATIC_VERTEX), np.asarray(part[3])))
            if fixture:
                id = f"{key(fixture):08x}"
                if id not in next(iter(lighting["snapshots"].values()))["beams"]: raise ValueError(f"Fixture has no lighting beam: {fixture}")
                if any(f["id"] == id for f in fixtures): raise ValueError(f"Duplicate light fixture: {fixture}")
                model = f"{key('mesh-'+str(node['mesh'])+'-'+str(scale.tolist())):08x}"
                fixture_models.setdefault(model, local_meshes)
                fixtures.append({"id": id, "model": model, "position": world[:3, 3].tolist(), "rotation": fixture_rotation,
                                 "fixedPosition": bool(extras.get("gh3FixtureFixedPosition")), "object": extras.get("gh3FixtureObject", fixture)})
                if extras.get("gh3FixtureCreated") is False: fixtures[-1]["created"] = False
                if not isinstance(fixtures[-1]["object"], str) or not 1 <= len(fixtures[-1]["object"]) <= 256: raise ValueError("Invalid fixture object name")
            else:
                meshes.extend(local_meshes)
                groups.extend([f"{key(extras.get('gh3LightGroup', 'Stage')):08x}"]*len(local_meshes))
                visibility.extend([visibility_group]*len(local_meshes))
                object_ids.extend([object_id]*len(local_meshes))
        if not meshes: raise ValueError("Stage has no visible scenery")
        definition = write_meshes(folder, meshes, materials, groups)
        definition["name"] = manifest["name"]
        definition["visibilityGroups"] = visibility
        definition["hiddenGroups"] = sorted(hidden.intersection(visibility))
        definition["objectIds"] = object_ids
        definition["objects"] = objects
        start = manifest["band"].get("guitarist", next(iter(manifest["band"].values())))["position"]
        definition["cameraPosition"] = [start[0], start[1]+3, start[2]-10]
        definition["cameraTarget"] = [start[0], start[1]+1, start[2]]
        write_json(folder/"stage.json", definition)
        if "scripts" in manifest:
            path = relative(source, manifest["scripts"]); inputs.append(path)
            scripts = load_json(path)
            fields(scripts, {"version", "definitions", "startup", "nodes", "textures", "performanceChanged", "prefixes", "lightVolumes", "movies"}, {"version", "definitions"})
            if scripts["version"] != 1 or not isinstance(scripts["definitions"], dict) or len(scripts["definitions"]) > 50000:
                raise ValueError("Invalid stage script definitions")
            if any(not re.fullmatch(r"[0-9a-f]{8}", name) or not isinstance(value, list) or len(value) != 2 for name, value in scripts["definitions"].items()):
                raise ValueError("Invalid typed script value")
            startup = scripts.get("startup", [])
            if not isinstance(startup, list) or len(startup) > 256 or any(not isinstance(name, str) or len(name) > 256 for name in startup):
                raise ValueError("Invalid stage startup scripts")
            write_json(folder/"scripts.json", scripts)
            nodes = scripts.get("nodes", [])
            performance = scripts.get("performanceChanged", "")
            if not isinstance(performance, str) or len(performance) > 256: raise ValueError("Invalid stage performance script")
            if not isinstance(nodes, list) or len(nodes) > 256 or any(not isinstance(name, str) or not name or len(name) > 256 for name in nodes):
                raise ValueError("Invalid stage node arrays")
            prefixes = scripts.get("prefixes", {})
            volumes = scripts.get("lightVolumes", {})
            if not isinstance(volumes, dict) or len(volumes) > 1024 or any(not isinstance(name, str) or not isinstance(beam, str) or not beam or len(beam) > 256 for name, beam in volumes.items()):
                raise ValueError("Invalid script light volumes")
            if not isinstance(prefixes, dict) or len(prefixes) > 100000 or any(not isinstance(name, str) or not isinstance(members, list) or
                any(not isinstance(member, str) or not member or len(member) > 256 for member in members) for name, members in prefixes.items()):
                raise ValueError("Invalid stage node prefixes")
            textures = scripts.get("textures", {})
            if not isinstance(textures, dict) or len(textures) > 2048: raise ValueError("Invalid script texture library")
            for name, file in textures.items():
                if not isinstance(name, str) or not name or len(name) > 256: raise ValueError("Invalid script texture name")
                image = relative(source, file); inputs.append(image)
                destination_image = folder/image.relative_to(source)
                if destination_image.exists(): raise ValueError("Script texture replaces another pack file")
                destination_image.parent.mkdir(parents=True, exist_ok=True)
                Image.open(image).verify()
                shutil.copy2(image, destination_image)
            movies = scripts.get("movies", {})
            if not isinstance(movies, dict) or len(movies) > 256: raise ValueError("Invalid stage movie library")
            for name, file in movies.items():
                if not isinstance(name, str) or not name or len(name) > 256: raise ValueError("Invalid stage movie name")
                movie = relative(source, file); inputs.append(movie)
                destination_movie = folder/movie.relative_to(source)
                if destination_movie.exists(): raise ValueError("Stage movie replaces another pack file")
                with movie.open("rb") as stream:
                    if stream.read(8) != b"GH3VID01": raise ValueError("Invalid movie frame bank")
                destination_movie.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(movie, destination_movie)
        for name, parts in fixture_models.items():
            model_dir = folder/"fixtures"/name
            used = {p[0]: materials[p[0]] for p in parts}
            write_meshes(model_dir, parts, used)
            (model_dir/"textures").mkdir()
            for texture in {m["texture"] for m in used.values()}-{""}:
                shutil.copyfile(texture_dir/(texture+".png"), model_dir/"textures"/(texture+".png"))
        if fixtures: write_json(folder/"fixtures"/"fixtures.json", {"version": 1, "housings": fixtures})
        actors = []
        for name, actor in manifest.get("actors", {}).items():
            from stage_actor import compile_actor
            fields(actor, {"model", "transform", "clip", "lightGroup", "playback", "role", "initiallyVisible", "actions", "object", "script", "created", "scale", "animationEvents"}, {"model", "transform"})
            transform(actor["transform"])
            model = relative(source, actor["model"]); inputs.append(model)
            id = f"{key(name):08x}"
            clips = compile_actor(model, folder/"actors"/id, import_materials, write_meshes, split_mesh)
            clip = actor.get("clip", clips[0])
            if clip not in clips: raise ValueError(f"Actor {name} has no clip {clip}")
            playback = actor.get("playback", "loop")
            if playback not in ("loop", "hold"): raise ValueError("Invalid actor playback mode")
            role = actor.get("role", "")
            visible = actor.get("initiallyVisible", True)
            actions = actor.get("actions", {})
            source_object = actor.get("object", ""); script = actor.get("script", ""); created = actor.get("created", True)
            if not all(isinstance(value, str) and len(value) <= 256 for value in (source_object, script)) or type(created) != bool: raise ValueError("Invalid actor script binding")
            if script and not source_object: raise ValueError("A scripted actor needs an object name")
            animation_events = actor.get("animationEvents", "")
            if not isinstance(animation_events, str) or len(animation_events) > 256: raise ValueError("Invalid animation event table")
            scale = vector(actor.get("scale", [1, 1, 1]))
            if any(value <= 0 for value in scale): raise ValueError("Actor scale must be positive")
            if not isinstance(role, str) or len(role) > 128 or type(visible) != bool: raise ValueError("Invalid actor role or visibility")
            if not isinstance(actions, dict) or len(actions) > 256: raise ValueError("Invalid actor actions")
            for action, choices in actions.items():
                if not isinstance(action, str) or not action or not isinstance(choices, list) or not 1 <= len(choices) <= 256 or any(c not in clips for c in choices):
                    raise ValueError(f"Invalid actor action: {action}")
            actors.append({"id": id, "clip": clips.index(clip), "clips": {c: i for i, c in enumerate(clips)},
                           "actions": {a: [clips.index(c) for c in choices] for a, choices in actions.items()},
                           "role": role, "initiallyVisible": visible, "playback": playback, "transform": actor["transform"],
                           "object": source_object, "script": script, "created": created, "scale": scale, "animationEvents": animation_events,
                           "lightGroup": f"{key(actor.get('lightGroup', 'Stage')):08x}"})
        if actors: write_json(folder/"actors.json", {"version": 1, "actors": actors})
        write_json(folder/"cameras.json", cameras); write_json(folder/"lighting.json", lighting)
        pack = {"version": 1, "id": manifest["id"], "name": manifest["name"], "library": manifest["library"],
                "band": manifest["band"], "waypoints": waypoints, "effects": effects, "crowd": crowd,
                "sources": {str(p.relative_to(source)): sha256(p.read_bytes()).hexdigest() for p in inputs}}
        pack["files"] = {str(p.relative_to(folder)): sha256(p.read_bytes()).hexdigest() for p in folder.rglob("*") if p.is_file()}
        write_json(folder/"pack.json", pack)
        if destination.exists():
            if not (destination/"pack.json").is_file(): raise ValueError("Destination is not a compiled stage pack")
            backup = Path(tempfile.mkdtemp(prefix=".stage-previous-", dir=destination.parent))
            backup.rmdir()
            destination.rename(backup)
            try:
                folder.rename(destination)
            except OSError:
                backup.rename(destination)
                raise
            shutil.rmtree(backup)
        else:
            folder.rename(destination)
    return {"id": pack["id"], "name": pack["name"], "meshes": len(meshes), "vertices": sum(m[1] for m in meshes), "fixtures": len(fixtures),
            "materials": len(materials), "cameras": sum(len(v) for v in cameras["arrays"].values())}
