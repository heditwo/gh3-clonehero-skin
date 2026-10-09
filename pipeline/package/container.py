"""Package container helpers that the converter and the Blender add-on share: JSON bytes, hashes, and reproducible zips.

Relative imports only: the Blender add-on bundles this module (blender/build.py).
"""

import hashlib
import json
import os
import shutil
import zipfile
from pathlib import Path

from . import decisions

ZIP_TIME = (1980, 1, 1, 0, 0, 0)


def json_bytes(value):
    return (json.dumps(value, indent=2, ensure_ascii=False, allow_nan=False) + "\n").encode()


def sha256_file(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""): digest.update(block)
    return digest.hexdigest()


def file_list(folder, names):
    """The manifest 'files' object for package paths in a folder."""
    folder = Path(folder)
    return {name: {"sha256": sha256_file(folder/name), "size": (folder/name).stat().st_size} for name in sorted(names)}


def write_zip(folder, target):
    """Zip a package folder reproducibly: manifest.json first, then sorted paths, fixed time and attributes."""
    folder, target = Path(folder), Path(target)
    names = sorted(p.relative_to(folder).as_posix() for p in folder.rglob("*") if p.is_file())
    names = ["manifest.json", *(name for name in names if name != "manifest.json")]
    temporary = target.with_name("." + target.name + ".tmp")
    with zipfile.ZipFile(temporary, "w", allowZip64=True) as archive:
        for name in names:
            info = zipfile.ZipInfo(name, ZIP_TIME)
            info.create_system = 3
            info.external_attr = 0o100644 << 16
            info.compress_type = decisions.zip_method(name)
            # ZipFile.open does not apply the archive level to a given ZipInfo (3.13 keeps _compresslevel as an alias).
            info._compresslevel = decisions.DEFLATE_LEVEL
            info.file_size = (folder/name).stat().st_size
            with open(folder/name, "rb") as source, archive.open(info, "w") as destination:
                shutil.copyfileobj(source, destination, 1 << 20)
    os.replace(temporary, target)
