"""Rotation conversions shared by the character, skeleton, and chain code: quaternions are [x, y, z, w], vectorized
over the leading axes."""

import numpy as np


def matrix_quaternions(matrices) -> np.ndarray:
    """[x, y, z, w] quaternions (w >= 0 where possible) of rotation matrices [..., 3, 3]."""
    m = np.asarray(matrices, dtype=np.float64)
    shape = m.shape[:-2]
    m = m.reshape(-1, 3, 3)
    q = np.empty((len(m), 4))
    trace = m[:, 0, 0] + m[:, 1, 1] + m[:, 2, 2]
    cases = [
        trace > 0,
        (m[:, 0, 0] >= m[:, 1, 1]) & (m[:, 0, 0] >= m[:, 2, 2]),
        m[:, 1, 1] >= m[:, 2, 2],
        np.ones(len(m), bool),
    ]
    done = np.zeros(len(m), bool)
    for case, select in enumerate(cases):
        rows = select & ~done
        done |= rows
        r = m[rows]
        if case == 0:
            s = np.sqrt(trace[rows] + 1) * 2
            q[rows] = np.stack(
                [(r[:, 2, 1] - r[:, 1, 2]) / s, (r[:, 0, 2] - r[:, 2, 0]) / s, (r[:, 1, 0] - r[:, 0, 1]) / s, s / 4], -1
            )
            continue
        i = case - 1
        j = (i + 1) % 3
        k = (i + 2) % 3
        s = np.sqrt(np.maximum(0, 1 + r[:, i, i] - r[:, j, j] - r[:, k, k])) * 2
        values = np.empty((len(r), 4))
        values[:, i] = s / 4
        values[:, j] = (r[:, j, i] + r[:, i, j]) / s
        values[:, k] = (r[:, k, i] + r[:, i, k]) / s
        values[:, 3] = (r[:, k, j] - r[:, j, k]) / s
        q[rows] = values
    q /= np.linalg.norm(q, axis=-1, keepdims=True)
    return q.reshape(*shape, 4)


def quaternion_matrices(q: np.ndarray) -> np.ndarray:
    """3x3 rotation matrices of quaternions [..., 4] ([x, y, z, w])."""
    x, y, z, w = (q[..., i] for i in range(4))
    return np.stack(
        [
            np.stack([1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)], -1),
            np.stack([2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)], -1),
            np.stack([2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)], -1),
        ],
        -2,
    )


def qnormalize(q: np.ndarray) -> np.ndarray:
    return q / np.linalg.norm(q, axis=-1, keepdims=True)


def quaternion_matrix(q) -> np.ndarray:
    """The rotation matrix of one quaternion [x, y, z, w] (normalized first)."""
    x, y, z, w = np.asarray(q, dtype=float) / np.linalg.norm(q)
    return np.array(
        [
            [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
            [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
            [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
        ]
    )
