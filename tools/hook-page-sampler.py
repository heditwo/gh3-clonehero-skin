#!/usr/bin/env python3
"""Watch Dobby hook slot pages in a Clone Hero process.

usage: hook-page-sampler.py <out.json> <pid-file> <command...>
Starts the command (a parent may read /proc/<pid>/mem under Yama ptrace_scope 1). Every 50 ms it reads
/proc/<pid>/maps and the known hook sites in GameAssembly.so. For each hook slot it records what was mapped at the slot
page before the hook (the victim of Dobby's MAP_FIXED page), whether the owner of that memory later uses the 64 KB
chunk around the page, and whether the slot still points to executable memory. Exits with the shell-style status.
The hook sites are the six GameAssembly.so functions that MelonLoader 0.7.3 hooks in Clone Hero 1.1.0.6142 (Linux).
Example: hook-page-sampler.py run.json run.pid ./run-gh3-skin.sh --preview
"""

import json
import os
import subprocess
import sys
import time
from pathlib import Path

out, pid_file = sys.argv[1], sys.argv[2]
process = subprocess.Popen(sys.argv[3:])
pid = process.pid
Path(pid_file).write_text(str(pid))
RESERVATION = 0x7FFF0000 - 100 * 1024 * 1024
SITES = [0x28C0B40, 0x290F940, 0x2843400, 0x2847140, 0x5D67C80, 0x5D67D40]
start = time.monotonic()
state = {"hooks": {}, "events": []}


def now():
    return round(time.monotonic() - start, 2)


def event(text):
    state["events"].append([now(), text])


def read_maps():
    rows = []
    with open(f"/proc/{pid}/maps") as f:
        for line in f:
            parts = line.split(None, 5)
            a, b = (int(x, 16) for x in parts[0].split("-"))
            rows.append((a, b, parts[1], int(parts[2], 16), parts[5].strip() if len(parts) > 5 else ""))
    return rows


def holder(maps, address):
    return next((m for m in maps if m[0] <= address < m[1]), None)


def classify(maps, page, clr):
    m = holder(maps, page)
    if m is None:
        return "free"
    a, b, p, off, n = m
    rs = clr - RESERVATION if clr else None
    in_clr = rs is not None and rs <= page < clr and any(x[0] <= rs < x[1] or x[0] == rs for x in maps)
    kind = ("file " + os.path.basename(n)) if n.startswith("/") else "clr-reservation" if in_clr else (n or "anon")
    return f"{kind} {p} {a:#x}-{b:#x}"


def scan(maps):
    sites = []
    path = next(n for a, b, p, off, n in maps if n.endswith("/GameAssembly.so"))
    with open(path, "rb") as f:
        for a, b, p, off, n in maps:
            if n != path or "x" not in p:
                continue
            f.seek(off)
            for chunk in range(a, b, 1 << 20):
                size = min(1 << 20, b - chunk)
                disk = f.read(size)
                live = os.pread(mem, size, chunk)
                if disk == live:
                    continue
                i = 0
                while i < size:
                    if live[i] != disk[i]:
                        j = live.rfind(b"\xff\x25", max(0, i - 6), i + 2)
                        site = chunk + (j if j >= 0 else i)
                        if not sites or site - (ga + sites[-1][0]) > 8:
                            entry = [site - ga]
                            if j >= 0:
                                slot = site + 6 + int.from_bytes(live[j + 2 : j + 6], "little", signed=True)
                                entry.append(hex(slot))
                                entry.append(classify(maps, slot & ~0xFFF, clr))
                            sites.append(entry)
                        i += 8
                    else:
                        i += 1
    return [[hex(e[0])] + e[1:] for e in sites]


mem = -1
ga = ga_end = clr = None
previous = []
while process.poll() is None:
    try:
        maps = read_maps()
    except OSError:
        break
    if ga is None:
        ga = next((a for a, b, p, off, n in maps if n.endswith("/GameAssembly.so") and off == 0), None)
        if ga is not None:
            ga_end = max(b for a, b, p, off, n in maps if n.endswith("/GameAssembly.so"))
    if clr is None:
        clr = next((a for a, b, p, off, n in maps if n.endswith("/libcoreclr.so") and off == 0), None)
        if clr is not None and ga is not None:
            state.update(
                ga=hex(ga),
                clr=hex(clr),
                ga_minus_clr_mb=round((ga - clr) / 2**20, 1),
                clr_reservation_start=hex(clr - RESERVATION),
            )
            mem = os.open(f"/proc/{pid}/mem", os.O_RDONLY)
    if ga is None or clr is None:
        previous = maps
        time.sleep(0.05)
        continue
    for site in SITES:
        try:
            code = os.pread(mem, 8, ga + site)
        except OSError:
            continue
        key = hex(site)
        hook = state["hooks"].get(key)
        if code[:2] != b"\xff\x25":
            if hook and not hook.get("removed"):
                hook["removed"] = now()
            continue
        slot = ga + site + 6 + int.from_bytes(code[2:6], "little", signed=True)
        try:
            value = int.from_bytes(os.pread(mem, 8, slot), "little")
        except OSError:
            value = None
        target = holder(maps, value) if value is not None else None
        valid = target is not None and "x" in target[2]
        page = slot & ~0xFFF
        if hook is None or hook["slot"] != hex(slot):
            if hook is not None:
                event(f"hook {key} slot moved {hook['slot']} -> {slot:#x}")
            hook = state["hooks"][key] = {
                "slot": hex(slot),
                "t_install": now(),
                "valid": valid,
                "victim": classify(previous, page, clr) if hook is None else hook.get("victim"),
                "page_after": classify(maps, page, clr),
            }
            event(f"hook {key} slot {slot:#x}; before: {hook['victim']}")
        if not valid and hook.get("valid") and not hook.get("invalid"):
            hook["invalid"] = now()
            hook["invalid_value"] = hex(value) if value is not None else None
            event(f"hook {key} slot {slot:#x} no longer points to executable memory: {hook['invalid_value']}")
        chunk = page & ~0xFFFF
        neighbours = [
            m
            for m in maps
            if m[0] < chunk + 0x10000
            and m[1] > chunk
            and not (m[0] <= page and m[1] >= page + 0x1000)
            and not m[2].startswith("---")
        ]
        if neighbours and not hook.get("owner_uses_chunk"):
            hook["owner_uses_chunk"] = now()
            n = neighbours[0]
            hook["owner_mapping"] = f"{n[0]:#x}-{n[1]:#x} {n[2]} {os.path.basename(n[4])}"
            event(f"hook {key} slot page {page:#x}: 64 KB chunk now also holds {hook['owner_mapping']}")
    if (
        not state.get("scan")
        and len(state["hooks"]) >= 6
        and now() > max(h["t_install"] for h in state["hooks"].values()) + 1
    ):
        state["scan"] = scan(maps)
        event(f"full scan: {len(state['scan'])} patched sites in GameAssembly.so")
    previous = maps
    time.sleep(0.05)

status = process.wait()
state["duration"] = now()
state["status"] = status
with open(out, "w") as f:
    json.dump(state, f, indent=1)
sys.exit(128 - status if status < 0 else status)
