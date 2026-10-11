"""Translate GH3 PC material templates (D3D9 vs_3_0/ps_3_0 bytecode in MaterialLibrary.bin.xen) to GLSL.

Each template becomes one vertex and one fragment function of the stage surface shader. The functions run GH3's own
instructions; only the engine constant registers (transforms, time, camera, lights) map to Clone Hero values.
All GH3 math runs in GH3 world space: the stage mesh stores z negated, so the shader negates z on input and output.
"""

import struct
from functools import lru_cache
from pathlib import Path

from pipeline.gh3 import d3d9, materials as gh3_materials

SCALAR = {"rcp", "rsq", "exp", "log", "pow"}
# GLSL for each instruction; {0}, {1}, {2} are the source operands.
EXPRESSIONS = {
    "mov": "{0}",
    "add": "({0} + {1})",
    "sub": "({0} - {1})",
    "mul": "({0} * {1})",
    "mad": "({0} * {1} + {2})",
    "dp3": "vec4(dot({0}.xyz, {1}.xyz))",
    "dp4": "vec4(dot({0}, {1}))",
    "dp2add": "vec4(dot({0}.xy, {1}.xy) + {2}.x)",
    "min": "min({0}, {1})",
    "max": "max({0}, {1})",
    "slt": "vec4(lessThan({0}, {1}))",
    "sge": "vec4(greaterThanEqual({0}, {1}))",
    "frc": "fract({0})",
    "abs": "abs({0})",
    "lrp": "mix({2}, {1}, {0})",
    "cmp": "mix({2}, {1}, vec4(greaterThanEqual({0}, vec4(0.0))))",
    "nrm": "vec4({0}.xyz * gh3_rsq(dot({0}.xyz, {0}.xyz)), 0.0)",
    "crs": "vec4(cross({0}.xyz, {1}.xyz), 0.0)",
    "rcp": "vec4(gh3_rcp({0}.x))",
    "rsq": "vec4(gh3_rsq(abs({0}.x)))",
    "exp": "vec4(exp2({0}.x))",
    "log": "vec4(gh3_log(abs({0}.x)))",
    "pow": "vec4(gh3_pow(abs({0}.x), {1}.x))",
    "sincos": "vec4(cos({0}.x), sin({0}.x), 0.0, 0.0)",
}
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
# A template that reads the key light or the SH registers is lit; one that reads only the ambient is ambient.
KEY_LIGHT_REGISTERS = {177, 178, 188, 189, 190, 191}
WIBBLE_DIRECTION_REGISTER = 119
SHADOW_REGISTERS = {156, 157, 158, 159}


class UnsupportedShaderError(ValueError):
    pass


@lru_cache(maxsize=1)
def library(path=gh3_materials.MATERIAL_LIBRARY):
    """Map each template checksum to (name, vertex bytecode, pixel bytecode)."""
    records = gh3_materials.library_records(Path(path).read_bytes())
    return {f"{checksum:08x}": (name, vs, ps) for checksum, name, vs, ps in records}


def _float(token):
    return struct.unpack("<f", struct.pack("<I", token))[0]


def _literal(value):
    if value != value or value in (float("inf"), float("-inf")):
        raise UnsupportedShaderError("Non-finite shader literal")
    text = repr(float(value))
    return text if "e" in text or "." in text else text + ".0"


