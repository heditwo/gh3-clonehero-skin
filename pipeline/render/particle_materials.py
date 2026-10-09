"""Export particle shader sort fields from the original PC material library."""

import argparse
import struct
from hashlib import sha256
from pathlib import Path

from pipeline.paths import GH3_DATA, LIBRARY
from pipeline.stagepack.pack import load_json, write_json


def read_techniques(data):
    if data[:8] != b'MATL\0\0\0\x02':
        raise ValueError('Unsupported material library')
    count = struct.unpack_from('>I', data, 8)[0]
    if count > 4096:
        raise ValueError('Too many material records')
    base = (20+count*4+15) & ~15
    records = {}
    for index in range(count):
        offset = struct.unpack_from('>I', data, 20+index*4)[0]
        row = struct.unpack_from('>10I', data, base+offset)
        name_at = base+row[1]
        name = data[name_at:data.index(b'\0', name_at)].decode()
        if not name.startswith('ParticleQuickMaterial'): continue
        if row[6] != 1: raise ValueError('Particle shader has multiple techniques')
        if name in records: raise ValueError('Duplicate particle shader: '+name)
        records[name] = dict(sortId=struct.unpack_from('>I', data, base+row[7]+0x64)[0] & 255, flags=row[3])
    result = {}
    for lit in (True, False):
        rows = []
        features = ('diffusemap', 'specularmap', 'normalmap', 'specularlighting', 'softedge', 'staticshadow', 'dynamicshadow') if lit else (
            'diffusemap', 'softedge', 'staticshadow', 'dynamicshadow')
        for variant in range(1 << len(features)):
            name = 'ParticleQuickMaterial'+('' if lit else 'NoLighting')+''.join('_'+name for index, name in enumerate(features)
                if variant & (1 << (len(features)-index-1)))
            rows.append(dict(name=name, **records.pop(name)))
        result['lit' if lit else 'unlit'] = rows
    if records: raise ValueError('Unknown particle shader variants')
    return dict(version=1, **result)


def convert(source, output):
    result = read_techniques(source.read_bytes())
    write_json(output, result)
    path = output.parent.parent/'library.json'
    if path.is_file():
        manifest = load_json(path)
        manifest['files'][str(output.relative_to(path.parent))] = sha256(output.read_bytes()).hexdigest()
        write_json(path, manifest)
    print(f'{output}: 144 particle material techniques')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(prog="python -m pipeline.render.particle_materials", description=__doc__)
    parser.add_argument('--source', type=Path, default=GH3_DATA/'FXFILES/MaterialLibrary.bin.xen')
    parser.add_argument('--output', type=Path, default=LIBRARY/'render/particle-materials.json')
    args = parser.parse_args()
    convert(args.source, args.output)
