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
ZIP_SYSTEM_UNIX = 3
REGULAR_FILE_644 = 0o100644
COPY_BUFFER = 1 << 20


def json_bytes(value) -> bytes:
    return (json.dumps(value, indent=2, ensure_ascii=False, allow_nan=False) + "\n").encode()


def sha256_file(path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for block in iter(lambda: stream.read(COPY_BUFFER), b""):
            digest.update(block)
    return digest.hexdigest()


def file_list(folder, names) -> dict[str, dict]:
    """The manifest 'files' object for package paths in a folder."""
    folder = Path(folder)
    return {
        name: {"sha256": sha256_file(folder / name), "size": (folder / name).stat().st_size} for name in sorted(names)
    }


def write_zip(folder, target) -> None:
    """Zip a package folder reproducibly: manifest.json first, then sorted paths, fixed time and attributes."""
    folder, target = Path(folder), Path(target)
    names = sorted(p.relative_to(folder).as_posix() for p in folder.rglob("*") if p.is_file())
    names = ["manifest.json", *(name for name in names if name != "manifest.json")]
    temporary = target.with_name("." + target.name + ".tmp")
    with zipfile.ZipFile(temporary, "w", allowZip64=True) as archive:
        for name in names:
            info = zipfile.ZipInfo(name, ZIP_TIME)
            info.create_system = ZIP_SYSTEM_UNIX
            info.external_attr = REGULAR_FILE_644 << 16
            info.compress_type = decisions.zip_method(name)
            # ZipFile.open does not apply the archive level to a given ZipInfo (3.13 keeps _compresslevel as an alias).
            info._compresslevel = decisions.DEFLATE_LEVEL  # type: ignore[attr-defined]
            info.file_size = (folder / name).stat().st_size
            with open(folder / name, "rb") as source, archive.open(info, "w") as destination:
                shutil.copyfileobj(source, destination, COPY_BUFFER)
    os.replace(temporary, target)
