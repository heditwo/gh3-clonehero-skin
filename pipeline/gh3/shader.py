"""Translate GH3 PC material templates (D3D9 vs_3_0/ps_3_0 bytecode in MaterialLibrary.bin.xen) to GLSL.

Each template becomes one vertex and one fragment function of the stage surface shader. The functions run GH3's own
instructions; only the engine constant registers (transforms, time, camera, lights) map to Clone Hero values.
All GH3 math runs in GH3 world space: the stage mesh stores z negated, so the shader negates z on input and output.
"""

import struct
from functools import lru_cache
from pathlib import Path

from pipeline.gh3 import materials as gh3_materials

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
SCALAR = {"rcp", "rsq", "exp", "log", "pow"}
SAMPLER_TYPES = {2: "2d", 3: "cube", 4: "volume"}
# Unity texture properties for samplers s0..s9. s1–s5 reuse the legacy stage shader's sampler names.
SAMPLER_NAMES = (
    "_MainTex",
    "_Gh3Lightmap",
    "_Gh3NormalTex",
    "_Gh3SpecularTex",
    "_Gh3LayerTex",
    "_Gh3MaskTex",
    "_Gh3Tex6",
    "_Gh3Tex7",
    "_Gh3Tex8",
    "_Gh3Tex9",
)
VERTEX_CONSTANTS = 16
PIXEL_CONSTANTS = 16
# GH3 engine registers. Vertex: c119 wibble direction, c120–c122 camera axes (inverse view rows), c124–c127 view-projection,
# c155.x time, c160–c163 world, c168–c171 world-view-projection. Pixel: c123 eye, c137 lightmap strengths, c138 normal-map
# fade (start, end, -, 1/range), c140/c142 prop tint, c176 ambient, c177 key light direction, c178 key colour (w: key
# lightmap factor), c179 specular colour, c188–c191 SH (constant, x, y, z).
VERTEX_ENGINE = {
    156: "gh3_zero",
    157: "gh3_zero",
    158: "gh3_zero",
    159: "gh3_zero",
    119: "gh3_c119",
    120: "gh3_c120",
    121: "gh3_c121",
    122: "gh3_c122",
    124: "gh3_c124",
    125: "gh3_c125",
    126: "gh3_c126",
    127: "gh3_c127",
    155: "gh3_c155",
    160: "gh3_c160",
    161: "gh3_c161",
    162: "gh3_c162",
    163: "gh3_c163",
    168: "gh3_c168",
    169: "gh3_c169",
    170: "gh3_c170",
    171: "gh3_c171",
}
PIXEL_ENGINE = {
    123: "gh3_c123",
    137: "gh3_c137",
    138: "gh3_c138",
    140: "gh3_c140",
    142: "gh3_c142",
    176: "gh3_c176",
    177: "gh3_c177",
    178: "gh3_c178",
    179: "gh3_c179",
    188: "gh3_c188",
    189: "gh3_c189",
    190: "gh3_c190",
    191: "gh3_c191",
}
LIGHT_REGISTERS = {176, 177, 178, 179, 188, 189, 190, 191}


class Unsupported(ValueError):
    pass


def _records(data):
    if data[:8] != b"MATL\0\0\0\x02":
        raise ValueError("Unsupported material library")
    count = struct.unpack_from(">I", data, 8)[0]
    base = (20 + count * 4 + 15) & ~15
    shader_base = base + struct.unpack_from(">I", data, 16)[0]
    for index in range(count):
        record = base + struct.unpack_from(">I", data, 20 + index * 4)[0]
        row = struct.unpack_from(">10I", data, record)
        name = data[base + row[1] : data.index(b"\0", base + row[1])].decode()
        descriptor = base + row[7]
        programs = []
        # Variant 0 is the normal scene pass: vertex shader size and offset at +0x50/+0x58, pixel shader at +0x60/+0x68.
        for size_at, offset_at in ((0x50, 0x58), (0x60, 0x68)):
            size = struct.unpack_from(">I", data, descriptor + size_at)[0]
            start = shader_base + struct.unpack_from(">I", data, descriptor + offset_at)[0]
            programs.append(data[start : start + size])
        yield f"{row[2]:08x}", name, programs[0], programs[1]


