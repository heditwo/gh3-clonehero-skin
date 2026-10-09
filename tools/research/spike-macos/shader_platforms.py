"""Count shader compiler platforms and GPU program types over all serialized files of a Clone Hero data folder."""
import sys
from collections import Counter
from pathlib import Path

import UnityPy
from UnityPy.enums import ShaderCompilerPlatform, ShaderGpuProgramType

platforms, programs, shaders = Counter(), Counter(), 0
data = Path(sys.argv[1])
files = [p for p in data.rglob('*') if p.is_file() and not p.suffix in ('.resS', '.resource', '.json', '.dat', '.opus', '.yml', '.info', '.config')]
for path in files:
    try:
        env = UnityPy.load(str(path))
    except Exception:
        continue
    for asset in env.assets:
        for obj in asset.objects.values():
            if obj.type.name != 'Shader':
                continue
            tree = obj.read_typetree()
            shaders += 1
            platforms.update(ShaderCompilerPlatform(p).name for p in tree['platforms'])
            for sub in tree['m_ParsedForm']['m_SubShaders']:
                for shader_pass in sub['m_Passes']:
                    for name in ('progVertex', 'progFragment'):
                        for group in shader_pass[name]['m_PlayerSubPrograms']:
                            programs.update(ShaderGpuProgramType(p['m_GpuProgramType']).name for p in group)
print('shaders', shaders)
print('platforms', dict(platforms))
print('gpu program types', dict(programs))
