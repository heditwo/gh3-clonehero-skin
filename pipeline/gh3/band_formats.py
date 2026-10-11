"""Bounded readers for the owner's GH3 PC skeletons (.ske) and animation clips (.ska), and the guitar arm solver.

The layouts were found by reading the GH3 PC files (no public specification); src/Native and the C# clip reader of the
mod read the same data.
"""

import math

import numpy as np

from pipeline.gh3.formats import values, word

# Skeleton (.ske): bone count (+4), then a table (+16) of eight offsets: names, parents, three unused, inverse bind
# matrices (64 bytes each), local positions and rotations (16 bytes each).
SKELETON_BONE_COUNT, SKELETON_TABLE = 4, 16
MAX_SKELETON_BONES = 255
BIND_TOLERANCE = 0.0001

# Clip (.ska): word 0 is 0 and word 32 is 0x48; flags (+36), duration in seconds (+40), bone count (u16 at +44),
# partial-clip mask offset (+24), and the four track offsets (+88): rotation keys, translation keys, and the byte size of
# each bone's rotation and translation track.
CLIP_HEADER_TAG = 0x48
CLIP_TAG, CLIP_FLAGS, CLIP_DURATION, CLIP_BONE_COUNT = 32, 36, 40, 44
CLIP_PARTIAL_MASKS, CLIP_TRACKS = 24, 88
MAX_CLIP_SECONDS = 600
CLIP_FLAG_ONE_POSE = 0x40
CLIP_FLAG_RELATIVE_TRANSLATIONS = 0x200
CLIP_FLAG_PARTIAL = 0x80000
CLIP_FLAG_COMPRESSED_ROTATIONS = 0x800000
CLIP_FLAG_ROTATION_TIME_WORD = 0x100
CLIP_FLAG_FLOAT_TRANSLATION_TIMES = 0x80000000
# Rotation keys: a 16-bit header (time in the low 11 bits unless the time has its own word), then x, y, z as int16 / 16384;
# w comes from the unit length, its sign from bit 15. With bit 14, the axes whose bit (13, 12, 11) is set are one byte;
# without any of these bits, two bytes give x and y (z = y).
KEY_TIME_MASK = 0x7FF
KEY_SMALL = 0x4000
KEY_SMALL_AXES = 0x3800
KEY_BYTE_AXES = (0x2000, 0x1000, 0x800)
KEY_W_NEGATIVE = 0x8000
QUATERNION_SCALE = 16384.0
# An uncompressed rotation key with this time marks 8 bytes of padding in the track.
PADDING_KEY = 0xDEAD
PADDING_SIZE = 8
# Translation keys: a byte time (minus 0x40) or a u16 time; the first key of the clip has 12 zero bytes after its time.
TRANSLATION_SHORT_TIME_BASE = 0x40
KEY_RATE = 60
KEY_TIME_TOLERANCE = 0.02

# Guitar arms (GH3 guitarist skeleton): upper arm, forearm, hand, hand target bone, and the elbow side (+1 right).
GUITAR_ARMS = [(11, 12, 13, 122, 1), (15, 16, 17, 124, -1)]
# Elbow pole direction before it is made perpendicular to the shoulder-to-target line (tuned; no GH3 source).
ELBOW_POLE_SIDE, ELBOW_POLE_DOWN, ELBOW_POLE_FORWARD = 0.5, -1, 0.1
REACH_MARGIN = 0.0001


