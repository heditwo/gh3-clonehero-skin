import importlib.util
from pathlib import Path
import unittest

from gh3_formats import key


SPEC = importlib.util.spec_from_file_location("prepare_fixtures", Path(__file__).resolve().parents[1] / "prepare-fixtures.py")
FIXTURES = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(FIXTURES)


class FixtureFormatTests(unittest.TestCase):
    def test_only_created_physical_housings_are_loaded(self):
        nodes = [{key("Name"): i, key("Type"): key(model), key("Pos"): [0, 0, 0], 0: flags}
                 for i, model, flags in [(1, "GO_LightHousing01", [key("CreatedAtStart")]),
                     (2, "GO_NoHousing01", [key("CreatedAtStart")]), (3, "GO_LightHousing01", [])]]
        snapshot = [{key("HousingChecksum"): 1, key("Pos"): [1, 2, 3], key("Quat"): [0, 0, 0]}]
        result = FIXTURES.instances(nodes, snapshot)
        self.assertEqual(len(result), 1)
        self.assertEqual(result[0]["position"], [1, 2, -3])
        self.assertAlmostEqual(sum(v * v for v in result[0]["rotation"]), 1)
        self.assertFalse(result[0]["fixedPosition"])

    def test_fixed_housings_keep_node_position_and_snapshot_rotation(self):
        node = {key("Name"): 1, key("Type"): key("GO_TankHousing01"), key("Pos"): [2, 3, 4],
                0: [key("CreatedAtStart"), key("IgnoreSnapshotPositions")]}
        snapshot = [{key("HousingChecksum"): 1, key("Pos"): [0, 0, 0], key("Quat"): [0, 0, 0]}]
        result = FIXTURES.instances([node], snapshot)[0]
        self.assertEqual(result["position"], [2, 3, -4])
        self.assertTrue(result["fixedPosition"])
        self.assertEqual(result["rotation"], FIXTURES.lighting.beam_rotation([0, 0, 0]))


if __name__ == "__main__":
    unittest.main()