@lru_cache(maxsize=1)
def library(path=gh3_materials.MATERIAL_LIBRARY):
    """Map each template checksum to (name, vertex bytecode, pixel bytecode)."""
    return {checksum: (name, vs, ps) for checksum, name, vs, ps in _records(Path(path).read_bytes())}


def _float(token):
    return struct.unpack("<f", struct.pack("<I", token))[0]


def _literal(value):
    if value != value or value in (float("inf"), float("-inf")):
        raise Unsupported("Non-finite shader literal")
    text = repr(float(value))
    return text if "e" in text or "." in text else text + ".0"


class Program:
    def __init__(self, code, stage):
        if len(code) < 8 or code[-4:] != b"\xff\xff\0\0":
            raise Unsupported("Missing shader program")
        version = struct.unpack_from("<I", code)[0]
        if version != (0xFFFE0300 if stage == "vs" else 0xFFFF0300):
            raise Unsupported(f"Unsupported {stage} version {version:08x}")
        self.stage = stage
        self.inputs = {}  # v index -> (usage, usage index)
        self.outputs = {}  # o index -> (usage, usage index)
        self.samplers = {}  # s index -> type
        self.defs = {}  # c index -> literal vec4
        self.constants = set()
        self.body = []
        self.temps = 0
        instructions = list(gh3_materials._instructions(code))
        for opcode, arguments in instructions:
            if opcode == 31:
                self._declare(arguments)
            elif opcode == 81:
                index = gh3_materials._register(arguments[0])[1]
                self.defs[index] = "vec4(" + ", ".join(_literal(_float(t)) for t in arguments[1:5]) + ")"
        for opcode, arguments in instructions:
            if opcode in (31, 81):
                continue
            name = OPCODES.get(opcode)
            if name is None:
                raise Unsupported(f"Unsupported {stage} opcode {opcode}")
            self.body.append("    " + self._instruction(name, arguments))

    def _declare(self, arguments):
        usage = arguments[0] & 31
        usage_index = (arguments[0] >> 16) & 15
        kind, index = gh3_materials._register(arguments[1])
        if kind == 10:
            sampler = SAMPLER_TYPES.get((arguments[0] >> 27) & 15)
            if sampler is None:
                raise Unsupported("Unknown sampler type")
            self.samplers[index] = sampler
        elif kind == 1:
            self.inputs[index] = (USAGES.get(usage, f"usage{usage}"), usage_index)
        elif kind == 6:
            self.outputs[index] = (USAGES.get(usage, f"usage{usage}"), usage_index)
        elif kind != 17:
            raise Unsupported(f"Unsupported declaration register type {kind}")

    def register(self, token):
        kind, index = gh3_materials._register(token)
        if token & (1 << 13):
            raise Unsupported("Relative addressing is not supported")
        if kind == 0:
            self.temps = max(self.temps, index + 1)
            return f"r{index}"
        if kind == 1:
            usage = self.inputs.get(index)
            if usage is None:
                raise Unsupported(f"Undeclared input v{index}")
            return f"v{index}"
        if kind == 2:
            if index in self.defs:
                return f"d{index}"
            self.constants.add(index)
            return f"c{index}"
        if kind == 6:
            return f"o{index}"
        if kind == 8:
            if index != 0:
                raise Unsupported("Multiple render targets are not supported")
            return "oC0"
        if kind == 17:
            return "vPos" if index == 0 else "vFace"
        raise Unsupported(f"Unsupported register type {kind}")

    def source(self, token):
        name = self.register(token)
        swizzle = "".join("xyzw"[(token >> (16 + 2 * i)) & 3] for i in range(4))
        value = name if swizzle == "xyzw" else f"{name}.{swizzle}"
        modifier = (token >> 24) & 15
        if modifier == 0:
            return value
        if modifier == 1:
            return f"(-{value})"
        if modifier == 11:
            return f"abs({value})"
        if modifier == 12:
            return f"(-abs({value}))"
        raise Unsupported(f"Unsupported source modifier {modifier}")

    def destination(self, token):
        name = self.register(token)
        mask = "".join("xyzw"[i] for i in range(4) if token >> (16 + i) & 1)
        if (token >> 24) & 15:
            raise Unsupported("Result shift is not supported")
        return name, mask, bool(token >> 20 & 1)

    def _instruction(self, name, arguments):
        if name in ("ifc", "else", "endif"):
            raise Unsupported("Dynamic branches are not supported")
        if name == "texkill":
            return f"if (any(lessThan({self.register(arguments[0])}.xyz, vec3(0.0)))) discard;"
        target, mask, saturate = self.destination(arguments[0])
        s = [self.source(token) for token in arguments[1 : 2 if name == "texld" else None]]
        if name == "texld":
            sampler = gh3_materials._register(arguments[2])[1]
            kind = self.samplers.get(sampler)
            if kind == "volume":
                raise Unsupported("Volume textures are not supported")
            if sampler >= len(SAMPLER_NAMES):
                # s10–s15 are engine render targets (projected prop shadow, framebuffer); they sample as unshadowed white.
                value = "vec4(1.0)"
            elif kind == "cube":
                # The exporter stores each GH3 cube map as a latitude-longitude image (see cube_to_equirect).
                value = f"gh3_cube({SAMPLER_NAMES[sampler]}, {s[0]}.xyz)"
            else:
                value = f"texture({SAMPLER_NAMES[sampler]}, gh3_uv({s[0]}))"
        elif name == "mov":
            value = s[0]
        elif name == "add":
            value = f"({s[0]} + {s[1]})"
        elif name == "sub":
            value = f"({s[0]} - {s[1]})"
        elif name == "mul":
            value = f"({s[0]} * {s[1]})"
        elif name == "mad":
            value = f"({s[0]} * {s[1]} + {s[2]})"
        elif name == "dp3":
            value = f"vec4(dot({s[0]}.xyz, {s[1]}.xyz))"
        elif name == "dp4":
            value = f"vec4(dot({s[0]}, {s[1]}))"
        elif name == "dp2add":
            value = f"vec4(dot({s[0]}.xy, {s[1]}.xy) + {s[2]}.x)"
        elif name == "min":
            value = f"min({s[0]}, {s[1]})"
        elif name == "max":
            value = f"max({s[0]}, {s[1]})"
        elif name == "slt":
            value = f"vec4(lessThan({s[0]}, {s[1]}))"
        elif name == "sge":
            value = f"vec4(greaterThanEqual({s[0]}, {s[1]}))"
        elif name == "frc":
            value = f"fract({s[0]})"
        elif name == "abs":
            value = f"abs({s[0]})"
        elif name == "lrp":
            value = f"mix({s[2]}, {s[1]}, {s[0]})"
        elif name == "cmp":
            value = f"mix({s[2]}, {s[1]}, vec4(greaterThanEqual({s[0]}, vec4(0.0))))"
        elif name == "nrm":
            value = f"vec4({s[0]}.xyz * gh3_rsq(dot({s[0]}.xyz, {s[0]}.xyz)), 0.0)"
        elif name == "crs":
            value = f"vec4(cross({s[0]}.xyz, {s[1]}.xyz), 0.0)"
        elif name == "rcp":
            value = f"vec4(gh3_rcp({s[0]}.x))"
        elif name == "rsq":
            value = f"vec4(gh3_rsq(abs({s[0]}.x)))"
        elif name == "exp":
            value = f"vec4(exp2({s[0]}.x))"
        elif name == "log":
            value = f"vec4(gh3_log(abs({s[0]}.x)))"
        elif name == "pow":
            value = f"vec4(gh3_pow(abs({s[0]}.x), {s[1]}.x))"
        elif name == "sincos":
            value = f"vec4(cos({s[0]}.x), sin({s[0]}.x), 0.0, 0.0)"
        else:
            raise Unsupported(f"Unsupported instruction {name}")
        if saturate:
            value = f"clamp({value}, 0.0, 1.0)"
        if target.startswith("o") and target != "oC0" and target[1:].isdigit():
            usage = self.outputs.get(int(target[1:]))
            if usage is None:
                raise Unsupported(f"Undeclared output {target}")
        if len(mask) == 4:
            return f"{target} = {value};"
        return f"{target}.{mask} = ({value}).{mask};"


