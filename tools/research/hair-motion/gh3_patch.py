"""Patch the GH3 test copy: bot plays, player 1 plays CHAR, only CHAR stays a guitarist, and (if CAM=1) every
<zone> camera with LookAtBone looks from POS at BONE. Originals: ~/.cache/rockgod-gh3/orig/."""

import os
import re
import struct
import sys

sys.path.insert(0, str(__import__("pathlib").Path(__file__).resolve().parents[3]))
from pipeline.gh3.formats import key, qb_text, values, word

G = "/home/hedi/games/gh3-matched-capture/Guitar Hero III/DATA/"
O = "/home/hedi/.cache/rockgod-gh3/orig/"
KEEP, POS, FOV, BONE = sys.argv[1], tuple(float(x) for x in sys.argv[2].split(",")), float(sys.argv[3]), sys.argv[4]
ZONE = os.environ.get("ZONE", "z_artdeco")
CAM = os.environ.get("CAM", "1") == "1"
SO = tuple(float(x) for x in os.environ.get("SO", "0,0").split(","))
stats = {"cams": 0, "orbit": 0, "types": 0}
SPEC = os.environ.get("ZONES") or f"{ZONE}:{sys.argv[2]}"
for zone_spec in SPEC.split(";"):
    ZONE, pos_text = zone_spec.split(":")
    POS = tuple(float(x) for x in pos_text.split(","))
    d = bytearray(open(O + ZONE + ".pak.xen", "rb").read())
    if CAM:
        off = 0
        qbs = []
        while off + 32 <= len(d):
            kind, rel, size, _, name, fn, _, flags = values(d, off, "8I")
            if kind == key(".last"):
                break
            if kind == key(".qb"):
                qbs.append((off + rel, size))
            off += 192 if flags & 0x20 else 32
        for QO, QL in qbs:
            q = d[QO : QO + QL]

            def items(o):
                o = word(q, o + 4)
                out = []
                while o:
                    kind, name, value, nxt = values(q, o, "4I")
                    out.append((kind, name, value, o))
                    o = nxt
                return out

            def patch_struct(s, top=False, arr=""):
                its = items(s)
                names = {n: (k, v, o) for k, n, v, o in its}
                if key("LookAtBone") in names and key("Pos") in names:
                    struct.pack_into(">3f", q, names[key("Pos")][1] + 4, *POS)
                    if key("FOV") in names:
                        struct.pack_into(">f", q, names[key("FOV")][2] + 8, FOV)
                    struct.pack_into(">I", q, names[key("LookAtBone")][2] + 8, key(BONE))
                    if key("LookAt") in names:
                        struct.pack_into(">I", q, names[key("LookAt")][2] + 8, key("GUITARIST"))
                    if key("LockTo") in names:
                        struct.pack_into(">I", q, names[key("LockTo")][2] + 8, key("World"))
                    if key("ScreenOffset") in names:
                        struct.pack_into(">2f", q, names[key("ScreenOffset")][1] + 4, *SO)
                    stats["cams"] += 1
                if key("orbitangle") in names:
                    struct.pack_into(">I", q, names[key("orbitangle")][2] + 8, 0)
                    stats["orbit"] += 1
                if top and arr == ZONE + "_cameras" and key("Type") in names:
                    struct.pack_into(">I", q, names[key("Type")][2] + 8, key("motion_none"))
                    stats["types"] += 1
                for k, n, v, o in its:
                    if k == 0x008A0000:
                        patch_struct(v)

            for mm in re.finditer(re.escape(struct.pack(">I", 0x00200C00)), q):
                o = mm.start()
                if o % 4:
                    continue
                nm = word(q, o + 4)
                a = word(q, o + 12)
                try:
                    kind, count = values(q, a, "II")
                except ValueError:
                    continue
                if kind != 0x10A00:
                    continue
                lst = word(q, a + 8) if count > 1 else a + 8
                for i in range(count):
                    patch_struct(word(q, lst + 4 * i), True, ZONE + "_cameras" if key(ZONE + "_cameras") == nm else "")
            d[QO : QO + QL] = q
        open(G + "ZONES/" + ZONE + ".pak.xen", "wb").write(d)

