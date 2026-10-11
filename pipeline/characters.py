"""Export the playable GH3 characters (every outfit and style) as runtime band models on their GH3 rig profiles.

The inventory comes from the GH3 QB tables: `Musician_Profiles` (guitar_band_profile.qb) gives the characters, their
outfits, styles, skeletons, and default instruments; `musician_body` and `musician_instrument`
(guitar_band_profile_data.qb) give the model and instrument PAKs; `Secret_Characters`, `Bonus_Outfits`, and
`Bonus_Styles` (store_data.qb) tell how GH3 unlocks them. Profiles with `debug_only` (the BoneMan test models) are not
exported.

Each variant keeps the GH3 meshes, materials (GH3 templates and constants), textures, and hair cards as the library band
does (pipeline/library/band.py), on its own GH3 skeleton: the rig profile of its GH3 profile and outfit in the character
library gh3-characters-v1 (pipeline.character_library). Axel's first outfit uses the library profile
gh3-guitarist-axel-v1. The two singers use gh3-singer-female-v1 and gh3-singer-bret-v1: the skeleton, animation set,
and ragdoll of their GH3 vocalist profile.

Output: assets/characters/<id>/ (meshes.bin GH3BND02, stage.json, textures/*.png, character.json) and
assets/characters/inventory.json. The packages step (pipeline.package.build) writes them as .chchar packages.

Usage: python -m pipeline.characters [--data DATA] [--output DIR] [--only ID ...]
"""

import argparse
import json
import re
import shutil
import struct
from pathlib import Path

from pipeline.gh3.data import Source, script
from pipeline.gh3.formats import find_array, key
from pipeline.gh3.hair_mesh import combine_hair
from pipeline.gh3.mesh_cutout import trim_prop_cutouts
from pipeline.library.models import read_models
from pipeline.package import runtime
from pipeline.paths import CHARACTERS, GH3_DATA

CLIP_RIG_PROFILE = "gh3-guitarist-axel-v1"
# The GH3 vocalist profiles that have their own model, with their rig profile in the character library and their name.
# Singer_Alt and the *_Alt profiles only change the microphone.
SINGERS = {
    "Singer_Female": ("gh3-singer-female-v1", "Female singer"),
    "Singer_Bret": ("gh3-singer-bret-v1", "Bret Michaels"),
}
RIG_NAMES = [
    f"GH3_Guitarist_{n}{s}"
    for n in (
        "Axel",
        "Casey",
        "Izzy",
        "Johnny",
        "Judy",
        "Lars",
        "Midori",
        "Xavier",
        "Morello",
        "Ripper",
        "RockGod",
        "Satan",
        "Slash",
    )
    for s in ("", "_2")
] + ["GH3_Singer_Male", "GH3_Singer_Female", "GH3_Singer_BretMichaels"]
SLUGS = {
    "Satan": "lou",
    "RockGod": "god-of-rock",
    "Ripper": "grim-ripper",
    "Singer_Female": "singer-female",
    "Singer_Bret": "singer-bret",
}


def qb_items(data):
    """The top-level arrays of a QB file: {checksum: array}."""
    file_key = struct.unpack_from(">I", data, 36)[0]
    found = {}
    for offset in range(28, len(data) - 20, 4):
        kind, name, owner = struct.unpack_from(">3I", data, offset)
        if owner == file_key and kind == 0x00200C00 and name not in found:
            found[name] = find_array(data, name)
    return found


def text(value):
    return value if isinstance(value, str) else ""


def slug(name):
    return SLUGS.get(name, re.sub(r"[^a-z0-9]+", "-", name.lower()).strip("-"))


def profile_id(name, second):
    """The rig profile ID of a GH3 guitarist profile and outfit (second: the outfit 2 skeleton or ragdoll differs)."""
    if name == "Axel" and not second:
        return CLIP_RIG_PROFILE
    return f"gh3-guitarist-{slug(name)}{'-outfit2' if second else ''}-v1"


def outfits(profile):
    """(outfit number, skeleton, ragdoll, second) for each outfit of a guitarist profile. create_band_member
    (guitar_anim_tree.qb) uses `skeleton2` and `ragdoll2` for outfit 2."""
    rows = profile.get(key("outfits")) or [None]
    first = (profile[key("Skeleton")], profile[key("ragdoll")])
    result = []
    for outfit in range(1, len(rows) + 1):
        use_second = outfit == 2 and profile.get(key("skeleton2")) not in (None, key("None"))
        rig, ragdoll = (profile[key("skeleton2")], profile[key("ragdoll2")]) if use_second else first
        result.append((outfit, rig, ragdoll, use_second and (rig, ragdoll) != first))
    return result


