"""Tests of the character tools that run without Blender: the pure-Python Brotli decoder of the add-on, the shared
character module (pipeline/package/character.py: model split, library members as packages, hair chains, clip poses with
the game IK, weight limits, character.json), and the character comparison. blender/test.sh tests the Blender parts."""

import importlib
import importlib.util
import json
import random
import sys
import tempfile
import types
import unittest
import zipfile
from pathlib import Path

import numpy as np

from pipeline.package import character, compare, q48
from pipeline.package.glb import Glb
from pipeline.package.validate import Validator, gh3_key

ROOT = Path(__file__).resolve().parents[1]
ADDON = ROOT / "blender" / "chstage_tools"
LIBRARY = ROOT / "assets" / "packages" / "gh3-band-v1.chlib"
ROLES = ("guitarist", "bassist", "singer", "drummer")
ATTACHMENT_PARTS = {"guitarist": [11, 12, 13], "bassist": [1, 2], "singer": [4, 5], "drummer": [1]}


def addon_module(name):
    package = "chstage_tools_nobpy"
    if package not in sys.modules:
        module = types.ModuleType(package)
        module.__path__ = [str(ADDON)]
        sys.modules[package] = module
    return importlib.import_module(f"{package}.{name}")


brotli_decode = addon_module("brotli_decode")


def bone(name):
    return f"bone-{gh3_key(name):08x}"


