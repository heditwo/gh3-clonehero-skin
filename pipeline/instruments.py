"""Export every GH3 guitar and bass, and the highway of each GH3 guitarist, for the instrument and character packages.

The inventory comes from the GH3 QB tables: `musician_instrument` (guitar_band_profile_data.qb: names, finish, model
PAK, `Type` guitar or bass) and the store lists of store_data.qb (`Free_Guitars`, `Bonus_Guitars`,
`Bonus_Guitar_Finishes`, `Secret_Guitars`, and the bass lists, with prices). The drum kit and the microphones are not
exported.

An instrument PAK has one skin and one texture file. Every vertex follows `bone_guitar_body`, a bone of every GH3
guitarist and bassist skeleton, so one export plays on all of them. The meshes, materials (GH3 templates and constants),
and textures are converted as the library band does (pipeline.characters.load_models).

GH3 takes the highway from the character, not from the instrument: `create_guitarist` (guitar_character.qb) sets
`player_status.highway_material` to `sys_<profile name>_1_highway`, and the startup script of each character model PAK
loads `<name>_1_Highway.tex`. The instrument PAKs load only their skin and texture. This module writes the 512 x 1024
image of each guitarist profile; the packages step puts it into each character package of that profile.

Output: assets/instruments/<id>/ (meshes.bin GH3BND02, stage.json, textures/*.png, instrument.json),
assets/instruments/highways/<profile>.png, and assets/instruments/inventory.json.

Usage: python -m pipeline.instruments [--data DATA] [--output DIR] [--only ID ...]
"""

import argparse
import hashlib
import json
import re
import shutil
import struct
from pathlib import Path

import numpy as np

from pipeline.characters import SLUGS, load_models, pak_file, qb_items, text
from pipeline.gh3 import scene
from pipeline.gh3.data import Source, script
from pipeline.gh3.formats import find_array, key, values
from pipeline.paths import GH3_DATA, INSTRUMENTS
from pipeline.stagepack.gltf import SKINNED_GH3_VERTEX

STORE_LISTS = (
    "Free_Guitars",
    "Bonus_Guitars",
    "Bonus_Guitar_Finishes",
    "Secret_Guitars",
    "Free_Basses",
    "Bonus_Basses",
    "Bonus_Bass_Finishes",
    "Secret_Basses",
)
# The rig profile that the package GLB is bound to. The game binds an instrument to the rest pose of the performer that
# holds it, as GH3 does with the skeleton of the band member.
PROFILES = {"guitar": "gh3-guitarist-axel-v1", "bass": "gh3-bassist-v1"}
GUITAR_BODY = key("bone_guitar_body")
HIGHWAY_SIZE = (512, 1024)


def slug(desc_name):
    """gh3.<slug> for a GH3 instrument desc_name: 'Instrument_Guitar_Demonik' -> 'demonik', 'Instrument_ES335_Red' -> 'es335-red'."""
    name = re.sub(r"^Instrument_(Guitar_)?", "", desc_name, flags=re.IGNORECASE)
    return re.sub(r"[^a-z0-9]+", "-", name.lower()).strip("-")


def display_name(logo, name, series, finish):
    """'Gibson Les Paul Standard (Ebony)'; GH3's own guitars ('Special') read 'Obliviaxe: Lou's'."""
    name, series, finish, logo = (" ".join(v.split()) for v in (name, series, finish, logo))
    if logo == "Special":
        label = f"{name}: {series}" if series else name
        return label if finish in ("", "Special", series) else f"{label} ({finish})"
    label = " ".join(v for v in (logo, name, series) if v)
    return label if finish in ("", "Special") else f"{label} ({finish})"


def store_rows(scripts):
    """{instrument checksum: (store list, price)} from store_data.qb."""
    store = qb_items(script(scripts, "store_data"))
    found = {}
    for name in STORE_LISTS:
        for row in store.get(key(name)) or []:
            checksum = row if isinstance(row, int) else row[key("id")]
            price = 0 if isinstance(row, int) else int(row.get(key("price"), 0))
            found[checksum] = (name, price)
    return found


def inventory(data_directory):
    """Every GH3 guitar and bass: id, names, kind, PAK, and how GH3 offers it."""
    source = Source(data_directory)
    scripts = source.scripts()
    rows = qb_items(script(scripts, "guitar_band_profile_data"))[key("musician_instrument")]
    store = store_rows(scripts)
    instruments = []
    for row in rows:
        kind = {key("guitar"): "guitar", key("bass"): "bass"}.get(row.get(key("Type")))
        if kind is None:
            continue
        desc_name = text(row.get(key("desc_name")))
        fields = {f: " ".join(text(row.get(key(f))).split()) for f in ("logo", "name", "name2", "finish")}
        listed, price = store.get(row[key("desc_id")], ("", 0))
        instruments.append(
            {
                "id": f"gh3.{slug(desc_name)}",
                "descName": desc_name,
                "descId": f"{row[key('desc_id')]:08x}",
                "kind": kind,
                "name": display_name(fields["logo"], fields["name"], fields["name2"], fields["finish"]),
                "brand": fields["logo"],
                "model": fields["name"],
                "series": fields["name2"],
                "finish": fields["finish"],
                "description": "\n".join(
                    line.strip() for line in text(row.get(key("blurb"))).replace("\\n", "\n").strip().split("\n")
                ),
                "pak": pak_file(source.directory, text(row[key("pak")])).name,
                "profile": PROFILES[kind],
                "store": listed or "none",
                "price": price,
            }
        )
    ids = [i["id"] for i in instruments]
    if len(set(ids)) != len(ids):
        raise ValueError("two GH3 instruments have the same package ID")
    return source, scripts, instruments


