import os
from pathlib import Path
import struct
import unittest

from gh3_formats import key
from gh3_direction import Source
from gh3_havok import Ragdoll


class HairFormatTests(unittest.TestCase):
    def test_truncated_and_foreign_packfiles_are_rejected(self):
        for data in (b"", bytes(207), bytes(500)):
            with self.assertRaises(ValueError):
                Ragdoll(data)

    @unittest.skipUnless(os.environ.get("GH3_HAIR_SOURCE"), "requires original GH3 data")
    def test_original_ragdoll_reflection_and_bounds(self):
        entries = Source(Path(os.environ["GH3_HAIR_SOURCE"])).load("ZONES/global.pak.xen")
        data = next(b for k, n, b in entries if k == key(".rag") and n == 0x8efb7275)
        rag = Ragdoll(data)
        bodies = {rag.field(p, c, "name"): p for p, c in rag.types.items() if c == "hkRigidBody"}
        self.assertEqual(len([n for n in bodies if n.startswith("Bone_ACC_")]), 15)
        motion, cls = rag.field(bodies["Bone_ACC_01"], "hkRigidBody", "motion")
        self.assertAlmostEqual(rag.field(motion, cls, "inertiaAndMassInv")[3], 0.2)
        state, cls = rag.field(motion, cls, "motionState")
        self.assertAlmostEqual(rag.field(state, cls, "angularDamping"), 0.05)
        with self.assertRaises(ValueError):
            Ragdoll(data[:-1])
        corrupted = bytearray(data)
        struct.pack_into("<I", corrupted, 84, len(data) + 1)
        with self.assertRaises(ValueError):
            Ragdoll(corrupted)


if __name__ == "__main__":
    unittest.main()
