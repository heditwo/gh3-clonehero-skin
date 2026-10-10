import struct
import unittest

from pipeline.render import d3d11

# Vertex parameter blob of Hidden/BlitCopy (Direct3D 11) in Windows Clone Hero 1.1.0.6142 (Unity 2022.3.62f2).
BLIT_VERTEX_PARAMETERS = bytes.fromhex(
    "ba750a0c04000000000000000000000000000000000000000800000024476c6f62616c7340000000010000000b0000005f4d61696e5465785f5354"
    "00000000000100000004000000000000000000000020000000000000000c000000556e69747950657244726177b00000000100000013000000756e"
    "6974795f4f626a656374546f576f726c6400000000000400000004000000010000000000000000000000000000000d000000556e69747950657246"
    "72616d6500000070010000010000000e000000756e6974795f4d6174726978565000000000000004000000040000000100000000000000100100"
    "0000000000030000000800000024476c6f62616c730100000000000000000000000c000000556e69747950657244726177010000000100000000"
    "0000000d000000556e6974795065724672616d65000000010000000200000000000000"
)


def dxbc(buffers=(), bindings=(), inputs=()):
    """A shader model 4.0 DXBC container with RDEF (buffers: name, size, [(variable, offset)]) and ISGN chunks."""
    strings, rdef_tail = {}, bytearray()

    def string(text):
        if text not in strings:
            strings[text] = len(rdef_tail)
            rdef_tail.extend(text.encode() + b"\0")
        return strings[text]

    binding_offset = 28
    buffer_offset = binding_offset + 32 * len(bindings)
    variable_offset = buffer_offset + 24 * len(buffers)
    variable_count = sum(len(variables) for _, _, variables in buffers)
    type_offset = variable_offset + 24 * variable_count
    tail_offset = type_offset + 16
    body = bytearray()
    for name, kind, point, dimension in bindings:
        body += struct.pack("<8I", tail_offset + string(name), kind, 5 if kind == 2 else 0, dimension, 0, point, 1, 0)
    first = 0
    for name, size, variables in buffers:
        body += struct.pack("<6I", tail_offset + string(name), len(variables), variable_offset + 24 * first, size, 0, 0)
        first += len(variables)
    for _, _, variables in buffers:
        for variable, offset in variables:
            body += struct.pack("<6I", tail_offset + string(variable), offset, 16, 2, type_offset, 0)
    body += struct.pack("<6HI", 1, 3, 1, 4, 0, 0, 0)
    rdef = (
        struct.pack("<4I2BH2I", len(buffers), buffer_offset, len(bindings), binding_offset, 0, 4, 0xFFFE, 0, 0)
        + body
        + rdef_tail
    )
    isgn = bytearray(struct.pack("<2I", len(inputs), 8))
    names = bytearray()
    for index, (name, semantic_index) in enumerate(inputs):
        isgn += struct.pack("<5I4B", 8 + 24 * len(inputs) + len(names), semantic_index, 0, 3, index, 15, 15, 0, 0)
        names += name.encode() + b"\0"
    isgn += names
    chunks = [(b"RDEF", bytes(rdef)), (b"ISGN", bytes(isgn))]
    offset, table, data = 32 + 4 * len(chunks), b"", b""
    for tag, chunk in chunks:
        table += struct.pack("<I", offset + len(data))
        data += tag + struct.pack("<I", len(chunk)) + chunk
    return b"DXBC" + bytes(16) + struct.pack("<3I", 1, offset + len(data), len(chunks)) + table + data


