"""Tests of the Blender add-on parts that run without Blender (blender/chstage_tools). blender/test.sh runs the rest in Blender."""

import importlib.util
import json
import math
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path

import numpy as np

from pipeline.package.validate import Validator
from pipeline.stagepack import template

ROOT = Path(__file__).resolve().parents[1]
ADDON = ROOT / "blender" / "chstage_tools"


def load(path, name):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


sidecars = load(ADDON / "sidecars.py", "chstage_sidecars")
gltf_io = load(ADDON / "gltf_io.py", "chstage_gltf_io")
build = load(ROOT / "blender" / "build.py", "chstage_build")


def blender_matrix(location, axis=None, degrees=0):
    m = np.eye(4)
    if axis is not None:
        a = math.radians(degrees); c, s = math.cos(a), math.sin(a)
        m[:3, :3] = {"x": [[1, 0, 0], [0, c, -s], [0, s, c]], "z": [[c, -s, 0], [s, c, 0], [0, 0, 1]]}[axis]
    m[:3, 3] = location
    return m.tolist()


def package():
    return {"id": "author.my-stage", "version": "1.0.0", "name": "My Stage", "authors": [{"name": "Me", "role": "", "url": ""}],
            "license": "CC-BY-4.0", "permissions": {"redistribute": True, "modify": True, "commercial": False},
            "requires": [{"id": "gh3-band-v1", "version": "1.0.0"}], "credits": [], "homepage": "", "minModVersion": ""}


def band(role, location, degrees=0):
    return {"object": f"band-{role}", "kind": "BAND", "role": role, "matrix": blender_matrix(location, "z", degrees)}


def camera(name, location, groups, anchor="world", look_at=None, fov=70.0, degrees=90):
    # A Blender camera turned 90 degrees about X looks along Blender +Y: runtime +Z.
    point = {"object": name, "matrix": blender_matrix(location, "x", degrees), "fov": fov, "anchor": anchor, "lookAt": look_at, "screenOffset": None}
    return {"object": name, "name": name, "type": "stage", "control": "TwoCam", "tag": "", "groups": groups, "waypoints": [], "times": [],
            "interpolation": None, "orbitAngle": None, "angularSpeed": None, "handCam": False, "disableOrbit1": False, "disableOrbit2": False,
            "points": [point]}


class CoordinateTests(unittest.TestCase):
    def test_blender_vectors_become_runtime_vectors(self):
        frame = sidecars.transform(blender_matrix([1, 2, 3]))
        self.assertEqual(frame, {"position": [1.0, 3.0, 2.0], "rotation": [0.0, 0.0, 0.0, 1.0]})

    def test_camera_looking_along_blender_y_has_identity_runtime_rotation(self):
        position, rotation = sidecars.runtime_frame(blender_matrix([0, -12, 3], "x", 90), looks=True)
        np.testing.assert_allclose(position, [0, 3, -12])
        np.testing.assert_allclose(rotation, np.eye(3), atol=1e-12)

    def test_blender_turn_about_up_is_a_runtime_turn_about_y(self):
        _, rotation = sidecars.runtime_frame(blender_matrix([0, 0, 0], "z", 90))
        # Left-handed runtime frame: the Blender counterclockwise turn (seen from above) is a negative turn about +Y.
        np.testing.assert_allclose(rotation @ [1, 0, 0], [0, 0, 1], atol=1e-12)
        q = sidecars.quaternion(rotation)
        np.testing.assert_allclose(q, [0, -math.sqrt(0.5), 0, math.sqrt(0.5)], atol=1e-12)

    def test_runtime_frame_and_blender_matrix_are_inverse(self):
        rng = np.random.default_rng(3)
        for _ in range(20):
            q = rng.normal(size=4); q /= np.linalg.norm(q)
            position = rng.normal(size=3)
            for looks in (False, True):
                matrix = sidecars.blender_matrix(position, q, looks)
                back_position, back_rotation = sidecars.runtime_frame(matrix, looks)
                np.testing.assert_allclose(back_position, position, atol=1e-12)
                np.testing.assert_allclose(back_rotation, sidecars.quaternion_matrix(q), atol=1e-12)

    def test_scale_does_not_change_the_rotation(self):
        matrix = np.asarray(blender_matrix([0, 0, 0], "z", 30)); matrix[:3, :3] *= 2.5
        np.testing.assert_allclose(sidecars.runtime_frame(matrix)[1], sidecars.runtime_frame(blender_matrix([0, 0, 0], "z", 30))[1], atol=1e-12)


