"""Characters with their own skeleton (pipeline/package/skeleton.py, docs/PACKAGE-FORMAT.md section 9.6): the automatic
map for MMD, VRoid, Mixamo, and Unreal names, the checks of the map and the rest pose, and the retarget fixture that the
C# test (tests/RetargetLogicTests.cs) also runs, so that the Blender preview and the game agree.

RETARGET_FIXTURE_WRITE=1 writes tests/fixtures/retarget-case.json again."""

import json
import os
import unittest
from pathlib import Path

import numpy as np

from pipeline.package import humanoid as humanoid_finder, skeleton

FIXTURE = Path(__file__).resolve().parent/"fixtures"/"retarget-case.json"
FIXTURE_ANY = Path(__file__).resolve().parent/"fixtures"/"retarget-any-rig.json"
SIDES = (("l", "left", 1), ("r", "right", -1))


def gh3_rig():
    """A GH3-like rig (bone checksums as the game names them), local bind rows in runtime coordinates (identity rotations)."""
    key = skeleton._key
    rows = [("control_root", None, (0, 0, 0)), ("bone_pelvis", "control_root", (0, 1.0, 0)),
            ("bone_stomach_lower", "bone_pelvis", (0, 1.08, 0)), ("bone_stomach_upper", "bone_stomach_lower", (0, 1.18, 0.02)),
            ("bone_chest", "bone_stomach_upper", (0, 1.28, 0.05)), ("bone_neck", "bone_chest", (0, 1.45, 0.05)), ("bone_head", "bone_neck", (0, 1.52, 0.02)),
            ("bone_guitar_body", "control_root", (0.35, 0.95, -0.2))]
    for s, side, sign in SIDES:
        rows += [(f"bone_collar_{s}", "bone_chest", (sign * 0.02, 1.37, 0.01)), (f"bone_bicep_{s}", f"bone_collar_{s}", (sign * 0.15, 1.37, 0.03)),
                 (f"bone_forearm_{s}", f"bone_bicep_{s}", (sign * 0.31, 1.2, 0.03)), (f"bone_palm_{s}", f"bone_forearm_{s}", (sign * 0.47, 1.04, 0.02)),
                 (f"bone_thigh_{s}", "bone_pelvis", (sign * 0.09, 0.95, 0.01)), (f"bone_knee_{s}", f"bone_thigh_{s}", (sign * 0.11, 0.61, 0.02)),
                 (f"bone_ankle_{s}", f"bone_knee_{s}", (sign * 0.15, 0.17, 0.02)), (f"bone_toe_{s}", f"bone_ankle_{s}", (sign * 0.15, 0.09, -0.07))]
        for finger, z in (("Index", -0.01), ("Middle", 0.013), ("Little", 0.047)):
            checksums = skeleton.GH3_FINGERS[side + finger]
            rows += [("bone-" + checksums[0], f"bone_palm_{s}", (sign * 0.53, 0.99, z)), ("bone-" + checksums[1], "bone-" + checksums[0], (sign * 0.56, 0.96, z))]
    names = [r[0] for r in rows]
    bones = [n[5:] if n.startswith("bone-") else key(n) for n in names]
    parents = [names.index(r[1]) if r[1] else -1 for r in rows]
    world = np.array([r[2] for r in rows], dtype=float)
    bind = [[*(world[i] - (world[p] if p >= 0 else 0)), 0, 0, 0, 1] for i, p in enumerate(parents)]
    return names, bones, parents, bind


