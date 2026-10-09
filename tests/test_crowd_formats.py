import importlib.util
from pathlib import Path
import struct
import unittest


SPEC = importlib.util.spec_from_file_location("prepare_stage", Path(__file__).resolve().parents[1] / "prepare-stage.py")
STAGE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(STAGE)


def crowd_fixture():
    quads = 25
    base, header, vertices = 384, 528, 688
    indices = vertices + quads * 4 * 36
    data = bytearray(indices + quads * 6 * 2)
    struct.pack_into(">H", data, 34, 1)
    struct.pack_into(">I", data, 36, 272)
    struct.pack_into(">I", data, 48, 123)
    struct.pack_into(">I", data, 48 + 160, 0xA17D40ED)
    struct.pack_into(">II", data, 48 + 184, 0, 256)
    struct.pack_into(">I", data, 48 + 196, 256)
    struct.pack_into(">I", data, base + 76, 1)
    struct.pack_into(">I", data, base + 128, header - base)
    struct.pack_into(">I", data, header + 20, 123)
    struct.pack_into(">I", data, header + 28, 0x10010)
    struct.pack_into(">HHI", data, header + 36, quads * 6, quads * 4, 4)
    struct.pack_into(">I", data, header + 64, vertices - base - 32)
    data[header + 90] = 36
    struct.pack_into(">I", data, header + 92, indices - base - 32)
    for person in range(quads):
        for corner, (u, v) in enumerate([(0, 0), (0, 1), (1, 0), (1, 1)]):
            struct.pack_into(">6f4B2f", data, vertices + (person * 4 + corner) * 36,
                             person + u, 1 - v, 0, 0, 0, -1, 255, 255, 255, 255, u, v)
        struct.pack_into(">6H", data, indices + person * 12, *(person * 4 + i for i in [0, 1, 2, 2, 1, 3]))
    return data, indices


class CrowdFormatTests(unittest.TestCase):
    def test_atlas_rows_and_wrap_preserve_complete_people(self):
        _, meshes = STAGE.scene(crowd_fixture()[0])
        vertices = meshes[0][2]
        for person, left, top in [(0, 0, 1), (7, 7 / 8, 1), (8, 0, 2 / 3), (23, 7 / 8, 1 / 3), (24, 0, 1)]:
            uv = [struct.unpack_from("<2f", vertices, (person * 4 + i) * STAGE.STATIC_STRIDE + 24) for i in range(4)]
            for actual, expected in zip(uv, [(left, top), (left, top - 1 / 3), (left + 1 / 8, top), (left + 1 / 8, top - 1 / 3)]):
                self.assertAlmostEqual(actual[0], expected[0], places=6)
                self.assertAlmostEqual(actual[1], expected[1], places=6)

    def test_faces_cannot_join_different_crowd_cells(self):
        data, indices = crowd_fixture()
        struct.pack_into(">H", data, indices + 4, 6)
        with self.assertRaisesRegex(ValueError, "independent crowd"):
            STAGE.scene(data)


if __name__ == "__main__":
    unittest.main()
