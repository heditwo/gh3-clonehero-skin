import hashlib
import json
import struct
import tempfile
import unittest
import zipfile
import zlib
from pathlib import Path

import numpy as np

from pipeline.package import q48
from pipeline.package.schema import SCHEMA_DIR, Schemas
from pipeline.package.validate import Validator, validate


def png(width=1, height=1):
    def chunk(kind, data): return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))
    rows = b"".join(b"\0" + b"\xff\xff\xff\xff" * width for _ in range(height))
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)) + \
        chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b"")


class GlbBuilder:
    def __init__(self):
        self.json = {"asset": {"version": "2.0"}, "scene": 0, "scenes": [{"nodes": []}], "nodes": [], "meshes": [],
                     "materials": [], "images": [], "textures": [], "accessors": [], "bufferViews": []}
        self.binary = bytearray()

    def view(self, data):
        self.binary.extend(b"\0" * (-len(self.binary) % 4))
        self.json["bufferViews"].append({"buffer": 0, "byteOffset": len(self.binary), "byteLength": len(data)})
        self.binary.extend(data)
        return len(self.json["bufferViews"]) - 1

    def accessor(self, values, kind, component=5126):
        values = np.asarray(values, dtype={5126: "<f4", 5125: "<u4", 5121: "u1"}[component])
        self.json["accessors"].append({"bufferView": self.view(values.tobytes()), "componentType": component, "count": len(values), "type": kind})
        return len(self.json["accessors"]) - 1

    def image(self, name):
        self.json["images"].append({"bufferView": self.view(png()), "mimeType": "image/png", "name": name})
        self.json["textures"].append({"source": len(self.json["images"]) - 1})

    def triangle(self, material=None, skinned=False):
        attributes = {"POSITION": self.accessor([[0, 0, 0], [1, 0, 0], [0, 1, 0]], "VEC3"),
                      "NORMAL": self.accessor([[0, 0, 1]] * 3, "VEC3")}
        if skinned:
            attributes["JOINTS_0"] = self.accessor([[0, 1, 0, 0]] * 3, "VEC4", 5121)
            attributes["WEIGHTS_0"] = self.accessor([[0.5, 0.5, 0, 0]] * 3, "VEC4")
        primitive = {"attributes": attributes, "indices": self.accessor([0, 1, 2], "SCALAR", 5125)}
        if material is not None: primitive["material"] = material
        self.json["meshes"].append({"primitives": [primitive]})
        return len(self.json["meshes"]) - 1

    def node(self, row, root=True):
        self.json["nodes"].append(row)
        if root: self.json["scenes"][0]["nodes"].append(len(self.json["nodes"]) - 1)
        return len(self.json["nodes"]) - 1

    def bytes(self):
        self.binary.extend(b"\0" * (-len(self.binary) % 4))
        self.json["buffers"] = [{"byteLength": len(self.binary)}]
        text = json.dumps(self.json).encode()
        text += b" " * (-len(text) % 4)
        return struct.pack("<5I", 0x46546C67, 2, 28 + len(text) + len(self.binary), len(text), 0x4E4F534A) + text + \
            struct.pack("<2I", len(self.binary), 0x004E4942) + bytes(self.binary)


def stage_glb(texture="#lightmap", fixture="beam-1"):
    glb = GlbBuilder()
    glb.image("lightmap")
    glb.json["materials"].append({"name": "floor", "extras": {"gh3Shading": "lit", "gh3Shader": {
        "template": "0123abcd", "vertexConstants": [], "pixelConstants": [[1, 1, 1, 1]], "textures": [texture], "addressing": [0],
        "flags": 0, "blend": 0, "alphaRef": 0, "drawOrder": 0}}})
    glb.node({"name": "floor", "mesh": glb.triangle(0), "extras": {"gh3LightGroup": "Stage", "blenderProperty": 3}})
    if fixture: glb.node({"name": "housing", "mesh": glb.triangle(0), "extras": {"gh3FixtureId": fixture}})
    return glb.bytes()


