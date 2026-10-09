"""Build the package format v1 JSON files and GLB extras from plain scene descriptions.

This module does not import bpy, so the tests run it without Blender. collect.py makes the descriptions from the
Blender scene. Blender world coordinates: Z up, right-handed. Package JSON: Unity runtime coordinates (Y up,
left-handed, docs/PACKAGE-FORMAT.md section 5). A Blender vector (x, y, z) is (x, z, y) at runtime.
"""

import math

import numpy as np

FORMAT = "chstage/1.0"
LIBRARY_ID = "gh3-band-v1"
ROLES = ("guitarist", "bassist", "singer", "drummer")
EFFECTS = ("pyro", "sparks", "fireworks", "smoke")
ANCHOR_PARTS = ("root", "head", "hand_left", "hand_right", "bone")
SHOT_TYPES = ("stage", "longshot", "mid", "closeup", "normal", "demonik")
SHOT_CONTROLS = ("TwoCam", "HandCam", "Orbit", "OrbitAndMorphTwoCam", "OrbitContinuous", "Keyframes", "Pan", "ThreePos",
                 "FourPos", "SpecialStepMove")
LIGHT_TYPES = ("Ambient", "Dir", "Point", "Spot")
MOODS = ("intro", "verse", "chorus", "solo", "flare", "blackout", "strobe")
BLENDER_LIGHT_TYPES = {"POINT": "Point", "SUN": "Dir", "SPOT": "Spot", "AREA": "Point"}
FILES = {"model": "stage.glb", "placements": "placements.json", "cameras": "cameras.json", "lighting": "lighting.json"}

SWAP = np.array([[1., 0, 0], [0, 0, 1], [0, 1, 0]])
# Blender cameras, lights, and beams look along local -Z; runtime cameras and lights look along local +Z.
LOOK = np.diag([1., 1, -1])


class Issues:
    def __init__(self):
        self.items = []

    def error(self, message, obj=None, material=None): self.items.append(("ERROR", message, obj, material))

    def warn(self, message, obj=None, material=None): self.items.append(("WARNING", message, obj, material))


def clean(value, digits=6):
    """Round floats for readable JSON; keep the sign of zero out of the file."""
    if isinstance(value, (list, tuple, np.ndarray)): return [clean(v, digits) for v in value]
    if isinstance(value, (float, np.floating)):
        value = round(float(value), digits)
        return 0.0 if value == 0 else value
    return value


def rotation_part(matrix):
    """Rotation of a 4x4 or 3x3 matrix without scale (polar decomposition)."""
    m = np.asarray(matrix, dtype=float)[:3, :3]
    u, _, vt = np.linalg.svd(m)
    r = u @ vt
    if np.linalg.det(r) < 0: r = u @ np.diag([1, 1, -1]) @ vt
    return r


def quaternion(r):
    """[x, y, z, w] of a rotation matrix, with w >= 0."""
    r = np.asarray(r, dtype=float)
    trace = r[0, 0] + r[1, 1] + r[2, 2]
    if trace > 0:
        s = math.sqrt(trace + 1) * 2
        q = [(r[2, 1] - r[1, 2]) / s, (r[0, 2] - r[2, 0]) / s, (r[1, 0] - r[0, 1]) / s, s / 4]
    elif r[0, 0] > r[1, 1] and r[0, 0] > r[2, 2]:
        s = math.sqrt(1 + r[0, 0] - r[1, 1] - r[2, 2]) * 2
        q = [s / 4, (r[0, 1] + r[1, 0]) / s, (r[0, 2] + r[2, 0]) / s, (r[2, 1] - r[1, 2]) / s]
    elif r[1, 1] > r[2, 2]:
        s = math.sqrt(1 + r[1, 1] - r[0, 0] - r[2, 2]) * 2
        q = [(r[0, 1] + r[1, 0]) / s, s / 4, (r[1, 2] + r[2, 1]) / s, (r[0, 2] - r[2, 0]) / s]
    else:
        s = math.sqrt(1 + r[2, 2] - r[0, 0] - r[1, 1]) * 2
        q = [(r[0, 2] + r[2, 0]) / s, (r[1, 2] + r[2, 1]) / s, s / 4, (r[1, 0] - r[0, 1]) / s]
    q = np.asarray(q) / np.linalg.norm(q)
    return q if q[3] >= 0 else -q


