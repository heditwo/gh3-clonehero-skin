"""Pre-encoded package textures (docs/PACKAGE-FORMAT.md section 7): the GH3TEX02 file, its BC1/BC3 encoder, and a decoder.

GH3TEX02: magic, Unity TextureFormat (4 RGBA32, 10 BC1, 12 BC3), width, height, mip count, flags (int32 little-endian
each), then the full mip chain, largest level first, rows bottom first, as Texture2D.LoadRawTextureData takes it. The
importer copies the file into the cache without a change.

The encoder gives the same bytes as the C# importer encoder of 2026-10-10 (src/Packages/BlockCompression.cs before
format 1.0 stored pre-encoded textures): principal-axis endpoints, least-squares refinement, exact single colours, and
min/max alpha with 8 levels. All blocks of a level are encoded together with numpy.

Relative imports only: the Blender add-on bundles this module (blender/build.py). numpy is the only dependency.
"""

import json
import math
import struct
import zlib

import numpy as np

MAGIC = b"GH3TEX02"
HEADER = struct.Struct("<8s5i")
HEADER_SIZE = HEADER.size
RGBA32, BC1, BC3 = 4, 10, 12
FORMATS = {RGBA32: "RGBA32", BC1: "BC1", BC3: "BC3"}
FLAG_LINEAR = 1
FLAGS = FLAG_LINEAR
MIME_TYPE = "image/vnd-gh3.tex"
EXTENSION = "GH3_texture_bc"
MAX_SIZE = 4096
MINIMUM_PSNR = 36.0
# Blocks for each numpy batch (a 256 x 256 pixel area): about 10 MB of work arrays.
BATCH = 1 << 12


class TextureError(ValueError):
    pass


def mip_count(width, height):
    count, size = 1, max(width, height)
    while size > 1:
        size >>= 1
        count += 1
    return count


