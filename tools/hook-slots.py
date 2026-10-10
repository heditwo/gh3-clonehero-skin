#!/usr/bin/env python3
"""Find the Dobby hook sites in GameAssembly.so in a Clone Hero core dump and check their jump-address slots.

usage: hook-slots.py <core> <game-dir>
Needs gdb. Extract the core with `coredumpctl dump <pid> --output=<core>` to a disk folder (not a RAM disk) and delete it
after use. See docs/STATUS.md#stability.
"""

import os
import re
import subprocess
import sys
import tempfile

core, game = sys.argv[1], sys.argv[2]
exe = f"{game}/clonehero"
ga_path = f"{game}/GameAssembly.so"


def gdb(*commands):
    args = ["gdb", "-q", "-batch", "-ex", "set debuginfod enabled off", exe, core]
    for c in commands:
        args += ["-ex", c]
    return subprocess.run(args, capture_output=True, text=True, timeout=600).stdout


maps = []
for line in gdb("info proc mappings").splitlines():
    m = re.match(r"\s*0x([0-9a-f]+)\s+0x([0-9a-f]+)\s+0x[0-9a-f]+\s+0x([0-9a-f]+)\s*(\S*)", line)
    if m:
        maps.append((int(m[1], 16), int(m[2], 16), int(m[3], 16), m[4]))
loads = []
for line in subprocess.run(["readelf", "-lW", core], capture_output=True, text=True).stdout.splitlines():
    f = line.split()
    if f and f[0] == "LOAD":
        flags = " ".join(f[6:-1])
        loads.append((int(f[2], 16), int(f[5], 16), flags))


def base_of(name):
    return min(a for a, b, off, n in maps if n.endswith(name) and off == 0)


def describe(addr):
    names = [n for a, b, off, n in maps if a <= addr < b]
    perms = [fl for a, size, fl in loads if a <= addr < a + size]
    return f"{names[0] if names else 'anon'} [{perms[0] if perms else 'unmapped'}]"


ga = base_of("GameAssembly.so")
clr = base_of("libcoreclr.so")
reserve_start = clr - (0x7FFF0000 - 100 * 1024 * 1024)
print(
    f"GameAssembly {ga:#x}  libcoreclr {clr:#x}  GA-coreclr {ga - clr:#x}  CLR reservation {reserve_start:#x}..{clr:#x}"
)

lo, hi = 0x2457000, 0x6116760
dump = tempfile.mktemp(suffix=".bin", dir=os.path.dirname(os.path.abspath(core)))
gdb(f"dump binary memory {dump} {ga + lo:#x} {ga + hi:#x}")
with open(dump, "rb") as f:
    mem = f.read()
os.remove(dump)
with open(ga_path, "rb") as f:
    f.seek(lo)
    fil = f.read(len(mem))
i = 0
while i < len(mem):
    if mem[i] != fil[i]:
        j = i
        while j < len(mem) and mem[j] != fil[j]:
            j += 1
        start = i - 2 if mem[i - 2 : i] == b"\xff\x25" else i - 1 if mem[i - 1] == 0xFF else i
        site = lo + start
        if mem[start : start + 2] == b"\xff\x25":
            disp = int.from_bytes(mem[start + 2 : start + 6], "little", signed=True)
            slot = ga + site + 6 + disp
            val = gdb(f"x/gx {slot:#x}")
            m = re.search(r":\s+0x([0-9a-f]+)", val)
            value = int(m[1], 16) if m else None
            target = describe(value) if value is not None else "?"
            in_res = reserve_start <= slot < clr
            print(
                f"site GA+{site:#x} slot {slot:#x} (= site-2GB page? {((ga + site - 0x80000000) | 0xFFF) + 1 == slot & ~0xFFF}) "
                f"in CLR reservation at +{slot - reserve_start:#x}: {in_res}; slot region {describe(slot)}; "
                f"value {value:#x} -> {target}"
                if value is not None
                else f"site {site:#x} slot {slot:#x} unreadable"
            )
        else:
            print(f"diff at GA+{lo + i:#x} len {j - i}: {mem[i:j][:16].hex()}")
        i = j
    i += 1
