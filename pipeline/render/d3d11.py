"""Translate the stage GLSL programs to Unity Direct3D 11 programs: GLSL to SPIR-V to HLSL to DXBC, and the Unity metadata.

Unity does not read the DXBC reflection at run time. It binds constant buffers, textures, and samplers from the parameter blob
next to each program. This module writes that blob from the reflection (RDEF) of the compiled DXBC, so the slots always agree.
"""

import re
import struct
import subprocess
import tempfile
from pathlib import Path

from pipeline.render import toolchain

VERSION = 202012090
# Unity built-in constant buffers (UnityShaderVariables.cginc in 2022.3): size and member offsets. Unity shares these buffers
# between all shaders, so the stage shaders use the same names, sizes, and offsets.
BUILTIN_BUFFERS = {
    'UnityPerCamera': (144, {'_Time': 0, '_ProjectionParams': 80, '_ScreenParams': 96, '_ZBufferParams': 112, 'unity_OrthoParams': 128}),
    'UnityPerDraw': (176, {'unity_ObjectToWorld': 0, 'unity_WorldToObject': 64}),
    'UnityPerFrame': (368, {'glstate_matrix_projection': 80, 'unity_MatrixV': 144, 'unity_MatrixInvV': 208, 'unity_MatrixVP': 272}),
}
MATRIX = 'hlslcc_mtx4x4'
# Vertex input semantic: (Unity shader channel, Unity vertex component). Measured in the bind channels of the Windows
# Clone Hero shaders: vertex 0/0, normal 1/1, tangent 2/2, color 3/3, texture coordinate n (4 + n)/(5 + n).
CHANNELS = {'POSITION': (0, 0), 'NORMAL': (1, 1), 'TANGENT': (2, 2), 'COLOR': (3, 3), **{f'TEXCOORD{i}': (4 + i, 5 + i) for i in range(8)}}
UNIFORM = re.compile(r'(?:layout\(location = \d+\) )?uniform (\w+) (\w+)(?:\[(\d+)\])?;')
ATTRIBUTE = re.compile(r'in (\w+) in_(POSITION|NORMAL|TANGENT|COLOR|TEXCOORD)(\d);')


def aligned(text):
    encoded = text.encode()
    return struct.pack('<I', len(encoded)) + encoded + bytes((-len(encoded)) % 4)


def split(code):
    """Return the vertex and fragment GLSL of a Unity GLSL program (one text with VERTEX and FRAGMENT sections)."""
    vertex, fragment = code.split('#ifdef FRAGMENT\n', 1)
    vertex = vertex.split('#ifdef VERTEX\n', 1)[1].rsplit('#endif', 1)[0]
    return vertex, fragment.rsplit('#endif', 1)[0]


def prepare(source, vertex):
    """Make GLSL 4.30 for glslang: built-in uniforms in Unity's constant buffers, explicit bindings, and vertex input semantics."""
    lines, buffers, samplers, semantics = [], {}, 0, []
    for line in source.splitlines():
        if match := UNIFORM.fullmatch(line):
            kind, name, count = match.group(1), match.group(2), int(match.group(3) or 1)
            if kind.startswith('sampler'):
                lines.append(f'layout(binding = {samplers}) uniform {kind} {name};')
                samplers += 1
                continue
            unity = name.removeprefix(MATRIX)
            buffer = next((buffer for buffer, (_, members) in BUILTIN_BUFFERS.items() if unity in members), None)
            if buffer:
                if kind != 'vec4': raise ValueError(f'Built-in uniform {name} must be vec4')
                buffers.setdefault(buffer, {})[BUILTIN_BUFFERS[buffer][1][unity]] = (name, count)
                continue
            lines.append(f'uniform {kind} {name}{f"[{count}]" if match.group(3) else ""};')
        elif vertex and (match := ATTRIBUTE.fullmatch(line)):
            semantic = match.group(2) + (match.group(3) if match.group(2) == 'TEXCOORD' else '')
            lines.append(f'layout(location = {len(semantics)}) {line}')
            semantics.append(semantic)
        elif line.startswith('#version'):
            lines += ['#version 430', '#define GH3_D3D11 1']
        else:
            lines.append(line)
    # $Globals takes b0 when the stage has loose uniforms; the Unity buffers follow in a fixed order.
    loose = any(UNIFORM.fullmatch(line) and 'sampler' not in line for line in lines)
    declarations = []
    for binding, buffer in enumerate(sorted(buffers), int(loose)):
        body, position = [], 0
        for offset, (name, count) in sorted(buffers[buffer].items()):
            if offset > position: body.append(f'vec4 gh3_{buffer}{position}[{(offset - position) // 16}];')
            body.append(f'vec4 {name}[{count}];' if count > 1 else f'vec4 {name};')
            position = offset + 16 * count
        declarations.append(f'layout(std140, binding = {binding}) uniform {buffer} {{ {" ".join(body)} }};')
    header = next(index for index, line in enumerate(lines) if line and not line.startswith('#'))
    lines[header:header] = declarations
    return '\n'.join(lines) + '\n', semantics, buffers


