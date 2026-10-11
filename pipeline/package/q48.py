"""Reference encoder and decoder of the q48 clip encoding (docs/PACKAGE-FORMAT.md, "Clip encoding q48").

A package clip file (`*.q48.br`) is one Brotli stream of the bytes that `encode` returns.
"""

import struct

import numpy as np

MAGIC = b"GH3CLQ01"
HEADER = struct.Struct("<8sBBHHf")
TRACK = struct.Struct("<BBB")
BITS = 15
FPS = 30
POSITION, ROTATION = 1, 2
SQRT1_2 = np.sqrt(0.5)
OTHERS = np.array([[1, 2, 3], [0, 2, 3], [0, 1, 3], [0, 1, 2]])
# Positions: 16-bit steps between the lowest and the highest value of each axis; delta-coded 16-bit words.
POSITION_STEPS = 65535
WORD_MASK = 0xFFFF
MIN_ROTATION_NORM = 1e-6


def frame_count(duration: float) -> int:
    return round(duration * FPS) + 1


def _delta(values):
    values = values.astype(np.int64)
    out = values.copy()
    out[1:] = values[1:] - values[:-1]
    return (out & WORD_MASK).astype("<u2").tobytes()


def _undelta(raw, frames):
    return np.cumsum(np.frombuffer(raw, dtype="<u2", count=frames).astype(np.int64)) & WORD_MASK


def _positions(p):
    low, high = p.min(0), p.max(0)
    if len(p) == 1 or np.all(high == low):
        return struct.pack("<3f", *p[0]), True
    scale = (high - low) / POSITION_STEPS
    scale[scale == 0] = 1
    ints = np.round((p - low) / scale).astype(np.int64)
    return struct.pack("<6f", *low, *scale) + b"".join(_delta(ints[:, c]) for c in range(3)), False


def _smallest_three(q, bits):
    q = q / np.linalg.norm(q, axis=-1, keepdims=True)
    index = np.argmax(np.abs(q), axis=-1)
    q = q * np.where(np.take_along_axis(q, index[:, None], -1) < 0, -1.0, 1.0)
    top = (1 << bits) - 1
    rest = np.take_along_axis(q, OTHERS[index], -1)
    return index.astype(np.uint8), np.clip(np.round((rest / SQRT1_2 + 1) / 2 * top), 0, top).astype(np.int64)


def _rotations(q, bits):
    index, ints = _smallest_three(q, bits)
    if np.all(index == index[0]) and np.all(ints == ints[0]):
        return struct.pack("<4f", *q[0]), True
    return index.tobytes() + b"".join(_delta(ints[:, c]) for c in range(3)), False


def encode(positions, rotations, tracks, bones, duration, bits=BITS):
    """positions (frames, tracks, 3) and rotations (frames, tracks, 4, [x, y, z, w]) in Unity runtime coordinates;
    tracks is a list of (bone index, mask). Returns the uncompressed clip bytes."""
    frames = len(positions)
    if not 1 <= frames <= 65535 or not 0 < len(tracks) <= 65535 or not 0 < bones <= 255:
        raise ValueError("Clip exceeds the q48 limits")
    table, position_blocks, rotation_blocks = [], [], []
    for t, (bone, mask) in enumerate(tracks):
        flags = 0
        if mask & POSITION:
            data, constant = _positions(np.asarray(positions[:, t], dtype=np.float64))
            position_blocks.append(data)
            flags |= constant
        if mask & ROTATION:
            data, constant = _rotations(np.asarray(rotations[:, t], dtype=np.float64), bits)
            rotation_blocks.append(data)
            flags |= constant << 1
        table.append(TRACK.pack(bone, mask, flags))
    return HEADER.pack(MAGIC, bits, bones, frames, len(tracks), duration) + b"".join(
        table + position_blocks + rotation_blocks
    )


def decode(data: bytes) -> tuple[dict, np.ndarray, np.ndarray]:  # noqa: C901
    """Return (header dict, positions (frames, tracks, 3), rotations (frames, tracks, 4)). Raises ValueError on a
    malformed clip. Channels absent from a track mask decode as zero position and identity rotation."""
    if len(data) < HEADER.size:
        raise ValueError("Truncated q48 header")
    magic, bits, bones, frames, count, duration = HEADER.unpack_from(data)
    if magic != MAGIC:
        raise ValueError("Not a q48 clip")
    if not 2 <= bits <= 16 or bones == 0 or frames == 0 or count == 0:
        raise ValueError("Invalid q48 header")
    if not np.isfinite(duration) or duration < 0 or (frames > 1 and duration <= 0):
        raise ValueError("Invalid q48 duration")
    at = HEADER.size
    if len(data) < at + count * TRACK.size:
        raise ValueError("Truncated q48 track table")
    tracks = [TRACK.unpack_from(data, at + i * TRACK.size) for i in range(count)]
    at += count * TRACK.size
    if any(bone >= bones or not 1 <= mask <= 3 or flags & ~mask for bone, mask, flags in tracks):
        raise ValueError("Invalid q48 track")
    if len({bone for bone, _, _ in tracks}) != count:
        raise ValueError("Repeated q48 track bone")
    positions = np.zeros((frames, count, 3))
    rotations = np.tile([0.0, 0.0, 0.0, 1.0], (frames, count, 1))
    top = (1 << bits) - 1

    def take(size):
        nonlocal at
        if at + size > len(data):
            raise ValueError("Truncated q48 data")
        at += size
        return data[at - size : at]

    for t, (_, mask, flags) in enumerate(tracks):
        if not mask & POSITION:
            continue
        if flags & POSITION:
            positions[:, t] = struct.unpack("<3f", take(12))
            continue
        low_scale = np.array(struct.unpack("<6f", take(24)))
        ints = np.stack([_undelta(take(2 * frames), frames) for _ in range(3)], 1)
        positions[:, t] = low_scale[:3] + ints * low_scale[3:]
    for t, (_, mask, flags) in enumerate(tracks):
        if not mask & ROTATION:
            continue
        if flags & ROTATION:
            q = np.array(struct.unpack("<4f", take(16)))
            norm = np.linalg.norm(q)
            if not np.isfinite(norm) or norm < MIN_ROTATION_NORM:
                raise ValueError("Invalid q48 constant rotation")
            rotations[:, t] = q / norm
            continue
        index = np.frombuffer(take(frames), dtype=np.uint8).astype(np.int64)
        if np.any(index > 3):
            raise ValueError("Invalid q48 rotation index")
        ints = np.stack([_undelta(take(2 * frames), frames) for _ in range(3)], 1)
        if np.any(ints > top):
            raise ValueError("q48 rotation value out of range")
        rest = (ints / top * 2 - 1) * SQRT1_2
        q = np.empty((frames, 4))
        np.put_along_axis(q, OTHERS[index], rest, -1)
        np.put_along_axis(q, index[:, None], np.sqrt(np.clip(1 - np.sum(rest * rest, -1), 0, 1))[:, None], -1)
        rotations[:, t] = q / np.linalg.norm(q, axis=-1, keepdims=True)
    if at != len(data):
        raise ValueError("Extra q48 data")
    if not np.isfinite(positions).all():
        raise ValueError("Non-finite q48 position")
    header = {
        "bits": bits,
        "bones": bones,
        "frames": frames,
        "duration": duration,
        "tracks": [(b, m) for b, m, _ in tracks],
    }
    return header, positions, rotations
