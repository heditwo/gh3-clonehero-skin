"""Bounded readers for GH3 PC PAK archives and QB data (big-endian), and the GH3 name checksum.

The C# readers in src/Native/Gh3Pak.cs and src/Native/Gh3Qb.cs read the same layouts.
"""

import struct
from zlib import crc32

# PAK entry header: type, data offset (relative to the header), size, 4 bytes, name checksum, 8 bytes, flags (eight
# big-endian words). With FLAG_FULL_NAME a 160-byte full name follows the header.
PAK_ENTRY_SIZE = 32
PAK_ENTRY_WITH_NAME_SIZE = 192
PAK_FLAG_FULL_NAME = 0x20
PAK_END_TYPE = ".last"

# QB file: a 28-byte header whose third word is this tag, then the sections.
QB_HEADER_SIZE = 28
QB_FILE_TAG = bytes.fromhex("1c080204")
QB_SECTION_ARRAY = 0x00200C00
QB_SECTION_STRUCT = 0x00200A00

# QB array headers are 0x0001TT00 and struct items 0x00TT0000 with bit 23 set; TT is the value type
# (1 integer, 2 float, 3 string, 4 wide string, 5 pair, 6 vector, 10 struct, 12 array, 13 checksum).
QB_ARRAY_EMPTY = 0x00010000
QB_ARRAY_INTEGER = 0x00010100
QB_ARRAY_FLOAT = 0x00010200
QB_ARRAY_STRING = 0x00010300
QB_ARRAY_WIDE_STRING = 0x00010400
QB_ARRAY_PAIR = 0x00010500
QB_ARRAY_VECTOR = 0x00010600
QB_ARRAY_STRUCT = 0x00010A00
QB_ARRAY_ARRAY = 0x00010C00
QB_ARRAY_CHECKSUM = 0x00010D00
QB_STRUCT_HEADER = 0x100
QB_ITEM_INTEGER = 0x00810000
QB_ITEM_FLOAT = 0x00820000
QB_ITEM_STRING = 0x00830000
QB_ITEM_WIDE_STRING = 0x00840000
QB_ITEM_PAIR = 0x00850000
QB_ITEM_VECTOR = 0x00860000
QB_ITEM_STRUCT = 0x008A0000
QB_ITEM_ARRAY = 0x008C0000
QB_ITEM_CHECKSUM = 0x008D0000
# Type 0x1A: a one-word value that the C# reader also keeps as a raw word.
QB_ITEM_TYPE_1A = 0x009A0000

# Reader limits against damaged files; the GH3 files stay far below them.
MAX_QB_DEPTH = 12
MAX_QB_ARRAY_ITEMS = 1000000
MAX_QB_STRUCT_ITEMS = 10000
MAX_NODE_PREFIXES = 100000

# Node prefix table (.qs): array checksum and row count, then rows of (prefix checksum, index offset, index count).
NODE_PREFIX_HEADER_SIZE = 8
NODE_PREFIX_ROW_SIZE = 12


def key(text):
    """The GH3 checksum of a name: the inverted CRC-32 of the lower-case text."""
    return ~crc32(text.lower().encode()) & 0xFFFFFFFF


def is_qb_file(data):
    return len(data) > QB_HEADER_SIZE and data[8:12] == QB_FILE_TAG


def values(data, offset, fmt):
    if offset < 0 or offset + struct.calcsize(">" + fmt) > len(data):
        raise ValueError(f"Data outside file at {offset}")
    return struct.unpack_from(">" + fmt, data, offset)


def word(data, offset):
    return values(data, offset, "I")[0]


def pak_entries(data):
    """(type checksum, name checksum, bytes) of each PAK entry, up to the end marker."""
    offset = 0
    while offset + PAK_ENTRY_SIZE <= len(data):
        kind, relative, size, _, name, _, _, flags = values(data, offset, "8I")
        if kind == key(PAK_END_TYPE):
            return
        start = offset + relative
        if start < offset + PAK_ENTRY_SIZE or start + size > len(data):
            raise ValueError("Invalid PAK entry")
        yield kind, name, data[start : start + size]
        offset += PAK_ENTRY_WITH_NAME_SIZE if flags & PAK_FLAG_FULL_NAME else PAK_ENTRY_SIZE
    raise ValueError("PAK has no end marker")


