"""Read the GH3 venue cameras (guitar_cameras.qb, <venue>_cameras.qb) and the band performance commands of the songs."""

import math

from pipeline.gh3.data import script
from pipeline.gh3.formats import find_array, key

ROLES = {
    key(name): role
    for name, role in [
        ("guitarist", "guitarist"),
        ("bassist", "bassist"),
        ("vocalist", "singer"),
        ("drummer", "drummer"),
        ("world", "world"),
    ]
}
WORDS = """stance_a stance_b stance_c intro intro_smstg win lose win_smstg lose_smstg
idle jump kick solo special release long_note transitions good normal bad slow med fast short long
no_strum_anim start_anim cycle_anim stop_left_anim stop_right_anim
guitarist_start guitarist_walk01 guitarist_walk02 guitarist_walk03 bassist_start bassist_walk01 bassist_walk02
strum mute normal_strums bass_normal_strums bass_pick_strums bass_slap_strums
closeup mid stage longshot orbit_stage orbit_drummer orbit_singer orbit_closeup orbit_mid
guitar_closeup special01 special02 special03 special04 normal demonik none""".split()
WORDS = {key(name): name for name in WORDS}


# Camera field defaults of the GH3 camera scripts.
DEFAULT_FIELD_OF_VIEW = 72
DEFAULT_STEP_TIME = 6
DEFAULT_STEP_COUNT = 6
# The camera control scripts CameraCuts_<name> that the stage camera supports.
CAMERA_CONTROLS = [
    "TwoCam",
    "HandCam",
    "Orbit",
    "OrbitAndMorphTwoCam",
    "Pan",
    "ThreePos",
    "FourPos",
    "SpecialStepMove",
    "QuickZoom",
    "HandCamZoom2",
    "SplineMorph",
    "NewIntro",
]
# Camera arrays that every venue can have, in addition to the prefixes of CameraCuts_NoteMapping.
CAMERA_PREFIXES = {
    "cameras",
    "cameras_intro",
    "cameras_fastintro",
    "cameras_win",
    "cameras_pan",
    "cameras_dolly",
    "cameras_zoom_slow",
    "cameras_zoom_fast",
    "cameras_zoom_out",
}
# Keyframes for the controls that move by script, not by their camera points (from the first venue export, ee94b4e;
# no GH3 citation): QuickZoom, the lens steps of HandCamZoom2, and the five NewIntro poses (position, quaternion xyz, FOV).
QUICK_ZOOM_TIMES = [0, 2, 2.75]
HAND_CAM_ZOOM_LENSES = [50, 50, 53, 53, 51]
HAND_CAM_ZOOM_TIMES = [0, 2, 2.6, 2.7, 2.95, 2.98, 3.28]
NEW_INTRO_POSES = [
    ([6.6424026, 2.254917, 24.026098], [0.006744, -0.99366295, 0.092284], 72),
    ([6.38234, 1.9634129, 22.308147], [0.017372, -0.979427, 0.17970501], 72),
    ([6.38239, 1.9634119, 22.308136], [0.009489, -0.99132794, 0.09404301], 72),
    ([6.38239, 1.9634119, 22.308136], [0.0077960007, -0.99213994, 0.064734], 36),
    ([6.38239, 1.9634119, 22.308136], [0.0077960007, -0.99213994, 0.064734], 38),
]
NEW_INTRO_SEQUENCE = [0, 0, 1, 1, 2, 2, 3, 3, 4]
NEW_INTRO_TIMES = [0, 0.3, 3.1, 3.45, 4.15, 4.95, 5.45, 5.55, 5.85]
SPLINE_POINT_COUNTS = (3, 4)
# Song performance commands: QB script, command type, parameter name, and default value.
PERFORMANCE_COMMANDS = [
    ("Band_ChangeStance", "stance", "stance", "stance_a"),
    ("Band_PlayAnim", "perform", "anim", "idle"),
    ("Band_PlayIdle", "perform", "anim", "idle"),
    ("Band_PlayFacialAnim", "face", "anim", "none"),
    ("Band_PlaySimpleAnim", "clip", "anim", "none"),
    ("Band_WalkToNode", "walk", "node", "guitarist_start"),
    ("Band_SetStrumStyle", "strum-style", "style", "normal"),
    ("Band_StopStrumming", "strum-style", "style", "none"),
    ("Band_DisableArms", "arms", "disable_arms", "2"),
    ("Band_EnableArms", "arms", "disable_arms", "0"),
]
# disableArms values of a performance command.
ARMS_AUTOMATIC, ARMS_NO_AUTO_STRUM, ARMS_NO_AUTO_ARMS = 0, 1, 2


def name(value):
    """The text of a checksum when it is a known direction word, else its hex form."""
    return WORDS.get(value, f"{value:08x}") if isinstance(value, int) else str(value).lower()


def vector(value):
    x, y, z = value
    return [x, y, -z]


def camera_point(data):
    xyz = data.get(key("Quat"), (0, 0, 0))
    x, y, z = xyz
    w = math.sqrt(max(0, 1 - sum(v * v for v in xyz)))
    return {
        "position": vector(data.get(key("Pos"), (0, 0, 0))),
        "rotation": [-z, w, -x, y],
        "fieldOfView": data.get(key("FOV"), DEFAULT_FIELD_OF_VIEW),
        "lockTo": ROLES.get(data.get(key("LockTo")), "world"),
        "lockBone": f"{data.get(key('LockToBone'), 0):08x}",
        "lookAt": ROLES.get(data.get(key("LookAt")), ""),
        "lookBone": f"{data.get(key('LookAtBone'), 0):08x}",
        "screenOffset": data.get(key("ScreenOffset"), (0, 0)),
    }


