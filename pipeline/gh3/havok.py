"""Read the self-described records in GH3's 32-bit Havok 4.6 ragdolls (packfiles with their hkClass records)."""

import struct

# Packfile header: magic, layout rules (4-byte pointers, little-endian, reuse padding, empty base class), file version
# (+20), Havok version text (+40), and three 48-byte section headers (+84): base and the six fixup and data offsets.
PACKFILE_MAGIC = bytes.fromhex("57e0e05710c0c010")
PACKFILE_LAYOUT = bytes([4, 1, 0, 1])
PACKFILE_LAYOUT_AT, PACKFILE_VERSION_AT, PACKFILE_CONTENTS_AT = 16, 20, 40
PACKFILE_VERSION = 3
HAVOK_VERSION = b"Havok-4.6.0-r1\0"
PACKFILE_HEADER_SIZE = 208
SECTION_HEADERS_AT, SECTION_HEADER_SIZE, SECTION_COUNT = 84, 48, 3
# Fixup tables: local (source, destination), global (source, section, destination), and virtual (object, section,
# class name) records; unused records start with 0xFFFFFFFF.
LOCAL_FIXUP_SIZE, GLOBAL_FIXUP_SIZE, VIRTUAL_FIXUP_SIZE = 8, 12, 12
UNUSED_FIXUP = 0xFFFFFFFF
# hkClass records: parent (+4), members (+24), member count (+28); hkClassMember records are 20 bytes: name (+0),
# class (+4), type, subtype, array size, flags, and offset (+12).
CLASS_PARENT, CLASS_MEMBERS, CLASS_MEMBER_COUNT = 4, 24, 28
MEMBER_SIZE, MEMBER_CLASS, MEMBER_TYPE = 20, 4, 12
MAX_CLASS_MEMBERS = 256
MAX_STRING = 1024
MAX_CLASS_DEPTH = 32
# hkClassMember::Type values of the Havok 4.6 SDK.
TYPE_POINTER, TYPE_STRUCT, TYPE_CSTRING = 20, 25, 29
TYPE_FORMATS = {
    1: "B",  # TYPE_BOOL
    3: "b",  # TYPE_INT8
    4: "B",  # TYPE_UINT8
    6: "H",  # TYPE_UINT16
    8: "I",  # TYPE_UINT32
    11: "f",  # TYPE_REAL
    12: "4f",  # TYPE_VECTOR4
    13: "4f",  # TYPE_QUATERNION
    18: "16f",  # TYPE_TRANSFORM
}
POINTER_SIZE = 4


class Ragdoll:
    def __init__(self, data):
        self.data = data
        if (
            len(data) < PACKFILE_HEADER_SIZE
            or data[:8] != PACKFILE_MAGIC
            or data[PACKFILE_LAYOUT_AT : PACKFILE_LAYOUT_AT + 4] != PACKFILE_LAYOUT
        ):
            raise ValueError("Expected a little-endian 32-bit Havok ragdoll")
        if (
            not data[PACKFILE_CONTENTS_AT:].startswith(HAVOK_VERSION)
            or struct.unpack_from("<I", data, PACKFILE_VERSION_AT)[0] != PACKFILE_VERSION
        ):
            raise ValueError("Unsupported Havok ragdoll version")
        self.sections = [
            struct.unpack_from("<7I", data, SECTION_HEADERS_AT + i * SECTION_HEADER_SIZE) for i in range(SECTION_COUNT)
        ]
        self.refs, self.types, self.classes = {}, {}, {}
        for section, (base, local, glob, virt, exports, imports, end) in enumerate(self.sections):
            if (
                not PACKFILE_HEADER_SIZE <= base <= len(data)
                or not 0 <= local <= glob <= virt <= exports <= imports <= end <= len(data) - base
            ):
                raise ValueError("Invalid Havok section bounds")
            self._read_fixups(section, local, glob, virt, exports)
        self._read_classes()

    def _read_fixups(self, section, local, glob, virt, exports):
        for offset in range(local, glob, LOCAL_FIXUP_SIZE):
            if self.unpack((section, offset), "I")[0] == UNUSED_FIXUP:
                continue
            a, b = self.unpack((section, offset), "2I")
            if a != UNUSED_FIXUP:
                self.refs[(section, a)] = (section, b)
        for offset in range(glob, virt, GLOBAL_FIXUP_SIZE):
            if self.unpack((section, offset), "I")[0] == UNUSED_FIXUP:
                continue
            a, target, b = self.unpack((section, offset), "3I")
            if a != UNUSED_FIXUP:
                self.refs[(section, a)] = (target, b)
        for offset in range(virt, exports, VIRTUAL_FIXUP_SIZE):
            if self.unpack((section, offset), "I")[0] == UNUSED_FIXUP:
                continue
            a, target, b = self.unpack((section, offset), "3I")
            if a != UNUSED_FIXUP:
                self.types[(section, a)] = self.string((target, b))

    def _read_classes(self):
        names = {p: self.string(self.refs[p]) for p, kind in self.types.items() if kind == "hkClass"}
        for (section, offset), name in names.items():
            record = {"parent": names.get(self.refs.get((section, offset + CLASS_PARENT))), "fields": {}}
            count = self.unpack((section, offset + CLASS_MEMBER_COUNT), "I")[0]
            if count > MAX_CLASS_MEMBERS:
                raise ValueError("Invalid Havok field count")
            if count:
                ms, mo = self.refs[(section, offset + CLASS_MEMBERS)]
                for i in range(count):
                    at = mo + i * MEMBER_SIZE
                    field = self.string(self.refs[(ms, at)])
                    kind, subtype, length, flags, position = self.unpack((ms, at + MEMBER_TYPE), "2B3H")
                    member_class = names.get(self.refs.get((ms, at + MEMBER_CLASS)))
                    record["fields"][field] = (kind, length, position, member_class)
            self.classes[name] = record

    def unpack(self, address, fmt):
        section, offset = address
        size = struct.calcsize("<" + fmt)
        if not 0 <= section < SECTION_COUNT or not 0 <= offset <= self.sections[section][-1] - size:
            raise ValueError("Havok address outside section")
        return struct.unpack_from("<" + fmt, self.data, self.sections[section][0] + offset)

    def string(self, address):
        self.unpack(address, "B")
        section, offset = address
        start = self.sections[section][0] + offset
        end = self.data.find(
            b"\0", start, min(start + MAX_STRING, self.sections[section][0] + self.sections[section][-1])
        )
        if end < 0:
            raise ValueError("Unterminated Havok string")
        return self.data[start:end].decode("ascii")

    def field(self, address, cls, name):
        """The value of a member of the object at `address` of class `cls` (or of one of its parents)."""
        for _ in range(MAX_CLASS_DEPTH):
            if cls not in self.classes:
                raise ValueError(f"Missing Havok field {name}")
            record = self.classes[cls]
            if name in record["fields"]:
                break
            cls = record["parent"]
        else:
            raise ValueError("Havok class parent cycle")
        kind, length, offset, member_class = record["fields"][name]
        at = address[0], address[1] + offset
        if kind == TYPE_CSTRING:
            return self.string(self.refs[at]) if at in self.refs else ""
        if kind == TYPE_STRUCT:
            return at, member_class
        if kind == TYPE_POINTER:
            if length:
                return [self.refs.get((at[0], at[1] + POINTER_SIZE * i)) for i in range(length)]
            return self.refs.get(at)
        fmt = TYPE_FORMATS.get(kind)
        if fmt is None:
            raise ValueError(f"Unsupported Havok field type {kind}")
        value = self.unpack(at, fmt)
        return value if len(value) > 1 else value[0]