def _input_expression(usage, index):
    if usage == "position" and index == 0:
        return "gh3_position"
    if usage == "position" and index == 1:
        return "gh3_position1"
    if usage == "normal" and index == 0:
        return "gh3_normal"
    if usage == "binormal" and index == 0:
        return "gh3_binormal"
    if usage == "binormal" and index == 1:
        return "gh3_binormal1"
    if usage == "tangent" and index == 0:
        return "gh3_tangent"
    # GH3 PC skins characters on the CPU: its vertex shaders read already-skinned streams and have no bone palette.
    # A palette (blend weights or indices) would need its own mapping, so it is reported instead of translated.
    if usage in ("blendweight", "blendindices"):
        raise Unsupported(f"GPU skinning input {usage}{index} is not supported")
    if usage == "color" and index == 0:
        return "in_COLOR0"
    if usage == "texcoord" and 0 <= index <= 5:
        return f"vec4(in_TEXCOORD{index}, 0.0, 1.0)"
    if usage in ("binormal", "tangent", "color", "texcoord"):
        # GH3 meshes for these templates carry no such stream; D3D supplies (0, 0, 0, 1).
        return "vec4(0.0, 0.0, 0.0, 1.0)"
    raise Unsupported(f"Unsupported vertex input {usage}{index}")


