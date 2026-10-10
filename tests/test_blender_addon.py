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
glb_tools = load(ADDON / "glb_tools.py", "chstage_glb_tools")
PACKAGES = ROOT / "assets" / "packages"
build = load(ROOT / "blender" / "build.py", "chstage_build")


def blender_matrix(location, axis=None, degrees=0):
    m = np.eye(4)
    if axis is not None:
        a = math.radians(degrees)
        c, s = math.cos(a), math.sin(a)
        m[:3, :3] = {"x": [[1, 0, 0], [0, c, -s], [0, s, c]], "z": [[c, -s, 0], [s, c, 0], [0, 0, 1]]}[axis]
    m[:3, 3] = location
    return m.tolist()


def package():
    return {
        "id": "author.my-stage",
        "version": "1.0.0",
        "name": "My Stage",
        "authors": [{"name": "Me", "role": "", "url": ""}],
        "license": "CC-BY-4.0",
        "permissions": {"redistribute": True, "modify": True, "commercial": False},
        "requires": [{"id": "gh3-band-v1", "version": "1.0.0"}],
        "credits": [],
        "homepage": "",
        "minModVersion": "",
    }


def band(role, location, degrees=0):
    return {"object": f"band-{role}", "kind": "BAND", "role": role, "matrix": blender_matrix(location, "z", degrees)}