def guitarist_profiles(scripts):
    """(profile name, first model PAK path) of each GH3 profile that GH3 offers (not `debug_only`). Only the guitarist
    model PAKs have a highway; highway() finds it."""
    profiles = find_array(script(scripts, "guitar_band_profile"), "Musician_Profiles")
    bodies = {r[key("desc_id")]: r for r in qb_items(script(scripts, "guitar_band_profile_data"))[key("musician_body")]}
    result = []
    for profile in profiles:
        if profile.get(key("debug_only")):
            continue
        name = text(profile[key("name")])
        desc = (
            key(f"Guitarist_{name}_Outfit1_Style1")
            if profile.get(key("outfits"))
            else profile[key("musician_body")][key("desc_id")]
        )
        if desc in bodies:
            result.append((name, text(bodies[desc][key("pak")])))
    return result


def pak_tex_entries(data):
    """(file-name checksum, bytes) of the .tex entries of a PAK. The second name field of the PAK header is the checksum
    of the file name without its folder and extension, for example key('axel_1_highway')."""
    offset = 0
    while offset + 32 <= len(data):
        kind, relative, size, _, _, file_name, _, flags = values(data, offset, "8I")
        if kind == key(".last"):
            return
        if kind == key(".tex"):
            yield file_name, data[offset + relative : offset + relative + size]
        offset += 192 if flags & 0x20 else 32


def highway(source, profile_name, model_pak):
    """The highway image of a guitarist profile: <name>_1_Highway.tex of its model PAK (one 512 x 1024 image), or None
    when the model PAK has no highway (the singers, the bassist, and the drummer)."""
    path = pak_file(source.directory, model_pak)
    data = (source.directory / "PAK" / path.name).read_bytes()
    found = [t for name, t in pak_tex_entries(data) if name == key(f"{profile_name}_1_highway")]
    if not found:
        return path.name, None
    # The file can hold a small extra texture (RockGod_1: a 32 x 32 image); the highway is the 512 x 1024 image.
    images = [image for blob in found for _, image in scene.textures(blob) if image.size == HIGHWAY_SIZE]
    if len(images) != 1:
        raise ValueError(f"{path.name}: no {profile_name}_1_Highway image of {HIGHWAY_SIZE}")
    return path.name, images[0].convert("RGBA")


def export(instrument, source, output):
    directory = output / instrument["id"]
    if directory.exists():
        shutil.rmtree(directory)
    (directory / "textures").mkdir(parents=True)
    materials, meshes = load_models(source, [instrument["pak"]], directory, "guitarist")
    for _, _, vertices, _ in meshes:
        rows = np.frombuffer(bytes(vertices), dtype=SKINNED_GH3_VERTEX)
        if not np.all((rows["joints"] == 119) | (rows["weights"] == 0)):
            raise ValueError(f"{instrument['pak']}: a vertex follows a bone other than bone_guitar_body")
    with (directory / "meshes.bin").open("wb") as stream:
        stream.write(b"GH3BND02" + struct.pack("<I", len(meshes)))
        for material, count, vertices, faces in meshes:
            stream.write(struct.pack("<III", int(material, 16), count, len(faces) * 3))
            stream.write(vertices)
            stream.write(np.asarray(faces, dtype="<u4").tobytes())
    (directory / "stage.json").write_text(
        json.dumps(
            {
                "version": 1,
                "name": instrument["id"],
                "cameraPosition": [0, 0, 0],
                "cameraTarget": [0, 1, 0],
                "materials": materials,
            },
            indent=2,
        )
        + "\n"
    )
    info = {
        **instrument,
        "vertices": sum(m[1] for m in meshes),
        "parts": len(meshes),
        "templates": sorted({m["gh3"]["template"] for m in materials.values() if "gh3" in m}),
    }
    (directory / "instrument.json").write_text(json.dumps(info, indent=2) + "\n")
    return info


def main(argv=None):
    parser = argparse.ArgumentParser(prog="python -m pipeline.instruments", description=__doc__.splitlines()[0])
    parser.add_argument("--data", type=Path, default=GH3_DATA)
    parser.add_argument("--output", type=Path, default=INSTRUMENTS)
    parser.add_argument("--only", action="append", help="Export only this instrument ID (repeatable)")
    args = parser.parse_args(argv)
    source, scripts, instruments = inventory(args.data)
    args.output.mkdir(parents=True, exist_ok=True)
    exported = []
    for instrument in instruments:
        if args.only and instrument["id"] not in args.only:
            continue
        info = export(instrument, source, args.output)
        exported.append(info["id"])
        print(f"{info['id']}: {info['name']} ({info['kind']}), {info['vertices']} vertices, {info['parts']} parts")
    highways = {}
    (args.output / "highways").mkdir(exist_ok=True)
    for name, pak in guitarist_profiles(scripts):
        pak_name, image = highway(source, name, pak)
        if image is None:
            continue
        target = args.output / "highways" / f"{SLUGS.get(name, name.lower())}.png"
        image.save(target)
        highways[name] = {
            "file": f"highways/{target.name}",
            "source": pak_name,
            "sha256": hashlib.sha256(image.tobytes()).hexdigest(),
        }
        print(f"highway of {name}: {pak_name}")
    if not args.only:
        wanted = {i["id"] for i in instruments} | {"highways"}
        for folder in args.output.iterdir():
            if folder.is_dir() and folder.name not in wanted:
                shutil.rmtree(folder)
    (args.output / "inventory.json").write_text(
        json.dumps({"version": 1, "instruments": instruments, "highways": highways, "sources": source.hashes}, indent=2)
        + "\n"
    )
    print(f"{len(exported)} instruments and {len(highways)} highways in {args.output}")


if __name__ == "__main__":
    main()
