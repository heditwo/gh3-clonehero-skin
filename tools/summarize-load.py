"""Summarize the runs of scripts/measure-load.sh: the median of each load time over the runs of each flow.

Usage: summarize-load.py <output folder> [<second output folder>]
With two folders, the table has a column for each (for example before and after a change).
"""
import re
import statistics
import sys
from pathlib import Path

MARK = re.compile(r"\[timing\] ([\d.]+) s (.*?) \(\+\d+ ms; heap (\d+) MB, rss (\d+) MB.*?jit (\d+) methods in (\d+) ms\)")


def marks(log):
    rows = []
    for line in log.splitlines():
        match = MARK.search(line)
        if match:
            rows.append((float(match.group(1)), match.group(2), int(match.group(3)), int(match.group(4)), int(match.group(6))))
    return rows


def first(rows, text, after=0.0):
    return next((row for row in rows if text in row[1] and row[0] >= after), None)


def peak_rss(folder):
    path = folder / "samples.csv"
    if not path.exists():
        return None
    values = [float(line.split(",")[2]) for line in path.read_text().splitlines()[1:] if line.count(",") >= 5]
    return max(values) if values else None


def metrics(folder):
    log = (folder / "Latest.log").read_text(errors="replace")
    rows = marks(log)
    result = {}
    flow = folder.name.rsplit("-", 1)[0]
    early = first(rows, "mod early init")
    init = first(rows, "mod init [")
    if early:
        result["process start to mod early init (s)"] = early[0]
    if init:
        result["mod init (ms)"] = float(re.search(r"\+(\d+) ms", next(l for l in log.splitlines() if "mod init [" in l)).group(1))
        textures = re.search(r"skin textures (\d+)", next(l for l in log.splitlines() if "mod init [" in l))
        if textures:
            result["mod init: skin textures (ms)"] = float(textures.group(1))
    scan, done = first(rows, "package scan starts"), first(rows, "package scan and cache check done")
    if scan and done:
        result["package scan on the main thread (ms)"] = round((done[0] - scan[0]) * 1000)
    menu = first(rows, "frame 1 of scene Main Menu")
    if menu:
        result["process start to main menu frame 1 (s)"] = menu[0]
    menu30 = first(rows, "frame 30 of scene Main Menu")
    if menu30:
        result["process start to main menu frame 30 (s)"] = menu30[0]
    finished = first(rows, "package imports finished")
    if finished and flow.startswith("cold"):
        result["process start to all packages imported (s)"] = finished[0]
    took = re.search(r"all packages ready; the imports took ([\d.]+) s", log)
    if took:
        result["package imports, total (s)"] = float(took.group(1))
    long_frames = [float(m.group(1)) for m in re.finditer(r"long frame (\d+) ms in Main Menu", log)]
    if menu:
        result["long menu frames (> 50 ms)"] = len(long_frames)
    gameplay = first(rows, "scene Gameplay loaded")
    if gameplay:
        result["process start to gameplay scene (s)"] = gameplay[0]
        stage = first(rows, "stage loaded [", gameplay[0])
        start = first(rows, "stage load starts", gameplay[0])
        clock = first(rows, "song clock runs", gameplay[0])
        if stage:
            result["gameplay scene to stage loaded (s)"] = round(stage[0] - gameplay[0], 3)
            result["process start to stage loaded (s)"] = stage[0]
        if start and stage:
            result["JIT during the stage load (ms)"] = stage[4] - start[4]
        if clock and flow == "warm-song":
            result["gameplay scene to song clock (s)"] = round(clock[0] - gameplay[0], 3)
        load = re.search(r"GH3 stage load time: (\d+) ms", log)
        if load and flow in ("warm-song", "cold-song"):
            result["stage load on the main thread (ms)"] = float(load.group(1))
    if flow.startswith("reload"):
        loads = [float(v) for v in re.findall(r"GH3 stage load time: (\d+) ms", log)]
        if len(loads) > 1:
            result["stage load, first song (ms)"] = loads[0]
            result["stage load, next songs, median (ms)"] = statistics.median(loads[1:])
        scenes = [row for row in rows if "scene Gameplay loaded" in row[1]]
        clocks = [row for row in rows if "song clock runs" in row[1]]
        if len(scenes) > 1 and len(clocks) == len(scenes):
            result["next songs: gameplay scene to song clock, median (s)"] = round(statistics.median(c[0] - s[0] for s, c in zip(scenes[1:], clocks[1:])), 3)
        zeros = [row for row in rows if "song time 0" in row[1]]
        if zeros:
            result["resident memory at song time 0, first song (MB)"] = zeros[0][3]
            result["resident memory at song time 0, last song (MB)"] = zeros[-1][3]
            result["managed heap at song time 0, last song (MB)"] = zeros[-1][2]
    opened, ready = first(rows, "picker opens"), first(rows, "picker lists ready")
    if opened and ready:
        result["picker open: lists (ms)"] = round((ready[0] - opened[0]) * 1000)
        preview = first(rows, "picker preview of", opened[0])
        if preview:
            result["picker open: first preview (ms)"] = round((preview[0] - opened[0]) * 1000)
    rss = peak_rss(folder)
    if rss:
        result["peak resident memory (MB)"] = rss
    return flow, result


def summarize(root):
    flows = {}
    for folder in sorted(Path(root).iterdir()):
        if (folder / "Latest.log").exists():
            flow, values = metrics(folder)
            for name, value in values.items():
                flows.setdefault(flow, {}).setdefault(name, []).append(value)
    return flows


def fmt(values):
    if not values:
        return "-"
    median = statistics.median(values)
    text = f"{median:.2f}" if median < 100 and median != int(median) else f"{median:.0f}"
    return f"{text} (n={len(values)})"


tables = [summarize(path) for path in sys.argv[1:]]
for flow in sorted(set().union(*tables)):
    print(f"\n### {flow}\n")
    print("| Metric | " + " | ".join(Path(path).name for path in sys.argv[1:]) + " |")
    print("| --- |" + " --- |" * len(tables))
    names = []
    for table in tables:
        for name in table.get(flow, {}):
            if name not in names:
                names.append(name)
    for name in names:
        print(f"| {name} | " + " | ".join(fmt(table.get(flow, {}).get(name, [])) for table in tables) + " |")
