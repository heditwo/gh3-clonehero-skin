import sys, UnityPy, lz4.block, struct
from UnityPy.enums import ShaderCompilerPlatform, ShaderGpuProgramType
env = UnityPy.load(sys.argv[1])
a = env.assets[0]
print('unity', a.unity_version)
for obj in a.objects.values():
    if obj.type.name != 'Shader': continue
    t = obj.read_typetree()
    if t['m_ParsedForm']['m_Name'] != 'Hidden/BlitCopy': continue
    print([ShaderCompilerPlatform(p).name for p in t['platforms']])
    for sub in t['m_ParsedForm']['m_SubShaders']:
        for p in sub['m_Passes']:
            for n in ('progVertex','progFragment'):
                print(n, [[ (ShaderGpuProgramType(x['m_GpuProgramType']).name, x['m_BlobIndex']) for x in progs] for progs in p[n]['m_PlayerSubPrograms']])
    for i,plat in enumerate(t['platforms']):
        off=t['offsets'][i][0]; ln=t['compressedLengths'][i][0]
        raw=lz4.block.decompress(bytes(t['compressedBlob'])[off:off+ln], t['decompressedLengths'][i][0])
        cnt,=struct.unpack_from('<I',raw)
        print(ShaderCompilerPlatform(plat).name,'entries',cnt,'raw',len(raw))
        for k in range(cnt):
            o,s,seg=struct.unpack_from('<3I',raw,4+12*k)
            print('  entry',k,o,s,seg, raw[o:o+8].hex())
        open(f'/tmp/opencode/spike-d3d11/blit-{plat}.bin','wb').write(raw)