class D3D11Tests(unittest.TestCase):
    def test_prepare_moves_unity_matrices_to_unity_buffers(self):
        source = (
            "#version 150\n#extension GL_ARB_explicit_attrib_location : require\nuniform vec4 hlslcc_mtx4x4unity_ObjectToWorld[4];\n"
            "uniform vec4 hlslcc_mtx4x4unity_MatrixVP[4];\nuniform vec4 _Gh3Color;\nlayout(location = 0) uniform sampler2D _MainTex;\n"
            "uniform sampler2D _Gh3Mask;\nin vec4 in_POSITION0;\nin vec2 in_TEXCOORD6;\nvoid main() {}\n"
        )
        glsl, semantics, buffers = d3d11.prepare(source, True)
        self.assertIn("#version 430\n#define GH3_D3D11 1\n", glsl)
        self.assertIn(
            "layout(std140, binding = 1) uniform UnityPerDraw { vec4 hlslcc_mtx4x4unity_ObjectToWorld[4]; };", glsl
        )
        self.assertIn(
            "layout(std140, binding = 2) uniform UnityPerFrame { vec4 gh3_UnityPerFrame0[17]; vec4 hlslcc_mtx4x4unity_MatrixVP[4]; };",
            glsl,
        )
        self.assertIn("layout(binding = 0) uniform sampler2D _MainTex;", glsl)
        self.assertIn("layout(binding = 1) uniform sampler2D _Gh3Mask;", glsl)
        self.assertIn("layout(location = 1) in vec2 in_TEXCOORD6;", glsl)
        self.assertEqual(semantics, ["POSITION", "TEXCOORD6"])
        self.assertEqual(
            buffers,
            {
                "UnityPerDraw": {0: ("hlslcc_mtx4x4unity_ObjectToWorld", 4)},
                "UnityPerFrame": {272: ("hlslcc_mtx4x4unity_MatrixVP", 4)},
            },
        )

    def test_parameters_match_the_unity_blit_shader(self):
        program = dxbc(
            [
                ("$Globals", 64, [("_MainTex_ST", 32)]),
                ("UnityPerDraw", 64, [("_12_hlslcc_mtx4x4unity_ObjectToWorld", 0)]),
                ("UnityPerFrame", 336, [("_20_gh3_UnityPerFrame0", 0), ("_20_hlslcc_mtx4x4unity_MatrixVP", 272)]),
            ],
            [("$Globals", 0, 0, 0), ("UnityPerDraw", 0, 1, 0), ("UnityPerFrame", 0, 2, 0)],
        )
        builtins = {
            "UnityPerDraw": {0: ("hlslcc_mtx4x4unity_ObjectToWorld", 4)},
            "UnityPerFrame": {272: ("hlslcc_mtx4x4unity_MatrixVP", 4)},
        }
        blob, counts = d3d11.parameters(program, builtins)
        self.assertEqual(blob, BLIT_VERTEX_PARAMETERS)
        self.assertEqual(counts, (0, 3, 0))

    def test_textures_use_their_combined_sampler(self):
        program = dxbc(
            [("$Globals", 16, [("_Gh3Color", 0)])],
            [("__MainTex_sampler", 3, 0, 0), ("_MainTex", 2, 0, 4), ("$Globals", 0, 0, 0)],
        )
        blob, counts = d3d11.parameters(program, {})
        self.assertEqual(counts, (1, 1, 1))
        self.assertIn(d3d11.aligned("_MainTex") + struct.pack("<4I", 0, 0, 0, 4), blob)

    def test_vertex_program_binds_unity_channels(self):
        program = dxbc(inputs=[("POSITION", 0), ("NORMAL", 0), ("TEXCOORD", 0), ("TEXCOORD", 7)])
        blob = d3d11.program(struct.pack("<2I", d3d11.VERSION, 15) + bytes(24), program, (0, 3, 0), True)
        self.assertEqual(blob[32:36], bytes((2, 0, 3, 0)))
        self.assertEqual(blob[70:74], b"DXBC")
        self.assertEqual(
            struct.unpack_from("<10I", blob, len(blob) - 40), (1 | 2 | 16 | 2048, 4, 0, 0, 1, 1, 4, 5, 11, 12)
        )

    def test_pixel_inputs_get_the_full_vertex_signature(self):
        vertex = (
            "struct SPIRV_Cross_Output\n{\n    float4 color : TEXCOORD0;\n    float2 uv : TEXCOORD1;\n"
            "    float4 gl_Position : SV_Position;\n};\n"
        )
        fragment = (
            "struct SPIRV_Cross_Input\n{\n    float2 uv : TEXCOORD1;\n};\n\nstruct SPIRV_Cross_Output\n{\n    float4 SV_Target0 : SV_Target0;\n};\n"
            "SPIRV_Cross_Output main(SPIRV_Cross_Input stage_input)\n"
        )
        matched = d3d11.match_inputs(vertex, fragment)
        self.assertIn(
            "struct SPIRV_Cross_Input\n{\n    float4 color : TEXCOORD0;\n    float2 uv : TEXCOORD1;\n    float4 gl_Position : SV_Position;\n};",
            matched,
        )
        empty = d3d11.match_inputs(
            vertex, "struct SPIRV_Cross_Output\n{\n    float4 SV_Target0 : SV_Target0;\n};\nSPIRV_Cross_Output main()\n"
        )
        self.assertIn("SPIRV_Cross_Output main(SPIRV_Cross_Input stage_input)", empty)
        self.assertIn("float2 uv : TEXCOORD1;", empty)


if __name__ == "__main__":
    unittest.main()
