"""Tests of the venue tools of the Blender add-on that run without Blender: actors.json, actor GLB edits, rig anchors,
the script bank index and edits, and the lighting preview values. blender/test.sh tests the Blender parts."""

import json
import math
import unittest
from pathlib import Path

import numpy as np

from tests.addon import addon_module

ROOT = Path(__file__).resolve().parents[1]
ADDON = ROOT / "blender" / "chstage_tools"
PACKAGES = ROOT / "assets" / "packages"
UNPACKED = PACKAGES / "unpacked"


sidecars = addon_module("sidecars")
glb_tools = addon_module("glb_tools")
rig = addon_module("rig")
script_bank = addon_module("script_bank")


def close(test, a, b, path=""):
    if isinstance(a, dict):
        test.assertEqual(set(a), set(b), path)
        for key in a:
            close(test, a[key], b[key], f"{path}.{key}")
    elif isinstance(a, list):
        test.assertEqual(len(a), len(b), path)
        sign = -1 if len(a) == 4 and all(isinstance(v, (int, float)) for v in a) and np.dot(a, b) < 0 else 1
        for i, (x, y) in enumerate(zip(a, b)):
            close(test, x, sign * y if isinstance(y, (int, float)) and not isinstance(y, bool) else y, f"{path}[{i}]")
    elif isinstance(a, (int, float)) and not isinstance(a, bool):
        test.assertAlmostEqual(a, b, delta=1e-5, msg=path)
    else:
        test.assertEqual(a, b, path)


class GlbBuilder:
    """A small GLB: nodes, one skin, float accessors, animations."""

    def __init__(self):
        self.gltf = {
            "asset": {"version": "2.0"},
            "buffers": [{"byteLength": 0}],
            "bufferViews": [],
            "accessors": [],
            "nodes": [],
            "scenes": [{"nodes": []}],
        }
        self.binary = bytearray()

    def accessor(self, values, kind):
        array = np.asarray(values, dtype="<f4")
        self.binary.extend(b"\0" * (-len(self.binary) % 4))
        self.gltf["bufferViews"].append({"buffer": 0, "byteOffset": len(self.binary), "byteLength": array.nbytes})
        self.binary.extend(array.tobytes())
        self.gltf["accessors"].append(
            {"bufferView": len(self.gltf["bufferViews"]) - 1, "componentType": 5126, "count": len(array), "type": kind}
        )
        return len(self.gltf["accessors"]) - 1

    def node(self, name, parent=None, **fields):
        self.gltf["nodes"].append({"name": name, **fields})
        index = len(self.gltf["nodes"]) - 1
        if parent is None:
            self.gltf["scenes"][0]["nodes"].append(index)
        else:
            self.gltf["nodes"][parent].setdefault("children", []).append(index)
        return index

    def animation(self, name, channels):
        animation = {"name": name, "channels": [], "samplers": []}
        for node, path, times, values in channels:
            animation["samplers"].append(
                {
                    "input": self.accessor(np.reshape(times, (-1, 1)), "SCALAR"),
                    "output": self.accessor(values, {3: "VEC3", 4: "VEC4"}[np.shape(values)[1]]),
                }
            )
            animation["channels"].append(
                {"sampler": len(animation["samplers"]) - 1, "target": {"node": node, "path": path}}
            )
        self.gltf.setdefault("animations", []).append(animation)

    def bytes(self):
        self.gltf["buffers"][0]["byteLength"] = len(self.binary)
        return glb_tools.write_glb(self.gltf, bytes(self.binary))


def two_bone_rig():
    builder = GlbBuilder()
    head = sidecars.joint_name("head")
    root = builder.node("bone-00000001", translation=[0, 0, 0])
    builder.node(head, root, translation=[0, 1.5, 0.25])
    turn = [0, math.sin(math.pi / 4), 0, math.cos(math.pi / 4)]
    builder.animation(
        "idle",
        [(root, "rotation", [0, 1], [turn, [0, 0, 0, 1]]), (root, "scale", [0, 1], [[1, 1, 1], [1.00001, 1, 1]])],
    )
    builder.animation("other", [(root, "translation", [0, 1], [[0, 0, 0], [1, 0, 0]])])
    return builder.bytes(), head


