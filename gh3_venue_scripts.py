"""Read literal placement data from GH3 venue script sections."""

import re
import struct

from gh3_formats import find_array, key, word


def script_bytes(data, name):
    at = data.find(struct.pack(">II", 0x00200700, key(name)), 28)
    if at < 0: return None
    at = word(data, at+12)
    size, compressed_size = word(data, at+4), word(data, at+8)
    if size > 10000000 or at+12+compressed_size > len(data): raise ValueError("Invalid QB script size")
    source = data[at+12:at+12+compressed_size]
    if size == compressed_size: return source
    ring = bytearray(b" "*4078+b"\0"*18); write = 4078
    output = bytearray(); cursor = 0
    while cursor < len(source):
        flags = source[cursor]; cursor += 1
        for bit in range(8):
            if cursor == len(source): break
            if flags & (1 << bit):
                chunk = bytes([source[cursor]]); cursor += 1
                output.extend(chunk); ring[write] = chunk[0]; write = (write+1) % 4096
            else:
                if cursor+2 > len(source): raise ValueError("Truncated QB script reference")
                lo, hi = source[cursor:cursor+2]; cursor += 2
                start = lo | ((hi & 0xf0) << 4)
                for i in range((hi & 15)+3):
                    byte = ring[(start+i) % 4096]
                    output.append(byte); ring[write] = byte; write = (write+1) % 4096
            if len(output) > size: raise ValueError("QB script exceeds its size")
    if len(output) != size: raise ValueError("QB script size differs")
    return bytes(output)


def literal_positions(data, qbs=()):
    marker = b"\x16"+struct.pack("<I", key("Pos"))+b"\x07\x1e"
    positions = [struct.unpack_from("<3f", data, m.end()) for m in re.finditer(re.escape(marker), data) if m.end()+12 <= len(data)]
    reference = b"\x16"+struct.pack("<I", key("Pos"))+b"\x07\x0e\x4b\x16"
    for match in re.finditer(re.escape(reference), data):
        if match.end()+5 > len(data) or data[match.end()+4] != 5: continue
        name = struct.unpack_from("<I",data,match.end())[0]
        rows = next((rows for qb in qbs if (rows := find_array(qb,name)) is not None), [])
        if any(not isinstance(p,tuple) or len(p) != 3 for p in rows): raise ValueError("Particle placement array must contain vectors")
        positions.extend(rows)
    return list(dict.fromkeys(positions))
