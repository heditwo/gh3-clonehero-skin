import dnfile, sys
from pathlib import Path
from collections import defaultdict

mod, d = sys.argv[1], Path(sys.argv[2])
md = dnfile.dnPE(mod).net.mdtables
asms = {i + 1: str(r.Name) for i, r in enumerate(md.AssemblyRef.rows)}
refs = defaultdict(set)
for m in md.MemberRef.rows:
    p = m.Class
    if p.table is None or p.table.name != "TypeRef":
        continue
    t = p.row
    s = t.ResolutionScope
    if s.table is None or s.table.name != "AssemblyRef":
        continue
    a = asms[s.row_index]
    if (d / f"{a}.dll").exists():
        refs[(a, f"{t.TypeNamespace}.{t.TypeName}")].add(str(m.Name))
cache = {}
total = missing = 0
for (a, tn), names in sorted(refs.items()):
    if a not in cache:
        mdt = dnfile.dnPE(str(d / f"{a}.dll")).net.mdtables
        cache[a] = {
            f"{t.TypeNamespace}.{t.TypeName}": {str(x.row.Name) for x in (t.MethodList or [])}
            | {str(x.row.Name) for x in (t.FieldList or [])}
            for t in mdt.TypeDef.rows
        }
    have = cache[a].get(tn)
    for n in names:
        total += 1
        if have is None or n not in have:
            missing += 1
            print("MISSING", a, tn, n)
print("member refs to interop assemblies", total, "missing", missing, "assemblies", sorted({a for a, _ in refs}))