class RigTests(unittest.TestCase):
    def test_joint_frames_use_the_first_key_and_runtime_coordinates(self):
        data, head = two_bone_rig()
        frames = rig.joint_frames(data)
        position, rotation = frames[head]
        # glTF: the root turns +90 degrees about Y, so the head offset (0, 1.5, 0.25) goes to (0.25, 1.5, 0); runtime negates Z.
        np.testing.assert_allclose(position, [0.25, 1.5, 0], atol=1e-6)
        np.testing.assert_allclose(rotation @ [1, 0, 0], [0, 0, 1], atol=1e-6)
        self.assertIn(head, rig.anchor_frames(data))

    def test_anchor_names(self):
        self.assertEqual(sidecars.joint_name("head"), f"bone-{sidecars.qb_key('bone_head'):08x}")
        self.assertEqual(sidecars.joint_name("bone", "c00e3395"), "bone-c00e3395")
        self.assertEqual(sidecars.joint_name("bone", "Bone_Palm_L"), sidecars.joint_name("hand_left"))
        self.assertEqual(sidecars.checksum_text("name"), "0xa1dc81f9")

    def test_library_rig_head_is_at_head_height(self):
        model = UNPACKED / "gh3-band-v1.chlib" / "band" / "guitarist" / "model.glb"
        if not model.is_file():
            self.skipTest("assets/packages is not generated")
        frames = rig.anchor_frames(model.read_bytes())
        position, _ = rig.frame_of(frames[sidecars.joint_name("head")])
        self.assertTrue(1.3 < position[1] < 2.0, position)
        self.assertIn("bone-c00e3395", frames)

    def test_bone_anchor_uses_the_rig_frame(self):
        data, head = two_bone_rig()
        anchors = {"guitarist": rig.anchor_frames(data)}
        placements = {
            "version": 1,
            "band": {"guitarist": {"position": [2, 0, 1], "rotation": [0, 0.3826834, 0, 0.9238795]}},
        }
        point = {
            "anchor": "guitarist.head",
            "position": [0.1, 0.2, -0.4],
            "rotation": [0.1, 0.2, 0.0, 0.9746794],
            "horizontalFov": 40,
        }
        cameras = {
            "version": 1,
            "shots": {"face": {"type": "closeup", "control": "TwoCam", "points": [point]}},
            "groups": {"wide": ["face"]},
        }
        markers = [{**m, "object": "band"} for m in sidecars.open_placements(placements)[0]]
        shots, _ = sidecars.open_cameras(cameras, placements, anchors)
        opened = shots[0]["points"][0]
        # The Blender camera is at the head bone plus the offset in the bone frame.
        origin, axes, exact = sidecars.anchor_frame("guitarist.head", sidecars.band_frames(markers), anchors)
        self.assertTrue(exact)
        world, _ = sidecars.runtime_frame(opened["matrix"], looks=True)
        np.testing.assert_allclose(world, origin + axes @ point["position"], atol=1e-9)
        row = {
            **shots[0],
            "object": "face",
            "interpolation": None,
            "points": [
                {
                    "object": "face",
                    "matrix": opened["matrix"],
                    "fov": 40,
                    "anchor": "guitarist.head",
                    "lookAt": None,
                    "useRotation": True,
                    "screenOffset": None,
                }
            ],
        }
        issues = sidecars.Issues()
        built = sidecars.build_cameras([row], [], markers, issues, anchors=anchors)
        close(self, point, built["shots"]["face"]["points"][0])
        self.assertEqual(issues.items, [])
        # Without the rig: the performer root and a warning.
        issues = sidecars.Issues()
        sidecars.build_cameras([row], [], markers, issues)
        self.assertEqual([s for s, _, _, _ in issues.items], ["WARNING"])

    def test_reanchor_keeps_the_offset(self):
        data, _ = two_bone_rig()
        anchors = {"guitarist": rig.anchor_frames(data)}
        band = sidecars.band_frames(
            [{"kind": "BAND", "role": "guitarist", "matrix": sidecars.blender_matrix([1, 0, 2], [0, 0, 0, 1])}]
        )
        matrix = sidecars.blender_matrix([3, 1.7, 0], [0, 0.2, 0, 0.98], looks=True)
        point = {"anchor": "guitarist.head", "matrix": matrix}
        position, rotation = sidecars.point_offset(point, band, {})
        moved = sidecars.offset_matrix("guitarist.head", position, rotation, band, anchors)
        again = sidecars.point_offset({"anchor": "guitarist.head", "matrix": moved}, band, anchors)
        np.testing.assert_allclose(again[0], position, atol=1e-9)
        np.testing.assert_allclose(again[1], rotation, atol=1e-9)