def _varying(usage, index):
    if usage == "texcoord" and 0 <= index <= 9:
        return f"gh3_T{index}"
    if usage == "color" and 0 <= index <= 1:
        return f"gh3_C{index}"
    return None


def translate(checksum):
    """Return the GLSL functions and resource needs of one template."""
    name, vs_code, ps_code = library()[checksum]
    vs = Program(vs_code, "vs")
    ps = Program(ps_code, "ps")
    function = f"t{checksum}"
    lines = [f"// {name}", f"void gh3_vs_{function}()", "{"]
    lines += [f"    const vec4 d{i} = {v};" for i, v in sorted(vs.defs.items())]
    for index in sorted(vs.constants):
        if index in VERTEX_ENGINE:
            lines.append(f"    vec4 c{index} = {VERTEX_ENGINE[index]};")
        elif index < VERTEX_CONSTANTS:
            lines.append(f"    vec4 c{index} = _Gh3V{index};")
        else:
            raise Unsupported(f"Unmapped vertex engine constant c{index}")
    for index, (usage, usage_index) in sorted(vs.inputs.items()):
        lines.append(f"    vec4 v{index} = {_input_expression(usage, usage_index)};")
    if vs.temps:
        lines.append("    vec4 " + ", ".join(f"r{i} = vec4(0.0)" for i in range(vs.temps)) + ";")
    position = None
    for index, (usage, usage_index) in sorted(vs.outputs.items()):
        lines.append(f"    vec4 o{index} = vec4(0.0);")
        if usage == "position" and usage_index == 0:
            position = index
    if position is None:
        raise Unsupported("The vertex shader writes no position")
    lines += vs.body
    lines.append(f"    gl_Position = o{position};")
    for index, (usage, usage_index) in sorted(vs.outputs.items()):
        varying = _varying(usage, usage_index)
        if varying:
            lines.append(f"    {varying} = o{index};")
        elif usage not in ("position", "fog", "psize"):
            raise Unsupported(f"Unsupported vertex output {usage}{usage_index}")
    lines.append("}")
    vertex = "\n".join(lines)

    color = gh3_materials.classify(ps_code)["diffuseConstant"]
    lines = [f"// {name}", f"vec4 gh3_ps_{function}()", "{"]
    lines += [f"    const vec4 d{i} = {v};" for i, v in sorted(ps.defs.items())]
    lights = sorted(ps.constants & LIGHT_REGISTERS)
    for index in sorted(ps.constants):
        if index in PIXEL_ENGINE:
            lines.append(f"    vec4 c{index} = {PIXEL_ENGINE[index]}();")
        elif index < PIXEL_CONSTANTS:
            # Scripts set the Pass0 material colour (m_psPass0MaterialColor) on the constant that tints sampler 0.
            lines.append(f"    vec4 c{index} = _Gh3P{index}" + (" * _Color;" if index == color else ";"))
        else:
            raise Unsupported(f"Unmapped pixel engine constant c{index}")
    for index, (usage, usage_index) in sorted(ps.inputs.items()):
        varying = _varying(usage, usage_index)
        if varying is None:
            raise Unsupported(f"Unsupported pixel input {usage}{usage_index}")
        lines.append(f"    vec4 v{index} = {varying};")
    if ps.temps:
        lines.append("    vec4 " + ", ".join(f"r{i} = vec4(0.0)" for i in range(ps.temps)) + ";")
    lines.append("    vec4 oC0 = vec4(0.0);")
    lines += ps.body
    lines += ["    return oC0;", "}"]
    fragment = "\n".join(lines)
    vertex_written = {_varying(*usage) for usage in vs.outputs.values()}
    missing = [_varying(*usage) for usage in ps.inputs.values() if _varying(*usage) not in vertex_written]
    if missing:
        raise Unsupported(f"Pixel inputs not written by the vertex shader: {missing}")
    approximations = []
    if 119 in vs.constants:
        approximations.append("c119 wibble direction is GH3's default (zero)")
    if vs.constants & {156, 157, 158, 159} or any(i >= len(SAMPLER_NAMES) for i in ps.samplers):
        approximations.append("engine shadow projection and render-target samplers are unshadowed")
    inputs = {f"{u}{i}" for u, i in vs.inputs.values()}
    extra = "position1" if "position1" in inputs else "binormal1" if "binormal1" in inputs else ""
    return {
        "name": name,
        "function": function,
        "extraStream": extra,
        "cubeSamplers": sorted(i for i, kind in ps.samplers.items() if kind == "cube"),
        "vertex": vertex,
        "fragment": fragment,
        "samplers": {index: kind for index, kind in ps.samplers.items()},
        "lighting": bool(lights),
        "shading": "lit" if set(lights) & {177, 178, 188, 189, 190, 191} else "ambient" if lights else "unlit",
        "vertexConstants": sorted(i for i in vs.constants if i < VERTEX_CONSTANTS),
        "pixelConstants": sorted(i for i in ps.constants if i < PIXEL_CONSTANTS),
        "colorConstant": color,
        "inputs": sorted({f"{u}{i}" for u, i in vs.inputs.values()}),
        "approximations": approximations,
    }


