"""Build the stage shader bundles (OpenGL Core and Direct3D 11 programs) with the installed Clone Hero serialization format."""

import argparse
import struct
from copy import copy, deepcopy
from hashlib import sha256
from pathlib import Path

import lz4.block
import UnityPy
from UnityPy.enums import ClassIDType
from UnityPy.files import ObjectReader
from UnityPy.helpers.Tpk import get_typetree_node

from pipeline.gh3 import shader as gh3_shader
from pipeline.paths import GAME, LIBRARY, ROOT, WINDOWS_GAME
from pipeline.render import d3d11
from pipeline.stagepack.pack import load_json, write_json


def aligned(text):
    encoded = text.encode()
    return struct.pack('<I', len(encoded)) + encoded + bytes((-len(encoded)) % 4)


def stage_templates(code, output):
    """Insert the GH3 material templates used by the compiled stages, the GH3 characters, and the GH3 instruments into the
    stage surface shader."""
    stages = output.parent.parent.parent.parent/'stages'
    characters = output.parent.parent.parent.parent/'characters'
    instruments = output.parent.parent.parent.parent/'instruments'
    used = set()
    # Venue scenery and actors, the shared band and crowd library next to the output bundle, the GH3 characters and instruments.
    for path in [*stages.glob('*/**/stage.json'), *output.parent.parent.glob('band/*/stage.json'), *output.parent.parent.glob('crowd/**/stage.json'),
                 *characters.glob('*/stage.json'), *instruments.glob('*/stage.json')]:
        used.update(m['gh3']['template'] for m in load_json(path)['materials'].values() if 'gh3' in m and m['gh3']['template'] != '00000000')
    if not used:
        used = set(gh3_shader.library())
    vertex, fragment, table, failures = gh3_shader.uber(used)
    for checksum, reason in sorted(failures.items()):
        print(f'WARNING: GH3 template {checksum} is not translated and uses the fallback surface model: {reason}')
    for row in table.values():
        for note in row['approximations']: print(f"NOTE: GH3 template {row['name']}: {note}")
    if '// GH3_TEMPLATE_VERTEX' not in code or '// GH3_TEMPLATE_FRAGMENT' not in code:
        raise ValueError('Stage shader has no template insertion points')
    code = code.replace('// GH3_TEMPLATE_VERTEX', vertex).replace('// GH3_TEMPLATE_FRAGMENT', fragment)
    write_json(output.with_name('stage-templates.json'), {'version': 1, 'templates': table,
                                                         'untranslated': {k: v for k, v in sorted(failures.items())}})
    print(f'{len(table)} GH3 material templates in the stage shader, {len(failures)} untranslated')
    return code, table


def blit_shader(path):
    asset = UnityPy.load(str(path)).assets[0]
    if asset.unity_version != '2022.3.62f2':
        raise ValueError('Stage shader export requires Clone Hero Unity 2022.3.62f2')
    return asset, next(obj for obj in asset.objects.values() if obj.type.name == 'Shader' and obj.read_typetree()['m_ParsedForm']['m_Name'] == 'Hidden/BlitCopy')


def platform_blob(tree, platform):
    index = tree['platforms'].index(platform)
    offset = tree['offsets'][index][0]
    length = tree['compressedLengths'][index][0]
    return lz4.block.decompress(bytes(tree['compressedBlob'])[offset:offset+length], tree['decompressedLengths'][index][0])


def d3d11_templates(windows_game):
    """Return the Direct3D 11 vertex and pixel program headers and the pass data hash of the Windows Clone Hero blit shader."""
    _, shader = blit_shader(windows_game/'Clone Hero_Data/Resources/unity_builtin_extra')
    tree = shader.read_typetree()
    raw = platform_blob(tree, 4)
    entries = [raw[offset:offset+size] for offset, size, _ in (struct.unpack_from('<3I', raw, 4 + 12 * i) for i in range(struct.unpack_from('<I', raw)[0]))]
    if len(entries) != 4 or [struct.unpack_from('<2I', entry) for entry in entries] != [(202012090, 4), (202012090, 15), (202012090, 2), (202012090, 17)]:
        raise ValueError('The Windows blit shader has an unknown Direct3D 11 program layout')
    for entry, counts, channels in ((entries[1], (0, 3, 0), (17, 2, 0, 0, 4, 5)), (entries[3], (1, 1, 1), (0, 0))):
        length, = struct.unpack_from('<I', entry, 28)
        code = entry[32:32+length]
        if code[:4] != bytes((2, *counts)) or code[4:38] != bytes(34) or code[38:42] != b'DXBC' or struct.unpack_from(f'<{len(channels)}I', entry, (32+length+3)&~3) != channels:
            raise ValueError('The Windows blit shader has an unknown Direct3D 11 program format')
    shader_pass = tree['m_ParsedForm']['m_SubShaders'][0]['m_Passes'][0]
    return (entries[1][:32], entries[3][:32]), shader_pass['m_EditorDataHash'][shader_pass['m_Platforms'].index(4)]


