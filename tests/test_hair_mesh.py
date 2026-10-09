import copy
import struct
import tempfile
import unittest
from pathlib import Path

from PIL import Image

from pipeline.gh3.hair_mesh import combine_hair


class HairMeshTests(unittest.TestCase):
    def mesh(self, material, uv):
        data = b"".join(struct.pack("<8f4B4B4f", i, 1, 2, 0, 1, 0, u, v,
                                    60, 70, 80, 255, 3, 7, 0, 0, 0.75, 0.25, 0, 0)
                        for i, (u, v) in enumerate(uv))
        return material, len(uv), data, [(0, 1, 2)]

    def material(self, texture):
        return {"texture": texture, "template": "6f3f03f3", "blend": 3, "cutoff": 16 / 255}

    def test_shared_texture_merges_without_changing_vertices(self):
        materials = {"a": self.material("11111111"), "b": self.material("11111111"),
                     "body": {"template": "other", "blend": 0}}
        a = self.mesh("a", [(0, 0), (1, 0), (0, 1)])
        b = self.mesh("b", [(-0.1, 0), (0.2, 0), (0, 1)])
        body = self.mesh("body", [(0, 0), (1, 0), (0, 1)])
        result = combine_hair([a, body, b], materials, ".")
        self.assertIs(result[0], body)
        self.assertEqual(result[1][1:], (6, a[2] + b[2], [(0, 1, 2), (3, 4, 5)]))
        self.assertTrue(materials["a"]["sortedTransparency"])

    def test_atlas_preserves_wrapped_texture_samples_and_skin_weights(self):
        with tempfile.TemporaryDirectory() as folder:
            materials = {"a": self.material("11111111"), "b": self.material("22222222")}
            original = copy.deepcopy(materials)
            images = {}
            for i, name in enumerate(["11111111", "22222222"]):
                image = Image.new("RGBA", (8, 16))
                image.putdata([(x * 20, y * 10, i * 200, 20 + x * y) for y in range(16) for x in range(8)])
                image.save(Path(folder) / f"{name}.png")
                images[name] = image
            meshes = [self.mesh("a", [(-0.3, -0.2), (2.3, 1.2), (0.4, 0.6)]),
                      self.mesh("b", [(0.15, 0.11), (0.85, 0.27), (0.18, 1.12)])]
            result = combine_hair(meshes, materials, folder)[0]
            with Image.open(Path(folder) / f'{materials["a"]["texture"]}.png') as atlas:
                offset = 0
                for key, count, data, _ in meshes:
                    image = images[original[key]["texture"]]
                    for i in range(count):
                        source = data[i * 56:(i + 1) * 56]
                        target = result[2][offset:offset + 56]
                        self.assertEqual(source[:24] + source[32:], target[:24] + target[32:])
                        u, v = struct.unpack_from("<2f", source, 24)
                        au, av = struct.unpack_from("<2f", target, 24)
                        expected = image.getpixel((int((u % 1) * image.width), int(((1 - v) % 1) * image.height)))
                        self.assertEqual(expected, atlas.getpixel((int(au * atlas.width), int((1 - av) * atlas.height))))
                        offset += 56


if __name__ == "__main__":
    unittest.main()
