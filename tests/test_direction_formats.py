import struct
import unittest

from pipeline.gh3.direction import camera_point, performance_command
from pipeline.gh3.formats import key, qb_array, qb_struct


class DirectionFormatTests(unittest.TestCase):
    def test_particle_size_pairs_and_path_vectors(self):
        pairs = struct.pack(">6I2fI2f", 0x10500, 2, 12, 20, 32, 0x10000, 0.2, 0.2, 0x10000, 0, 0)
        self.assertAlmostEqual(qb_array(pairs, 0)[0][0], 0.2, places=6)
        self.assertEqual(qb_array(pairs, 0)[1], (0, 0))
        vector = struct.pack(">4I3f", 0x10600, 1, 12, 0x10000, 1, 2, 3)
        self.assertEqual(qb_array(vector, 0), [(1, 2, 3)])
        with self.assertRaises(ValueError):
            qb_array(vector[:-1], 0)

    def test_light_node_float_array(self):
        data = struct.pack(">3I4f", 0x10200, 4, 12, 0.5, -0.5, 0.5, 0.5)
        self.assertEqual(qb_array(data, 0), [0.5, -0.5, 0.5, 0.5])
        with self.assertRaises(ValueError):
            qb_array(data[:-1], 0)

    def test_camera_screen_offset_pair_is_read_without_vector_padding(self):
        data = struct.pack(">6I3f", 0x100, 8, 0x00850000, key("ScreenOffset"), 24, 0, 0, 0.4, -0.2)
        offset = qb_struct(data, 0)[key("ScreenOffset")]
        self.assertAlmostEqual(offset[0], 0.4, places=6)
        self.assertAlmostEqual(offset[1], -0.2, places=6)

    def test_camera_reflection_preserves_forward_and_lens(self):
        point = camera_point({key("Pos"): [1, 2, 3], key("Quat"): [0, 1, 0], key("FOV"): 72})
        self.assertEqual(point["position"], [1, 2, -3])
        self.assertEqual(point["rotation"], [0, 0, 0, 1])
        self.assertEqual(point["fieldOfView"], 72)

    def test_facial_cue_preserves_original_clip_key_and_time(self):
        event = performance_command({key("time"): 44764, key("scr"): key("Band_PlayFacialAnim"),
            key("params"): {key("Name"): key("vocalist"), key("anim"): 0x12345678}})
        self.assertEqual((event["time"], event["role"], event["type"], event["value"]),
                         (44.764, "singer", "face", "12345678"))

    def test_original_queue_and_cycle_flags_survive_export(self):
        event = performance_command({key("time"): 42097, key("scr"): key("Band_PlayAnim"),
            key("params"): {key("anim"): key("special"), 0: [key("no_wait"), key("cycle")]}})
        self.assertEqual(event["value"], "special")
        self.assertTrue(event["immediate"])
        self.assertTrue(event["loop"])


if __name__ == "__main__":
    unittest.main()
