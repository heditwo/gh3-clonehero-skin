import dnfile, sys

L, W = sys.argv[1:3]
used = {
    "Il2CppCloneHero.dll": [
        "Il2Cpp.BasePlayer",
        "Il2Cpp.GameManager",
        "Il2Cpp.GlobalVariables",
        "Il2Cpp.HighwayScroll",
        "Il2Cpp.ObjectPublicAbstractSealedInBoInObDoInSiDoInDoUnique",
        "Il2Cpp.ObjectPublicDoSiBoSiDoBoSiDoSiDoUnique",
        "Il2Cpp.ScoreManager",
        "Il2Cpp.SongEntry",
        "Il2Cpp.SpNeckRenderer",
        "Il2Cpp.StarProgress",
        "Il2Cpp.TrackFadeManager",
    ],
    "Il2CppStrikeCore.dll": [
        "Il2Cpp.ObjectPublicAbstractDoBoDoInBoObDoInSiBoUnique",
        "Il2Cpp.ObjectPublicObInObDoSiDoUIInBoInUnique",
    ],
}


def load(path):
    pe = dnfile.dnPE(path)
    md = pe.net.mdtables
    out = {}
    for t in md.TypeDef.rows:
        n = f"{t.TypeNamespace}.{t.TypeName}"
        f = [str(x.row.Name) for x in t.FieldList]
        m = [str(x.row.Name) for x in t.MethodList]
        data = [x for x in f if x.startswith("NativeFieldInfoPtr_")]
        meth = sorted(x for x in m if not x.startswith(("get_", "set_")))
        out[n] = (data, meth)
    return out


for asm, types in used.items():
    a, b = load(f"{L}/{asm}"), load(f"{W}/{asm}")
    for t in types:
        if t not in b:
            print(f"{t}: MISSING on Windows")
            continue
        print(
            f"{t}: data fields same order={a[t][0] == b[t][0]} ({len(a[t][0])}/{len(b[t][0])}), method name set same={a[t][1] == b[t][1]} ({len(a[t][1])}/{len(b[t][1])})"
        )
