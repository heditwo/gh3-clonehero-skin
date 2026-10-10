#!/usr/bin/env python3
"""Compare two native methods by the object offsets they access and their call count.

Usage: native-offsets.py <GameAssembly.so> <GameAssembly.dll> NAME:LINUX_RVA:LENGTH:WINDOWS_RVA:LENGTH ...
The RVAs and lengths come from `tools/InteropMap` (`type` mode). In the Linux GameAssembly.so the RVA is the file offset.
Run with: uv run --with capstone --with pefile python tools/native-offsets.py ...
"""

import re
import sys

import capstone
import pefile

disassembler = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)


def summary(code, address):
    offsets, calls = set(), 0
    for instruction in disassembler.disasm(code, address):
        calls += instruction.mnemonic == "call"
        for match in re.finditer(r"\[(r[a-z0-9]+) \+ (0x[0-9a-f]+)\]", instruction.op_str):
            if match.group(1) not in ("rsp", "rbp", "rip"):
                offsets.add(int(match.group(2), 16))
    return " ".join(hex(offset) for offset in sorted(offsets)), calls


linux = open(sys.argv[1], "rb").read()
windows_pe = pefile.PE(sys.argv[2], fast_load=True)
windows = open(sys.argv[2], "rb").read()
for spec in sys.argv[3:]:
    name, linux_rva, linux_length, windows_rva, windows_length = spec.split(":")
    start, length = int(linux_rva, 16), int(linux_length, 16)
    offsets, calls = summary(linux[start : start + length], start)
    print(f"{name} linux: offsets {offsets}; calls {calls}")
    start, length = int(windows_rva, 16), int(windows_length, 16)
    file_offset = windows_pe.get_offset_from_rva(start)
    offsets, calls = summary(windows[file_offset : file_offset + length], start)
    print(f"{name} windows: offsets {offsets}; calls {calls}")