def convert(game, output, kind='screen-color', windows_game=WINDOWS_GAME):
    (vertex_template, pixel_template), d3d11_hash = d3d11_templates(windows_game)
    asset, shader = blit_shader(game/'clonehero_Data/Resources/unity_builtin_extra')
    tree = shader.read_typetree()
    raw = platform_blob(tree, 15)
    count, = struct.unpack_from('<I', raw)
    if count != 2:
        raise ValueError('The installed blit shader has an unknown program layout')
    offset, size, segment = struct.unpack_from('<3I', raw, 4)
    parameters = raw[offset:offset+size]
    offset, size, segment = struct.unpack_from('<3I', raw, 16)
    program = raw[offset:offset+size]
    if len(parameters) != 224 or parameters[0x2c:0x38] != aligned('_Color') or parameters[0x50:0x60] != aligned('_MainTex_ST') or struct.unpack_from('<2I', parameters) != (202012090, 2):
        raise ValueError('The installed blit shader has an unknown parameter layout')
    if struct.unpack_from('<2I', program) != (202012090, 6) or segment != 0:
        raise ValueError('The installed blit shader has an unknown OpenGL program format')
    length, = struct.unpack_from('<I', program, 28)
    outline = kind == 'outline'
    particle = kind == 'particle'
    tesla = kind == 'tesla'
    surface = kind == 'stage'
    shader_name = 'Outline' if outline else 'Particle' if particle else 'Tesla' if tesla else 'Stage' if surface else 'ScreenColor'
    code = (ROOT/f'src/Stage/Shaders/{shader_name}.glsl').read_bytes()
    templates = []
    if surface:
        code, table = stage_templates(code.decode(), output)
        code = code.encode()
        templates = [f'_Gh3V{i}' for i in range(gh3_shader.VERTEX_CONSTANTS)] + [f'_Gh3P{i}' for i in range(gh3_shader.PIXEL_CONSTANTS)]
    program = program[:28] + struct.pack('<I', len(code)) + code + bytes((-len(code)) % 4) + program[(32+length+3)&~3:]
    if outline or particle or tesla or surface:
        # Vertex channel mask: position, normal, tangent, color, and texture coordinate bits.
        # Stage surfaces also read texture coordinates 4-7 (GH3 lightmap streams and the billboard pivot).
        program = program[:-8] + struct.pack('<2I', 127 if particle else 4095 if surface else 3, 0)
    names = ('_Gh3Outline', '_Gh3Color', '_Gh3ViewZ', '_Gh3Clock') if outline else (
        '_Gh3Particle', '_Gh3SoftEdge', '_Gh3CameraForward', '_Gh3Lighting', '_Gh3AmbientWeights', '_Gh3LightDirection', '_Gh3LightColor',
        '_Gh3SH0', '_Gh3SH1', '_Gh3SH2', '_Gh3SH3', '_Gh3Maps', '_Gh3Eye') if particle else (
        '_Gh3TeslaAnimation', '_Gh3TeslaShape', '_Gh3TeslaColor') if tesla else (
        '_Color', '_Gh3Surface', '_Gh3LightIndex', '_Gh3CamRight', '_Gh3CamUp',
        '_Gh3UvWibble', '_Gh3UvPivot', '_Gh3LayerColor', '_Gh3Layers', '_Gh3Template',
        '_Gh3Toon', '_Gh3ToonShade', '_Gh3ToonRim', '_Gh3ToonOutline', '_Gh3ToonOptions', *templates) if surface else (
        '_Gh3Red', '_Gh3Green', '_Gh3Blue')
    properties_names = names
    # Per-frame values shared by every stage surface; they stay out of the material properties so the global values apply.
    if surface: names = names + ('_Gh3Time', '_Gh3ViewRight', '_Gh3ViewUp', '_Gh3ViewForward', '_Gh3ViewEye')
    variables = b''.join(aligned(name) + parameters[0x38:0x4c] + struct.pack('<I', index * 16) for index, name in enumerate(names))
    if surface:
        parameters = parameters[:0x74] + struct.pack('<I', 16 * len(names)) + parameters[0x78:0xa0] + struct.pack('<I', 16 * len(names) + 16) + parameters[0xa4:]
    if particle or surface:
        extra = ('_CameraDepthTexture', '_Gh3NormalTex', '_Gh3SpecularTex') if particle else ('_Gh3Lightmap', '_Gh3LightData', '_Gh3NormalTex', '_Gh3SpecularTex', '_Gh3LayerTex', '_Gh3MaskTex', *gh3_shader.SAMPLER_NAMES[6:])
        textures = b''.join(aligned(name) + struct.pack('<4I', 0, index, index, 4) for index, name in enumerate(extra, 1))
        parameters = parameters[:0xa8] + struct.pack('<I', 2 + len(extra)) + parameters[0xac:0xc8] + textures + parameters[0xc8:]
    parameters = parameters[:0x24] + struct.pack('<2I', 16 * len(names), len(names) + 2) + variables + parameters[0x50:]
    raw = struct.pack('<I', 2) + struct.pack('<3I', 28, len(parameters), 0) + struct.pack('<3I', 28+len(parameters), len(program), 0) + parameters + program
    blob = lz4.block.compress(raw, store_size=False)
    # The same GLSL, through SPIR-V and HLSL, gives the Direct3D 11 programs (Windows default graphics API).
    direct3d = d3d11.compile_program(code.decode(), (vertex_template, pixel_template), shader_name)
    direct3d_blob = lz4.block.compress(direct3d, store_size=False)
    tree.update(platforms=[15, 4], offsets=[[0], [len(blob)]], compressedLengths=[[len(blob)], [len(direct3d_blob)]],
                decompressedLengths=[[len(raw)], [len(direct3d)]], compressedBlob=blob + direct3d_blob, stageCounts=[1, 2])
    form = tree['m_ParsedForm']
    form['m_Name'] = 'GH3/' + shader_name
    properties = form['m_PropInfo']['m_Props']
    color = properties.pop()
    if particle or surface:
        texture_property = next(prop for prop in properties if prop['m_Name'] == '_MainTex')
        for name in ('_Gh3NormalTex', '_Gh3SpecularTex') if particle else ('_Gh3Lightmap', '_Gh3LightData', '_Gh3NormalTex', '_Gh3SpecularTex', '_Gh3LayerTex', '_Gh3MaskTex', *gh3_shader.SAMPLER_NAMES[6:]):
            prop = deepcopy(texture_property)
            prop['m_Name'] = prop['m_Description'] = name
            properties.append(prop)
    for index, name in enumerate(properties_names):
        prop = deepcopy(color)
        prop['m_Name'] = prop['m_Description'] = name
        prop['m_Type'] = 1
        for channel in range(4):
            prop[f'm_DefValue[{channel}]'] = float(name == '_Color') if surface else 0.0 if outline or particle or tesla else float(index == channel)
        properties.append(prop)
    if outline or particle or tesla or surface:
        blend_properties = [('_SrcBlend', 5), ('_DstBlend', 10)]
        if particle or tesla: blend_properties += [('_SrcBlendAlpha', 5), ('_DstBlendAlpha', 10), ('_BlendOp', 0), ('_BlendOpAlpha', 0), ('_Cull', 0)]
        if surface: blend_properties = [('_SrcBlend', 1), ('_DstBlend', 0), ('_BlendOp', 0), ('_ZWrite', 1), ('_ZTest', 4), ('_Cull', 0),
                                        ('_StencilRef', 0), ('_StencilReadMask', 255), ('_StencilWriteMask', 255), ('_StencilComp', 8), ('_StencilPass', 0)]
        for name, value in blend_properties:
            prop = deepcopy(color)
            prop.update(m_Name=name, m_Description=name, m_Type=2)
            prop['m_DefValue[0]'] = float(value)
            properties.append(prop)
    for sub in form['m_SubShaders']:
        for shader_pass in sub['m_Passes']:
            # The mod checks this name: Unity's error shader, used when no program fits the graphics API, has unnamed passes.
            shader_pass['m_Name'] = shader_pass['m_State']['m_Name'] = 'GH3'
            shader_pass['m_Platforms'] = [15, 4]
            shader_pass['m_EditorDataHash'] = shader_pass['m_EditorDataHash'][:1] + [d3d11_hash]
            vertex, fragment = shader_pass['progVertex'], shader_pass['progFragment']
            fragment['m_PlayerSubPrograms'] = [[] for _ in vertex['m_PlayerSubPrograms']]
            fragment['m_ParameterBlobIndices'] = [[] for _ in vertex['m_PlayerSubPrograms']]
            for i, programs in enumerate(vertex['m_PlayerSubPrograms']):
                indices = [n for n, program in enumerate(programs) if program['m_GpuProgramType'] == 6]
                vertex['m_PlayerSubPrograms'][i] = [programs[n] for n in indices]
                vertex['m_ParameterBlobIndices'][i] = [vertex['m_ParameterBlobIndices'][i][n] for n in indices]
                # Direct3D 11 blob entries: 0 vertex parameters, 1 vertex program (type 15), 2 pixel parameters, 3 pixel program (type 17).
                for prog, program_type, blob_index in ((vertex, 15, 1), (fragment, 17, 3)):
                    for program in vertex['m_PlayerSubPrograms'][i][:1]:
                        prog['m_PlayerSubPrograms'][i].append(dict(deepcopy(program), m_BlobIndex=blob_index, m_GpuProgramType=program_type))
                        prog['m_ParameterBlobIndices'][i].append(blob_index - 1)
        if outline:
            original = sub['m_Passes'][0]
            sub['m_Passes'] = []
            for index in range(3):
                shader_pass = deepcopy(original)
                state = shader_pass['m_State']
                state['zTest']['val'] = 4
                state['rtBlend0']['colMask']['val'] = 15 if index == 1 else 0
                state['stencilRef']['val'] = 1 if index != 2 else 0
                state['stencilReadMask']['val'] = state['stencilWriteMask']['val'] = 1
                for stencil in ('stencilOp', 'stencilOpFront', 'stencilOpBack'):
                    state[stencil]['comp']['val'] = 6 if index == 1 else 8
                    state[stencil]['pass']['val'] = 0 if index == 1 else 2
                if index == 1:
                    state['rtBlend0']['srcBlend'].update(val=5, name='_SrcBlend')
                    state['rtBlend0']['destBlend'].update(val=10, name='_DstBlend')
                sub['m_Passes'].append(shader_pass)
        if surface:
            for shader_pass in sub['m_Passes']:
                state = shader_pass['m_State']
                state['zTest'].update(val=4, name='_ZTest')
                state['zWrite'].update(val=1, name='_ZWrite')
                state['culling'].update(val=0, name='_Cull')
                state['rtBlend0']['srcBlend'].update(val=1, name='_SrcBlend')
                state['rtBlend0']['destBlend'].update(val=0, name='_DstBlend')
                state['rtBlend0']['blendOp'].update(val=0, name='_BlendOp')
                # Character features drawn over the hair (StageAssets): the default (always, keep) leaves the stencil unchanged.
                state['stencilRef'].update(val=0, name='_StencilRef')
                state['stencilReadMask'].update(val=255, name='_StencilReadMask')
                state['stencilWriteMask'].update(val=255, name='_StencilWriteMask')
                for stencil in ('stencilOp', 'stencilOpFront', 'stencilOpBack'):
                    state[stencil]['comp'].update(val=8, name='_StencilComp')
                    state[stencil]['pass'].update(val=0, name='_StencilPass')
        if particle or tesla:
            for shader_pass in sub['m_Passes']:
                state = shader_pass['m_State']
                state['zTest']['val'] = 4
                state['culling'].update(val=0, name='_Cull')
                for field, name, value in (('srcBlend', '_SrcBlend', 5), ('destBlend', '_DstBlend', 10), ('srcBlendAlpha', '_SrcBlendAlpha', 5),
                                           ('destBlendAlpha', '_DstBlendAlpha', 10), ('blendOp', '_BlendOp', 0), ('blendOpAlpha', '_BlendOpAlpha', 0)):
                    state['rtBlend0'][field].update(val=value, name=name)
    shader.type_id = 0
    shader.path_id = 2
    shader.save_typetree(tree)
    bundle_type = copy(shader.serialized_type)
    bundle_type.class_id = 142
    bundle_type.old_type_hash = bytes(16)
    asset.types = [shader.serialized_type, bundle_type]
    asset._enable_type_tree = True
    for entry in asset.types:
        entry.type_dependencies = []
        entry.node = get_typetree_node(entry.class_id, asset.version)
        for node in entry.node.traverse():
            node.m_TypeFlags = node.m_TypeFlags or 0
            node.m_RefTypeHash = node.m_RefTypeHash or 0
    bundle = ObjectReader(asset, asset.reader, 1, 1, bundle_type, 142, ClassIDType.AssetBundle, 0, 0, None, None)
    reference = {'m_FileID': 0, 'm_PathID': 2}
    info = {'preloadIndex': 0, 'preloadSize': 1, 'asset': reference}
    bundle_name = 'gh3-' + kind
    bundle.save_typetree({'m_Name': bundle_name, 'm_PreloadTable': [reference], 'm_Container': [('gh3/' + kind, info)],
                         'm_MainAsset': info, 'm_RuntimeCompatibility': 1, 'm_AssetBundleName': bundle_name, 'm_Dependencies': [],
                         'm_IsStreamedSceneAssetBundle': False, 'm_ExplicitDataLayout': 0, 'm_PathFlags': 0, 'm_SceneHashes': []})
    asset.objects = {1: bundle, 2: shader}
    asset.externals = []
    asset.script_types = []
    data = asset.save()
    metadata = bytes(16) + struct.pack('>I2IH', 1, len(data), len(data), 0) + struct.pack('>IQQI', 1, 0, len(data), 4) + ('CAB-' + bundle_name).encode() + b'\0'
    header = b'UnityFS\0' + struct.pack('>I', 6) + b'5.x.x\0' + asset.unity_version.encode() + b'\0'
    header += struct.pack('>Q3I', len(header)+20+len(metadata)+len(data), len(metadata), len(metadata), 64)
    contents = header + metadata + data
    if len(UnityPy.load(contents).objects) != 2:
        raise ValueError('Stage shader bundle verification failed')
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_bytes(contents)
    library = output.parent.parent/'library.json'
    if library.is_file():
        manifest = load_json(library)
        manifest['files'][str(output.relative_to(library.parent))] = sha256(contents).hexdigest()
        if surface:
            table = output.with_name('stage-templates.json')
            manifest['files'][str(table.relative_to(library.parent))] = sha256(table.read_bytes()).hexdigest()
        write_json(library, manifest)
    print(f'{output}: {len(contents)} bytes')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(prog="python -m pipeline.render.shaders", description=__doc__)
    parser.add_argument('--game', type=Path, default=GAME)
    parser.add_argument('--windows-game', type=Path, default=WINDOWS_GAME, help='Windows Clone Hero folder (Direct3D 11 program template)')
    parser.add_argument('--output', type=Path, default=LIBRARY/'render/screen-color.bundle')
    parser.add_argument('--outline-output', type=Path, default=LIBRARY/'render/outline.bundle')
    parser.add_argument('--particle-output', type=Path, default=LIBRARY/'render/particle.bundle')
    parser.add_argument('--tesla-output', type=Path, default=LIBRARY/'render/tesla.bundle')
    parser.add_argument('--stage-output', type=Path, default=LIBRARY/'render/stage.bundle')
    args = parser.parse_args()
    convert(args.game, args.output, windows_game=args.windows_game)
    convert(args.game, args.outline_output, 'outline', windows_game=args.windows_game)
    convert(args.game, args.particle_output, 'particle', windows_game=args.windows_game)
    convert(args.game, args.tesla_output, 'tesla', windows_game=args.windows_game)
    convert(args.game, args.stage_output, 'stage', windows_game=args.windows_game)