def camera(name, location, groups, anchor="world", look_at=None, fov=70.0, degrees=90):
    # A Blender camera turned 90 degrees about X looks along Blender +Y: runtime +Z.
    point = {
        "object": name,
        "matrix": blender_matrix(location, "x", degrees),
        "fov": fov,
        "anchor": anchor,
        "lookAt": look_at,
        "screenOffset": None,
    }
    return {
        "object": name,
        "name": name,
        "type": "stage",
        "control": "TwoCam",
        "tag": "",
        "groups": groups,
        "waypoints": [],
        "times": [],
        "interpolation": None,
        "orbitAngle": None,
        "angularSpeed": None,
        "handCam": False,
        "disableOrbit1": False,
        "disableOrbit2": False,
        "points": [point],
    }


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
            q = rng.normal(size=4)
            q /= np.linalg.norm(q)
            position = rng.normal(size=3)
            for looks in (False, True):
                matrix = sidecars.blender_matrix(position, q, looks)
                back_position, back_rotation = sidecars.runtime_frame(matrix, looks)
                np.testing.assert_allclose(back_position, position, atol=1e-12)
                np.testing.assert_allclose(back_rotation, sidecars.quaternion_matrix(q), atol=1e-12)

    def test_scale_does_not_change_the_rotation(self):
        matrix = np.asarray(blender_matrix([0, 0, 0], "z", 30))
        matrix[:3, :3] *= 2.5
        np.testing.assert_allclose(
            sidecars.runtime_frame(matrix)[1], sidecars.runtime_frame(blender_matrix([0, 0, 0], "z", 30))[1], atol=1e-12
        )


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
        sidecars.build_cameras(
            [
                camera("a", [0, 0, 0], ["wide"], anchor="singer.head"),
                camera("b", [0, 0, 0], ["wide"], anchor="drummer.root"),
            ],
            [],
            [band("singer", [0, 0, 0])],
            issues,
        )
        self.assertEqual([(s, o) for s, _, o, _ in issues.items], [("WARNING", "a"), ("ERROR", "b")])

    def test_missing_wide_group_duplicate_roles_and_lights(self):
        issues = sidecars.Issues()
        sidecars.build_cameras([camera("a", [0, 0, 0], ["guitarist"])], [], [], issues)
        sidecars.build_placements(
            [band("singer", [0, 0, 0]), band("singer", [1, 0, 0]) | {"object": "other"}], [], issues
        )
        lights = [
            {"source": "Lamp", "id": "Stage", "group": "Stage", "type": "Ambient", "matrix": blender_matrix([0, 0, 0])}
        ]
        lighting = sidecars.build_lighting(
            lights,
            [{"name": "fill", "lights": [], "beams": []}],
            [{"mood": "intro", "snapshots": ["fill"]}],
            [],
            issues,
        )
        messages = [(m, o) for _, m, o, _ in issues.items]
        self.assertIn(("no camera shot is in the group 'wide' (required)", None), messages)
        self.assertIn(("two band placements for the role singer", "other"), messages)
        self.assertIn(("snapshot 'fill' has no settings for the light 'Stage'; use Capture", "Lamp"), messages)
        self.assertIn(("lighting needs the mood 'blackout'", None), messages)
        self.assertEqual(len(lighting["snapshots"]["fill"]["lights"]), 1)

    def test_material_settings_map_to_gltf_and_extras(self):
        base = {"shading": "lit", "blend": "OPAQUE", "alphaCutoff": 0.5}
        self.assertEqual(
            sidecars.material_fields(base), ({"gh3Shading": "lit"}, {"alphaMode": "OPAQUE", "doubleSided": False})
        )
        self.assertEqual(sidecars.material_fields(base | {"shading": "NONE"})[0], {})
        self.assertEqual(
            sidecars.material_fields(base | {"blend": "CUTOUT", "alphaCutoff": 0.3, "twoSided": True})[1],
            {"alphaMode": "MASK", "alphaCutoff": 0.3, "doubleSided": True},
        )
        extras, gltf = sidecars.material_fields(
            base
            | {
                "shading": "unlit",
                "blend": "ADDITIVE",
                "blendCutoff": True,
                "colorScale": 2.0,
                "crowdAtlas": True,
                "crowdRole": "crowd3",
                "billboard": True,
                "movieSlot": 0,
                "sortedTransparency": True,
                "flipbook": {"columns": 4, "rows": 2, "fps": 8, "offset": 0},
                "lightmap": "lightmap.png",
            }
        )
        self.assertEqual(
            extras,
            {
                "gh3Shading": "unlit",
                "gh3Additive": True,
                "gh3AlphaCutoff": 0.5,
                "gh3ColorScale": 2.0,
                "gh3CrowdAtlas": True,
                "gh3CrowdRole": "crowd3",
                "gh3Billboard": True,
                "gh3MovieSlot": 0,
                "gh3SortedTransparency": True,
                "gh3TextureAnimation": {"columns": 4, "rows": 2, "fps": 8, "offset": 0},
            },
        )
        self.assertEqual(
            gltf,
            {"alphaMode": "BLEND", "doubleSided": False, "occlusionTexture": {"image": "lightmap.png", "texCoord": 1}},
        )

    def test_toon_settings_map_to_extras_and_back(self):
        toon = {
            "threshold": 0.4,
            "softness": 0.07,
            "shadeColor": [0.8, 0.79, 0.86],
            "shadeTexture": "shade",
            "shadeMaskTexture": None,
            "rimColor": [0, 0, 0],
            "rimWidth": 0,
            "sphereTexture": "sphere.png",
            "sphereMode": "add",
            "outline": {"width": 0.0015, "color": [0.3, 0.2, 0.4], "textured": False},
        }
        extras, _ = sidecars.material_fields(
            {"shading": "toon", "blend": "OPAQUE", "alphaCutoff": 0.5, "toon": toon, "hairLayer": "hair"}
        )
        self.assertEqual(
            extras,
            {
                "gh3Shading": "toon",
                "gh3HairLayer": "hair",
                "gh3Toon": {
                    "threshold": 0.4,
                    "softness": 0.07,
                    "shadeColor": [0.8, 0.79, 0.86],
                    "shadeTexture": "#shade",
                    "sphereTexture": "#sphere.png",
                    "sphereMode": "add",
                    "outline": {"width": 0.0015, "color": [0.3, 0.2, 0.4], "textured": False},
                },
            },
        )
        props, other = sidecars.open_material({"extras": extras})
        self.assertEqual(other, {})
        self.assertEqual(
            (
                props["shading"],
                props["hair_layer"],
                props["toon_threshold"],
                props["use_outline"],
                props["outline_color"],
            ),
            ("toon", "hair", 0.4, True, [0.3, 0.2, 0.4]),
        )
        self.assertEqual(
            sidecars.toon_references(extras), {"toon_shade_image": "#shade", "toon_sphere_image": "#sphere.png"}
        )

    def test_mmd_toon_ramp(self):
        # toon_defo.bmp of the Tda model: 8 white rows, a 3-row blend, then the shade color.
        rows = [[255] * 3] * 8 + [[241, 240, 244], [221, 218, 230], [206, 202, 220]] + [[206, 202, 220]] * 5
        threshold, softness, shade = sidecars.toon_ramp_settings(
            np.repeat(np.array(rows, dtype=float)[:, None, :] / 255, 16, axis=1)
        )
        self.assertAlmostEqual(threshold, 0.436, delta=0.01)
        self.assertAlmostEqual(softness, 0.072, delta=0.01)
        self.assertEqual([round(c, 3) for c in shade], [0.808, 0.792, 0.863])
        self.assertEqual(sidecars.toon_ramp_settings(np.ones((4, 4, 3)))[:2], (0.0, 0.001))

    def test_node_extras(self):
        node = {
            "lightGroup": "Band",
            "visibilityGroup": "crowd-lighters",
            "initiallyVisible": False,
            "fixtureId": "beam-1",
            "fixtureFixedPosition": True,
            "fixtureObject": "housing",
            "fixtureCreated": False,
            "object": "door",
            "pivot": "ORIGIN",
            "created": True,
            "script": "door_open",
        }
        self.assertEqual(
            sidecars.node_extras(node, blender_matrix([1, 2, 3])),
            {
                "gh3LightGroup": "Band",
                "gh3VisibilityGroup": "crowd-lighters",
                "gh3InitiallyVisible": False,
                "gh3FixtureId": "beam-1",
                "gh3FixtureFixedPosition": True,
                "gh3FixtureObject": "housing",
                "gh3FixtureCreated": False,
                "gh3Object": "door",
                "gh3ObjectPivot": [1.0, 3.0, 2.0],
                "gh3ObjectRotation": [0.0, 0.0, 0.0, 1.0],
                "gh3ObjectCreated": True,
                "gh3ObjectScript": "door_open",
            },
        )
        self.assertEqual(sidecars.node_extras({"initiallyVisible": False}), {})

    def test_template_scene_description_is_a_valid_package(self):
        """The template layout through sidecars.py, with the example.studio stage GLB, passes the shared validator."""
        issues = sidecars.Issues()
        markers = [
            band(role, [x, z, y])
            for role, (x, y, z) in {
                "guitarist": (2, 0, 0),
                "bassist": (-3, 0, 0),
                "singer": (0, 0, -0.5),
                "drummer": (0, 0, 3),
            }.items()
        ]
        markers.append(
            {"object": "w", "kind": "WAYPOINT", "name": "guitarist_walk01", "matrix": blender_matrix([4, 0, 0])}
        )
        markers.append({"object": "p", "kind": "EFFECT", "effect": "pyro", "matrix": blender_matrix([-6, 0, 0])})
        lights = [
            {"source": g, "id": g, "group": g, "type": "Ambient", "matrix": blender_matrix([0, 0, 0])}
            for g in ("Stage", "Band")
        ]
        beam = {
            "id": "left-beam",
            "position": [-5, 3, 5],
            "rotation": [1, 0, 0, 0],
            "color": [0.1, 0.6, 1],
            "density": 0.6,
            "startRadius": 0.1,
            "endRadius": 1.8,
            "range": 5,
        }
        snapshot = {
            "name": "fill",
            "lights": [
                {"light": g, "color": [1, 1, 1], "intensity": 0.65, "near": 0, "far": 200} for g in ("Stage", "Band")
            ],
            "beams": [beam],
        }
        lighting = sidecars.build_lighting(
            lights,
            [snapshot],
            [{"mood": "intro", "snapshots": ["fill"]}, {"mood": "blackout", "snapshots": ["fill"]}],
            [],
            issues,
        )
        self.assertEqual(
            lighting["snapshots"]["fill"]["beams"]["left-beam"]["rotation"], [0.707107, 0.0, 0.0, 0.707107]
        )
        files = {
            "manifest.json": sidecars.build_manifest(package(), sidecars.stage_entry(True)),
            "placements.json": sidecars.build_placements(
                markers, [{"role": "crowd1", "model": "crowd_a", "yaw": 10}], issues
            ),
            "cameras.json": sidecars.build_cameras(
                [camera("wide", [0, -12, 3], ["wide", "default"], look_at="singer.head")],
                [{"note": 77, "group": "wide"}],
                markers,
                issues,
            ),
            "lighting.json": lighting,
        }
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
        for key in (
            "export_extras",
            "export_attributes",
            "export_yup",
            "export_rest_position_armature",
            "use_active_scene",
        ):
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
                # The manifest declares GPL-3.0-or-later; the license text goes with the extension.
                self.assertTrue(bundle.read("LICENSE").decode().lstrip().startswith("GNU GENERAL PUBLIC LICENSE"))
                self.assertFalse(any("__pycache__" in n for n in names))
                for name in build.VALIDATOR_FILES:
                    self.assertEqual(
                        bundle.read(f"vendor/package_format/{name}"),
                        (ROOT / "pipeline" / "package" / name).read_bytes(),
                    )
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


