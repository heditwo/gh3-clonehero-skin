import struct
import unittest

import numpy as np
from gh3_band_formats import animation, quaternion, rotation, sample_animation, sample_track


def clip_fixture():
    data = bytearray(192)
    struct.pack_into(">IIfHH", data, 32, 0x48, 0x800000, 1, 1, 2)
    struct.pack_into(">4I", data, 88, 128, 144, 188, 190)
    struct.pack_into(">H3hH3h", data, 128, 0x8000, 0, 0, 0, 60, 0, 0, 11585)
    struct.pack_into(">BHB6f", data, 144, 0x40, 0, 0, 0, 0, 0, 0.2, 0.3, 0.4)
    struct.pack_into(">BHB3f", data, 172, 0x7C, 0, 0, 1, 2, 3)
    struct.pack_into(">2H", data, 188, 16, 44)
    return data


class BandFormatTests(unittest.TestCase):
    def test_translation_deltas_and_quaternion_sign(self):
        clip = animation(clip_fixture(), 1)
        self.assertEqual(clip[1][0][0][1][3], -1)
        p, q = sample_animation(clip, np.array([[1, 2, 3]]), np.array([[0, 0, 0, 1]]), [0, 1])
        np.testing.assert_allclose(p[:, 0], [[1.2, 2.3, 3.4], [2, 4, 6]], atol=1e-6)
        np.testing.assert_allclose(np.linalg.norm(q, axis=2), 1)

    def test_partial_single_hand_pose_does_not_replace_body(self):
        data = bytearray(212)
        struct.pack_into(">I", data, 24, 192)
        struct.pack_into(">IIfHH", data, 32, 0x48, 0x80240, 0, 2, 2)
        struct.pack_into(">4I", data, 88, 128, 160, 0, 0)
        struct.pack_into(">4f", data, 144, 0, 0, 0, 1)
        struct.pack_into(">4f", data, 176, 0.25, 0, 0, 1)
        struct.pack_into(">5I", data, 192, 2, 2, 0, 0, 0)
        clip = animation(data, 2)
        self.assertEqual(list(clip[1]), [1])
        p, _ = sample_animation(clip, np.array([[1, 0, 0], [2, 0, 0]]), np.array([[0, 0, 0, 1]] * 2), [0])
        np.testing.assert_allclose(p[0], [[1, 0, 0], [2.25, 0, 0]])

    def test_rotation_conversion_round_trip(self):
        q = np.array([0.2, -0.4, 0.5, 0.7])
        np.testing.assert_allclose(rotation(quaternion(rotation(q))), rotation(q), atol=1e-8)

    def test_antipodal_quaternions_interpolate_without_zero(self):
        result = sample_track([(0, [0, 0, 0, 1]), (1, [0, 0, 0, -1])], [0.5], True)
        np.testing.assert_allclose(result, [[0, 0, 0, 1]])

    def test_truncated_track_rejected(self):
        with self.assertRaises(ValueError):
            animation(clip_fixture()[:-1], 1)

    def test_wrong_skeleton_and_bad_key_time_rejected(self):
        with self.assertRaises(ValueError):
            animation(clip_fixture(), 2)
        data = clip_fixture()
        struct.pack_into(">H", data, 136, 120)
        with self.assertRaisesRegex(ValueError, "key times"):
            animation(data, 1)


if __name__ == "__main__":
    unittest.main()