VERTEX_PROLOGUE = (
    """// Generated by pipeline/gh3/shader.py from GH3's MaterialLibrary: one function per material template.
uniform vec4 _Gh3Template;
"""
    + "".join(f"uniform vec4 _Gh3V{i};\n" for i in range(VERTEX_CONSTANTS))
    + """uniform vec4 _Gh3Time;
uniform vec4 _Gh3ViewRight;
uniform vec4 _Gh3ViewUp;
uniform vec4 _Gh3ViewForward;
in vec2 in_TEXCOORD4;
in vec2 in_TEXCOORD5;
in vec2 in_TEXCOORD6;
in vec2 in_TEXCOORD7;
"""
    + "".join(f"out vec4 gh3_T{i};\n" for i in range(10))
    + """out vec4 gh3_C0;
out vec4 gh3_C1;
vec4 gh3_position, gh3_position1, gh3_normal, gh3_binormal, gh3_binormal1, gh3_tangent;
vec4 gh3_c119, gh3_c120, gh3_c121, gh3_c122, gh3_c124, gh3_c125, gh3_c126, gh3_c127, gh3_c155;
vec4 gh3_zero = vec4(0.0);
vec4 gh3_c160, gh3_c161, gh3_c162, gh3_c163, gh3_c168, gh3_c169, gh3_c170, gh3_c171;
float gh3_rcp(float x) { return 1.0 / x; }
float gh3_rsq(float x) { return inversesqrt(x); }
float gh3_log(float x) { return log2(x); }
float gh3_pow(float x, float y) { return x == 0.0 ? (y == 0.0 ? 1.0 : 0.0) : exp2(y * log2(x)); }
"""
)

