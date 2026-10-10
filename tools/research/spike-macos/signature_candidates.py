"""For each obfuscated game method that the mod calls, list the methods with the same signature in another build.

The obfuscator renames and reorders methods in each build, so an indexed interop name such as
`Method_Public_Void_Texture2D_0` is not stable. When a signature has exactly one candidate in both builds,
the mapping is unambiguous. Otherwise a binding map must pick the method by other means (code analysis or a runtime check).

Usage: signature_candidates.py <Gh3Skin.dll> <interop dir A> <interop dir B> [label A] [label B]
"""

import re
import sys
from pathlib import Path

import dnfile

from interop_compare import mod_references


def methods(path):
    md = dnfile.dnPE(str(path)).net.mdtables
    return {
        f"{t.TypeNamespace}.{t.TypeName}": [str(m.row.Name) for m in t.MethodList] if t.MethodList else []
        for t in md.TypeDef.rows
    }


def key(name):
    return re.sub(r"_\d+$", "", name.replace("_PDM", ""))


def main():
    mod, a_dir, b_dir = sys.argv[1:4]
    a_label, b_label = (sys.argv[4], sys.argv[5]) if len(sys.argv) > 5 else ("A", "B")
    a_dir, b_dir = Path(a_dir), Path(b_dir)
    loaded = {}
    unique = ambiguous = 0
    for (assembly, type_name), names in sorted(mod_references(mod).items()):
        for folder in (a_dir, b_dir):
            if (folder, assembly) not in loaded:
                loaded[folder, assembly] = methods(folder / f"{assembly}.dll")
        a = loaded[a_dir, assembly].get(type_name, [])
        b = loaded[b_dir, assembly].get(type_name, [])
        for name in sorted(n for n in names if n.startswith("Method_")):
            a_candidates = [n for n in a if key(n) == key(name)]
            b_candidates = [n for n in b if key(n) == key(name)]
            verdict = "unique" if len(a_candidates) == len(b_candidates) == 1 else "AMBIGUOUS"
            unique += verdict == "unique"
            ambiguous += verdict != "unique"
            print(
                f"{type_name.split('.')[-1]}.{name}: {verdict}; {a_label} {len(a_candidates)}, "
                f"{b_label} {len(b_candidates)} {b_candidates}"
            )
    print(f"\nobfuscated methods called by the mod: unique signature={unique}, ambiguous={ambiguous}")


if __name__ == "__main__":
    main()
