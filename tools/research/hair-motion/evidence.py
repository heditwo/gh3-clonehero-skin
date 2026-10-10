"""Table and plot: GH3 vs mod before vs mod after, same head path from the GH3 memory recordings."""

import json

import matplotlib
import numpy as np

matplotlib.use("Agg")
import matplotlib.pyplot as plt
from hairlib import *

B = WORK + "/bench/"
chars = [
    ("God of Rock", "gor1", "gh3-guitarist-god-of-rock-v1", "gor"),
    ("Judy Nails", "judy1", "gh3-guitarist-judy-v1", "judy"),
    ("Izzy Sparks", "izzy1", "gh3-guitarist-izzy-v1", "izzy"),
]
bands = [(0.3, 1.5), (1.5, 3), (3, 6), (6, 12)]
fs = 120


def stats(t, W, prof, tt, ref=None):
    m = metrics(t, W, prof)
    out = {"band": np.zeros(len(bands)), "dev": [], "err": [], "acf": 0}
    onhead = {prof.head}
    for j in prof.joints:
        if j["parent"] in onhead:
            onhead.add(j["bone"])
    children = {j["parent"] for j in prof.joints}
    tips = {}
    for c, r in m.items():
        tip = resample(t, r["tip"], tt)
        tips[c] = tip
        out["dev"].append(np.linalg.norm(tip - np.median(tip, 0), axis=1))
        v = np.diff(tip, axis=0) * fs
        f = np.fft.rfftfreq(len(v), 1 / fs)
        P = (np.abs(np.fft.rfft(v - v.mean(0), axis=0)) ** 2).sum(1) / len(v) ** 2
        for k, (a, b) in enumerate(bands):
            out["band"][k] += P[(f >= a) & (f < b)].sum() * 2
        if ref is not None:
            out["err"].append(np.linalg.norm(tip - ref[c], axis=1))
    out["band"] = np.sqrt(out["band"])
    out["dev"] = np.concatenate(out["dev"])
    if out["err"]:
        out["err"] = np.concatenate(out["err"])
    ends = [c for c in m if c in onhead and c not in children]
    A = 0
    for c in ends:
        v = np.gradient(tips[c], axis=0) * fs
        v -= v.mean(0)
        n = len(v)
        F = np.fft.rfft(v, 2 * n, axis=0)
        a = np.fft.irfft(np.abs(F) ** 2, axis=0)[:n].sum(1)
        A = A + a / a[0]
    A /= len(ends)
    out["decay"] = np.nonzero(np.abs(A[: 2 * fs]) > 0.1)[0][-1] / fs
    out["acf"] = A[: int(1.5 * fs)]
    return out, tips


rows = []
fig, ax = plt.subplots(2, 3, figsize=(16, 8))
for col, (name, run, pid, short) in enumerate(chars):
    prof = Profile(pid)
    t, W = gh3_run(f"{WORK}/gh3/{run}", prof, 2.12, 3.6)
    tt = np.arange(2, t[-1] - 0.5, 1 / fs)
    g, gt = stats(t, W, prof, tt)
    res = {"GH3": g}
    for label, f in [("mod before", f"b-{short}"), ("mod after", f"a-{short}")]:
        tb, Wb = bench(B + f + ".bin", prof)
        res[label], _ = stats(tb, Wb, prof, tt, gt)
    for label, r in res.items():
        rows.append(
            dict(
                character=name,
                run=label,
                tip_dev_p50_cm=round(100 * float(np.median(r["dev"])), 1),
                tip_dev_p95_cm=round(100 * float(np.percentile(r["dev"], 95)), 1),
                band_rms=[round(float(x), 3) for x in r["band"]],
                ratio=[round(float(x), 2) for x in r["band"] / g["band"]],
                ring_decay_s=round(float(r["decay"]), 2),
                tip_error_vs_gh3_p50_cm=(round(100 * float(np.median(r["err"])), 1) if len(r["err"]) else None),
            )
        )
    x = np.arange(len(bands))
    w = 0.27
    for k, (label, r) in enumerate(res.items()):
        ax[0, col].bar(x + (k - 1) * w, r["band"], w, label=label)
        ax[1, col].plot(np.arange(len(r["acf"])) / fs, r["acf"], label=f"{label} (decay {r['decay']:.2f} s)")
    ax[0, col].set_xticks(x, [f"{a}-{b} Hz" for a, b in bands])
    ax[0, col].set_title(f"{name}: hair tip speed vs head (m/s RMS)")
    ax[0, col].legend()
    ax[1, col].axhline(0.1, color="gray", lw=0.5)
    ax[1, col].axhline(-0.1, color="gray", lw=0.5)
    ax[1, col].set_title(f"{name}: ringing (autocorrelation of tip speed)")
    ax[1, col].set_xlabel("lag (s)")
    ax[1, col].legend(fontsize=8)
plt.suptitle("Same head motion (GH3 memory recording, My Curse) through GH3 Havok, the mod before, and the mod after")
plt.tight_layout()
plt.savefig(WORK + "/evidence-plot.png", dpi=80)
json.dump(rows, open(WORK + "/evidence.json", "w"), indent=1)
print(
    "%-12s %-11s %8s %8s %-28s %-22s %8s %8s"
    % (
        "character",
        "run",
        "dev p50",
        "dev p95",
        "tip speed RMS 0.3-1.5/1.5-3/3-6/6-12 Hz",
        "ratio to GH3",
        "ring s",
        "err p50",
    )
)
for r in rows:
    print(
        "%-12s %-11s %7.1fc %7.1fc %-28s %-22s %8.2f %8s"
        % (
            r["character"],
            r["run"],
            r["tip_dev_p50_cm"],
            r["tip_dev_p95_cm"],
            "/".join("%.2f" % x for x in r["band_rms"]),
            "/".join("%.2f" % x for x in r["ratio"]),
            r["ring_decay_s"],
            r["tip_error_vs_gh3_p50_cm"],
        )
    )