class SidecarTests(unittest.TestCase):
    def test_anchored_point_is_relative_to_the_performer(self):
        issues = sidecars.Issues()
        markers = [band("guitarist", [2, 0, 0], 90)]
        shot = camera("guitar", [2, -2.5, 1.5], ["guitarist"], anchor="guitarist.root", look_at="guitarist.head")
        cameras = sidecars.build_cameras([shot, camera("wide", [0, -12, 3], ["wide"])], [], markers, issues)
        point = cameras["shots"]["guitar"]["points"][0]
        # The offset is in the performer frame: it turns with the performer.
        np.testing.assert_allclose(point["position"], [-2.5, 1.5, 0], atol=1e-6)
        self.assertEqual(point["lookAt"], "guitarist.head")
        self.assertEqual(cameras["groups"], {"guitarist": ["guitar"], "wide": ["wide"]})
        self.assertEqual(issues.items, [])

    def test_bone_anchor_warns_and_missing_performer_is_an_error(self):
        issues = sidecars.Issues()
        sidecars.build_cameras([camera("a", [0, 0, 0], ["wide"], anchor="singer.head"), camera("b", [0, 0, 0], ["wide"], anchor="drummer.root")],
                               [], [band("singer", [0, 0, 0])], issues)
        self.assertEqual([(s, o) for s, _, o, _ in issues.items], [("WARNING", "a"), ("ERROR", "b")])

    def test_missing_wide_group_duplicate_roles_and_lights(self):
        issues = sidecars.Issues()
        sidecars.build_cameras([camera("a", [0, 0, 0], ["guitarist"])], [], [], issues)
        sidecars.build_placements([band("singer", [0, 0, 0]), band("singer", [1, 0, 0]) | {"object": "other"}], [], issues)
        lights = [{"source": "Lamp", "id": "Stage", "group": "Stage", "type": "Ambient", "matrix": blender_matrix([0, 0, 0])}]
        lighting = sidecars.build_lighting(lights, [{"name": "fill", "lights": [], "beams": []}], [{"mood": "intro", "snapshots": ["fill"]}], [], issues)
        messages = [(m, o) for _, m, o, _ in issues.items]
        self.assertIn(("no camera shot is in the group 'wide' (required)", None), messages)
        self.assertIn(("two band placements for the role singer", "other"), messages)
        self.assertIn(("snapshot 'fill' has no settings for the light 'Stage'; use Capture", "Lamp"), messages)
        self.assertIn(("lighting needs the mood 'blackout'", None), messages)
        self.assertEqual(len(lighting["snapshots"]["fill"]["lights"]), 1)

    def test_material_settings_map_to_gltf_and_extras(self):
        base = {"shading": "lit", "blend": "OPAQUE", "alphaCutoff": 0.5}
        self.assertEqual(sidecars.material_fields(base), ({"gh3Shading": "lit"}, {"alphaMode": "OPAQUE", "doubleSided": False}))
        self.assertEqual(sidecars.material_fields(base | {"blend": "CUTOUT", "alphaCutoff": 0.3, "twoSided": True})[1],
                         {"alphaMode": "MASK", "alphaCutoff": 0.3, "doubleSided": True})
        extras, gltf = sidecars.material_fields(base | {"shading": "unlit", "blend": "ADDITIVE", "blendCutoff": True, "colorScale": 2.0,
                                                        "crowdAtlas": True, "crowdRole": "crowd3", "billboard": True, "movieSlot": 0,
                                                        "sortedTransparency": True, "flipbook": {"columns": 4, "rows": 2, "fps": 8, "offset": 0},
                                                        "lightmap": "lightmap.png"})
        self.assertEqual(extras, {"gh3Shading": "unlit", "gh3Additive": True, "gh3AlphaCutoff": 0.5, "gh3ColorScale": 2.0, "gh3CrowdAtlas": True,
                                  "gh3CrowdRole": "crowd3", "gh3Billboard": True, "gh3MovieSlot": 0, "gh3SortedTransparency": True,
                                  "gh3TextureAnimation": {"columns": 4, "rows": 2, "fps": 8, "offset": 0}})
        self.assertEqual(gltf, {"alphaMode": "BLEND", "doubleSided": False, "occlusionTexture": {"image": "lightmap.png", "texCoord": 1}})

    def test_node_extras(self):
        node = {"lightGroup": "Band", "visibilityGroup": "crowd-lighters", "initiallyVisible": False, "fixtureId": "beam-1",
                "fixtureFixedPosition": True, "fixtureObject": "housing", "fixtureCreated": False, "object": "door", "pivot": True,
                "created": True, "script": "door_open"}
        self.assertEqual(sidecars.node_extras(node, blender_matrix([1, 2, 3])), {
            "gh3LightGroup": "Band", "gh3VisibilityGroup": "crowd-lighters", "gh3InitiallyVisible": False, "gh3FixtureId": "beam-1",
            "gh3FixtureFixedPosition": True, "gh3FixtureObject": "housing", "gh3FixtureCreated": False, "gh3Object": "door",
            "gh3ObjectPivot": [1.0, 3.0, 2.0], "gh3ObjectRotation": [0.0, 0.0, 0.0, 1.0], "gh3ObjectCreated": True, "gh3ObjectScript": "door_open"})
        self.assertEqual(sidecars.node_extras({"initiallyVisible": False}), {})

    def test_template_scene_description_is_a_valid_package(self):
        """The template layout through sidecars.py, with the example.studio stage GLB, passes the shared validator."""
        issues = sidecars.Issues()
        markers = [band(role, [x, z, y]) for role, (x, y, z) in {"guitarist": (2, 0, 0), "bassist": (-3, 0, 0), "singer": (0, 0, -0.5), "drummer": (0, 0, 3)}.items()]
        markers.append({"object": "w", "kind": "WAYPOINT", "name": "guitarist_walk01", "matrix": blender_matrix([4, 0, 0])})
        markers.append({"object": "p", "kind": "EFFECT", "effect": "pyro", "matrix": blender_matrix([-6, 0, 0])})
        lights = [{"source": g, "id": g, "group": g, "type": "Ambient", "matrix": blender_matrix([0, 0, 0])} for g in ("Stage", "Band")]
        beam = {"id": "left-beam", "position": [-5, 3, 5], "rotation": [1, 0, 0, 0], "color": [0.1, 0.6, 1], "density": 0.6,
                "startRadius": 0.1, "endRadius": 1.8, "range": 5}
        snapshot = {"name": "fill", "lights": [{"light": g, "color": [1, 1, 1], "intensity": 0.65, "near": 0, "far": 200} for g in ("Stage", "Band")], "beams": [beam]}
        lighting = sidecars.build_lighting(lights, [snapshot], [{"mood": "intro", "snapshots": ["fill"]}, {"mood": "blackout", "snapshots": ["fill"]}], [], issues)
        self.assertEqual(lighting["snapshots"]["fill"]["beams"]["left-beam"]["rotation"], [0.707107, 0.0, 0.0, 0.707107])
        files = {"manifest.json": sidecars.build_manifest(package(), True),
                 "placements.json": sidecars.build_placements(markers, [{"role": "crowd1", "model": "crowd_a", "yaw": 10}], issues),
                 "cameras.json": sidecars.build_cameras([camera("wide", [0, -12, 3], ["wide", "default"], look_at="singer.head")], [{"note": 77, "group": "wide"}], markers, issues),
                 "lighting.json": lighting}
        self.assertEqual(issues.items, [])
        with tempfile.TemporaryDirectory() as temporary:
            template.create(Path(temporary))
            mapped = {name: json.dumps(value).encode() for name, value in files.items()}
            mapped["stage.glb"] = Path(temporary, "stage.glb")
            report = Validator("author.my-stage.chstage", files=mapped).run()
        self.assertEqual(report.errors, [])


