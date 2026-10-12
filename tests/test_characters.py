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
from pipeline.paths import CHARACTER_LIBRARY, CHARACTERS, GH3_DATA, LIBRARY
from tests.test_package_build import clip_bytes, library, skinned_model, write

DATA = Path(os.environ.get("GH3_DATA", GH3_DATA))
HEAD = key("bone_head")


def rig(names, parents, positions):
    """A GH3 skeleton tuple (names, parents, positions, rotations) without rotations."""
    rotations = np.tile([0.0, 0.0, 0.0, 1.0], (len(names), 1))
    return list(names), list(parents), np.array(positions, dtype=float), rotations


class CharacterPackageTests(unittest.TestCase):
    def test_package_is_valid_on_the_profile_rig(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            folder = library(root / "library")
            character = root / "characters/gh3.test"
            skinned_model(character, "0000abcd", "gh3.test")
            write(
                character / "character.json",
                {
                    "id": "gh3.test",
                    "name": "Test singer",
                    "profile": "gh3-singer-male-v1",
                    "model": "Test.pak.xen",
                    "instrumentInModel": False,
                    "instrumentModel": "",
                    "instrumentName": "",
                    "unlock": "start",
                },
            )
            (root / "out").mkdir()
            unpacked, archive = build.build_character(character, folder, root / "out")
            self.assertEqual(archive.name, "gh3.test.chchar")
            manifest = json.loads((unpacked / "manifest.json").read_text())
            self.assertEqual(
                (manifest["license"], manifest["requires"]),
                ("LicenseRef-GH3-Derived", [{"id": "gh3-band-v1", "version": "1.0.0"}]),
            )
            self.assertEqual(
                json.loads((unpacked / "character.json").read_text()),
                {"version": 1, "library": "gh3-band-v1", "profile": "gh3-singer-male-v1"},
            )
            report = Validator(archive, library=folder).run()
            self.assertEqual((report.errors, report.warnings), ([], []))


def character_library(root, profile="gh3-guitarist-test-v1"):
    """A small character library folder (pipeline.character_library layout) with one profile on a two-bone rig."""
    bind = [[0, 1, 0, 0, 0, 0, 1], [0, 0.5, 0.25, 0, 0.6, 0, 0.8]]
    joint = {
        "bone": 1,
        "parent": 0,
        "mass": 1.0,
        "friction": 0.5,
        "angularDamping": 0.1,
        "linearDamping": 0.0,
        "position": [0, 0.5, 0.25],
        "rotation": [0, 0.6, 0, 0.8],
        "length": 0.1,
        "type": "prismatic",
        "axis": [0, 1, 0],
        "range": [0, 0.05],
    }
    chain = {
        "bones": ["0000000a", "0000000b", "0000000b"],
        "target": "0000000a",
        "hinge": [0, 0, -1],
        "cosMax": -0.97,
        "cosMin": 0.97,
    }
    clip = {"duration": 1.0, "frames": 31, "rootDistance": 0.0}
    write(
        root / "characters.json",
        {
            "version": 1,
            "library": "gh3-characters-v1",
            "clipRig": {
                "profile": "gh3-guitarist-axel-v1",
                "bones": ["0000000a", "0000000b"],
                "parents": [-1, 0],
                "bind": bind,
            },
            "profiles": {
                profile: {
                    "role": "guitarist",
                    "name": "Test",
                    "outfit": 1,
                    "skeleton": "12345678",
                    "scale": 0.9,
                    "animations": "test",
                    "hair": "87654321",
                    "ik": "hero",
                    "bones": ["0000000a", "0000000b"],
                    "parents": [-1, 0],
                    "bind": bind,
                }
            },
            "animations": {
                "test": {
                    "animSet": "11111111",
                    "groups": {"stance_a/idle/normal/med": ["c1c1c1c1"]},
                    "layers": {},
                    "clips": {"c1c1c1c1": clip},
                }
            },
            "hair": {
                "87654321": {
                    "head": 0,
                    "joints": [joint],
                    "capsule": {"radius": 0.1, "a": [0, 0, 0], "b": [0.1, 0, 0]},
                    "ragdoll": "87654321",
                }
            },
            "ik": {"hero": [chain]},
        },
    )
    write(root / "clips/c1c1c1c1.bin.gz", clip_bytes())
    return root


class CharacterLibraryPackageTests(unittest.TestCase):
    def test_character_library_and_a_character_on_its_profile(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            folder = library(root / "library")
            characters_folder = character_library(root / "characters-library")
            (root / "out").mkdir()
            (_, chlib), clips = build.build_character_library(characters_folder, root / "out", jobs=1)
            self.assertEqual((chlib.name, len(clips)), ("gh3-characters-v1.chlib", 1))
            report = Validator(chlib).run()
            self.assertEqual((report.errors, report.warnings), ([], []))
            manifest = json.loads((root / "out/unpacked/gh3-characters-v1.chlib/manifest.json").read_text())
            self.assertEqual(
                (manifest["format"], manifest["entry"], "minModVersion" in manifest),
                ("chlib/1.0", {"characters": "characters.json"}, False),
            )
            character = root / "characters/gh3.test"
            skinned_model(character, "0000abcd", "gh3.test")
            write(
                character / "character.json",
                {
                    "id": "gh3.test",
                    "name": "Test guitarist",
                    "profile": "gh3-guitarist-test-v1",
                    "model": "Test.pak.xen",
                    "instrumentInModel": False,
                    "instrumentModel": "",
                    "instrumentName": "",
                    "unlock": "start",
                },
            )
            unpacked, archive = build.build_character(character, folder, root / "out", characters_folder)
            manifest = json.loads((unpacked / "manifest.json").read_text())
            self.assertEqual(
                (manifest["format"], "minModVersion" in manifest, [r["id"] for r in manifest["requires"]]),
                ("chchar/1.0", False, ["gh3-band-v1", "gh3-characters-v1"]),
            )
            self.assertEqual(
                json.loads((unpacked / "character.json").read_text()),
                {"version": 1, "library": "gh3-characters-v1", "profile": "gh3-guitarist-test-v1"},
            )
            report = Validator(archive, library=folder, character_library=chlib).run()
            self.assertEqual((report.errors, report.warnings), ([], []))
            report = Validator(archive, library=folder).run()
            self.assertEqual(report.errors, [])
            self.assertTrue(
                any("not checked" in w for w in report.warnings), "without the character library the rig is not checked"
            )

    def test_validator_finds_broken_references(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            folder = character_library(root / "characters-library")
            data = json.loads((folder / "characters.json").read_text())
            data["profiles"]["gh3-guitarist-test-v1"].update({"animations": "missing", "hair": "00000000"})
            data["animations"]["test"]["groups"]["walk"] = ["d2d2d2d2"]
            data["animations"]["other"] = {**data["animations"]["test"], "groups": {}, "clipRig": "none"}
            write(folder / "characters.json", data)
            (root / "out").mkdir()
            (_, chlib), _ = build.build_character_library(folder, root / "out", jobs=1)
            errors = "\n".join(Validator(chlib).run().errors)
            for text in ("no animation set missing", "no hair 00000000", "names clip d2d2d2d2", "no clip rig none"):
                self.assertIn(text, errors)

    def test_band_library_profiles_reject_the_character_library_fields(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            folder = library(root / "library")
            character = root / "characters/gh3.test"
            skinned_model(character, "0000abcd", "gh3.test")
            write(
                character / "character.json",
                {
                    "id": "gh3.test",
                    "name": "Test singer",
                    "profile": "gh3-singer-male-v1",
                    "model": "Test.pak.xen",
                    "instrumentInModel": False,
                    "instrumentModel": "",
                    "instrumentName": "",
                    "unlock": "start",
                },
            )
            (root / "out").mkdir()
            unpacked, _ = build.build_character(character, folder, root / "out")
            write(
                unpacked / "character.json",
                {"version": 1, "library": "gh3-band-v1", "profile": "gh3-guitarist-judy-v1", "animations": "judy"},
            )
            manifest = json.loads((unpacked / "manifest.json").read_text())
            manifest["files"] = {}
            write(unpacked / "manifest.json", manifest)
            errors = "\n".join(Validator(unpacked, library=folder).run().errors)
            self.assertIn("the band library has no profile 'gh3-guitarist-judy-v1'", errors)
            write(
                unpacked / "character.json",
                {"version": 1, "library": "gh3-band-v1", "profile": "gh3-singer-male-v1", "animations": "judy"},
            )
            errors = "\n".join(Validator(unpacked, library=folder).run().errors)
            self.assertIn("'animations' needs a profile of a character library", errors)


@unittest.skipUnless((DATA / "PAK/qb.pak.xen").is_file(), "needs the GH3 PC data")
class InventoryTests(unittest.TestCase):
    def test_inventory(self):
        variants, skipped = characters.inventory(DATA)
        ids = [v["id"] for v in variants]
        self.assertEqual((len(ids), len(set(ids))), (72, 72))
        families = {}
        for v in variants:
            families[v["id"].split(".")[1]] = families.get(v["id"].split(".")[1], 0) + 1
        self.assertEqual(
            families,
            {
                "axel": 8,
                "casey": 8,
                "izzy": 8,
                "judy": 8,
                "johnny": 8,
                "lars": 8,
                "midori": 8,
                "xavier": 8,
                "slash": 1,
                "morello": 1,
                "lou": 2,
                "god-of-rock": 1,
                "grim-ripper": 1,
                "singer-bret": 1,
                "singer-female": 1,
            },
        )
        judy = next(v for v in variants if v["id"] == "gh3.judy.outfit2.style3")
        self.assertEqual(
            (judy["name"], judy["model"], judy["skeleton"], judy["instrument"], judy["unlock"]),
            (
                "Judy Nails: Skool Grrl (Stripes)",
                "Judy_2_color3.pak.xen",
                "GH3_Guitarist_Judy_2",
                "Instrument_SG_Worn_Red",
                "store (bonus style)",
            ),
        )
        self.assertEqual(
            {v["unlock"] for v in variants if v["id"] in ("gh3.slash", "gh3.lou.outfit1")}, {"store (secret character)"}
        )
        self.assertEqual(
            sorted(s["profile"] for s in skipped if "debug_only" in s["reason"]),
            ["BoneMan_Axel", "BoneMan_Judy", "BoneMan_Lars", "BoneMan_Ped", "BoneMan_Xavier"],
        )
        profiles = {v["id"]: v["profile"] for v in variants}
        self.assertEqual(
            [
                profiles[i]
                for i in (
                    "gh3.axel.outfit1.style2",
                    "gh3.axel.outfit2.style1",
                    "gh3.judy.outfit2.style3",
                    "gh3.lou.outfit2",
                    "gh3.god-of-rock",
                    "gh3.grim-ripper",
                    "gh3.singer-female",
                )
            ],
            [
                "gh3-guitarist-axel-v1",
                "gh3-guitarist-axel-outfit2-v1",
                "gh3-guitarist-judy-outfit2-v1",
                "gh3-guitarist-lou-v1",
                "gh3-guitarist-god-of-rock-v1",
                "gh3-guitarist-grim-ripper-v1",
                "gh3-singer-female-v1",
            ],
        )


class AccessorySkeletonTests(unittest.TestCase):
    def test_with_accessories_moves_only_the_accessory_bones(self):
        from pipeline.character_library import with_accessories

        names = [1, HEAD, key("Bone_ACC_01"), key("Bone_ACC_02"), 2]
        body = rig(names, [-1, 0, 0, 2, 1], [[0, 0, 0], [0, 1, 0], [0, 0.5, 0], [0, 0.1, 0], [0, 0.1, 0]])
        other = rig(names, [-1, 0, 1, 2, 1], [[0, 0, 0], [0, 1.1, 0], [0, 0.2, 0], [0, 0.2, 0], [0, 0.2, 0]])
        _, parents, positions, _ = with_accessories(body, other, {key("Bone_ACC_01"), key("Bone_ACC_02")})
        self.assertEqual(parents, [-1, 0, 1, 2, 1])
        np.testing.assert_allclose(positions, [[0, 0, 0], [0, 1, 0], [0, 0.2, 0], [0, 0.2, 0], [0, 0.1, 0]])


@unittest.skipUnless(
    (DATA / "PAK/qb.pak.xen").is_file() and (CHARACTER_LIBRARY / "characters.json").is_file(),
    "needs the GH3 PC data and the character library",
)
class CharacterLibraryTests(unittest.TestCase):
    def test_profiles_and_sets(self):
        data = json.loads((CHARACTER_LIBRARY / "characters.json").read_text())
        self.assertEqual(len(data["profiles"]), 22)
        self.assertEqual(
            sorted(data["animations"]),
            [
                "axel",
                "casey",
                "grim-ripper",
                "izzy",
                "johnny",
                "judy",
                "lars",
                "lou",
                "midori",
                "morello",
                "singer-female",
                "singer-male",
                "slash",
                "xavier",
            ],
        )
        profiles = data["profiles"]
        self.assertEqual(
            [
                (profiles[p]["role"], profiles[p]["animations"], profiles[p]["hair"], profiles[p]["ik"])
                for p in ("gh3-singer-female-v1", "gh3-singer-bret-v1")
            ],
            [("singer", "singer-female", "cd277aa1", "singer"), ("singer", "singer-male", "8efb7275", "singer")],
        )
        self.assertEqual(
            {name: row.get("clipRig") for name, row in data["animations"].items() if name.startswith("singer")},
            {"singer-female": "singer", "singer-male": "singer"},
        )
        self.assertEqual(
            (
                profiles["gh3-guitarist-judy-v1"]["scale"],
                profiles["gh3-guitarist-lou-v1"]["ik"],
                profiles["gh3-guitarist-grim-ripper-v1"]["hair"],
                profiles["gh3-guitarist-god-of-rock-v1"]["animations"],
                profiles["gh3-guitarist-grim-ripper-v1"]["animations"],
            ),
            (0.8999999761581421, "satan", None, "axel", "grim-ripper"),
        )
        self.assertEqual(
            data["animations"]["grim-ripper"]["clips"],
            data["animations"]["morello"]["clips"],
            "the Grim Ripper plays Morello's clips",
        )
        self.assertNotEqual(
            data["animations"]["grim-ripper"]["layers"]["strum/default/short"],
            data["animations"]["morello"]["layers"]["strum/default/short"],
        )

    def test_a_set_clip_with_the_profile_offset_is_the_gh3_clip_on_the_gh3_skeleton(self):
        """Judy's clips are stored on the clip rig (Axel); with the offset of section 10.1 they equal the GH3 clip on Judy's skeleton."""
        from pipeline.gh3.band_formats import animation, sample_animation, skeleton
        from pipeline.gh3.data import Source

        data = json.loads((CHARACTER_LIBRARY / "characters.json").read_text())
        profile = data["profiles"]["gh3-guitarist-judy-v1"]
        offset = np.array(profile["bind"])[:, :3] - np.array(data["clipRig"]["bind"])[:, :3]
        source = Source(DATA)
        judy = skeleton(
            next(
                b
                for k, n, b in source.load("ZONES/global.pak.xen")
                if k == key(".ske") and n == int(profile["skeleton"], 16)
            )
        )
        bank = {n: b for k, n, b in source.load("PAK/Judy_1_anims.pak.xen") if k == key(".ska")}
        clip = data["animations"]["judy"]["groups"]["stance_a/idle/normal/med"][0]
        bones, duration, tracks, poses = runtime.read_clip(
            __import__("gzip").decompress((CHARACTER_LIBRARY / "clips" / f"{clip}.bin.gz").read_bytes())
        )
        times = np.linspace(0, duration, len(poses))
        p, q = sample_animation(animation(bank[int(clip, 16)], len(judy[0])), judy[2], judy[3], times)
        worst = 0.0
        for track, (bone, mask) in enumerate(tracks):
            if mask & 1:
                worst = max(worst, float(np.abs(poses[:, track, :3] + offset[bone] - p[:, bone] * [1, 1, -1]).max()))
            if mask & 2:
                np.testing.assert_allclose(
                    np.abs((poses[:, track, 3:] * q[:, bone] * [1, 1, -1, 1]).sum(1)), 1, atol=1e-5
                )
        self.assertLess(worst, 1e-5)

    def test_singer_clips_with_the_profile_offset_are_the_gh3_clips_on_her_skeleton(self):
        """The female singer's set clips (on the singer clip rig) and a song facial clip of Barracuda (on the band library
        singer rig) with the offsets of section 10.1 equal the GH3 clips on GH3_Singer_Female."""
        import gzip

        from pipeline.gh3.band_formats import animation, sample_animation, skeleton
        from pipeline.gh3.data import Source

        data = json.loads((CHARACTER_LIBRARY / "characters.json").read_text())
        profile = data["profiles"]["gh3-singer-female-v1"]
        clip_rig = data["clipRigs"][data["animations"]["singer-female"]["clipRig"]]
        offset = np.array(profile["bind"])[:, :3] - np.array(clip_rig["bind"])[:, :3]
        source = Source(DATA)
        female = skeleton(
            next(
                b
                for k, n, b in source.load("ZONES/global.pak.xen")
                if k == key(".ske") and n == int(profile["skeleton"], 16)
            )
        )

        def worst(stored, gh3):
            bones, duration, tracks, poses = runtime.read_clip(gzip.decompress(stored.read_bytes()))
            times = np.linspace(0, duration, len(poses))
            p, q = sample_animation(animation(gh3, len(female[0])), female[2], female[3], times)
            result = 0.0
            for track, (bone, mask) in enumerate(tracks):
                if mask & 1:
                    result = max(
                        result, float(np.abs(poses[:, track, :3] + offset[bone] - p[:, bone] * [1, 1, -1]).max())
                    )
                if mask & 2:
                    np.testing.assert_allclose(
                        np.abs((poses[:, track, 3:] * q[:, bone] * [1, 1, -1, 1]).sum(1)), 1, atol=1e-5
                    )
            return result

        bank = {n: b for k, n, b in source.load("PAK/Singer_Female_anims.pak.xen") if k == key(".ska")}
        clip = data["animations"]["singer-female"]["groups"]["stance_a/idle/normal/med"][0]
        self.assertLess(worst(CHARACTER_LIBRARY / "clips" / f"{clip}.bin.gz", bank[int(clip, 16)]), 1e-5)
        song = {n: b for k, n, b in source.load("SONGS/barracuda_song.pak.xen") if k == key(".ska")}
        face = min(song)
        self.assertLess(worst(LIBRARY / "direction/singer" / f"{face:08x}.bin.gz", song[face]), 1e-5)

    def test_god_of_rock_hair_fits_his_ragdoll(self):
        """GH3 gives the God of Rock Axel's skeleton; his ragdoll fits GH3_Guitarist_RockGod, so his rig takes its accessory bones."""
        from pipeline.character_library import unity_rig
        from pipeline.gh3.band_formats import skeleton
        from pipeline.gh3.data import Source
        from pipeline.gh3.havok import Ragdoll
        from pipeline.library import hair

        source = Source(DATA)
        entries = source.load("ZONES/global.pak.xen")
        rigs = {n: b for k, n, b in entries if k == key(".ske")}
        axel, own = (unity_rig(skeleton(rigs[key(n)])) for n in ("GH3_Guitarist_Axel", "GH3_Guitarist_RockGod"))
        profile = json.loads((CHARACTER_LIBRARY / "characters.json").read_text())["profiles"][
            "gh3-guitarist-god-of-rock-v1"
        ]
        self.assertEqual(profile["skeleton"], f"{key('GH3_Guitarist_Axel'):08x}")
        accessories = {f"{key(f'Bone_ACC_{i:02d}'):08x}" for i in range(1, 16)}
        for i, bone in enumerate(profile["bones"]):
            expected = own if bone in accessories else axel
            self.assertEqual((profile["parents"][i], profile["bind"][i]), (expected["parents"][i], expected["bind"][i]))
        ragdoll = Ragdoll(next(b for k, n, b in entries if k == key(".rag") and n == 0xA494694F))
        with self.assertRaisesRegex(ValueError, "does not fit the skeleton"):
            hair.ragdoll_member(
                ragdoll,
                skeleton(rigs[key("GH3_Guitarist_Axel")]),
                hair.accessory_bones(source.scripts()),
                all_chains=True,
            )


@unittest.skipUnless(
    (CHARACTERS / "gh3.axel.outfit1.style1/meshes.bin").is_file() and (LIBRARY / "band/guitarist/meshes.bin").is_file(),
    "needs the exported characters and the library",
)
class ExportTests(unittest.TestCase):
    def test_axel_body_equals_the_library_member(self):
        exported = {
            m: (v, f.tobytes())
            for m, _, v, f in runtime.read_meshes((CHARACTERS / "gh3.axel.outfit1.style1/meshes.bin").read_bytes())
        }
        member = {
            m: (v, f.tobytes())
            for m, _, v, f in runtime.read_meshes((LIBRARY / "band/guitarist/meshes.bin").read_bytes())
        }
        common = exported.keys() & member.keys()
        self.assertEqual(len(common), 12, "the body and hair parts; the instruments differ (Explorer White, SG Black)")
        self.assertTrue(all(exported[m] == member[m] for m in common))
