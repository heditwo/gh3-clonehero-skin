import struct, sys
def cstr(d, o): return d[o:d.index(b'\0', o)].decode()
for p in sys.argv[1:]:
    d = open(p, 'rb').read()
    magic, h, one, size, n = d[:4], d[4:20], *struct.unpack_from('<III', d, 20)
    offs = struct.unpack_from(f'<{n}I', d, 32)
    chunks = {d[o:o+4].decode(): (o+8, struct.unpack_from('<I', d, o+4)[0]) for o in offs}
    print(p, magic, 'hash', h.hex(), 'size', size, len(d), 'chunks', {k: v[1] for k, v in chunks.items()})
    o, _ = chunks['RDEF']; r = d[o:]
    ncb, cbo, nb, bo = struct.unpack_from('<4I', r)
    for i in range(nb):
        name, typ, ret, dim, samples, point, count, flags = struct.unpack_from('<8I', r, bo + 32*i)
        print('   bind', cstr(r, name), 'type', typ, 'slot', point)
    for i in range(ncb):
        name, nv, vo, sz, fl, ty = struct.unpack_from('<6I', r, cbo + 24*i)
        vars_ = []
        for k in range(nv):
            vn, so, vs, vf, vt, dv = struct.unpack_from('<6I', r, vo + 24*k)
            vars_.append(f'{cstr(r, vn)}@{so}')
        print('   cbuffer', cstr(r, name), 'size', sz, 'vars', nv, vars_[:6], '...' if nv > 6 else '')
