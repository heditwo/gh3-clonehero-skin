"""Read the self-described records in GH3's 32-bit Havok 4.6 ragdolls."""

import struct


class Ragdoll:
    def __init__(self, data):
        self.data = data
        if len(data) < 208 or data[:8] != bytes.fromhex("57e0e05710c0c010") or data[16:20] != bytes([4, 1, 0, 1]):
            raise ValueError("Expected a little-endian 32-bit Havok ragdoll")
        if not data[40:].startswith(b"Havok-4.6.0-r1\0") or struct.unpack_from("<I", data, 20)[0] != 3:
            raise ValueError("Unsupported Havok ragdoll version")
        self.sections = [struct.unpack_from("<7I", data, 84 + i * 48) for i in range(3)]
        self.refs, self.types, self.classes = {}, {}, {}
        for section, (base, local, glob, virt, exports, imports, end) in enumerate(self.sections):
            if (
                not 208 <= base <= len(data)
                or not 0 <= local <= glob <= virt <= exports <= imports <= end <= len(data) - base
            ):
                raise ValueError("Invalid Havok section bounds")
            for offset in range(local, glob, 8):
                if self.unpack((section, offset), "I")[0] == 0xFFFFFFFF:
                    continue
                a, b = self.unpack((section, offset), "2I")
                if a != 0xFFFFFFFF:
                    self.refs[(section, a)] = (section, b)
            for offset in range(glob, virt, 12):
                if self.unpack((section, offset), "I")[0] == 0xFFFFFFFF:
                    continue
                a, target, b = self.unpack((section, offset), "3I")
                if a != 0xFFFFFFFF:
                    self.refs[(section, a)] = (target, b)
            for offset in range(virt, exports, 12):
                if self.unpack((section, offset), "I")[0] == 0xFFFFFFFF:
                    continue
                a, target, b = self.unpack((section, offset), "3I")
                if a != 0xFFFFFFFF:
                    self.types[(section, a)] = self.string((target, b))
        names = {p: self.string(self.refs[p]) for p, kind in self.types.items() if kind == "hkClass"}
        for (section, offset), name in names.items():
            record = {"parent": names.get(self.refs.get((section, offset + 4))), "fields": {}}
            count = self.unpack((section, offset + 28), "I")[0]
            if count > 256:
                raise ValueError("Invalid Havok field count")
            if count:
                ms, mo = self.refs[(section, offset + 24)]
                for i in range(count):
                    at = mo + i * 20
                    field = self.string(self.refs[(ms, at)])
                    kind, subtype, length, flags, position = self.unpack((ms, at + 12), "2B3H")
                    record["fields"][field] = (kind, length, position, names.get(self.refs.get((ms, at + 4))))
            self.classes[name] = record

    def unpack(self, address, fmt):
        section, offset = address
        size = struct.calcsize("<" + fmt)
        if not 0 <= section < 3 or not 0 <= offset <= self.sections[section][-1] - size:
            raise ValueError("Havok address outside section")
        return struct.unpack_from("<" + fmt, self.data, self.sections[section][0] + offset)

    def string(self, address):
        self.unpack(address, "B")
        section, offset = address
        start = self.sections[section][0] + offset
        end = self.data.find(b"\0", start, min(start + 1024, self.sections[section][0] + self.sections[section][-1]))
        if end < 0:
            raise ValueError("Unterminated Havok string")
        return self.data[start:end].decode("ascii")

    def field(self, address, cls, name):
        for _ in range(32):
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
        if kind == 29:
            return self.string(self.refs[at]) if at in self.refs else ""
        if kind == 25:
            return at, member_class
        if kind == 20:
            if length:
                return [self.refs.get((at[0], at[1] + 4 * i)) for i in range(length)]
            return self.refs.get(at)
        fmt = {1: "B", 3: "b", 4: "B", 6: "H", 8: "I", 11: "f", 12: "4f", 13: "4f", 18: "16f"}.get(kind)
        if fmt is None:
            raise ValueError(f"Unsupported Havok field type {kind}")
        value = self.unpack(at, fmt)
        return value if len(value) > 1 else value[0]
