"""Ringing of the hair (same head input): autocorrelation of the tip velocity in the head frame, chain end joints pooled.
A springy hair rings: its autocorrelation has a strong second peak. Also the impulse response of a synthetic head turn."""

import sys, json, numpy as np
from hairlib import *

run, pid = sys.argv[1], sys.argv[2]
prof = Profile(pid)
t, W = gh3_run(run, prof, 2.12, 3.6)
fs = 120
tt = np.arange(2, t[-1] - 1, 1 / fs)
onhead = {prof.head}
for j in prof.joints:
    if j["parent"] in onhead:
        onhead.add(j["bone"])
children = {j["parent"] for j in prof.joints}
ends = [j["bone"] for j in prof.joints if j["bone"] in onhead and j["bone"] not in children]


def acf(name, t_, W_):
    m = metrics(t_, W_, prof)
    A = 0
    for c in ends:
        v = np.gradient(resample(t_, m[c]["tip"], tt), axis=0) * fs
        v -= v.mean(0)
        n = len(v)
        F = np.fft.rfft(v, 2 * n, axis=0)
        a = np.fft.irfft(np.abs(F) ** 2, axis=0)[:n].sum(1)
        A = A + a / a[0]
    A /= len(ends)
    lag = np.arange(len(A)) / fs
    z = np.argmax(A < 0)
    nxt = z + np.argmax(A[z : int(fs)])
    trough = np.argmin(A[: int(0.6 * fs)])
    # envelope decay: lag after which |acf| stays below 0.1
    env = np.nonzero(np.abs(A[: int(2 * fs)]) > 0.1)[0][-1] / fs
    return dict(
        name=name,
        zero=float(lag[z]),
        trough=float(A[trough]),
        peak_lag=float(lag[nxt]),
        peak=float(A[nxt]),
        decay=float(env),
    )


rows = [acf("GH3", t, W)] + [acf(p.split("/")[-1].replace(".bin", ""), *bench(p, prof)) for p in sys.argv[3:]]
print("%-24s %9s %9s %10s %9s %10s" % ("run", "zero lag", "trough", "peak lag", "peak", "decay<0.1"))
for r in rows:
    print(
        "%-24s %8.3fs %9.2f %9.3fs %9.2f %9.2fs"
        % (r["name"], r["zero"], r["trough"], r["peak_lag"], r["peak"], r["decay"])
    )
json.dump(rows, open(f"{WORK}/ring-{pid}.json", "w"), indent=1)
