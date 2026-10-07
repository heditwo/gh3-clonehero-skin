"""Small, bounded readers for GH3 PC PAK files and QB data arrays."""

import struct
from zlib import crc32


def key(text):
    return ~crc32(text.lower().encode()) & 0xFFFFFFFF


def values(data, offset, fmt):
    if offset < 0 or offset + struct.calcsize(">" + fmt) > len(data):
        raise ValueError(f"Data outside file at {offset}")
    return struct.unpack_from(">" + fmt, data, offset)


def word(data, offset):
    return values(data, offset, "I")[0]


def pak_entries(data):
    offset = 0
    while offset + 32 <= len(data):
        kind, relative, size, _, name, _, _, flags = values(data, offset, "8I")
        if kind == key(".last"):
            return
        start = offset + relative
        if start < offset + 32 or start + size > len(data):
            raise ValueError("Invalid PAK entry")
        yield kind, name, data[start:start + size]
        offset += 192 if flags & 0x20 else 32
    raise ValueError("PAK has no end marker")


def node_prefixes(data, arrays):
    array, count = values(data, 0, "2I")
    if array not in arrays or count > 100000 or 8 + count*12 > len(data): raise ValueError("Invalid node prefix table")
    nodes = arrays[array]; start = 8 + count*12; result = {}
    for index in range(count):
        name, offset, length = values(data, 8 + index*12, "3I")
        if name in result or offset % 2 or start + offset + length*2 > len(data): raise ValueError("Invalid node prefix range")
        indices = values(data, start + offset, f"{length}H")
        if any(i >= len(nodes) for i in indices): raise ValueError("Node prefix index is outside its array")
        result[name] = [nodes[i][key("Name")] for i in indices]
    return result


def qb_array(data, offset, depth=0):
    if depth > 12:
        raise ValueError("QB array nesting is too deep")
    kind, count = values(data, offset, "II")
    if kind == 0x10000:
        return []
    if count > 1000000:
        raise ValueError("QB array is too large")
    start = word(data, offset + 8) if count > 1 else offset + 8
    if kind == 0x10200:
        return list(values(data, start, f"{count}f"))
    entries = values(data, start, f"{count}I")
    if kind in (0x10100, 0x10D00):
        return list(entries)
    if kind in (0x10500, 0x10600):
        return [values(data, entry + 4, "2f" if kind == 0x10500 else "3f") for entry in entries]
    if kind == 0x10C00:
        return [qb_array(data, entry, depth + 1) for entry in entries]
    if kind == 0x10A00:
        return [qb_struct(data, entry, depth + 1) for entry in entries]
    raise ValueError(f"Unsupported QB array type {kind:08x}")


def qb_struct(data, offset, depth=0):
    if depth > 12 or word(data, offset) != 0x100:
        raise ValueError("Invalid QB struct")
    result = {0: []}
    offset = word(data, offset + 4)
    visited = set()
    while offset:
        if offset in visited or len(visited) > 10000:
            raise ValueError("Invalid QB struct links")
        visited.add(offset)
        kind, name, value, following = values(data, offset, "4I")
        if kind == 0x00850000:
            value = values(data, value + 4, "2f")
        elif kind == 0x00860000:
            value = values(data, value + 4, "3f")
        elif kind == 0x00820000:
            value = values(data, offset + 8, "f")[0]
        elif kind == 0x00830000:
            end = data.find(b"\0", value)
            if value >= len(data) or end < 0:
                raise ValueError("Invalid QB string")
            value = data[value:end].decode("latin1")
        elif kind == 0x00840000:
            end = value
            while values(data, end, "H")[0] != 0:
                end += 2
            value = data[value:end].decode("utf-16-be")
        elif kind == 0x008C0000:
            value = qb_array(data, value, depth + 1)
        elif kind == 0x008A0000:
            value = qb_struct(data, value, depth + 1)
        elif kind not in (0x008D0000, 0x00810000, 0x009A0000):
            raise ValueError(f"Unsupported QB struct type {kind:08x}")
        if name == 0:
            result[0].append(value)
        else:
            result[name] = value
        offset = following
    return result


def find_array(data, name):
    marker = struct.pack(">II", 0x00200C00, key(name) if isinstance(name, str) else name)
    offset = data.find(marker, 28)
    if offset < 0:
        return None
    if offset % 4:
        raise ValueError("Unaligned QB section")
    return qb_array(data, word(data, offset + 12))


def find_struct(data, name):
    marker = struct.pack(">II", 0x00200A00, key(name) if isinstance(name, str) else name)
    offset = data.find(marker, 28)
    if offset < 0:
        return None
    if offset % 4:
        raise ValueError("Unaligned QB section")
    return qb_struct(data, word(data, offset + 12))
