"""Find or download the pinned shader tools that make the Direct3D 11 programs.

glslang and SPIRV-Cross are Khronos release binaries. FXC and d3dcompiler_47.dll come from Microsoft's Windows SDK NuGet
package and run under Wine. Every file is checked against a SHA-256 value, and the files go to a user cache folder.
"""

import hashlib
import io
import os
import shutil
import subprocess
import tarfile
import urllib.request
import zipfile
from pathlib import Path

CACHE = Path(os.environ.get('GH3_SHADER_TOOLS') or Path.home()/'.cache/gh3-skin/shader-tools')
GLSLANG = ('https://github.com/KhronosGroup/glslang/releases/download/16.6.0/glslang-16.6.0-linux-x86_64-release.tar.gz',
           '2c34071f56ecf39d16233294d3739cf43f981fd39080f2fd9dccb782101191ce', 'bin/glslang')
SPIRV_CROSS = ('https://github.com/KhronosGroup/SPIRV-Cross/releases/download/2021-01-15/spirv-cross-gcc-trusty-64bit-9acb9ec31f.tar.gz',
               '7621ce136adbc7b80916188dd0e69411d9dcd87094607cc7918a4b0adf53dc19', 'bin/spirv-cross')
WINDOWS_SDK = 'https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.cpp/10.0.26100.7705/microsoft.windows.sdk.cpp.10.0.26100.7705.nupkg'
WINDOWS_SDK_LICENSE = 'https://aka.ms/WinSDKLicenseURL'
WINDOWS_SDK_FILES = {'fxc.exe': ('c/bin/10.0.26100.0/x64/fxc.exe', '31fa10504b1551337de4d06ec2194143885a76a8b5a81177999e2faa34729183'),
                     'd3dcompiler_47.dll': ('c/Redist/D3D/x64/d3dcompiler_47.dll', 'a05f99734f7c4822fefc12b367af21fd0976ed6608752fb1e1e80b6ece7ecbbb')}


def _checked(path, sha256):
    return path.is_file() and hashlib.sha256(path.read_bytes()).hexdigest() == sha256


def _archive_tool(name, url, sha256, member):
    override = os.environ.get(f'GH3_{name.upper().replace("-", "_")}')
    if override: return Path(override)
    target = CACHE/name/member
    if target.is_file(): return target
    archive = CACHE/'downloads'/url.rsplit('/', 1)[1]
    if not _checked(archive, sha256):
        archive.parent.mkdir(parents=True, exist_ok=True)
        print(f'Downloading {url}')
        data = urllib.request.urlopen(url, timeout=120).read()
        if hashlib.sha256(data).hexdigest() != sha256: raise ValueError(f'{url} has an unexpected SHA-256 value')
        archive.write_bytes(data)
    with tarfile.open(archive) as tar:
        tar.extract(member, CACHE/name, filter='data')
    return target


class _RangeFile(io.RawIOBase):
    """Read a remote zip file with HTTP range requests, so only the needed members download (the package is 160 MB)."""

    def __init__(self, url):
        response = urllib.request.urlopen(urllib.request.Request(url, method='HEAD'), timeout=120)
        self.url, self.size, self.position = response.url, int(response.headers['Content-Length']), 0

    def readable(self): return True
    def seekable(self): return True
    def tell(self): return self.position

    def seek(self, offset, whence=0):
        self.position = offset if whence == 0 else self.position + offset if whence == 1 else self.size + offset
        return self.position

    def readinto(self, buffer):
        if self.position >= self.size: return 0
        end = min(self.size, self.position + len(buffer)) - 1
        data = urllib.request.urlopen(urllib.request.Request(self.url, headers={'Range': f'bytes={self.position}-{end}'}), timeout=120).read()
        buffer[:len(data)] = data
        self.position += len(data)
        return len(data)


def _windows_sdk():
    folder = CACHE/'windows-sdk'
    if all(_checked(folder/name, sha256) for name, (_, sha256) in WINDOWS_SDK_FILES.items()): return folder
    if os.environ.get('GH3_ACCEPT_WINDOWS_SDK_LICENSE') != '1':
        raise RuntimeError('The Direct3D 11 shader programs need FXC and d3dcompiler_47.dll from the Microsoft Windows SDK NuGet package. '
                           f'Read the Windows SDK license ({WINDOWS_SDK_LICENSE}) and set GH3_ACCEPT_WINDOWS_SDK_LICENSE=1 to download them.')
    print(f'Downloading FXC and d3dcompiler_47.dll from {WINDOWS_SDK}')
    package = zipfile.ZipFile(io.BufferedReader(_RangeFile(WINDOWS_SDK), 1 << 20))
    folder.mkdir(parents=True, exist_ok=True)
    for name, (member, sha256) in WINDOWS_SDK_FILES.items():
        data = package.read(member)
        if hashlib.sha256(data).hexdigest() != sha256: raise ValueError(f'{member} has an unexpected SHA-256 value')
        (folder/name).write_bytes(data)
    return folder


def glslang():
    return _archive_tool('glslang', *GLSLANG)


def spirv_cross():
    return _archive_tool('spirv-cross', *SPIRV_CROSS)


def wine():
    """Return the Wine command and its environment. GH3_WINE selects a Wine binary; the default is `wine` on PATH."""
    binary = os.environ.get('GH3_WINE') or shutil.which('wine')
    if not binary: raise RuntimeError('Wine is necessary to run FXC on Linux. Install Wine or set GH3_WINE to a Wine binary.')
    binary = Path(binary)
    environment = dict(os.environ, WINEPREFIX=str(CACHE/'wine-prefix'), WINEDEBUG='-all', WINEDLLOVERRIDES='d3dcompiler_47=n;mscoree,mshtml=', DISPLAY='')
    # A relocated Wine tree (for example an extracted package) does not find its own DLLs without WINEDLLPATH.
    libraries = binary.resolve().parent.parent/'lib/wine'
    if 'WINEDLLPATH' not in os.environ and libraries.is_dir(): environment['WINEDLLPATH'] = str(libraries)
    return binary, environment


def fxc(jobs, directory):
    """Compile (HLSL file, profile, DXBC file) jobs in `directory` with Microsoft FXC under Wine."""
    sdk = _windows_sdk()
    binary, environment = wine()
    for name in WINDOWS_SDK_FILES: shutil.copyfile(sdk/name, Path(directory)/name)
    try:
        for source, profile, output in jobs:
            result = subprocess.run([str(binary), 'fxc.exe', '/nologo', '/T', profile, '/E', 'main', '/O3', '/Fo', output, source],
                                    cwd=directory, env=environment, capture_output=True, text=True)
            if result.returncode != 0 or not (Path(directory)/output).is_file():
                raise RuntimeError(f'FXC failed for {source} ({profile}):\n{result.stdout}\n{result.stderr}'[-6000:])
    finally:
        server = binary.parent/'wineserver'
        if server.is_file(): subprocess.run([str(server), '-k'], env=environment, capture_output=True)