def camera_motion(control, points, params):
    if control == "SplineMorph":
        points = [camera_point(p) for p in params[key("keyframes")]]
        if len(points) not in SPLINE_POINT_COUNTS:
            raise ValueError("Unsupported source camera spline length")
        return ("ThreePos" if len(points) == 3 else "FourPos"), points, {}
    if control == "QuickZoom":
        return "Keyframes", [points[0], points[0], points[1]], {"times": QUICK_ZOOM_TIMES, "interpolation": "smooth"}
    if control == "HandCamZoom2":
        lens = [points[0]["fieldOfView"]] * 2 + HAND_CAM_ZOOM_LENSES
        return (
            "Keyframes",
            [{**points[0], "fieldOfView": fov} for fov in lens],
            {"times": HAND_CAM_ZOOM_TIMES, "interpolation": "smooth"},
        )
    if control == "NewIntro":
        poses = [camera_point({key("Pos"): p, key("Quat"): q, key("FOV"): fov}) for p, q, fov in NEW_INTRO_POSES]
        return (
            "Keyframes",
            [poses[i] for i in NEW_INTRO_SEQUENCE],
            {"times": NEW_INTRO_TIMES, "interpolation": "smooth"},
        )
    return control, points, {}


def _signed(value):
    """A QB integer (stored as an unsigned word) as a signed 32-bit value."""
    return value - 0x100000000 if value > 0x7FFFFFFF else value


def cameras(source, scripts, venue="z_budokan"):
    """The camera arrays and the note selectors of a venue, in the stage-pack cameras.json form."""
    entries = source.load(f"ZONES/{venue}.pak.xen")
    data = next(b for k, n, b in entries if k == key(".qb") and n == key(f"zones\\{venue}\\{venue}_cameras.qb"))
    mapping = find_array(script(scripts, "guitar_cameras"), "CameraCuts_NoteMapping")
    selectors = {}
    prefixes = set(CAMERA_PREFIXES)
    for row in mapping:
        params = row[key("params")]
        note = row[key("MidiNote")]
        if key("prefix") in params:
            prefix = params[key("prefix")]
            prefixes.add(prefix)
            selectors[note] = {
                "array": prefix,
                "name": name(params[key("Name")]) if key("Name") in params else "",
                "type": "normal"
                if key("selectguitartype") in params.get(0, [])
                else name(params[key("Type")])
                if key("Type") in params
                else "",
            }
        else:
            selectors[note] = {"enable": params.get(key("enable")) == key("true")}
    arrays, unsupported = {}, []
    for prefix in sorted(prefixes):
        rows = find_array(data, f"{venue}_{prefix}")
        if rows is None:
            rows = find_array(script(scripts, "guitar_cameras"), f"default_{prefix}")
        if not rows:
            continue
        shots = []
        for row in rows:
            params = row.get(key("params"), row)
            control = next((c for c in CAMERA_CONTROLS if row.get(key("controlscript")) == key("CameraCuts_" + c)), "")
            if not control:
                unsupported.append(f"{row.get(key('controlscript'), 0):08x}")
                continue
            points = [camera_point(params[key(f"Cam{i}")]) for i in range(1, 5) if key(f"Cam{i}") in params]
            control, points, motion = camera_motion(control, points or [camera_point(params)], params)
            angle = _signed(params.get(key("orbitangle"), 0))
            nodes = row.get(key("includenodes"), params.get(key("includenodes"), []))
            shots.append(
                {
                    "name": name(row.get(key("Name"), params.get(key("Name"), 0))),
                    "type": name(row.get(key("Type"), params.get(key("Type"), 0))),
                    "control": control,
                    "points": points or [camera_point(params)],
                    "nodes": [f"{node:08x}" for node in nodes],
                    "orbitAngle": angle,
                    "stepTime": params.get(key("StepTime"), DEFAULT_STEP_TIME),
                    "stepCount": params.get(key("StepNum"), DEFAULT_STEP_COUNT),
                    "stepDirection": vector(params.get(key("StepDir"), (0, 0, 0))),
                    "handCam": control == "HandCam" or key("UseHandCam") in params.get(0, []),
                    "disableOrbit1": key("DisableOrbit1") in params.get(0, []),
                    "disableOrbit2": key("DisableOrbit2") in params.get(0, []),
                    **motion,
                }
            )
        if shots:
            arrays[prefix] = shots
    return {
        "version": 1,
        "arrays": arrays,
        "notes": selectors,
        "unmappedControls": sorted(set(unsupported)),
        "sources": source.hashes.copy(),
    }


def performance_command(row):
    """A song performance command row as a direction event, or None when the mod does not use it."""
    params = row.get(key("params"), {})
    command = row.get(key("scr"))
    role = ROLES.get(params.get(key("Name"), key("guitarist")))
    kinds = {key(s): (kind, parameter, default) for s, kind, parameter, default in PERFORMANCE_COMMANDS}
    if command not in kinds or role is None:
        return None
    kind, parameter, default = kinds[command]
    value = params.get(key(parameter), default)
    if kind == "arms":
        value = str(value)
    elif kind in ("face", "clip") and isinstance(value, int):
        value = f"{value:08x}"
    else:
        value = name(value)
    return {
        "time": row[key("time")] / 1000,
        "type": kind,
        "role": role,
        "value": value,
        "immediate": key("no_wait") in params.get(0, []),
        "loop": key("cycle") in params.get(0, []) or command == key("Band_PlayIdle"),
        "count": params.get(key("repeat_count"), 1),
        "disableArms": ARMS_NO_AUTO_ARMS
        if key("disable_auto_arms") in params.get(0, [])
        else ARMS_NO_AUTO_STRUM
        if key("disable_auto_strum") in params.get(0, [])
        else ARMS_AUTOMATIC,
    }
