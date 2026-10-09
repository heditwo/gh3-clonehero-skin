"""Print Markdown tables from analyze.py output."""

import json
import pickle
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).parent))
from analyze import BINS, COMPRESSORS, ENCODINGS, percentile  # noqa: E402

out = Path(sys.argv[1])
infos = json.loads((out / "clips.json").read_text())
stats = pickle.loads((out / "stats.pickle").read_bytes())
roles = ["guitarist", "bassist", "drummer", "singer"]
mb = 1e6

print("## Clip inventory\n")
print("| Role | Clips | Song facial clips | Seconds | 30 fps frames | Tracks (rot / pos) | GH3 rot keys | 30 fps rot samples | Reduced rot keys | SKA bytes | Current .bin.gz |")
print("|---|---:|---:|---:|---:|---|---:|---:|---:|---:|---:|")
for role in roles + ["total"]:
    rows = [i for i in infos if role in (i["role"], "total")]
    rot_samples = sum(i["frames"] * i["rotTracks"] for i in rows)
    print(f"| {role} | {len(rows)} | {sum(1 for i in rows if i['song'])} | {sum(i['duration'] for i in rows):.0f} | {sum(i['frames'] for i in rows)} | "
          f"{sum(i['rotTracks'] for i in rows)} / {sum(i['posTracks'] for i in rows)} | {sum(i['rotKeys'] for i in rows)} | {rot_samples} | {sum(i['reducedRotKeys'] for i in rows)} | "
          f"{sum(i['ska'] for i in rows) / mb:.1f} MB | {sum(i['sizes']['f32']['file'] for i in rows) / mb:.1f} MB |")
print(f"\nClips with fractional translation key times: {sum(1 for i in infos if i['fractionalTimes'])}\n")

print("## Size (MB, each clip compressed alone)\n")
for role in roles + ["total"]:
    rows = [i for i in infos if role in (i["role"], "total")]
    print(f"\n### {role}\n")
    print("| Encoding | " + " | ".join(COMPRESSORS) + " |")
    print("|---|" + "---:|" * len(COMPRESSORS))
    for enc in ENCODINGS:
        print(f"| {enc} | " + " | ".join(f"{sum(i['sizes'][enc][c] for i in rows) / mb:.2f}" for c in COMPRESSORS) + " |")


def table(prefix, variants, metrics):
    print("| Role | Variant | " + " | ".join(f"{m} max / p99" for m in metrics) + " |")
    print("|---|---|" + "---:|" * len(metrics))
    for role in roles:
        if role not in stats: continue
        hist, maxima, _ = stats[role]
        for v in variants:
            cells = []
            for m in metrics:
                k = f"{prefix}/{v}/{m}"
                cells.append(f"{maxima[k]:.3g} / {percentile(hist[k], 0.99):.2g}" if k in maxima else "-")
            print(f"| {role} | {v} | " + " | ".join(cells) + " |")


rot = ["rot/all", "rot/fingers_L", "rot/fingers_R", "rot/body"]
eff = ["eff/head", "eff/palm_L", "eff/palm_R", "eff/tips_L", "eff/tips_R"]
print("\n## Quantization only: against the current 30 fps clips at their frames\n")
print("Rotation error in degrees (local bone rotation):\n")
table("vsCurrent", ["q48", "q32", "keys", "q48kr"], rot)
print("\nEnd-effector position error in cm (forward kinematics):\n")
table("vsCurrent", ["q48", "q32", "keys", "q48kr"], eff + ["pos_cm"])
print("\n## Against the original GH3 SKA keys, sampled at 60 Hz\n")
print("Rotation error in degrees:\n")
table("vsGH3", ["f32", "q48", "q32", "keys", "q48kr", "keys-direct"], rot)
print("\nEnd-effector position error in cm:\n")
table("vsGH3", ["f32", "q48", "q32", "keys", "q48kr", "keys-direct"], eff)

print("\n## Worst bones (vsGH3 rotation max, degrees)\n")
for role in roles:
    if role not in stats: continue
    _, _, bone_max = stats[role]
    for v in ["f32", "q48", "q32"]:
        top = sorted(((val, b) for (k, b), val in bone_max.items() if k == f"vsGH3/{v}/rot"), reverse=True)[:5]
        print(f"- {role} {v}: " + ", ".join(f"bone {b} {val:.2f}" for val, b in top))