p = bytearray(open(O + "qb.pab.xen", "rb").read())
for o in (0x5026C, 0x508DC):
    assert word(p, o) == key("bot_play")
    struct.pack_into(">I", p, o + 4, 1)
for o in (0x50750, 0x50DB0) if KEEP != "NONE" else ():
    assert word(p, o) == key("character_id") and word(p, o + 4) == key("Axel")
    struct.pack_into(">I", p, o + 4, key(KEEP))
PO = 661824
q2 = p[PO : PO + 28868]
m = q2.find(struct.pack(">II", 0x00200C00, key("Musician_Profiles")))
a = word(q2, m + 12)
kind, count = values(q2, a, "II")
lst = word(q2, a + 8)
off = 0


def items2(o):
    o = word(q2, o + 4)
    out = {}
    while o:
        kind, name, value, nxt = values(q2, o, "4I")
        out[name] = (kind, value, o)
        o = nxt
    return out


SWAP = os.environ.get("SWAP")
if SWAP:
    a_, b_ = SWAP.split(",")
    found = {}
    for i in range(count):
        f = items2(word(q2, lst + 4 * i))
        found[qb_text(q2, f[key("Name")][1], False)] = f[key("Name")][2]
    va, vb = word(q2, found[a_] + 8), word(q2, found[b_] + 8)
    struct.pack_into(">I", q2, found[a_] + 8, vb)
    struct.pack_into(">I", q2, found[b_] + 8, va)
    stats["swapped"] = SWAP
for i in range(count):
    f = items2(word(q2, lst + 4 * i))
    if (
        KEEP != "NONE"
        and f.get(key("Type"), (0, 0))[1] == key("GUITARIST")
        and qb_text(q2, f[key("Name")][1], False) != KEEP
    ):
        struct.pack_into(">I", q2, f[key("Type")][2] + 8, key("vocalist"))
        off += 1
