#!/usr/bin/env python3
"""Summarize GH3_SKIN_PROFILE=1 log lines. Usage: summarize-profile.py <log>..."""

import re
import statistics
import sys

LINE = re.compile(r"GH3 profile song (-?[\d.]+)s: (\d+) fps; gen0 (\d+); ms/frame (.*?); KB/frame (.*)$")


def pairs(text):
    result = {}
    for item in text.split(", "):
        name, _, value = item.rpartition(" ")
        try:
            result[name] = float(value)
        except ValueError:
            pass
    return result


def summarize(path):
    rows = []
    for line in open(path, errors="ignore"):
        match = LINE.search(line)
        if match and float(match[1]) > 2:
            rows.append((float(match[1]), int(match[2]), int(match[3]), pairs(match[4]), pairs(match[5])))
    if not rows:
        return None

    def mean(selected, index):
        names = {name for row in selected for name in row[index]}
        return {name: statistics.mean(row[index].get(name, 0) for row in selected) for name in names}

    normal = [row for row in rows if not 44 <= row[0] <= 56] or rows
    burst = [row for row in rows if 44 <= row[0] <= 56] or rows
    return {
        "fps": (statistics.median(row[1] for row in normal), min(row[1] for row in rows)),
        "gen0": statistics.mean(row[2] for row in rows),
        "normal": mean(normal, 3),
        "burst": mean(burst, 3),
        "alloc": mean(normal, 4),
        "burstAlloc": mean(burst, 4),
    }


def top(values, count=6):
    return ", ".join(f"{name} {value:.2f}" for name, value in sorted(values.items(), key=lambda pair: -pair[1])[:count])


for path in sys.argv[1:]:
    summary = summarize(path)
    if summary is None:
        print(f"{path}: no samples")
        continue
    print(
        f"{path.split('/')[-1].split('.')[0]:11s} median {summary['fps'][0]:.0f} fps, min {summary['fps'][1]} fps, gen0 GCs/s {summary['gen0']:.1f}"
    )
    print(f"    normal ms: {top(summary['normal'])}")
    print(f"    burst  ms: {top(summary['burst'])}")
    print(f"    normal KB: {top(summary['alloc'])}")
    print(f"    burst  KB: {top(summary['burstAlloc'])}")
