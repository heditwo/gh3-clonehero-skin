"""Characters with their own skeleton (character.json skeleton and face; docs/PACKAGE-FORMAT.md section 9.6).

The humanoid roles (VRM 1.0 names) and the GH3 bone of each one, the checks of the map and the rest pose (the same
rules as src/Packages/CharacterSkeleton.cs), an automatic map from bone names (MMD in Japanese and English, VRM and VRoid,
Mixamo, Unreal mannequin), and the retarget of GH3 clips to the skeleton (the algorithm of
src/Stage/Band/CharacterRetarget.cs, vectorized over frames) for the clip preview of the Blender add-on.

numpy only and relative imports, so the Blender add-on bundles it (blender/build.py). Runtime coordinates (the mod's
Unity space): glTF with z negated for positions and x, y negated for rotations; quaternions are [x, y, z, w].
"""

import re
import unicodedata

import numpy as np

from .profiles import gh3_key

FACE_SLOTS = ("blink", "a", "i", "u", "e", "o")
MAX_FOLLOWERS = 128
REQUIRED = (
    "hips",
    "spine",
    "head",
    "leftUpperArm",
    "leftLowerArm",
    "leftHand",
    "rightUpperArm",
    "rightLowerArm",
    "rightHand",
    "leftUpperLeg",
    "leftLowerLeg",
    "leftFoot",
    "rightUpperLeg",
    "rightLowerLeg",
    "rightFoot",
)
SPINE = ("spine", "chest", "upperChest")
TWIST = ("leftUpperArmTwist", "leftLowerArmTwist", "rightUpperArmTwist", "rightLowerArmTwist")
FINGERS = ("Thumb", "Index", "Middle", "Ring", "Little")
KNUCKLES = ("Middle", "Ring", "Index", "Little")
THUMB_PARTS = ("Metacarpal", "Proximal", "Distal")
FINGER_PARTS = ("Proximal", "Intermediate", "Distal")
GH3_FINGERS = {
    "leftThumb": ("9b410faa", "e139af08", "b28d0e8b"),
    "leftIndex": ("7f8c4cd6", "22b5d884", "71017907"),
    "leftMiddle": ("68ec1dda", "f55a971a", "a6ee3699"),
    "leftRing": ("d3dd2e3d", "cf2e73af", "9c9ad22c"),
    "leftLittle": ("0fd44a48", "1047c981", "43f36802"),
    "rightThumb": ("614e32c9", "1b36926b", "488233e8"),
    "rightIndex": ("858371b5", "d8bae5e7", "8b0e4464"),
    "rightMiddle": ("92e320b9", "0f55aa79", "5ce10bfa"),
    "rightRing": ("29d2135e", "35214ecc", "6695ef4f"),
    "rightLittle": ("f5db772b", "ea48f4e2", "b9fc5561"),
}


def _key(name):
    return f"{gh3_key(name):08x}"


def _table():
    table = {
        "hips": _key("bone_pelvis"),
        "neck": _key("bone_neck"),
        "head": _key("bone_head"),
        "jaw": _key("bone_jaw"),
        "leftEye": _key("bone_eye_l"),
        "rightEye": _key("bone_eye_r"),
    }
    for side, s in (("left", "l"), ("right", "r")):
        for role, bone in (
            ("Shoulder", "collar"),
            ("UpperArm", "bicep"),
            ("LowerArm", "forearm"),
            ("Hand", "palm"),
            ("UpperLeg", "thigh"),
            ("LowerLeg", "knee"),
            ("Foot", "ankle"),
            ("Toes", "toe"),
        ):
            table[side + role] = _key(f"bone_{bone}_{s}")
        for finger in FINGERS:
            parts = THUMB_PARTS if finger == "Thumb" else FINGER_PARTS
            for k, part in enumerate(parts):
                table[side + finger + part] = GH3_FINGERS[side + finger][k]
    return table


# Role to the GH3 bone checksum (without the spine roles; spine_sources chooses them).
GH3 = _table()
ROLES = tuple(GH3) + SPINE + TWIST


def spine_sources(count):
    """The GH3 spine bones for the spine roles that a skeleton has, bottom to top."""
    if count == 1:
        return [_key("bone_stomach_upper")]
    if count == 2:
        return [_key("bone_stomach_lower"), _key("bone_chest")]
    return [_key("bone_stomach_lower"), _key("bone_stomach_upper"), _key("bone_chest")]


def next_role(role, has):
    """The role whose joint gives the direction of a role's bone, or None."""
    if role in SPINE:
        for other in SPINE[SPINE.index(role) + 1 :]:
            if has(other):
                return other
        return "neck" if has("neck") else "head"
    if role == "neck":
        return "head"
    for side in ("left", "right"):
        if not role.startswith(side):
            continue
        part = role[len(side) :]
        following = {
            "Shoulder": "UpperArm",
            "UpperArm": "LowerArm",
            "LowerArm": "Hand",
            "UpperLeg": "LowerLeg",
            "LowerLeg": "Foot",
            "Foot": "Toes",
            "ThumbMetacarpal": "ThumbProximal",
            "ThumbProximal": "ThumbDistal",
        }.get(part)
        if part == "Hand":
            return knuckle(side, has)
        for finger in FINGERS[1:]:
            if part == finger + "Proximal":
                following = finger + "Intermediate"
            elif part == finger + "Intermediate":
                following = finger + "Distal"
        return side + following if following else None
    return None


def knuckle(side, has):
    """The finger base that gives the direction of a hand: the middle finger, else the ring, index, or little finger."""
    return next((side + f + "Proximal" for f in KNUCKLES if has(side + f + "Proximal")), None)


def palm_line(side, has):
    """The two finger bases across the palm (index side, little side) for the roll of a hand, or None."""
    first = next((side + f + "Proximal" for f in ("Index", "Middle") if has(side + f + "Proximal")), None)
    last = next((side + f + "Proximal" for f in ("Little", "Ring") if has(side + f + "Proximal")), None)
    return (first, last) if first and last and first != last else None


def torso_between(parents, humanoid, rest_p):
    """Joints without a role between two torso roles (hips, spine, chest, upper chest, neck, head), as (joint, upper
    role joint, lower role joint, fraction of the way down to up): the retarget turns them part of the way, so a long
    spine or neck bends along its whole length."""
    torso = [humanoid[r] for r in ("hips",) + SPINE + ("neck", "head") if r in humanoid]
    out = []
    for lower in torso:
        path, x = [], parents[lower]
        while x >= 0 and x not in torso:
            path.append(x)
            x = parents[x]
        if x < 0 or not path:
            continue
        points = [rest_p[x]] + [rest_p[b] for b in reversed(path)] + [rest_p[lower]]
        steps = np.cumsum([0.0] + [float(np.linalg.norm(points[i + 1] - points[i])) for i in range(len(points) - 1)])
        total = steps[-1] if steps[-1] > 1e-9 else 1.0
        for k, bone in enumerate(reversed(path)):
            out.append((bone, x, lower, steps[k + 1] / total))
    return out