def float32(matrix):
    """A matrix as Blender stores it (float32)."""
    return np.asarray(matrix, dtype=np.float32).astype(np.float64)


def scene_from_package(folder):
    """The plain scene descriptions that "Open .chstage" makes from a package (as package_open.py and collect.py do,
    with float32 matrices), for the build functions."""
    placements = json.loads((folder / "placements.json").read_text())
    cameras = json.loads((folder / "cameras.json").read_text())
    markers, crowd, keys = sidecars.open_placements(placements)
    markers = [{**m, "object": f"marker{i}", "matrix": float32(m["matrix"])} for i, m in enumerate(markers)]
    shots, notes = sidecars.open_cameras(cameras, placements)
    shot_rows = []
    for shot in shots:
        points = [
            {
                "object": shot["name"],
                "matrix": float32(p["matrix"]),
                "fov": p["fov"],
                "anchor": sidecars.anchor_name(p["anchorRole"], p["anchorPart"], p["anchorBone"]),
                "lookAt": None
                if p["lookAtRole"] == "none"
                else sidecars.anchor_name(p["lookAtRole"], p["lookAtPart"], p["lookAtBone"]),
                "useRotation": p["useRotation"],
                "screenOffset": p["screenOffset"],
            }
            for p in shot["points"]
        ]
        shot_rows.append(
            {
                **shot,
                "object": shot["name"],
                "interpolation": None if shot["interpolation"] == "default" else shot["interpolation"],
                "points": points,
            }
        )
    lighting = json.loads((folder / "lighting.json").read_text())
    lights, snapshots, moods, colors = sidecars.open_lighting(lighting)
    lights = [
        {
            **light,
            "source": light["id"],
            "matrix": float32(light["matrix"]),
            **(
                {"hotspot": light["hotspot"], "field": light["field"]}
                if light["useCone"]
                else {"hotspot": None, "field": None}
            ),
        }
        for light in lights
    ]
    snapshots = [
        {**snap, "lights": [{**row, "light": lights[row["index"]]["source"]} for row in snap["lights"]]}
        for snap in snapshots
    ]
    return {
        "placements": (markers, crowd, keys),
        "cameras": (shot_rows, notes, markers),
        "lighting": (lights, snapshots, moods, colors),
    }