def pak_file(directory, pak_path):
    """The PAK file of a QB `pak` path, for example 'pak\\models\\guitarists\\Judy_1\\Judy_1.pak' -> PAK/Judy_1.pak.xen."""
    name = pak_path.replace("\\", "/").rsplit("/", 1)[-1].lower().removesuffix(".pak") + ".pak.xen"
    found = [p for p in (directory / "PAK").iterdir() if p.name.lower() == name]
    if len(found) != 1:
        raise ValueError(f"no PAK for {pak_path}")
    return found[0]


def inventory(data_directory):
    """Every exported variant: id, names, role, profile, model and instrument PAKs, skeleton, and how GH3 offers it."""
    source = Source(data_directory)
    scripts = source.scripts()
    profiles = find_array(script(scripts, "guitar_band_profile"), "Musician_Profiles")
    tables = qb_items(script(scripts, "guitar_band_profile_data"))
    # QB ids (desc_id, Skeleton, store ids) are checksums.
    bodies = {row[key("desc_id")]: row for row in tables[key("musician_body")]}
    instruments = {row[key("desc_id")]: row for row in tables[key("musician_instrument")]}
    rig_names = {key(name): name for name in RIG_NAMES}
    store = qb_items(script(scripts, "store_data"))
    secret = {row[key("id")] for row in store[key("Secret_Characters")]}
    bonus_outfits = {row[key("id")] for row in store[key("Bonus_Outfits")]}
    bonus_styles = {row[key("id")] for row in store[key("Bonus_Styles")]}
    variants, skipped = [], []

    def add(profile, role, outfit, style, desc, rig, outfit_name, style_name, instrument, rig_profile):
        body = bodies[desc]
        unlock = (
            "store (secret character)"
            if desc in secret
            else "store (bonus outfit)"
            if desc in bonus_outfits
            else "store (bonus style)"
            if desc in bonus_styles
            else "start"
        )
        name = text(profile.get(key("fullname"))) or text(profile[key("name")])
        styled = bool(style_name.strip())
        label = (
            name
            if not outfit_name or outfit_name == name
            else f"{name}: {outfit_name}" + (f" ({style_name})" if styled else "")
        )
        if role == "singer":
            label = SINGERS[text(profile[key("name")])][1]
        identifier = (
            f"gh3.{slug(text(profile[key('name')]))}"
            + (f".outfit{outfit}" if outfit_name else "")
            + (f".style{style}" if styled else "")
        )
        if role == "singer":
            unlock = "song (GH3 selects the singer of each song)"
        row = instruments.get(instrument)
        variants.append(
            {
                "id": identifier,
                "name": label,
                "character": name,
                "profileName": text(profile[key("name")]),
                "role": role,
                "profile": rig_profile,
                "outfit": outfit,
                "style": style,
                "outfitName": outfit_name,
                "styleName": style_name.strip(),
                "descId": f"{desc:08x}",
                "model": pak_file(source.directory, text(body[key("pak")])).name,
                "mesh": text(body[key("mesh")]),
                "skeleton": rig_names[rig],
                "instrument": (text(row.get(key("desc_name"))) or text(row.get(key("name")))) if row else "",
                "instrumentModel": pak_file(source.directory, text(row[key("pak")])).name if row else "",
                "instrumentName": " ".join(
                    text(row.get(key(f))).strip()
                    for f in ("logo", "name", "name2", "finish")
                    if text(row.get(key(f))).strip()
                )
                if row
                else "",
                "unlock": unlock,
                "animSet": names.get(profile.get(key("anim_set")), ""),
                "strumAnims": names.get(profile.get(key("strum_anims")), ""),
                "fingerAnims": names.get(profile.get(key("finger_anims")), ""),
                "scale": list(profile.get(key("scale"), [1, 1, 1])),
            }
        )

    # Names of the animation-set checksums of the profiles (for the inventory report only).
    names = {
        key(f"{text(p[key('name')])}{suffix}"): f"{text(p[key('name')])}{suffix}"
        for p in profiles
        for suffix in ("_animations",)
    }
    names.update({key(f"Normal{text(p[key('name')])}"): f"Normal{text(p[key('name')])}" for p in profiles})
    names.update(
        {
            key(f"guitarist_finger_anims_{text(p[key('name')])}"): f"guitarist_finger_anims_{text(p[key('name')])}"
            for p in profiles
        }
    )
    names.update(
        {
            key(n): n
            for n in (
                "singer_male_animations",
                "singer_female_animations",
                "bassist_animations",
                "drummer_animations",
                "normal",
                "Bass_Slap",
            )
        }
    )
    for profile in profiles:
        name = text(profile[key("name")])
        kind = profile.get(key("Type"))
        if profile.get(key("debug_only")):
            skipped.append({"profile": name, "reason": "debug_only (GH3 does not offer it)"})
            continue
        instrument = (profile.get(key("musician_instrument")) or {}).get(key("desc_id"))
        if kind == key("Guitarist"):
            rows = profile.get(key("outfits")) or []
            if not rows:
                add(
                    profile,
                    "guitarist",
                    1,
                    1,
                    profile[key("musician_body")][key("desc_id")],
                    profile[key("Skeleton")],
                    "",
                    "",
                    instrument,
                    profile_id(name, False),
                )
                continue
            for (outfit, rig, _, second), row in zip(outfits(profile), rows):
                styles = row.get(key("styles")) or [" "]
                for style, style_name in enumerate(styles, 1):
                    desc = key(f"Guitarist_{name}_Outfit{outfit}_Style{style}")
                    add(
                        profile,
                        "guitarist",
                        outfit,
                        style,
                        desc,
                        rig,
                        text(row[key("name")]),
                        text(style_name),
                        instrument,
                        profile_id(name, second),
                    )
        elif kind == key("vocalist") and name in SINGERS:
            add(
                profile,
                "singer",
                1,
                1,
                profile[key("musician_body")][key("desc_id")],
                profile[key("Skeleton")],
                "",
                "",
                instrument,
                SINGERS[name][0],
            )
        else:
            skipped.append(
                {
                    "profile": name,
                    "reason": "the library band member of this role"
                    if kind != key("vocalist")
                    else "the same model as another vocalist profile (only the microphone differs)",
                }
            )
    return variants, skipped