def hierarchy():
    """Role pairs where the first joint must be a parent (at any depth) of the second."""
    pairs = [("hips", "leftUpperLeg"), ("hips", "rightUpperLeg"), ("neck", "head")]
    pairs += [
        (top, below)
        for top in SPINE
        for below in ("neck", "head", "leftShoulder", "rightShoulder", "leftUpperArm", "rightUpperArm")
    ]
    pairs += [
        ("spine", "chest"),
        ("spine", "upperChest"),
        ("chest", "upperChest"),
        ("head", "jaw"),
        ("head", "leftEye"),
        ("head", "rightEye"),
    ]
    for side in ("left", "right"):
        chain = ("Shoulder", "UpperArm", "UpperArmTwist", "LowerArm", "LowerArmTwist", "Hand")
        pairs += [(side + chain[i], side + chain[j]) for i in range(len(chain)) for j in range(i + 1, len(chain))]
        pairs += [
            (side + "UpperLeg", side + "LowerLeg"),
            (side + "LowerLeg", side + "Foot"),
            (side + "Foot", side + "Toes"),
        ]
        for finger in FINGERS:
            parts = THUMB_PARTS if finger == "Thumb" else FINGER_PARTS
            pairs += [
                (side + "Hand", side + finger + parts[0]),
                (side + finger + parts[0], side + finger + parts[1]),
                (side + finger + parts[1], side + finger + parts[2]),
            ]
    return pairs


# Rest pose and checks.
def quaternion_matrices(q):
    x, y, z, w = (q[..., i] for i in range(4))
    return np.stack(
        [
            np.stack([1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)], -1),
            np.stack([2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)], -1),
            np.stack([2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)], -1),
        ],
        -2,
    )


def runtime_bind(local):
    """[x, y, z, qx, qy, qz, qw] in runtime coordinates of glTF local matrices (..., 4, 4); the scale is removed."""
    from .character import matrix_quaternions

    local = np.asarray(local, dtype=np.float64)
    rotation = local[..., :3, :3] / np.linalg.norm(local[..., :3, :3], axis=-2, keepdims=True)
    q = matrix_quaternions(rotation) * [-1, -1, 1, 1]
    return np.concatenate([local[..., :3, 3] * [1, 1, -1], q], -1)


def world_positions(parents, bind):
    """Runtime world positions and rotations of a rest pose (bind rows [x, y, z, qx, qy, qz, qw])."""
    bind = np.asarray(bind, dtype=np.float64)
    count = len(parents)
    position, rotation, done = np.zeros((count, 3)), np.zeros((count, 4)), [False] * count

    def visit(bone, depth=0):
        if done[bone]:
            return
        if depth > count:
            raise ValueError("the skeleton has a cycle")
        local_p, local_q = bind[bone, :3], bind[bone, 3:] / np.linalg.norm(bind[bone, 3:])
        parent = parents[bone]
        if parent >= 0:
            visit(parent, depth + 1)
            local_p = position[parent] + qrotate(rotation[parent], local_p)
            local_q = qmul(rotation[parent], local_q)
        position[bone], rotation[bone], done[bone] = local_p, local_q, True

    for bone in range(count):
        visit(bone)
    return position, rotation


def check_map(names, parents, bind, humanoid, followers=(), instrument=None):
    """The checks of character.json skeleton on a skeleton (joint names, parent indices, runtime bind rows). humanoid:
    {role: joint name}; followers: [{bone, source, weight}]; instrument: {bone, offset} or None. Returns
    (errors, warnings, chain_bones): the joint names that can be chain joints."""
    errors, warnings = [], []
    index = {name: i for i, name in enumerate(names)}
    keys = {_key(name) if not re.fullmatch(r"bone-[0-9a-f]{8}", name) else name[5:]: i for i, name in enumerate(names)}

    def joint(name):
        if name in index:
            return index[name]
        key = name[5:] if re.fullmatch(r"bone-[0-9a-f]{8}", name) else _key(name)
        return keys.get(key, -1)

    mapped = {}
    for role, name in humanoid.items():
        if role not in ROLES:
            errors.append(f"skeleton.humanoid: unknown role '{role}'")
            continue
        bone = joint(name)
        if bone < 0:
            errors.append(f"skeleton.humanoid.{role}: the skeleton has no joint '{name}'")
            continue
        if bone in mapped.values():
            errors.append(f"skeleton.humanoid: joint '{name}' has two roles")
            continue
        mapped[role] = bone
    missing = [role for role in REQUIRED if role not in humanoid]
    if missing:
        errors.append(f"skeleton.humanoid needs the roles {', '.join(missing)}")
    if errors:
        return errors, warnings, set()

    def above(a, b):
        parent = parents[b]
        while parent >= 0:
            if parent == a:
                return True
            parent = parents[parent]
        return False

    for a, b in hierarchy():
        if a in mapped and b in mapped and not above(mapped[a], mapped[b]):
            errors.append(
                f"skeleton.humanoid: {a} ('{names[mapped[a]]}') must be a parent of {b} ('{names[mapped[b]]}')"
            )
    finger_roles = [r for r in GH3 if any(part in r for part in ("Metacarpal", "Proximal", "Intermediate", "Distal"))]
    count = sum(1 for r in finger_roles if r in mapped)
    if count < len(finger_roles):
        warnings.append(
            f"the skeleton maps {count} of {len(finger_roles)} finger bones; unmapped fingers keep their rest pose"
        )
    try:
        position, _ = world_positions(parents, bind)
    except ValueError as error:
        return errors + [str(error)], warnings, set()

    def p(role):
        return position[mapped[role]]

    feet = min(p("leftFoot")[1], p("rightFoot")[1])
    height = p("head")[1] - feet
    if not 0.2 < height < 5:
        errors.append(f"the rest pose is not upright: the head is {height:.2f} m above the feet (export with +Y up)")
    elif not feet < p("hips")[1] < p("head")[1]:
        errors.append("the rest pose is not upright: the hips must be between the feet and the head")
    if p("leftUpperArm")[0] <= p("rightUpperArm")[0] or p("leftUpperLeg")[0] <= p("rightUpperLeg")[0]:
        errors.append(
            "the left and right roles are swapped: the character's left side must be on +X in the GLB (the model faces +Z in glTF)"
        )
    if (
        "leftToes" in mapped
        and "rightToes" in mapped
        and p("leftToes")[2] + p("rightToes")[2] > p("leftFoot")[2] + p("rightFoot")[2]
    ):
        errors.append("the toes are behind the feet: the model must face +Z in glTF (Blender: -Y)")
    for side in ("left", "right"):
        arm = p(side + "Hand") - p(side + "UpperArm")
        below = float(np.degrees(np.arcsin(np.clip(-arm[1] / max(np.linalg.norm(arm), 1e-9), -1, 1))))
        if not -10 <= below <= 80:
            warnings.append(
                f"the {side} arm points {below:.0f}° below the horizontal at rest; a T-pose or an A-pose works best"
            )
    follower_bones = set()
    if len(followers) > MAX_FOLLOWERS:
        errors.append(f"skeleton.followers has more than {MAX_FOLLOWERS} rows")
    sources = {}
    for row in followers:
        bone, source = joint(row["bone"]), joint(row["source"])
        if bone < 0 or source < 0:
            errors.append(
                f"skeleton.followers: the skeleton has no joint '{row['bone'] if bone < 0 else row['source']}'"
            )
            continue
        if bone == source:
            errors.append(f"skeleton.followers: '{row['bone']}' follows itself")
            continue
        if bone in mapped.values():
            errors.append(f"skeleton.followers: '{row['bone']}' has a humanoid role; the retarget drives it")
            continue
        if bone in follower_bones:
            errors.append(f"skeleton.followers: '{row['bone']}' follows two bones")
            continue
        if not -4 <= row["weight"] <= 4:
            errors.append(f"skeleton.followers: the weight of '{row['bone']}' must be in -4 to 4")
        follower_bones.add(bone)
        sources[bone] = source
    for start in sources:
        bone, steps = start, 0
        while bone in sources and steps <= len(sources):
            bone, steps = sources[bone], steps + 1
            if bone == start:
                errors.append("skeleton.followers has a cycle")
                break
    if instrument:
        if "bone" in instrument and joint(instrument["bone"]) < 0:
            errors.append(f"skeleton.instrument: the skeleton has no joint '{instrument['bone']}'")
        if np.linalg.norm(instrument.get("offset", [0, 0, 0])) > 1:
            errors.append("skeleton.instrument offset must be at most 1 m")
        if "scale" in instrument and not 0.5 <= instrument["scale"] <= 1.5:
            errors.append("skeleton.instrument scale must be in 0.5 to 1.5")
    blocked = set(mapped.values()) | follower_bones
    for bone in mapped.values():
        parent = parents[bone]
        while parent >= 0:
            blocked.add(parent)
            parent = parents[parent]
    return errors, warnings, {names[i] for i in range(len(names)) if i not in blocked}