def target_rig(seed=3, scale=0.85):
    """A skeleton with the MMD layout (center and waist above sibling upper and lower body, twist bones, followers), a
    T-pose, random bone rolls, and another size."""
    random = np.random.default_rng(seed)
    rows = [("center", None, (0, 0.5, 0)), ("waist", "center", (0, 0.98, 0)), ("lower", "waist", (0, 1.0, 0)), ("upper", "waist", (0, 1.06, 0)),
            ("upper2", "upper", (0, 1.25, 0.03)), ("neck", "upper2", (0, 1.44, 0.04)), ("head", "neck", (0, 1.52, 0.02))]
    humanoid = {"hips": "lower", "spine": "upper", "chest": "upper2", "neck": "neck", "head": "head"}
    followers = []
    for s, side, sign in SIDES:
        y = 1.37
        rows += [(f"shoulder.{s}", "upper2", (sign * 0.02, y, 0.01)), (f"arm.{s}", f"shoulder.{s}", (sign * 0.13, y, 0.02)),
                 (f"armtwist.{s}", f"arm.{s}", (sign * 0.23, y, 0.02)), (f"armtwist1.{s}", f"arm.{s}", (sign * 0.18, y, 0.02)),
                 (f"elbow.{s}", f"armtwist.{s}", (sign * 0.33, y, 0.02)), (f"handtwist.{s}", f"elbow.{s}", (sign * 0.42, y, 0.02)),
                 (f"handtwist1.{s}", f"elbow.{s}", (sign * 0.38, y, 0.02)), (f"wrist.{s}", f"handtwist.{s}", (sign * 0.5, y, 0.02)),
                 (f"index1.{s}", f"wrist.{s}", (sign * 0.56, y, -0.012)), (f"index2.{s}", f"index1.{s}", (sign * 0.59, y, -0.012)),
                 (f"middle1.{s}", f"wrist.{s}", (sign * 0.565, y, 0.01)), (f"middle2.{s}", f"middle1.{s}", (sign * 0.595, y, 0.01)),
                 (f"little1.{s}", f"wrist.{s}", (sign * 0.55, y, 0.04)),
                 (f"legD.{s}", "lower", (sign * 0.08, 0.92, 0.0)), (f"kneeD.{s}", f"legD.{s}", (sign * 0.085, 0.5, 0.0)),
                 (f"ankleD.{s}", f"kneeD.{s}", (sign * 0.09, 0.07, 0.03)), (f"toe.{s}", f"ankleD.{s}", (sign * 0.09, 0.02, -0.1)),
                 (f"hair1.{s}", "head", (sign * 0.08, 1.6, 0.05))]
        humanoid.update({f"{side}Shoulder": f"shoulder.{s}", f"{side}UpperArm": f"arm.{s}", f"{side}UpperArmTwist": f"armtwist.{s}",
                         f"{side}LowerArm": f"elbow.{s}", f"{side}LowerArmTwist": f"handtwist.{s}", f"{side}Hand": f"wrist.{s}",
                         f"{side}IndexProximal": f"index1.{s}", f"{side}IndexIntermediate": f"index2.{s}", f"{side}MiddleProximal": f"middle1.{s}",
                         f"{side}MiddleIntermediate": f"middle2.{s}", f"{side}LittleProximal": f"little1.{s}",
                         f"{side}UpperLeg": f"legD.{s}", f"{side}LowerLeg": f"kneeD.{s}", f"{side}Foot": f"ankleD.{s}", f"{side}Toes": f"toe.{s}"})
        followers += [{"bone": f"armtwist1.{s}", "source": f"armtwist.{s}", "weight": 0.5}, {"bone": f"handtwist1.{s}", "source": f"handtwist.{s}", "weight": 0.5}]
    names = [r[0] for r in rows]
    parents = [names.index(r[1]) if r[1] else -1 for r in rows]
    world = np.array([r[2] for r in rows], dtype=float) * scale
    rotation = random.normal(size=(len(rows), 4))
    rotation[:, 3] = np.abs(rotation[:, 3]) + 0.3
    rotation /= np.linalg.norm(rotation, axis=1, keepdims=True)
    bind = []
    for i, p in enumerate(parents):
        if p < 0: bind.append([*world[i], *rotation[i]]); continue
        inverse = skeleton.qinv(rotation[p])
        bind.append([*skeleton.qrotate(inverse, world[i] - world[p]), *skeleton.qnorm(skeleton.qmul(inverse, rotation[i]))])
    return names, parents, bind, humanoid, followers


