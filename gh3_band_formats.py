"""Bounded readers for the owner's GH3 PC skeletons and performance clips."""

import math

import numpy as np
from gh3_formats import values, word


def rotation(q):
    x, y, z, w = q / np.linalg.norm(q)
    return np.array([[1 - 2 * (y*y + z*z), 2 * (x*y - z*w), 2 * (x*z + y*w)],
                     [2 * (x*y + z*w), 1 - 2 * (x*x + z*z), 2 * (y*z - x*w)],
                     [2 * (x*z - y*w), 2 * (y*z + x*w), 1 - 2 * (x*x + y*y)]])


def skeleton(data):
    count = word(data, 4)
    if not 1 <= count <= 255:
        raise ValueError("Invalid skeleton size")
    names_at, parents_at, _, _, _, matrices_at, positions_at, rotations_at = values(data, 16, "8I")
    names = values(data, names_at, f"{count}I")
    parents = [names.index(parent) if parent in names else -1 for parent in values(data, parents_at, f"{count}I")]
    positions = np.array([values(data, positions_at + i * 16, "4f")[:3] for i in range(count)])
    rotations = np.array([values(data, rotations_at + i * 16, "4f") for i in range(count)])
    bind = np.array([values(data, matrices_at + i * 64, "16f") for i in range(count)]).reshape(count, 4, 4).transpose(0, 2, 1)
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
        if not np.allclose(matrix(i) @ bind[i], np.eye(4), atol=0.0001):
            raise ValueError(f"Skeleton bind pose mismatch at bone {i}")
    return names, parents, positions, rotations


def animation(data, expected_bones):
    if word(data, 0) != 0 or word(data, 32) != 0x48:
        raise ValueError("Expected a GH3 SKA clip")
    flags = word(data, 36)
    duration = values(data, 40, "f")[0]
    count = values(data, 44, "H")[0]
    if count != expected_bones or not math.isfinite(duration) or not 0 <= duration <= 600:
        raise ValueError("Invalid animation size or duration")
    q, t, qs, ts = values(data, 88, "4I")
    rotations, positions = {}, {}
    if flags & 0x40:
        active = set(range(count))
        if flags & 0x80000:
            partial = word(data, 24)
            masks = values(data, partial + 4, "4I")
            active = {i for i in range(count) if masks[i // 32] & (1 << (i % 32))}
        for i in range(count):
            if i not in active:
                continue
            rotations[i] = [(0, values(data, q + i * 16, "4f"))]
            positions[i] = [(0, values(data, t + i * 16, "4f")[:3])]
        return duration, rotations, positions, not bool(flags & 0x200)
    first_translation = True
    for bone, size in enumerate(values(data, qs, f"{count}H")):
        end = q + size
        frames = []
        while q < end:
            encoded = values(data, q, "H")[0]
            q += 2
            if flags & 0x800000:
                if flags & 0x100:
                    time = encoded
                    encoded = values(data, q, "H")[0]
                    q += 2
                else:
                    time = encoded & 0x7FF
                if encoded & 0x4000:
                    if not encoded & 0x3800:
                        x, y = values(data, q, "2B")
                        xyz = [x, y, y]
                        q += 2
                    else:
                        xyz = []
                        for flag in (0x2000, 0x1000, 0x800):
                            if encoded & flag:
                                xyz.append(values(data, q, "B")[0])
                                q += 1
                            else:
                                q = (q + 1) // 2 * 2
                                xyz.append(values(data, q, "h")[0])
                                q += 2
                        q = (q + 1) // 2 * 2
                else:
                    xyz = values(data, q, "3h")
                    q += 6
            else:
                time = encoded
                xyz = values(data, q, "3h")
                q += 6
                if time == 0xDEAD:
                    end += 8
                    continue
                encoded = 0
            xyz = np.array(xyz) / 16384.0
            w = math.sqrt(max(0, 1 - np.dot(xyz, xyz))) * (-1 if encoded & 0x8000 else 1)
            frames.append((time / 60, (*xyz, w)))
        if q != end:
            raise ValueError("Quaternion track exceeds its block")
        if frames:
            rotations[bone] = frames
    for bone, size in enumerate(values(data, ts, f"{count}H")):
        end = t + size
        frames = []
        while t < end:
            if flags & 0x80000000:
                time = values(data, t, "f")[0]
                t += 4
            else:
                short, long, _ = values(data, t, "BHB")
                time = short - 0x40 if short else long
                t += 4
                if first_translation:
                    if any(values(data, t, "3f")):
                        raise ValueError("Invalid translation prefix")
                    t += 12
                    first_translation = False
            frames.append((time / 60, values(data, t, "3f")))
            t += 12
        if t != end:
            raise ValueError("Translation track exceeds its block")
        if frames:
            positions[bone] = frames
    for tracks in (rotations, positions):
        for frames in tracks.values():
            times = [frame[0] for frame in frames]
            if times != sorted(times) or min(times) < 0 or max(times) > duration + 0.02:
                raise ValueError("Invalid animation key times")
            if not all(math.isfinite(v) for _, row in frames for v in row):
                raise ValueError("Non-finite animation")
    return duration, rotations, positions, False


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
    k = np.array([[m[0, 0] - m[1, 1] - m[2, 2], m[1, 0] + m[0, 1], m[2, 0] + m[0, 2], m[2, 1] - m[1, 2]],
                  [m[1, 0] + m[0, 1], m[1, 1] - m[0, 0] - m[2, 2], m[2, 1] + m[1, 2], m[0, 2] - m[2, 0]],
                  [m[2, 0] + m[0, 2], m[2, 1] + m[1, 2], m[2, 2] - m[0, 0] - m[1, 1], m[1, 0] - m[0, 1]],
                  [m[2, 1] - m[1, 2], m[0, 2] - m[2, 0], m[1, 0] - m[0, 1], np.trace(m)]]) / 3
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

        for upper, lower, hand, target, side in [(11, 12, 13, 122, 1), (15, 16, 17, 124, -1)]:
            a, b, c = (matrix(i)[:3, 3].copy() for i in (upper, lower, hand))
            target_matrix = matrix(target).copy()
            destination = target_matrix[:3, 3]
            length_a, length_b = np.linalg.norm(b - a), np.linalg.norm(c - b)
            direction = destination - a
            distance = np.linalg.norm(direction)
            direction /= max(distance, 1e-8)
            distance = np.clip(distance, abs(length_a - length_b) + 0.0001, length_a + length_b - 0.0001)
            along = (length_a**2 - length_b**2 + distance**2) / (2 * distance)
            pole = np.array([side * 0.5, -1, 0.1])
            pole -= direction * np.dot(pole, direction)
            pole /= np.linalg.norm(pole)
            elbow = a + direction * along + pole * math.sqrt(max(0, length_a**2 - along**2))
            set_rotation(upper, aim_rotation(b - a, elbow - a) @ matrix(upper)[:3, :3])
            b, c = (matrix(i)[:3, 3].copy() for i in (lower, hand))
            set_rotation(lower, aim_rotation(c - b, destination - b) @ matrix(lower)[:3, :3])
            set_rotation(hand, target_matrix[:3, :3])