def quaternion_matrix(q):
    """Rotation matrix of [x, y, z, w]."""
    x, y, z, w = np.asarray(q, dtype=float) / np.linalg.norm(q)
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                     [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                     [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])


def runtime_position(vector):
    return SWAP @ np.asarray(vector, dtype=float)[:3]


def runtime_frame(matrix, looks=False):
    """Runtime position and rotation matrix of a Blender world matrix. looks: the object looks along local -Z."""
    m = np.asarray(matrix, dtype=float)
    rotation = SWAP @ rotation_part(m) @ (LOOK if looks else SWAP)
    return SWAP @ m[:3, 3], rotation


def transform(matrix, looks=False):
    position, rotation = runtime_frame(matrix, looks)
    return {"position": clean(position), "rotation": clean(quaternion(rotation))}


def blender_matrix(position, rotation, looks=False):
    """Inverse of runtime_frame: a Blender world matrix from a runtime position and [x, y, z, w] rotation."""
    m = np.eye(4)
    m[:3, :3] = SWAP @ quaternion_matrix(rotation) @ (LOOK if looks else SWAP)
    m[:3, 3] = SWAP @ np.asarray(position, dtype=float)
    return m


def build_manifest(package, has_lighting, preview=False):
    manifest = {"format": FORMAT, "id": package["id"], "version": package["version"], "name": package["name"]}
    if package.get("description"): manifest["description"] = package["description"]
    manifest["authors"] = [{k: v for k, v in author.items() if v} for author in package["authors"]]
    manifest["license"] = package["license"]
    manifest["permissions"] = {key: bool(package["permissions"][key]) for key in ("redistribute", "modify", "commercial")}
    if package.get("credits"): manifest["credits"] = list(package["credits"])
    if package.get("homepage"): manifest["homepage"] = package["homepage"]
    if package.get("requires"): manifest["requires"] = [dict(r) for r in package["requires"]]
    manifest["entry"] = {key: path for key, path in FILES.items() if key != "lighting" or has_lighting}
    # A development folder can have empty 'files'; the export step writes the hashes.
    manifest["files"] = {}
    if package.get("minModVersion"): manifest["minModVersion"] = package["minModVersion"]
    if preview: manifest["preview"] = "preview.png"
    return manifest


def build_placements(markers, crowd, issues):
    placements = {"version": 1, "band": {}}
    waypoints = {}; effects = {}
    for marker in markers:
        frame = transform(marker["matrix"])
        if marker["kind"] == "BAND":
            if marker["role"] in placements["band"]: issues.error(f"two band placements for the role {marker['role']}", marker["object"])
            placements["band"][marker["role"]] = frame
        elif marker["kind"] == "WAYPOINT":
            if marker["name"] in waypoints: issues.error(f"two waypoints have the name '{marker['name']}'", marker["object"])
            waypoints[marker["name"]] = frame
        elif marker["kind"] == "EFFECT":
            effects.setdefault(marker["effect"], []).append(frame)
    if not placements["band"]: issues.error("the stage needs at least one band placement (Add > Band placement)")
    if waypoints: placements["waypoints"] = waypoints
    if effects: placements["effects"] = {kind: effects[kind] for kind in EFFECTS if kind in effects}
    if crowd: placements["crowd"] = [{"role": c["role"], "model": c["model"], "yaw": clean(c["yaw"])} for c in crowd]
    return placements


def anchor_name(role, part, bone):
    if role in ("world", "none"): return role
    return f"{role}.bone.{bone}" if part == "bone" else f"{role}.{part}"