def json_close(test, a, b, path=""):
    """a and b are equal; numbers within 1e-5; q and -q are equal; keys with default values (false, "", []) can be missing in b."""
    if isinstance(a, dict):
        for key, value in a.items():
            if key not in b:
                test.assertIn(value, (False, "", [], {}, 0), f"{path}.{key}")
                continue
            json_close(test, value, b[key], f"{path}.{key}")
        test.assertEqual(set(b) - set(a), set(), path)
    elif isinstance(a, list):
        test.assertEqual(len(a), len(b), path)
        if path.endswith("otation") and len(a) == 4 and np.dot(a, b) < 0:
            b = [-v for v in b]
        for index, (x, y) in enumerate(zip(a, b)):
            json_close(test, x, y, f"{path}[{index}]")
    elif isinstance(a, (int, float)) and not isinstance(a, bool):
        test.assertAlmostEqual(a, b, delta=1e-5, msg=path)
    else:
        test.assertEqual(a, b, path)


class Owner:
    def __init__(self, kept=""):
        self.kept = kept


class OpenTests(unittest.TestCase):
    """The open_* functions of sidecars.py give back the package files through the build functions."""

    def check_package(self, folder):
        scene = sidecars.Issues()
        described = scene_from_package(folder)
        markers, crowd, keys = described["placements"]
        placements = sidecars.build_placements(markers, crowd, scene, keys=keys)
        json_close(self, json.loads((folder / "placements.json").read_text()), placements)
        shots, notes, markers = described["cameras"]
        cameras = sidecars.build_cameras(shots, notes, markers, scene)
        source = json.loads((folder / "cameras.json").read_text())
        # Group member order comes from the shot order; the kept source rows give the source order (Keeper).
        self.assertEqual(
            {g: sorted(m) for g, m in source["groups"].items()}, {g: sorted(m) for g, m in cameras["groups"].items()}
        )
        cameras["groups"] = source["groups"]
        json_close(self, source, cameras)
        lighting = sidecars.build_lighting(*described["lighting"], scene)
        json_close(self, json.loads((folder / "lighting.json").read_text()), lighting)
        self.assertEqual([m for s_, m, _, _ in scene.items if s_ == "ERROR"], [])

    def test_example_studio(self):
        folder = PACKAGES / "unpacked" / "example.studio.chstage"
        if not folder.is_dir():
            self.skipTest("assets/packages is not generated")
        self.check_package(folder)

    def test_gh3_venues(self):
        folders = sorted((PACKAGES / "unpacked").glob("gh3.*.chstage"))
        if not folders:
            self.skipTest("assets/packages is not generated")
        for folder in folders:
            with self.subTest(folder.name):
                self.check_package(folder)

    def test_gh3_materials_and_nodes(self):
        folder = PACKAGES / "unpacked" / "gh3.dive.chstage"
        if not folder.is_dir():
            self.skipTest("assets/packages is not generated")
        gltf, _ = glb_tools.read_glb((folder / "stage.glb").read_bytes())
        for material in gltf["materials"]:
            props, other = sidecars.open_material(material)
            description = {
                "shading": props["shading"],
                "blend": props["blend"],
                "alphaCutoff": props["alpha_cutoff"],
                "blendCutoff": props["use_blend_cutoff"],
                "twoSided": props["two_sided"],
                "colorScale": props["color_scale"],
                "crowdAtlas": props["crowd_atlas"],
                "crowdRole": None if props["crowd_role"] == "NONE" else props["crowd_role"],
                "billboard": props["billboard"],
                "movieSlot": props["movie_slot"],
                "sortedTransparency": props["sorted_transparency"],
                "flipbook": {k: props[f"flipbook_{k}"] for k in ("columns", "rows", "fps", "offset")}
                if props["use_flipbook"]
                else None,
                "otherExtras": other,
                "opaqueAlpha": props["opaque_alpha"],
            }
            source = sidecars.gltf_material_row(material)
            json_close(self, source, sidecars.material_row(description))
        for node in gltf["nodes"]:
            props = sidecars.open_node(node.get("extras", {}))
            description = {
                "lightGroup": props["light_group"],
                "visibilityGroup": props["visibility_group"],
                "initiallyVisible": props["initially_visible"],
                "fixtureId": props["fixture_id"],
                "fixtureFixedPosition": props["fixture_fixed_position"],
                "fixtureObject": props["fixture_object"],
                "fixtureCreated": props["fixture_created"] if props["use_fixture_created"] else None,
                "object": props["script_object"],
                "pivot": props.get("object_pivot_mode", "NONE"),
                "pivotPosition": props.get("object_pivot"),
                "pivotRotation": props.get("object_rotation"),
                "created": props["object_created"] if props["use_object_created"] else None,
                "script": props["object_script"],
            }
            json_close(self, node.get("extras", {}), sidecars.node_extras(description))

    def test_bone_anchor_keeps_the_local_offset(self):
        placements = {
            "version": 1,
            "band": {"guitarist": {"position": [2, 0, 1], "rotation": [0, 0.3826834, 0, 0.9238795]}},
        }
        point = {
            "anchor": "guitarist.bone.c00e3395",
            "position": [0.1, 1.6, -0.4],
            "rotation": [0.1, 0.2, 0.0, 0.9746794],
            "horizontalFov": 40,
        }
        cameras = {
            "version": 1,
            "shots": {"hand": {"type": "closeup", "control": "TwoCam", "points": [point]}},
            "groups": {"wide": ["hand"]},
        }
        markers, _, _ = sidecars.open_placements(placements)
        markers = [{**m, "object": "band"} for m in markers]
        shots, _ = sidecars.open_cameras(cameras, placements)
        opened = shots[0]["points"][0]
        self.assertEqual(
            (opened["anchorRole"], opened["anchorPart"], opened["anchorBone"]), ("guitarist", "bone", "c00e3395")
        )
        row = {
            **shots[0],
            "object": "hand",
            "interpolation": None,
            "points": [
                {
                    "object": "hand",
                    "matrix": opened["matrix"],
                    "fov": 40,
                    "anchor": "guitarist.bone.c00e3395",
                    "lookAt": None,
                    "useRotation": True,
                    "screenOffset": None,
                }
            ],
        }
        issues = sidecars.Issues()
        built = sidecars.build_cameras([row], [], markers, issues)
        json_close(self, point, built["shots"]["hand"]["points"][0])
        self.assertTrue(any("bone c00e3395" in m for _, m, _, _ in issues.items), issues.items)


