import json
import os
import tempfile
import unittest
from pathlib import Path

import numpy as np

from pipeline import characters
from pipeline.gh3.formats import key
from pipeline.package import build, runtime
from pipeline.package.validate import Validator
from pipeline.paths import CHARACTERS, GH3_DATA, LIBRARY
from pipeline.stagepack.gltf import SKINNED_GH3_VERTEX
from tests.test_package_build import library, skinned_model, write

DATA = Path(os.environ.get("GH3_DATA", GH3_DATA))
HEAD = key("bone_head")


def rig(names, parents, positions):
    """A GH3 skeleton tuple (names, parents, positions, rotations) without rotations."""
    rotations = np.tile([0.0, 0.0, 0.0, 1.0], (len(names), 1))
    return list(names), list(parents), np.array(positions, dtype=float), rotations


class RetargetTests(unittest.TestCase):
    # 0 root, 1 head, 2 face bone, 3 and 4 a hair chain, 5 an arm, 6 an accessory on the arm.
    NAMES = [1, HEAD, 2, key("Bone_ACC_01"), key("Bone_ACC_02"), 3, key("Bone_ACC_03")]
    PARENTS = [-1, 0, 1, 1, 3, 0, 5]

    def test_chains(self):
        parents = [-1, 0, 1, 1, 3, 4, 4]
        self.assertEqual(sorted(characters.chains(parents, {2, 3, 4, 5, 6}, 1)), [[2], [3, 4, 5], [6]])

    def test_plan(self):
        source = rig(self.NAMES, self.PARENTS, [[0, 0, 0], [0, 1.5, 0], [0, 0.1, 0.1], [0, 0.2, -0.05], [0, -0.1, 0], [0.3, 1.2, 0], [0, 0.1, 0]])
        # The profile has the head 5 cm higher and the arm 10 cm farther out; its hair chain has three joints.
        target = rig(self.NAMES, self.PARENTS, [[0, 0, 0], [0, 1.55, 0], [0, 0.12, 0.08], [0, 0.18, -0.06], [0, -0.1, 0], [0.4, 1.2, 0], [0, 0.2, 0]])
        plan, hair = characters.retarget_plan(source, target, [3, 4])
        self.assertEqual([bone for bone, _ in plan], [0, 1, 2, 3, 4, 5, 5])
        for bone in (1, 2, 3, 4):
            np.testing.assert_allclose(plan[bone][1][:3, 3], [0, 0.05, 0], atol=1e-12, err_msg="head, face, and hair move with the head")
        np.testing.assert_allclose(plan[5][1][:3, 3], [0.1, 0, 0], atol=1e-12)
        np.testing.assert_allclose(plan[6][1], plan[5][1], err_msg="an accessory moves with its body parent")
        self.assertEqual(hair, [{"chain": [3, 4], "to": [3, 4], "distance": 0.0224}])
        far = rig(self.NAMES, self.PARENTS, [[0, 0, 0], [0, 1.5, 0], [0, 0.1, 0.1], [0, 0.2, 0.5], [0, -0.1, 0], [0.3, 1.2, 0], [0, 0.1, 0]])
        plan, hair = characters.retarget_plan(far, target, [3, 4])
        self.assertEqual([plan[3][0], plan[4][0], hair[0]["to"]], [1, 1, []], "a far hair chain stays on the head")

    def test_retarget_moves_and_merges_weights(self):
        moved = np.eye(4); moved[:3, 3] = [0.1, 0, 0]
        plan = [(0, np.eye(4)), (0, moved), (2, np.eye(4))]
        vertices = np.zeros(2, dtype=SKINNED_GH3_VERTEX)
        vertices["position"] = [[1, 2, 3], [4, 5, 6]]
        vertices["normal"] = [0, 0, 1]
        vertices["joints"] = [[0, 1, 0, 0], [2, 0, 0, 0]]
        vertices["weights"] = [[0.5, 0.5, 0, 0], [1, 0, 0, 0]]
        (_, _, data, _), = characters.retarget([("0000abcd", 2, vertices.tobytes(), [(0, 1, 1)])], plan)
        result = np.frombuffer(data, dtype=SKINNED_GH3_VERTEX)
        np.testing.assert_allclose(result["position"][0], [1.05, 2, 3])
        self.assertEqual((result["joints"][0].tolist(), result["weights"][0].tolist()), ([0, 0, 0, 0], [1, 0, 0, 0]))
        self.assertEqual(result[1].tobytes(), vertices[1].tobytes(), "a vertex that the plan does not change keeps its bytes")


