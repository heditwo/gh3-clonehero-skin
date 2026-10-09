"""Spike: pixel statistics of the skinned GLB comparison renders.

Run in /tmp/opencode/spike-skinned: uv run --with pillow --with numpy python <repo>/pipeline/spike/compare_renders.py run-glb run-original
"""

import sys
import numpy as np
from PIL import Image
def load(p): return np.asarray(Image.open(p).convert("RGB")).astype(int)
def stats(a, b):
    d = np.abs(a-b); m = d.max(2)
    return f"max {d.max():3d}; mean {d.mean():.4f}; px>0 {int((m>0).sum()):6d}; px>8 {int((m>8).sum()):6d}"
for run in sys.argv[1:]:
    print(run)
    for i in range(4):
        f = lambda kind, p: load(f"{run}/compare-{i}-{kind}-{p}.png")
        a0, b0, a1, b1 = f("gh3bnd02", 0), f("glb", 0), f("gh3bnd02", 1), f("glb", 1)
        print(f"  t{i} gh3bnd02 vs glb pass0: {stats(a0,b0)} | pass1: {stats(a1,b1)} | gh3bnd02 repeat: {stats(a0,a1)} | glb repeat: {stats(b0,b1)}")
        Image.fromarray(np.clip(np.abs(a1-b1)*16,0,255).astype("uint8")).save(f"{run}/diff-{i}-pass1-x16.png")
for i in (0, 1):
    a = load(f"run-glb/frames/frame-0{i}.png"); b = load(f"run-original/frames/frame-0{i}.png")
    print(f"on-screen frame {i} (two runs, glb vs original): {stats(a,b)}")
    Image.fromarray(np.clip(np.abs(a-b)*4,0,255).astype("uint8")).save(f"onscreen-diff-{i}-x4.png")
