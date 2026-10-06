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
from gh3_formats import key
from gh3_direction import camera_motion, camera_point
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
