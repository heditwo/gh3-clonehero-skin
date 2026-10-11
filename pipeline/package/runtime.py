"""Read and write the current runtime files of the library (the input of the mod today, the cache layout later).

GH3BND02 skinned meshes, GH3ANM01 rigs with sampled clips, GH3CLP01 performance clips, GH3VID01 movie frame banks,
and the gzip PCM stage sounds. The package converter reads them; the round-trip check writes them again.
"""

import json
import struct
import wave
from io import BytesIO

import numpy as np

from pipeline.stagepack.gltf import SKINNED_GH3_VERTEX

# Sampled clips (GH3ANM01 and GH3CLP01) have 30 frames per second, the GH3 animation rate of the band.
CLIP_SAMPLE_RATE = 30
BIND = struct.Struct("<i7f")
# GH3CLP01 track masks: the clip animates the translation, the rotation, or both of a bone.
TRACK_TRANSLATION, TRACK_ROTATION = 1, 2
CLIP_HEADER = struct.Struct("<8sIIIf")
MOVIE_HEADER = struct.Struct("<8s5I")


def read_meshes(data, magic=b"GH3BND02", dtype=SKINNED_GH3_VERTEX):
    """Return [(material checksum hex, vertex count, packed vertex bytes, faces (n, 3) uint32)]."""
    if data[:8] != magic:
        raise ValueError(f"Expected {magic.decode()} meshes")
    (count,) = struct.unpack_from("<I", data, 8)
    cursor = 12
    parts = []
    for _ in range(count):
        material, vertices, indices = struct.unpack_from("<III", data, cursor)
        cursor += 12
        size = vertices * dtype.itemsize
        packed = bytes(data[cursor : cursor + size])
        cursor += size
        faces = np.frombuffer(data, "<u4", indices, cursor).reshape(-1, 3)
        cursor += indices * 4
        parts.append((f"{material:08x}", vertices, packed, faces))
    if cursor != len(data):
        raise ValueError(f"Extra {magic.decode()} data")
    return parts


def write_meshes(parts, magic=b"GH3BND02"):
    out = bytearray(magic + struct.pack("<I", len(parts)))
    for material, count, packed, faces in parts:
        faces = np.asarray(faces, dtype="<u4")
        out += struct.pack("<III", int(material, 16), count, faces.size) + packed + faces.tobytes()
    return bytes(out)


def model_stage(name, materials):
    """The stage.json of a model folder."""
    return {
        "version": 1,
        "name": name,
        "cameraPosition": [0, 0, 0],
        "cameraTarget": [0, 1, 0],
        "materials": materials,
    }


def write_model(directory, name, materials, meshes):
    """Write meshes.bin (GH3BND02) and stage.json of a model folder."""
    (directory / "meshes.bin").write_bytes(write_meshes(meshes))
    (directory / "stage.json").write_text(json.dumps(model_stage(name, materials), indent=2) + "\n")


def sample_times(duration):
    """The frame count and the frame times of a clip sampled at CLIP_SAMPLE_RATE from 0 to its duration."""
    frames = round(duration * CLIP_SAMPLE_RATE) + 1
    return frames, np.linspace(0, duration, frames)


def read_animation(data):
    """Return (parents, bind (bones, 7) float32, [(duration, poses (frames, bones, 7) float32)]) in Unity space."""
    if data[:8] != b"GH3ANM01":
        raise ValueError("Expected GH3ANM01 animation")
    bones, count = struct.unpack_from("<II", data, 8)
    rows = [BIND.unpack_from(data, 16 + i * BIND.size) for i in range(bones)]
    parents = [r[0] for r in rows]
    bind = np.array([r[1:] for r in rows], dtype=np.float32)
    cursor = 16 + bones * BIND.size
    clips = []
    for _ in range(count):
        duration, frames = struct.unpack_from("<fI", data, cursor)
        cursor += 8
        poses = np.frombuffer(data, "<f4", frames * bones * 7, cursor).reshape(frames, bones, 7)
        cursor += poses.nbytes
        clips.append((duration, poses))
    if cursor != len(data):
        raise ValueError("Extra GH3ANM01 data")
    return parents, bind, clips


def write_animation(parents, bind, clips):
    out = bytearray(b"GH3ANM01" + struct.pack("<II", len(parents), len(clips)))
    for parent, row in zip(parents, bind):
        out += BIND.pack(parent, *row)
    for duration, poses in clips:
        out += struct.pack("<fI", duration, len(poses)) + np.asarray(poses, dtype="<f4").tobytes()
    return bytes(out)


def read_clip(data):
    """GH3CLP01 bytes (decompressed): return (bones, duration, [(bone, mask)], poses (frames, tracks, 7) float32)."""
    magic, bones, frames, count, duration = CLIP_HEADER.unpack_from(data)
    if magic != b"GH3CLP01":
        raise ValueError("Expected a GH3CLP01 clip")
    tracks = [struct.unpack_from("<II", data, CLIP_HEADER.size + 8 * i) for i in range(count)]
    cursor = CLIP_HEADER.size + 8 * count
    poses = np.frombuffer(data, "<f4", frames * count * 7, cursor).reshape(frames, count, 7)
    if cursor + poses.nbytes != len(data):
        raise ValueError("Extra GH3CLP01 data")
    return bones, duration, tracks, poses


def write_clip(bones, duration, tracks, poses):
    out = bytearray(CLIP_HEADER.pack(b"GH3CLP01", bones, len(poses), len(tracks), duration))
    for bone, mask in tracks:
        out += struct.pack("<II", bone, mask)
    return bytes(out + np.asarray(poses, dtype="<f4").tobytes())


def read_movie(data):
    """GH3VID01 bytes: return (width, height, [numerator, denominator], [PNG frame bytes])."""
    magic, width, height, count, numerator, denominator = MOVIE_HEADER.unpack_from(data)
    if magic != b"GH3VID01":
        raise ValueError("Expected a GH3VID01 movie")
    offsets = struct.unpack_from(f"<{count + 1}Q", data, MOVIE_HEADER.size)
    if (
        offsets[0] != MOVIE_HEADER.size + 8 * (count + 1)
        or offsets[-1] != len(data)
        or any(b < a for a, b in zip(offsets, offsets[1:]))
    ):
        raise ValueError("Invalid GH3VID01 frame table")
    return width, height, [numerator, denominator], [bytes(data[a:b]) for a, b in zip(offsets, offsets[1:])]


def write_movie(width, height, rate, frames):
    offsets = [MOVIE_HEADER.size + 8 * (len(frames) + 1)]
    for frame in frames:
        offsets.append(offsets[-1] + len(frame))
    return (
        MOVIE_HEADER.pack(b"GH3VID01", width, height, len(frames), *rate)
        + struct.pack(f"<{len(offsets)}Q", *offsets)
        + b"".join(frames)
    )


def wav_from_pcm(pcm, rate, channels):
    """16-bit PCM in a WAV container; the samples are not changed."""
    out = BytesIO()
    with wave.open(out, "wb") as audio:
        audio.setnchannels(channels)
        audio.setsampwidth(2)
        audio.setframerate(rate)
        audio.writeframes(pcm)
    return out.getvalue()


def pcm_from_wav(data):
    with wave.open(BytesIO(data)) as audio:
        if audio.getsampwidth() != 2:
            raise ValueError("Expected 16-bit PCM")
        return audio.readframes(audio.getnframes()), audio.getframerate(), audio.getnchannels()
