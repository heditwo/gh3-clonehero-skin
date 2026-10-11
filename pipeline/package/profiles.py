"""The rig profiles of the libraries: band roles, profile IDs, bone keys, and the profile rigs of library packages."""

import json
import re
import struct
import zlib
from pathlib import Path

import numpy as np

from .glb import Glb, decompose
from .package_files import Report, Source

PROFILE_ID = re.compile(r"[a-z0-9][a-z0-9-]{0,63}")
PROFILES = {
    "gh3-guitarist-axel-v1": "guitarist",
    "gh3-bassist-v1": "bassist",
    "gh3-singer-male-v1": "singer",
    "gh3-drummer-v1": "drummer",
}
LIBRARY_ID = "gh3-band-v1"
CHARACTER_LIBRARY_ID = "gh3-characters-v1"


def bone_key(name: str) -> str:
    """The rig key of a bone name of character.json: the checksum of 'bone-<checksum>', else the GH3 checksum of the name."""
    match = re.fullmatch(r"bone-([0-9a-f]{8})", name)
    return match.group(1) if match else f"{gh3_key(name):08x}"


def gh3_key(name: str) -> int:
    """GH3 checksum of a name (pipeline.gh3.formats.key, repeated here so the add-on bundle has no other dependency)."""
    return ~zlib.crc32(name.lower().encode()) & 0xFFFFFFFF


def name_key(name) -> int:
    """A GH3 name as the mod reads it (Gh3QbText.Key): a '0x' checksum or a name text."""
    return int(name[2:], 16) if isinstance(name, str) and name.lower().startswith("0x") else gh3_key(str(name))


def library_instruments(library) -> set[str]:
    """The instrument IDs of band/band.json in a library package (zip or folder) or the current library folder."""
    library = Path(library)
    try:
        if library.is_file() or (library / "manifest.json").is_file():
            source = Source(library, Report())
            band = json.loads(source.read("band/band.json")) if "band/band.json" in source.set else {}
        else:
            band = json.loads((library / "band" / "band.json").read_text())
    except (OSError, ValueError):
        return set()
    return {m.get("instrument") for m in band.get("members", []) if m.get("instrument")}


def load_profile(library, role):
    """Profile rig: joint checksums, parents, and glTF-space local bind transforms. `library` is a library package
    (zip or unpacked folder; the rig comes from band/<role>/model.glb) or the current library folder."""
    library = Path(library)
    if library.is_file() or (library / "manifest.json").is_file():
        report = Report()
        source = Source(library, report)
        if report.errors or f"band/{role}/model.glb" not in source.set:
            raise ValueError(f"the library has no band/{role}/model.glb")
        glb = Glb(source.read(f"band/{role}/model.glb"))
        nodes = glb.list("nodes")
        joints = glb.list("skins")[0]["joints"]
        parents = glb.parents()
        names = [nodes[j]["name"].removeprefix("bone-").lower() for j in joints]
        local = [decompose(glb.node_matrix(nodes[j])) for j in joints]
        return (
            names,
            [joints.index(parents[j]) if parents.get(j) in joints else -1 for j in joints],
            np.array([t for t, _, _ in local]),
            np.array([r for _, r, _ in local]),
        )
    bones = json.loads((library / "direction" / "performance.json").read_text())["members"][role]["bones"]
    data = (library / "band" / role / "animation.bin").read_bytes()
    if data[:8] != b"GH3ANM01":
        raise ValueError("expected GH3ANM01")
    (count,) = struct.unpack_from("<I", data, 8)
    rows = [struct.unpack_from("<i7f", data, 16 + i * 32) for i in range(count)]
    if len(rows) != len(bones):
        raise ValueError("bone names differ from the skeleton")
    # animation.bin is in Unity space: glTF space negates z of positions and x, y of rotations (see GlbWriter.actor).
    translations = np.array([r[1:4] for r in rows]) * [1, 1, -1]
    rotations = np.array([r[4:8] for r in rows]) * [-1, -1, 1, 1]
    return [b.lower() for b in bones], [r[0] for r in rows], translations, rotations


def load_character_library(path) -> dict:
    """characters.json of a character library package (zip or folder) or of the pipeline folder."""
    path = Path(path)
    if path.is_file() or (path / "manifest.json").is_file():
        report = Report()
        source = Source(path, report)
        if report.errors or "characters.json" not in source.set:
            raise ValueError("the package has no characters.json")
        return json.loads(source.read("characters.json"))
    return json.loads((path / "characters.json").read_text())


def character_profile(library, profile):
    """A character library profile as load_profile returns it: names, parents, glTF-space translations and rotations."""
    row = library["profiles"][profile]
    bind = np.array(row["bind"], dtype=np.float64)
    return [b.lower() for b in row["bones"]], row["parents"], bind[:, :3] * [1, 1, -1], bind[:, 3:] * [-1, -1, 1, 1]
