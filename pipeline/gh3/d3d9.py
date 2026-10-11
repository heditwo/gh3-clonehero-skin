"""Direct3D 9 shader bytecode (vs_3_0, ps_3_0) tokens, as the GH3 PC material library stores them.

Values from the Direct3D 9 shader model reference (d3d9types.h: D3DSHADER_INSTRUCTION_OPCODE_TYPE, D3DSHADER_PARAM_REGISTER_TYPE,
D3DDECLUSAGE, D3DSAMPLER_TEXTURE_TYPE).
"""

import struct

VERSION_VS_3_0 = 0xFFFE0300
VERSION_PS_3_0 = 0xFFFF0300
PS_3_0_TOKEN = b"\0\3\xff\xff"
END_TOKEN = b"\xff\xff\0\0"
OPCODE_END = 0xFFFF
OPCODE_COMMENT = 0xFFFE

OPCODE_MUL = 5
OPCODE_DCL = 31
OPCODE_TEXLD = 66
OPCODE_DEF = 81
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
    32: "pow",
    33: "crs",
    35: "abs",
    36: "nrm",
    37: "sincos",
    41: "ifc",
    42: "else",
    43: "endif",
    65: "texkill",
    66: "texld",
    88: "cmp",
    90: "dp2add",
}

REGISTER_TEMP = 0
REGISTER_INPUT = 1
REGISTER_CONST = 2
REGISTER_OUTPUT = 6
REGISTER_COLOR_OUT = 8
REGISTER_SAMPLER = 10
REGISTER_MISC = 17
RELATIVE_ADDRESSING = 1 << 13

USAGES = {
    0: "position",
    1: "blendweight",
    2: "blendindices",
    3: "normal",
    4: "psize",
    5: "texcoord",
    6: "tangent",
    7: "binormal",
    8: "tessfactor",
    10: "color",
    11: "fog",
    12: "depth",
}
SAMPLER_TYPES = {2: "2d", 3: "cube", 4: "volume"}

SOURCE_MODIFIER_NONE = 0
SOURCE_MODIFIER_NEGATE = 1
SOURCE_MODIFIER_ABS = 11
SOURCE_MODIFIER_ABS_NEGATE = 12


def instructions(code):
    """(opcode, argument tokens) of each instruction after the version token, up to the end token."""
    offset = 4
    while offset < len(code):
        (token,) = struct.unpack_from("<I", code, offset)
        opcode = token & 0xFFFF
        if opcode == OPCODE_END:
            return
        if opcode == OPCODE_COMMENT:
            offset += 4 + ((token >> 16) & 0x7FFF) * 4
            continue
        count = (token >> 24) & 15
        yield opcode, struct.unpack_from(f"<{count}I", code, offset + 4)
        offset += 4 + count * 4


def register(token):
    """(register type, register index) of a parameter token."""
    return ((token >> 28) & 7) | ((token >> 8) & 24), token & 2047


def swizzle(token):
    """The four source swizzle components (0 x, 1 y, 2 z, 3 w) of a source parameter token."""
    return [(token >> (16 + 2 * i)) & 3 for i in range(4)]


def source_modifier(token):
    return (token >> 24) & 15