def skinned_glb(offset=0.0, ibm_offset=0.0, scale_key=1.0, material=None):
    glb = GlbBuilder()
    root = glb.node({"name": "bone-00000001", "translation": [0, 1, 0], "children": [1]})
    glb.node({"name": "bone-00000002", "translation": [0, 0.5 + offset, 0]}, root=False)
    ibm = [np.linalg.inv(m).T.reshape(-1) for m in (np.array([[1, 0, 0, 0], [0, 1, 0, 1], [0, 0, 1, 0], [0, 0, 0, 1.0]]),
                                                    np.array([[1, 0, 0, 0], [0, 1, 0, 1.5 + ibm_offset], [0, 0, 1, 0], [0, 0, 0, 1.0]]))]
    glb.json["skins"] = [{"joints": [0, 1], "inverseBindMatrices": glb.accessor(ibm, "MAT4")}]
    glb.json["materials"].append(material or {"name": "skin"})
    if material: glb.image("shade")
    glb.node({"name": "model", "mesh": glb.triangle(0, skinned=True), "skin": 0})
    times = glb.accessor([0, 1], "SCALAR")
    glb.json["animations"] = [{"name": "idle", "samplers": [
        {"input": times, "output": glb.accessor([[0, 0, 0, 1], [0, 0.0871557, 0, 0.9961947]], "VEC4")},
        {"input": times, "output": glb.accessor([[1, 1, 1], [scale_key] * 3], "VEC3"), "interpolation": "STEP"}],
        "channels": [{"sampler": 0, "target": {"node": 1, "path": "rotation"}}, {"sampler": 1, "target": {"node": 1, "path": "scale"}}]}]
    return glb.bytes()


def chain_glb(offset=0.0):
    """skinned_glb with two added chain bones under bone-00000002 (tail_01 and tail_02, 0.2 m apart along +Y)."""
    glb = GlbBuilder()
    glb.node({"name": "bone-00000001", "translation": [0, 1, 0], "children": [1]})
    glb.node({"name": "bone-00000002", "translation": [0, 0.5, 0], "children": [2]}, root=False)
    glb.node({"name": "tail_01", "translation": [0, 0.2 + offset, 0], "children": [3]}, root=False)
    glb.node({"name": "tail_02", "translation": [0, 0.2, 0]}, root=False)
    heights = [1, 1.5, 1.7 + offset, 1.9 + offset]
    ibm = [np.linalg.inv(np.array([[1, 0, 0, 0], [0, 1, 0, h], [0, 0, 1, 0], [0, 0, 0, 1.0]])).T.reshape(-1) for h in heights]
    glb.json["skins"] = [{"joints": [0, 1, 2, 3], "inverseBindMatrices": glb.accessor(ibm, "MAT4")}]
    glb.json["materials"].append({"name": "skin"})
    glb.node({"name": "model", "mesh": glb.triangle(0, skinned=True), "skin": 0})
    return glb.bytes()


def chain_json(**changes):
    joint = {"length": 0.2, "mass": 1, "friction": 0.05, "angularDamping": 0.5, "maxAngularVelocity": 20, "radius": 0.02,
             "limits": {"coneLimit": [-0.6, 0.6], "planesLimit": [-0.5, 0.5]}}
    value = {"bones": [{"name": "tail_01", "parent": "bone-00000002", "bind": [0, 0.2, 0, 0, 0, 0, 1]},
                       {"name": "tail_02", "parent": "tail_01", "bind": [0, 0.2, 0, 0, 0, 0, 1]}],
             "colliders": [{"name": "body", "bone": "bone-00000001", "radius": 0.1, "a": [0, 0, 0], "b": [0, 0.4, 0]}],
             "chains": [{"name": "tail", "colliders": ["body"], "joints": [{"bone": "tail_01", **joint}, {"bone": "tail_02", **joint}]}]}
    value.update(changes)
    return value


IDENTITY = [0, 0, 0, 1]


