"""The rig data of the GH3 band rigs for the libraries: IK chains, twist and split constraints, and rig points.

The runtime reads these rows from catalog.json and characters.json (docs/PACKAGE-FORMAT.md section 10.2), so it does not
look for GH3 bone names. The IK chains and the constraints are GH3 data (guitar_anim_tree.qb). The rig points name the
GH3 bones that the band code uses by their use: the instrument, the microphone and its stand, the mouth, the drum sticks
and kit, and the throne. The pipeline knows these GH3 names; the runtime reads the points.
"""

from pipeline.gh3.formats import find_array, key

IK_FIELDS = ("bone0", "bone1", "bone2")
# GH3 guitar_anim_tree.qb: the IK parameter array that the anim tree of each band role uses (guitarist_static_tree and
# drummer_static_tree). The mod does not run the IK of vocalist_static_tree, so the singer has none.
ROLE_IK = {"guitarist": "Hero_Ik_params", "bassist": "Hero_Ik_params", "drummer": "drummer_IK_params"}
# The end bone of an arm chain gives its hand: the right arm strums, the left arm frets (hero_toggle_arms).
HANDS = {key("Bone_Palm_R"): "right", key("Bone_Palm_L"): "left"}
CONSTRAINT_TYPES = {key(name): name for name in ("Split", "SplitR", "Twist", "TwistChild")}
# The rig points (PACKAGE-FORMAT.md section 10.2) and their GH3 bones. The throne bone of the drum kit has no known name
# (GH3 checksum fdeaebd9); the 226 throne vertices of Drum_Set001 are on it.
RIG_POINTS = {
    "instrument": key("bone_guitar_body"),
    "microphone": key("bone_mic"),
    "mouthUpper": key("Bone_Lip_Upper_Mid"),
    "mouthLower": key("Bone_Lip_Lower_Mid"),
    "stickLeft": key("Bone_Drum_Stick_L"),
    "stickRight": key("Bone_Drum_Stick_R"),
    "seat": 0xFDEAEBD9,
}


def ik_chains(anim_tree: bytes, name: str | int) -> list[dict]:
    """The rows of a GH3 IK parameter array: bones, target, hinge, cosine limits, and the hand of an arm chain."""
    chains = []
    for row in find_array(anim_tree, name):
        chain = {
            "bones": [f"{row[key(f)]:08x}" for f in IK_FIELDS],
            "target": f"{row[key('boneTarget')]:08x}",
            "hinge": [float(value) for value in row[key("HingeAxis")]],
            "cosMax": float(row[key("CosMaxHingeAngle")]),
            "cosMin": float(row[key("CosMinHingeAngle")]),
        }
        if row[key("bone2")] in HANDS:
            chain["hand"] = HANDS[row[key("bone2")]]
        chains.append(chain)
    return chains


def constraints(anim_tree: bytes) -> list[dict]:
    """The rows of GH3 hero_ConstraintBones: type, bone, target, and amount."""
    return [
        {
            "type": CONSTRAINT_TYPES[row[key("type")]],
            "bone": f"{row[key('bone')]:08x}",
            "target": f"{row[key('target')]:08x}",
            "amount": float(row[key("amount")]),
        }
        for row in find_array(anim_tree, "hero_ConstraintBones")
    ]


def points(bones) -> dict[str, str]:
    """The rig points of a rig (bone checksums as integers or 8-digit text) that the rig has."""
    present = {int(bone, 16) if isinstance(bone, str) else bone for bone in bones}
    return {name: f"{bone:08x}" for name, bone in RIG_POINTS.items() if bone in present}
