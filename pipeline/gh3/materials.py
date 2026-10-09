"""Classify GH3 PC scene material templates from their original pixel shaders."""

import struct
from functools import lru_cache
from pathlib import Path

from pipeline.paths import GH3_DATA

MATERIAL_LIBRARY = GH3_DATA/'FXFILES/MaterialLibrary.bin.xen'
SH_REGISTERS = range(188, 192)
AMBIENT_REGISTER = 176


def _records(data):
    if data[:8] != b"MATL\0\0\0\x02":
        raise ValueError("Unsupported material library")
    count = struct.unpack_from(">I", data, 8)[0]
    if count > 4096:
        raise ValueError("Too many material records")
    base = (20 + count*4 + 15) & ~15
    shader_base = base + struct.unpack_from(">I", data, 16)[0]
    for index in range(count):
        record = base + struct.unpack_from(">I", data, 20 + index*4)[0]
        row = struct.unpack_from(">10I", data, record)
        name = data[base + row[1]:data.index(b"\0", base + row[1])].decode()
        descriptor = base + row[7]
        size = struct.unpack_from(">I", data, descriptor + 0x60)[0]
        start = shader_base + struct.unpack_from(">I", data, descriptor + 0x68)[0]
        code = data[start:start + size]
        if size and (code[:4] != b"\0\3\xff\xff" or code[-4:] != b"\xff\xff\0\0"):
            continue
        yield row[2], name, code


def _instructions(code):
    offset = 4
    while offset < len(code):
        token, = struct.unpack_from("<I", code, offset)
        opcode = token & 0xFFFF
        if opcode == 0xFFFF:
            return
        if opcode == 0xFFFE:
            offset += 4 + ((token >> 16) & 0x7FFF)*4
            continue
        count = (token >> 24) & 15
        yield opcode, struct.unpack_from(f"<{count}I", code, offset + 4)
        offset += 4 + count*4


def _register(token):
    return ((token >> 28) & 7) | ((token >> 8) & 24), token & 2047


def classify(code):
    """Return the shading mode, diffuse constant, sampler count, and per-sampler UV scale constants of one pixel shader."""
    constants = set(); samplers = set(); diffuse = None; diffuse_register = None
    scaled = {}; uv_scales = {}
    for opcode, arguments in _instructions(code):
        if opcode in (31, 81):
            continue
        sources = [_register(argument) for argument in arguments[1:]]
        constants.update(index for kind, index in sources if kind == 2)
        destination = _register(arguments[0]) if arguments else None
        if opcode == 66 and sources[0] in scaled: uv_scales[sources[-1][1]] = scaled[sources[0]]
        if destination is not None and destination[0] == 0:
            scaled.pop(destination, None)
        # "mul rN.xy, cK.c, vM" before "texld rX, rN, sS" scales that sampler's UV by one constant component.
        if opcode == 5 and len(sources) == 2 and destination[0] == 0:
            kinds = [kind for kind, _ in sources]
            if sorted(kinds) == [1, 2]:
                token = arguments[1 + kinds.index(2)]
                swizzle = [(token >> (16 + 2 * i)) & 3 for i in range(4)]
                if len(set(swizzle)) == 1:
                    scaled[destination] = (sources[kinds.index(2)][1], swizzle[0])
        if opcode == 66:
            sampler = sources[-1][1]
            samplers.add(sampler)
            if sampler == 0 and diffuse is None: diffuse_register = destination
        elif opcode == 5 and diffuse_register is not None and diffuse is None:
            if destination == diffuse_register and diffuse_register in sources:
                diffuse = next((index for kind, index in sources if kind == 2), None)
            diffuse_register = None
    if constants & set(SH_REGISTERS):
        shading = "lit"
    elif AMBIENT_REGISTER in constants:
        shading = "ambient"
    else:
        shading = "unlit"
    return {"shading": shading, "diffuseConstant": diffuse, "samplers": len(samplers), "uvScales": uv_scales}


@lru_cache(maxsize=1)
def templates(path=MATERIAL_LIBRARY):
    """Map each template checksum to its name and pixel-shader classification."""
    return {f"{checksum:08x}": {"name": name, **classify(code)} for checksum, name, code in _records(Path(path).read_bytes())}


def lightmap_slot(template, sampler_count):
    """Return the lightmap sampler index for a lightmapped (ChkBox) template, or None."""
    name = template["name"]
    if "ChkBox" not in name or sampler_count < 2:
        return None
    return sampler_count - (2 if name.startswith("Env") else 1)