class KeeperTests(unittest.TestCase):
    def test_unchanged_rows_give_the_source_and_changed_rows_the_new_value(self):
        owner = Owner(sidecars.kept_text(shot={"type": "stage", "waypoints": [], "position": [0.123456789]}))
        recorder = sidecars.Keeper("record")
        self.assertEqual(
            recorder({"type": "stage", "position": [0.123457]}, owner, "shot"),
            {"type": "stage", "position": [0.123457]},
        )
        recorder.store()
        keeper = sidecars.Keeper()
        self.assertEqual(
            keeper({"type": "stage", "position": [0.123457]}, owner, "shot"),
            {"type": "stage", "waypoints": [], "position": [0.123456789]},
        )
        self.assertEqual(
            keeper({"type": "mid", "position": [0.123457]}, owner, "shot"), {"type": "mid", "position": [0.123457]}
        )
        self.assertEqual(
            sidecars.Keeper("compute")({"type": "stage", "position": [0.123457]}, owner, "shot")["position"], [0.123457]
        )
        self.assertEqual(keeper({"a": 1}, Owner(), "shot"), {"a": 1})
        self.assertEqual(keeper({"a": 1}, None, "shot"), {"a": 1})

    def test_manifest_round_trip(self):
        folder = PACKAGES / "unpacked" / "gh3.dive.chstage"
        if not folder.is_dir():
            self.skipTest("assets/packages is not generated")
        manifest = json.loads((folder / "manifest.json").read_text())
        extra = {k: v for k, v in manifest["entry"].items() if k in ("scripts", "actors")}
        built = sidecars.build_manifest(
            sidecars.open_manifest(manifest), sidecars.stage_entry(True, extra), manifest.get("preview")
        )
        self.assertEqual(
            {k: v for k, v in manifest.items() if k != "files"}, {k: v for k, v in built.items() if k != "files"}
        )
        self.assertEqual(list(built["entry"]), ["model", "placements", "cameras", "lighting", "scripts", "actors"])