p[PO : PO + 28868] = q2
stats["profiles_disabled"] = off
TRANSPLANT = os.environ.get("TRANSPLANT")
if TRANSPLANT:
    # Give the free profile DST every field of the secret profile SRC, and give DST's appearance rows SRC's model files.
    src_name, dst_name = TRANSPLANT.split(",")
    q2 = p[PO : PO + 28868]
    rows = {}
    for i in range(count):
        f = items2(word(q2, lst + 4 * i))
        rows[qb_text(q2, f[key("Name")][1], False)] = f
    S = rows[src_name]
    dsts = (
        [
            n
            for n, f in rows.items()
            if n != src_name and not n.startswith("BoneMan") and f.get(key("Type"), (0, 0))[1] == key("GUITARIST")
        ]
        if dst_name == "ALL"
        else [dst_name]
    )
    stats["targets"] = dsts
    for dn in dsts:
        Dd = rows[dn]
        copied = []
        for n, (kind, value, o) in Dd.items():
            if n in (key("Name"), key("Type")):
                continue
            if n in S and kind in (0x008D0000, 0x00810000, 0x009A0000):
                struct.pack_into(">I", q2, o + 8, S[n][1])
                copied.append(n)
            elif n in S and kind == 0x008A0000:
                # Pointer items must not be shared (the loader relocates them in place): copy the inline fields of the struct.
                so = word(q2, S[n][1] + 4)
                do = word(q2, value + 4)
                while so and do:
                    ks, ns, vs, nxs = values(q2, so, "4I")
                    kd, nd, vd, nxd = values(q2, do, "4I")
                    assert ns == nd and ks == kd == 0x008D0000
                    struct.pack_into(">I", q2, do + 8, vs)
                    so, do = nxs, nxd
                copied.append(n)
            elif n == key("skeleton2"):
                struct.pack_into(">I", q2, o + 8, S[key("skeleton")][1])
                copied.append(n)
            elif n == key("scale"):
                struct.pack_into(">3f", q2, value + 4, 1.0, 1.0, 1.0)
                copied.append(n)
        stats["transplanted"] = len(copied)
        stats["kept"] = [hex(n) for n in Dd if n not in copied and n != key("Name")]
    p[PO : PO + 28868] = q2
    pak = open("/home/hedi/games/gh3-test-tools/game/DATA/PAK/qb.pak.xen", "rb").read()
    L = len(pak)
    comb = pak + bytes(p)
    off = 0
    while True:
        kind, rel, size, _, name, fn, _, flags = values(comb, off, "8I")
        if fn == 0x9E406955:
            AO = off + rel - L
            AL = size
            break
        off += 192 if flags & 0x20 else 32
    a3 = p[AO : AO + AL]
    mk = a3.find(struct.pack(">II", 0x00200C00, 0xBA554DCF))
    arr = word(a3, mk + 12)
    _, n_ = values(a3, arr, "II")
    lst3 = word(a3, arr + 8)

    def items3(o):
        o = word(a3, o + 4)
        out = {}
        while o:
            kind, name, value, nxt = values(a3, o, "4I")
            out[name] = (kind, value, o)
            o = nxt
        return out

    table = [items3(word(a3, lst3 + 4 * i)) for i in range(n_)]
    src_desc = S[0xBA554DCF][1]
    src_desc = word(q2, word(q2, src_desc + 4) + 8)
    src_row = next(r for r in table if r[0x4BB2084E][1] == src_desc)
    changed = 0
    for r in table if os.environ.get("APPEAR", "1") == "1" else []:
        path = a3[r[0x1E90C5A9][1] : r[0x1E90C5A9][1] + 80].split(b"\0")[0].lower()
        if r is not src_row and any(b"\\" + dn.lower().encode() in path for dn in dsts):
            for f in (0x1E90C5A9, 0xF2C9AC8B, 0xE2A0D600, 0x7E4C3BD7):
                struct.pack_into(">I", a3, r[f][2] + 8, src_row[f][1])
            changed += 1
    DESCSWAP = os.environ.get("DESCSWAP")
    if DESCSWAP:
        # Swap the inline desc ids of two appearance rows: the game then loads row B's model files for A's desc and back.
        da, db = (key(x) for x in DESCSWAP.split(","))
        ra = next(r for r in table if r[0x4BB2084E][1] == da)
        rb = next(r for r in table if r[0x4BB2084E][1] == db)
        struct.pack_into(">I", a3, ra[0x4BB2084E][2] + 8, db)
        struct.pack_into(">I", a3, rb[0x4BB2084E][2] + 8, da)
        stats["descswap"] = DESCSWAP
    p[AO : AO + AL] = a3
    stats["appearance_rows"] = changed
UNSECRET = os.environ.get("UNSECRET")
if UNSECRET:
    # The store lists secret characters by profile name (field 0xf8b75377); a profile that is not listed is free.
    import re as _re

    pat = struct.pack(">II", 0xF8B75377, key(UNSECRET))
    n_un = 0
    for mm in _re.finditer(_re.escape(pat), p):
        o = mm.start()
        if o % 4 == 0 and word(p, o - 4) == 0x008D0000:
            struct.pack_into(">I", p, o + 4, key("none"))
            n_un += 1
    stats["unsecret"] = n_un
if os.environ.get("UNLOCK") == "1":
    # setup_character_tags (new saves only): SetGlobalTags Secret_Characters[3] (the God of Rock) params = {unlocked = 1}, three times.
    qbf = open("/home/hedi/.cache/rockgod-gh3/qb/ba999f63.qb", "rb").read()
    old_sct = qbf[1104 : 1104 + 112]
    at = p.find(old_sct)
    assert at > 0 and p.find(old_sct, at + 1) < 0
    lz = open("/home/hedi/.cache/rockgod-gh3/tools/sct-unlock.lz", "rb").read()
    assert len(lz) <= 100
    struct.pack_into(">I", p, at + 8, len(lz))
    p[at + 12 : at + 12 + len(lz)] = lz
    stats["unlock"] = len(lz)
