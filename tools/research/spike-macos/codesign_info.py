"""Print architectures, code signature flags, team ID, and entitlements of Mach-O files (no macOS tools needed)."""
import struct
import sys

CPU = {0x7: 'i386', 0x01000007: 'x86_64', 0x0100000c: 'arm64'}
FLAGS = {0x2: 'adhoc', 0x10000: 'runtime (hardened)', 0x200: 'library-validation', 0x800: 'restrict'}


def slices(data):
    magic, = struct.unpack_from('>I', data)
    if magic == 0xcafebabe:
        count, = struct.unpack_from('>I', data, 4)
        for i in range(count):
            cpu, _, offset, size, _ = struct.unpack_from('>5I', data, 8 + 20 * i)
            yield CPU.get(cpu, hex(cpu)), data[offset:offset + size]
    else:
        cpu, = struct.unpack_from('<I', data, 4)
        yield CPU.get(cpu, hex(cpu)), data


def signature(macho):
    magic, = struct.unpack_from('<I', macho)
    header = 32 if magic == 0xfeedfacf else 28
    ncmds, = struct.unpack_from('<I', macho, 16)
    at = header
    for _ in range(ncmds):
        cmd, size = struct.unpack_from('<2I', macho, at)
        if cmd == 0x1d:
            offset, length = struct.unpack_from('<2I', macho, at + 8)
            return macho[offset:offset + length]
        at += size
    return None


def describe(blob):
    out = {}
    _, _, count = struct.unpack_from('>3I', blob)
    for i in range(count):
        _, offset = struct.unpack_from('>2I', blob, 12 + 8 * i)
        magic, length = struct.unpack_from('>2I', blob, offset)
        body = blob[offset:offset + length]
        if magic == 0xfade0c02:
            version, flags = struct.unpack_from('>2I', body, 8)
            ident_offset, = struct.unpack_from('>I', body, 20)
            out['identifier'] = body[ident_offset:body.index(b'\0', ident_offset)].decode()
            out['flags'] = [name for bit, name in FLAGS.items() if flags & bit] or [hex(flags)]
            if version >= 0x20200:
                team_offset, = struct.unpack_from('>I', body, 48)
                if team_offset:
                    out['team'] = body[team_offset:body.index(b'\0', team_offset)].decode()
        elif magic == 0xfade7171:
            text = body[8:].decode()
            out['entitlements'] = [line.strip()[5:-6] for line in text.splitlines() if line.strip().startswith('<key>')]
        elif magic == 0xfade0b01:
            out['cms signed'] = length > 8
    return out


for path in sys.argv[1:]:
    data = open(path, 'rb').read()
    for arch, macho in slices(data):
        blob = signature(macho)
        print(path.split('/Contents/')[-1], arch, describe(blob) if blob else 'unsigned')