def test_glb(primitives, extra_accessors=()):
    """A GLB with one mesh. primitives: lists of (attribute name, float32 array) and an index array."""
    gltf = {
        "asset": {"version": "2.0"},
        "buffers": [{"byteLength": 0}],
        "bufferViews": [],
        "accessors": [],
        "meshes": [{"primitives": []}],
        "nodes": [{"mesh": 0}],
        "scenes": [{"nodes": [0]}],
    }
    binary = bytearray()

    def add(array, component, kind):
        binary.extend(b"\0" * (-len(binary) % 4))
        gltf["bufferViews"].append({"buffer": 0, "byteOffset": len(binary), "byteLength": array.nbytes})
        binary.extend(array.tobytes())
        gltf["accessors"].append(
            {"bufferView": len(gltf["bufferViews"]) - 1, "componentType": component, "count": len(array), "type": kind}
        )
        return len(gltf["accessors"]) - 1

    for attributes, indices in primitives:
        primitive = {
            "attributes": {
                name: add(np.asarray(v, dtype="<f4"), 5126, {2: "VEC2", 3: "VEC3"}[np.shape(v)[1]])
                for name, v in attributes
            },
            "indices": add(np.asarray(indices, dtype="<u2"), 5123, "SCALAR"),
        }
        gltf["meshes"][0]["primitives"].append(primitive)
    gltf["buffers"][0]["byteLength"] = len(binary)
    return glb_tools.write_glb(gltf, bytes(binary))