class GltfSettingsTests(unittest.TestCase):
    def test_ordered_set_keeps_insertion_order(self):
        names = gltf_io._OrderedSet()
        names.update(gltf_io._OrderedSet(["_GH3_UV0", "_GH3_BINORMAL", "_GH3_UV1"]))
        names.update(["_GH3_UV1", "_GH3_TANGENT"])
        self.assertEqual(list(names), ["_GH3_UV0", "_GH3_BINORMAL", "_GH3_UV1", "_GH3_TANGENT"])
        self.assertIn("_GH3_TANGENT", names)

    def test_safe_options_match_the_spike(self):
        self.assertEqual(gltf_io.IMPORT_OPTIONS["bone_heuristic"], "BLENDER")
        self.assertFalse(gltf_io.IMPORT_OPTIONS["merge_vertices"])
        for key in ("export_extras", "export_attributes", "export_yup", "export_rest_position_armature", "use_active_scene"):
            self.assertTrue(gltf_io.EXPORT_OPTIONS[key])
        self.assertFalse(gltf_io.EXPORT_OPTIONS["export_hierarchy_flatten_bones"])
        self.assertFalse(gltf_io.fix_custom_attribute_import())


class BuildTests(unittest.TestCase):
    def test_extension_zip_bundles_the_shared_validator(self):
        with tempfile.TemporaryDirectory() as temporary:
            archive = build.build(Path(temporary))
            with zipfile.ZipFile(archive) as bundle:
                names = set(bundle.namelist())
                self.assertIn("blender_manifest.toml", names)
                self.assertFalse(any("__pycache__" in n for n in names))
                for name in build.VALIDATOR_FILES:
                    self.assertEqual(bundle.read(f"vendor/package_format/{name}"), (ROOT / "pipeline" / "package" / name).read_bytes())
                for schema in (ROOT / "schemas").glob("*.schema.json"):
                    self.assertEqual(bundle.read(f"vendor/package_format/schemas/{schema.name}"), schema.read_bytes())
                bundle.extractall(Path(temporary) / "extension")
            # The bundled copy works as its own package, with its own schemas, without the repository on the path.
            vendor = Path(temporary) / "extension" / "vendor"
            load(vendor / "__init__.py", "chstage_vendor")
            load(vendor / "package_format" / "__init__.py", "chstage_vendor.package_format")
            for name in ("glb", "q48", "schema", "validate"):
                module = load(vendor / "package_format" / f"{name}.py", f"chstage_vendor.package_format.{name}")
            schema = sys.modules["chstage_vendor.package_format.schema"]
            self.assertEqual(schema.SCHEMA_DIR, vendor / "package_format" / "schemas")
            report = module.Validator("x.chstage", files={"manifest.json": b"{}"}).run()
            self.assertTrue(any("missing field 'format'" in e for e in report.errors), report.errors)
            manifest = (ADDON / "blender_manifest.toml").read_text()
            self.assertIn('blender_version_min = "4.2.0"', manifest)


if __name__ == "__main__":
    unittest.main()