class CharacterPackageTests(unittest.TestCase):
    def test_package_is_valid_on_the_profile_rig(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            folder, _ = library(root/"library")
            character = root/"characters/gh3.test"
            skinned_model(character, "0000abcd", "gh3.test")
            write(character/"character.json", {"id": "gh3.test", "name": "Test singer", "profile": "gh3-singer-male-v1", "model": "Test.pak.xen",
                                               "instrumentInModel": False, "instrumentModel": "", "instrumentName": "", "unlock": "start"})
            (root/"out").mkdir()
            unpacked, archive = build.build_character(character, folder, root/"out")
            self.assertEqual(archive.name, "gh3.test.chchar")
            manifest = json.loads((unpacked/"manifest.json").read_text())
            self.assertEqual((manifest["license"], manifest["requires"]), ("LicenseRef-GH3-Derived", [{"id": "gh3-band-v1", "version": "1.0.0"}]))
            self.assertEqual(json.loads((unpacked/"character.json").read_text()), {"version": 1, "library": "gh3-band-v1", "profile": "gh3-singer-male-v1"})
            report = Validator(archive, library=folder).run()
            self.assertEqual((report.errors, report.warnings), ([], []))


@unittest.skipUnless((DATA/"PAK/qb.pak.xen").is_file(), "needs the GH3 PC data")
class InventoryTests(unittest.TestCase):
    def test_inventory(self):
        variants, skipped = characters.inventory(DATA)
        ids = [v["id"] for v in variants]
        self.assertEqual((len(ids), len(set(ids))), (72, 72))
        families = {}
        for v in variants: families[v["id"].split(".")[1]] = families.get(v["id"].split(".")[1], 0) + 1
        self.assertEqual(families, {"axel": 8, "casey": 8, "izzy": 8, "judy": 8, "johnny": 8, "lars": 8, "midori": 8, "xavier": 8, "slash": 1,
                                    "morello": 1, "lou": 2, "god-of-rock": 1, "grim-ripper": 1, "singer-bret": 1, "singer-female": 1})
        judy = next(v for v in variants if v["id"] == "gh3.judy.outfit2.style3")
        self.assertEqual((judy["name"], judy["model"], judy["skeleton"], judy["instrument"], judy["unlock"]),
                         ("Judy Nails: Skool Grrl (Stripes)", "Judy_2_color3.pak.xen", "GH3_Guitarist_Judy_2", "Instrument_SG_Worn_Red", "store (bonus style)"))
        self.assertEqual({v["unlock"] for v in variants if v["id"] in ("gh3.slash", "gh3.lou.outfit1")}, {"store (secret character)"})
        self.assertEqual(sorted(s["profile"] for s in skipped if "debug_only" in s["reason"]),
                         ["BoneMan_Axel", "BoneMan_Judy", "BoneMan_Lars", "BoneMan_Ped", "BoneMan_Xavier"])


@unittest.skipUnless((CHARACTERS/"gh3.axel.outfit1.style1/meshes.bin").is_file() and (LIBRARY/"band/guitarist/meshes.bin").is_file(),
                     "needs the exported characters and the library")
class ExportTests(unittest.TestCase):
    def test_axel_body_equals_the_library_member(self):
        exported = {m: (v, f.tobytes()) for m, _, v, f in runtime.read_meshes((CHARACTERS/"gh3.axel.outfit1.style1/meshes.bin").read_bytes())}
        member = {m: (v, f.tobytes()) for m, _, v, f in runtime.read_meshes((LIBRARY/"band/guitarist/meshes.bin").read_bytes())}
        common = exported.keys() & member.keys()
        self.assertEqual(len(common), 12, "the body and hair parts; the instruments differ (Explorer White, SG Black)")
        self.assertTrue(all(exported[m] == member[m] for m in common))