def any_rig(seed=5, scale=1.1):
    """A skeleton that the automatic map can give for other rigs: five spine bones (three with a role, the others
    between them), a neck of two bones, hands without a middle finger, two-joint thumbs, no shoulders, no toes, an A-pose,
    bone rolls, and another size."""
    random = np.random.default_rng(seed)
    rows = [("root", None, (0, 0, 0)), ("pelvis", "root", (0, 1.0, 0)), ("spine_01", "pelvis", (0, 1.07, 0)), ("spine_02", "spine_01", (0, 1.14, 0.01)),
            ("spine_03", "spine_02", (0, 1.21, 0.02)), ("spine_04", "spine_03", (0, 1.28, 0.03)), ("spine_05", "spine_04", (0, 1.35, 0.03)),
            ("neck_01", "spine_05", (0, 1.45, 0.03)), ("neck_02", "neck_01", (0, 1.49, 0.03)), ("head", "neck_02", (0, 1.53, 0.02))]
    humanoid = {"hips": "pelvis", "spine": "spine_01", "chest": "spine_03", "upperChest": "spine_05", "neck": "neck_01", "head": "head"}
    for s, side, sign in SIDES:
        rows += [(f"upperarm_{s}", "spine_05", (sign * 0.17, 1.4, 0.03)), (f"lowerarm_{s}", f"upperarm_{s}", (sign * 0.36, 1.21, 0.03)),
                 (f"hand_{s}", f"lowerarm_{s}", (sign * 0.53, 1.04, 0.03)), (f"index_01_{s}", f"hand_{s}", (sign * 0.6, 0.98, 0.0)),
                 (f"index_02_{s}", f"index_01_{s}", (sign * 0.63, 0.95, 0.0)), (f"ring_01_{s}", f"hand_{s}", (sign * 0.6, 0.98, 0.04)),
                 (f"pinky_01_{s}", f"hand_{s}", (sign * 0.59, 0.99, 0.06)), (f"thumb_01_{s}", f"hand_{s}", (sign * 0.55, 1.02, -0.035)),
                 (f"thumb_02_{s}", f"thumb_01_{s}", (sign * 0.57, 1.0, -0.075)),
                 (f"thigh_{s}", "pelvis", (sign * 0.1, 0.95, 0.0)), (f"calf_{s}", f"thigh_{s}", (sign * 0.1, 0.52, 0.01)), (f"foot_{s}", f"calf_{s}", (sign * 0.1, 0.08, 0.0))]
        humanoid.update({f"{side}UpperArm": f"upperarm_{s}", f"{side}LowerArm": f"lowerarm_{s}", f"{side}Hand": f"hand_{s}",
                         f"{side}IndexProximal": f"index_01_{s}", f"{side}IndexIntermediate": f"index_02_{s}", f"{side}RingProximal": f"ring_01_{s}",
                         f"{side}LittleProximal": f"pinky_01_{s}", f"{side}ThumbMetacarpal": f"thumb_01_{s}", f"{side}ThumbProximal": f"thumb_02_{s}",
                         f"{side}UpperLeg": f"thigh_{s}", f"{side}LowerLeg": f"calf_{s}", f"{side}Foot": f"foot_{s}"})
    names = [r[0] for r in rows]
    parents = [names.index(r[1]) if r[1] else -1 for r in rows]
    world = np.array([r[2] for r in rows], dtype=float) * scale
    rotation = random.normal(size=(len(rows), 4))
    rotation[:, 3] = np.abs(rotation[:, 3]) + 0.3
    rotation /= np.linalg.norm(rotation, axis=1, keepdims=True)
    bind = []
    for i, p in enumerate(parents):
        if p < 0: bind.append([*world[i], *rotation[i]]); continue
        inverse = skeleton.qinv(rotation[p])
        bind.append([*skeleton.qrotate(inverse, world[i] - world[p]), *skeleton.qnorm(skeleton.qmul(inverse, rotation[i]))])
    return names, parents, bind, humanoid, []


def poses(source_parents, source_bind, frames=6, seed=11):
    random = np.random.default_rng(seed)
    count = len(source_parents)
    t = np.repeat(np.array(source_bind)[None, :, :3], frames, 0)
    axis = random.normal(size=(frames, count, 3))
    axis /= np.linalg.norm(axis, axis=-1, keepdims=True)
    angle = random.uniform(-0.6, 0.6, size=(frames, count, 1))
    q = np.concatenate([axis * np.sin(angle / 2), np.cos(angle / 2)], -1)
    q[:, 0] = [0, 0, 0, 1]
    t[:, 1] += random.uniform(-0.08, 0.08, size=(frames, 3))
    gains = random.uniform(0, 1, size=(frames, 2))
    gains[0] = 1
    return t, q, gains