VERTEX_MAIN = """
// GH3 world space is the stage mesh space with z negated (F). World W = F M F, world-view-projection = VP M F, view-projection = VP F.
void gh3_vertex_main()
{
    mat4 model = mat4(hlslcc_mtx4x4unity_ObjectToWorld[0], hlslcc_mtx4x4unity_ObjectToWorld[1], hlslcc_mtx4x4unity_ObjectToWorld[2], hlslcc_mtx4x4unity_ObjectToWorld[3]);
    mat4 viewProjection = mat4(hlslcc_mtx4x4unity_MatrixVP[0], hlslcc_mtx4x4unity_MatrixVP[1], hlslcc_mtx4x4unity_MatrixVP[2], hlslcc_mtx4x4unity_MatrixVP[3]);
    const vec4 flip = vec4(1.0, 1.0, -1.0, 1.0);
    gh3_c160 = model[0] * flip; gh3_c161 = model[1] * flip; gh3_c162 = -model[2] * flip; gh3_c163 = model[3] * flip;
    mat4 objectClip = viewProjection * model;
    gh3_c168 = objectClip[0]; gh3_c169 = objectClip[1]; gh3_c170 = -objectClip[2]; gh3_c171 = objectClip[3];
    gh3_c124 = viewProjection[0]; gh3_c125 = viewProjection[1]; gh3_c126 = -viewProjection[2]; gh3_c127 = viewProjection[3];
    gh3_c120 = vec4(_Gh3ViewRight.xyz * flip.xyz, 0.0); gh3_c121 = vec4(_Gh3ViewUp.xyz * flip.xyz, 0.0); gh3_c122 = vec4(_Gh3ViewForward.xyz * flip.xyz, 0.0);
    gh3_c155 = vec4(_Gh3Time.x, 0.0, 0.0, 0.0);
    gh3_c119 = vec4(0.0);
    // D3D expands FLOAT3 vertex elements to (x, y, z, 1).
    gh3_position = vec4(in_POSITION0.xyz * flip.xyz, 1.0);
    gh3_normal = vec4(in_NORMAL0 * flip.xyz, 1.0);
    // Static meshes carry binormal 0 in the tangent channel. Skinned meshes (_Gh3Template.w) carry tangent 0 there, skinned by
    // Unity like GH3's CPU skinning; binormal 0 is the skinned normal x tangent with the sign GH3's own binormal had.
    bool skinned = _Gh3Template.w > 0.5;
    gh3_tangent = skinned ? vec4(in_TANGENT0.xyz * flip.xyz, 1.0) : vec4(0.0, 0.0, 0.0, 1.0);
    gh3_binormal = skinned ? vec4(cross(in_NORMAL0, in_TANGENT0.xyz) * in_TANGENT0.w * flip.xyz, 1.0) : vec4(in_TANGENT0.xyz * flip.xyz, 1.0);
    // Texture coordinates 6-7 carry the template's extra stream: position 1 (billboards) or binormal 1 (two-tangent meshes).
    gh3_position1 = vec4(vec3(in_TEXCOORD6, in_TEXCOORD7.x) * flip.xyz, 1.0);
    gh3_binormal1 = gh3_position1;
    gh3_vs_template(int(_Gh3Template.x + 0.5));
}
"""

