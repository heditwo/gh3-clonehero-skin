"""Count Texture2D formats over all serialized files of a Clone Hero data folder."""
import sys
from collections import Counter
from pathlib import Path

import UnityPy
from UnityPy.enums import TextureFormat

formats = Counter()
for path in Path(sys.argv[1]).rglob('*'):
    if not path.is_file() or path.suffix in ('.resS', '.resource', '.json', '.dat', '.opus', '.yml', '.info', '.config'):
        continue
    try:
        env = UnityPy.load(str(path))
    except Exception:
        continue
    for asset in env.assets:
        for obj in asset.objects.values():
            if obj.type.name == 'Texture2D':
                formats[TextureFormat(obj.read().m_TextureFormat).name] += 1
for name, count in formats.most_common():
    print(f'{name}\t{count}')
