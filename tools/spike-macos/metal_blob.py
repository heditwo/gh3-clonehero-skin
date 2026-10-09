"""Dump the Metal program blobs of one shader in a Unity serialized file.

Usage: metal_blob.py <serialized file> <shader name> [output dir]
Prints, for each blob entry: the Unity program header, the Metal header (magic 0xf00dcafe),
the entry point name, the MSL source size, and the trailing bytes. With an output dir, writes the MSL sources.
"""
import struct
import sys
from pathlib import Path

import lz4.block
import UnityPy
from UnityPy.enums import ShaderCompilerPlatform, ShaderGpuProgramType

METAL = 14


def hexdump(data, base=0):
    for i in range(0, len(data), 16):
        chunk = data[i:i + 16]
        print(f'    {base + i:5d}  {chunk.hex(" ")}')


def main():
    path, shader_name = sys.argv[1:3]
    out = Path(sys.argv[3]) if len(sys.argv) > 3 else None
    env = UnityPy.load(path)
    shader = next(obj.read_typetree() for asset in env.assets for obj in asset.objects.values()
                  if obj.type.name == 'Shader' and obj.read_typetree()['m_ParsedForm']['m_Name'] == shader_name)
    print('platforms', [ShaderCompilerPlatform(p).name for p in shader['platforms']])
    index = shader['platforms'].index(METAL)
    offset = shader['offsets'][index][0]
    length = shader['compressedLengths'][index][0]
    raw = lz4.block.decompress(bytes(shader['compressedBlob'])[offset:offset + length], shader['decompressedLengths'][index][0])
    count, = struct.unpack_from('<I', raw)
    print('entries', count, 'decompressed bytes', len(raw))
    for entry in range(count):
        start, size, _ = struct.unpack_from('<3I', raw, 4 + 12 * entry)
        blob = raw[start:start + size]
        version, kind = struct.unpack_from('<2I', blob)
        if kind == 2:
            print(f'entry {entry}: parameters, {size} bytes (version {version})')
            hexdump(blob[:64])
            continue
        print(f'entry {entry}: program {ShaderGpuProgramType(kind).name}, {size} bytes (version {version})')
        metal = blob.find(struct.pack('<I', 0xf00dcafe))
        payload, = struct.unpack_from('<I', blob, metal - 4)
        print(f'  unity header {metal - 4} bytes, payload length {payload}')
        hexdump(blob[:metal])
        source = blob.find(b'#include <metal_stdlib>')
        print(f'  metal header {source - metal} bytes (with the entry point name)')
        hexdump(blob[metal:source], metal)
        end = blob.index(b'\0', source)
        print(f'  MSL source {end - source} bytes; trailer {size - end} bytes')
        hexdump(blob[end:], end)
        if out:
            out.mkdir(parents=True, exist_ok=True)
            (out / f'{shader_name.replace("/", "_")}-{entry}.metal').write_bytes(blob[source:end])


if __name__ == '__main__':
    main()
