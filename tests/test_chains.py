import math
import unittest

import numpy as np

from pipeline.package import chains
from tests.test_package_format import chain_glb


class ChainRowTests(unittest.TestCase):
    def test_rows_of_an_exported_glb(self):
        settings = dict(chains.PRESETS["skirt"])
        # Blender world: glTF +Y up is Blender +Z; the bones point up.
        joints = {"tail_01": {"settings": settings, "chain": "tail", "axis": [0, 0, 0.2], "side": [1, 0, 0]},
                  "tail_02": {"settings": settings, "chain": "tail", "axis": [0, 0, 0.2], "side": [1, 0, 0]}}
        colliders = [{"name": "body", "bone": "bone-00000001", "radius": 0.1, "a": [0, 0, 1], "b": [0, 0, 1.4]}]
        bones, collider_rows, chain_rows = chains.character_rows(chain_glb(), ["bone-00000001", "bone-00000002"], joints, colliders,
                                                                 [{"name": "tail", "colliders": None}, {"name": "empty", "colliders": None}])
        self.assertEqual([(b["name"], b["parent"]) for b in bones], [("tail_01", "bone-00000002"), ("tail_02", "tail_01")])
        np.testing.assert_allclose(bones[1]["bind"], [0, 0.2, 0, 0, 0, 0, 1], atol=1e-6)
        self.assertEqual(collider_rows, [{"name": "body", "bone": "bone-00000001", "radius": 0.1, "a": [0, 0, 0], "b": [0, 0.4, 0]}])
        self.assertEqual(len(chain_rows), 1)
        joint = chain_rows[0]["joints"][0]
        self.assertEqual((joint["bone"], joint["length"], joint["direction"]), ("tail_01", 0.2, [0, 1, 0]))
        self.assertAlmostEqual(joint["limits"]["coneLimit"][1], math.radians(settings["cone"]), places=5)
        self.assertAlmostEqual(joint["limits"]["planesLimit"][1], math.radians(settings["planes"]), places=5)
        self.assertEqual((joint["planeAxis"], joint["friction"]), ([1, 0, 0], settings["friction"] * settings["mass"]))

    def test_strand_points_run_from_the_root(self):
        line = np.array([[0, 0, z] for z in np.linspace(1.5, 0.5, 41)]) + np.random.default_rng(1).normal(0, 0.002, (41, 3))
        points = chains.strand_points(line[::-1], np.array([0, 0, 1.6]), 4)
        self.assertEqual(points.shape, (5, 3))
        self.assertGreater(points[0, 2], points[-1, 2])
        steps = np.linalg.norm(np.diff(points, axis=0), axis=1)
        self.assertLess(steps.max() - steps.min(), 0.02)

    def test_collider_fits(self):
        angles = np.linspace(0, 2 * np.pi, 24, endpoint=False)
        ring = np.array([[0.08 * math.cos(a), 0.08 * math.sin(a), z] for z in np.linspace(0, 0.4, 9) for a in angles])
        self.assertAlmostEqual(chains.segment_radius(ring, np.array([0, 0, 0.0]), np.array([0, 0, 0.4])), 0.08, places=4)
        self.assertIsNone(chains.segment_radius(ring, np.array([1, 0, 0.0]), np.array([1, 0, 0.0])))
        body = np.array([[x, y, z] for x in np.linspace(-0.2, 0.2, 9) for y in np.linspace(-0.15, 0.15, 7) for z in (-0.02, 0.02)])
        a, b, radius = chains.flat_capsule(body)
        self.assertLessEqual(radius, 0.12)
        self.assertGreater(abs((b - a)[0]) + abs((b - a)[1]), 0)

    def test_mmd_mapping(self):
        self.assertAlmostEqual(chains.mmd_damping(0.5), math.log(2), places=6)
        self.assertLess(chains.mmd_damping(1.0), 7)
        # A bone along world -Z, a joint frame equal to the world: the swing axes are x and y.
        cone, planes, axis = chains.mmd_limits([0, 0, -1], np.eye(3), [-1.0, 1.0, -0.1, 0.2, -0.05, 0.05])
        self.assertAlmostEqual(cone, 1.0)
        self.assertEqual(planes, (-0.1, 0.2))
        np.testing.assert_allclose(axis, np.cross([0, 1, 0], [0, 0, -1]))
        self.assertEqual(chains.mmd_limits([0, 0, -1], np.eye(3), [0] * 6), (0.0, None, None))
        self.assertEqual(chains.mmd_limits([0, 0, -1], np.eye(3), [-0.5, 0.5, -0.45, 0.45, 0, 0])[1:], (None, None))

    def test_names(self):
        self.assertEqual(chains.chain_name("髪 left/1"), "left_1")
        self.assertEqual(chains.chain_name("tail", {"tail", "tail_2"}), "tail_3")
        self.assertEqual(chains.chain_name("???"), "chain")


if __name__ == "__main__":
    unittest.main()