def run_case(rig=target_rig):
    names, bones, source_parents, source_bind = gh3_rig()
    target_names, parents, bind, humanoid, followers = rig()
    index = {n: i for i, n in enumerate(target_names)}
    t, q, gains = poses(source_parents, source_bind)
    retarget = skeleton.Retarget(bones, source_parents, source_bind, target_names, parents, bind, {r: index[b] for r, b in humanoid.items()},
                                 [(index[f["bone"]], index[f["source"]], f["weight"]) for f in followers], role="guitarist")
    out = [retarget.apply(t[f:f + 1], q[f:f + 1], gains[f, 0], gains[f, 1]) for f in range(len(t))]
    case = {"sourceBones": bones, "sourceParents": source_parents, "sourceBind": source_bind, "names": target_names, "parents": parents,
            "bind": bind, "humanoid": humanoid, "followers": followers, "role": "guitarist",
            "frames": [{"positions": t[f].tolist(), "rotations": q[f].tolist(), "right": float(gains[f, 0]), "left": float(gains[f, 1]),
                        "targetPositions": o[0][0].tolist(), "targetRotations": o[1][0].tolist(), "sourceOut": o[2][0].tolist()} for f, o in enumerate(out)]}
    return case, retarget


class AutoMapTest(unittest.TestCase):
    def check_names(self, names, expected, parents=None):
        parents = parents or [-1] * len(names)
        found = skeleton.auto_map(names, parents)
        for role, name in expected.items(): self.assertEqual(found.get(role), name, role)

    def test_mmd(self):
        names = ["センター", "グルーブ", "腰", "上半身", "上半身2", "首", "頭", "下半身", "肩.L", "腕.L", "腕捩.L", "ひじ.L", "手捩.L", "手首.L",
                 "親指０.L", "親指１.L", "親指２.L", "人指１.L", "人指２.L", "人指３.L", "足.L", "ひざ.L", "足首.L", "足D.L", "ひざD.L", "足首D.L",
                 "足先EX.L", "足ＩＫ.L", "腕捩1.L", "肩P.L"]
        parents = [-1, 0, 1, 2, 3, 4, 5, 2, 4, 8, 9, 10, 11, 12, 13, 14, 15, 13, 17, 18, 7, 20, 21, 7, 23, 24, 25, 0, 9, 4]
        deform = set(names) - {"足.L", "ひざ.L", "足首.L"}
        found = skeleton.auto_map(names, parents, deform=deform)
        self.assertEqual(found["hips"], "下半身")
        self.assertEqual(found["spine"], "上半身")
        self.assertEqual(found["chest"], "上半身2")
        self.assertEqual(found["leftUpperLeg"], "足D.L")
        self.assertEqual(found["leftLowerLeg"], "ひざD.L")
        self.assertEqual(found["leftFoot"], "足首D.L")
        self.assertEqual(found["leftToes"], "足先EX.L")
        self.assertEqual(found["leftUpperArmTwist"], "腕捩.L")
        self.assertEqual(found["leftLowerArmTwist"], "手捩.L")
        self.assertEqual(found["leftThumbMetacarpal"], "親指０.L")
        self.assertEqual(found["leftIndexDistal"], "人指３.L")

    def test_vroid_mixamo_unreal(self):
        self.check_names(["J_Bip_C_Hips", "J_Bip_C_Spine", "J_Bip_C_Chest", "J_Bip_C_UpperChest", "J_Bip_C_Neck", "J_Bip_C_Head", "J_Bip_L_UpperArm",
                          "J_Bip_L_LowerArm", "J_Bip_L_Hand", "J_Bip_L_Index1", "J_Bip_L_Thumb1", "J_Bip_R_UpperLeg", "J_Bip_R_LowerLeg", "J_Bip_R_Foot",
                          "J_Bip_R_ToeBase"],
                         {"hips": "J_Bip_C_Hips", "neck": "J_Bip_C_Neck", "leftUpperArm": "J_Bip_L_UpperArm", "leftLowerArm": "J_Bip_L_LowerArm",
                          "leftHand": "J_Bip_L_Hand", "leftIndexProximal": "J_Bip_L_Index1", "leftThumbMetacarpal": "J_Bip_L_Thumb1",
                          "rightUpperLeg": "J_Bip_R_UpperLeg", "rightLowerLeg": "J_Bip_R_LowerLeg", "rightFoot": "J_Bip_R_Foot", "rightToes": "J_Bip_R_ToeBase"})
        self.check_names(["mixamorig:Hips", "mixamorig:LeftArm", "mixamorig:LeftForeArm", "mixamorig:LeftHand", "mixamorig:LeftUpLeg", "mixamorig:LeftLeg",
                          "mixamorig:LeftFoot", "mixamorig:LeftHandPinky1", "mixamorig:RightShoulder"],
                         {"hips": "mixamorig:Hips", "leftUpperArm": "mixamorig:LeftArm", "leftLowerArm": "mixamorig:LeftForeArm", "leftHand": "mixamorig:LeftHand",
                          "leftUpperLeg": "mixamorig:LeftUpLeg", "leftLowerLeg": "mixamorig:LeftLeg", "leftFoot": "mixamorig:LeftFoot",
                          "leftLittleProximal": "mixamorig:LeftHandPinky1", "rightShoulder": "mixamorig:RightShoulder"})
        self.check_names(["pelvis", "spine_01", "neck_01", "head", "clavicle_l", "upperarm_l", "lowerarm_l", "hand_l", "upperarm_twist_01_l",
                          "lowerarm_twist_01_l", "thumb_01_l", "thigh_r", "calf_r", "foot_r", "ball_r", "ik_hand_root"],
                         {"hips": "pelvis", "neck": "neck_01", "leftShoulder": "clavicle_l", "leftUpperArm": "upperarm_l", "leftLowerArm": "lowerarm_l",
                          "leftHand": "hand_l", "leftUpperArmTwist": "upperarm_twist_01_l", "leftLowerArmTwist": "lowerarm_twist_01_l",
                          "leftThumbMetacarpal": "thumb_01_l", "rightUpperLeg": "thigh_r", "rightLowerLeg": "calf_r", "rightFoot": "foot_r", "rightToes": "ball_r"})

    def test_face(self):
        self.assertEqual(skeleton.auto_face(["まばたき", "あ", "い", "う", "え", "お", "笑い"]),
                         {"blink": "まばたき", "a": "あ", "i": "い", "u": "う", "e": "え", "o": "お"})
        self.assertEqual(skeleton.auto_face(["Fcl_EYE_Close", "Fcl_MTH_A", "Fcl_MTH_O"]), {"blink": "Fcl_EYE_Close", "a": "Fcl_MTH_A", "o": "Fcl_MTH_O"})


