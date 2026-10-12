"""The GH3 rules that the pipeline reads from the GH3 scripts instead of tables in the code (PACKAGING-PLAN decision 16)."""

import os
import unittest
from pathlib import Path

from pipeline.gh3.formats import key
from pipeline.paths import GH3_DATA

DATA = Path(os.environ.get("GH3_DATA", GH3_DATA))


@unittest.skipUnless((DATA / "PAK/qb.pak.xen").is_file(), "needs the GH3 PC data")
class Gh3RuleTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        from pipeline.gh3.data import Source

        cls.scripts = Source(DATA).scripts()

    def test_small_venues(self):
        from pipeline.gh3.levels import small_venue_zones

        self.assertEqual(
            small_venue_zones(self.scripts), {key(f"z_{name}") for name in ("party", "dive", "video", "prison", "hell")}
        )

    def test_venue_titles(self):
        from pipeline.gh3.levels import level_titles

        titles = level_titles(self.scripts)
        self.assertEqual((titles["z_dive"], titles["z_wikker"]), ("Mitch's Moose Lounge", "Desert Rock Festival"))

    def test_pc_port_exclusion(self):
        from pipeline.character_library import pc_port_without_accessories

        self.assertEqual(pc_port_without_accessories(self.scripts), {key("Ripper")})

    def test_rig_data(self):
        from pipeline.gh3.data import script
        from pipeline.library import rigs

        tree = script(self.scripts, "guitar_anim_tree")
        hero = rigs.ik_chains(tree, "Hero_Ik_params")
        drummer = rigs.ik_chains(tree, "drummer_IK_params")
        self.assertEqual([c.get("hand") for c in hero], ["right", "left", None, None])
        self.assertEqual(hero[0]["target"], f"{key('Bone_IK_Hand_Guitar_R'):08x}")
        self.assertEqual(drummer[1]["target"], f"{key('Bone_IK_Hand_Slave_L'):08x}")
        self.assertEqual({c["type"] for c in rigs.constraints(tree)}, {"Split", "SplitR", "Twist", "TwistChild"})


if __name__ == "__main__":
    unittest.main()
