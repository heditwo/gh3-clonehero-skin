#!/usr/bin/env python3
"""Print a readable listing of an original GH3 pixel shader. Usage: disassemble-ps.py <template name>..."""

import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from pipeline.gh3 import d3d9, materials as gh3_materials  # noqa: E402

# The listing also names the instructions that the GLSL translator does not support.
OPCODES = {**d3d9.OPCODES, 23: "m3x3", 31: "dcl", 40: "if", 81: "def", 91: "dsx", 92: "dsy", 95: "texldl"}
TYPES = {0: "r", 1: "v", 2: "c", 3: "t", 8: "oC", 9: "oDepth", 10: "s", 14: "b", 17: "vMisc", 19: "p"}
MODIFIERS = {1: "-{}", 11: "|{}|", 12: "-|{}|", 13: "!{}"}


def register(token):
    kind, index = d3d9.register(token)
    return f"{TYPES.get(kind, f'?{kind}_')}{index}"


def source(token):
    swizzle = "".join("xyzw"[component] for component in d3d9.swizzle(token))
    text = register(token) + ("" if swizzle == "xyzw" else "." + (swizzle[0] if len(set(swizzle)) == 1 else swizzle))
    return MODIFIERS.get((token >> 24) & 15, "{}").format(text)


def destination(token):
    mask = "".join("xyzw"[i] for i in range(4) if token >> (16 + i) & 1)
    return ("_sat " if token >> 20 & 1 else "") + register(token) + ("" if mask == "xyzw" else "." + mask)


def listing(code):
    for opcode, arguments in d3d9.instructions(code):
        name = OPCODES.get(opcode, f"op{opcode}")
        if opcode == d3d9.OPCODE_DEF:
            yield f"def {register(arguments[0])} = " + ", ".join(
                f"{value:g}" for value in struct.unpack("<4f", struct.pack("<4I", *arguments[1:5]))
            )
        elif opcode == d3d9.OPCODE_DCL:
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
