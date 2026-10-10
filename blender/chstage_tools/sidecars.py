"""Build the package format v1 JSON files and GLB extras from plain scene descriptions, and read them back.

This module does not import bpy, so the tests run it without Blender. collect.py makes the descriptions from the
Blender scene; package_open.py makes Blender data from the descriptions that the open_* functions return. Blender
world coordinates: Z up, right-handed. Package JSON: Unity runtime coordinates (Y up, left-handed,
docs/PACKAGE-FORMAT.md section 5). A Blender vector (x, y, z) is (x, z, y) at runtime.
"""

import json
import math
import re
import zlib

import numpy as np

FORMAT = "chstage/1.0"
LIBRARY_ID = "gh3-band-v1"
ROLES = ("guitarist", "bassist", "singer", "drummer")
EFFECTS = ("pyro", "sparks", "fireworks", "smoke")
ANCHOR_PARTS = ("root", "head", "hand_left", "hand_right", "bone")
SHOT_TYPES = ("stage", "longshot", "mid", "closeup", "normal", "demonik")
SHOT_CONTROLS = (
    "TwoCam",
    "HandCam",
    "Orbit",
    "OrbitAndMorphTwoCam",
    "OrbitContinuous",
    "Keyframes",
    "Pan",
    "ThreePos",
    "FourPos",
    "SpecialStepMove",
)
LIGHT_TYPES = ("Ambient", "Dir", "Point", "Spot")
MOODS = ("intro", "verse", "chorus", "solo", "flare", "blackout", "strobe")
BLENDER_LIGHT_TYPES = {"POINT": "Point", "SUN": "Dir", "SPOT": "Spot", "AREA": "Point"}
RUNTIME_LIGHT_TYPES = {"Ambient": "POINT", "Dir": "SUN", "Point": "POINT", "Spot": "SPOT"}
ENTRY_ORDER = ("model", "placements", "cameras", "lighting", "scripts", "actors")
FILES = {"model": "stage.glb", "placements": "placements.json", "cameras": "cameras.json", "lighting": "lighting.json"}
# Anchor parts and their GH3 bones (pipeline/stagepack/pack.py anchor()).
PART_BONES = {"head": "bone_head", "hand_left": "Bone_Palm_L", "hand_right": "Bone_Palm_R"}
PLAYBACK = ("loop", "hold")
OPTIONAL_PLACEMENTS = ("waypoints", "effects", "crowd")
# Material extras that the material panel edits. The add-on keeps the other gh3* keys (for example the GH3 shader
# record) without a change.
PANEL_MATERIAL_EXTRAS = {
    "gh3Shading",
    "gh3Additive",
    "gh3AlphaCutoff",
    "gh3ColorScale",
    "gh3CrowdAtlas",
    "gh3CrowdRole",
    "gh3Billboard",
    "gh3MovieSlot",
    "gh3SortedTransparency",
    "gh3TextureAnimation",
    "gh3Toon",
    "gh3HairLayer",
}
# gh3Toon texture references and their Blender material settings.
TOON_TEXTURES = {
    "shadeTexture": "toon_shade_image",
    "shadeMaskTexture": "toon_mask_image",
    "sphereTexture": "toon_sphere_image",
}
# A camera point without a rotation looks at its target. The preview aims at this height above the performer root.
LOOK_HEIGHT = 1.5

SWAP = np.array([[1.0, 0, 0], [0, 0, 1], [0, 1, 0]])
# Blender cameras, lights, and beams look along local -Z; runtime cameras and lights look along local +Z.
LOOK = np.diag([1.0, 1, -1])


class Issues:
    def __init__(self):
        self.items = []

    def error(self, message, obj=None, material=None):
        self.items.append(("ERROR", message, obj, material))

    def warn(self, message, obj=None, material=None):
        self.items.append(("WARNING", message, obj, material))


def plain(value):
    """The value as JSON data (lists, Python numbers), so that two values compare as their JSON files do."""
    return json.loads(json.dumps(value))


class Keeper:
    """Write the source value of a row again when the Blender data gives the same row as when the package was opened.

    "Open .chstage" stores the source row and the row that the Blender data gave just after the open on the owner of
    the row (the 'kept' JSON text of an object, a material, or a list item). Export compares the row of the current
    Blender data with the stored row. Equal: nothing changed in Blender, so the source row goes into the package
    without the float32 and rounding changes of the Blender data. Different: the current row goes into the package.

    mode "keep" (export), "compute" (ignore the kept rows), or "record" (collect the rows for open).
    """

    def __init__(self, mode="keep"):
        self.mode = mode
        self.recorded = []

    def __call__(self, computed, owner, kind):
        computed = plain(computed)
        if owner is None or self.mode == "compute":
            return computed
        if self.mode == "record":
            self.recorded.append((owner, kind, computed))
            return computed
        entry = load_kept(owner).get(kind)
        if entry and "source" in entry and entry.get("computed") == computed:
            return entry["source"]
        return computed

    def store(self):
        """Record mode: add the rows to the kept data of their owners."""
        for owner, kind, computed in self.recorded:
            data = load_kept(owner)
            if kind in data:
                data[kind]["computed"] = computed
            owner.kept = json.dumps(data, separators=(",", ":"))
        self.recorded.clear()


def load_kept(owner):
    return json.loads(owner.kept) if getattr(owner, "kept", "") else {}


def kept_text(**rows):
    """The kept JSON text of an owner for its source rows (one for each kind)."""
    return json.dumps({kind: {"source": plain(row)} for kind, row in rows.items()}, separators=(",", ":"))