class ActorTests(unittest.TestCase):
    def actor_files(self):
        files = sorted(UNPACKED.glob("*.chstage/actors.json"))
        if not files:
            self.skipTest("assets/packages is not generated")
        return files

    def test_actors_json_round_trip(self):
        for path in self.actor_files():
            source = json.loads(path.read_text())
            opened = sidecars.open_actors(source)
            issues = sidecars.Issues()
            built = sidecars.build_actors([{**a, "object": a["name"]} for a in opened], issues)
            with self.subTest(path.parent.name):
                if not source["actors"]:
                    self.assertIsNone(built)
                    continue
                self.assertEqual(issues.items, [])
                for name, row in source["actors"].items():
                    expected = {k: v for k, v in row.items() if v not in ("",)}
                    close(self, expected, built["actors"][name])

    def test_edited_actor_row(self):
        opened = sidecars.open_actors(
            {
                "version": 1,
                "actors": {
                    "sign": {
                        "model": "sign.glb",
                        "transform": {"position": [0, 3, 6], "rotation": [0, 0, 0, 1]},
                        "clip": "turn",
                    }
                },
            }
        )
        actor = {**opened[0], "object": "sign", "role": "sign/1", "playback": "hold", "created": False}
        matrix = np.asarray(actor["matrix"])
        matrix[:3, :3] *= 2
        actor["matrix"] = matrix
        built = sidecars.build_actors([actor], sidecars.Issues())["actors"]["sign"]
        self.assertEqual(
            (built["role"], built["playback"], built["created"], built["scale"]),
            ("sign/1", "hold", False, [2.0, 2.0, 2.0]),
        )
        self.assertEqual(built["transform"]["position"], [0.0, 3.0, 6.0])

    def test_runtime_scale_axes(self):
        matrix = sidecars.scaled_matrix([0, 0, 0], [0, 0, 0, 1], [1, 2, 3])
        np.testing.assert_allclose(sidecars.runtime_scale(matrix), [1, 2, 3])
        # Blender Z is runtime Y.
        np.testing.assert_allclose(np.linalg.norm(np.asarray(matrix)[:3, :3], axis=0), [1, 3, 2])

    def test_duplicate_names_are_errors(self):
        actor = {"object": "a", "name": "x", "model": "x.glb", "matrix": np.eye(4).tolist()}
        issues = sidecars.Issues()
        sidecars.build_actors([actor, {**actor, "object": "b"}], issues)
        self.assertEqual([(s, o) for s, _, o, _ in issues.items], [("ERROR", "b")])


class ActorGlbTests(unittest.TestCase):
    def test_keep_animations(self):
        data, _ = two_bone_rig()
        self.assertEqual(glb_tools.animation_names(glb_tools.keep_animations(data, ["other"])), ["other"])
        self.assertIs(glb_tools.keep_animations(data, ["idle", "other"]), data)
        gltf, _ = glb_tools.read_glb(glb_tools.keep_animations(data, []))
        self.assertNotIn("animations", gltf)

    def test_unit_scale_channels_are_dropped(self):
        data, _ = two_bone_rig()
        dropped, count, problems = glb_tools.drop_unit_scale_channels(data)
        self.assertEqual((count, problems), (1, []))
        gltf, _ = glb_tools.read_glb(dropped)
        self.assertEqual([c["target"]["path"] for c in gltf["animations"][0]["channels"]], ["rotation"])
        self.assertEqual(len(gltf["animations"][0]["samplers"]), 1)
        builder = GlbBuilder()
        node = builder.node("bone-1")
        builder.animation("grow", [(node, "scale", [0, 1], [[1, 1, 1], [2, 1, 1]])])
        _, count, problems = glb_tools.drop_unit_scale_channels(builder.bytes())
        self.assertEqual(count, 0)
        self.assertEqual(len(problems), 1)

    def test_merge_animations_by_joint_name(self):
        data, head = two_bone_rig()
        without = glb_tools.keep_animations(data, ["idle"])
        gltf, _ = glb_tools.read_glb(without)
        # The exported model has other node indices: put a new node first.
        gltf["nodes"].insert(0, {"name": "Armature", "children": [1]})
        gltf["nodes"][1].pop("children", None)
        gltf["nodes"][1]["children"] = [2]
        gltf["scenes"][0]["nodes"] = [0]
        for animation in gltf["animations"]:
            for channel in animation["channels"]:
                channel["target"]["node"] += 1
        _, binary = glb_tools.read_glb(without)
        merged, problems = glb_tools.merge_animations(glb_tools.write_glb(gltf, binary), data, {"other"})
        self.assertEqual(problems, [])
        result, result_binary = glb_tools.read_glb(merged)
        self.assertEqual(glb_tools.animation_names(merged), ["idle", "other"])
        channel = result["animations"][1]["channels"][0]
        self.assertEqual(result["nodes"][channel["target"]["node"]]["name"], "bone-00000001")
        values = rig.accessor(result, result_binary, result["animations"][1]["samplers"][0]["output"])
        np.testing.assert_allclose(values, [[0, 0, 0], [1, 0, 0]])

    def test_add_images_and_references(self):
        data, _ = two_bone_rig()
        gltf, binary = glb_tools.read_glb(data)
        gltf["materials"] = [
            {"name": "m", "extras": {"gh3Shader": {"textures": ["#a", "", "#b"]}, "gh3MaskTexture": "#c"}}
        ]
        data = glb_tools.write_glb(gltf, binary)
        self.assertEqual(glb_tools.texture_reference_names(gltf), {"a", "b", "c"})
        png = glb_tools.PNG_SIGNATURE + glb_tools.png_chunk(b"IEND", b"")
        added = glb_tools.add_images(data, {"a": png})
        self.assertEqual(glb_tools.image_bytes_by_name(added), {"a": png})

    def test_package_actor_glbs(self):
        files = sorted(UNPACKED.glob("*.chstage/*.glb"))
        files = [f for f in files if f.name != "stage.glb"]
        if not files:
            self.skipTest("assets/packages is not generated")
        for path in files:
            data = path.read_bytes()
            self.assertEqual(glb_tools.drop_unit_scale_channels(data), (data, 0, []), path)
            names = glb_tools.animation_names(data)
            self.assertEqual(glb_tools.animation_names(glb_tools.keep_animations(data, names[:1])), names[:1])


