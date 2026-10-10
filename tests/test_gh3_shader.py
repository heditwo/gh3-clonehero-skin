import shutil
import struct
import subprocess
import tempfile
import unittest
from pathlib import Path

from pipeline.gh3 import shader as gh3_shader
from pipeline.render import d3d11

ROOT = Path(__file__).resolve().parent.parent

VENUE_TEMPLATES = {
    "D_3ChkBox_Pass0",
    "D_N_S_4ChkBox",
    "D_1VertexColorOnly",
    "Billboard_Fast",
    "Wibble_Simple",
    "AnimatedTexture",
    "D_S_3ChkBox_Pass0",
    "D_3ChkBox_Pass0_Pass1",
    "D_N_S_1SeperateChnl",
    "EnvMask_D_N_S_2ChkBox",
    "D_3ChkBox_Pass1",
    "D_1VertexColorAmbientLit",
    "AnimatedTexture_Lit",
    "D_N_S_4ChkBox_Pass1_Pass2",
    "D_N_S_4ChkBox_Pass1",
    "Env_D_N_S_Decal_3ChkBox",
    "Wibble_Pos_Pass0",
    "D_NBlend_S_3ChkBox_Pass0",
    "D_S_3ChkBox_Pass0_Pass1",
    "Wibble_Pos_Simple",
    "Wibble_Rot",
    "Env_D_N_S_Decal_3ChkBox_Pass2",
    "D_N_S_Prop_ChkBox",
    "Env_D_N_S_Decal_3ChkBox_Pass1",
    "Wibble",
    "Wibble_Simple_Billboard",
    "D_NBlend_S_3ChkBox_Pass0_Pass1",
    "Flare",
    "D_N_S_Prop_SeperateChnl",
    "AnimatedTexture_Billboard",
    "SkinShader",
    "SkinShader_Pass1",
    "SkinShader_VertexColor",
    "HairShader",
}


def token(kind, index, swizzle=0xE4, modifier=0, mask=15, saturate=False, destination=False):
    value = (1 << 31) | ((kind & 7) << 28) | ((kind & 24) << 8) | index
    if destination:
        return value | (mask << 16) | (int(saturate) << 20)
    return value | (swizzle << 16) | (modifier << 24)


def program(stage, *instructions):
    words = [0xFFFE0300 if stage == "vs" else 0xFFFF0300]
    for opcode, arguments in instructions:
        words += [opcode | (len(arguments) << 24), *arguments]
    return struct.pack(f"<{len(words)}I", *words) + b"\xff\xff\0\0"


class TranslatorTests(unittest.TestCase):
    def test_instruction_semantics(self):
        code = program(
            "ps",
            (31, [0x80000005, token(1, 0, destination=True)]),
            (
                4,
                [
                    token(0, 1, mask=3, saturate=True, destination=True),
                    token(1, 0, 0x00),
                    token(2, 2, modifier=1),
                    token(2, 3),
                ],
            ),
            (18, [token(0, 2, destination=True), token(0, 1, 0x55), token(1, 0), token(2, 2)]),
            (88, [token(0, 3, destination=True), token(0, 2, modifier=11), token(2, 0), token(2, 1)]),
        )
        lines = gh3_shader.Program(code, "ps").body
        self.assertEqual(lines[0].strip(), "r1.xy = (clamp((v0.xxxx * (-c2) + c3), 0.0, 1.0)).xy;")
        # lrp dst, s0, s1, s2 = s0*s1 + (1-s0)*s2; cmp selects s1 where s0 >= 0.
        self.assertEqual(lines[1].strip(), "r2 = mix(c2, v0, r1.yyyy);")
        self.assertEqual(lines[2].strip(), "r3 = mix(c1, c0, vec4(greaterThanEqual(abs(r2), vec4(0.0))));")

    def test_gpu_skinning_inputs_are_reported(self):
        code = program(
            "vs",
            (31, [0x80000001, token(1, 0, destination=True)]),
            (31, [0x80000000, token(1, 1, destination=True)]),
            (31, [0x80000000, token(6, 0, destination=True)]),
            (1, [token(6, 0, destination=True), token(1, 1)]),
        )
        with self.assertRaises(gh3_shader.Unsupported):
            gh3_shader._input_expression("blendweight", 0)
        self.assertEqual(gh3_shader.Program(code, "vs").inputs[0], ("blendweight", 0))

    def test_unsupported_instructions_are_reported(self):
        code = program("ps", (41 | (1 << 16), [token(2, 0), token(2, 1)]))
        with self.assertRaises(gh3_shader.Unsupported):
            gh3_shader.Program(code, "ps")


@unittest.skipUnless(gh3_shader.gh3_materials.MATERIAL_LIBRARY.exists(), "GH3 MaterialLibrary is not installed")
class LibraryTests(unittest.TestCase):
    def test_every_venue_template_translates(self):
        names = {checksum: row[0] for checksum, row in gh3_shader.library().items()}
        checksums = [checksum for checksum, name in names.items() if name in VENUE_TEMPLATES]
        self.assertEqual({names[c] for c in checksums}, VENUE_TEMPLATES)
        _, _, table, failures = gh3_shader.uber(checksums)
        self.assertEqual(failures, {})
        self.assertEqual(len(table), len(VENUE_TEMPLATES))

    @unittest.skipUnless(shutil.which("glslangValidator"), "glslangValidator is not installed")
    def test_generated_stage_shader_compiles(self):
        source = (ROOT / "src/Stage/Shaders/Stage.glsl").read_text()
        names = {checksum: row[0] for checksum, row in gh3_shader.library().items()}
        vertex, fragment, _, _ = gh3_shader.uber([c for c, n in names.items() if n in VENUE_TEMPLATES])
        source = source.replace("// GH3_TEMPLATE_VERTEX", vertex).replace("// GH3_TEMPLATE_FRAGMENT", fragment)
        with tempfile.TemporaryDirectory() as folder:
            for stage, body in zip(("vert", "frag"), d3d11.split(source)):
                # OpenGL Core as Unity compiles it, and the Direct3D 11 variant as glslang reads it for SPIR-V.
                for name, text, options in (
                    ("gl", body, []),
                    ("d3d11", d3d11.prepare(body, stage == "vert")[0], ["-G", "--aml"]),
                ):
                    path = Path(folder) / f"stage-{name}.{stage}"
                    path.write_text(text)
                    result = subprocess.run(
                        ["glslangValidator", *options, "-o", str(path) + ".spv", str(path)]
                        if options
                        else ["glslangValidator", str(path)],
                        capture_output=True,
                        text=True,
                    )
                    self.assertEqual(result.returncode, 0, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