def stage_files(**changes):
    placements = {"version": 1, "band": {"guitarist": {"position": [0, 0, 0], "rotation": IDENTITY}},
                  "waypoints": {"guitarist_walk01": {"position": [1, 0, 0], "rotation": IDENTITY}}}
    cameras = {"version": 1, "groups": {"wide": ["wide-1"], "guitarist": ["close"]}, "shots": {
        "wide-1": {"type": "longshot", "control": "TwoCam", "points": [{"position": [0, 2, -5], "rotation": IDENTITY, "horizontalFov": 60, "anchor": "world"}]},
        "close": {"type": "closeup", "control": "Keyframes", "times": [0, 2], "waypoints": ["guitarist_walk01"], "points": [
            {"position": [0, 1, -1], "horizontalFov": 40, "anchor": "guitarist.root", "lookAt": "guitarist.head"},
            {"position": [0, 1, -2], "horizontalFov": 40, "anchor": "guitarist.root", "lookAt": "guitarist.bone.Bone_Palm_L"}]}}}
    light = {"color": [1, 1, 1], "intensity": 1, "near": 0, "far": 10}
    beam = {"position": [0, 3, 0], "rotation": IDENTITY, "color": [1, 1, 1], "density": 0.5, "startRadius": 0.1, "endRadius": 1, "range": 5}
    lighting = {"version": 1, "lights": [{"id": "key", "group": "Band", "type": "Spot", "position": [0, 3, 0], "rotation": IDENTITY, "hotspot": 20, "field": 40}],
                "snapshots": {"on": {"lights": [light], "beams": {"beam-1": beam}}, "off": {"lights": [{**light, "intensity": 0}], "beams": {"beam-1": beam}}},
                "moods": {"intro": ["on"], "blackout": ["off"]}, "colors": {"red": [1, 0, 0]}}
    actors = {"version": 1, "actors": {"sign": {"model": "sign.glb", "transform": {"position": [0, 4, 7], "rotation": IDENTITY}, "clip": "idle"}}}
    files = {"stage.glb": stage_glb(), "sign.glb": skinned_glb(), "placements.json": placements, "cameras.json": cameras,
             "lighting.json": lighting, "actors.json": actors, "preview.png": png(16, 9)}
    files.update(changes)
    return {name: json.dumps(value).encode() if isinstance(value, dict) else value for name, value in files.items() if value is not None}


def manifest(contents, kind="chstage", entry=None, **changes):
    value = {"format": f"{kind}/1.0", "id": "example.test", "version": "1.0.0", "name": "Test", "authors": [{"name": "Tester"}],
             "license": "CC-BY-4.0", "permissions": {"redistribute": True, "modify": True, "commercial": False},
             "requires": [{"id": "gh3-band-v1", "version": "1.0.0"}], "preview": "preview.png",
             "entry": entry or {"model": "stage.glb", "placements": "placements.json", "cameras": "cameras.json",
                                "lighting": "lighting.json", "actors": "actors.json"},
             "files": {name: {"sha256": hashlib.sha256(data).hexdigest(), "size": len(data)} for name, data in sorted(contents.items())}}
    value.update(changes)
    return json.dumps({key: item for key, item in value.items() if item is not None}).encode()


def write_zip(path, files, compress=None):
    with zipfile.ZipFile(path, "w") as archive:
        for name, data in files.items():
            method = compress or (zipfile.ZIP_DEFLATED if name.endswith(".json") else zipfile.ZIP_STORED)
            archive.writestr(zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0)), data, compress_type=method)
    return path


def package(folder, files=None, name="test.chstage", **manifest_changes):
    files = stage_files() if files is None else files
    return write_zip(Path(folder) / name, {"manifest.json": manifest(files, **manifest_changes), **files})


def errors_of(path, **options):
    return validate(path, **options).errors