def chunks(dxbc):
    if dxbc[:4] != b'DXBC': raise ValueError('Not a DXBC program')
    count, = struct.unpack_from('<I', dxbc, 28)
    return {dxbc[o:o + 4].decode(): dxbc[o + 8:o + 8 + struct.unpack_from('<I', dxbc, o + 4)[0]] for o in struct.unpack_from(f'<{count}I', dxbc, 32)}


def cstring(data, offset):
    return data[offset:data.index(b'\0', offset)].decode()


def signature(chunk):
    count, = struct.unpack_from('<I', chunk)
    elements = []
    for index in range(count):
        name, semantic_index, system_value, component, register = struct.unpack_from('<5I', chunk, 8 + 24 * index)
        elements.append((cstring(chunk, name), semantic_index, system_value, register))
    return elements


def reflect(dxbc):
    """Return the constant buffers ({name: (size, [(variable, offset, class, type, rows, columns, elements)])}) and bindings."""
    rdef = chunks(dxbc)['RDEF']
    buffer_count, buffer_offset, binding_count, binding_offset = struct.unpack_from('<4I', rdef)
    variable_size = 40 if rdef[17] >= 5 else 24
    bindings = []
    for index in range(binding_count):
        name, kind, _, dimension, _, point, count, _ = struct.unpack_from('<8I', rdef, binding_offset + 32 * index)
        bindings.append((cstring(rdef, name), kind, point, dimension, count))
    buffers = {}
    for index in range(buffer_count):
        name, count, offset, size, _, kind = struct.unpack_from('<6I', rdef, buffer_offset + 24 * index)
        variables = []
        for number in range(count):
            variable, start, _, _, type_offset = struct.unpack_from('<5I', rdef, offset + variable_size * number)
            variables.append((cstring(rdef, variable), start, *struct.unpack_from('<5H', rdef, type_offset)))
        if kind == 0: buffers[cstring(rdef, name)] = (size, variables)
    return buffers, bindings


def parameters(dxbc, builtins):
    """Write the Unity parameter blob of one DXBC program."""
    buffers, bindings = reflect(dxbc)
    groups, entries = [], []
    textures = {name: (point, dimension) for name, kind, point, dimension, _ in bindings if kind == 2}
    samplers = {name: point for name, kind, point, _, _ in bindings if kind == 3}
    for name, (point, dimension) in sorted(textures.items(), key=lambda item: item[1]):
        if dimension != 4: raise ValueError(f'{name}: only 2D textures are supported')
        # SPIRV-Cross names the sampler of a combined GLSL sampler `_<texture>_sampler`.
        sampler = samplers.get(f'_{name}_sampler')
        if sampler is None: raise ValueError(f'{name} has no sampler')
        entries.append(aligned(name) + struct.pack('<4I', 0, point, sampler, 2 << 1))
    for name, kind, point, _, _ in sorted(bindings, key=lambda binding: (binding[1], binding[2])):
        if kind != 0: continue
        size, variables = buffers[name]
        rows = []
        if name in BUILTIN_BUFFERS:
            size = BUILTIN_BUFFERS[name][0]
            for offset, (glsl, _) in sorted(builtins[name].items()):
                rows.append((glsl.removeprefix(MATRIX), 0, 4, 4, 1, 0, offset) if glsl.startswith(MATRIX) else (glsl, 0, 1, 4, 0, 0, offset))
        else:
            for variable, offset, _, base, height, width, elements in variables:
                if base not in (2, 3, 19): raise ValueError(f'{variable}: unsupported constant type {base}')
                unity_type = {3: 0, 2: 1, 19: 5}[base]
                if variable.startswith(MATRIX):
                    rows.append((variable.removeprefix(MATRIX), unity_type, 4, 4, 1, 0, offset))
                else:
                    rows.append((variable, unity_type, height, width, 0, elements, offset))
        group = aligned(name) + struct.pack('<2I', size, len(rows))
        group += b''.join(aligned(row[0]) + struct.pack('<6I', *row[1:]) for row in rows)
        groups.append(group + struct.pack('<I', 0))
        entries.append(aligned(name) + struct.pack('<3I', 1, point, 0))
    # The Unity program header stores slot counts, so the slots of each kind must be 0 to n - 1.
    for kind in (0, 2, 3):
        if sorted(point for _, k, point, _, _ in bindings if k == kind) != list(range(sum(k == kind for _, k, _, _, _ in bindings))):
            raise ValueError(f'Resource slots of kind {kind} are not contiguous')
    blob = struct.pack('<2I', VERSION, len(groups) + 1) + aligned('') + struct.pack('<3I', 0, 0, 0) + b''.join(groups)
    counts = (len(textures), len(groups), len(samplers))
    return blob + struct.pack('<I', len(entries)) + b''.join(entries), counts