def load_models(source, files, directory, role):
    """GH3 meshes, materials, and textures of the model and instrument PAKs, as pipeline/library/band.py converts them."""
    paks = [f"PAK/{path}" for path in files if path]
    materials, meshes = read_models(source, paks, directory / "textures", "character material ID collision")
    if role == "singer":
        meshes = trim_prop_cutouts(meshes, materials, directory / "textures")
    meshes = combine_hair(meshes, materials, directory / "textures")
    used = {t for m in materials.values() for t in [m.get("texture", ""), *m.get("gh3", {}).get("textures", [])]}
    for path in (directory / "textures").glob("*.png"):
        if path.stem not in used:
            path.unlink()
    return materials, meshes


def export(variant, source, output):
    role = variant["role"]
    directory = output / variant["id"]
    if directory.exists():
        shutil.rmtree(directory)
    (directory / "textures").mkdir(parents=True)
    # A guitarist keeps the default GH3 guitar of its profile (the game uses it while the character plays the
    # guitarist). A singer keeps its microphone only when it is not the library one.
    instrument = (
        variant["instrumentModel"] if role == "guitarist" or variant["instrument"].lower().endswith("_fem") else ""
    )
    materials, meshes = load_models(source, [variant["model"], instrument], directory, role)
    runtime.write_model(directory, variant["id"], materials, meshes)
    info = {
        **variant,
        "instrumentInModel": bool(instrument),
        "vertices": sum(m[1] for m in meshes),
        "parts": len(meshes),
        "templates": sorted({m["gh3"]["template"] for m in materials.values() if "gh3" in m}),
    }
    (directory / "character.json").write_text(json.dumps(info, indent=2) + "\n")
    return info


def main(argv=None):
    parser = argparse.ArgumentParser(prog="python -m pipeline.characters", description=__doc__.splitlines()[0])
    parser.add_argument("--data", type=Path, default=GH3_DATA)
    parser.add_argument("--output", type=Path, default=CHARACTERS)
    parser.add_argument("--only", action="append", help="Export only this character ID (repeatable)")
    args = parser.parse_args(argv)
    source = Source(args.data)
    variants, skipped = inventory(args.data)
    args.output.mkdir(parents=True, exist_ok=True)
    exported = []
    for variant in variants:
        if args.only and variant["id"] not in args.only:
            continue
        info = export(variant, source, args.output)
        exported.append(info["id"])
        print(
            f"{info['id']}: {info['name']}, {info['vertices']} vertices, {info['parts']} parts, instrument {info['instrument'] or '-'}"
        )
    if not args.only:
        wanted = {v["id"] for v in variants}
        for folder in args.output.iterdir():
            if folder.is_dir() and folder.name not in wanted:
                shutil.rmtree(folder)
    (args.output / "inventory.json").write_text(
        json.dumps({"version": 1, "variants": variants, "skipped": skipped, "sources": source.hashes}, indent=2) + "\n"
    )
    print(f"{len(exported)} characters in {args.output}")


if __name__ == "__main__":
    main()
