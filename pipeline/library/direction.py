"""Convert the owner's original GH3 camera and performance libraries."""

import gzip
import json

import numpy as np

from pipeline.gh3.band_formats import (
    animation,
    clip_bone_count,
    multiply_quaternions,
    sample_animation,
    skeleton,
)
from pipeline.gh3.data import Source, scalar, script
from pipeline.gh3.direction import cameras, name
from pipeline.gh3.formats import find_array, find_struct, is_qb_file, key
from pipeline.package import runtime

MIN_CLIP_SECONDS = 1 / 60
CLIP_GZIP_LEVEL = 3
# Scalars of guitar_animation_data.qb that the band animation uses, with the value that applies when the file has none.
ANIMATION_CONSTANTS = [
    ("slow_tempo", 75),
    ("med_tempo", 115),
    ("fast_tempo", 145),
    ("short_strum_max_gem_length", 150),
    ("med_strum_max_gem_length", 600),
    ("strum_anim_lead_time", 250),
    ("drum_blend_time", 0.1),
    ("fret_blend_time", 0.1),
    ("drum_anim_lead_time", 150),
    ("drummer_arm_blend_out_delay", 0.25),
    ("drummer_clear_arm_twist_value_delay", 0),
    ("drummer_twist_rate", 0.05),
    ("finger_press_blend_time", 0.07),
    ("finger_release_blend_time", 0.2),
    ("finger_note_padding", 0.01),
    # The shortest walk of walk_to_pos (guitar_walking.qb).
    ("min_walk_dist", 0.75),
]
# MIDI notes of the GH3 animation tracks: drums (track_36 to track_57) and the fret hand positions (track_101 to 127).
DRUM_PITCHES = range(36, 58)
FRET_PITCHES = range(101, 128)
# Chord layers: one for each combination of the five fret colours; a chord clip names at most four colours.
FRET_COLORS = ["green", "red", "yellow", "blue", "orange"]
CHORD_MASKS = 1 << len(FRET_COLORS)
MAX_CHORD_COLORS = 4
# The walk struct of a guitarist animation set (`<name>_animations.walking`, guitar_animation_data_<name>.qb): the clips
# of the forward and backward walks, the turn clips (play_turn_anim), and the turn scalars of walk_to_pos and
# turn_to_face (guitar_walking.qb; min_turn selects the backward walk).
WALKING = "walking"
WALK_DIRECTIONS = ("forward", "backward")
WALK_TURN_CLIPS = ("turn_left", "turn_idle", "turn_right")
WALK_SCALARS = ("min_turn", "max_turn", "turn_speed", "face_audience_speed")
# The turn scalars of a set without `walking` (guitar_animation_data.qb default_turn_data: min_turn, max_turn).
DEFAULT_TURN_DATA = "default_turn_data"

# The last column lists extra paks with clips: Axel's model pak holds his intro and win animations.
MEMBERS = [
    ("guitarist", "GH3_guitarist_axel", "axel_1_anims", "guitar_animation_data_axel", "axel_animations", ["axel_1"]),
    ("bassist", "GH3_bassist", "Bassist_anims", "guitar_animation_data_bassist", "bassist_animations", []),
    (
        "singer",
        "GH3_singer_male",
        "Singer_Male_Anims",
        "guitar_animation_data_singer_male",
        "singer_male_animations",
        [],
    ),
    ("drummer", "GH3_drummer", "Drummer_anims", "guitar_animation_data", "drummer_animations", []),
]


def flatten(data, prefix=""):
    result = {}
    for field, value in data.items():
        if field == 0:
            continue
        path = prefix + name(field)
        if isinstance(value, dict):
            result.update(flatten(value, path + "/"))
        elif isinstance(value, list):
            result[path] = [f"{v:08x}" for v in value if isinstance(v, int)]
        elif isinstance(value, int):
            result[path] = [f"{value:08x}"]
    return result


def without_walking(anim_set):
    """An animation set struct without its `walking` struct (walk_groups gives the walk groups)."""
    return {field: value for field, value in anim_set.items() if field != key(WALKING)}