class FindTest(unittest.TestCase):
    """The automatic map from the skeleton alone (pipeline/package/humanoid.py): names that say nothing, a turned model, a
    mirrored model, helper bones without weights."""

    def world(self, rig=any_rig):
        """Joint positions in glTF coordinates (right-handed: +Y up, the model looks toward +Z, its left side on +X)."""
        names, parents, bind, humanoid, _ = rig()
        position, _ = skeleton.world_positions(parents, bind)
        return names, parents, position * [1, 1, -1], humanoid

    def check(self, names, parents, heads, humanoid, weights=None, **expect):
        found = humanoid_finder.find(names, parents, heads, weights=weights)
        self.assertTrue(found["found"], found["notes"])
        got = {role: names[bone] for role, bone in found["roles"].items()}
        for role in skeleton.REQUIRED: self.assertEqual(got.get(role), humanoid[role], role)
        for key, value in expect.items(): self.assertEqual(found[key], value, key)
        return found

    def test_names_say_nothing(self):
        names, parents, heads, humanoid = self.world()
        anonymous = [f"joint{i}" for i in range(len(names))]
        self.check(anonymous, parents, heads, {r: f"joint{names.index(b)}" for r, b in humanoid.items()})

    def test_turned_and_scaled(self):
        names, parents, heads, humanoid = self.world()
        # glTF (+Y up, +Z front) to a Z-up model that looks toward +X, in centimetres.
        turned = np.stack([heads[:, 2], heads[:, 0], heads[:, 1]], 1) * 100
        found = self.check(names, parents, turned, humanoid)
        np.testing.assert_allclose(found["frame"]["up"], [0, 0, 1], atol=1e-6)
        np.testing.assert_allclose(found["frame"]["forward"], [1, 0, 0], atol=1e-6)

    def test_mirrored_names(self):
        names, parents, heads, humanoid = self.world()
        mirrored = heads * [-1, 1, 1]
        swap = {r: humanoid[("right" + r[4:]) if r.startswith("left") else ("left" + r[5:]) if r.startswith("right") else r] for r in humanoid}
        self.check(names, parents, mirrored, swap, mirrored=True)

    def test_helpers_without_weights(self):
        names, parents, heads, humanoid = self.world()
        names, parents, heads = names + ["ik_hand_l", "pole_l"], parents + [0, 0], np.vstack([heads, heads[names.index("hand_l")], [0.3, 0.5, 0.6]])
        weights = [0 if n in ("root", "ik_hand_l", "pole_l") else 10 for n in names]
        self.check(names, parents, heads, humanoid, weights)


