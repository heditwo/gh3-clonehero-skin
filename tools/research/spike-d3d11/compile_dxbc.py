import ctypes, re, sys, time
from ctypes import wintypes

d3d = ctypes.WinDLL(sys.argv[1] if len(sys.argv) > 1 and sys.argv[1].endswith('.dll') else 'd3dcompiler_47.dll')

class Blob(ctypes.Structure):
    pass

def blob_bytes(ptr):
    vtbl = ctypes.cast(ptr, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
    get_pointer = ctypes.WINFUNCTYPE(ctypes.c_void_p, ctypes.c_void_p)(vtbl[3])
    get_size = ctypes.WINFUNCTYPE(ctypes.c_size_t, ctypes.c_void_p)(vtbl[4])
    return ctypes.string_at(get_pointer(ptr), get_size(ptr))

D3DCompile = d3d.D3DCompile
D3DCompile.argtypes = [ctypes.c_char_p, ctypes.c_size_t, ctypes.c_char_p, ctypes.c_void_p, ctypes.c_void_p,
                       ctypes.c_char_p, ctypes.c_char_p, ctypes.c_uint, ctypes.c_uint, ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(ctypes.c_void_p)]
D3DCompile.restype = ctypes.c_long

jobs = [a for a in sys.argv[1:] if not a.endswith('.dll')]
for job in jobs:
    source_path, profile, output = job.split(',')
    source = open(source_path, 'rb').read()
    # SPIRV-Cross binding guesses collide between stages; let the compiler assign registers.
    source = re.sub(rb':\s*register\([a-z]\d+\)', b'', source)
    code, errors = ctypes.c_void_p(), ctypes.c_void_p()
    start = time.time()
    hr = D3DCompile(source, len(source), source_path.encode(), None, None, b'main', profile.encode(), 0, 0, ctypes.byref(code), ctypes.byref(errors))
    elapsed = time.time() - start
    message = blob_bytes(errors).decode(errors='replace') if errors.value else ''
    if hr == 0 and code.value:
        data = blob_bytes(code)
        open(output, 'wb').write(data)
        print(f'{source_path} {profile}: OK {len(data)} bytes in {elapsed:.1f}s')
    else:
        print(f'{source_path} {profile}: FAILED hr=0x{hr & 0xffffffff:08x} in {elapsed:.1f}s')
    if message:
        print(message[:3000])