def build_cameras(shots, notes, markers, issues):
    band = {m["role"]: runtime_frame(m["matrix"]) for m in markers if m["kind"] == "BAND"}
    result = {"version": 1, "shots": {}, "groups": {}}
    for shot in shots:
        name = shot["name"]
        if name in result["shots"]: issues.error(f"two camera shots have the name '{name}'", shot["object"]); continue
        points = []
        for point in shot["points"]:
            position, rotation = runtime_frame(point["matrix"], looks=True)
            anchor = point["anchor"]
            if anchor != "world":
                role, _, part = anchor.partition(".")
                if role not in band:
                    issues.error(f"shot '{name}': anchor {anchor} needs a band placement for {role}", point["object"])
                else:
                    origin, frame = band[role]
                    position = frame.T @ (position - origin); rotation = frame.T @ rotation
                    if part != "root":
                        issues.warn(f"shot '{name}': the game places the camera relative to the {part} bone; "
                                    "Blender shows it relative to the performer root", point["object"])
            row = {"anchor": anchor, "position": clean(position), "rotation": clean(quaternion(rotation)),
                   "horizontalFov": clean(point["fov"], 4)}
            if point.get("lookAt"): row["lookAt"] = point["lookAt"]
            if point.get("screenOffset") is not None: row["screenOffset"] = clean(point["screenOffset"])
            points.append(row)
        definition = {"type": shot["type"], "control": shot["control"], "points": points}
        for key in ("tag", "orbitAngle", "angularSpeed", "interpolation"):
            if shot.get(key) not in (None, ""): definition[key] = clean(shot[key])
        for key in ("handCam", "disableOrbit1", "disableOrbit2"):
            if shot.get(key): definition[key] = True
        if shot.get("waypoints"): definition["waypoints"] = list(shot["waypoints"])
        if shot.get("times"): definition["times"] = clean(shot["times"])
        result["shots"][name] = definition
        if not shot["groups"]: issues.warn(f"shot '{name}' is in no camera group", shot["object"])
        for group in shot["groups"]:
            result["groups"].setdefault(group, []).append(name)
    if "wide" not in result["groups"]: issues.error("no camera shot is in the group 'wide' (required)")
    if notes:
        result["notes"] = {}
        for note in notes:
            selector = {key: note[key] for key in ("group", "tag", "type") if note.get(key)}
            if note.get("enable") is not None: selector["enable"] = note["enable"]
            result["notes"][str(note["note"])] = selector
    return result


def build_lighting(lights, snapshots, moods, colors, issues):
    if not lights and not snapshots: return None
    result = {"version": 1, "lights": [], "snapshots": {}, "moods": {}, "colors": {}}
    for light in lights:
        position, rotation = runtime_frame(light["matrix"], looks=True)
        row = {"id": light["id"]}
        if light.get("object"): row["object"] = light["object"]
        row.update(group=light["group"] or "Stage", type=light["type"], position=clean(position), rotation=clean(quaternion(rotation)))
        if light.get("overrideColor"): row["overrideColor"] = True
        if light.get("hotspot") is not None: row["hotspot"] = clean(light["hotspot"], 4); row["field"] = clean(light["field"], 4)
        result["lights"].append(row)
    if not lights: issues.error("lighting needs at least one stage light (Light properties > Stage light)")
    for snapshot in snapshots:
        settings = {entry["light"]: entry for entry in snapshot["lights"] if entry.get("light")}
        rows = []
        for light in lights:
            entry = settings.get(light["source"])
            if entry is None:
                issues.error(f"snapshot '{snapshot['name']}' has no settings for the light '{light['id']}'; use Capture", light["source"])
                entry = {"color": [0, 0, 0], "intensity": 0, "near": 0, "far": 0}
            row = {"color": clean(entry["color"]), "intensity": clean(entry["intensity"]), "near": clean(entry["near"]), "far": clean(entry["far"])}
            if entry.get("inherit"): row["inherit"] = True
            rows.append(row)
        beams = {}
        for beam in snapshot["beams"]:
            position = runtime_position(beam["position"])
            w, x, y, z = beam["rotation"]
            rotation = SWAP @ quaternion_matrix([x, y, z, w]) @ LOOK
            row = {"position": clean(position), "rotation": clean(quaternion(rotation)), "color": clean(beam["color"]),
                   "density": clean(beam["density"]), "startRadius": clean(beam["startRadius"]), "endRadius": clean(beam["endRadius"]),
                   "range": clean(beam["range"])}
            if beam.get("inherit"): row["inherit"] = True
            beams[beam["id"]] = row
        if snapshot["name"] in result["snapshots"]: issues.error(f"two snapshots have the name '{snapshot['name']}'")
        result["snapshots"][snapshot["name"]] = {"lights": rows, "beams": beams}
    for mood in moods:
        if mood["mood"] in result["moods"]: issues.error(f"the mood '{mood['mood']}' occurs two times")
        result["moods"][mood["mood"]] = list(mood["snapshots"])
    for name in ("intro", "blackout"):
        if name not in result["moods"]: issues.error(f"lighting needs the mood '{name}'")
    for color in colors: result["colors"][color["name"]] = clean(color["color"])
    return result