def walk_groups(anim_set, common):
    """({`walk/<direction>/<clip>/<speed>`: refs}, {walking scalar: value}) of an animation set.

    GH3 walk_to_pos plays `walking.forward` or `walking.backward` of the performer's own set: it reads `anim_set` from the
    performer's info struct, not the `normal_walk_data` of the Band_WalkToNode event (PARITY-NOTES.md, Walk clips). A set
    without `walking` (the bassist) gets normal_walk_data as its forward walk and the scalars of default_turn_data.
    """
    walking = anim_set.get(key(WALKING))
    if walking is None:
        turn = find_struct(common, DEFAULT_TURN_DATA)
        scalars = {field: turn[key(field)] for field in WALK_SCALARS if key(field) in turn}
        return flatten(find_struct(common, "normal_walk_data"), "walk/forward/"), scalars
    known = {key(field) for field in (*WALK_DIRECTIONS, *WALK_TURN_CLIPS, *WALK_SCALARS)}
    if not set(walking) - {0} <= known:
        raise ValueError("unknown field in an animation set walking struct")
    groups = {}
    for direction in WALK_DIRECTIONS:
        groups.update(flatten(walking[key(direction)], f"walk/{direction}/"))
    for field in WALK_TURN_CLIPS:
        if key(field) in walking:
            groups[f"walk/{field}"] = [f"{walking[key(field)]:08x}"]
    return groups, {field: walking[key(field)] for field in WALK_SCALARS if key(field) in walking}


def hand_layers(data, common, profile, profiles):
    """The strum, chord, and fret tables of a guitarist profile (`strum_anims`, `finger_anims`, `fret_anims` of
    Musician_Profiles). `data` is the QB file of the profile's animation set."""
    layers = {}
    styles = ["Normal", "Mute", "bad", "Bass_Normal", "Bass_Pick", "Bass_Slap"] + [
        "Normal" + p[key("Name")] for p in profiles
    ]
    strum_sets = {
        style.lower(): found
        for style in styles
        for source in [data, common]
        if (found := find_struct(source, style + "_Strums")) is not None
    }
    strum_sets["default"] = next(
        found for style, found in strum_sets.items() if key(style) == profile[key("strum_anims")]
    )
    for style, found in strum_sets.items():
        for field, refs in flatten(found).items():
            layers[f"strum/{style}/{field}"] = refs
    fingers = find_struct(data, profile[key("finger_anims")])
    for mask in range(CHORD_MASKS):
        colors = [color for i, color in enumerate(FRET_COLORS) if mask & (1 << i)][:MAX_CHORD_COLORS]
        ref = fingers.get(key("_".join(colors) or "none"), fingers.get(key("none")))
        if ref:
            layers[f"chord/{mask}"] = [f"{ref:08x}"]
    frets = find_struct(common, profile[key("fret_anims")])
    for pitch in FRET_PITCHES:
        if key(f"track_{pitch}") in frets:
            layers[f"fret/{pitch}"] = [f"{frets[key(f'track_{pitch}')]:08x}"]
    return layers


def write_clip(path, data, rig, source_rig=None, rotations_only=False):
    names, parents, positions, rotations = rig
    source_names, _, source_positions, source_rotations = source_rig or rig
    clip = animation(data, len(source_names))
    duration = max(clip[0], MIN_CLIP_SECONDS)
    frames = max(2, round(duration * runtime.CLIP_SAMPLE_RATE) + 1)
    p, q = sample_animation(clip, source_positions, source_rotations, np.linspace(0, duration, frames))
    mapping = [
        (i, names.index(source_names[i])) for i in sorted(clip[1].keys() | clip[2].keys()) if source_names[i] in names
    ]
    if not mapping:
        raise ValueError("Animation has no matching bones")
    source_indices = [a for a, _ in mapping]
    distance = float(np.linalg.norm(p[-1, 0, [0, 2]] - p[0, 0, [0, 2]]))
    if source_rig is not None:
        for source_index, target in mapping:
            p[:, source_index] += positions[target] - source_positions[source_index]
            inverse = source_rotations[source_index] * [-1, -1, -1, 1]
            q[:, source_index] = multiply_quaternions(
                multiply_quaternions(q[:, source_index], inverse), rotations[target]
            )
    packed = np.concatenate((p[:, source_indices] * [1, 1, -1], q[:, source_indices] * [1, 1, -1, 1]), axis=2)
    if not np.isfinite(packed).all():
        raise ValueError("Non-finite direction clip")
    tracks = [
        (
            target,
            (runtime.TRACK_TRANSLATION if source_index in clip[2] and not rotations_only else 0)
            | (runtime.TRACK_ROTATION if source_index in clip[1] else 0),
        )
        for source_index, target in mapping
    ]
    path.parent.mkdir(parents=True, exist_ok=True)
    with (
        path.open("wb") as raw,
        gzip.GzipFile(fileobj=raw, mode="wb", mtime=0, compresslevel=CLIP_GZIP_LEVEL) as stream,
    ):
        stream.write(runtime.write_clip(len(names), duration, tracks, packed))
    return {"duration": duration, "frames": frames, "rootDistance": distance}


