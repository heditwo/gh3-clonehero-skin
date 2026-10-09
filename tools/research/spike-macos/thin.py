"""List the slices of a universal Mach-O file, or write one slice: thin.py <file> [arch output]."""
import struct
import sys

CPU = {0x7: 'i386', 0x01000007: 'x86_64', 0x0100000c: 'arm64'}
data = open(sys.argv[1], 'rb').read()
count, = struct.unpack_from('>I', data, 4)
for i in range(count):
    cpu, _, offset, size, align = struct.unpack_from('>5I', data, 8 + 20 * i)
    print(CPU.get(cpu, hex(cpu)), f'offset=0x{offset:x}', f'size={size}')
    if len(sys.argv) > 3 and CPU.get(cpu) == sys.argv[2]:
        open(sys.argv[3], 'wb').write(data[offset:offset + size])
