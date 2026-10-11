"""The owner's GH3 PC DATA folder: PAK loading with source hashes, the QB script files, and QB scalars."""

import struct
from hashlib import sha256
from pathlib import Path

from pipeline.gh3.formats import QB_HEADER_SIZE, QB_SECTION_FLOAT, QB_SECTION_INTEGER, key, pak_entries, values


class Source:
    """Loads PAK files of a GH3 DATA folder and records the SHA-256 of each file it reads (the export sources)."""

    def __init__(self, directory):
        self.directory = Path(directory)
        self.hashes = {}

    def load(self, relative):
        """The entries of a PAK file and of its .pab data file when there is one."""
        path = self.directory / relative
        data = path.read_bytes()
        self.hashes[relative] = sha256(data).hexdigest()
        pab = path.with_name(path.name.replace(".pak.", ".pab."))
        if pab.exists():
            extra = pab.read_bytes()
            self.hashes[str(pab.relative_to(self.directory))] = sha256(extra).hexdigest()
            data += extra
        return list(pak_entries(data))

    def scripts(self):
        """{name checksum: QB file} of qb.pak."""
        return {n: data for kind, n, data in self.load("PAK/qb.pak.xen") if kind == key(".qb")}


def script(scripts, filename):
    """The QB file scripts/guitar/<filename>.qb."""
    return scripts[key(f"scripts\\guitar\\{filename}.qb")]


def scalar(data, name, default):
    """The top-level QB integer or float with this name, or the default."""
    for kind, fmt in [(QB_SECTION_INTEGER, "i"), (QB_SECTION_FLOAT, "f")]:
        marker = struct.pack(">II", kind, key(name))
        offset = data.find(marker, QB_HEADER_SIZE)
        if offset >= 0 and offset % 4 == 0:
            return values(data, offset + 12, fmt)[0]
    return default