class SchemaTests(unittest.TestCase):
    def test_schema_files_parse_and_use_supported_keywords(self):
        schemas = Schemas()
        for path in SCHEMA_DIR.glob("*.schema.json"):
            document = json.loads(path.read_text())
            self.assertEqual(document["$schema"], "https://json-schema.org/draft/2020-12/schema")
            self.assertTrue(document["$id"].endswith(path.name))
            schemas.validate({}, path.name)

    def test_agrees_with_reference_implementation(self):
        try:
            import jsonschema
            import referencing
        except ImportError:
            self.skipTest("jsonschema is not installed")
        resources = [(json.loads(p.read_text())["$id"], referencing.Resource.from_contents(json.loads(p.read_text()))) for p in SCHEMA_DIR.glob("*.schema.json")]
        registry = referencing.Registry().with_resources(resources)
        schemas = Schemas()
        files = stage_files()
        documents = [("placements.schema.json", json.loads(files["placements.json"])), ("cameras.schema.json", json.loads(files["cameras.json"])),
                     ("lighting.schema.json", json.loads(files["lighting.json"])), ("actors.schema.json", json.loads(files["actors.json"])),
                     ("manifest.schema.json", json.loads(manifest(files))), ("character.schema.json", {"version": 1, "library": "gh3-band-v1", "profile": "gh3-drummer-v1"}),
                     ("scripts.schema.json", {"version": 1, "definitions": {"0123abcd": [7, 1.5]}, "textures": {"a": "t/a.png"}})]
        broken = [(name, {**value, "unknown": 1}) for name, value in documents] + [
            ("cameras.schema.json", {**documents[1][1], "groups": {"guitarist": ["close"]}}),
            ("manifest.schema.json", {**documents[4][1], "format": "chstage/2.0"}),
            ("manifest.schema.json", {**documents[4][1], "entry": {"model": "stage.glb"}}),
            ("lighting.schema.json", {**documents[2][1], "moods": {"intro": ["on"]}})]
        for name, value in documents + broken:
            schema = json.loads((SCHEMA_DIR / name).read_text())
            jsonschema.Draft202012Validator.check_schema(schema)
            reference = jsonschema.Draft202012Validator(schema, registry=registry).is_valid(value)
            self.assertEqual(not schemas.validate(value, name), reference, name)
        self.assertTrue(all(not schemas.validate(value, name) for name, value in documents))


class StagePackageTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.folder = Path(self.temporary.name)

    def tearDown(self):
        self.temporary.cleanup()

    def test_valid_stage_package(self):
        report = validate(package(self.folder))
        self.assertEqual(report.errors, [])
        self.assertEqual(report.warnings, [])

    def test_hashes_and_file_list(self):
        files = stage_files()
        path = write_zip(self.folder / "a.chstage", {"manifest.json": manifest(files), **files, "stage.glb": stage_glb(texture="")})
        self.assertTrue(any("SHA-256" in e or "size differs" in e for e in errors_of(path)))
        path = write_zip(self.folder / "b.chstage", {"manifest.json": manifest(files), **files, "extra.txt": b"x"})
        self.assertTrue(any("not listed" in e for e in errors_of(path)))
        path = write_zip(self.folder / "c.chstage", {"manifest.json": manifest(files, files={}), **files})
        self.assertTrue(any("must list every file" in e for e in errors_of(path)))

    def test_portable_paths(self):
        for bad in ("Stage.GLB", "../x.png", "con.png", "a//b.png", ".hidden.png"):
            files = stage_files(**{"stage.glb": stage_glb()})
            files[bad] = png()
            self.assertTrue(errors_of(package(self.folder, files, name="p.chstage")), bad)

    def test_extension_and_format(self):
        self.assertTrue(any("does not match the file extension" in e for e in errors_of(package(self.folder, name="x.chchar"))))
        self.assertTrue(any("newer mod" in e for e in errors_of(package(self.folder, format="chstage/1.1"))))

    def test_compression_recommendation(self):
        files = stage_files()
        path = write_zip(self.folder / "d.chstage", {"manifest.json": manifest(files), **files}, compress=zipfile.ZIP_DEFLATED)
        report = validate(path)
        self.assertEqual(report.errors, [])
        self.assertTrue(any("use STORE" in w for w in report.warnings))

    def test_texture_references(self):
        self.assertTrue(any("not glTF indices" in e for e in errors_of(package(self.folder, stage_files(**{"stage.glb": stage_glb(texture=0)})))))
        self.assertTrue(any("no GLB image named" in e for e in errors_of(package(self.folder, stage_files(**{"stage.glb": stage_glb(texture="#other")})))))
        self.assertTrue(any("texture file is missing" in e for e in errors_of(package(self.folder, stage_files(**{"stage.glb": stage_glb(texture="textures/a.png")})))))
        files = stage_files(**{"stage.glb": stage_glb(texture="textures/a.png"), "textures/a.png": png()})
        self.assertEqual(errors_of(package(self.folder, files)), [])

    def test_reserved_extras(self):
        glb = GlbBuilder()
        glb.node({"name": "n", "mesh": glb.triangle(), "extras": {"gh3LightGrup": "Stage"}})
        self.assertTrue(any("gh3LightGrup" in e for e in errors_of(package(self.folder, stage_files(**{"stage.glb": glb.bytes()})))))

    def test_references(self):
        files = stage_files()
        cameras = json.loads(files["cameras.json"])
        cameras["groups"]["wide"] = ["close"]
        self.assertTrue(any("without waypoint restriction" in e for e in errors_of(package(self.folder, stage_files(**{"cameras.json": cameras})))))
        cameras["groups"]["wide"] = ["missing"]
        self.assertTrue(any("missing shot" in e for e in errors_of(package(self.folder, stage_files(**{"cameras.json": cameras})))))
        lighting = json.loads(files["lighting.json"])
        lighting["snapshots"]["off"]["lights"] = []
        self.assertTrue(any("must list every light" in e for e in errors_of(package(self.folder, stage_files(**{"lighting.json": lighting})))))
        self.assertTrue(any("has no lighting beam" in e for e in errors_of(package(self.folder, stage_files(**{"stage.glb": stage_glb(fixture="beam-9")})))))
        actors = json.loads(files["actors.json"])
        actors["actors"]["sign"]["clip"] = "spin"
        self.assertTrue(any("no clip 'spin'" in e for e in errors_of(package(self.folder, stage_files(**{"actors.json": actors})))))
        placements = json.loads(files["placements.json"])
        placements["band"]["guitarist"]["rotation"] = [0, 0, 0, 2]
        self.assertTrue(any("unit length" in e for e in errors_of(package(self.folder, stage_files(**{"placements.json": placements})))))

    def test_script_bank_lights(self):
        def key(name): return f"0x{~zlib.crc32(name.lower().encode()) & 0xFFFFFFFF:08x}"
        def node(name, kind): return [10, [[key("Name"), [13, key(name)]], [key("Class"), [13, key(kind)]]]]
        def warnings(nodes, light_object=None):
            scripts = {"version": 1, "definitions": {}}
            if nodes is not None:
                scripts = {"version": 1, "nodes": ["club_NodeArray"], "definitions": {key("club_NodeArray")[2:]: [12, nodes]}}
            files = stage_files(**{"scripts.json": scripts})
            if light_object:
                lighting = json.loads(files["lighting.json"])
                lighting["lights"][0]["object"] = light_object
                files["lighting.json"] = json.dumps(lighting).encode()
            entry = {"model": "stage.glb", "placements": "placements.json", "cameras": "cameras.json", "lighting": "lighting.json",
                     "actors": "actors.json", "scripts": "scripts.json"}
            report = validate(package(self.folder, files, entry=entry))
            self.assertEqual(report.errors, [])
            return [w for w in report.warnings if "GH3 light object" in w]
        self.assertEqual(warnings(None), [])
        self.assertEqual(warnings([node("club_box", "GameObject")]), [])
        self.assertEqual(len(warnings([node("club_light", "LevelLight")])), 1)
        self.assertEqual(warnings([node("club_light", "LevelLight")], "club_light"), [])
        self.assertEqual(warnings([node("club_light", "LevelLight")], key("club_light")), [])

    def test_actor_skin_and_unit_scale_channels(self):
        self.assertEqual(errors_of(package(self.folder, stage_files(**{"sign.glb": skinned_glb(scale_key=1.00005)}))), [])
        self.assertTrue(any("scale keys" in e for e in errors_of(package(self.folder, stage_files(**{"sign.glb": skinned_glb(scale_key=1.1)})))))
        self.assertTrue(any("inverse bind" in e for e in errors_of(package(self.folder, stage_files(**{"sign.glb": skinned_glb(ibm_offset=0.01)})))))

    def test_development_folder(self):
        folder = self.folder / "dev.chstage"
        files = stage_files()
        for name, data in {"manifest.json": manifest(files, files={}), **files}.items():
            (folder / name).parent.mkdir(parents=True, exist_ok=True)
            (folder / name).write_bytes(data)
        report = validate(folder)
        self.assertEqual(report.errors, [])
        self.assertTrue(any("development folder" in w for w in report.warnings))


class CharacterPackageTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.folder = Path(self.temporary.name)
        library = self.folder / "library"
        (library / "direction").mkdir(parents=True)
        (library / "band" / "drummer").mkdir(parents=True)
        (library / "direction" / "performance.json").write_text(json.dumps({"members": {"drummer": {"bones": ["00000001", "00000002"]}}}))
        # Unity-space bind pose: z negated against glTF; x and y of the rotation negated.
        rows = struct.pack("<i7f", -1, 0, 1, 0, 0, 0, 0, 1) + struct.pack("<i7f", 0, 0, 0.5, 0, 0, 0, 0, 1)
        (library / "band" / "drummer" / "animation.bin").write_bytes(b"GH3ANM01" + struct.pack("<II", 2, 0) + rows)
        self.library = library

    def tearDown(self):
        self.temporary.cleanup()

    def character(self, glb, profile="gh3-drummer-v1", hair=(), **fields):
        files = {"character.glb": glb, "character.json": json.dumps({"version": 1, "library": "gh3-band-v1", "profile": profile, "hairMaterials": list(hair),
                                                                     **fields}).encode()}
        return package(self.folder, files, name="c.chchar", kind="chchar", entry={"model": "character.glb", "character": "character.json"}, preview=None)

    def test_valid_character(self):
        report = validate(self.character(skinned_glb()), library=self.library)
        self.assertEqual(report.errors, [])
        self.assertTrue(any("animations are ignored" in w for w in report.warnings))

    def test_profile_tolerances(self):
        self.assertEqual(errors_of(self.character(skinned_glb(offset=0.00005)), library=self.library), [])
        self.assertTrue(any("joint positions differ" in e for e in errors_of(self.character(skinned_glb(offset=0.001, ibm_offset=0.001)), library=self.library)))
        self.assertTrue(any("has no profile" in e for e in errors_of(self.character(skinned_glb(), profile="gh3-keytarist-v1"), library=self.library)))
        self.assertTrue(any("hair material" in e for e in errors_of(self.character(skinned_glb(), hair=["hair"]), library=self.library)))

    def test_toon_material_and_hair_layer(self):
        toon = {"threshold": 0.45, "softness": 0.05, "shadeTexture": "#shade", "shadeColor": [0.8, 0.8, 0.9], "rimColor": [0.3, 0.3, 0.3], "rimWidth": 0.2,
                "sphereTexture": "#shade", "sphereMode": "multiply", "outline": {"width": 0.0015, "color": [0, 0, 0], "textured": True}}
        material = {"name": "skin", "extras": {"gh3Shading": "toon", "gh3Toon": toon, "gh3HairLayer": "overHair"}}
        report = validate(self.character(skinned_glb(material=material)), library=self.library)
        self.assertEqual(report.errors, [])
        for bad, text in (({"gh3Toon": {**toon, "outline": {"width": 0.5}}}, "width"), ({"gh3Toon": {**toon, "shadeTexture": "#missing"}}, "no GLB image named"),
                          ({"gh3HairLayer": "brow"}, "gh3HairLayer"), ({"gh3Toon": {**toon, "ramp": 1}}, "ramp")):
            broken = {"name": "skin", "extras": {**material["extras"], **bad}}
            self.assertTrue(any(text in e for e in errors_of(self.character(skinned_glb(material=broken)), library=self.library)), bad)
        lit = {"name": "skin", "extras": {"gh3Shading": "lit", "gh3Toon": toon}}
        self.assertTrue(any("without gh3Shading 'toon'" in w for w in validate(self.character(skinned_glb(material=lit)), library=self.library).warnings))

    def test_toon_is_only_for_characters(self):
        glb = GlbBuilder()
        glb.json["materials"].append({"name": "floor", "extras": {"gh3Shading": "toon"}})
        glb.node({"name": "floor", "mesh": glb.triangle(0)})
        self.assertTrue(any("only for characters" in e for e in errors_of(package(self.folder, stage_files(**{"stage.glb": glb.bytes()})))))


    def test_custom_chains(self):
        report = validate(self.character(chain_glb(), **chain_json()), library=self.library)
        self.assertEqual((report.errors, report.warnings), ([], []))
        self.assertTrue(any("not in the profile or in character.json bones" in e for e in errors_of(self.character(chain_glb()), library=self.library)))
        self.assertTrue(any("joint positions differ" in e for e in errors_of(self.character(chain_glb(offset=0.001), **chain_json()), library=self.library)))
        def errors(**changes): return "\n".join(errors_of(self.character(chain_glb(), **chain_json(**changes)), library=self.library))
        value = chain_json()
        bones = value["bones"]
        self.assertIn("has the name of a profile bone", errors(bones=[*bones, {"name": "bone-00000001", "parent": "tail_02", "bind": [0, 0, 0, 0, 0, 0, 1]}]))
        self.assertIn("is not a bone of the rig", errors(bones=[{**bones[0], "parent": "missing"}, bones[1]]))
        self.assertIn("is not a bone of the rig", errors(bones=[bones[1], bones[0]]))
        self.assertIn("unit length", errors(bones=[{**bones[0], "bind": [0, 0.2, 0, 0, 0, 0, 2]}, bones[1]]))
        chain = value["chains"][0]
        joints = chain["joints"]
        self.assertIn("must be a bone of 'bones'", errors(chains=[{**chain, "joints": [{**joints[0], "bone": "bone-00000002"}]}]))
        self.assertIn("joint of an earlier chain", errors(chains=[chain, {**chain, "name": "again"}]))
        self.assertIn("two chains have the name", errors(chains=[{**chain, "joints": [joints[0]]}, {**chain, "joints": [joints[1]]}]))
        self.assertIn("parent joint must come before it", errors(chains=[{**chain, "joints": [joints[1], joints[0]]}]))
        self.assertIn("no collider 'legs'", errors(chains=[{**chain, "colliders": ["legs"]}]))
        self.assertIn("cone limit", errors(chains=[{**chain, "joints": [{**joints[0], "limits": {"coneLimit": [0, 2.0]}}]}]))
        self.assertIn("planesLimit must be", errors(chains=[{**chain, "joints": [{**joints[0], "limits": {"coneLimit": [0, 1], "planesLimit": [0.5, -0.5]}}]}]))
        self.assertIn("come together", errors(chains=[{**chain, "joints": [{**joints[0], "twistAxis": [0, 1, 0]}]}]))
        self.assertIn("'direction' must have unit length", errors(chains=[{**chain, "joints": [{**joints[0], "direction": [0, 2, 0]}]}]))
        self.assertIn("is on the chain bone", errors(colliders=[{**value["colliders"][0], "bone": "tail_01"}]))
        self.assertIn("'axis' and 'range'", errors(chains=[{**chain, "joints": [{**joints[0], "type": "hinge", "axis": [1, 0, 0], "range": [-1, 1]}]}]))
        hinge = {key: item for key, item in joints[0].items() if key != "limits"}
        self.assertNotIn("tail_01", errors(chains=[{**chain, "joints": [{**hinge, "type": "hinge", "axis": [1, 0, 0], "range": [-1, 1]}]}]))
        report = validate(self.character(chain_glb(), **chain_json(chains=[{**chain, "joints": [{**joints[0], "length": 0.1}, joints[1]]}])), library=self.library)
        self.assertTrue(any("does not start at the end" in w for w in report.warnings), report.warnings)
        springs = [{**j, "stiffness": 40, "drag": 15, "gravity": 0.5} for j in joints]
        report = validate(self.character(chain_glb(), **chain_json(chains=[{**chain, "bendLimit": 1.0, "joints": springs}])), library=self.library)
        self.assertEqual(report.errors, [])
        self.assertIn("stiffness", errors(chains=[{**chain, "joints": [{**springs[0], "stiffness": 6000}, springs[1]]}]))
        self.assertIn("gravity", errors(chains=[{**chain, "joints": [{**springs[0], "gravity": -1}, springs[1]]}]))
        self.assertIn("bendLimit", errors(chains=[{**chain, "bendLimit": 4, "joints": springs}]))
        many = [{"name": f"b{i}", "parent": "bone-00000002" if i == 0 else f"b{i - 1}", "bind": [0, 0.01, 0, 0, 0, 0, 1]} for i in range(65)]
        self.assertIn("too many items", "\n".join(errors_of(self.character(chain_glb(), bones=many), library=self.library)))