def _animation_constants(common):
    constants = {n: scalar(common, n, d) for n, d in ANIMATION_CONSTANTS}
    for table in ["drummer_left_arm_twist_factors", "drummer_right_arm_twist_factors"]:
        for pitch in DRUM_PITCHES:
            factor = find_struct(common, table).get(key(f"track_{pitch}"))
            if factor is not None:
                constants[f"{table}/{pitch}"] = factor
    return constants


def _waypoints(nodes):
    """{node checksum: [x, y, z, yaw in degrees]} of the Budokan nodes with a position (runtime coordinates)."""
    return {
        f"{node[key('Name')]:08x}": [
            *np.asarray(node[key("Pos")]) * [1, 1, -1],
            -node.get(key("Angles"), [0, 0, 0])[1] * 180 / np.pi,
        ]
        for node in nodes
        if key("Name") in node and key("Pos") in node
    }


def _drummer_layers(common):
    drums = find_struct(common, "drummer_anims")
    layers = {}
    for pitch in DRUM_PITCHES:
        refs = drums.get(key(f"track_{pitch}"))
        if refs:
            layers[f"drum/{pitch}"] = [f"{ref:08x}" for ref in refs]
    for field in ["sticktap_l", "sticktap_r", "stickdown_l", "stickdown_r"]:
        layers[field] = [f"{drums[key(field)]:08x}"]
    for i, ref in enumerate(find_array(common, "cymbal_anims")):
        layers[f"cymbal/{i}"] = [f"{ref:08x}"]
    # Difference clip of applydifference in drummer_static_tree; its translations are deltas, not poses.
    layers["bodytwist"] = [f"{key('Test_Drum_BodyTwist_D'):08x}"]
    return layers


def _member_clips(output, role, rig, groups, layers, bank, walking):
    """Write the clips of a member's groups and layers; returns (clips, missing references).

    `walking`: the clips of normal_walk_data (made on Judy's skeleton) for a member without its own walk. GH3 plays a clip
    on another skeleton with the rotations as keyed and the translations relative to that skeleton's rest pose
    (PARITY-NOTES.md, Walk clips), as write_clip samples it on `rig`.
    """
    missing = []
    needed = {ref for refs in [*groups.values(), *layers.values()] for ref in refs}
    walk_refs = {r for k, refs in groups.items() if k.startswith("walk/") for r in refs}
    clips = {}
    for ref in sorted(needed):
        if int(ref, 16) not in bank and ref in walk_refs and int(ref, 16) in walking:
            clips[ref] = write_clip(output / role / f"{ref}.bin.gz", walking[int(ref, 16)], rig)
        elif int(ref, 16) not in bank:
            missing.append(ref)
        else:
            clips[ref] = write_clip(
                output / role / f"{ref}.bin.gz",
                bank[int(ref, 16)],
                rig,
                rotations_only=ref in layers.get("bodytwist", []),
            )
    return clips, missing