class Program:
    def __init__(self, code, stage):
        if len(code) < 8 or code[-4:] != d3d9.END_TOKEN:
            raise UnsupportedShaderError("Missing shader program")
        version = struct.unpack_from("<I", code)[0]
        if version != (d3d9.VERSION_VS_3_0 if stage == "vs" else d3d9.VERSION_PS_3_0):
            raise UnsupportedShaderError(f"Unsupported {stage} version {version:08x}")
        self.stage = stage
        self.inputs = {}  # v index -> (usage, usage index)
        self.outputs = {}  # o index -> (usage, usage index)
        self.samplers = {}  # s index -> type
        self.defs = {}  # c index -> literal vec4
        self.constants = set()
        self.body = []
        self.temps = 0
        instructions = list(d3d9.instructions(code))
        for opcode, arguments in instructions:
            if opcode == d3d9.OPCODE_DCL:
                self._declare(arguments)
            elif opcode == d3d9.OPCODE_DEF:
                index = d3d9.register(arguments[0])[1]
                self.defs[index] = "vec4(" + ", ".join(_literal(_float(t)) for t in arguments[1:5]) + ")"
        for opcode, arguments in instructions:
            if opcode in (d3d9.OPCODE_DCL, d3d9.OPCODE_DEF):
                continue
            name = d3d9.OPCODES.get(opcode)
            if name is None:
                raise UnsupportedShaderError(f"Unsupported {stage} opcode {opcode}")
            self.body.append("    " + self._instruction(name, arguments))

    def _declare(self, arguments):
        usage = arguments[0] & 31
        usage_index = (arguments[0] >> 16) & 15
        kind, index = d3d9.register(arguments[1])
        if kind == d3d9.REGISTER_SAMPLER:
            sampler = d3d9.SAMPLER_TYPES.get((arguments[0] >> 27) & 15)
            if sampler is None:
                raise UnsupportedShaderError("Unknown sampler type")
            self.samplers[index] = sampler
        elif kind == d3d9.REGISTER_INPUT:
            self.inputs[index] = (d3d9.USAGES.get(usage, f"usage{usage}"), usage_index)
        elif kind == d3d9.REGISTER_OUTPUT:
            self.outputs[index] = (d3d9.USAGES.get(usage, f"usage{usage}"), usage_index)
        elif kind != d3d9.REGISTER_MISC:
            raise UnsupportedShaderError(f"Unsupported declaration register type {kind}")

    def register(self, token):
        kind, index = d3d9.register(token)
        if token & d3d9.RELATIVE_ADDRESSING:
            raise UnsupportedShaderError("Relative addressing is not supported")
        if kind == d3d9.REGISTER_TEMP:
            self.temps = max(self.temps, index + 1)
            return f"r{index}"
        if kind == d3d9.REGISTER_INPUT:
            usage = self.inputs.get(index)
            if usage is None:
                raise UnsupportedShaderError(f"Undeclared input v{index}")
            return f"v{index}"
        if kind == d3d9.REGISTER_CONST:
            if index in self.defs:
                return f"d{index}"
            self.constants.add(index)
            return f"c{index}"
        if kind == d3d9.REGISTER_OUTPUT:
            return f"o{index}"
        if kind == d3d9.REGISTER_COLOR_OUT:
            if index != 0:
                raise UnsupportedShaderError("Multiple render targets are not supported")
            return "oC0"
        if kind == d3d9.REGISTER_MISC:
            return "vPos" if index == 0 else "vFace"
        raise UnsupportedShaderError(f"Unsupported register type {kind}")

    def source(self, token):
        name = self.register(token)
        swizzle = "".join("xyzw"[component] for component in d3d9.swizzle(token))
        value = name if swizzle == "xyzw" else f"{name}.{swizzle}"
        modifier = d3d9.source_modifier(token)
        if modifier == d3d9.SOURCE_MODIFIER_NONE:
            return value
        if modifier == d3d9.SOURCE_MODIFIER_NEGATE:
            return f"(-{value})"
        if modifier == d3d9.SOURCE_MODIFIER_ABS:
            return f"abs({value})"
        if modifier == d3d9.SOURCE_MODIFIER_ABS_NEGATE:
            return f"(-abs({value}))"
        raise UnsupportedShaderError(f"Unsupported source modifier {modifier}")

    def destination(self, token):
        name = self.register(token)
        mask = "".join("xyzw"[i] for i in range(4) if token >> (16 + i) & 1)
        if (token >> 24) & 15:
            raise UnsupportedShaderError("Result shift is not supported")
        return name, mask, bool(token >> 20 & 1)

    def _instruction(self, name, arguments):
        if name in ("ifc", "else", "endif"):
            raise UnsupportedShaderError("Dynamic branches are not supported")
        if name == "texkill":
            return f"if (any(lessThan({self.register(arguments[0])}.xyz, vec3(0.0)))) discard;"
        target, mask, saturate = self.destination(arguments[0])
        s = [self.source(token) for token in arguments[1 : 2 if name == "texld" else None]]
        if name == "texld":
            sampler = d3d9.register(arguments[2])[1]
            kind = self.samplers.get(sampler)
            if kind == "volume":
                raise UnsupportedShaderError("Volume textures are not supported")
            if sampler >= len(SAMPLER_NAMES):
                # s10–s15 are engine render targets (projected prop shadow, framebuffer); they sample as unshadowed white.
                value = "vec4(1.0)"
            elif kind == "cube":
                # The exporter stores each GH3 cube map as a latitude-longitude image (see cube_to_equirect).
                value = f"gh3_cube({SAMPLER_NAMES[sampler]}, {s[0]}.xyz)"
            else:
                value = f"texture({SAMPLER_NAMES[sampler]}, gh3_uv({s[0]}))"
        elif name in EXPRESSIONS:
            value = EXPRESSIONS[name].format(*s)
        else:
            raise UnsupportedShaderError(f"Unsupported instruction {name}")
        if saturate:
            value = f"clamp({value}, 0.0, 1.0)"
        if target.startswith("o") and target != "oC0" and target[1:].isdigit():
            usage = self.outputs.get(int(target[1:]))
            if usage is None:
                raise UnsupportedShaderError(f"Undeclared output {target}")
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
        raise UnsupportedShaderError(f"GPU skinning input {usage}{index} is not supported")
    if usage == "color" and index == 0:
        return "in_COLOR0"
    if usage == "texcoord" and 0 <= index <= 5:
        return f"vec4(in_TEXCOORD{index}, 0.0, 1.0)"
    if usage in ("binormal", "tangent", "color", "texcoord"):
        # GH3 meshes for these templates carry no such stream; D3D supplies (0, 0, 0, 1).
        return "vec4(0.0, 0.0, 0.0, 1.0)"
    raise UnsupportedShaderError(f"Unsupported vertex input {usage}{index}")