def node_extras(node, matrix=None):
    """gh3* node extras (PACKAGE-FORMAT 6.4) of one object description."""
    extras = {}
    if node.get("lightGroup"): extras["gh3LightGroup"] = node["lightGroup"]
    if node.get("visibilityGroup"):
        extras["gh3VisibilityGroup"] = node["visibilityGroup"]
        extras["gh3InitiallyVisible"] = bool(node.get("initiallyVisible", True))
    if node.get("fixtureId"):
        extras["gh3FixtureId"] = node["fixtureId"]
        if node.get("fixtureFixedPosition"): extras["gh3FixtureFixedPosition"] = True
        if node.get("fixtureObject"): extras["gh3FixtureObject"] = node["fixtureObject"]
        if node.get("fixtureCreated") is not None: extras["gh3FixtureCreated"] = bool(node["fixtureCreated"])
    if node.get("object"):
        extras["gh3Object"] = node["object"]
        if node.get("pivot") and matrix is not None:
            frame = transform(matrix)
            extras["gh3ObjectPivot"] = frame["position"]; extras["gh3ObjectRotation"] = frame["rotation"]
        if node.get("created") is not None: extras["gh3ObjectCreated"] = bool(node["created"])
        if node.get("script"): extras["gh3ObjectScript"] = node["script"]
    return extras


def material_fields(material):
    """(gh3* material extras, standard glTF material fields) of one material description (PACKAGE-FORMAT 6.5)."""
    extras = {"gh3Shading": material["shading"]}
    gltf = {"alphaMode": "OPAQUE", "doubleSided": bool(material.get("twoSided"))}
    blend = material["blend"]
    if blend == "CUTOUT":
        gltf.update(alphaMode="MASK", alphaCutoff=clean(material["alphaCutoff"], 4))
    elif blend in ("BLEND", "ADDITIVE"):
        gltf["alphaMode"] = "BLEND"
        if blend == "ADDITIVE": extras["gh3Additive"] = True
        if material.get("blendCutoff"): extras["gh3AlphaCutoff"] = clean(material["alphaCutoff"], 4)
    if material.get("colorScale", 1) != 1: extras["gh3ColorScale"] = clean(material["colorScale"], 4)
    if material.get("crowdAtlas"): extras["gh3CrowdAtlas"] = True
    if material.get("crowdRole"): extras["gh3CrowdRole"] = material["crowdRole"]
    if material.get("billboard"): extras["gh3Billboard"] = True
    if material.get("movieSlot", -1) >= 0: extras["gh3MovieSlot"] = int(material["movieSlot"])
    if material.get("sortedTransparency"): extras["gh3SortedTransparency"] = True
    if material.get("flipbook"): extras["gh3TextureAnimation"] = {k: clean(v, 4) for k, v in material["flipbook"].items()}
    if material.get("lightmap"): gltf["occlusionTexture"] = {"image": material["lightmap"], "texCoord": 1}
    return extras, gltf
