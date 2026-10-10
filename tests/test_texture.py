import hashlib
import struct
import unittest

import numpy as np

from pipeline.package import texture


def pattern(width, height, alpha=False, seed=7):
    rng = np.random.default_rng(seed)
    y, x = np.mgrid[0:height, 0:width]
    rgba = np.zeros((height, width, 4), dtype=np.uint8)
    line = (x + y) * 2
    rgba[..., 0] = np.clip(line + rng.integers(0, 6, (height, width)), 0, 255)
    rgba[..., 1] = np.clip(line // 2 + 30, 0, 255)
    rgba[..., 2] = np.clip(220 - line, 0, 255)
    rgba[..., 3] = np.where(x < width // 2, 255, 64) if alpha else 255
    return rgba


class TextureTests(unittest.TestCase):
    def test_formats_and_mips(self):
        bc1 = texture.encode(pattern(64, 32))
        fmt, width, height, mips, flags = texture.read_header(bc1)
        self.assertEqual((fmt, width, height, mips, flags), (texture.BC1, 64, 32, 7, 0))
        self.assertEqual(len(bc1), texture.HEADER_SIZE + sum(texture.level_size(fmt, w, h) for w, h in texture.levels(64, 32, 7)))
        self.assertEqual(texture.read_header(texture.encode(pattern(64, 32, alpha=True)))[0], texture.BC3)
        self.assertEqual(texture.read_header(texture.encode(pattern(6, 5)))[0], texture.RGBA32)
        self.assertEqual(texture.read_header(texture.encode(pattern(8, 8), linear=True))[4], texture.FLAG_LINEAR)
        self.assertEqual(texture.problems(bc1), [])

    def test_quality_limit_keeps_rgba32(self):
        # Random colours in every pixel: BC1 cannot keep them above 36 dB.
        noise = np.random.default_rng(1).integers(0, 256, (16, 16, 4), dtype=np.uint8)
        noise[..., 3] = 255
        data = texture.encode(noise)
        self.assertEqual(texture.read_header(data)[0], texture.RGBA32)
        self.assertTrue(np.array_equal(texture.decode(data), noise))

    def test_decode_quality(self):
        source = pattern(64, 64, alpha=True)
        source[..., 2] = 128
        data = texture.encode(source)
        self.assertGreater(texture.psnr(texture.decode(data), source), 36)
        self.assertEqual(texture.decode(data, 6).shape, (1, 1, 4))

    def test_encoder_output_is_stable(self):
        # The encoder gives the bytes of the C# importer encoder of 2026-10-10 (checked on all 1,536 GH3 package textures).
        digest = hashlib.sha256(texture.encode(pattern(64, 32)) + texture.encode(pattern(32, 32, alpha=True))).hexdigest()
        self.assertEqual(digest, STABLE_DIGEST)

    def test_rules(self):
        data = bytearray(texture.encode(pattern(8, 8)))
        self.assertIn("multiples of 4", texture.problems(bytes(data[:12] + struct.pack("<i", 6) + data[16:]))[0])
        self.assertIn("header gives", texture.problems(bytes(data[:-1]))[0])
        self.assertIn("unknown texture flags", texture.problems(bytes(data[:24] + struct.pack("<i", 4) + data[28:]))[0])
        self.assertIn("not a GH3TEX02", texture.problems(b"\x89PNG\r\n\x1a\n" + bytes(40))[0])
        punch = bytearray(data)
        punch[texture.HEADER_SIZE:texture.HEADER_SIZE + 8] = bytes([0, 0, 255, 255, 255, 255, 255, 255])
        self.assertIn("must use BC3", texture.problems(bytes(punch))[0])

    def test_glb_round_trip(self):
        from tests.test_package_format import GlbBuilder
        glb = GlbBuilder()
        glb.image("a", texture.png_bytes(pattern(16, 16)), "image/png")
        glb.image("n", texture.png_bytes(pattern(8, 8, seed=3)), "image/png")
        glb.json["materials"].append({"name": "m", "normalTexture": {"index": 1}})
        png = glb.bytes()
        encoded = texture.encode_glb(png, texture.encode_pngs)
        gltf, _ = texture.split_glb(encoded)
        self.assertEqual([i["mimeType"] for i in gltf["images"]], [texture.MIME_TYPE] * 2)
        self.assertEqual(gltf["extensionsRequired"], [texture.EXTENSION])
        rows = texture.glb_textures(encoded)
        self.assertEqual([texture.read_header(data)[4] for _, _, data in rows], [0, texture.FLAG_LINEAR])
        opened = texture.decode_glb(encoded)
        self.assertNotIn("extensionsRequired", texture.split_glb(opened)[0])
        self.assertEqual(texture.encode_glb(opened, texture.encode_pngs), encoded)
        source = texture.glb_textures(encoded)[0][2]
        self.assertEqual(texture.unchanged_source(texture.png_bytes(texture.decode(source), source)), source)
        self.assertIsNone(texture.unchanged_source(texture.png_bytes(texture.decode(source), source), linear=True))
        changed = texture.png_bytes(pattern(16, 16, seed=9), texture.glb_textures(encoded)[0][2])
        self.assertIsNone(texture.unchanged_source(changed))
        self.assertNotEqual(texture.encode_png_image(changed, texture.png_rgba(changed)), texture.glb_textures(encoded)[0][2])


STABLE_DIGEST = "896a8491e56a1017b51c0ddbc0ca418f7881aa5a94a8025feaf701292c0f426e"


if __name__ == "__main__":
    unittest.main()