def node_prefixes(data, arrays):
    """The node name groups of a .qs prefix table: {prefix checksum: [node names]}."""
    array, count = values(data, 0, "2I")
    start = NODE_PREFIX_HEADER_SIZE + count * NODE_PREFIX_ROW_SIZE
    if array not in arrays or count > MAX_NODE_PREFIXES or start > len(data):
        raise ValueError("Invalid node prefix table")
    nodes = arrays[array]
    result = {}
    for index in range(count):
        name, offset, length = values(data, NODE_PREFIX_HEADER_SIZE + index * NODE_PREFIX_ROW_SIZE, "3I")
        if name in result or offset % 2 or start + offset + length * 2 > len(data):
            raise ValueError("Invalid node prefix range")
        indices = values(data, start + offset, f"{length}H")
        if any(i >= len(nodes) for i in indices):
            raise ValueError("Node prefix index is outside its array")
        result[name] = [nodes[i][key("Name")] for i in indices]
    return result


def qb_array(data, offset, depth=0):
    if depth > MAX_QB_DEPTH:
        raise ValueError("QB array nesting is too deep")
    kind, count = values(data, offset, "II")
    if kind == QB_ARRAY_EMPTY:
        return []
    if count > MAX_QB_ARRAY_ITEMS:
        raise ValueError("QB array is too large")
    start = word(data, offset + 8) if count > 1 else offset + 8
    if kind == QB_ARRAY_FLOAT:
        return list(values(data, start, f"{count}f"))
    entries = values(data, start, f"{count}I")
    if kind in (QB_ARRAY_INTEGER, QB_ARRAY_CHECKSUM):
        return list(entries)
    if kind in (QB_ARRAY_PAIR, QB_ARRAY_VECTOR):
        return [values(data, entry + 4, "2f" if kind == QB_ARRAY_PAIR else "3f") for entry in entries]
    if kind in (QB_ARRAY_STRING, QB_ARRAY_WIDE_STRING):
        return [qb_text(data, entry, kind == QB_ARRAY_WIDE_STRING) for entry in entries]
    if kind == QB_ARRAY_ARRAY:
        return [qb_array(data, entry, depth + 1) for entry in entries]
    if kind == QB_ARRAY_STRUCT:
        return [qb_struct(data, entry, depth + 1) for entry in entries]
    raise ValueError(f"Unsupported QB array type {kind:08x}")


def qb_text(data, offset, wide):
    if not wide:
        end = data.find(b"\0", offset)
        if offset >= len(data) or end < 0:
            raise ValueError("Invalid QB string")
        return data[offset:end].decode("latin1")
    end = offset
    while values(data, end, "H")[0] != 0:
        end += 2
    return data[offset:end].decode("utf-16-be")


def qb_struct(data, offset, depth=0):
    """A QB struct as {name checksum: value}; unnamed items go to the list at key 0."""
    if depth > MAX_QB_DEPTH or word(data, offset) != QB_STRUCT_HEADER:
        raise ValueError("Invalid QB struct")
    result = {0: []}
    offset = word(data, offset + 4)
    visited = set()
    while offset:
        if offset in visited or len(visited) > MAX_QB_STRUCT_ITEMS:
            raise ValueError("Invalid QB struct links")
        visited.add(offset)
        kind, name, value, following = values(data, offset, "4I")
        if kind == QB_ITEM_PAIR:
            value = values(data, value + 4, "2f")
        elif kind == QB_ITEM_VECTOR:
            value = values(data, value + 4, "3f")
        elif kind == QB_ITEM_FLOAT:
            value = values(data, offset + 8, "f")[0]
        elif kind in (QB_ITEM_STRING, QB_ITEM_WIDE_STRING):
            value = qb_text(data, value, kind == QB_ITEM_WIDE_STRING)
        elif kind == QB_ITEM_ARRAY:
            value = qb_array(data, value, depth + 1)
        elif kind == QB_ITEM_STRUCT:
            value = qb_struct(data, value, depth + 1)
        elif kind not in (QB_ITEM_CHECKSUM, QB_ITEM_INTEGER, QB_ITEM_TYPE_1A):
            raise ValueError(f"Unsupported QB struct type {kind:08x}")
        if name == 0:
            result[0].append(value)
        else:
            result[name] = value
        offset = following
    return result


def _find_section(data, section_type, name):
    marker = struct.pack(">II", section_type, key(name) if isinstance(name, str) else name)
    offset = data.find(marker, QB_HEADER_SIZE)
    if offset < 0:
        return None
    if offset % 4:
        raise ValueError("Unaligned QB section")
    return word(data, offset + 12)


def find_array(data, name):
    """The top-level QB array with this name (or checksum), or None."""
    start = _find_section(data, QB_SECTION_ARRAY, name)
    return None if start is None else qb_array(data, start)


def find_struct(data, name):
    """The top-level QB struct with this name (or checksum), or None."""
    start = _find_section(data, QB_SECTION_STRUCT, name)
    return None if start is None else qb_struct(data, start)
