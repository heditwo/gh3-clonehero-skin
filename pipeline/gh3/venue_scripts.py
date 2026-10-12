"""Read literal placement data from GH3 venue scripts (compressed QB script sections)."""

import re
import struct

from pipeline.gh3.formats import QB_HEADER_SIZE, find_array, key, word

QB_SECTION_SCRIPT = 0x00200700
# Script header: checksum, size, compressed size; then the LZSS data (when the sizes differ).
SCRIPT_HEADER_SIZE = 12
MAX_SCRIPT_SIZE = 10000000
# LZSS of the QB scripts (the same as src/Gh3/Gh3ScriptCode.cs): a 4096-byte ring that starts with spaces and
# writes from 4078; each flag bit selects a literal byte (1) or a 12-bit ring offset and a length of 3 to 18 (0).
RING_SIZE = 4096
RING_START = 4078
MIN_MATCH = 3
# Script bytecode: name token (0x16) with the checksum of "Pos", "=" (0x07), then a vector literal (0x1E) or an
# opening parenthesis (0x0E), an argument token (0x4B), and the name token of a global array.
POS_LITERAL = b"\x16" + struct.pack("<I", key("Pos")) + b"\x07\x1e"
POS_ARRAY_REFERENCE = b"\x16" + struct.pack("<I", key("Pos")) + b"\x07\x0e\x4b\x16"
# The token after the array name of POS_ARRAY_REFERENCE.
ARRAY_INDEX_TOKEN = 5


def script_bytes(data, name):
    """The bytecode of the QB script with this name, decompressed; None when the file has no such script."""
    at = data.find(struct.pack(">II", QB_SECTION_SCRIPT, key(name)), QB_HEADER_SIZE)
    if at < 0:
        return None
    at = word(data, at + 12)
    size, compressed_size = word(data, at + 4), word(data, at + 8)
    if size > MAX_SCRIPT_SIZE or at + SCRIPT_HEADER_SIZE + compressed_size > len(data):
        raise ValueError("Invalid QB script size")
    source = data[at + SCRIPT_HEADER_SIZE : at + SCRIPT_HEADER_SIZE + compressed_size]
    if size == compressed_size:
        return source
    return _decompress(source, size)


def _decompress(source, size):
    ring = bytearray(b" " * RING_START + b"\0" * (RING_SIZE - RING_START))
    write = RING_START
    output = bytearray()
    cursor = 0
    while cursor < len(source):
        flags = source[cursor]
        cursor += 1
        for bit in range(8):
            if cursor == len(source):
                break
            if flags & (1 << bit):
                chunk = bytes([source[cursor]])
                cursor += 1
                output.extend(chunk)
                ring[write] = chunk[0]
                write = (write + 1) % RING_SIZE
            else:
                if cursor + 2 > len(source):
                    raise ValueError("Truncated QB script reference")
                lo, hi = source[cursor : cursor + 2]
                cursor += 2
                start = lo | ((hi & 0xF0) << 4)
                for i in range((hi & 15) + MIN_MATCH):
                    byte = ring[(start + i) % RING_SIZE]
                    output.append(byte)
                    ring[write] = byte
                    write = (write + 1) % RING_SIZE
            if len(output) > size:
                raise ValueError("QB script exceeds its size")
    if len(output) != size:
        raise ValueError("QB script size differs")
    return bytes(output)


def literal_positions(data, qbs=()):
    """The Pos vectors of a script: the literal ones and the rows of the global arrays it names (first seen order)."""
    positions = [
        struct.unpack_from("<3f", data, m.end())
        for m in re.finditer(re.escape(POS_LITERAL), data)
        if m.end() + 12 <= len(data)
    ]
    for match in re.finditer(re.escape(POS_ARRAY_REFERENCE), data):
        if match.end() + 5 > len(data) or data[match.end() + 4] != ARRAY_INDEX_TOKEN:
            continue
        name = struct.unpack_from("<I", data, match.end())[0]
        rows = next((rows for qb in qbs if (rows := find_array(qb, name)) is not None), [])
        if any(not isinstance(p, tuple) or len(p) != 3 for p in rows):
            raise ValueError("Particle placement array must contain vectors")
        positions.extend(rows)
    return list(dict.fromkeys(positions))
