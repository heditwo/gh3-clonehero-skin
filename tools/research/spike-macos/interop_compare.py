"""Compare two sets of Il2CppInterop assemblies at the members that the mod uses.

Usage: interop_compare.py <Gh3Skin.dll> <interop dir A> <interop dir B> [label A] [label B]
"""

import sys
from collections import defaultdict
from pathlib import Path

import dnfile

GAME_ASSEMBLIES = ("Il2CppCloneHero", "Il2CppStrikeCore")


def mod_references(path):
    md = dnfile.dnPE(path).net.mdtables
    assemblies = {i + 1: str(row.Name) for i, row in enumerate(md.AssemblyRef.rows)}
    refs = defaultdict(set)
    for member in md.MemberRef.rows:
        parent = member.Class
        if parent.table is None or parent.table.name != "TypeRef":
            continue
        typeref = parent.row
        scope = typeref.ResolutionScope
        if scope.table is None or scope.table.name != "AssemblyRef":
            continue
        assembly = assemblies[scope.row_index]
        if assembly in GAME_ASSEMBLIES and str(member.Name) != ".ctor":
            refs[(assembly, f"{typeref.TypeNamespace}.{typeref.TypeName}")].add(str(member.Name))
    return refs


def load(path):
    md = dnfile.dnPE(str(path)).net.mdtables
    types = {}
    for row in md.TypeDef.rows:
        fields = [str(f.row.Name) for f in row.FieldList] if row.FieldList else []
        methods = [str(m.row.Name) for m in row.MethodList] if row.MethodList else []
        types[f"{row.TypeNamespace}.{row.TypeName}"] = (fields, methods)
    return types


def main():
    mod, a_dir, b_dir = sys.argv[1:4]
    a_label, b_label = (sys.argv[4:6] + ["A", "B"])[:2] if len(sys.argv) > 4 else ("A", "B")
    a_dir, b_dir = Path(a_dir), Path(b_dir)

    for assembly in GAME_ASSEMBLIES:
        a, b = load(a_dir / f"{assembly}.dll"), load(b_dir / f"{assembly}.dll")
        common = set(a) & set(b)
        same = sum(1 for name in common if a[name] == b[name])
        print(f"{assembly}: types {a_label}={len(a)} {b_label}={len(b)} common={len(common)} identical members={same}")

    refs = mod_references(mod)
    cache = {}
    missing_total = 0
    print(f"\nmod references: {sum(len(v) for v in refs.values())} members in {len(refs)} types")
    for (assembly, type_name), names in sorted(refs.items()):
        for label, folder in ((a_label, a_dir), (b_label, b_dir)):
            cache.setdefault(folder, {}).setdefault(assembly, None)
            if cache[folder][assembly] is None:
                cache[folder][assembly] = load(folder / f"{assembly}.dll")
        a_type = cache[a_dir][assembly].get(type_name)
        b_type = cache[b_dir][assembly].get(type_name)
        if a_type is None or b_type is None:
            print(f"{type_name}: MISSING in {a_label if a_type is None else b_label}")
            missing_total += len(names)
            continue
        missing = sorted(n for n in names if n not in b_type[1] and n not in b_type[0])
        missing_total += len(missing)
        a_data = [f for f in a_type[0] if f.startswith("NativeFieldInfoPtr_")]
        b_data = [f for f in b_type[0] if f.startswith("NativeFieldInfoPtr_")]
        print(
            f"{type_name}: data fields same order={a_data == b_data} ({len(a_data)}/{len(b_data)}), "
            f"method list identical={a_type[1] == b_type[1]} ({len(a_type[1])}/{len(b_type[1])}), "
            f"mod names missing in {b_label}={missing}"
        )
    print(f"\nmod member names missing in {b_label}: {missing_total}")


if __name__ == "__main__":
    main()