class CheckTest(unittest.TestCase):
    def test_target_rig_is_valid_and_errors(self):
        names, parents, bind, humanoid, followers = target_rig()
        errors, warnings, chain_bones = skeleton.check_map(names, parents, bind, humanoid, followers)
        self.assertEqual(errors, [])
        self.assertIn("hair1.l", chain_bones)
        self.assertNotIn("waist", chain_bones)
        self.assertNotIn("armtwist1.l", chain_bones)
        swapped = dict(humanoid, leftUpperArm=humanoid["rightUpperArm"], rightUpperArm=humanoid["leftUpperArm"])
        errors, _, _ = skeleton.check_map(names, parents, bind, swapped)
        self.assertTrue(any("must be a parent" in e or "swapped" in e for e in errors), errors)
        errors, _, _ = skeleton.check_map(names, parents, bind, {k: v for k, v in humanoid.items() if k != "leftFoot"})
        self.assertTrue(any("needs the roles leftFoot" in e for e in errors), errors)


class RetargetFixtureTest(unittest.TestCase):
    def test_fixture(self):
        case, retarget = run_case()
        if os.environ.get("RETARGET_FIXTURE_WRITE") == "1":
            FIXTURE.parent.mkdir(exist_ok=True)
            FIXTURE.write_text(json.dumps(case, separators=(",", ":")) + "\n")
        stored = json.loads(FIXTURE.read_text())
        for ours, theirs in zip(case["frames"], stored["frames"]):
            np.testing.assert_allclose(ours["targetPositions"], theirs["targetPositions"], atol=1e-6)
            np.testing.assert_allclose(np.abs(np.sum(np.array(ours["targetRotations"]) * theirs["targetRotations"], -1)), 1, atol=1e-6)
        self.assertLess(abs(retarget.hip_scale - 0.85), 0.05)

    def test_any_rig_fixture(self):
        case, retarget = run_case(any_rig)
        if os.environ.get("RETARGET_FIXTURE_WRITE") == "1": FIXTURE_ANY.write_text(json.dumps(case, separators=(",", ":")) + "\n")
        stored = json.loads(FIXTURE_ANY.read_text())
        for ours, theirs in zip(case["frames"], stored["frames"]):
            np.testing.assert_allclose(ours["targetPositions"], theirs["targetPositions"], atol=1e-6)
            np.testing.assert_allclose(np.abs(np.sum(np.array(ours["targetRotations"]) * theirs["targetRotations"], -1)), 1, atol=1e-6)
        names = case["names"]
        # The spine bones without a role turn part of the way between their role neighbours.
        self.assertEqual({names[b]: round(float(f), 1) for b, (_, _, f) in retarget.between.items()}, {"spine_02": 0.5, "spine_04": 0.5, "neck_02": 0.5})
        # The hand points to the ring finger (no middle finger), and the arm IK uses its base.
        self.assertEqual(skeleton.next_role("leftHand", lambda r: r in case["humanoid"]), "leftRingProximal")
        self.assertTrue(all(arm[4] >= 0 for arm in retarget.arms))


if __name__ == "__main__":
    unittest.main()