def glb_skeleton(glb):
    """The skeleton of a skinned GLB: joint names, parent joint indices, and runtime bind rows."""
    nodes = glb.list("nodes")
    joints = glb.list("skins")[0]["joints"]
    parents_of = glb.parents()
    names = [nodes[j].get("name", "") for j in joints]
    parents = [joints.index(parents_of[j]) if parents_of.get(j) in joints else -1 for j in joints]
    local = np.array([glb.node_matrix(nodes[j]) for j in joints])
    return names, parents, runtime_bind(local), local


def morph_names(glb):
    """The morph target names of the GLB meshes (mesh extras targetNames, as Blender writes them)."""
    found = set()
    for mesh in glb.list("meshes"):
        names = (mesh.get("extras") or {}).get("targetNames", [])
        count = max((len(p.get("targets", [])) for p in mesh.get("primitives", [])), default=0)
        found.update(names[:count])
    return found


# Automatic map from bone names.
_FULL = str.maketrans("０１２３４５６７８９", "0123456789")
_SIDE_PATTERNS = (
    (re.compile(r"^(.*)[._ -]([lr])$"), 2),
    (re.compile(r"^([lr])[._ -](.*)$"), 1),
    (re.compile(r"^(left|right)(.*)$"), 1),
    (re.compile(r"^(.*)(left|right)$"), 2),
    (re.compile(r"^([左右])(.*)$"), 1),
    (re.compile(r"^(.*)_([lr])_(.*)$"), 2),
)
_SKIP = re.compile(
    r"(^|[^a-z])(ik|ctrl|control|helper|dummy|shadow|end|tip|nub|null|root|mch|org|wgt)([^a-z]|$)|ＩＫ|ik$|ik[._]|親(?!指)|先$|キャンセル|補助|^\+|_dummy_|_shadow_|操作"
)


def _split(name):
    """(side 'l', 'r', or '', base) of a bone name: lower case, ASCII digits, no prefixes such as 'mixamorig:'."""
    text = unicodedata.normalize("NFKC", name).translate(_FULL).lower().strip()
    text = text.split(":")[-1].split("|")[-1]
    text = re.sub(r"^(j_bip_|j_sec_|j_adj_|bip0?1[ _]|def[-_]|orgs?[-_])", "", text)
    for pattern, group in _SIDE_PATTERNS:
        match = pattern.match(text)
        if not match:
            continue
        side = match.group(group)
        side = "l" if side in ("l", "left", "左") else "r" if side in ("r", "right", "右") else ""
        if not side:
            continue
        rest = "".join(g for i, g in enumerate(match.groups(), 1) if i != group)
        return side, re.sub(r"[ _.\-]", "", rest)
    text = re.sub(r"^c_", "", text)
    return "", re.sub(r"[ _.\-]", "", text)


_CENTRE = {
    "hips": ("hips", "pelvis", "下半身", "lowerbody", "hip"),
    "neck": ("neck", "neck01", "首"),
    "head": ("head", "頭"),
    "jaw": ("jaw",),
}
_SIDED = {
    "Shoulder": ("shoulder", "clavicle", "肩", "collar"),
    "UpperArm": ("upperarm", "arm", "腕"),
    "UpperArmTwist": ("armtwist", "腕捩", "upperarmtwist01", "upperarmtwist", "shouldertwist"),
    "LowerArm": ("lowerarm", "forearm", "elbow", "ひじ", "肘"),
    "LowerArmTwist": ("wristtwist", "手捩", "lowerarmtwist01", "forearmtwist", "lowerarmtwist", "handtwist"),
    "Hand": ("hand", "wrist", "手首"),
    "UpperLeg": ("upperleg", "thigh", "upleg", "leg", "足", "legd", "足d"),
    "LowerLeg": ("lowerleg", "calf", "knee", "shin", "ひざ", "膝", "kneed", "ひざd"),
    "Foot": ("foot", "ankle", "足首", "ankled", "足首d"),
    "Toes": ("toes", "toe", "toebase", "ball", "足先ex", "つま先", "toeex"),
    "Eye": ("eye", "目"),
}
_FINGER_NAMES = {
    "Thumb": ("thumb", "親指", "handthumb"),
    "Index": ("index", "人指", "人差指", "fore", "handindex", "indexfinger"),
    "Middle": ("middle", "中指", "handmiddle", "mid"),
    "Ring": ("ring", "薬指", "third", "handring"),
    "Little": ("little", "pinky", "小指", "handpinky", "pinkie"),
}
_DEFORM_VARIANTS = {"UpperLeg": ("足d", "legd"), "LowerLeg": ("ひざd", "kneed"), "Foot": ("足首d", "ankled")}