LEVEL = os.environ.get("LEVEL")
if LEVEL or os.environ.get("SONG"):
    pak = open("/home/hedi/games/gh3-test-tools/game/DATA/PAK/qb.pak.xen", "rb").read()
    L = len(pak)
    comb = pak + bytes(p)
    off = 0
    nlev = 0
    while True:
        kind, rel, size, _, name, fn, _, flags = values(comb, off, "8I")
        if kind == key(".last"):
            break
        s0 = off + rel - L
        off += 192 if flags & 0x20 else 32
        if s0 < 0:
            continue
        qq = p[s0 : s0 + size]
        for listname in ["GH3_Career_Songs", "GH3_General_Songs"]:
            mk = qq.find(struct.pack(">II", 0x00200A00, key(listname)))
            if mk < 0 or mk % 4:
                continue
            top = word(qq, mk + 12)
            o = word(qq, top + 4)
            while o:
                kind, name, value, nxt = values(qq, o, "4I")
                if kind == 0x008A0000:
                    o2 = word(qq, value + 4)
                    while o2:
                        k2, n2, v2, nx2 = values(qq, o2, "4I")
                        if n2 == 0x651533EC and LEVEL:
                            struct.pack_into(">I", qq, o2 + 8, key(LEVEL))
                            nlev += 1
                        if n2 == key("songs") and os.environ.get("SONG") and listname == "GH3_General_Songs":
                            akind, acount = values(qq, v2, "II")
                            stats.setdefault("songkinds", []).append(hex(akind))
                            lstp = word(qq, v2 + 8) if acount > 1 else v2 + 8
                            for si in range(acount):
                                if word(qq, lstp + 4 * si) == key(os.environ.get("SONG_FROM", "slowride")):
                                    struct.pack_into(">I", qq, lstp + 4 * si, key(os.environ["SONG"]))
                                    stats["songs"] = stats.get("songs", 0) + 1
                        o2 = nx2
                o = nxt
        p[s0 : s0 + size] = qq
    stats["levels"] = nlev

SONGNAME = os.environ.get("SONGNAME")
if SONGNAME:
    src_song, dst_song = SONGNAME.split(":")
    pak = open("/home/hedi/games/gh3-test-tools/game/DATA/PAK/qb.pak.xen", "rb").read()
    L = len(pak)
    comb = pak + bytes(p)
    off = 0
    while True:
        kind, rel, size, _, name, fn, _, flags = values(comb, off, "8I")
        if kind == key(".last"):
            break
        s0 = off + rel - L
        off += 192 if flags & 0x20 else 32
        if s0 < 0:
            continue
        qq = p[s0 : s0 + size]
        mk = qq.find(struct.pack(">II", 0x00200A00, key("permanent_songlist_props")))
        if mk < 0 or mk % 4:
            continue
        top = word(qq, mk + 12)
        o = word(qq, top + 4)
        while o:
            kind, name, value, nxt = values(qq, o, "4I")
            if name == key(src_song):
                o2 = word(qq, value + 4)
                while o2:
                    k2, n2, v2, nx2 = values(qq, o2, "4I")
                    if n2 == key("name"):
                        old = qq[v2 : v2 + len(src_song) + 1]
                        assert old == src_song.encode() + b"\0", old
                        qq[v2 : v2 + len(dst_song) + 1] = dst_song.encode() + b"\0"
                        stats["songname"] = SONGNAME
                    o2 = nx2
            o = nxt
        p[s0 : s0 + size] = qq
if os.environ.get("PERFORMANCE") == "1":
    # Cheat_PerformanceMode (guitar.qb global, -1): 1 hides the highway and the HUD.
    at = 333588
    assert (
        word(p, at) == 0x00200100 and word(p, at + 4) == key("Cheat_PerformanceMode") and word(p, at + 12) == 0xFFFFFFFF
    )
    struct.pack_into(">I", p, at + 12, 1)
    stats["performance"] = 1
open(os.environ.get("OUT", G + "PAK/qb.pab.xen"), "wb").write(p)
print(stats)