def program(template, dxbc, counts, vertex):
    """Write a Unity Direct3D 11 program: the header of the Windows template, the slot counts, DXBC, and the bind channels."""
    code = bytes((2, *counts)) + bytes(34) + dxbc
    blob = template[:8] + bytes(20) + struct.pack('<I', len(code)) + code + bytes((-len(code)) % 4)
    channels = []
    if vertex:
        for name, index, system_value, register in signature(chunks(dxbc)['ISGN']):
            if system_value: continue
            semantic = name if name != 'TEXCOORD' else f'TEXCOORD{index}'
            if index and name != 'TEXCOORD' or semantic not in CHANNELS: raise ValueError(f'Unsupported vertex input {name}{index}')
            channels.append(CHANNELS[semantic])
    mask = sum(1 << channel for channel, _ in channels)
    return blob + struct.pack('<2I', mask, len(channels)) + b''.join(struct.pack('<2I', *channel) for channel in channels)


def match_inputs(vertex_hlsl, fragment_hlsl):
    """Give the pixel shader the full vertex output signature, so that the Direct3D 11 runtime links the two stages."""
    outputs = re.search(r'struct SPIRV_Cross_Output\n\{\n(.*?)\n\};', vertex_hlsl, re.S).group(1).splitlines()
    inputs = re.search(r'struct SPIRV_Cross_Input\n\{\n(.*?)\n\};', fragment_hlsl, re.S)
    used = {line.rsplit(':', 1)[1].strip(' ;'): line for line in inputs.group(1).splitlines()} if inputs else {}
    members = [used.get(line.rsplit(':', 1)[1].strip(' ;'), line) for line in outputs]
    missing = set(used) - {line.rsplit(':', 1)[1].strip(' ;') for line in outputs}
    if missing: raise ValueError(f'Pixel inputs without vertex outputs: {sorted(missing)}')
    block = 'struct SPIRV_Cross_Input\n{\n' + '\n'.join(members) + '\n};'
    if inputs: return fragment_hlsl[:inputs.start()] + block + fragment_hlsl[inputs.end():]
    fragment_hlsl = fragment_hlsl.replace('SPIRV_Cross_Output main()', 'SPIRV_Cross_Output main(SPIRV_Cross_Input stage_input)')
    return fragment_hlsl.replace('struct SPIRV_Cross_Output', block + '\n\nstruct SPIRV_Cross_Output', 1)


def compile_program(code, templates, name):
    """Return the Direct3D 11 platform blob (vertex parameters, vertex program, pixel parameters, pixel program)."""
    vertex_template, pixel_template = templates
    glslang, spirv_cross = toolchain.glslang(), toolchain.spirv_cross()
    with tempfile.TemporaryDirectory(prefix='gh3-d3d11-') as directory:
        folder = Path(directory)
        hlsl, builtins = {}, {}
        for stage, source in zip(('vert', 'frag'), split(code)):
            glsl, semantics, builtins[stage] = prepare(source, stage == 'vert')
            (folder/f'{name}.{stage}').write_text(glsl)
            run([glslang, '-G', '--aml', '-o', f'{name}.{stage}.spv', f'{name}.{stage}'], folder)
            options = [argument for location, semantic in enumerate(semantics) for argument in ('--set-hlsl-vertex-input-semantic', str(location), semantic)]
            run([spirv_cross, f'{name}.{stage}.spv', '--hlsl', '--shader-model', '40', *options, '--output', f'{name}.{stage}.hlsl'], folder)
            hlsl[stage] = (folder/f'{name}.{stage}.hlsl').read_text()
        (folder/f'{name}.frag.hlsl').write_text(match_inputs(hlsl['vert'], hlsl['frag']))
        toolchain.fxc([(f'{name}.vert.hlsl', 'vs_4_0', f'{name}.vert.dxbc'), (f'{name}.frag.hlsl', 'ps_4_0', f'{name}.frag.dxbc')], folder)
        vertex, pixel = (folder/f'{name}.vert.dxbc').read_bytes(), (folder/f'{name}.frag.dxbc').read_bytes()
    outputs = [(n, i, r) for n, i, s, r in signature(chunks(vertex)['OSGN'])]
    inputs = [(n, i, r) for n, i, s, r in signature(chunks(pixel)['ISGN'])]
    if outputs != inputs: raise ValueError(f'{name}: the pixel input signature does not match the vertex output signature')
    vertex_parameters, vertex_counts = parameters(vertex, builtins['vert'])
    pixel_parameters, pixel_counts = parameters(pixel, builtins['frag'])
    entries = [vertex_parameters, program(vertex_template, vertex, vertex_counts, True), pixel_parameters, program(pixel_template, pixel, pixel_counts, False)]
    offset, table = 4 + 12 * len(entries), b''
    for entry in entries:
        table += struct.pack('<3I', offset, len(entry), 0)
        offset += len(entry)
    return struct.pack('<I', len(entries)) + table + b''.join(entries)


def run(command, directory):
    result = subprocess.run([str(part) for part in command], cwd=directory, capture_output=True, text=True)
    if result.returncode != 0: raise RuntimeError(f'{Path(command[0]).name} failed:\n{result.stdout}\n{result.stderr}'[-6000:])
