#!/usr/bin/env python3
"""Print a readable listing of an original GH3 pixel shader. Usage: disassemble-ps.py <template name>..."""

import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from pipeline.gh3 import materials as gh3_materials  # noqa: E402

OPCODES = {
    1: "mov",
    2: "add",
    3: "sub",
    4: "mad",
    5: "mul",
    6: "rcp",
    7: "rsq",
    8: "dp3",
    9: "dp4",
    10: "min",
    11: "max",
    12: "slt",
    13: "sge",
    14: "exp",
    15: "log",
    18: "lrp",
    19: "frc",
    23: "m3x3",
    31: "dcl",
    32: "pow",
    33: "crs",
    35: "abs",
    36: "nrm",
    37: "sincos",
    40: "if",
    41: "ifc",
    42: "else",
    43: "endif",
    65: "texkill",
    66: "texld",
    81: "def",
    88: "cmp",
    90: "dp2add",
    91: "dsx",
    92: "dsy",
    95: "texldl",
}
TYPES = {0: "r", 1: "v", 2: "c", 3: "t", 8: "oC", 9: "oDepth", 10: "s", 14: "b", 17: "vMisc", 19: "p"}
MODIFIERS = {1: "-{}", 11: "|{}|", 12: "-|{}|", 13: "!{}"}


def register(token):
    kind, index = gh3_materials._register(token)
    return f"{TYPES.get(kind, f'?{kind}_')}{index}"


def source(token):
    swizzle = "".join("xyzw"[(token >> (16 + 2 * i)) & 3] for i in range(4))
    text = register(token) + ("" if swizzle == "xyzw" else "." + (swizzle[0] if len(set(swizzle)) == 1 else swizzle))
    return MODIFIERS.get((token >> 24) & 15, "{}").format(text)


def destination(token):
    mask = "".join("xyzw"[i] for i in range(4) if token >> (16 + i) & 1)
    return ("_sat " if token >> 20 & 1 else "") + register(token) + ("" if mask == "xyzw" else "." + mask)


def listing(code):
    for opcode, arguments in gh3_materials._instructions(code):
        name = OPCODES.get(opcode, f"op{opcode}")
        if opcode == 81:
            yield f"def {register(arguments[0])} = " + ", ".join(
                f"{value:g}" for value in struct.unpack("<4f", struct.pack("<4I", *arguments[1:5]))
            )
        elif opcode == 31:
            yield f"dcl {destination(arguments[1])}"
        elif arguments:
            yield f"{name} " + ", ".join([destination(arguments[0])] + [source(argument) for argument in arguments[1:]])
        else:
            yield name


def main():
    records = {name: code for _, name, code in gh3_materials._records(gh3_materials.MATERIAL_LIBRARY.read_bytes())}
    for name in sys.argv[1:]:
        print(f"== {name}")
        for line in listing(records[name]):
            print("  " + line)


if __name__ == "__main__":
    main()
