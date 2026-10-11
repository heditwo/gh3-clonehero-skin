"""Measure the upper eyelid turn of the GH3 singer facial clips (the CharacterFace blink constants).

Usage: uv run --with numpy python tools/research/neutral-face/gh3_eyelid_turns.py [--library assets/libraries/gh3-band-v1]

The turn is the angle of the eyelid bone from its bind pose (the larger of the two eyelids) in each frame of the 570
clips. The script prints the percentiles, and the runs above 20° (blinks: short runs; held closed eyes: runs over 1 s).
"""

import argparse
import importlib.util
from pathlib import Path

import numpy as np

CLIP_FPS = 30
PERCENTILES = (5, 25, 50, 75, 80, 90, 95, 99)
BLINK_START = 20
SHORT_RUN = 0.3
LONG_RUN = 1.0


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--library", type=Path, default=Path("assets/libraries/gh3-band-v1"))
    args = parser.parse_args()
    spec = importlib.util.spec_from_file_location("lipsync_shapes", Path(__file__).parents[2] / "lipsync-shapes.py")
    shapes = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(shapes)
    deltas, _, _, clips = shapes.load(args.library)
    lid = np.max([np.linalg.norm(deltas[:, shapes.ALL.index(name), :3], axis=1) for name in shapes.LIDS], axis=0)
    print(f"clips {clips}, frames {len(lid)}")
    print("lid turn percentiles (degrees):", dict(zip(PERCENTILES, np.percentile(lid, PERCENTILES).round(1))))
    above = lid > BLINK_START
    edges = np.diff(np.r_[0, above.astype(int), 0])
    starts, ends = np.where(edges == 1)[0], np.where(edges == -1)[0]
    seconds = (ends - starts) / CLIP_FPS
    peaks = np.array([lid[s:e].max() for s, e in zip(starts, ends)])
    held = [lid[s:e].mean() for s, e, n in zip(starts, ends, seconds) if n > LONG_RUN]
    print(
        f"runs above {BLINK_START} degrees: {len(seconds)}; median peak of runs up to {SHORT_RUN} s: {np.median(peaks[seconds <= SHORT_RUN]):.1f}"
    )
    print(f"median turn of runs over {LONG_RUN} s: {np.median(held):.1f}")


if __name__ == "__main__":
    main()