def clean(value, digits=6):
    """Round floats for readable JSON; keep the sign of zero out of the file."""
    if isinstance(value, (list, tuple, np.ndarray)):
        return [clean(v, digits) for v in value]
    if isinstance(value, (float, np.floating)):
        value = round(float(value), digits)
        return 0.0 if value == 0 else value
    if isinstance(value, np.integer):
        return int(value)
    return value


def rotation_part(matrix):
    """Rotation of a 4x4 or 3x3 matrix without scale (polar decomposition)."""
    m = np.asarray(matrix, dtype=float)[:3, :3]
    u, _, vt = np.linalg.svd(m)
    r = u @ vt
    if np.linalg.det(r) < 0:
        r = u @ np.diag([1, 1, -1]) @ vt
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
    return np.array(
        [
            [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
            [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
            [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
        ]
    )


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


def frame_matrix(position, rotation, looks=False):
    """Inverse of runtime_frame: a Blender world matrix from a runtime position and rotation matrix."""
    m = np.eye(4)
    m[:3, :3] = SWAP @ np.asarray(rotation, dtype=float) @ (LOOK if looks else SWAP)
    m[:3, 3] = SWAP @ np.asarray(position, dtype=float)
    return m


def blender_matrix(position, rotation, looks=False):
    """A Blender world matrix from a runtime position and [x, y, z, w] rotation."""
    return frame_matrix(position, quaternion_matrix(rotation), looks)


def blender_quaternion(rotation, looks=False):
    """Blender (w, x, y, z) of a runtime [x, y, z, w] rotation."""
    x, y, z, w = quaternion(frame_matrix([0, 0, 0], quaternion_matrix(rotation), looks)[:3, :3])
    return [w, x, y, z]


def look_rotation(position, target):
    """Runtime rotation matrix whose +Z points from position to target, with +Y up as far as possible."""
    forward = np.asarray(target, dtype=float) - np.asarray(position, dtype=float)
    if np.linalg.norm(forward) < 1e-9:
        return np.eye(3)
    forward /= np.linalg.norm(forward)
    up = np.array([0.0, 1, 0]) if abs(forward[1]) < 0.999 else np.array([0.0, 0, 1])
    right = np.cross(up, forward)
    right /= np.linalg.norm(right)
    return np.column_stack([right, np.cross(forward, right), forward])


# Manifest.
def stage_entry(has_lighting, extra=()):
    """The 'entry' object of a venue: the add-on files and the kept entries (for example 'scripts' and 'actors')."""
    entry = {key: path for key, path in FILES.items() if key != "lighting" or has_lighting}
    entry.update(extra)
    return {
        key: entry[key]
        for key in sorted(entry, key=lambda k: ENTRY_ORDER.index(k) if k in ENTRY_ORDER else len(ENTRY_ORDER))
    }


def build_manifest(package, entry, preview=None, format=FORMAT):
    manifest = {"format": format, "id": package["id"], "version": package["version"], "name": package["name"]}
    if package.get("description"):
        manifest["description"] = package["description"]
    manifest["authors"] = [{k: v for k, v in author.items() if v} for author in package["authors"]]
    manifest["license"] = package["license"]
    manifest["permissions"] = {
        key: bool(package["permissions"][key]) for key in ("redistribute", "modify", "commercial")
    }
    if package.get("credits"):
        manifest["credits"] = list(package["credits"])
    if package.get("homepage"):
        manifest["homepage"] = package["homepage"]
    if package.get("requires"):
        manifest["requires"] = [dict(r) for r in package["requires"]]
    manifest["entry"] = dict(entry)
    # A development folder can have empty 'files'; the export step writes the hashes.
    manifest["files"] = {}
    if package.get("minModVersion"):
        manifest["minModVersion"] = package["minModVersion"]
    if preview:
        manifest["preview"] = preview
    return manifest


def open_manifest(manifest):
    """Package description (the inverse of build_manifest) of a manifest."""
    return {
        "id": manifest.get("id", ""),
        "version": manifest.get("version", ""),
        "name": manifest.get("name", ""),
        "description": manifest.get("description", ""),
        "authors": [
            {"name": a.get("name", ""), "role": a.get("role", ""), "url": a.get("url", "")}
            for a in manifest.get("authors", [])
        ],
        "license": manifest.get("license", ""),
        "permissions": dict(manifest.get("permissions", {})),
        "credits": list(manifest.get("credits", [])),
        "homepage": manifest.get("homepage", ""),
        "requires": [dict(r) for r in manifest.get("requires", [])],
        "minModVersion": manifest.get("minModVersion", ""),
    }


# Placements.
def build_placements(markers, crowd, issues, keeper=None, keys=()):
    """keys: optional top-level keys that the file has also when they are empty (as in the opened package)."""
    keeper = keeper or Keeper("compute")
    placements = {"version": 1, "band": {}}
    waypoints = {}
    effects = {}
    for marker in markers:
        frame = keeper(transform(marker["matrix"]), marker.get("owner"), "placement")
        if marker["kind"] == "BAND":
            if marker["role"] in placements["band"]:
                issues.error(f"two band placements for the role {marker['role']}", marker["object"])
            placements["band"][marker["role"]] = frame
        elif marker["kind"] == "WAYPOINT":
            if marker["name"] in waypoints:
                issues.error(f"two waypoints have the name '{marker['name']}'", marker["object"])
            waypoints[marker["name"]] = frame
        elif marker["kind"] == "EFFECT":
            effects.setdefault(marker["effect"], []).append(frame)
    if not placements["band"]:
        issues.error("the stage needs at least one band placement (Add > Band placement)")
    if waypoints or "waypoints" in keys:
        placements["waypoints"] = waypoints
    if effects or "effects" in keys:
        placements["effects"] = {kind: effects[kind] for kind in EFFECTS if kind in effects}
    if crowd or "crowd" in keys:
        placements["crowd"] = [
            keeper({"role": c["role"], "model": c["model"], "yaw": clean(c["yaw"])}, c.get("owner"), "crowd")
            for c in crowd
        ]
    return placements


def open_placements(placements):
    """(markers, crowd, keys): Blender marker descriptions with their source rows, crowd rows, and the present optional keys."""
    markers = []
    for order, (role, frame) in enumerate(placements.get("band", {}).items()):
        markers.append(
            {
                "kind": "BAND",
                "role": role,
                "order": order,
                "matrix": blender_matrix(frame["position"], frame["rotation"]),
                "source": frame,
            }
        )
    for order, (name, frame) in enumerate(placements.get("waypoints", {}).items()):
        markers.append(
            {
                "kind": "WAYPOINT",
                "name": name,
                "order": order,
                "matrix": blender_matrix(frame["position"], frame["rotation"]),
                "source": frame,
            }
        )
    for kind, frames in placements.get("effects", {}).items():
        for order, frame in enumerate(frames):
            markers.append(
                {
                    "kind": "EFFECT",
                    "effect": kind,
                    "order": order,
                    "matrix": blender_matrix(frame["position"], frame["rotation"]),
                    "source": frame,
                }
            )
    crowd = [{**row, "source": row} for row in placements.get("crowd", [])]
    return markers, crowd, [key for key in OPTIONAL_PLACEMENTS if key in placements]


# Cameras.
def anchor_name(role, part, bone):
    if role in ("world", "none"):
        return role
    return f"{role}.bone.{bone}" if part == "bone" else f"{role}.{part}"


def parse_anchor(value):
    """(role, part, bone) of an anchor or lookAt value. A missing lookAt is ('none', 'head', '')."""
    if value in (None, "", "none"):
        return "none", "head", ""
    if value == "world":
        return "world", "root", ""
    role, _, part = value.partition(".")
    if part.startswith("bone."):
        return role, "bone", part[5:]
    return role, part, ""


def band_frames(markers):
    """Runtime frames of the band placements: role to (position, rotation matrix)."""
    return {m["role"]: runtime_frame(m["matrix"]) for m in markers if m["kind"] == "BAND"}


def qb_key(name):
    """GH3 checksum of a name (pipeline.gh3.formats.key)."""
    return ~zlib.crc32(name.lower().encode()) & 0xFFFFFFFF


def checksum_text(value):
    """'0x' and 8 hex digits for a name or a checksum text."""
    if re.fullmatch(r"0x[0-9a-fA-F]{8}", value):
        return value.lower()
    if re.fullmatch(r"[0-9a-f]{8}", value):
        return "0x" + value
    return f"0x{qb_key(value):08x}"


def joint_name(part, bone=""):
    """GLB joint name ('bone-<checksum>') of an anchor part: head, hand_left, hand_right, or bone with a bone name."""
    if part == "bone":
        return "bone-" + (bone if re.fullmatch(r"[0-9a-f]{8}", bone) else f"{qb_key(bone):08x}")
    return f"bone-{qb_key(PART_BONES[part]):08x}"


def anchor_frame(anchor, band, anchors=None):
    """(origin, rotation, exact) of the runtime frame of a camera anchor, or None when its role has no band placement.

    anchors: role to {joint name: [position (3), rotation (9)]}, the bone frames of the proxy rig relative to the
    performer root (rig.anchor_frames). A bone anchor without a rig frame uses the performer root (exact False)."""
    role, _, part = anchor.partition(".")
    if role not in band:
        return None
    origin, frame = band[role]
    if part in ("", "root"):
        return origin, frame, True
    stored = (
        (anchors or {})
        .get(role, {})
        .get(joint_name("bone", part[5:]) if part.startswith("bone.") else joint_name(part))
    )
    if stored is None:
        return origin, frame, False
    values = np.asarray(stored, dtype=float)
    return origin + frame @ values[:3], frame @ values[3:12].reshape(3, 3), True


def build_cameras(shots, notes, markers, issues, keeper=None, groups_owner=None, anchors=None):
    """A point is relative to its anchor. A bone anchor (head, hands, bone) uses the bone of the proxy rig (anchors);
    without a rig, Blender shows the point relative to the performer root (the same rule as open_cameras)."""
    keeper = keeper or Keeper("compute")
    band = band_frames(markers)
    result = {"version": 1, "shots": {}, "groups": {}}
    groups = {}
    for shot in shots:
        name = shot["name"]
        if name in result["shots"]:
            issues.error(f"two camera shots have the name '{name}'", shot["object"])
            continue
        points = []
        for point in shot["points"]:
            position, rotation = runtime_frame(point["matrix"], looks=True)
            anchor = point["anchor"]
            if anchor != "world":
                role, _, part = anchor.partition(".")
                frame = anchor_frame(anchor, band, anchors)
                if frame is None:
                    issues.error(f"shot '{name}': anchor {anchor} needs a band placement for {role}", point["object"])
                else:
                    origin, axes, exact = frame
                    position = axes.T @ (position - origin)
                    rotation = axes.T @ rotation
                    if not exact:
                        bone = f"bone {part[5:]}" if part.startswith("bone.") else f"{part} bone"
                        issues.warn(
                            f"shot '{name}': the game places the camera relative to the {bone}; Blender shows it relative "
                            "to the performer root (no GH3 library: set it in the add-on preferences)",
                            point["object"],
                        )
            row = {"anchor": anchor, "position": clean(position)}
            if point.get("useRotation", True):
                row["rotation"] = clean(quaternion(rotation))
            elif not point.get("lookAt"):
                issues.error(f"shot '{name}': a point without rotation needs a look-at target", point["object"])
            row["horizontalFov"] = clean(point["fov"], 4)
            if point.get("lookAt"):
                row["lookAt"] = point["lookAt"]
            if point.get("screenOffset") is not None:
                row["screenOffset"] = clean(point["screenOffset"])
            points.append(row)
        definition = {"type": shot["type"], "control": shot["control"], "points": points}
        for key in ("tag", "orbitAngle", "stepTime", "stepCount", "angularSpeed", "interpolation"):
            if shot.get(key) not in (None, ""):
                definition[key] = clean(shot[key])
        for key in ("stepDirection", "heightEase"):
            if shot.get(key) is not None:
                definition[key] = clean(shot[key])
        for key in ("handCam", "disableOrbit1", "disableOrbit2"):
            if shot.get(key):
                definition[key] = True
        if shot.get("waypoints"):
            definition["waypoints"] = list(shot["waypoints"])
        if shot.get("times"):
            definition["times"] = clean(shot["times"])
        result["shots"][name] = keeper(definition, shot.get("owner"), "shot")
        if not shot["groups"]:
            issues.warn(f"shot '{name}' is in no camera group", shot["object"])
        for group in shot["groups"]:
            groups.setdefault(group, []).append(name)
    result["groups"] = keeper(groups, groups_owner, "groups")
    if "wide" not in result["groups"]:
        issues.error("no camera shot is in the group 'wide' (required)")
    if notes:
        result["notes"] = {}
        for note in notes:
            selector = {key: note[key] for key in ("group", "tag", "type") if note.get(key)}
            if note.get("enable") is not None:
                selector["enable"] = note["enable"]
            result["notes"][str(note["note"])] = keeper(selector, note.get("owner"), "note")
    return result


def look_target(anchor, band, anchors=None):
    """Runtime position that a point without rotation looks at: the rig bone, or 1.5 m above the performer root."""
    frame = anchor_frame(anchor, band, anchors) if anchor not in (None, "", "none", "world") else None
    if frame is None:
        return np.zeros(3)
    origin, _, exact = frame
    root = not anchor.partition(".")[2] or anchor.endswith(".root")
    return origin if exact and not root else band[anchor.partition(".")[0]][0] + [0, LOOK_HEIGHT, 0]


def point_matrix(point, band, anchors=None):
    """Blender world matrix of a cameras.json point (runtime offset relative to its anchor)."""
    position = np.asarray(point["position"], dtype=float)
    rotation = quaternion_matrix(point["rotation"]) if "rotation" in point else None
    frame = anchor_frame(point["anchor"], band, anchors) if point["anchor"] != "world" else None
    if frame is not None:
        origin, axes, _ = frame
        position = origin + axes @ position
        if rotation is not None:
            rotation = axes @ rotation
    if rotation is None:
        rotation = look_rotation(position, look_target(point.get("lookAt"), band, anchors))
    return frame_matrix(position, rotation, looks=True)


def open_cameras(cameras, placements, anchors=None):
    """(shots, notes): Blender shot descriptions (point matrices, anchors, settings) with their source rows."""
    markers, _, _ = open_placements(placements)
    band = band_frames(markers)
    groups_of = {}
    for group, members in cameras.get("groups", {}).items():
        for member in members:
            groups_of.setdefault(member, []).append(group)
    shots = []
    for order, (name, shot) in enumerate(cameras.get("shots", {}).items()):
        points = []
        for point in shot["points"]:
            role, part, bone = parse_anchor(point["anchor"])
            look_role, look_part, look_bone = parse_anchor(point.get("lookAt"))
            points.append(
                {
                    "matrix": point_matrix(point, band, anchors),
                    "fov": point["horizontalFov"],
                    "anchorRole": role,
                    "anchorPart": part,
                    "anchorBone": bone,
                    "lookAtRole": look_role,
                    "lookAtPart": look_part,
                    "lookAtBone": look_bone,
                    "screenOffset": point.get("screenOffset"),
                    "useRotation": "rotation" in point,
                }
            )
        shots.append(
            {
                "name": name,
                "order": order,
                "type": shot["type"],
                "control": shot["control"],
                "tag": shot.get("tag", ""),
                "groups": groups_of.get(name, []),
                "waypoints": list(shot.get("waypoints", [])),
                "times": list(shot.get("times", [])),
                "interpolation": shot.get("interpolation", "default"),
                "orbitAngle": shot.get("orbitAngle"),
                "angularSpeed": shot.get("angularSpeed"),
                "stepTime": shot.get("stepTime"),
                "stepCount": shot.get("stepCount"),
                "stepDirection": shot.get("stepDirection"),
                "heightEase": shot.get("heightEase"),
                "handCam": bool(shot.get("handCam")),
                "disableOrbit1": bool(shot.get("disableOrbit1")),
                "disableOrbit2": bool(shot.get("disableOrbit2")),
                "points": points,
                "source": shot,
            }
        )
    notes = [
        {
            "note": int(number),
            "group": selector.get("group", ""),
            "tag": selector.get("tag", ""),
            "type": selector.get("type", ""),
            "enable": selector.get("enable"),
            "source": selector,
        }
        for number, selector in cameras.get("notes", {}).items()
    ]
    return shots, notes


def point_offset(point, band, anchors=None):
    """Runtime (position, rotation) of a Blender point description relative to its anchor (for re-anchoring)."""
    position, rotation = runtime_frame(point["matrix"], looks=True)
    frame = anchor_frame(point["anchor"], band, anchors) if point["anchor"] != "world" else None
    if frame is None:
        return position, rotation
    origin, axes, _ = frame
    return axes.T @ (position - origin), axes.T @ rotation


def offset_matrix(anchor, position, rotation, band, anchors=None):
    """Blender world matrix of a runtime offset from an anchor (the inverse of point_offset)."""
    frame = anchor_frame(anchor, band, anchors) if anchor != "world" else None
    if frame is not None:
        origin, axes, _ = frame
        position, rotation = origin + axes @ position, axes @ rotation
    return frame_matrix(position, rotation, looks=True)


# Actors (animated scenery, actors.json).
def scaled_matrix(position, rotation, scale):
    """Blender world matrix of a runtime transform and a runtime scale (local axes)."""
    m = blender_matrix(position, rotation)
    m[:3, :3] = m[:3, :3] @ np.diag(SWAP @ np.asarray(scale, dtype=float))
    return m


def runtime_scale(matrix):
    """Runtime scale (local axes) of a Blender world matrix."""
    m = np.asarray(matrix, dtype=float)[:3, :3]
    return SWAP @ np.linalg.norm(m, axis=0)


def build_actors(actors, issues, keeper=None):
    """actors.json of actor descriptions (object, name, model, matrix, and the optional fields), or None without actors."""
    if not actors:
        return None
    keeper = keeper or Keeper("compute")
    result = {"version": 1, "actors": {}}
    for actor in actors:
        name = actor["name"]
        if name in result["actors"]:
            issues.error(f"two actors have the name '{name}'", actor["object"])
            continue
        if not re.fullmatch(r"[A-Za-z0-9_.-]{1,128}", name):
            issues.error(f"actor name '{name}': use 1-128 letters, digits, '_', '.', or '-'", actor["object"])
        row = {"model": actor["model"]}
        for key, field in (
            ("clip", "clip"),
            ("lightGroup", "lightGroup"),
            ("playback", "playback"),
            ("object", "scriptObject"),
            ("script", "script"),
        ):
            if actor.get(field):
                row[key] = actor[field]
        for key in ("created", "initiallyVisible"):
            if actor.get(key) is not None:
                row[key] = bool(actor[key])
        scale = clean(runtime_scale(actor["matrix"]))
        if any(abs(v - 1) > 1e-6 for v in scale) or actor.get("writeScale"):
            row["scale"] = scale
        if any(v <= 0 for v in scale):
            issues.error(f"actor '{name}': the scale must be positive", actor["object"])
        if actor.get("animationEvents"):
            row["animationEvents"] = actor["animationEvents"]
        row["transform"] = transform(actor["matrix"])
        if actor.get("role"):
            row["role"] = actor["role"]
        if actor.get("actions"):
            row["actions"] = {k: list(v) for k, v in actor["actions"].items()}
        result["actors"][name] = keeper(row, actor.get("owner"), "actor")
    return result


def open_actors(actors):
    """Blender actor descriptions of actors.json, with their source rows."""
    result = []
    for order, (name, row) in enumerate(actors.get("actors", {}).items()):
        frame = row["transform"]
        result.append(
            {
                "name": name,
                "order": order,
                "model": row["model"],
                "clip": row.get("clip", ""),
                "playback": row.get("playback", ""),
                "lightGroup": row.get("lightGroup", ""),
                "role": row.get("role", ""),
                "scriptObject": row.get("object", ""),
                "script": row.get("script", ""),
                "animationEvents": row.get("animationEvents", ""),
                "created": row.get("created"),
                "initiallyVisible": row.get("initiallyVisible"),
                "writeScale": "scale" in row,
                "actions": {k: list(v) for k, v in row.get("actions", {}).items()},
                "matrix": scaled_matrix(frame["position"], frame["rotation"], row.get("scale", [1, 1, 1])),
                "source": row,
            }
        )
    return result


# Lighting.
def build_lighting(lights, snapshots, moods, colors, issues, keeper=None):
    if not lights and not snapshots:
        return None
    keeper = keeper or Keeper("compute")
    result = {"version": 1, "lights": [], "snapshots": {}, "moods": {}, "colors": {}}
    for light in lights:
        position, rotation = runtime_frame(light["matrix"], looks=True)
        row = {"id": light["id"]}
        if light.get("object"):
            row["object"] = light["object"]
        row.update(
            group=light["group"] or "Stage",
            type=light["type"],
            position=clean(position),
            rotation=clean(quaternion(rotation)),
        )
        if light.get("overrideColor"):
            row["overrideColor"] = True
        if light.get("hotspot") is not None:
            row["hotspot"] = clean(light["hotspot"], 4)
            row["field"] = clean(light["field"], 4)
        result["lights"].append(keeper(row, light.get("owner"), "light"))
    if not lights:
        issues.error("lighting needs at least one stage light (Light properties > Stage light)")
    for snapshot in snapshots:
        settings = {entry["light"]: entry for entry in snapshot["lights"] if entry.get("light")}
        rows = []
        for light in lights:
            entry = settings.get(light["source"])
            if entry is None:
                issues.error(
                    f"snapshot '{snapshot['name']}' has no settings for the light '{light['id']}'; use Capture",
                    light["source"],
                )
                entry = {"color": [0, 0, 0], "intensity": 0, "near": 0, "far": 0}
            row = {"color": clean(entry["color"]), "intensity": clean(entry["intensity"])}
            if entry.get("specular") is not None:
                row["specular"] = clean(entry["specular"])
            row.update(near=clean(entry["near"]), far=clean(entry["far"]))
            if entry.get("inherit"):
                row["inherit"] = True
            rows.append(row)
        beams = {}
        for beam in snapshot["beams"]:
            position = runtime_position(beam["position"])
            w, x, y, z = beam["rotation"]
            rotation = SWAP @ quaternion_matrix([x, y, z, w]) @ LOOK
            row = {
                "position": clean(position),
                "rotation": clean(quaternion(rotation)),
                "color": clean(beam["color"]),
                "density": clean(beam["density"]),
                "startRadius": clean(beam["startRadius"]),
                "endRadius": clean(beam["endRadius"]),
                "range": clean(beam["range"]),
            }
            if beam.get("inherit"):
                row["inherit"] = True
            beams[beam["id"]] = row
        if snapshot["name"] in result["snapshots"]:
            issues.error(f"two snapshots have the name '{snapshot['name']}'")
        result["snapshots"][snapshot["name"]] = keeper(
            {"lights": rows, "beams": beams}, snapshot.get("owner"), "snapshot"
        )
    for mood in moods:
        if mood["mood"] in result["moods"]:
            issues.error(f"the mood '{mood['mood']}' occurs two times")
        result["moods"][mood["mood"]] = keeper(list(mood["snapshots"]), mood.get("owner"), "mood")
    for name in ("intro", "blackout"):
        if name not in result["moods"]:
            issues.error(f"lighting needs the mood '{name}'")
    for color in colors:
        result["colors"][color["name"]] = keeper(clean(color["color"]), color.get("owner"), "color")
    return result


# Lighting preview (lighting_tools.py): Blender light power for a GH3 light.
PREVIEW_SUN_STRENGTH = 3.0
PREVIEW_MIN_RADIUS = 0.5
# A point light that is at full intensity up to this distance (GH3 fill lights: near 80 m) lights the stage evenly.
PREVIEW_FILL_NEAR = 20.0


def preview_light(kind, intensity, near, far):
    """(Blender light power, ambient intensity) that show a GH3 light: W for a point or spot light, W/m2 for a sun.

    GH3 attenuation is linear from near to far; Blender uses the inverse square law. The power gives the GH3 intensity
    at the middle of the near-far range (a preview, not a match). Ambient lights and fill lights (near >= 20 m) add to
    the ambient color of the preview world. Blender has no negative lights: they get 0."""
    if intensity <= 0:
        return 0.0, 0.0
    if kind == "Ambient" or (kind in ("Point", "Spot") and near >= PREVIEW_FILL_NEAR):
        return 0.0, float(intensity)
    if kind == "Dir":
        return intensity * PREVIEW_SUN_STRENGTH, 0.0
    radius = max(PREVIEW_MIN_RADIUS, 0.5 * (near + far))
    return intensity * 4 * math.pi**2 * radius**2, 0.0


def open_lighting(lighting):
    """(lights, snapshots, moods, colors) descriptions for Blender, with their source rows."""
    lights = []
    for order, row in enumerate(lighting.get("lights", [])):
        field = row.get("field")
        lights.append(
            {
                "order": order,
                "id": row["id"],
                "object": row.get("object", ""),
                "group": row["group"],
                "type": row["type"],
                "blenderType": RUNTIME_LIGHT_TYPES[row["type"]],
                "matrix": blender_matrix(row["position"], row["rotation"], looks=True),
                "overrideColor": bool(row.get("overrideColor")),
                "useCone": field is not None,
                "hotspot": row.get("hotspot", 0),
                "field": field or 0,
                "source": row,
            }
        )
    snapshots = []
    for name, snapshot in lighting.get("snapshots", {}).items():
        rows = [
            {
                "index": index,
                "color": row["color"],
                "intensity": row["intensity"],
                "near": row["near"],
                "far": row["far"],
                "inherit": bool(row.get("inherit")),
                "specular": row.get("specular"),
            }
            for index, row in enumerate(snapshot["lights"])
        ]
        beams = [
            {
                "id": beam_id,
                "position": list(SWAP @ np.asarray(beam["position"], dtype=float)),
                "rotation": blender_quaternion(beam["rotation"], looks=True),
                "color": beam["color"],
                "density": beam["density"],
                "startRadius": beam["startRadius"],
                "endRadius": beam["endRadius"],
                "range": beam["range"],
                "inherit": bool(beam.get("inherit")),
            }
            for beam_id, beam in snapshot["beams"].items()
        ]
        snapshots.append({"name": name, "lights": rows, "beams": beams, "source": snapshot})
    moods = [
        {"mood": mood, "snapshots": list(names), "source": names} for mood, names in lighting.get("moods", {}).items()
    ]
    colors = [{"name": name, "color": color, "source": color} for name, color in lighting.get("colors", {}).items()]
    return lights, snapshots, moods, colors


# Stage GLB extras.
def node_extras(node, matrix=None):
    """gh3* node extras (PACKAGE-FORMAT 6.4) of one object description."""
    extras = {}
    if node.get("lightGroup"):
        extras["gh3LightGroup"] = node["lightGroup"]
    if node.get("visibilityGroup"):
        extras["gh3VisibilityGroup"] = node["visibilityGroup"]
        extras["gh3InitiallyVisible"] = bool(node.get("initiallyVisible", True))
    if node.get("fixtureId"):
        extras["gh3FixtureId"] = node["fixtureId"]
        if node.get("fixtureFixedPosition"):
            extras["gh3FixtureFixedPosition"] = True
        if node.get("fixtureObject"):
            extras["gh3FixtureObject"] = node["fixtureObject"]
        if node.get("fixtureCreated") is not None:
            extras["gh3FixtureCreated"] = bool(node["fixtureCreated"])
    if node.get("object"):
        extras["gh3Object"] = node["object"]
        pivot = node.get("pivot")
        if pivot == "ORIGIN" and matrix is not None:
            frame = transform(matrix)
            extras["gh3ObjectPivot"] = frame["position"]
            extras["gh3ObjectRotation"] = frame["rotation"]
        elif pivot == "CUSTOM":
            extras["gh3ObjectPivot"] = [float(v) for v in node["pivotPosition"]]
            extras["gh3ObjectRotation"] = [float(v) for v in node["pivotRotation"]]
        if node.get("created") is not None:
            extras["gh3ObjectCreated"] = bool(node["created"])
        if node.get("script"):
            extras["gh3ObjectScript"] = node["script"]
    return extras


def open_node(extras):
    """Blender object settings (CHSTAGE_PG_object property names) of the gh3* extras of a stage node."""
    props = {
        "light_group": extras.get("gh3LightGroup", ""),
        "visibility_group": extras.get("gh3VisibilityGroup", ""),
        "initially_visible": bool(extras.get("gh3InitiallyVisible", True)),
        "fixture_id": extras.get("gh3FixtureId", ""),
        "fixture_fixed_position": bool(extras.get("gh3FixtureFixedPosition", False)),
        "fixture_object": extras.get("gh3FixtureObject", ""),
        "use_fixture_created": "gh3FixtureCreated" in extras,
        "fixture_created": bool(extras.get("gh3FixtureCreated", True)),
        "script_object": extras.get("gh3Object", ""),
        "use_object_created": "gh3ObjectCreated" in extras,
        "object_created": bool(extras.get("gh3ObjectCreated", True)),
        "object_script": extras.get("gh3ObjectScript", ""),
    }
    if "gh3ObjectPivot" in extras or "gh3ObjectRotation" in extras:
        props.update(
            object_pivot_mode="CUSTOM",
            object_pivot=extras.get("gh3ObjectPivot", [0, 0, 0]),
            object_rotation=extras.get("gh3ObjectRotation", [0, 0, 0, 1]),
        )
    return props


def material_fields(material):
    """(gh3* material extras, standard glTF material fields) of one material description (PACKAGE-FORMAT 6.5)."""
    # An absent gh3Shading is not "lit": the mod then draws the material without the stage surface model.
    extras = {"gh3Shading": material["shading"]} if material["shading"] != "NONE" else {}
    gltf = {"alphaMode": "OPAQUE", "doubleSided": bool(material.get("twoSided"))}
    blend = material["blend"]
    if blend == "CUTOUT":
        gltf.update(alphaMode="MASK", alphaCutoff=clean(material["alphaCutoff"]))
    elif blend in ("BLEND", "ADDITIVE"):
        gltf["alphaMode"] = "BLEND"
        if blend == "ADDITIVE":
            extras["gh3Additive"] = True
        if material.get("blendCutoff"):
            extras["gh3AlphaCutoff"] = clean(material["alphaCutoff"])
    if material.get("colorScale", 1) != 1:
        extras["gh3ColorScale"] = clean(material["colorScale"], 4)
    if material.get("crowdAtlas"):
        extras["gh3CrowdAtlas"] = True
    if material.get("crowdRole"):
        extras["gh3CrowdRole"] = material["crowdRole"]
    if material.get("billboard"):
        extras["gh3Billboard"] = True
    if material.get("movieSlot", -1) >= 0:
        extras["gh3MovieSlot"] = int(material["movieSlot"])
    if material.get("sortedTransparency"):
        extras["gh3SortedTransparency"] = True
    if material.get("flipbook"):
        extras["gh3TextureAnimation"] = {k: clean(v, 4) for k, v in material["flipbook"].items()}
    if material.get("toon"):
        extras["gh3Toon"] = toon_extras(material["toon"])
    if material.get("hairLayer"):
        extras["gh3HairLayer"] = material["hairLayer"]
    for key, value in (material.get("otherExtras") or {}).items():
        if key not in PANEL_MATERIAL_EXTRAS:
            extras[key] = value
    if material.get("lightmap"):
        gltf["occlusionTexture"] = {"image": material["lightmap"], "texCoord": 1}
    return extras, gltf


def toon_extras(toon):
    """gh3Toon of the toon settings of a material description (PACKAGE-FORMAT 6.5). Images are '#<image name>' references."""

    def color(value):
        return [clean(v, 4) for v in value]

    extras = {
        "threshold": clean(toon["threshold"], 4),
        "softness": clean(toon["softness"], 4),
        "shadeColor": color(toon["shadeColor"]),
    }
    for key in TOON_TEXTURES:
        if toon.get(key):
            extras[key] = "#" + toon[key]
    if toon.get("rimWidth"):
        extras.update(rimColor=color(toon["rimColor"]), rimWidth=clean(toon["rimWidth"], 4))
    if toon.get("sphereTexture"):
        extras["sphereMode"] = toon["sphereMode"]
    if toon.get("outline"):
        line = toon["outline"]
        extras["outline"] = {
            "width": clean(line["width"], 6),
            "color": color(line["color"]),
            "textured": bool(line["textured"]),
        }
    return extras


def open_toon(extras):
    """Blender material settings of gh3Toon and gh3HairLayer (the texture references stay in toon_references)."""
    toon = extras.get("gh3Toon") if isinstance(extras.get("gh3Toon"), dict) else {}
    props = {
        "hair_layer": extras.get("gh3HairLayer", "NONE"),
        "toon_threshold": toon.get("threshold", 0.5),
        "toon_softness": toon.get("softness", 0.05),
        "toon_shade_color": toon.get("shadeColor", [1, 1, 1]),
        "toon_rim_color": toon.get("rimColor", [0, 0, 0]),
        "toon_rim_width": toon.get("rimWidth", 0),
        "toon_sphere_mode": toon.get("sphereMode", "add"),
        "use_outline": "outline" in toon,
    }
    if "outline" in toon:
        line = toon["outline"]
        props.update(
            outline_width=line.get("width", 0.0015),
            outline_color=line.get("color", [0, 0, 0]),
            outline_textured=bool(line.get("textured")),
        )
    return props


def toon_ramp_settings(pixels):
    """(threshold, softness, shade color) of an MMD toon ramp (rows top to bottom, RGB 0-1). MMD reads the ramp at
    v = 0.5 - 0.5 N.L from the top: the half-Lambert light amount is 1 - v."""
    column = np.asarray(pixels, dtype=float)[:, pixels.shape[1] // 2, :3]
    lit, shade = column[0], column[-1]
    luminance = column @ np.array([0.299, 0.587, 0.114])
    span = luminance[0] - luminance[-1]
    if span < 1e-3:
        return 0.0, 0.001, tuple(float(v) for v in lit)
    amount = np.clip((luminance - luminance[-1]) / span, 0, 1)
    rows = len(amount)
    v = (np.arange(rows) + 0.5) / rows

    def crossing(level):
        index = int(np.argmax(amount < level))
        if index == 0:
            return v[0]
        a, b = amount[index - 1], amount[index]
        return v[index - 1] + (v[index] - v[index - 1]) * (a - level) / max(a - b, 1e-6)

    middle, start, end = crossing(0.5), crossing(0.9), crossing(0.1)
    shade_color = tuple(float(s / max(l, 1e-3)) for s, l in zip(shade, lit))
    return (
        float(np.clip(1 - middle, 0, 1)),
        float(np.clip((end - start) / 2, 0.001, 0.5)),
        tuple(min(c, 4.0) for c in shade_color),
    )


def toon_references(extras):
    """Blender setting name to texture reference of the gh3Toon textures."""
    toon = extras.get("gh3Toon") if isinstance(extras.get("gh3Toon"), dict) else {}
    return {prop: toon[key] for key, prop in TOON_TEXTURES.items() if isinstance(toon.get(key), str) and toon[key]}


def material_row(material):
    """The material data that the add-on writes into stage.glb: extras, alphaMode, alphaCutoff, doubleSided, and the
    base color alpha of an opaque material (the Blender glTF importer and exporter drop it)."""
    extras, gltf = material_fields(material)
    row = {"extras": extras, "alphaMode": gltf["alphaMode"], "doubleSided": gltf["doubleSided"]}
    if "alphaCutoff" in gltf:
        row["alphaCutoff"] = gltf["alphaCutoff"]
    if gltf["alphaMode"] == "OPAQUE" and material.get("opaqueAlpha", 1) != 1:
        row["opaqueAlpha"] = clean(material["opaqueAlpha"])
    return row


def gltf_material_row(material):
    """material_row of a glTF material of a package GLB (glTF defaults filled in)."""
    row = {
        "extras": dict(material.get("extras", {})),
        "alphaMode": material.get("alphaMode", "OPAQUE"),
        "doubleSided": bool(material.get("doubleSided", False)),
    }
    if row["alphaMode"] == "MASK":
        row["alphaCutoff"] = material.get("alphaCutoff", 0.5)
    alpha = material.get("pbrMetallicRoughness", {}).get("baseColorFactor", [1, 1, 1, 1])[3]
    if row["alphaMode"] == "OPAQUE" and alpha != 1:
        row["opaqueAlpha"] = alpha
    return row


def open_material(material):
    """(Blender material settings, other gh3* extras) of a glTF material of a package GLB."""
    extras = material.get("extras", {})
    mode = material.get("alphaMode", "OPAQUE")
    blend = {"MASK": "CUTOUT", "BLEND": "ADDITIVE" if extras.get("gh3Additive") else "BLEND"}.get(mode, "OPAQUE")
    cutoff = material.get("alphaCutoff", 0.5) if mode == "MASK" else extras.get("gh3AlphaCutoff", 0.5)
    flipbook = extras.get("gh3TextureAnimation")
    props = {
        "shading": extras.get("gh3Shading", "NONE"),
        "blend": blend,
        "alpha_cutoff": cutoff,
        "use_blend_cutoff": mode == "BLEND" and "gh3AlphaCutoff" in extras,
        "two_sided": bool(material.get("doubleSided", False)),
        "color_scale": extras.get("gh3ColorScale", 1),
        "crowd_atlas": bool(extras.get("gh3CrowdAtlas", False)),
        "crowd_role": extras.get("gh3CrowdRole", "NONE"),
        "billboard": bool(extras.get("gh3Billboard", False)),
        "movie_slot": extras.get("gh3MovieSlot", -1),
        "sorted_transparency": bool(extras.get("gh3SortedTransparency", False)),
        "use_flipbook": flipbook is not None,
        "opaque_alpha": material.get("pbrMetallicRoughness", {}).get("baseColorFactor", [1, 1, 1, 1])[3]
        if mode == "OPAQUE"
        else 1,
    }
    if flipbook:
        props.update(
            flipbook_columns=flipbook.get("columns", 1),
            flipbook_rows=flipbook.get("rows", 1),
            flipbook_fps=flipbook.get("fps", 10),
            flipbook_offset=flipbook.get("offset", 0),
        )
    props.update(open_toon(extras))
    other = {key: value for key, value in extras.items() if key.startswith("gh3") and key not in PANEL_MATERIAL_EXTRAS}
    return props, other


def texture_references(extras):
    """(container, key) pairs of every texture reference in material extras (PACKAGE-FORMAT 6.6)."""
    refs = [(extras, key) for key in ("gh3MaskTexture", "gh3LayerTexture") if isinstance(extras.get(key), str)]
    shader = extras.get("gh3Shader")
    if isinstance(shader, dict) and isinstance(shader.get("textures"), list):
        refs += [
            (shader["textures"], index) for index, value in enumerate(shader["textures"]) if isinstance(value, str)
        ]
    return refs
