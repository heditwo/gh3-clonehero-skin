"""Compare GH3 hair (memory run) with the mod solver driven by the same head path.
usage: replay.py <run> <profile> <gx> <gz> <bench out...>   (first call with 'head' as bench out to write head.bin)"""

import sys, numpy as np
from hairlib import *

run, pid, gx, gz = sys.argv[1], sys.argv[2], float(sys.argv[3]), float(sys.argv[4])
prof = Profile(pid)
t, W = gh3_run(run, prof, gx, gz)
if sys.argv[5] == "head":
    head_file(t, W[prof.head], f"{run}/head.bin")
    print("head path", len(t), "rows", t[-1], "s")
    sys.exit()
g = metrics(t, W, prof)
tt = np.arange(2, t[-1] - 0.5, 1 / 120)


def summary(name, res, tsrc):
    rows = []
    for c, r in res.items():
        tip = resample(tsrc, r["tip"], tt)
        d = tip - np.median(tip, 0)
        rows.append((c, r, tip, np.sqrt((d**2).sum(1))))
    return rows


def spectrum_peak(x, fs=120):
    x = x - x.mean()
    f = np.fft.rfftfreq(len(x), 1 / fs)
    p = np.abs(np.fft.rfft(x * np.hanning(len(x)))) ** 2
    band = (f > 0.5) & (f < 10)
    return f[band][np.argmax(p[band])]


G = summary("gh3", g, t)
others = []
for path in sys.argv[5:]:
    tb, Wb = bench(path, prof)
    others.append((path.split("/")[-1], summary(path, metrics(tb, Wb, prof), tb)))
print("%-6s %-28s" % ("bone", "GH3: tip dev p50/p95 cm, cone p50/p95") + "".join("| %-34s" % n for n, _ in others))
tot = {"gh3": []}
for k, (c, r, tip, dev) in enumerate(G):
    line = "%-6s %5.1f %5.1f  %5.1f %5.1f      " % (
        prof.bones[c] & 0xFFFF,
        100 * np.median(dev),
        100 * np.percentile(dev, 95),
        np.median(r.get("cone", [0])),
        np.percentile(r.get("cone", [0]), 95),
    )
    tot["gh3"].append(dev)
    for n, rows in others:
        c2, r2, tip2, dev2 = rows[k]
        err = np.sqrt(((tip2 - tip) ** 2).sum(1))
        line += "| %5.1f %5.1f  %5.1f %5.1f err %4.1f " % (
            100 * np.median(dev2),
            100 * np.percentile(dev2, 95),
            np.median(r2.get("cone", [0])),
            np.percentile(r2.get("cone", [0]), 95),
            100 * np.median(err),
        )
        tot.setdefault(n, []).append(dev2)
    print(line)
print(
    "all joints tip deviation from median (cm): "
    + "  ".join(
        "%s p50 %.1f p95 %.1f" % (n, 100 * np.median(np.concatenate(v)), 100 * np.percentile(np.concatenate(v), 95))
        for n, v in tot.items()
    )
)

# tip velocity spectra (same head input): band RMS of the tip speed in the head frame, all joints pooled
bands = [(0.3, 1.5), (1.5, 3), (3, 6), (6, 12), (12, 30)]


def band_rms(rows):
    acc = np.zeros(len(bands))
    for c, r, tip, dev in rows:
        v = np.diff(tip, axis=0) * 120
        f = np.fft.rfftfreq(len(v), 1 / 120)
        P = (np.abs(np.fft.rfft(v - v.mean(0), axis=0)) ** 2).sum(1) / len(v) ** 2
        for k, (a, b) in enumerate(bands):
            acc[k] += P[(f >= a) & (f < b)].sum() * 2
    return np.sqrt(acc)


ref = band_rms(G)
print("tip speed RMS by band (m/s):", "  ".join(f"{a}-{b} Hz" for a, b in bands))
print("%-34s" % "gh3", " ".join("%7.3f" % v for v in ref))
for n, rows in others:
    v = band_rms(rows)
    print("%-34s" % n, " ".join("%7.3f" % x for x in v), "  ratio", " ".join("%4.2f" % x for x in v / ref))


def joint_band(rows, a=3, b=12):
    out = []
    for c, r, tip, dev in rows:
        v = np.diff(tip, axis=0) * 120
        f = np.fft.rfftfreq(len(v), 1 / 120)
        P = (np.abs(np.fft.rfft(v - v.mean(0), axis=0)) ** 2).sum(1) / len(v) ** 2
        out.append(np.sqrt(P[(f >= a) & (f < b)].sum() * 2))
    return np.array(out)


jg = joint_band(G)
print("per joint 3-12 Hz tip speed RMS ratio to GH3 (gh3 m/s):")
names = {
    104: "01",
    105: "02",
    106: "03",
    107: "04",
    108: "05",
    109: "06",
    110: "07",
    111: "08",
    112: "09",
    113: "10",
    114: "11",
    115: "12",
    116: "13",
    117: "14",
    118: "15",
}
print("       " + " ".join("%5s" % names.get(c, c) for c, *_ in G))
print("gh3    " + " ".join("%5.2f" % x for x in jg))
for n, rows in others:
    print("%-6s " % n[:6] + " ".join("%5.2f" % x for x in joint_band(rows) / jg))
print("median tip in head frame (cm), GH3 vs others")
for k, (c, r, tip, dev) in enumerate(G):
    line = "%3s gh3 %s" % (names.get(c, c), np.round(100 * np.median(tip, 0), 1))
    for n, rows in others:
        line += " | %s" % np.round(100 * np.median(rows[k][2], 0), 1)
    print(line)
lim = {j["bone"]: np.degrees(j["limits"]["coneLimit"][1]) for j in prof.joints if "limits" in j}
print("cone p95 minus limit (deg):")
print("       " + " ".join("%5s" % names.get(c, c) for c, *_ in G))
print("gh3    " + " ".join("%5.1f" % (np.percentile(r["cone"], 95) - lim[c]) for c, r, *_ in G))
for n, rows in others:
    print("%-6s " % n[:6] + " ".join("%5.1f" % (np.percentile(r["cone"], 95) - lim[c]) for c, r, *_ in rows))