def rotation(q):
    x, y, z, w = q / np.linalg.norm(q)
    return np.array(
        [
            [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
            [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
            [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
        ]
    )


def skeleton(data):
    """(bone name checksums, parent indices, local positions, local rotations [x, y, z, w]) of a .ske file."""
    count = word(data, SKELETON_BONE_COUNT)
    if not 1 <= count <= MAX_SKELETON_BONES:
        raise ValueError("Invalid skeleton size")
    names_at, parents_at, _, _, _, matrices_at, positions_at, rotations_at = values(data, SKELETON_TABLE, "8I")
    names = values(data, names_at, f"{count}I")
    parents = [names.index(parent) if parent in names else -1 for parent in values(data, parents_at, f"{count}I")]
    positions = np.array([values(data, positions_at + i * 16, "4f")[:3] for i in range(count)])
    rotations = np.array([values(data, rotations_at + i * 16, "4f") for i in range(count)])
    bind = (
        np.array([values(data, matrices_at + i * 64, "16f") for i in range(count)])
        .reshape(count, 4, 4)
        .transpose(0, 2, 1)
    )
    if not all(np.isfinite(array).all() for array in (positions, rotations, bind)):
        raise ValueError("Non-finite skeleton")
    world, visiting = {}, set()

    def matrix(i):
        if i in visiting:
            raise ValueError("Skeleton parent cycle")
        if i not in world:
            visiting.add(i)
            local = np.eye(4)
            local[:3, :3] = rotation(rotations[i] * [-1, -1, -1, 1])
            local[:3, 3] = positions[i]
            world[i] = matrix(parents[i]) @ local if parents[i] >= 0 else local
            visiting.remove(i)
        return world[i]

    for i in range(count):
        if not np.allclose(matrix(i) @ bind[i], np.eye(4), atol=BIND_TOLERANCE):
            raise ValueError(f"Skeleton bind pose mismatch at bone {i}")
    return names, parents, positions, rotations


def clip_bone_count(data):
    """The bone count of a .ska clip header (clips play only on skeletons with this many bones)."""
    return values(data, CLIP_BONE_COUNT, "H")[0]


def remap_animation(clip, source_names, target_names):
    source = {name: i for i, name in enumerate(source_names)}
    if len(source) != len(source_names) or len(set(target_names)) != len(target_names):
        raise ValueError("Duplicate animation bone names")
    indices = {source[name]: i for i, name in enumerate(target_names) if name in source}
    if not indices:
        raise ValueError("Animation and actor do not share any bones")
    return (
        clip[0],
        {indices[i]: frames for i, frames in clip[1].items() if i in indices},
        {indices[i]: frames for i, frames in clip[2].items() if i in indices},
        clip[3],
    )


def animation(data, expected_bones):
    """(duration, {bone: [(time, quaternion)]}, {bone: [(time, position)]}, absolute translations) of a .ska clip."""
    if word(data, 0) != 0 or word(data, CLIP_TAG) != CLIP_HEADER_TAG:
        raise ValueError("Expected a GH3 SKA clip")
    flags = word(data, CLIP_FLAGS)
    duration = values(data, CLIP_DURATION, "f")[0]
    count = values(data, CLIP_BONE_COUNT, "H")[0]
    if count != expected_bones or not math.isfinite(duration) or not 0 <= duration <= MAX_CLIP_SECONDS:
        raise ValueError("Invalid animation size or duration")
    q, t, qs, ts = values(data, CLIP_TRACKS, "4I")
    if flags & CLIP_FLAG_ONE_POSE:
        rotations, positions = _one_pose(data, flags, count, q, t)
        return duration, rotations, positions, not bool(flags & CLIP_FLAG_RELATIVE_TRANSLATIONS)
    rotations = _rotation_tracks(data, flags, values(data, qs, f"{count}H"), q)
    positions = _translation_tracks(data, flags, values(data, ts, f"{count}H"), t)
    for tracks in (rotations, positions):
        for frames in tracks.values():
            times = [frame[0] for frame in frames]
            if times != sorted(times) or min(times) < 0 or max(times) > duration + KEY_TIME_TOLERANCE:
                raise ValueError("Invalid animation key times")
            if not all(math.isfinite(v) for _, row in frames for v in row):
                raise ValueError("Non-finite animation")
    return duration, rotations, positions, False


def _one_pose(data, flags, count, q, t):
    """The single pose of a one-pose clip, for all bones or for the bones of its partial mask."""
    rotations, positions = {}, {}
    active = set(range(count))
    if flags & CLIP_FLAG_PARTIAL:
        partial = word(data, CLIP_PARTIAL_MASKS)
        masks = values(data, partial + 4, "4I")
        active = {i for i in range(count) if masks[i // 32] & (1 << (i % 32))}
    for i in range(count):
        if i not in active:
            continue
        rotations[i] = [(0, values(data, q + i * 16, "4f"))]
        positions[i] = [(0, values(data, t + i * 16, "4f")[:3])]
    return rotations, positions


def _rotation_key(data, flags, q):
    """(time in keys, x, y, z as int16 units, header bits, offset after the key); time None for a padding key."""
    encoded = values(data, q, "H")[0]
    q += 2
    if not flags & CLIP_FLAG_COMPRESSED_ROTATIONS:
        xyz = values(data, q, "3h")
        return (None if encoded == PADDING_KEY else encoded), xyz, 0, q + 6
    if flags & CLIP_FLAG_ROTATION_TIME_WORD:
        time = encoded
        encoded = values(data, q, "H")[0]
        q += 2
    else:
        time = encoded & KEY_TIME_MASK
    if not encoded & KEY_SMALL:
        return time, values(data, q, "3h"), encoded, q + 6
    if not encoded & KEY_SMALL_AXES:
        x, y = values(data, q, "2B")
        return time, [x, y, y], encoded, q + 2
    xyz = []
    for flag in KEY_BYTE_AXES:
        if encoded & flag:
            xyz.append(values(data, q, "B")[0])
            q += 1
        else:
            q = (q + 1) // 2 * 2
            xyz.append(values(data, q, "h")[0])
            q += 2
    return time, xyz, encoded, (q + 1) // 2 * 2


def _rotation_tracks(data, flags, sizes, q):
    rotations = {}
    for bone, size in enumerate(sizes):
        end = q + size
        frames = []
        while q < end:
            time, xyz, encoded, q = _rotation_key(data, flags, q)
            if time is None:
                end += PADDING_SIZE
                continue
            xyz = np.array(xyz) / QUATERNION_SCALE
            w = math.sqrt(max(0, 1 - np.dot(xyz, xyz))) * (-1 if encoded & KEY_W_NEGATIVE else 1)
            frames.append((time / KEY_RATE, (*xyz, w)))
        if q != end:
            raise ValueError("Quaternion track exceeds its block")
        if frames:
            rotations[bone] = frames
    return rotations


def _translation_tracks(data, flags, sizes, t):
    positions = {}
    first_translation = True
    for bone, size in enumerate(sizes):
        end = t + size
        frames = []
        while t < end:
            if flags & CLIP_FLAG_FLOAT_TRANSLATION_TIMES:
                time = values(data, t, "f")[0]
                t += 4
            else:
                short, long, _ = values(data, t, "BHB")
                time = short - TRANSLATION_SHORT_TIME_BASE if short else long
                t += 4
                if first_translation:
                    if any(values(data, t, "3f")):
                        raise ValueError("Invalid translation prefix")
                    t += 12
                    first_translation = False
            frames.append((time / KEY_RATE, values(data, t, "3f")))
            t += 12
        if t != end:
            raise ValueError("Translation track exceeds its block")
        if frames:
            positions[bone] = frames
    return positions


def sample_track(frames, times, quaternion=False):
    source_times = np.array([f[0] for f in frames])
    source = np.array([f[1] for f in frames])
    if quaternion:
        for i in range(1, len(source)):
            if np.dot(source[i - 1], source[i]) < 0:
                source[i] *= -1
    result = np.array([np.interp(times, source_times, source[:, i]) for i in range(source.shape[1])]).T
    if quaternion:
        result /= np.linalg.norm(result, axis=1)[:, None]
    return result


def sample_animation(clip, positions, rotations, times):
    _, q_tracks, t_tracks, absolute_translation = clip
    p = np.tile(np.asarray(positions, dtype=float), (len(times), 1, 1))
    q = np.tile(np.asarray(rotations, dtype=float), (len(times), 1, 1))
    for bone, frames in q_tracks.items():
        q[:, bone] = sample_track(frames, times, True)
    for bone, frames in t_tracks.items():
        sampled = sample_track(frames, times)
        p[:, bone] = sampled if absolute_translation else positions[bone] + sampled
    return p, q


def quaternion(matrix):
    m = matrix[:3, :3]
    k = (
        np.array(
            [
                [m[0, 0] - m[1, 1] - m[2, 2], m[1, 0] + m[0, 1], m[2, 0] + m[0, 2], m[2, 1] - m[1, 2]],
                [m[1, 0] + m[0, 1], m[1, 1] - m[0, 0] - m[2, 2], m[2, 1] + m[1, 2], m[0, 2] - m[2, 0]],
                [m[2, 0] + m[0, 2], m[2, 1] + m[1, 2], m[2, 2] - m[0, 0] - m[1, 1], m[1, 0] - m[0, 1]],
                [m[2, 1] - m[1, 2], m[0, 2] - m[2, 0], m[1, 0] - m[0, 1], np.trace(m)],
            ]
        )
        / 3
    )
    eigenvalues, eigenvectors = np.linalg.eigh(k)
    return eigenvectors[:, np.argmax(eigenvalues)]


def multiply_quaternions(a, b):
    xyz = a[..., 3:] * b[..., :3] + b[..., 3:] * a[..., :3] + np.cross(a[..., :3], b[..., :3])
    w = a[..., 3:] * b[..., 3:] - np.sum(a[..., :3] * b[..., :3], axis=-1, keepdims=True)
    return np.concatenate((xyz, w), axis=-1)


def aim_rotation(source, target):
    source = source / np.linalg.norm(source)
    target = target / np.linalg.norm(target)
    dot = np.clip(np.dot(source, target), -1, 1)
    if dot < -0.99999:
        axis = np.cross(source, [0, 1, 0] if abs(source[1]) < 0.9 else [0, 0, 1])
        return rotation(np.append(axis / np.linalg.norm(axis), 0))
    return rotation(np.append(np.cross(source, target), 1 + dot))


def solve_guitar_arms(parents, positions, rotations):
    """Two-bone IK in place: put each guitarist hand on its target bone in every frame."""
    for frame in range(len(positions)):
        world = {}

        def matrix(i):
            if i not in world:
                local = np.eye(4)
                local[:3, :3] = rotation(rotations[frame, i] * [-1, -1, -1, 1])
                local[:3, 3] = positions[frame, i]
                world[i] = matrix(parents[i]) @ local if parents[i] >= 0 else local
            return world[i]

        def set_rotation(bone, world_rotation):
            parent_rotation = matrix(parents[bone])[:3, :3]
            rotations[frame, bone] = quaternion(parent_rotation.T @ world_rotation) * [-1, -1, -1, 1]
            world.clear()

        for upper, lower, hand, target, side in GUITAR_ARMS:
            a, b, c = (matrix(i)[:3, 3].copy() for i in (upper, lower, hand))
            target_matrix = matrix(target).copy()
            destination = target_matrix[:3, 3]
            length_a, length_b = np.linalg.norm(b - a), np.linalg.norm(c - b)
            direction = destination - a
            distance = np.linalg.norm(direction)
            direction /= max(distance, 1e-8)
            distance = np.clip(distance, abs(length_a - length_b) + REACH_MARGIN, length_a + length_b - REACH_MARGIN)
            along = (length_a**2 - length_b**2 + distance**2) / (2 * distance)
            pole = np.array([side * ELBOW_POLE_SIDE, ELBOW_POLE_DOWN, ELBOW_POLE_FORWARD])
            pole -= direction * np.dot(pole, direction)
            pole /= np.linalg.norm(pole)
            elbow = a + direction * along + pole * math.sqrt(max(0, length_a**2 - along**2))
            set_rotation(upper, aim_rotation(b - a, elbow - a) @ matrix(upper)[:3, :3])
            b, c = (matrix(i)[:3, 3].copy() for i in (lower, hand))
            set_rotation(lower, aim_rotation(c - b, destination - b) @ matrix(lower)[:3, :3])
            set_rotation(hand, target_matrix[:3, :3])