class ScriptBankTests(unittest.TestCase):
    def bank(self, venue):
        path = UNPACKED / f"{venue}.chstage" / "scripts.json"
        if not path.is_file():
            self.skipTest("assets/packages is not generated")
        return json.loads(path.read_text())

    def test_index_of_a_venue(self):
        bank = self.bank("gh3.dive")
        index = script_bank.ScriptIndex(bank, names=["scenery-0000"])
        arrays = sum(len(bank["definitions"][f"{sidecars.qb_key(name):08x}"][1]) for name in bank["nodes"])
        self.assertEqual(len(index.nodes), arrays)
        events = index.events([("object startup", "0x57192f76", "gh3ObjectScript", "0x774b50b0")])
        kinds = [e[0] for e in events]
        self.assertEqual(kinds[0], "startup")
        self.assertIn("performance changed", kinds)
        self.assertIn("trigger script", kinds)
        self.assertTrue(events[0][3].startswith("script, "))
        self.assertEqual(index.label("0xa1dc81f9"), "name (0xa1dc81f9)")
        self.assertEqual(script_bank.problems(bank, {"stage.glb"}), [])

    def test_simple_values_and_edits(self):
        bank = self.bank("gh3.dive")
        index = script_bank.ScriptIndex(bank)
        node = index.nodes[0]
        rows = index.simple_values(node)
        position = next(r for r in rows if r[1].startswith("pos "))
        name = next(r for r in rows if r[1].startswith("name "))
        self.assertFalse(name[4])
        script_bank.set_value(bank, json.loads(position[0]), "1, 2, 3")
        self.assertEqual(
            script_bank.ScriptIndex(bank).nodes[0]["fields"][json.loads(position[0])[3]], [6, [1.0, 2.0, 3.0]]
        )
        with self.assertRaises(ValueError):
            script_bank.set_value(bank, json.loads(name[0]), "other")
        with self.assertRaises(ValueError):
            script_bank.set_value(bank, json.loads(position[0]), "1, 2")
        script_kind = next(k for k, v in bank["definitions"].items() if v[0] == 7)
        with self.assertRaises(ValueError):
            script_bank.set_value(bank, ["definition", script_kind], "x")
        self.assertEqual(script_bank.parse_value(13, "triggerscript"), "0x2ca8a299")
        self.assertEqual(script_bank.parse_value(1, "0x10"), 16)
        with self.assertRaises(ValueError):
            script_bank.parse_value(2, "nan")

    def test_top_level_settings(self):
        bank = {"version": 1, "definitions": {"248b0a48": [7, "AAAA"]}, "startup": ["z_dive_Setup"]}
        script_bank.set_top_level(bank, "startup", "z_dive_Setup, other")
        self.assertEqual(bank["startup"], ["z_dive_Setup", "other"])
        self.assertEqual(script_bank.problems(bank), ["startup script 'other' is missing"])
        script_bank.set_top_level(bank, "performanceChanged", "")
        self.assertNotIn("performanceChanged", bank)
        with self.assertRaises(ValueError):
            script_bank.set_top_level(bank, "definitions", {})


class LightingPreviewTests(unittest.TestCase):
    def test_preview_light(self):
        self.assertEqual(sidecars.preview_light("Ambient", 0.65, 0, 200), (0.0, 0.65))
        self.assertEqual(sidecars.preview_light("Point", 0.75, 80, 200), (0.0, 0.75))
        self.assertEqual(sidecars.preview_light("Point", -1, 0, 4), (0.0, 0.0))
        self.assertEqual(sidecars.preview_light("Dir", 1.5, 0, 7), (4.5, 0.0))
        power, _ = sidecars.preview_light("Point", 0.5, 0, 4)
        # Inverse square law: the irradiance at 2 m (the middle of 0-4 m) is the GH3 intensity.
        self.assertAlmostEqual(power / (4 * math.pi**2 * 2**2), 0.5)


if __name__ == "__main__":
    unittest.main()