class GlbToolsTests(unittest.TestCase):
    QUAD = [[0, 0, 0], [1, 0, 0], [1, 1, 0], [0, 1, 0]]

    def test_fill_texcoord_gaps(self):
        data = test_glb(
            [
                (
                    [
                        ("POSITION", self.QUAD),
                        ("NORMAL", [[0, 0, 1]] * 4),
                        ("TEXCOORD_0", [[0, 0]] * 4),
                        ("TEXCOORD_3", [[1, 1]] * 4),
                    ],
                    [0, 1, 2],
                )
            ]
        )
        filled, count = glb_tools.fill_texcoord_gaps(data)
        self.assertEqual(count, 2)
        gltf, binary = glb_tools.read_glb(filled)
        attributes = gltf["meshes"][0]["primitives"][0]["attributes"]
        self.assertEqual(
            sorted(attributes), ["NORMAL", "POSITION", "TEXCOORD_0", "TEXCOORD_1", "TEXCOORD_2", "TEXCOORD_3"]
        )
        zeros = glb_tools.accessor_rows(gltf, binary, attributes["TEXCOORD_1"])
        self.assertEqual(zeros.shape, (4, 8))
        self.assertFalse(zeros.any())
        self.assertEqual(glb_tools.fill_texcoord_gaps(filled), (filled, 0))

    def test_separate_faces(self):
        # Triangle 1 repeats triangle 0 with the opposite winding; triangle 2 has two corners at one position.
        positions = self.QUAD + [[0, 0, 0]]
        data = test_glb([([("POSITION", positions), ("NORMAL", [[0, 0, 1]] * 5)], [0, 1, 2, 2, 1, 0, 0, 4, 3])])
        separated, count = glb_tools.separate_faces(data)
        self.assertEqual(count, 2)
        gltf, binary = glb_tools.read_glb(separated)
        primitive = gltf["meshes"][0]["primitives"][0]
        faces = glb_tools.accessor_rows(gltf, binary, primitive["indices"]).view("<u4").reshape(-1, 3)
        self.assertEqual(faces.tolist(), [[0, 1, 2], [5, 6, 7], [8, 9, 10]])
        positions_after = (
            glb_tools.accessor_rows(gltf, binary, primitive["attributes"]["POSITION"]).view("<f4").reshape(-1, 3)
        )
        np.testing.assert_array_equal(
            positions_after[faces], np.asarray(positions, dtype=np.float32)[[[0, 1, 2], [2, 1, 0], [0, 4, 3]]]
        )

    def test_normalize_normals(self):
        data = test_glb([([("POSITION", self.QUAD), ("NORMAL", [[0, 0, 1.25]] * 4)], [0, 1, 2])])
        fixed, count = glb_tools.normalize_normals(data)
        self.assertEqual(count, 1)
        gltf, binary = glb_tools.read_glb(fixed)
        normals = (
            glb_tools.accessor_rows(gltf, binary, gltf["meshes"][0]["primitives"][0]["attributes"]["NORMAL"])
            .view("<f4")
            .reshape(-1, 3)
        )
        np.testing.assert_allclose(normals, [[0, 0, 1]] * 4)
        self.assertEqual(glb_tools.normalize_normals(fixed), (fixed, 0))

    def test_png_text_chunks_are_removed(self):
        header = glb_tools.png_chunk(b"IHDR", bytes(13))
        png = (
            glb_tools.PNG_SIGNATURE
            + header
            + glb_tools.png_chunk(b"tEXt", b"Date\0today")
            + glb_tools.png_chunk(b"IEND", b"")
        )
        self.assertEqual(
            glb_tools.strip_png_text(png), glb_tools.PNG_SIGNATURE + header + glb_tools.png_chunk(b"IEND", b"")
        )

    def test_texture_references(self):
        extras = {"gh3MaskTexture": "#a", "gh3Shader": {"textures": ["#b", "", "textures/c.png"]}}
        self.assertEqual([c[k] for c, k in sidecars.texture_references(extras)], ["#a", "#b", "", "textures/c.png"])


class CompareTests(unittest.TestCase):
    def test_package_equals_itself(self):
        package = PACKAGES / "gh3.credits.chstage"
        if not package.is_file():
            self.skipTest("assets/packages is not generated")
        from pipeline.package import compare

        report = compare.compare_packages(package, PACKAGES / "unpacked" / package.name)
        self.assertEqual(report["failures"], [])
        self.assertEqual(report["stage"]["max"]["POSITION"], 0)

    def test_quaternion_sign(self):
        from pipeline.package import compare

        self.assertEqual(compare.json_differences({"rotation": [0, 0, 0.6, 0.8]}, {"rotation": [0, 0, -0.6, -0.8]}), [])
        self.assertEqual(len(compare.json_differences({"position": [0, 0, 0.6]}, {"position": [0, 0, -0.6]})), 1)


if __name__ == "__main__":
    unittest.main()