class ClipTests(unittest.TestCase):
    def clip(self, frames=31):
        rng = np.random.default_rng(3)
        positions = np.cumsum(rng.normal(0, 0.01, (frames, 3, 3)), 0)
        positions[:, 1] = [0.1, 0.2, 0.3]
        rotations = rng.normal(0, 1, (frames, 3, 4))
        rotations[:, 2] = [0.1, 0.2, 0.3, 0.9]
        rotations /= np.linalg.norm(rotations, axis=-1, keepdims=True)
        return positions, rotations, [(0, 3), (4, 3), (7, 2)]

    def test_round_trip_error(self):
        positions, rotations, tracks = self.clip()
        data = q48.encode(positions, rotations, tracks, 8, 1.0)
        header, p, q = q48.decode(data)
        self.assertEqual((header["frames"], header["bones"], header["tracks"]), (31, 8, tracks))
        span = positions[:, 0].max(0) - positions[:, 0].min(0)
        self.assertLessEqual(np.abs(p[:, 0] - positions[:, 0]).max(), (span / 65535).max() * 0.51 + 1e-7)
        self.assertLess(np.abs(p[:, 1] - positions[:, 1]).max(), 1e-6)
        dots = np.abs(np.sum(q[:, :2] * rotations[:, :2], -1)).clip(0, 1)
        self.assertLess(np.degrees(2 * np.arccos(dots)).max(), 0.0075)
        self.assertLess(np.abs(np.abs(np.sum(q[:, 2] * rotations[:, 2], -1)) - 1).max(), 1e-6)
        self.assertEqual(len(data), 18 + 9 + (24 + 6 * 31) + 12 + 2 * (31 + 6 * 31) + 16)

    def test_rejects_malformed_clips(self):
        positions, rotations, tracks = self.clip()
        data = q48.encode(positions, rotations, tracks, 8, 1.0)
        for broken in (data[:-1], data + b"\0", b"GH3CLX01" + data[8:], data[:18] + bytes([9]) + data[19:]):
            with self.assertRaises(ValueError): q48.decode(broken)
        index = 18 + 9 + (24 + 6 * 31) + 12
        with self.assertRaises(ValueError): q48.decode(data[:index] + b"\x04" + data[index + 1:])

    def test_library_clip_files(self):
        try:
            import brotli
        except ImportError:
            self.skipTest("brotli is not installed")
        positions, rotations, tracks = self.clip()
        clip = brotli.compress(q48.encode(positions, rotations, tracks, 8, 1.0), quality=11)
        member = {"bones": ["00000001"] * 8, "groups": {}, "layers": {}, "clips": {"0000696c": {"duration": 1.0, "frames": 31, "rootDistance": 0}}}
        catalog = {"version": 1, "library": "gh3-band-v1", "members": {"drummer": member}}
        performance = {"version": 1, "members": {"drummer": member}, "constants": {}}
        files = {"catalog.json": json.dumps(catalog).encode(), "band/band.json": json.dumps({"version": 1, "members": []}).encode(),
                 "direction/performance.json": json.dumps(performance).encode(), "direction/drummer/0000696c.q48.br": clip,
                 "direction/drummer/broken.q48.br": brotli.compress(b"GH3CLQ01")}
        with tempfile.TemporaryDirectory() as folder:
            entry = {"catalog": "catalog.json", "band": "band/band.json", "performance": "direction/performance.json"}
            path = package(folder, files, name="gh3-band-v1.chlib", kind="chlib", id="gh3-band-v1", entry=entry, preview=None, requires=None)
            report = Validator(path).run()
        self.assertEqual(report.errors, ["direction/drummer/broken.q48.br: invalid clip: Truncated q48 header"])


if __name__ == "__main__":
    unittest.main()