class BrotliDecoderTests(unittest.TestCase):
    def test_dictionary_is_the_rfc_dictionary(self):
        self.assertEqual(len(brotli_decode.dictionary()), brotli_decode.DICTIONARY_SIZE)

    def test_decoder_equals_the_brotli_module(self):
        import brotli

        random.seed(7)
        texts = [
            (ROOT / "docs" / "PACKAGE-FORMAT.md").read_bytes()[:60000],
            b"",
            b"x",
            "Grüße, ünïcode text. ".encode() * 200,
            bytes(random.getrandbits(8) for _ in range(3000)),
            bytes(range(256)) * 40,
        ]
        for text in texts:
            for quality in (0, 2, 5, 11):
                for mode in (brotli.MODE_GENERIC, brotli.MODE_TEXT):
                    for window in (10, 16, 24):
                        data = brotli.compress(text, quality=quality, mode=mode, lgwin=window)
                        self.assertEqual(brotli_decode.decompress(data), text, (len(text), quality, mode, window))

    @unittest.skipUnless(LIBRARY.exists(), "no library package")
    def test_library_clips(self):
        import brotli

        library = character.Library(LIBRARY)
        names = sorted(n for n in library.source.names if n.endswith(".q48.br"))
        for name in names[::60]:
            data = library.source.read(name)
            self.assertEqual(brotli_decode.decompress(data), brotli.decompress(data), name)

    def test_damaged_stream(self):
        import brotli

        data = bytearray(brotli.compress(b"hello hello hello world" * 50, quality=11))
        with self.assertRaises(brotli_decode.BrotliError):
            brotli_decode.decompress(bytes(data[: len(data) // 2]))


class WeightTests(unittest.TestCase):
    def test_limit_weights_keeps_four_and_normalizes(self):
        weights = np.array([[0.5, 0.1, 0.2, 0.05, 0.15, 0.0], [0, 0, 0, 0, 0, 0], [2.0, 2.0, 0, 0, 0, 0]])
        limited = character.limit_weights(weights)
        self.assertEqual(list((limited > 0).sum(1)), [4, 0, 2])
        np.testing.assert_allclose(limited.sum(1), [1, 0, 1])
        self.assertEqual(limited[0, 3], 0)
        np.testing.assert_allclose(limited[2, :2], [0.5, 0.5])

    def test_quaternion_matrices_round_trip(self):
        rng = np.random.default_rng(3)
        q = rng.normal(size=(500, 4))
        q /= np.linalg.norm(q, axis=1, keepdims=True)
        back = character.matrix_quaternions(character.quaternion_matrices(q))
        self.assertLess(np.minimum(np.abs(back - q).max(1), np.abs(back + q).max(1)).max(), 1e-12)

    def test_character_json(self):
        value = character.character_json("gh3-singer-male-v1", ["hair"], "Microphone001")
        self.assertEqual(
            value,
            {
                "version": 1,
                "library": "gh3-band-v1",
                "profile": "gh3-singer-male-v1",
                "hairMaterials": ["hair"],
                "attachments": {"instrument": "Microphone001"},
            },
        )
        self.assertEqual(character.open_character_json(value)["instrument"], "Microphone001")
        self.assertEqual(
            character.character_json("gh3-drummer-v1"),
            {"version": 1, "library": "gh3-band-v1", "profile": "gh3-drummer-v1"},
        )

    def test_bone_labels(self):
        self.assertEqual(character.bone_label(bone("bone_head")), "bone_head")
        self.assertEqual(character.bone_label("bone-12345678"), "12345678")


@unittest.skipUnless(LIBRARY.exists(), "no library package")
class LibraryCharacterTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.library = character.Library(LIBRARY)

    def rig(self, role):
        return character.Rig(Glb(self.library.model(role)))

    def test_model_split(self):
        expected = {
            "guitarist": ("guitar", 3),
            "bassist": ("bass", 2),
            "singer": ("microphone and stand", 2),
            "drummer": ("drum kit and sticks", 1),
        }
        for role in ROLES:
            data = self.library.model(role)
            glb = Glb(data)
            hair = character.hair_joints(self.library.json("band/hair.json"), role)
            parts = character.split_model(data, hair)
            kinds = list(parts["kinds"].values())
            self.assertEqual(kinds.count("attachment"), expected[role][1], role)
            # The game splits the cache band model with the same rule (src/Packages/BandAttachments.cs); tests/AttachmentLogicTests.cs
            # expects the same parts.
            self.assertEqual(
                [p for (_, p), kind in sorted(parts["kinds"].items()) if kind == "attachment"],
                ATTACHMENT_PARTS[role],
                role,
            )
            self.assertEqual(kinds.count("hair"), 1, role)
            sorted_hair = sorted(
                m["name"] for m in glb.list("materials") if m.get("extras", {}).get("gh3SortedTransparency")
            )
            self.assertEqual(parts["hairMaterials"], sorted_hair, role)
            for part in ("body", "attachment"):
                split = Glb(parts[part])
                used = {p["material"] for m in split.list("meshes") for p in m["primitives"]}
                self.assertEqual(used, set(range(len(split.list("materials")))), (role, part))
                self.assertEqual(split.list("skins")[0]["joints"], glb.list("skins")[0]["joints"], (role, part))
                self.assertFalse(split.list("animations"))
                for accessor in range(len(split.list("accessors"))):
                    split.accessor(accessor)

    def test_members_are_valid_packages(self):
        with tempfile.TemporaryDirectory() as temporary:
            for role in ROLES:
                target = character.write_member(self.library, role, Path(temporary))
                report = Validator(target, LIBRARY).run()
                self.assertEqual((report.errors, report.warnings), ([], []), role)
                with zipfile.ZipFile(target) as package:
                    value = json.loads(package.read("character.json"))
                    manifest = json.loads(package.read("manifest.json"))
                self.assertEqual(value["profile"], character.ROLE_PROFILES[role])
                self.assertEqual(manifest["format"], "chchar/1.0")
                self.assertEqual(manifest["entry"], {"model": "character.glb", "character": "character.json"})
                again = character.write_member(self.library, role, Path(temporary) / "again")
                self.assertEqual(again.read_bytes(), target.read_bytes(), role)

    def test_instrument_names_are_checked(self):
        from pipeline.package import container

        with tempfile.TemporaryDirectory() as temporary:
            character.write_member(self.library, "bassist", Path(temporary))
            folder = Path(temporary) / "unpacked" / "gh3.bassist.chchar"
            for instrument, warned in (("tobB4", False), ("Kazoo", True)):
                value = json.loads((folder / "character.json").read_text())
                value["attachments"] = {"instrument": instrument}
                (folder / "character.json").write_bytes(container.json_bytes(value))
                manifest = json.loads((folder / "manifest.json").read_text())
                manifest["files"] = container.file_list(folder, list(manifest["files"]))
                (folder / "manifest.json").write_bytes(container.json_bytes(manifest))
                report = Validator(folder, LIBRARY).run()
                self.assertEqual(report.errors, [], instrument)
                self.assertEqual(any("Kazoo" in w for w in report.warnings), warned, (instrument, report.warnings))

    def test_hair_chains(self):
        counts = {"guitarist": (5, 9), "bassist": (12, 15), "singer": (7, 15), "drummer": (9, 15)}
        for role in ROLES:
            rig = self.rig(role)
            chains = character.hair_chains(self.library.json("band/hair.json"), role, rig)
            self.assertEqual((len(chains), sum(map(len, chains))), counts[role], role)
            head = rig.index(bone("bone_head"))
            for chain in chains:
                self.assertEqual(rig.parents[rig.index(chain[0])], head, role)

    def test_clip_list(self):
        items = character.clip_items(
            self.library.json("catalog.json"), self.library.json("band/band.json"), "guitarist"
        )
        self.assertEqual(len(items), 356)
        self.assertEqual([i[1] for i in items[:2]], ["gh3_guit_axel_a_fst_idle1", "gh3_guit_axel_a_med_idle2"])
        self.assertTrue(all(self.library.has_clip("guitarist", clip) for clip, *_ in items))

    def decoded(self, role, clip):
        import brotli

        return q48.decode(brotli.decompress(self.library.clip(role, clip)))

    def test_clip_poses_keep_the_bind_pose_of_channels_that_the_clip_does_not_animate(self):
        rig = self.rig("singer")
        clip = f"{gh3_key('gh3_singer_male_med_singidle2'):08x}"
        decoded = self.decoded("singer", clip)
        translations, quaternions = character.clip_local_poses(decoded, rig, "singer")
        animated = {bone for bone, mask in decoded[0]["tracks"] if mask & 1}
        still = [j for j in range(len(rig.names)) if j not in animated]
        np.testing.assert_allclose(
            translations[:, still], np.repeat(rig.local[still][None, :, :3, 3], len(translations), 0), atol=1e-12
        )
        world = character.world_poses(
            rig,
            translations[:1] * 0 + rig.local[None, :, :3, 3],
            character.matrix_quaternions(rig.local[None, :, :3, :3]),
        )
        np.testing.assert_allclose(world[0], rig.world, rtol=0, atol=1e-6)

    def test_game_ik_puts_the_hands_on_the_guitar(self):
        rig = self.rig("guitarist")
        clip = f"{gh3_key('gh3_guit_axel_a_fst_idle1'):08x}"
        decoded = self.decoded("guitarist", clip)
        raw = character.clip_local_poses(decoded, rig)
        solved = character.clip_local_poses(decoded, rig, "guitarist")
        for side in ("R", "L"):
            palm, target = rig.index(bone(f"Bone_Palm_{side}")), rig.index(bone(f"Bone_IK_Hand_Guitar_{side}"))
            before = np.linalg.norm(
                character.world_poses(rig, *raw)[:, palm, :3, 3] - character.world_poses(rig, *raw)[:, target, :3, 3],
                axis=-1,
            )
            world = character.world_poses(rig, *solved)
            after = np.linalg.norm(world[:, palm, :3, 3] - world[:, target, :3, 3], axis=-1)
            self.assertGreater(before.min(), 0.1, side)
            # The game limits the elbow angle (cosine within 0.97): the fret hand reaches the far neck target only in part of the frames.
            self.assertLess(after.max() if side == "R" else after.min(), 1e-3, side)
            self.assertTrue((after < before).all(), side)
            np.testing.assert_allclose(world[:, palm, :3, :3], world[:, target, :3, :3], atol=1e-6)
        pelvis = rig.index(bone("bone_pelvis"))
        np.testing.assert_allclose(solved[0][:, pelvis], raw[0][:, pelvis])


@unittest.skipUnless(LIBRARY.exists(), "no library package")
class CharacterCompareTests(unittest.TestCase):
    def test_joint_order_does_not_matter(self):
        library = character.Library(LIBRARY)
        with tempfile.TemporaryDirectory() as temporary:
            source = character.write_member(library, "drummer", Path(temporary))
            folder = Path(temporary) / "unpacked" / source.name
            glb = Glb((folder / "character.glb").read_bytes())
            gltf, binary = json.loads(json.dumps(glb.json)), bytearray(glb.binary)
            skin = gltf["skins"][0]
            count = len(skin["joints"])
            order = list(range(count))[::-1]
            skin["joints"] = [skin["joints"][i] for i in order]
            inverse = glb.accessor(skin["inverseBindMatrices"])[order]

            def add(values, accessor):
                data = np.ascontiguousarray(values).tobytes()
                binary.extend(b"\0" * (-len(binary) % 4))
                gltf["bufferViews"].append({"buffer": 0, "byteOffset": len(binary), "byteLength": len(data)})
                binary.extend(data)
                gltf["accessors"].append({**accessor, "bufferView": len(gltf["bufferViews"]) - 1})
                gltf["accessors"][-1].pop("byteOffset", None)
                return len(gltf["accessors"]) - 1

            skin["inverseBindMatrices"] = add(inverse.astype("<f4"), gltf["accessors"][skin["inverseBindMatrices"]])
            remap = np.argsort(order)
            for mesh in gltf["meshes"]:
                for primitive in mesh["primitives"]:
                    index = primitive["attributes"]["JOINTS_0"]
                    joints = glb.accessor(index).astype(np.int64)
                    primitive["attributes"]["JOINTS_0"] = add(
                        remap[joints].astype("<u2"), {**gltf["accessors"][index], "componentType": 5123}
                    )
            gltf["buffers"][0]["byteLength"] = len(binary)
            (folder / "character.glb").write_bytes(character.write_glb(gltf, bytes(binary)))
            manifest = json.loads((folder / "manifest.json").read_text())
            manifest["files"] = {}
            (folder / "manifest.json").write_text(json.dumps(manifest))
            result = compare.compare_packages(source, folder)
            self.assertEqual(result["failures"], [])
            self.assertEqual(result["stage"]["max"]["SKIN"], 0)
            self.assertEqual(result["stage"]["max"]["inverseBind"], 0)


CHARACTER_LIBRARY = ROOT / "assets" / "packages" / "gh3-characters-v1.chlib"
MIDORI = "gh3-guitarist-midori-v1"


class CharacterJsonTests(unittest.TestCase):
    def test_fixed_profile(self):
        value = character.character_json("gh3-guitarist-axel-v1", ["hair"])
        self.assertEqual(
            value,
            {"version": 1, "library": "gh3-band-v1", "profile": "gh3-guitarist-axel-v1", "hairMaterials": ["hair"]},
        )
        self.assertEqual(character.package_format(value["library"]), "chchar/1.0")
        description = character.complete_requirements(
            {"requires": [{"id": "gh3-band-v1", "version": "1.0.0"}], "minModVersion": ""}
        )
        self.assertEqual(description, {"requires": [{"id": "gh3-band-v1", "version": "1.0.0"}], "minModVersion": ""})

    def test_character_library_profile(self):
        value = character.character_json(MIDORI, [], "", "gh3-characters-v1", "midori")
        self.assertEqual(
            value, {"version": 1, "library": "gh3-characters-v1", "profile": MIDORI, "animations": "midori"}
        )
        self.assertEqual(character.open_character_json(value)["animations"], "midori")
        self.assertEqual(character.package_format("gh3-characters-v1"), "chchar/1.0")
        description = character.complete_requirements({"requires": [], "minModVersion": ""}, "gh3-characters-v1")
        self.assertEqual([r["id"] for r in description["requires"]], ["gh3-band-v1", "gh3-characters-v1"])
        self.assertEqual(description["minModVersion"], "")


@unittest.skipUnless(CHARACTER_LIBRARY.exists(), "no character library package")
class CharacterLibraryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.library = character.CharacterLibrary(CHARACTER_LIBRARY)

    def test_profiles_and_roles(self):
        self.assertEqual(self.library.id, "gh3-characters-v1")
        self.assertIn(MIDORI, self.library.profiles)
        self.assertEqual(self.library.role(MIDORI), "guitarist")
        self.assertEqual(self.library.label(MIDORI), "Midori (outfit 1)")

    def test_rig_from_bind_equals_the_gh3_character_package(self):
        packages = character.profile_packages(CHARACTER_LIBRARY.parent, MIDORI)
        self.assertEqual(packages[0].name, "gh3.midori.outfit1.style1.chchar")
        self.assertTrue(
            all(
                character.open_character_json(json.loads(zipfile.ZipFile(p).read("character.json")))["profile"]
                == MIDORI
                for p in packages
            )
        )
        glb_rig = character.Rig(Glb(character.profile_model(packages[0])))
        rig = self.library.rig(MIDORI)
        self.assertEqual(rig.names, glb_rig.names)
        self.assertEqual(rig.parents, glb_rig.parents)
        np.testing.assert_allclose(rig.world, glb_rig.world, atol=1e-5)

    def test_gh3_package_comes_before_a_custom_character_on_the_profile(self):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            source = character.profile_packages(CHARACTER_LIBRARY.parent, MIDORI)[0]
            for name, package_id in (("a-custom.chchar", "custom.idol"), (source.name, source.stem)):
                with zipfile.ZipFile(source) as original, zipfile.ZipFile(folder / name, "w") as copy:
                    for item in original.infolist():
                        data = original.read(item)
                        if item.filename == "manifest.json":
                            manifest = json.loads(data)
                            manifest["id"] = package_id
                            data = json.dumps(manifest).encode()
                        copy.writestr(item, data)
            self.assertEqual(
                [p.name for p in character.profile_packages(folder, MIDORI)], [source.name, "a-custom.chchar"]
            )

    def test_hair_chains_follow_the_profile_hair(self):
        chains = self.library.hair_chains(MIDORI)
        joints = [j["bone"] for j in self.library.hair(MIDORI)]
        self.assertEqual(
            sorted(n for chain in chains for n in chain), sorted(self.library.rig(MIDORI).names[j] for j in joints)
        )
        self.assertEqual(len(chains[0]), 3)
        self.assertEqual(self.library.hair_chains("gh3-guitarist-grim-ripper-v1"), [])

    def test_split_of_a_gh3_character_finds_its_instrument(self):
        package = character.profile_packages(CHARACTER_LIBRARY.parent, MIDORI)[0]
        parts = character.split_model(character.profile_model(package), self.library.hair_joints(MIDORI))
        self.assertIsNotNone(parts["attachment"])
        self.assertTrue(parts["hairMaterials"])
        self.assertIn("attachment", parts["kinds"].values())

    def test_clips_play_with_the_rest_offsets_of_the_profile(self):
        items = self.library.clip_items(MIDORI)
        self.assertGreater(len(items), 100)
        clip = next(
            clip for clip, label, _, _ in items if label.startswith("stance_a/idle") and self.library.has_clip(clip)
        )
        decoded = q48.decode(brotli_decode.decompress(self.library.clip(clip)))
        rig = self.library.rig(MIDORI)
        offsets = self.library.clip_offsets(MIDORI)
        moved, _ = character.clip_local_poses(decoded, rig, None, offsets)
        plain, _ = character.clip_local_poses(decoded, rig)
        knee = rig.index(bone("bone_knee_l"))
        rest = rig.local[knee, :3, 3]
        # The knee keeps the thigh length of the profile with the offsets, and gets the clip rig length without them.
        self.assertLess(np.abs(np.linalg.norm(moved[:, knee], axis=-1) - np.linalg.norm(rest)).max(), 1e-3)
        self.assertGreater(np.abs(np.linalg.norm(plain[:, knee], axis=-1) - np.linalg.norm(rest)).max(), 0.02)


class BuildTests(unittest.TestCase):
    def test_add_on_bundles_the_character_module_and_the_decoder(self):
        spec = importlib.util.spec_from_file_location("chstage_build_characters", ROOT / "blender" / "build.py")
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        self.assertIn("character.py", module.VALIDATOR_FILES)
        names = {relative for _, relative in module.files()}
        self.assertTrue(
            {
                "brotli_decode.py",
                "brotli_dictionary.bin",
                "character_tools.py",
                "character_package.py",
                "character_ui.py",
            }
            <= names
        )


if __name__ == "__main__":
    unittest.main()