def auto_map(names, parents, deform=None, spine_hint=None):
    """A humanoid map {role: bone name} from the bone names of a skeleton (MMD Japanese and English, VRM and VRoid,
    Mixamo, Unreal mannequin). deform: the bones that have vertex weights (MMD 'D' leg bones win over the IK chain
    bones when they have the weights). The result can miss roles; check it with check_map."""
    deform = set(names if deform is None else deform)
    parts = [_split(name) for name in names]
    usable = [
        not _SKIP.search(unicodedata.normalize("NFKC", name).lower())
        or bool(re.search(r"足先ex|つま先", unicodedata.normalize("NFKC", name).lower()))
        for name in names
    ]
    mixamo = any(base == "upleg" for _, base in parts)
    result = {}

    def find(side, bases, prefer_deform=True):
        found = [i for i, (s, b) in enumerate(parts) if s == side and b in bases and usable[i]]
        if not found:
            return None
        found.sort(key=lambda i: (bases.index(parts[i][1]), names[i] not in deform if prefer_deform else 0, i))
        return names[found[0]]

    for role, bases in _CENTRE.items():
        hit = find("", bases)
        if hit:
            result[role] = hit
    for side, label in (("l", "left"), ("r", "right")):
        for part, bases in _SIDED.items():
            if part == "Eye":
                hit = find(side, bases)
                if hit:
                    result[label + "Eye"] = hit
                continue
            bases = tuple(bases)
            if mixamo and part == "UpperLeg":
                bases = ("upleg", "upperleg", "thigh")
            if mixamo and part == "LowerLeg":
                bases = ("leg", "lowerleg", "calf", "knee")
            variants = _DEFORM_VARIANTS.get(part, ())
            hit = None
            for variant in variants:
                candidate = find(side, (variant,))
                if candidate and candidate in deform:
                    hit = candidate
                    break
            hit = hit or find(side, bases)
            if hit:
                result[label + part] = hit
        for finger, bases in _FINGER_NAMES.items():
            numbered = {}
            for i, (s, base) in enumerate(parts):
                if s != side or not usable[i]:
                    continue
                match = re.fullmatch(r"(.*?)(\d+)", base)
                if not match:
                    continue
                stem, number = (
                    match.group(1).rstrip("0")
                    if match.group(1).endswith("0") and len(match.group(2)) == 1
                    else match.group(1),
                    int(match.group(2)),
                )
                if stem in bases or stem.removesuffix("finger") in bases:
                    numbered.setdefault(number, names[i])
            if not numbered:
                continue
            ordered = [numbered[n] for n in sorted(numbered)][:3]
            roles = THUMB_PARTS if finger == "Thumb" else FINGER_PARTS
            if finger == "Thumb" and len(ordered) == 2:
                roles = THUMB_PARTS[1:]
            if finger != "Thumb" and len(ordered) == 2:
                roles = FINGER_PARTS[:2]
            for role, bone in zip(roles, ordered):
                result[label + finger + role] = bone
    # The spine: the spine bones between the hips (or their parent) and the neck, bottom to top.
    neck = names.index(result["neck"]) if "neck" in result else names.index(result["head"]) if "head" in result else -1
    chain = []
    if neck >= 0:
        parent = parents[neck]
        while parent >= 0:
            chain.append(parent)
            parent = parents[parent]
        chain.reverse()
        spine_like = [
            i for i in chain if usable[i] and re.search(r"spine|chest|上半身|upperbody|torso|^waist$", parts[i][1])
        ]
        hips = names.index(result["hips"]) if "hips" in result else -1
        spine_like = [i for i in spine_like if i != hips]
        if len(spine_like) > 3:
            spine_like = [spine_like[0], spine_like[len(spine_like) // 2], spine_like[-1]]
        for role, i in zip(
            SPINE if len(spine_like) == 3 else ("spine", "chest") if len(spine_like) == 2 else ("spine",), spine_like
        ):
            result[role] = names[i]
    return result


def mmd_followers(rows, names, humanoid):
    """skeleton.followers from MMD append rotations (the 付与親 of MMD Tools: rows of (bone, source, influence)), for the
    exported joints, without the bones that have a humanoid role."""
    exported, mapped = set(names), set(humanoid.values())
    return [
        {"bone": bone, "source": source, "weight": round(float(weight), 6)}
        for bone, source, weight in rows
        if bone in exported and source in exported and bone not in mapped and weight != 0
    ]


FACE_NAMES = {
    "blink": ("まばたき", "blink", "Blink", "Fcl_EYE_Close", "eyeBlink"),
    "a": ("あ", "A", "a", "aa", "Fcl_MTH_A", "vrc.v_aa", "mouth_a"),
    "i": ("い", "I", "i", "ih", "Fcl_MTH_I", "vrc.v_ih", "mouth_i"),
    "u": ("う", "U", "u", "ou", "Fcl_MTH_U", "vrc.v_ou", "mouth_u"),
    "e": ("え", "E", "e", "ee", "Fcl_MTH_E", "vrc.v_e", "mouth_e"),
    "o": ("お", "O", "o", "oh", "Fcl_MTH_O", "vrc.v_oh", "mouth_o"),
}


def _face_key(name):
    """A shape key name without the mesh prefix (glTF 'Face_Blendshape.Fcl_MTH_A'), in lower case."""
    return (
        unicodedata.normalize("NFKC", name).split(".")[-1].lower()
        if "." in name and not name.lower().startswith("vrc.")
        else unicodedata.normalize("NFKC", name).lower()
    )


_FACE_FUZZY = {
    "blink": (
        "fcl_eye_close",
        "eye_close",
        "eyeclose",
        "eyes_closed",
        "eyesclosed",
        "eye_closed",
        "blink",
        "eyeblink",
        "close_eyes",
        "mabataki",
        "blink_l",
        "eyeblinkleft",
    ),
    "a": (
        "fcl_mth_a",
        "mth_a",
        "mouth_a",
        "lip_a",
        "v_aa",
        "vrc.v_aa",
        "aa",
        "mouthopen",
        "mouth_open",
        "jawopen",
        "jaw_open",
        "mouse_open",
    ),
    "i": ("fcl_mth_i", "mth_i", "mouth_i", "lip_i", "v_ih", "vrc.v_ih", "ih"),
    "u": ("fcl_mth_u", "mth_u", "mouth_u", "lip_u", "v_ou", "vrc.v_ou", "ou"),
    "e": ("fcl_mth_e", "mth_e", "mouth_e", "lip_e", "v_e", "vrc.v_e", "ee"),
    "o": ("fcl_mth_o", "mth_o", "mouth_o", "lip_o", "v_oh", "vrc.v_oh", "oh"),
}


def auto_face(shape_keys):
    """character.json face from shape key names (MMD, VRM and VRoid, VRChat visemes, ARKit and Ready Player Me, and
    names with a mesh prefix)."""
    keys = list(shape_keys)
    result = {}
    for slot, candidates in FACE_NAMES.items():
        hit = next((name for name in candidates if name in keys), None)
        if hit is None:
            lower = {k.lower(): k for k in keys}
            hit = next((lower[c.lower()] for c in candidates if c.lower() in lower), None)
        if hit is None:
            short = {}
            for k in keys:
                short.setdefault(_face_key(k), k)
            hit = next((short[c] for c in _FACE_FUZZY[slot] if c in short), None)
        if hit:
            result[slot] = hit
    return result


# Quaternions [x, y, z, w], vectorized over leading axes.
def qmul(a, b):
    ax, ay, az, aw = np.moveaxis(np.asarray(a, dtype=np.float64), -1, 0)
    bx, by, bz, bw = np.moveaxis(np.asarray(b, dtype=np.float64), -1, 0)
    return np.stack(
        [
            aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz,
        ],
        -1,
    )


def qinv(q):
    return np.asarray(q) * [-1, -1, -1, 1] / np.sum(np.asarray(q) ** 2, -1, keepdims=True)


def qnorm(q):
    return q / np.linalg.norm(q, axis=-1, keepdims=True)


def qrotate(q, v):
    q = np.asarray(q, dtype=np.float64)
    v = np.asarray(v, dtype=np.float64)
    u, w = q[..., :3], q[..., 3:4]
    t = 2 * np.cross(u, v)
    return v + w * t + np.cross(u, t)


def from_to(a, b):
    a = a / np.maximum(np.linalg.norm(a, axis=-1, keepdims=True), 1e-12)
    b = b / np.maximum(np.linalg.norm(b, axis=-1, keepdims=True), 1e-12)
    dot = np.sum(a * b, -1, keepdims=True)
    q = np.concatenate([np.cross(a, b), 1 + dot], -1)
    opposite = dot[..., 0] < -0.999999
    if np.any(opposite):
        other = np.where((np.abs(a[..., 0:1]) < 0.9), [[1.0, 0, 0]], [[0, 1.0, 0]])
        axis = np.cross(other, a)
        axis /= np.maximum(np.linalg.norm(axis, axis=-1, keepdims=True), 1e-12)
        q = np.where(opposite[..., None], np.concatenate([axis, np.zeros(axis.shape[:-1] + (1,))], -1), q)
    return qnorm(q)


def twist_about(q, axis):
    projection = axis * np.sum(q[..., :3] * axis, -1, keepdims=True)
    twist = np.concatenate([projection, q[..., 3:4]], -1)
    small = np.sum(twist**2, -1) < 1e-12
    twist = np.where(small[..., None], [0, 0, 0, 1.0], twist)
    return qnorm(twist)


def power(q, t):
    q = np.where(q[..., 3:4] < 0, -q, q)
    s = np.linalg.norm(q[..., :3], axis=-1, keepdims=True)
    angle = 2 * np.arctan2(s, q[..., 3:4]) * t
    scale = np.where(s < 1e-7, 0, np.sin(angle / 2) / np.maximum(s, 1e-12))
    return np.concatenate([q[..., :3] * scale, np.where(s < 1e-7, 1, np.cos(angle / 2))], -1)


def slerp_identity(q, t):
    return power(q, t)


def _order(parents, after=()):
    count = len(parents)
    waits, following = [0] * count, [[] for _ in range(count)]
    for i, parent in enumerate(parents):
        if parent >= 0:
            waits[i] += 1
            following[parent].append(i)
    for bone, source in after:
        waits[bone] += 1
        following[source].append(bone)
    queue, order = [i for i in range(count) if waits[i] == 0], []
    while queue:
        bone = queue.pop(0)
        order.append(bone)
        for child in following[bone]:
            waits[child] -= 1
            if waits[child] == 0:
                queue.append(child)
    if len(order) != count:
        raise ValueError("the skeleton has a cycle (parents or followers)")
    return order


def _above(parents, a, b):
    parent = parents[b]
    while parent >= 0:
        if parent == a:
            return True
        parent = parents[parent]
    return False


class Retarget:
    """The retarget of src/Stage/Band/CharacterRetarget.cs for whole clips (frames on the first axis). Source: GH3 rig
    bone checksums, parents, bind rows; target: joint names, parents, bind rows, humanoid {role: joint index},
    followers [(bone, source, weight)], the instrument bone (or -1) and offset, and the band role."""

    def __init__(
        self,
        source_bones,
        source_parents,
        source_bind,
        names,
        parents,
        bind,
        humanoid,
        followers=(),
        instrument_bone=-1,
        instrument_offset=(0, 0, 0),
        role="guitarist",
        instrument_scale=0.0,
    ):
        self.source_parents, self.parents = list(source_parents), list(parents)
        self.source_bind, self.bind = np.asarray(source_bind, dtype=np.float64), np.asarray(bind, dtype=np.float64)
        self.source_order = _order(self.source_parents)
        source_index = {bone: i for i, bone in enumerate(source_bones)}
        sp, sq = world_positions(self.source_parents, self.source_bind)
        tp, tq = world_positions(self.parents, self.bind)
        self.source_rest_p, self.rest_p, self.rest_q = sp, tp, tq
        roles = {role2: source_index.get(checksum, -1) for role2, checksum in GH3.items()}
        spine = [r for r in SPINE if r in humanoid]
        for r, checksum in zip(spine, spine_sources(len(spine))):
            roles[r] = source_index.get(checksum, -1)
        count = len(names)
        self.map = [-1] * count
        for r, bone in humanoid.items():
            if roles.get(r, -1) >= 0 and 0 <= bone < count:
                self.map[bone] = roles[r]

        def t(r):
            return humanoid.get(r, -1)

        def s(r):
            return roles.get(r, -1)

        align, aligned = np.tile([0, 0, 0, 1.0], (count, 1)), [False] * count
        for r, bone in humanoid.items():
            if self.map[bone] < 0:
                continue
            following = next_role(r, lambda x: t(x) >= 0 and s(x) >= 0)
            if following is None or t(following) < 0 or s(following) < 0:
                continue
            a, b = tp[t(following)] - tp[bone], sp[s(following)] - sp[self.map[bone]]
            if np.dot(a, a) < 1e-10 or np.dot(b, b) < 1e-10:
                continue
            line = palm_line(r[:-4], lambda x: t(x) >= 0 and s(x) >= 0) if r.endswith("Hand") else None
            if line:
                ti, tl, si, sl = t(line[0]), t(line[1]), s(line[0]), s(line[1])
                if min(ti, tl, si, sl) >= 0:
                    align[bone], aligned[bone] = _frame_turn(a, tp[ti] - tp[tl], b, sp[si] - sp[sl]), True
                    continue
            align[bone], aligned[bone] = from_to(a, b), True
        self.correction = np.tile([0, 0, 0, 1.0], (count, 1))
        keeps = {"hips", "head", "jaw", "leftEye", "rightEye"}
        role_of = {bone: r for r, bone in humanoid.items()}
        for bone in _order(self.parents):
            if self.map[bone] < 0:
                continue
            if not aligned[bone]:
                if role_of[bone] not in keeps:
                    parent = self.parents[bone]
                    while parent >= 0:
                        if aligned[parent]:
                            align[bone] = align[parent]
                            break
                        parent = self.parents[parent]
                aligned[bone] = True
            self.correction[bone] = qnorm(qmul(qmul(qinv(sq[self.map[bone]]), align[bone]), tq[bone]))
        group = [b for b in (t("hips"), t("spine"), t("leftUpperLeg"), t("rightUpperLeg")) if b >= 0]
        self.root_motion = -1
        if group:
            common = group[0]
            for other in group[1:]:
                x = common
                while x >= 0 and not (x == other or _above(self.parents, x, other)):
                    x = self.parents[x]
                common = x if x >= 0 else common
            self.root_motion = common
        self.source_pelvis = s("hips")
        target_hip = (tp[t("leftUpperLeg")][1] + tp[t("rightUpperLeg")][1]) / 2
        source_hip = (sp[s("leftUpperLeg")][1] + sp[s("rightUpperLeg")][1]) / 2
        self.hip_scale = target_hip / source_hip if target_hip > 0.05 and source_hip > 0.05 else 1.0
        top = spine[-1] if spine else "spine"
        self.chest_target = instrument_bone if instrument_bone >= 0 else t(top)
        self.chest_source = source_index.get(_key("bone_chest"), -1)
        neck_t = t("neck") if t("neck") >= 0 else t("head")
        neck_s = s("neck") if s("neck") >= 0 else s("head")
        hips_t = (tp[t("leftUpperLeg")] + tp[t("rightUpperLeg")]) / 2
        hips_s = (sp[s("leftUpperLeg")] + sp[s("rightUpperLeg")]) / 2
        torso_t, torso_s = np.linalg.norm(tp[neck_t] - hips_t), np.linalg.norm(sp[neck_s] - hips_s)
        self.torso_scale = torso_t / torso_s if torso_t > 0.05 and torso_s > 0.05 else self.hip_scale
        self.offset = np.asarray(instrument_offset, dtype=np.float64)

        def arm(positions, index, side):
            a, b, c = index(side + "UpperArm"), index(side + "LowerArm"), index(side + "Hand")
            return (
                np.linalg.norm(positions[a] - positions[b]) + np.linalg.norm(positions[b] - positions[c])
                if min(a, b, c) >= 0
                else 0.0
            )

        arm_t, arm_s = arm(tp, t, "left") + arm(tp, t, "right"), arm(sp, s, "left") + arm(sp, s, "right")
        self.instrument_scale = (
            instrument_scale
            if instrument_scale > 0
            else float(np.clip(arm_t / arm_s, 0.5, 1.5))
            if arm_t > 0.1 and arm_s > 0.1
            else 1.0
        )
        self.upper_twist, self.lower_twist = [], []
        for side in ("left", "right"):
            u, ut, lo, lt, h = (
                t(side + "UpperArm"),
                t(side + "UpperArmTwist"),
                t(side + "LowerArm"),
                t(side + "LowerArmTwist"),
                t(side + "Hand"),
            )
            if min(u, ut, lo) >= 0 and self.parents[ut] == u and _above(self.parents, ut, lo):
                self.upper_twist.append((u, ut, lo, _unit(qrotate(qinv(tq[u]), tp[lo] - tp[u]))))
            if lo >= 0 and h >= 0 and _above(self.parents, lo, h):
                axis = _unit(qrotate(qinv(tq[lo]), tp[h] - tp[lo]))
                rest = qnorm(qmul(qinv(tq[lo]), tq[h]))
                twist = lt if lt >= 0 and _above(self.parents, lo, lt) and _above(self.parents, lt, h) else -1
                self.lower_twist.append((lo, twist, h, axis, rest))
        self.between = {
            bone: (upper, lower, fraction)
            for bone, upper, lower, fraction in torso_between(self.parents, humanoid, tp)
            if self.map[bone] < 0 and self.map[upper] >= 0 and self.map[lower] >= 0
        }
        self.followers = [
            (b, src, w, qnorm(qmul(qinv(tq[b]), tq[src])))
            for b, src, w in followers
            if 0 <= b < count and 0 <= src < count and self.map[b] < 0 and b != src
        ]
        self.follower_of = {f[0]: f for f in self.followers}
        self.order = _order(self.parents, [(f[0], f[1]) for f in self.followers])
        self.arms, self.legs = [], []
        # The GH3 joints of each IK chain (by the target upper joint): the GH3 elbow or knee gives the bend direction.
        self.poles = {}
        for side, sign in (("left", -1), ("right", 1)):
            u, lo, h, sh = t(side + "UpperArm"), t(side + "LowerArm"), t(side + "Hand"), s(side + "Hand")
            if min(u, lo, h, sh) >= 0 and _above(self.parents, u, lo) and _above(self.parents, lo, h):
                base = knuckle(side, lambda x: t(x) >= 0 and s(x) >= 0)
                tk = t(base) if base else -1
                offset = qrotate(qinv(tq[h]), tp[tk] - tp[h]) if tk >= 0 else np.zeros(3)
                reach = np.linalg.norm(tp[u] - tp[lo]) + np.linalg.norm(tp[lo] - tp[h])
                self.arms.append((u, lo, h, sh, s(base) if tk >= 0 else -1, offset, sign, reach))
                self.poles[u] = (s(side + "UpperArm"), s(side + "LowerArm"), sh)
            ul, ll, f, sf, su = (
                t(side + "UpperLeg"),
                t(side + "LowerLeg"),
                t(side + "Foot"),
                s(side + "Foot"),
                s(side + "UpperLeg"),
            )
            if min(ul, ll, f, sf, su) >= 0 and _above(self.parents, ul, ll) and _above(self.parents, ll, f):
                lift = tp[f][1] - (tp[ul][1] + (sp[sf][1] - sp[su][1]) * self.hip_scale)
                self.legs.append((ul, ll, f, sf, su, lift))
                self.poles[ul] = (su, s(side + "LowerLeg"), sf)
        self.attachments = []
        if role in ("guitarist", "bassist") and source_index.get(_key("bone_guitar_body"), -1) >= 0:
            self.attachments.append((source_index[_key("bone_guitar_body")], "chest"))
        if role == "singer" and source_index.get(_key("bone_mic"), -1) >= 0:
            self.attachments.append((source_index[_key("bone_mic")], "hand"))
        if self.chest_source < 0 or self.chest_target < 0:
            self.attachments = []
        self.palms = (s("leftHand"), s("rightHand"))
        self.hands = (t("leftHand"), t("rightHand"))

    def arm_gains(self, source_t, source_q):
        """(right, left) arm IK strengths (frames) for a clip without the game's arm events: 1 while the GH3 hand is on
        the instrument (within 10 cm of an instrument joint), 0 beyond 25 cm. The game takes the strengths from the
        performance (hero_toggle_arms); the Blender preview and Check Clips use this."""
        frames = source_t.shape[0]
        count = len(self.source_parents)
        sp, sq = np.zeros((frames, count, 3)), np.zeros((frames, count, 4))
        for bone in self.source_order:
            parent = self.source_parents[bone]
            if parent < 0:
                sp[:, bone], sq[:, bone] = source_t[:, bone], source_q[:, bone]
            else:
                sp[:, bone] = sp[:, parent] + qrotate(sq[:, parent], source_t[:, bone])
                sq[:, bone] = qnorm(qmul(sq[:, parent], source_q[:, bone]))
        roots = [root for root, mode in self.attachments if mode == "chest"]
        instrument = [b for b in range(count) if any(b == r or _above(self.source_parents, r, b) for r in roots)]
        gains = {1: np.ones(frames), -1: np.ones(frames)}
        if not instrument:
            return gains[1], gains[-1]
        for _, _, _, source_end, _, _, side, _ in self.arms:
            near = np.min(np.linalg.norm(sp[:, instrument] - sp[:, source_end, None], axis=-1), axis=1)
            x = np.clip((0.25 - near) / 0.15, 0, 1)
            gains[side] = x * x * (3 - 2 * x)
        return gains[1], gains[-1]

    def apply(self, source_t, source_q, right=1.0, left=1.0):
        """Local poses (frames, source bones, 3 and 4) to the local poses of the skeleton (frames, joints, 3 and 4),
        and the GH3 local poses with the moved instrument."""
        frames = source_t.shape[0]
        right = np.broadcast_to(np.asarray(right, dtype=np.float64), (frames,))
        left = np.broadcast_to(np.asarray(left, dtype=np.float64), (frames,))
        ns = len(self.source_parents)
        sp, sq = np.zeros((frames, ns, 3)), np.zeros((frames, ns, 4))
        for bone in self.source_order:
            parent = self.source_parents[bone]
            if parent < 0:
                sp[:, bone], sq[:, bone] = source_t[:, bone], source_q[:, bone]
            else:
                sp[:, bone] = sp[:, parent] + qrotate(sq[:, parent], source_t[:, bone])
                sq[:, bone] = qnorm(qmul(sq[:, parent], source_q[:, bone]))
        self.source_p, self.source_q = sp, sq
        self.goals = []
        count = len(self.parents)
        self.desired = np.zeros((frames, count, 4))
        self.driven = np.zeros((frames, count), bool)
        for bone, source in enumerate(self.map):
            if source >= 0:
                self.desired[:, bone] = qnorm(qmul(sq[:, source], self.correction[bone]))
                self.driven[:, bone] = True
        move = (
            (sp[:, self.source_pelvis] - self.source_rest_p[self.source_pelvis]) * self.hip_scale
            if self.source_pelvis >= 0
            else np.zeros((frames, 3))
        )
        self.wp, self.wq = np.zeros((frames, count, 3)), np.zeros((frames, count, 4))
        self._forward(move)
        out_t, out_q = source_t.copy(), source_q.copy()
        chest_move, instrument = np.zeros((frames, 3)), -1
        for root, mode in self.attachments:
            offset = qrotate(sq[:, self.chest_source], np.broadcast_to(self.offset, (frames, 3)))
            moved = (
                self.wp[:, self.chest_target]
                + (sp[:, root] - sp[:, self.chest_source]) * self.torso_scale
                + offset
                - sp[:, root]
            )
            if mode == "chest":
                moved = moved + self._reach(sp, root, moved, left, right)
            if mode == "hand":
                best, hand = np.full(frames, np.inf), np.zeros((frames, 3))
                for palm, own in zip(self.palms, self.hands):
                    if palm < 0 or own < 0:
                        continue
                    distance = np.linalg.norm(sp[:, palm] - sp[:, root], axis=-1)
                    closer = distance < best
                    best = np.where(closer, distance, best)
                    hand = np.where(closer[:, None], self.wp[:, own] - sp[:, palm], hand)
                x = np.clip((0.25 - best) / 0.15, 0, 1)
                weight = (x * x * (3 - 2 * x))[:, None]
                moved = moved + (hand - moved) * weight
            parent = self.source_parents[root]
            position = sp[:, root] + moved
            out_t[:, root] = position if parent < 0 else qrotate(qinv(sq[:, parent]), position - sp[:, parent])
            if mode == "chest":
                chest_move, instrument = moved, root

        def goal_of(bone):
            if instrument < 0:
                return sp[:, bone] + chest_move
            return sp[:, instrument] + chest_move + (sp[:, bone] - sp[:, instrument]) * self.instrument_scale

        for upper, lower, end, source_end, base, offset, side, _ in self.arms:
            gain = left if side < 0 else right
            goal = goal_of(base if base >= 0 else source_end)
            if base >= 0:
                goal = goal - qrotate(self.desired[:, end], np.broadcast_to(offset, (frames, 3)))
            self._solve(upper, lower, end, goal, gain, self._pole(sp, upper))
            self.goals.append((end, goal, gain, base >= 0, offset))
        for upper, lower, end, source_end, source_upper, lift in self.legs:
            goal = self.wp[:, upper] + (sp[:, source_end] - sp[:, source_upper]) * self.hip_scale + [0, lift, 0]
            self._solve(upper, lower, end, goal, np.ones(frames), self._pole(sp, upper))
        self._forward(move)
        t, q = np.zeros((frames, count, 3)), np.zeros((frames, count, 4))
        for bone in range(count):
            parent = self.parents[bone]
            if parent < 0:
                t[:, bone], q[:, bone] = self.wp[:, bone], self.wq[:, bone]
                continue
            inverse = qinv(self.wq[:, parent])
            t[:, bone] = (
                qrotate(inverse, self.wp[:, bone] - self.wp[:, parent])
                if bone == self.root_motion
                else self.bind[bone, :3]
            )
            q[:, bone] = qnorm(qmul(inverse, self.wq[:, bone]))
        return t, q, out_t, out_q

    def _goal(self, sp, bone, instrument, move):
        if instrument < 0:
            return sp[:, bone] + move
        return sp[:, instrument] + move + (sp[:, bone] - sp[:, instrument]) * self.instrument_scale

    def _reach(self, sp, instrument, move, left, right):
        """The shift of the instrument that brings the hand goals within the reach of the arms (CharacterRetarget.Reach)."""
        shift = np.zeros_like(move)
        for _ in range(2):
            step = np.zeros_like(move)
            for upper, _lower, end, source_end, base, offset, side, reach in self.arms:
                gain = left if side < 0 else right
                goal = self._goal(sp, base if base >= 0 else source_end, instrument, move + shift)
                if base >= 0:
                    goal = goal - qrotate(self.desired[:, end], np.broadcast_to(offset, goal.shape))
                vector = goal - self.wp[:, upper]
                excess = np.linalg.norm(vector, axis=-1) - reach * 0.98
                step -= _unit(vector) * (np.maximum(excess, 0) * np.where(gain > 0, gain, 0))[:, None]
            shift = shift + step
        return shift

    def _forward(self, move):
        frames = move.shape[0]
        for bone in self.order:
            parent = self.parents[bone]
            rest_p, rest_q = self.bind[bone, :3], self.bind[bone, 3:]
            parent_p = self.wp[:, parent] if parent >= 0 else np.zeros((frames, 3))
            parent_q = self.wq[:, parent] if parent >= 0 else np.tile([0, 0, 0, 1.0], (frames, 1))
            self.wp[:, bone] = (
                self.rest_p[bone] + move
                if bone == self.root_motion
                else parent_p + qrotate(parent_q, np.broadcast_to(rest_p, (frames, 3)))
            )
            carried = qnorm(qmul(parent_q, rest_q))
            rotation = np.where(self.driven[:, bone, None], self.desired[:, bone], carried)
            if bone in self.between:
                upper, lower, fraction = self.between[bone]
                turn_u = qmul(self.desired[:, upper], qinv(self.rest_q[upper]))
                turn_l = qmul(self.desired[:, lower], qinv(self.rest_q[lower]))
                turn = qmul(turn_u, power(qmul(qinv(turn_u), turn_l), fraction))
                rotation = qnorm(qmul(turn, self.rest_q[bone]))
            if bone in self.follower_of:
                _, source, weight, frame = self.follower_of[bone]
                source_parent = self.parents[source]
                local = (
                    self.wq[:, source]
                    if source_parent < 0
                    else qmul(qinv(self.wq[:, source_parent]), self.wq[:, source])
                )
                delta = qmul(qinv(self.bind[source, 3:]), local)
                rotation = qnorm(qmul(carried, qmul(qmul(frame, power(delta, weight)), qinv(frame))))
            for upper, twist, _, axis in self.upper_twist:
                if bone == upper:
                    local = qmul(qinv(carried), rotation)
                    swing = qmul(local, qinv(twist_about(local, axis)))
                    rotation = np.where(self.driven[:, bone, None], qnorm(qmul(carried, swing)), rotation)
                elif bone == twist:
                    rotation = np.where(
                        self.driven[:, upper, None], qnorm(qmul(self.desired[:, upper], rest_q)), rotation
                    )
            for lower, twist, hand, axis, hand_rest in self.lower_twist:
                if bone == lower and twist < 0:
                    turn = twist_about(qmul(qmul(qinv(rotation), self.desired[:, hand]), qinv(hand_rest)), axis)
                    rotation = np.where(
                        (self.driven[:, bone] & self.driven[:, hand])[:, None],
                        qnorm(qmul(rotation, power(turn, 0.5))),
                        rotation,
                    )
                elif bone == twist:
                    turn = twist_about(
                        qmul(qmul(qinv(self.wq[:, lower]), self.desired[:, hand]), qinv(hand_rest)), axis
                    )
                    turned = qnorm(qmul(qmul(qmul(self.wq[:, lower], turn), qinv(self.wq[:, lower])), rotation))
                    rotation = np.where(self.driven[:, hand, None], turned, rotation)
            self.wq[:, bone] = rotation

    def _pole(self, sp, upper):
        """The direction in which the GH3 elbow or knee sticks out of its shoulder-hand or hip-foot line (frames, 3)."""
        su, sl, se = self.poles.get(upper, (-1, -1, -1))
        if min(su, sl, se) < 0:
            return np.zeros((sp.shape[0], 3))
        line = _unit(sp[:, se] - sp[:, su])
        off = sp[:, sl] - sp[:, su]
        return off - line * np.sum(off * line, -1, keepdims=True)

    def _solve(self, upper, lower, end, goal, gain, pole=None):
        a, b, c = self.wp[:, upper], self.wp[:, lower], self.wp[:, end]
        upper_length, lower_length = np.linalg.norm(b - a, axis=-1), np.linalg.norm(c - b, axis=-1)
        to_goal = goal - a
        distance = np.clip(
            np.linalg.norm(to_goal, axis=-1),
            np.abs(upper_length - lower_length) + 1e-4,
            (upper_length + lower_length) * 0.9999,
        )
        u = _unit(to_goal)
        bend = (b - a) - u * np.sum((b - a) * u, -1, keepdims=True)
        if pole is not None:
            # The bend plane of the GH3 limb (its elbow or knee direction) when the GH3 limb is bent; else the pose's own plane.
            hint = pole - u * np.sum(pole * u, -1, keepdims=True)
            bend = np.where(
                (np.linalg.norm(pole, axis=-1) > 0.005)[:, None] & (np.sum(hint**2, -1) > 1e-8)[:, None], hint, bend
            )
        fallback = (c - a) - u * np.sum((c - a) * u, -1, keepdims=True)
        bend = np.where((np.sum(bend**2, -1) < 1e-10)[:, None], fallback, bend)
        v = _unit(bend)
        cos = np.clip((upper_length**2 + distance**2 - lower_length**2) / (2 * upper_length * distance), -1, 1)
        elbow = a + (u * cos[:, None] + v * np.sqrt(1 - cos**2)[:, None]) * upper_length[:, None]
        wrist = a + u * distance[:, None]
        first = from_to(b - a, elbow - a)
        second = from_to(qrotate(first, c - b), wrist - elbow)
        gain = np.asarray(gain, dtype=np.float64)
        first, second = power(first, gain[:, None]), power(second, gain[:, None])
        active = (gain > 0) & (upper_length > 1e-4) & (lower_length > 1e-4)
        base_u = np.where(self.driven[:, upper, None], self.desired[:, upper], self.wq[:, upper])
        base_l = np.where(self.driven[:, lower, None], self.desired[:, lower], self.wq[:, lower])
        self.desired[:, upper] = np.where(active[:, None], qnorm(qmul(first, base_u)), self.desired[:, upper])
        self.desired[:, lower] = np.where(
            active[:, None], qnorm(qmul(qmul(second, first), base_l)), self.desired[:, lower]
        )
        self.driven[:, upper] |= active
        self.driven[:, lower] |= active


def _unit(v):
    return v / np.maximum(np.linalg.norm(v, axis=-1, keepdims=True), 1e-12)


def _frame_turn(a, a2, b, b2):
    first = from_to(a, b)
    axis = _unit(b)
    x = qrotate(first, a2)
    x = x - axis * np.dot(x, axis)
    y = b2 - axis * np.dot(b2, axis)
    if np.dot(x, x) < 1e-10 or np.dot(y, y) < 1e-10:
        return first
    return qnorm(qmul(from_to(x, y), first))
