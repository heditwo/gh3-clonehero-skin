import math
import struct
import tempfile
import unittest
from pathlib import Path

from PIL import Image

from pipeline.gh3.mesh_cutout import trim_mesh, trim_prop_cutouts


def square(skinned=False, uv_offset=0):
    vertices = bytearray()
    for x, y in [(0, 0), (1, 0), (1, 1), (0, 1)]:
        vertices.extend(struct.pack("<8f4B", x, y, 0, 0, 0, 1, x + uv_offset, 1 - y, 255, 128, 64, 255))
        if skinned:
            vertices.extend(struct.pack("<4B4f", 11, 12, 0, 0, 1 - x, x, 0, 0))
    return "drum", 4, vertices, [(0, 1, 2), (0, 2, 3)]


def triangles(mesh):
    stride = len(mesh[2]) // mesh[1]
    return [[struct.unpack_from("<8f4B", mesh[2], i * stride) for i in face] for face in mesh[3]]


def area(mesh):
    return sum(((b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])) / 2 for a, b, c in triangles(mesh))


class MeshCutoutTests(unittest.TestCase):
    def test_prop_cutout_preserves_blended_materials(self):
        with tempfile.TemporaryDirectory() as folder:
            image = Image.new("RGBA", (2, 1), (255, 255, 255, 0))
            image.putpixel((1, 0), (255, 255, 255, 255))
            image.save(Path(folder) / "12345678.png")
            opaque = square(skinned=True)
            blended = ("hair", *opaque[1:])
            materials = {
                "drum": {"template": "f4e6db51", "blend": 0, "cutoff": 0.25, "texture": "12345678"},
                "hair": {"template": "6f3f03f3", "blend": 3, "cutoff": 0.25, "texture": "12345678"},
            }
            result = trim_prop_cutouts([opaque, blended], materials, folder)
            self.assertAlmostEqual(area(result[0]), 0.5)
            self.assertEqual(result[1], blended)
            self.assertTrue(materials["drum"]["geometryCutout"])
            self.assertNotIn("geometryCutout", materials["hair"])

    def test_mask_hole_preserves_winding_and_opaque_area(self):
        image = Image.new("RGBA", (4, 4), (255, 128, 0, 255))
        for x in (1, 2):
            for y in (1, 2):
                image.putpixel((x, y), (255, 128, 0, 0))
        mesh = trim_mesh(square(), image, 0.25)
        self.assertAlmostEqual(area(mesh), 0.75)
        for a, b, c in triangles(mesh):
            self.assertGreater((b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]), 0)
            x, y = sum(v[0] for v in (a, b, c)) / 3, sum(v[1] for v in (a, b, c)) / 3
            self.assertFalse(0.25 < x < 0.75 and 0.25 < y < 0.75)

    def test_cutoff_and_repeated_texture_coordinates(self):
        image = Image.new("RGBA", (2, 1), (255, 128, 0, 63))
        image.putpixel((1, 0), (255, 128, 0, 64))
        for offset in (-1, 0, 1):
            mesh = trim_mesh(square(uv_offset=offset), image, 64 / 255)
            self.assertAlmostEqual(area(mesh), 0.5)
            for triangle in triangles(mesh):
                self.assertTrue(all(v[0] >= 0.5 for v in triangle))

    def test_new_vertices_keep_skin_weights_normals_and_uvs(self):
        image = Image.new("RGBA", (2, 1), (255, 128, 0, 0))
        image.putpixel((1, 0), (255, 128, 0, 255))
        mesh = trim_mesh(square(skinned=True), image, 0.25)
        self.assertAlmostEqual(area(mesh), 0.5)
        for i in {i for face in mesh[3] for i in face}:
            values = struct.unpack_from("<8f4B", mesh[2], i * 56)
            ids = struct.unpack_from("<4B", mesh[2], i * 56 + 36)
            weights = struct.unpack_from("<4f", mesh[2], i * 56 + 40)
            bone_weights = {bone: sum(w for b, w in zip(ids, weights) if b == bone) for bone in (11, 12)}
            self.assertAlmostEqual(bone_weights[11], 1 - values[0])
            self.assertAlmostEqual(bone_weights[12], values[0])
            self.assertAlmostEqual(math.sqrt(sum(v * v for v in values[3:6])), 1)
            self.assertAlmostEqual(values[6], values[0])
            self.assertAlmostEqual(values[7], 1 - values[1])
            self.assertEqual(values[8:], (255, 128, 64, 255))

    def test_opaque_and_fully_transparent_faces(self):
        image = Image.new("RGBA", (2, 2), (255, 128, 0, 255))
        original = square()
        self.assertEqual(trim_mesh(original, image, 0.25), original)
        image.putalpha(0)
        self.assertEqual(trim_mesh(original, image, 0.25)[3], [])


if __name__ == "__main__":
    unittest.main()
