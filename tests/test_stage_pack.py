from copy import deepcopy
import importlib.util
from pathlib import Path
import struct
import tempfile
import unittest

import numpy as np

from stage_gltf import GlbReader, GlbWriter, VERTEX
from stage_pack import compile_stage, load_json, write_json
from gh3_venue_scripts import literal_positions, script_bytes
from gh3_formats import key, node_prefixes
from gh3_direction import camera_motion, camera_point
from gh3_band_formats import remap_animation
from stage_pack import compile_cameras


SPEC = importlib.util.spec_from_file_location("template", Path(__file__).resolve().parents[1]/"create-stage-template.py")
TEMPLATE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(TEMPLATE)


class StagePackTests(unittest.TestCase):
    def test_custom_pack_uses_public_assets_and_same_compiler(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); source = root/"author"; output = root/"compiled"
            TEMPLATE.create(source)
            result = compile_stage(source, output)
            self.assertEqual(result["id"], "example.studio")
            pack = load_json(output/"pack.json")
            self.assertEqual(set(pack["band"]), {"guitarist","bassist","singer","drummer"})
            self.assertEqual(pack["effects"]["sparks"][0]["position"], [-6,0,0])
            self.assertEqual(load_json(output/"actors.json")["actors"][0]["clip"],0)
            self.assertEqual(len(load_json(output/"fixtures/fixtures.json")["housings"]),1)
            before = (output/"pack.json").read_bytes()
            manifest = load_json(source/"stage-pack.json"); manifest["model"] = "../outside.glb"
            write_json(source/"stage-pack.json",manifest)
            with self.assertRaises(ValueError): compile_stage(source,output)
            self.assertEqual((output/"pack.json").read_bytes(),before)

    def test_gltf_transform_normals_uv_winding_round_trip(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary)/"model.glb"
            vertices = np.zeros(3,dtype=VERTEX)
            vertices["position"] = [[0,0,0],[1,0,0],[0,1,0]]
            vertices["normal"] = [0,0,1]; vertices["color"] = 255
            vertices["uv"] = [[0,0],[1,0],[0,1]]
            writer = GlbWriter()
            mesh = writer.mesh("triangle",[("surface",3,vertices.tobytes(),[(0,1,2)])],{"surface":{"blend":0}}, {})
            world = np.diag([-2,3,4,1]); world[:3,3] = [1,2,3]
            writer.node("triangle",mesh,world); writer.save(path)
            reader = GlbReader(path); _,node,matrix = next(reader.nodes())
            packed,faces = reader.primitive(reader.data["meshes"][node["mesh"]]["primitives"][0],matrix)
            np.testing.assert_allclose(packed["position"],[[1,2,3],[-1,2,3],[1,5,3]])
            np.testing.assert_allclose(packed["normal"],[[0,0,1]]*3)
            np.testing.assert_allclose(packed["uv"],vertices["uv"])
            np.testing.assert_array_equal(faces,[[0,2,1]])

    def test_lightmap_shading_and_second_uv_survive_import(self):
        from PIL import Image
        from stage_gltf import STATIC_VERTEX
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); source = root/"source"; output = root/"cache"
            TEMPLATE.create(source)
            glb = GlbReader(source/"stage.glb")
            vertices = np.zeros(3, dtype=STATIC_VERTEX)
            vertices["position"] = [[0, 0, 0], [1, 0, 0], [0, 1, 0]]
            vertices["normal"] = [0, 0, 1]; vertices["color"] = 255
            vertices["lightmap"] = [[.25, .75], [.5, .75], [.25, .5]]
            images = {"diffuse": Image.new("RGBA", (4, 4), (200, 100, 50, 255)), "baked": Image.new("RGBA", (4, 4), (40, 80, 120, 255))}
            materials = {"lit": {"blend": 0, "texture": "diffuse", "lightmap": "baked", "shading": "lit", "color": [.5, .5, .5, 1]}}
            writer = GlbWriter(); writer.data = glb.data; writer.binary = bytearray(glb.binary)
            writer.node("lit-surface", writer.mesh("lit-surface", [("lit", 3, vertices.tobytes(), [(0, 1, 2)])], materials, images))
            writer.save(source/"stage.glb")
            row = next(m for m in GlbReader(source/"stage.glb").data["materials"] if m["name"] == "lit")
            self.assertEqual(row["occlusionTexture"]["texCoord"], 1)
            self.assertEqual(row["extras"]["gh3Shading"], "lit")
            self.assertEqual(row["pbrMetallicRoughness"]["baseColorFactor"], [.5, .5, .5, 1])
            compile_stage(source, output)
            definition = load_json(output/"stage.json")
            lit = [m for m in definition["materials"].values() if m["shading"] == "lit"]
            self.assertEqual(len(lit), 1)
            self.assertTrue((output/"textures"/(lit[0]["lightmap"]+".png")).exists())
            data = (output/"meshes.bin").read_bytes()
            self.assertEqual(data[:8], b"GH3STG02")
            offset = 12; found = []
            for _ in range(struct.unpack_from("<I", data, 8)[0]):
                material, count, indices = struct.unpack_from("<III", data, offset); offset += 12
                rows = np.frombuffer(data, STATIC_VERTEX, count, offset); offset += count*44 + indices*4
                if f"{material:08x}" in definition["materials"] and definition["materials"][f"{material:08x}"]["shading"] == "lit": found = rows
            np.testing.assert_allclose(sorted(map(tuple, found["lightmap"])), sorted(map(tuple, vertices["lightmap"])), atol=1e-6)
            self.assertTrue(np.all(found["color"][:, :3] == 128))

    def test_material_templates_classify_original_pixel_shaders(self):
        import gh3_materials
        if not gh3_materials.LIBRARY.exists(): self.skipTest("Original GH3 material library is not installed")
        named = {value["name"]: value for value in gh3_materials.templates().values()}
        self.assertEqual((named["D_3ChkBox_Pass0"]["shading"], named["D_3ChkBox_Pass0"]["diffuseConstant"]), ("lit", 0))
        self.assertEqual((named["D_N_S_4ChkBox"]["shading"], named["D_N_S_4ChkBox"]["diffuseConstant"]), ("lit", 1))
        self.assertEqual(named["D_1VertexColorOnly"]["shading"], "unlit")
        self.assertEqual(named["D_1VertexColorAmbientLit"]["shading"], "ambient")
        self.assertEqual(gh3_materials.lightmap_slot(named["D_N_S_4ChkBox"], 4), 3)
        self.assertEqual(gh3_materials.lightmap_slot(named["Env_D_N_S_Decal_3ChkBox"], 5), 3)
        self.assertIsNone(gh3_materials.lightmap_slot(named["D_1VertexColorOnly"], 1))

    def test_scenery_visibility_survives_import_and_invalid_edits_preserve_cache(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); source = root/"source"; output = root/"cache"
            TEMPLATE.create(source)
            glb = GlbReader(source/"stage.glb")
            scenery = [node for node in glb.data["nodes"] if "mesh" in node and not node.get("extras", {}).get("gh3FixtureId")]
            scenery[0].setdefault("extras", {}).update(gh3VisibilityGroup="crowd-lighters", gh3InitiallyVisible=False)
            def save():
                writer = GlbWriter(); writer.data = glb.data; writer.binary = bytearray(glb.binary); writer.save(source/"stage.glb")
            save(); compile_stage(source, output)
            definition = load_json(output/"stage.json")
            mesh_count = struct.unpack_from("<I", (output/"meshes.bin").read_bytes(), 8)[0]
            self.assertEqual(len(definition["visibilityGroups"]), mesh_count)
            self.assertIn("crowd-lighters", definition["visibilityGroups"])
            self.assertEqual(definition["hiddenGroups"], ["crowd-lighters"])
            before = (output/"pack.json").read_bytes()
            for changes in ({"gh3InitiallyVisible": "false"}, {"gh3VisibilityGroup": ""}, {"gh3VisibilityGroup": 3}):
                scenery[0]["extras"].update(gh3VisibilityGroup="crowd-lighters", gh3InitiallyVisible=False)
                scenery[0]["extras"].update(changes)
                save()
                with self.assertRaises(ValueError): compile_stage(source, output)
                self.assertEqual((output/"pack.json").read_bytes(), before)

    def test_stage_crowd_cast_preserves_model_profile_and_orientation(self):
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary)/"source"; output = Path(temporary)/"cache"
            TEMPLATE.create(source)
            manifest = load_json(source/"stage-pack.json")
            member = {"role": "crowd3", "model": "crowd_prison_01", "yaw": -90}
            manifest["crowd"] = [member]
            write_json(source/"stage-pack.json", manifest); compile_stage(source, output)
            self.assertEqual(load_json(output/"pack.json")["crowd"], [member])
            for invalid in ([member, member], [{**member, "model": "../crowd"}], [{**member, "role": "guitarist"}], [{**member, "yaw": 4294967206}]):
                manifest["crowd"] = invalid
                write_json(source/"stage-pack.json", manifest)
                with self.assertRaises(ValueError): compile_stage(source, output)
                self.assertEqual(load_json(output/"pack.json")["crowd"], [member])

    def test_scenery_object_pivots_and_script_data_survive_import(self):
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary)/"source"; output = Path(temporary)/"cache"
            TEMPLATE.create(source)
            glb = GlbReader(source/"stage.glb")
            scenery = [node for node in glb.data["nodes"] if "mesh" in node and not node.get("extras", {}).get("gh3FixtureId")]
            for node in scenery[:2]: node.setdefault("extras", {}).update(gh3Object="moving-sign", gh3ObjectPivot=[1, 2, 3])
            def save():
                writer = GlbWriter(); writer.data = glb.data; writer.binary = bytearray(glb.binary); writer.save(source/"stage.glb")
            save()
            bank = {"version": 1, "definitions": {f"{key('answer'):08x}": [1, 42]}, "startup": []}
            write_json(source/"scripts.json", bank)
            manifest = load_json(source/"stage-pack.json"); manifest["scripts"] = "scripts.json"
            write_json(source/"stage-pack.json", manifest); compile_stage(source, output)
            definition = load_json(output/"stage.json")
            self.assertEqual(definition["objects"], {f"{key('moving-sign'):08x}": {"position": [1, 2, 3], "rotation": [0, 0, 0, 1], "created": True, "script": ""}})
            self.assertEqual(len(definition["objectIds"]), len(definition["lightGroups"]))
            self.assertEqual(load_json(output/"scripts.json"), bank)
            before = (output/"stage.json").read_bytes()
            scenery[1]["extras"]["gh3ObjectPivot"] = [4, 5, 6]; save()
            with self.assertRaisesRegex(ValueError, "one pivot"): compile_stage(source, output)
            self.assertEqual((output/"stage.json").read_bytes(), before)

    def test_actor_armature_container_is_baked_into_root_motion(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); source = root/"source"; output = root/"cache"
            TEMPLATE.create(source)
            glb = GlbReader(source/"sign.glb")
            children = glb.data["scenes"][0]["nodes"]
            glb.data["scenes"][0]["nodes"] = [len(glb.data["nodes"])]
            glb.data["nodes"].append({"name":"Armature","children":children,"translation":[3,0,2]})
            writer = GlbWriter(); writer.data = glb.data; writer.binary = bytearray(glb.binary); writer.save(source/"sign.glb")
            compile_stage(source,output)
            actor = load_json(output/"actors.json")["actors"][0]
            animation = (output/"actors"/actor["id"]/"animation.bin").read_bytes()
            np.testing.assert_allclose(struct.unpack_from("<3f",animation,20),[3,0,-2])
            np.testing.assert_allclose(struct.unpack_from("<3f",animation,56),[3,0,-2])

    def test_actor_actions_and_visibility_keep_clip_references(self):
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary)/"source"; output = Path(temporary)/"cache"
            TEMPLATE.create(source)
            manifest = load_json(source/"stage-pack.json")
            actor = next(iter(manifest["actors"].values()))
            clip = GlbReader(source/actor["model"]).data["animations"][0]["name"]
            actor.update(role="stage-diver/1", initiallyVisible=False, actions={"jump": [clip, clip]})
            write_json(source/"stage-pack.json", manifest); compile_stage(source, output)
            result = load_json(output/"actors.json")["actors"][0]
            self.assertEqual(result["actions"], {"jump": [0, 0]})
            self.assertEqual(result["clips"], {clip: 0})
            self.assertFalse(result["initiallyVisible"])
            self.assertEqual(result["role"], "stage-diver/1")
            before = (output/"pack.json").read_bytes()
            actor["actions"]["jump"] = ["missing"]
            write_json(source/"stage-pack.json", manifest)
            with self.assertRaises(ValueError): compile_stage(source, output)
            self.assertEqual((output/"pack.json").read_bytes(), before)

    def test_scripted_actor_bindings_and_meshless_pivots_survive_compilation(self):
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary)/"source"; output = Path(temporary)/"cache"
            TEMPLATE.create(source)
            manifest = load_json(source/"stage-pack.json")
            actor = next(iter(manifest["actors"].values()))
            actor.update(object="dancer", script="dancer_start", created=False, animationEvents="dancer_events", scale=[2, 3, 4])
            write_json(source/"stage-pack.json", manifest)
            glb = GlbReader(source/"stage.glb")
            glb.data["scenes"][0]["nodes"].append(len(glb.data["nodes"]))
            glb.data["nodes"].append({"name": "pivot", "extras": {"gh3Object": "pivot", "gh3ObjectPivot": [2, 4, 6]}})
            writer = GlbWriter(); writer.data = glb.data; writer.binary = bytearray(glb.binary); writer.save(source/"stage.glb")
            compile_stage(source, output)
            result = load_json(output/"actors.json")["actors"][0]
            for field in ("object", "script", "created", "animationEvents", "scale"): self.assertEqual(result[field], actor[field])
            self.assertEqual(load_json(output/"stage.json")["objects"][f"{key('pivot'):08x}"]["position"], [2, 4, 6])
            before = (output/"actors.json").read_bytes()
            actor["scale"] = [1, -1, 1]; write_json(source/"stage-pack.json", manifest)
            with self.assertRaises(ValueError): compile_stage(source, output)
            self.assertEqual((output/"actors.json").read_bytes(), before)

    def test_script_particle_resources_are_public_and_transactional(self):
        from PIL import Image
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary)/"source"; output = Path(temporary)/"cache"
            TEMPLATE.create(source)
            (source/"particles").mkdir()
            Image.new("RGBA", (4, 4), (255, 80, 20, 255)).save(source/"particles/fire.png")
            bank = {"version": 1, "definitions": {f"{key('nodes'):08x}": [12, []]}, "nodes": ["nodes"], "textures": {"fire": "particles/fire.png"}}
            write_json(source/"scripts.json", bank)
            manifest = load_json(source/"stage-pack.json"); manifest["scripts"] = "scripts.json"; write_json(source/"stage-pack.json", manifest)
            compile_stage(source, output)
            self.assertEqual((output/"particles/fire.png").read_bytes(), (source/"particles/fire.png").read_bytes())
            self.assertEqual(load_json(output/"scripts.json"), bank)
            before = (output/"pack.json").read_bytes()
            bank["textures"]["fire"] = "../outside.png"; write_json(source/"scripts.json", bank)
            with self.assertRaises(ValueError): compile_stage(source, output)
            self.assertEqual((output/"pack.json").read_bytes(), before)

    def test_animation_remapping_matches_names_and_keeps_unanimated_bones(self):
        rotation = [(0, (0, 0, 0, 1)), (1, (0, 0, 1, 0))]
        translation = [(0, (1, 2, 3))]
        clip = (1, {0: rotation, 2: rotation}, {1: translation}, 7)
        result = remap_animation(clip, [11, 22, 33], [22, 44, 11])
        self.assertEqual(result, (1, {2: rotation}, {0: translation}, 7))
        with self.assertRaises(ValueError): remap_animation(clip, [11, 11, 33], [11])

    def test_source_node_prefix_indices_retain_source_order(self):
        nodes = [{key("Name"): key(name)} for name in ["fan02", "fan04", "fan01", "fan03"]]
        data = struct.pack(">2I3I4H", key("nodes"), 1, key("fan"), 0, 4, 1, 3, 0, 2)
        self.assertEqual(node_prefixes(data, {key("nodes"): nodes}), {key("fan"): [key(name) for name in ["fan04", "fan03", "fan02", "fan01"]]})
        with self.assertRaises(ValueError): node_prefixes(data[:-1], {key("nodes"): nodes})
        with self.assertRaises(ValueError): node_prefixes(data, {key("nodes"): nodes[:3]})
        with self.assertRaises(ValueError): node_prefixes(data, {})

    def test_manifest_camera_and_fixture_failures_preserve_cache(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); source = root/"source"; output = root/"cache"
            TEMPLATE.create(source); compile_stage(source,output)
            manifest = load_json(source/"stage-pack.json")
            for field,value in [("unexpected",True),("id","../bad"),("band",{}),("effects",{"unknown":[]})]:
                invalid = deepcopy(manifest); invalid[field] = value
                write_json(source/"stage-pack.json",invalid)
                with self.assertRaises(ValueError): compile_stage(source,output)
            write_json(source/"stage-pack.json",manifest)
            camera = load_json(source/"cameras.json")
            camera["shots"]["wide"]["waypoints"] = ["missing"]
            write_json(source/"cameras.json",camera)
            with self.assertRaisesRegex(ValueError,"waypoint"): compile_stage(source,output)

    def test_pyro_positions_are_little_endian_script_literals(self):
        marker = b"\x16"+struct.pack("<I",key("Pos"))+b"\x07\x1e"
        position = struct.pack("<3f",-8.5,1,3)
        self.assertEqual(literal_positions(b"\0"+marker+position+b"\x01"+marker+position),[(-8.5,1,3)])

    def test_compressed_venue_script_and_truncated_reference(self):
        payload = b"\x07abc\xee\xf3"
        data = bytearray(60+len(payload))
        struct.pack_into(">5I",data,28,0x00200700,key("example"),0,48,0)
        struct.pack_into(">3I",data,48,0,9,len(payload))
        data[60:] = payload
        self.assertEqual(script_bytes(data,"example"),b"abcabcabc")
        struct.pack_into(">I",data,56,len(payload)-1)
        with self.assertRaises(ValueError): script_bytes(data,"example")

    def test_pyro_position_array_references(self):
        qb = bytearray(76)
        struct.pack_into(">5I",qb,28,0x00200C00,key("emitters"),0,48,0)
        struct.pack_into(">3I",qb,48,0x10600,1,60)
        struct.pack_into(">I3f",qb,60,0x10000,-4,1,7)
        reference = b"\x16"+struct.pack("<I",key("Pos"))+b"\x07\x0e\x4b\x16"+struct.pack("<I",key("emitters"))+b"\x05"
        self.assertEqual(literal_positions(reference+reference,[qb]),[(-4,1,7)])

    def test_camera_script_motion_compiles_to_public_keyframes(self):
        start = camera_point({key("FOV"):70})
        end = camera_point({key("FOV"):40})
        for control,points in [("QuickZoom",[start,end]),("HandCamZoom2",[start]),("NewIntro",[start])]:
            translated,points,motion = camera_motion(control,points,{})
            data = {"version":1,"shots":{"intro":{"type":"stage","control":translated,"points":[{
                "position": p["position"],"rotation":p["rotation"],"anchor":"world","horizontalFov":p["fieldOfView"]} for p in points],**motion}},
                "groups":{"wide":["intro"]}}
            result = compile_cameras(data,{}, {})["arrays"]["cameras_wide"][0]
            self.assertEqual(result["control"],"Keyframes")
            self.assertEqual(result["times"],motion["times"])
            data["shots"]["intro"]["times"][-1] = 0
            with self.assertRaisesRegex(ValueError,"increase"): compile_cameras(data,{}, {})


if __name__ == "__main__": unittest.main()