def _varying(usage, index):
    if usage == "texcoord" and 0 <= index <= 9:
        return f"gh3_T{index}"
    if usage == "color" and 0 <= index <= 1:
        return f"gh3_C{index}"
    return None


def _vertex_function(name, function, vs):
    lines = [f"// {name}", f"void gh3_vs_{function}()", "{"]
    lines += [f"    const vec4 d{i} = {v};" for i, v in sorted(vs.defs.items())]
    for index in sorted(vs.constants):
        if index in VERTEX_ENGINE:
            lines.append(f"    vec4 c{index} = {VERTEX_ENGINE[index]};")
        elif index < VERTEX_CONSTANTS:
            lines.append(f"    vec4 c{index} = _Gh3V{index};")
        else:
            raise UnsupportedShaderError(f"Unmapped vertex engine constant c{index}")
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
        raise UnsupportedShaderError("The vertex shader writes no position")
    lines += vs.body
    lines.append(f"    gl_Position = o{position};")
    for index, (usage, usage_index) in sorted(vs.outputs.items()):
        varying = _varying(usage, usage_index)
        if varying:
            lines.append(f"    {varying} = o{index};")
        elif usage not in ("position", "fog", "psize"):
            raise UnsupportedShaderError(f"Unsupported vertex output {usage}{usage_index}")
    lines.append("}")
    return "\n".join(lines)


def _fragment_function(name, function, ps, color):
    lines = [f"// {name}", f"vec4 gh3_ps_{function}()", "{"]
    lines += [f"    const vec4 d{i} = {v};" for i, v in sorted(ps.defs.items())]
    for index in sorted(ps.constants):
        if index in PIXEL_ENGINE:
            lines.append(f"    vec4 c{index} = {PIXEL_ENGINE[index]}();")
        elif index < PIXEL_CONSTANTS:
            # Scripts set the Pass0 material colour (m_psPass0MaterialColor) on the constant that tints sampler 0.
            lines.append(f"    vec4 c{index} = _Gh3P{index}" + (" * _Color;" if index == color else ";"))
        else:
            raise UnsupportedShaderError(f"Unmapped pixel engine constant c{index}")
    for index, (usage, usage_index) in sorted(ps.inputs.items()):
        varying = _varying(usage, usage_index)
        if varying is None:
            raise UnsupportedShaderError(f"Unsupported pixel input {usage}{usage_index}")
        lines.append(f"    vec4 v{index} = {varying};")
    if ps.temps:
        lines.append("    vec4 " + ", ".join(f"r{i} = vec4(0.0)" for i in range(ps.temps)) + ";")
    lines.append("    vec4 oC0 = vec4(0.0);")
    lines += ps.body
    lines += ["    return oC0;", "}"]
    return "\n".join(lines)


def translate(checksum):
    """Return the GLSL functions and resource needs of one template."""
    name, vs_code, ps_code = library()[checksum]
    vs = Program(vs_code, "vs")
    ps = Program(ps_code, "ps")
    function = f"t{checksum}"
    vertex = _vertex_function(name, function, vs)
    color = gh3_materials.classify(ps_code)["diffuseConstant"]
    lights = sorted(ps.constants & LIGHT_REGISTERS)
    fragment = _fragment_function(name, function, ps, color)
    vertex_written = {_varying(*usage) for usage in vs.outputs.values()}
    missing = [_varying(*usage) for usage in ps.inputs.values() if _varying(*usage) not in vertex_written]
    if missing:
        raise UnsupportedShaderError(f"Pixel inputs not written by the vertex shader: {missing}")
    approximations = []
    if WIBBLE_DIRECTION_REGISTER in vs.constants:
        approximations.append("c119 wibble direction is GH3's default (zero)")
    if vs.constants & SHADOW_REGISTERS or any(i >= len(SAMPLER_NAMES) for i in ps.samplers):
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
        "shading": "lit" if set(lights) & KEY_LIGHT_REGISTERS else "ambient" if lights else "unlit",
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
        except UnsupportedShaderError as error:
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
    for row in table.values():
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
