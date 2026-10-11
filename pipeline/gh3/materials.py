"""Classify GH3 PC scene material templates from their original pixel shaders."""

import struct
from functools import lru_cache
from pathlib import Path

from pipeline.gh3 import d3d9
from pipeline.paths import GH3_DATA

MATERIAL_LIBRARY = GH3_DATA / "FXFILES/MaterialLibrary.bin.xen"
# Pixel shader constant registers of the GH3 engine lights: c188–c191 spherical harmonics, c176 ambient.
SH_REGISTERS = range(188, 192)
AMBIENT_REGISTER = 176

# MaterialLibrary.bin.xen (big-endian): magic, record count (+8), shader data offset (+16), record offsets (+20); the
# records start at the next 16-byte boundary. Record words: name offset (1), template checksum (2), descriptor (7).
# Descriptor variant 0 is the normal scene pass: vertex shader size and offset at +0x50/+0x58, pixel shader at +0x60/+0x68.
LIBRARY_MAGIC = b"MATL\0\0\0\x02"
LIBRARY_COUNT, LIBRARY_SHADERS, LIBRARY_RECORDS = 8, 16, 20
RECORD_NAME, RECORD_CHECKSUM, RECORD_DESCRIPTOR = 1, 2, 7
VERTEX_PROGRAM = (0x50, 0x58)
PIXEL_PROGRAM = (0x60, 0x68)
MAX_LIBRARY_RECORDS = 4096


def library_records(data):
    """(template checksum, name, vertex bytecode, pixel bytecode) of each material library record."""
    if data[:8] != LIBRARY_MAGIC:
        raise ValueError("Unsupported material library")
    count = struct.unpack_from(">I", data, LIBRARY_COUNT)[0]
    base = (LIBRARY_RECORDS + count * 4 + 15) & ~15
    shader_base = base + struct.unpack_from(">I", data, LIBRARY_SHADERS)[0]
    for index in range(count):
        record = base + struct.unpack_from(">I", data, LIBRARY_RECORDS + index * 4)[0]
        row = struct.unpack_from(">10I", data, record)
        name_at = base + row[RECORD_NAME]
        name = data[name_at : data.index(b"\0", name_at)].decode()
        descriptor = base + row[RECORD_DESCRIPTOR]
        programs = []
        for size_at, offset_at in (VERTEX_PROGRAM, PIXEL_PROGRAM):
            size = struct.unpack_from(">I", data, descriptor + size_at)[0]
            start = shader_base + struct.unpack_from(">I", data, descriptor + offset_at)[0]
            programs.append(data[start : start + size])
        yield row[RECORD_CHECKSUM], name, programs[0], programs[1]


def _records(data):
    """(template checksum, name, pixel bytecode) of the records with a complete pixel shader (or none)."""
    if data[:8] == LIBRARY_MAGIC and struct.unpack_from(">I", data, LIBRARY_COUNT)[0] > MAX_LIBRARY_RECORDS:
        raise ValueError("Too many material records")
    for checksum, name, _, code in library_records(data):
        if code and (code[:4] != d3d9.PS_3_0_TOKEN or code[-4:] != d3d9.END_TOKEN):
            continue
        yield checksum, name, code


def classify(code):
    """Return the shading mode, diffuse constant, sampler count, and per-sampler UV scale constants of one pixel shader."""
    constants = set()
    samplers = set()
    diffuse = None
    diffuse_register = None
    scaled = {}
    uv_scales = {}
    for opcode, arguments in d3d9.instructions(code):
        if opcode in (d3d9.OPCODE_DCL, d3d9.OPCODE_DEF):
            continue
        sources = [d3d9.register(argument) for argument in arguments[1:]]
        constants.update(index for kind, index in sources if kind == d3d9.REGISTER_CONST)
        destination = d3d9.register(arguments[0]) if arguments else None
        if opcode == d3d9.OPCODE_TEXLD and sources[0] in scaled:
            uv_scales[sources[-1][1]] = scaled[sources[0]]
        if destination is not None and destination[0] == d3d9.REGISTER_TEMP:
            scaled.pop(destination, None)
        # "mul rN.xy, cK.c, vM" before "texld rX, rN, sS" scales that sampler's UV by one constant component.
        if opcode == d3d9.OPCODE_MUL and len(sources) == 2 and destination[0] == d3d9.REGISTER_TEMP:
            kinds = [kind for kind, _ in sources]
            if sorted(kinds) == [d3d9.REGISTER_INPUT, d3d9.REGISTER_CONST]:
                token = arguments[1 + kinds.index(d3d9.REGISTER_CONST)]
                swizzle = d3d9.swizzle(token)
                if len(set(swizzle)) == 1:
                    scaled[destination] = (sources[kinds.index(d3d9.REGISTER_CONST)][1], swizzle[0])
        if opcode == d3d9.OPCODE_TEXLD:
            sampler = sources[-1][1]
            samplers.add(sampler)
            if sampler == 0 and diffuse is None:
                diffuse_register = destination
        elif opcode == d3d9.OPCODE_MUL and diffuse_register is not None and diffuse is None:
            if destination == diffuse_register and diffuse_register in sources:
                diffuse = next((index for kind, index in sources if kind == d3d9.REGISTER_CONST), None)
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
    return {
        f"{checksum:08x}": {"name": name, **classify(code)}
        for checksum, name, code in _records(Path(path).read_bytes())
    }


def lightmap_slot(template, sampler_count):
    """Return the lightmap sampler index for a lightmapped (ChkBox) template, or None."""
    name = template["name"]
    if "ChkBox" not in name or sampler_count < 2:
        return None
    return sampler_count - (2 if name.startswith("Env") else 1)