def _song_facial_clips(source, output, definition, member_rigs):
    """Write the facial clips (Band_PlayFacialAnim) of every song into the singer and guitarist clip lists."""
    for pak in sorted((source.directory / "SONGS").glob("*_song.pak.xen")):
        song_id = pak.name.split("_song.")[0]
        entries = source.load(f"SONGS/{pak.name}")
        clips = {n: b for k, n, b in entries if k == key(".ska")}
        if not clips:
            continue
        for kind, _, data in entries:
            if kind != key(".qb"):
                continue
            for row in find_array(data, song_id + "_performance") or []:
                if row.get(key("scr")) != key("Band_PlayFacialAnim"):
                    continue
                params = row.get(key("params"), {})
                ref = params.get(key("anim"))
                role = "singer" if params.get(key("Name")) == key("vocalist") else "guitarist"
                if ref not in clips:
                    continue
                member = definition["members"][role]
                ref_name = f"{ref:08x}"
                member["clips"][ref_name] = write_clip(
                    output / role / f"{ref_name}.bin.gz", clips[ref], member_rigs[role], member_rigs["singer"]
                )


def convert(data_directory, output, shared=False):
    """Export the band performance library: the clips of each member, the song facial clips, and performance.json.

    shared: the library build (no Budokan cameras and waypoints).
    """
    source = Source(data_directory)
    scripts = source.scripts()
    output.mkdir(parents=True, exist_ok=True)
    if not shared:
        camera_bank = cameras(source, scripts)
        (output / "cameras.json").write_text(json.dumps(camera_bank, separators=(",", ":")) + "\n")
    global_entries = source.load("ZONES/global.pak.xen")
    rigs = {n: b for k, n, b in global_entries if k == key(".ske")}
    del global_entries
    hands = {n: b for k, n, b in source.load("PAK/perm_anims.pak.xen") if k == key(".ska")}
    walking = {n: b for k, n, b in source.load("PAK/Judy_1_anims.pak.xen") if k == key(".ska")}
    common = script(scripts, "guitar_animation_data")
    definition = {"version": 1, "members": {}, "constants": _animation_constants(common)}
    for _kind, _, data in [] if shared else source.load("ZONES/z_budokan.pak.xen"):
        if not is_qb_file(data):
            continue
        nodes = find_array(data, "z_budokan_NodeArray")
        if nodes is not None:
            definition["waypoints"] = _waypoints(nodes)
    member_rigs = {}
    profiles = find_array(script(scripts, "guitar_band_profile"), "Musician_Profiles")
    for role, rig_name, pak, qb, group, extra in MEMBERS:
        # Clips of an earlier build that the tables no longer name must not stay in the library file list.
        for old_clip in (output / role).glob("*.bin.gz"):
            old_clip.unlink()
        rig = skeleton(rigs[key(rig_name)])
        member_rigs[role] = rig
        anim_set = find_struct(script(scripts, qb), group)
        groups = flatten(without_walking(anim_set))
        layers, walking_scalars = {}, {}
        if role in ("guitarist", "bassist"):
            walk, walking_scalars = walk_groups(anim_set, common)
            groups.update(walk)
            profile = next(
                p for p in profiles if p.get(key("Skeleton")) == key(rig_name) and p.get(key("Type")) == key(role)
            )
            layers = hand_layers(script(scripts, qb), common, profile, profiles)
        if role == "drummer":
            layers = _drummer_layers(common)
        bank = {n: b for k, n, b in source.load(f"PAK/{pak}.pak.xen") if k == key(".ska")}
        for extra_pak in extra:
            bank.update({n: b for k, n, b in source.load(f"PAK/{extra_pak}.pak.xen") if k == key(".ska")})
        bank.update({n: b for n, b in hands.items() if clip_bone_count(b) == len(rig[0])})
        clips, missing = _member_clips(output, role, rig, groups, layers, bank, walking)
        for table in [groups, layers]:
            for field in list(table):
                table[field] = [ref for ref in table[field] if ref in clips]
                if not table[field]:
                    del table[field]
        definition["members"][role] = {
            "bones": [f"{n:08x}" for n in rig[0]],
            "groups": groups,
            "layers": layers,
            "clips": clips,
            "missingClips": missing,
            **({"walking": walking_scalars} if walking_scalars else {}),
        }
        print(
            f"{role}: {len(clips)} original clips, {len(groups)} state groups, {len(layers)} layers; {len(missing)} unavailable references"
        )
    _song_facial_clips(source, output, definition, member_rigs)
    definition["sources"] = source.hashes
    (output / "performance.json").write_text(json.dumps(definition, separators=(",", ":")) + "\n")
    print(
        f"Converted {sum(len(v['clips']) for v in definition['members'].values())} clips including song facial performances"
    )
    print(output)