def level_size(fmt, width, height):
    if fmt == RGBA32:
        return width * height * 4
    return max(1, (width + 3) // 4) * max(1, (height + 3) // 4) * (8 if fmt == BC1 else 16)


def levels(width, height, mips):
    """(width, height) of each mip level."""
    for _ in range(mips):
        yield width, height
        width, height = max(1, width // 2), max(1, height // 2)


def read_header(data):
    """(format, width, height, mips, flags) of a GH3TEX02 file. Raises TextureError when the file is not valid."""
    if len(data) < HEADER_SIZE:
        raise TextureError("file is too short for a GH3TEX02 texture")
    magic, fmt, width, height, mips, flags = HEADER.unpack_from(data)
    if magic != MAGIC:
        raise TextureError("not a GH3TEX02 texture")
    if fmt not in FORMATS:
        raise TextureError(f"unknown texture format {fmt} (4 RGBA32, 10 BC1, 12 BC3)")
    if not (1 <= width <= MAX_SIZE and 1 <= height <= MAX_SIZE):
        raise TextureError(f"texture size must be 1 to {MAX_SIZE} pixels")
    if mips != mip_count(width, height):
        raise TextureError(f"texture has {mips} mip levels, the full chain is {mip_count(width, height)}")
    if flags & ~FLAGS:
        raise TextureError(f"unknown texture flags {flags:#x}")
    if fmt != RGBA32 and (width % 4 or height % 4):
        raise TextureError("BC1 and BC3 textures must have a width and a height that are multiples of 4")
    expected = HEADER_SIZE + sum(level_size(fmt, w, h) for w, h in levels(width, height, mips))
    if len(data) != expected:
        raise TextureError(f"texture has {len(data)} bytes, its header gives {expected}")
    return fmt, width, height, mips, flags


def downsample(level):
    """Next mip level: 2 x 2 box filter with rounding; an odd last row or column repeats (as the C# importer did)."""
    height, width = level.shape[:2]
    h, w = max(1, height // 2), max(1, width // 2)
    y0 = np.minimum(np.arange(h) * 2, height - 1)
    y1 = np.minimum(np.arange(h) * 2 + 1, height - 1)
    x0 = np.minimum(np.arange(w) * 2, width - 1)
    x1 = np.minimum(np.arange(w) * 2 + 1, width - 1)
    wide = level.astype(np.uint16)
    total = wide[y0][:, x0] + wide[y0][:, x1] + wide[y1][:, x0] + wide[y1][:, x1] + 2
    return (total >> 2).astype(np.uint8)


def _expand5(v):
    return v << 3 | v >> 2


def _expand6(v):
    return v << 2 | v >> 4


def _quantize(value, bits):
    top = (1 << bits) - 1
    return np.clip(np.round(np.asarray(value, dtype=np.float64) * top / 255.0), 0, top).astype(np.int64)


def _single_table(bits):
    """For each 8-bit value, the endpoint pair whose 2/3 + 1/3 mix is closest (BC1 index 2)."""
    top = (1 << bits) - 1
    codes = np.arange(top + 1)
    expanded = _expand5(codes) if bits == 5 else _expand6(codes)
    a, b = expanded[:, None], expanded[None, :]
    mix = (2 * a + b) // 3
    table = np.zeros((256, 2), dtype=np.int64)
    for value in range(256):
        error = np.abs(mix - value) * 100 + np.abs(a - b)
        index = int(np.argmin(error))
        table[value] = divmod(index, top + 1)
    return table


_SINGLE5 = _single_table(5)
_SINGLE6 = _single_table(6)


def _pack(r, g, b):
    return r << 11 | g << 5 | b


def _palette(c0, c1):
    """(N, 4, 3) BC1 four-colour palette."""
    r0, g0, b0 = _expand5(c0 >> 11), _expand6(c0 >> 5 & 63), _expand5(c0 & 31)
    r1, g1, b1 = _expand5(c1 >> 11), _expand6(c1 >> 5 & 63), _expand5(c1 & 31)
    first, second = np.stack([r0, g0, b0], -1), np.stack([r1, g1, b1], -1)
    return np.stack([first, second, (2 * first + second) // 3, (first + 2 * second) // 3], 1)


def _match(rgb, c0, c1):
    """Indices (N, 16) and the summed squared error (N) of the nearest palette colours; the first of equal ones wins."""
    palette = _palette(c0, c1).astype(np.int32)
    errors = np.zeros((len(rgb), 16, 4), dtype=np.int32)
    for channel in range(3):
        difference = rgb[:, :, None, channel] - palette[:, None, :, channel]
        errors += difference * difference
    return errors.argmin(-1), errors.min(-1).sum(-1, dtype=np.int64)


_WEIGHTS = np.array([1.0, 0.0, 2.0 / 3, 1.0 / 3])


def _refine(rgb, indices):
    """Least-squares endpoints for the given indices; ok is False where the system is singular."""
    w_all = _WEIGHTS[indices]
    zeros = np.zeros(len(rgb))
    aa, bb, ab = zeros.copy(), zeros.copy(), zeros.copy()
    a_sum, b_sum = np.zeros((len(rgb), 3)), np.zeros((len(rgb), 3))
    # Sequential sums over the 16 pixels in pixel order, so the floating-point results equal the C# encoder.
    for i in range(16):
        w = w_all[:, i]
        v = 1 - w
        aa += w * w
        bb += v * v
        ab += w * v
        a_sum += w[:, None] * rgb[:, i]
        b_sum += v[:, None] * rgb[:, i]
    det = aa * bb - ab * ab
    ok = np.abs(det) >= 1e-9
    f = 1 / np.where(ok, det, 1.0)
    first = np.clip((a_sum * bb[:, None] - b_sum * ab[:, None]) * f[:, None], 0, 255)
    second = np.clip((b_sum * aa[:, None] - a_sum * ab[:, None]) * f[:, None], 0, 255)
    c0 = _pack(_quantize(first[:, 0], 5), _quantize(first[:, 1], 6), _quantize(first[:, 2], 5))
    c1 = _pack(_quantize(second[:, 0], 5), _quantize(second[:, 1], 6), _quantize(second[:, 2], 5))
    return ok, c0, c1


def _encode_color(block):
    """BC1 colour blocks (N, 8 bytes) and their squared errors for blocks (N, 16, 4) int32."""
    n = len(block)
    rgb = block[:, :, :3]
    low, high = rgb.min(1), rgb.max(1)
    single = (low == high).all(1)
    c0 = np.zeros(n, dtype=np.int64)
    c1 = np.zeros(n, dtype=np.int64)

    s = np.nonzero(single)[0]
    if len(s):
        r, g, b = low[s, 0], low[s, 1], low[s, 2]
        a = _pack(_SINGLE5[r, 0], _SINGLE6[g, 0], _SINGLE5[b, 0])
        z = _pack(_SINGLE5[r, 1], _SINGLE6[g, 1], _SINGLE5[b, 1])
        exact = _pack(_quantize(r, 5), _quantize(g, 6), _quantize(b, 5))
        same = (_expand5(exact >> 11) == r) & (_expand6(exact >> 5 & 63) == g) & (_expand5(exact & 31) == b)
        c0[s] = np.where(same, exact, a)
        c1[s] = np.where(same, exact, z)

    m = np.nonzero(~single)[0]
    if len(m):
        pixels = rgb[m].astype(np.float64)
        mean = rgb[m].sum(1) / 16
        centered = pixels - mean[:, None, :]
        xx = np.zeros(len(m))
        xy = xx.copy()
        xz = xx.copy()
        yy = xx.copy()
        yz = xx.copy()
        zz = xx.copy()
        for i in range(16):
            r, g, b = centered[:, i, 0], centered[:, i, 1], centered[:, i, 2]
            xx += r * r
            xy += r * g
            xz += r * b
            yy += g * g
            yz += g * b
            zz += b * b
        span = (high[m] - low[m]).astype(np.float64)
        ax, ay, az = span[:, 0], span[:, 1], span[:, 2]
        active = np.ones(len(m), dtype=bool)
        for _ in range(6):
            nx = xx * ax + xy * ay + xz * az
            ny = xy * ax + yy * ay + yz * az
            nz = xz * ax + yz * ay + zz * az
            length = np.maximum(np.abs(nx), np.maximum(np.abs(ny), np.abs(nz)))
            active &= length >= 1e-9
            safe = np.where(active, length, 1.0)
            ax = np.where(active, nx / safe, ax)
            ay = np.where(active, ny / safe, ay)
            az = np.where(active, nz / safe, az)
        flat = np.abs(ax) + np.abs(ay) + np.abs(az) < 1e-9
        ax = np.where(flat, 0.299, ax)
        ay = np.where(flat, 0.587, ay)
        az = np.where(flat, 0.114, az)
        d = rgb[m, :, 0] * ax[:, None] + rgb[m, :, 1] * ay[:, None] + rgb[m, :, 2] * az[:, None]
        rows = np.arange(len(m))
        top, bottom = rgb[m][rows, d.argmax(1)], rgb[m][rows, d.argmin(1)]
        c0[m] = _pack(_quantize(top[:, 0], 5), _quantize(top[:, 1], 6), _quantize(top[:, 2], 5))
        c1[m] = _pack(_quantize(bottom[:, 0], 5), _quantize(bottom[:, 1], 6), _quantize(bottom[:, 2], 5))

    indices, error = _match(rgb, c0, c1)
    refining = ~single
    for _ in range(2):
        refining &= error > 0
        r = np.nonzero(refining)[0]
        if not len(r):
            break
        ok, r0, r1 = _refine(rgb[r].astype(np.float64), indices[r])
        trial_indices, trial_error = _match(rgb[r], r0, r1)
        better = ok & (trial_error < error[r])
        take = r[better]
        c0[take], c1[take], error[take] = r0[better], r1[better], trial_error[better]
        indices[take] = trial_indices[better]
        refining[r[~better]] = False

    swap = c0 < c1
    c0, c1 = np.where(swap, c1, c0), np.where(swap, c0, c1)
    indices = np.where(swap[:, None], np.array([1, 0, 3, 2])[indices], indices)
    indices = np.where((c0 == c1)[:, None], 0, indices)
    bits = (indices.astype(np.uint64) << (2 * np.arange(16, dtype=np.uint64))).sum(1, dtype=np.uint64)
    out = np.zeros((n, 8), dtype=np.uint8)
    out[:, 0], out[:, 1], out[:, 2], out[:, 3] = c0 & 255, c0 >> 8, c1 & 255, c1 >> 8
    for k in range(4):
        out[:, 4 + k] = (bits >> np.uint64(8 * k)) & np.uint64(255)
    return out, error


def _encode_alpha(block):
    """BC3 alpha blocks (N, 8 bytes) and their squared errors."""
    alpha = block[:, :, 3]
    low, high = alpha.min(1), alpha.max(1)
    steps = np.arange(2, 8)
    palette = np.concatenate(
        [high[:, None], low[:, None], ((8 - steps) * high[:, None] + (steps - 1) * low[:, None] + 3) // 7], 1
    )
    errors = np.abs(alpha[:, :, None] - palette[:, None, :])
    choice = errors.argmin(-1)
    best = np.take_along_axis(errors, choice[..., None], -1)[..., 0]
    flat = high == low
    choice[flat] = 0
    best[flat] = 0
    bits = (choice.astype(np.uint64) << (3 * np.arange(16, dtype=np.uint64))).sum(1, dtype=np.uint64)
    out = np.zeros((len(block), 8), dtype=np.uint8)
    out[:, 0], out[:, 1] = high, low
    for k in range(6):
        out[:, 2 + k] = (bits >> np.uint64(8 * k)) & np.uint64(255)
    return out, (best * best).sum(1)


def _blocks(level):
    """(N, 16, 4) int32 pixel blocks in row-major block order; edge pixels repeat for levels smaller than 4."""
    height, width = level.shape[:2]
    bx, by = max(1, (width + 3) // 4), max(1, (height + 3) // 4)
    ys = np.minimum(np.arange(by * 4), height - 1)
    xs = np.minimum(np.arange(bx * 4), width - 1)
    padded = level[ys][:, xs]
    return padded.reshape(by, 4, bx, 4, 4).transpose(0, 2, 1, 3, 4).reshape(-1, 16, 4).astype(np.int32)


def _encode_batch(block, alpha):
    color, error = _encode_color(block)
    total = int(error.sum())
    if alpha:
        alpha_bytes, alpha_error = _encode_alpha(block)
        total += int(alpha_error.sum())
        color = np.concatenate([alpha_bytes, color], 1)
    return color.reshape(-1).tobytes(), total


def encode_level(level, alpha):
    """BC1 (alpha False) or BC3 bytes of one level (H, W, 4) uint8, and the summed squared error."""
    blocks = _blocks(level)
    parts = [_encode_batch(blocks[start : start + BATCH], alpha) for start in range(0, len(blocks), BATCH)]
    return b"".join(data for data, _ in parts), sum(error for _, error in parts)


def choose_format(rgba):
    """BC1 for opaque, BC3 for alpha, RGBA32 when a side is not a multiple of 4."""
    height, width = rgba.shape[:2]
    if width % 4 or height % 4:
        return RGBA32
    return BC1 if (rgba[:, :, 3] == 255).all() else BC3


def encode(rgba, linear=False):
    """GH3TEX02 bytes of an RGBA image (H, W, 4) uint8 with the top row first (as PNG files store it).

    A first level under MINIMUM_PSNR in BC1/BC3 (art that GH3 did not store as DXT, for example smooth gradients) stays
    RGBA32."""
    rgba = np.ascontiguousarray(np.asarray(rgba, dtype=np.uint8)[::-1])
    if rgba.ndim != 3 or rgba.shape[2] != 4:
        raise TextureError("expected an RGBA image")
    height, width = rgba.shape[:2]
    if not (1 <= width <= MAX_SIZE and 1 <= height <= MAX_SIZE):
        raise TextureError(f"texture size must be 1 to {MAX_SIZE} pixels")
    fmt = choose_format(rgba)
    first = None
    if fmt != RGBA32:
        first, squared = encode_level(rgba, fmt == BC3)
        error = squared / (width * height * (4 if fmt == BC3 else 3))
        if error > 0 and 10 * math.log10(255.0 * 255.0 / error) < MINIMUM_PSNR:
            fmt, first = RGBA32, None
    mips = mip_count(width, height)
    out = [HEADER.pack(MAGIC, fmt, width, height, mips, FLAG_LINEAR if linear else 0)]
    level = rgba
    for mip in range(mips):
        if fmt == RGBA32:
            out.append(level.tobytes())
        elif mip == 0:
            out.append(first)
        else:
            out.append(encode_level(level, fmt == BC3)[0])
        if mip + 1 < mips:
            level = downsample(level)
    return b"".join(out)


def decode_level(fmt, data, width, height):
    """RGBA (H, W, 4) uint8 of one level, rows in file order (bottom first). BC1 three-colour blocks decode as the GPU
    does: index 3 is transparent black."""
    if fmt == RGBA32:
        return np.frombuffer(data, dtype=np.uint8).reshape(height, width, 4).copy()
    bx, by = max(1, (width + 3) // 4), max(1, (height + 3) // 4)
    size = 8 if fmt == BC1 else 16
    raw = np.frombuffer(data, dtype=np.uint8).reshape(-1, size)
    color = raw[:, size - 8 :]
    words = color[:, :4].copy().view("<u2").astype(np.int32)
    c0, c1 = words[:, 0], words[:, 1]
    palette = np.empty((len(raw), 4, 4), dtype=np.int32)
    palette[:, :, 3] = 255
    first = np.stack([_expand5(c0 >> 11), _expand6(c0 >> 5 & 63), _expand5(c0 & 31)], -1)
    second = np.stack([_expand5(c1 >> 11), _expand6(c1 >> 5 & 63), _expand5(c1 & 31)], -1)
    palette[:, 0, :3], palette[:, 1, :3] = first, second
    palette[:, 2, :3], palette[:, 3, :3] = (2 * first + second) // 3, (first + 2 * second) // 3
    if fmt == BC1:
        three = c0 <= c1
        palette[three, 2, :3] = (first[three] + second[three]) // 2
        palette[three, 3] = 0
    bits = color[:, 4:].copy().view("<u4")[:, 0]
    indices = (bits[:, None] >> (2 * np.arange(16, dtype=np.uint32))) & 3
    pixels = palette[np.arange(len(raw))[:, None], indices]
    if fmt == BC3:
        a0, a1 = raw[:, 0].astype(np.int32), raw[:, 1].astype(np.int32)
        alpha_bits = np.zeros(len(raw), dtype=np.uint64)
        for k in range(6):
            alpha_bits |= raw[:, 2 + k].astype(np.uint64) << np.uint64(8 * k)
        codes = ((alpha_bits[:, None] >> (np.uint64(3) * np.arange(16, dtype=np.uint64))) & np.uint64(7)).astype(
            np.int64
        )
        i = np.arange(8)[None, :]
        eight = np.where(
            i == 0, a0[:, None], np.where(i == 1, a1[:, None], ((8 - i) * a0[:, None] + (i - 1) * a1[:, None] + 3) // 7)
        )
        six = np.where(
            i == 0,
            a0[:, None],
            np.where(
                i == 1,
                a1[:, None],
                np.where(i == 6, 0, np.where(i == 7, 255, ((6 - i) * a0[:, None] + (i - 1) * a1[:, None] + 2) // 5)),
            ),
        )
        table = np.where((a0 > a1)[:, None], eight, six)
        pixels[:, :, 3] = table[np.arange(len(raw))[:, None], codes]
    image = pixels.astype(np.uint8).reshape(by, bx, 4, 4, 4).transpose(0, 2, 1, 3, 4).reshape(by * 4, bx * 4, 4)
    return image[:height, :width]


def level_data(data):
    """[(width, height, bytes)] of all levels of a GH3TEX02 file."""
    fmt, width, height, mips, _ = read_header(data)
    result, cursor = [], HEADER_SIZE
    for w, h in levels(width, height, mips):
        size = level_size(fmt, w, h)
        result.append((w, h, data[cursor : cursor + size]))
        cursor += size
    return result


def decode(data, mip=0):
    """RGBA (H, W, 4) uint8 of one level, top row first (as PNG files store it)."""
    fmt = read_header(data)[0]
    width, height, payload = level_data(data)[mip]
    return decode_level(fmt, payload, width, height)[::-1].copy()


def psnr(a, b, channels=4):
    error = np.mean(
        (np.asarray(a, dtype=np.float64)[..., :channels] - np.asarray(b, dtype=np.float64)[..., :channels]) ** 2
    )
    return float("inf") if error == 0 else float(10 * np.log10(255.0 * 255.0 / error))


def problems(data):
    """Rule failures of a package texture (docs/PACKAGE-FORMAT.md section 7), as text; empty when it is valid."""
    try:
        fmt, width, height, mips, _ = read_header(data)
    except TextureError as error:
        return [str(error)]
    if fmt == BC1:
        # BC1 blocks with color0 <= color1 have a transparent index; alpha needs BC3.
        blocks = np.frombuffer(data, dtype=np.uint8, offset=HEADER_SIZE).reshape(-1, 8).astype(np.int64)
        c0, c1 = blocks[:, 0] | blocks[:, 1] << 8, blocks[:, 2] | blocks[:, 3] << 8
        bits = blocks[:, 4] | blocks[:, 5] << 8 | blocks[:, 6] << 16 | blocks[:, 7] << 24
        three = (c0 <= c1) & ((bits[:, None] >> (2 * np.arange(16)) & 3) == 3).any(1)
        if three.any():
            return [
                f"BC1 texture has {int(three.sum())} blocks with transparent pixels; textures with alpha must use BC3"
            ]
    return []


# GLB images (docs/PACKAGE-FORMAT.md section 6.2, GH3_texture_bc).
GLB_MAGIC, JSON_CHUNK, BIN_CHUNK = 0x46546C67, 0x4E4F534A, 0x004E4942
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"


def split_glb(data):
    """(glTF JSON, binary chunk) of GLB bytes."""
    magic, version, length = struct.unpack_from("<3I", data)
    if magic != GLB_MAGIC or version != 2 or length != len(data):
        raise TextureError("not a glTF 2.0 GLB")
    size, kind = struct.unpack_from("<2I", data, 12)
    if kind != JSON_CHUNK:
        raise TextureError("the first GLB chunk is not JSON")
    gltf = json.loads(bytes(data[20 : 20 + size]).decode("utf-8"))
    binary, cursor = b"", 20 + size
    if cursor < len(data):
        size, kind = struct.unpack_from("<2I", data, cursor)
        if kind != BIN_CHUNK:
            raise TextureError("the second GLB chunk is not binary")
        binary = bytes(data[cursor + 8 : cursor + 8 + size])
    return gltf, binary


def join_glb(gltf, binary):
    text = json.dumps(gltf, ensure_ascii=False, separators=(",", ":"), allow_nan=False).encode("utf-8")
    text += b" " * (-len(text) % 4)
    chunks = struct.pack("<2I", len(text), JSON_CHUNK) + text
    if binary:
        binary = bytes(binary) + b"\0" * (-len(binary) % 4)
        chunks += struct.pack("<2I", len(binary), BIN_CHUNK) + binary
    return struct.pack("<3I", GLB_MAGIC, 2, 12 + len(chunks)) + chunks


def _view_bytes(gltf, binary, index):
    view = gltf["bufferViews"][index]
    start = view.get("byteOffset", 0)
    return binary[start : start + view["byteLength"]]


def replace_images(gltf, binary, payloads):
    """New binary chunk with other bytes for some images ({image index: (bytes, mimeType)}). The buffer views keep their
    order; each starts at a multiple of 4."""
    images = gltf.get("images", [])
    by_view = {images[index]["bufferView"]: payload for index, (payload, _) in payloads.items()}
    out = bytearray()
    for index, view in enumerate(gltf.get("bufferViews", [])):
        data = by_view.get(index)
        if data is None:
            data = _view_bytes(gltf, binary, index)
        out += b"\0" * (-len(out) % 4)
        view["byteOffset"] = len(out)
        view["byteLength"] = len(data)
        out += data
    for index, (_, mime) in payloads.items():
        images[index]["mimeType"] = mime
    if gltf.get("buffers"):
        gltf["buffers"][0]["byteLength"] = len(out)
    return bytes(out)


def linear_images(gltf):
    """Indices of the images that hold data, not colour: the sources of glTF normal textures."""
    textures = gltf.get("textures", [])
    result = set()
    for material in gltf.get("materials", []):
        info = material.get("normalTexture")
        if info and 0 <= info.get("index", -1) < len(textures) and "source" in textures[info["index"]]:
            result.add(textures[info["index"]]["source"])
    return result


def _set_extension(gltf, used):
    for key in ("extensionsUsed", "extensionsRequired"):
        names = [name for name in gltf.get(key, []) if name != EXTENSION] + ([EXTENSION] if used else [])
        if names:
            gltf[key] = names
        else:
            gltf.pop(key, None)


def encode_glb(data, encode_many):
    """GLB bytes with every PNG image replaced by a GH3TEX02 image. encode_many([(PNG bytes, linear)]) returns the
    GH3TEX02 bytes in the same order (the caller decodes PNG and may encode in parallel)."""
    gltf, binary = split_glb(data)
    linear = linear_images(gltf)
    jobs = [
        (index, bytes(_view_bytes(gltf, binary, image["bufferView"])))
        for index, image in enumerate(gltf.get("images", []))
        if "bufferView" in image and image.get("mimeType") != MIME_TYPE
    ]
    if not jobs:
        return data
    for index, payload in jobs:
        if not payload.startswith(PNG_SIGNATURE):
            raise TextureError(f"image {index} is not a PNG file")
    encoded = encode_many([(payload, index in linear) for index, payload in jobs])
    binary = replace_images(gltf, binary, {index: (tex, MIME_TYPE) for (index, _), tex in zip(jobs, encoded)})
    _set_extension(gltf, True)
    return join_glb(gltf, binary)


def decode_glb(data, keep_source=True):
    """GLB bytes with every GH3TEX02 image replaced by a PNG image of its first level (for glTF tools and Blender).
    keep_source: the PNG keeps the GH3TEX02 file in a SOURCE_CHUNK (encode_png_image uses it again)."""
    gltf, binary = split_glb(data)
    views = [
        (index, bytes(_view_bytes(gltf, binary, image["bufferView"])))
        for index, image in enumerate(gltf.get("images", []))
        if image.get("mimeType") == MIME_TYPE and "bufferView" in image
    ]
    if not views and EXTENSION not in gltf.get("extensionsUsed", []):
        return data
    payloads = {index: (png_bytes(decode(view), view if keep_source else None), "image/png") for index, view in views}
    binary = replace_images(gltf, binary, payloads)
    _set_extension(gltf, False)
    return join_glb(gltf, binary)


def glb_textures(data):
    """[(image index, image JSON, GH3TEX02 bytes or None for other images)] of a GLB."""
    gltf, binary = split_glb(data)
    return [
        (
            index,
            image,
            bytes(_view_bytes(gltf, binary, image["bufferView"])) if image.get("mimeType") == MIME_TYPE else None,
        )
        for index, image in enumerate(gltf.get("images", []))
        if "bufferView" in image
    ]


# Private PNG chunk (ancillary, safe to copy) with the GH3TEX02 file that a PNG was decoded from: an image that comes
# back unchanged from an editor gets its original texture again, without a second encoding loss.
SOURCE_CHUNK = b"ghTx"


def png_bytes(rgba, source=None):
    """PNG file of an RGBA image (H, W, 4) uint8, top row first: RGB when it is opaque, filter Sub, zlib level 1 (fast;
    the file is for editing, not for the package).
    source: GH3TEX02 bytes to keep in a SOURCE_CHUNK."""
    rgba = np.asarray(rgba, dtype=np.uint8)
    height, width = rgba.shape[:2]
    pixels = rgba[:, :, :3] if (rgba[:, :, 3] == 255).all() else rgba
    channels = pixels.shape[2]
    rows = pixels.reshape(height, width * channels)
    filtered = rows.copy()
    filtered[:, channels:] = rows[:, channels:] - rows[:, :-channels]
    raw = np.concatenate([np.ones((height, 1), dtype=np.uint8), filtered], 1).tobytes()

    def chunk(kind, payload):
        return (
            struct.pack(">I", len(payload))
            + kind
            + payload
            + struct.pack(">I", zlib.crc32(kind + payload) & 0xFFFFFFFF)
        )

    header = struct.pack(">2I5B", width, height, 8, 6 if channels == 4 else 2, 0, 0, 0)
    kept = chunk(SOURCE_CHUNK, zlib.compress(source, 1)) if source else b""
    return PNG_SIGNATURE + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(raw, 1)) + kept + chunk(b"IEND", b"")


def png_source(png):
    """The GH3TEX02 bytes of the SOURCE_CHUNK of a PNG file, or None."""
    cursor = len(PNG_SIGNATURE)
    if not png.startswith(PNG_SIGNATURE):
        return None
    while cursor + 8 <= len(png):
        length, kind = struct.unpack_from(">I4s", png, cursor)
        if kind == SOURCE_CHUNK:
            try:
                return zlib.decompress(png[cursor + 8 : cursor + 8 + length])
            except zlib.error:
                return None
        cursor += 12 + length
    return None


def unchanged_source(png, linear=False):
    """The GH3TEX02 file of the SOURCE_CHUNK when the PNG is still the file that png_bytes made from it (no decode of
    the PNG needed), else None."""
    source = png_source(png)
    if source is None:
        return None
    try:
        if bool(read_header(source)[4] & FLAG_LINEAR) == linear and png_bytes(decode(source), source) == png:
            return source
    except TextureError:
        pass
    return None


def encode_png_image(png, rgba, linear=False):
    """GH3TEX02 bytes of a PNG image with its decoded pixels (top row first): the texture of its SOURCE_CHUNK when the
    pixels and the linear flag are still the same, else a new encoding."""
    source = png_source(png)
    if source is not None:
        try:
            if bool(read_header(source)[4] & FLAG_LINEAR) == linear and np.array_equal(decode(source), rgba):
                return source
        except TextureError:
            pass
    return encode(rgba, linear)


def png_rgba(png):
    """RGBA (H, W, 4) uint8 of a PNG file, top row first. Needs Pillow (the Blender add-on decodes with Blender)."""
    from io import BytesIO

    from PIL import Image

    return np.asarray(Image.open(BytesIO(png)).convert("RGBA"))


def encode_pngs(items):
    """encode_glb callback without worker processes (Pillow decodes)."""
    return [encode_png_image(png, png_rgba(png), linear) for png, linear in items]
