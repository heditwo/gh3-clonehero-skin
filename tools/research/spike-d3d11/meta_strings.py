import struct, sys


def names(p):
    d = open(p, "rb").read()
    sanity, ver = struct.unpack_from("<Ii", d)
    so, ss = struct.unpack_from("<ii", d, 24)
    return ver, d[so : so + ss].split(b"\0")


lv, l = names(sys.argv[1])
wv, w = names(sys.argv[2])
print("versions", lv, wv, "counts", len(l), len(w), "identical table", l == w)
ls, ws = set(l), set(w)
print("only linux", len(ls - ws), "only windows", len(ws - ls))
print(sorted(ls - ws)[:40])
print(sorted(ws - ls)[:40])