FRAGMENT_PROLOGUE = (
    """// Generated by pipeline/gh3/shader.py from GH3's MaterialLibrary: one function per material template.
uniform vec4 _Gh3Template;
"""
    + "".join(f"uniform vec4 _Gh3P{i};\n" for i in range(PIXEL_CONSTANTS))
    + """uniform vec4 _Gh3ViewEye;
"""
    + "".join(f"uniform sampler2D {name};\n" for name in SAMPLER_NAMES[6:])
    + "".join(f"in vec4 gh3_T{i};\n" for i in range(10))
    + """in vec4 gh3_C0;
in vec4 gh3_C1;
float gh3_rcp(float x) { return 1.0 / x; }
float gh3_rsq(float x) { return inversesqrt(x); }
float gh3_log(float x) { return log2(x); }
float gh3_pow(float x, float y) { return x == 0.0 ? (y == 0.0 ? 1.0 : 0.0) : exp2(y * log2(x)); }
vec2 gh3_uv(vec4 uv) { return vec2(uv.x, 1.0 - uv.y); }
// D3D cube lookup on a latitude-longitude image: u turns about +y from +z towards +x, v runs from +y (top) to -y.
vec4 gh3_cube(sampler2D map, vec3 direction)
{
    vec3 d = normalize(direction);
    vec2 uv = vec2(atan(d.x, d.z) * 0.15915494 + 0.5, acos(clamp(d.y, -1.0, 1.0)) * 0.31830989);
    return textureLod(map, vec2(uv.x, 1.0 - uv.y), 0.0);
}
const vec4 gh3_flip = vec4(1.0, 1.0, -1.0, 1.0);
// The light-data rows are in stage space; GH3 space negates z (directions and the SH z coefficient).
vec4 gh3_c176() { return Light(0); }
vec4 gh3_c177() { return Light(1) * gh3_flip; }
vec4 gh3_c178() { return Light(2); }
vec4 gh3_c179() { return Light(2); }
vec4 gh3_c188() { return Light(3); }
vec4 gh3_c189() { return Light(4); }
vec4 gh3_c190() { return Light(5); }
vec4 gh3_c191() { return -Light(6); }
vec4 gh3_c123() { return vec4(_Gh3ViewEye.xyz * gh3_flip.xyz, 1.0); }
vec4 gh3_c137() { return vec4(1.0); }
vec4 gh3_c138() { return vec4(0.0, 40.0, 30.0, 0.1); }
vec4 gh3_c140() { return vec4(0.0); }
vec4 gh3_c142() { return vec4(0.2, 0.8, 0.35, 0.65); }
"""
)

FRAGMENT_MAIN = """
// GH3's material bind: alpha test (flags bit 6 clear) passes alpha > reference, with ALPHAFUNC GREATER set at device creation.
vec4 gh3_fragment_main()
{
    vec4 color = gh3_ps_template(int(_Gh3Template.x + 0.5));
    if (_Gh3Template.y > 0.5 && floor(clamp(color.a, 0.0, 1.0) * 255.0 + 0.5) <= _Gh3Template.z) discard;
    return color;
}
"""


def uber(checksums):
    """Return (vertex GLSL, fragment GLSL, template table, failures) for the given template checksums."""
    vertex, fragment, table, failures = [], [], {}, {}
    for checksum in sorted(set(checksums)):
        if checksum not in library():
            failures[checksum] = "Template is not in MaterialLibrary"
            continue
        try:
            result = translate(checksum)
        except Unsupported as error:
            failures[checksum] = f"{library()[checksum][0]}: {error}"
            continue
        index = len(table) + 1
        table[checksum] = {
            "index": index,
            "name": result["name"],
            "shading": result["shading"],
            "extraStream": result["extraStream"],
            "samplers": {str(k): v for k, v in result["samplers"].items()},
            "approximations": result["approximations"],
        }
        vertex.append(result["vertex"])
        fragment.append(result["fragment"])
        table[checksum]["_function"] = result["function"]
    vertex_switch = ["void gh3_vs_template(int index)", "{", "    switch (index)", "    {"]
    fragment_switch = ["vec4 gh3_ps_template(int index)", "{", "    switch (index)", "    {"]
    for checksum, row in table.items():
        vertex_switch.append(f"        case {row['index']}: gh3_vs_{row['_function']}(); break;")
        fragment_switch.append(f"        case {row['index']}: return gh3_ps_{row['_function']}();")
    vertex_switch += ["        default: gl_Position = vec4(0.0); break;", "    }", "}"]
    fragment_switch += ["        default: return vec4(1.0, 0.0, 1.0, 1.0);", "    }", "}"]
    for row in table.values():
        row.pop("_function")
    return (
        VERTEX_PROLOGUE + "\n\n".join(vertex + ["\n".join(vertex_switch)]) + VERTEX_MAIN,
        FRAGMENT_PROLOGUE + "\n\n".join(fragment + ["\n".join(fragment_switch)]) + FRAGMENT_MAIN,
        table,
        failures,
    )
