"""Build the GH3 character library gh3-characters-v1: the rig profiles, animation sets, hair, and IK of the playable GH3
guitarists (docs/PACKAGE-FORMAT.md section 10.1).

GH3 gives each guitarist profile (`Musician_Profiles`, guitar_band_profile.qb) a skeleton and a ragdoll for each outfit
(`Skeleton`, `skeleton2`, `ragdoll`, `ragdoll2`), IK parameters (`ik_params`, default `Hero_Ik_params`), a model scale,
and an animation set: `anim_set` (the `<name>_animations` struct of guitar_animation_data_<name>.qb), `strum_anims`,
`finger_anims`, and `fret_anims`. create_band_member (guitar_anim_tree.qb) uses the outfit 2 skeleton and ragdoll when
the outfit is 2. create_guitarist (guitar_character.qb) copies the animation set fields of the profile; the bassist of a
co-op game is made by create_guitarist too, so a character plays the bass with its own set.

- Profiles: one for each distinct (skeleton, ragdoll, IK, scale, animation set). Axel's first outfit is the library
  profile gh3-guitarist-axel-v1 and is not repeated here.
- Animation sets: one for each distinct (anim_set, strum_anims, finger_anims, fret_anims); the God of Rock uses Axel's,
  the Grim Ripper Morello's clips with the `Normal` strums. The clips of a set come from its anim PAK, the model PAK
  (intro and win clips), the hand clips of perm_anims, and Judy's walk clips (`normal_walk_data`; GH3 plays them on
  every skeleton, the library band moves them to Axel's rig). All sets share one clip folder: GH3 shares clips by name,
  and a name has the same data in every PAK.
- Clips are stored as local transforms on the library rig gh3-guitarist-axel-v1 (`clipRig`), as the library band clips
  are. GH3 keys translations relative to the skeleton rest pose (pipeline.gh3.band_formats.sample_animation), so a
  profile plays a clip with the offset (profile rest position - clip rig rest position) on each position track; this is
  the GH3 result on the profile's own skeleton.
- Hair: all accessory chains of the ragdoll (pipeline.library.hair, all_chains). The God of Rock ragdoll and mesh fit
  the accessory bones of GH3_Guitarist_RockGod, not those of the skeleton of his profile (ACCESSORY_SKELETONS). On the PC port, create_guitarist does
  not call ragdoll_setaccessorybones for the Grim Ripper (`IsWinPort`), so his profile has no hair.

Output: assets/libraries/gh3-characters-v1/ (characters.json, clips/<clip>.bin.gz in GH3CLP01).

Usage: python -m pipeline.character_library [--data DATA] [--output DIR]
"""

import argparse
import gzip
import json
import shutil
from pathlib import Path

import numpy as np

from pipeline.characters import CLIP_RIG_PROFILE, outfits, profile_id, qb_items, slug
from pipeline.gh3.band_formats import skeleton
from pipeline.gh3.direction import Source, script
from pipeline.gh3.formats import find_array, find_struct, key, values
from pipeline.gh3.havok import Ragdoll
from pipeline.library import hair as hair_export
from pipeline.library.direction import flatten, hand_layers, write_clip
from pipeline.paths import CHARACTER_LIBRARY, GH3_DATA

LIBRARY_ID = "gh3-characters-v1"
CLIP_RIG = "GH3_Guitarist_Axel"
# create_guitarist skips ragdoll_setaccessorybones for this character on the PC port (IsWinPort).
NO_ACCESSORY_PHYSICS = {"Ripper"}
# The God of Rock profile names Axel's skeleton, and create_band_member gives that skeleton to the model and the ragdoll.
# His ragdoll and his mesh fit the accessory bones of GH3_Guitarist_RockGod (no profile names it): the hair hangs from
# the head there, and from the chest on Axel's skeleton. His rig takes those accessory bones; the other bones stay Axel's.
ACCESSORY_SKELETONS = {"RockGod": "GH3_Guitarist_RockGod"}
IK_FIELDS = ("bone0", "bone1", "bone2")


def guitarists(profiles):
    return [p for p in profiles if p.get(key("Type")) == key("Guitarist") and not p.get(key("debug_only"))]


def unity_rig(rig):
    """Bones (checksums), parents, and bind rows [x, y, z, qx, qy, qz, qw] in runtime coordinates (float32 values)."""
    names, parents, positions, rotations = rig
    bind = np.concatenate((positions * [1, 1, -1], rotations * [1, 1, -1, 1]), axis=1).astype(np.float32)
    return {"bones": [f"{n:08x}" for n in names], "parents": [int(p) for p in parents], "bind": [[float(v) for v in row] for row in bind]}


def with_accessories(rig, source, accessories):
    """`rig` with the parents and rest transforms of the accessory bones of `source` (a skeleton with the same bones)."""
    names, parents, positions, rotations = rig
    if list(names) != list(source[0]): raise ValueError("the accessory skeleton has other bones")
    parents, positions, rotations = list(parents), positions.copy(), rotations.copy()
    for i, name in enumerate(names):
        if name not in accessories: continue
        parents[i] = source[1][i]
        positions[i], rotations[i] = source[2][i], source[3][i]
    if any(names[p] in accessories for i, p in enumerate(parents) if p >= 0 and names[i] not in accessories):
        raise ValueError("a body bone hangs from an accessory bone")
    return names, parents, positions, rotations


def ik_chains(data, name):
    chains = []
    for row in find_array(data, name):
        chains.append({"bones": [f"{row[key(f)]:08x}" for f in IK_FIELDS], "target": f"{row[key('boneTarget')]:08x}",
                       "hinge": [float(row[key("HingeAxis")][0]), float(row[key("HingeAxis")][1]), float(row[key("HingeAxis")][2])],
                       "cosMax": float(row[key("CosMaxHingeAngle")]), "cosMin": float(row[key("CosMinHingeAngle")])})
    return chains


def struct_source(scripts, checksum):
    """The QB file that defines a struct."""
    found = [data for data in scripts.values() if find_struct(data, checksum) is not None]
    if not found: raise ValueError(f"no QB struct {checksum:08x}")
    return found[0]


def pak_name(directory, path):
    name = path.replace("\\", "/").rsplit("/", 1)[-1].lower().removesuffix(".pak") + ".pak.xen"
    found = [p.name for p in (directory/"PAK").iterdir() if p.name.lower() == name]
    if len(found) != 1: raise ValueError(f"no PAK for {path}")
    return found[0]


def build(data_directory, output):
    source = Source(data_directory)
    scripts = source.scripts()
    entries = source.load("ZONES/global.pak.xen")
    rigs = {n: b for k, n, b in entries if k == key(".ske")}
    ragdolls = {n: b for k, n, b in entries if k == key(".rag")}
    del entries
    profiles = find_array(script(scripts, "guitar_band_profile"), "Musician_Profiles")
    bodies = {row[key("desc_id")]: row for row in qb_items(script(scripts, "guitar_band_profile_data"))[key("musician_body")]}
    common = script(scripts, "guitar_animation_data")
    anim_tree = script(scripts, "guitar_anim_tree")
    accessories = hair_export.accessory_bones(scripts)
    clip_rig = skeleton(rigs[key(CLIP_RIG)])
    hands = {n: b for k, n, b in source.load("PAK/perm_anims.pak.xen") if k == key(".ska")}
    hands = {n: b for n, b in hands.items() if values(b, 44, "H")[0] == len(clip_rig[0])}
    walking = {n: b for k, n, b in source.load("PAK/Judy_1_anims.pak.xen") if k == key(".ska")}
    walk_groups = flatten(find_struct(common, "normal_walk_data"), "walk/")

    if output.exists(): shutil.rmtree(output)
    (output/"clips").mkdir(parents=True)
    library = {"version": 1, "library": LIBRARY_ID, "clipRig": {"profile": CLIP_RIG_PROFILE, **unity_rig(clip_rig)},
               "profiles": {}, "animations": {}, "hair": {}, "ik": {}}
    sets, clips, clip_bytes, missing = {}, {}, {}, {}

    def add_set(profile):
        signature = tuple(profile[key(f)] for f in ("anim_set", "strum_anims", "finger_anims", "fret_anims"))
        if signature in sets: return sets[signature]
        name = slug(profile[key("name")])
        sets[signature] = name
        data = struct_source(scripts, profile[key("anim_set")])
        groups = {**flatten(find_struct(data, profile[key("anim_set")])), **walk_groups}
        layers = hand_layers(data, common, profile, profiles)
        body = bodies[key(f"Guitarist_{profile[key('name')]}_Outfit1_Style1")]
        bank = {}
        for path in (body[key("anims_pak")], body[key("pak")]):
            bank.update({n: b for k, n, b in source.load(f"PAK/{pak_name(source.directory, path)}") if k == key(".ska")})
        bank.update(hands)
        walk_refs = {r for k, refs in walk_groups.items() for r in refs}
        set_clips, set_missing = {}, []
        for ref in sorted({r for refs in [*groups.values(), *layers.values()] for r in refs}):
            number = int(ref, 16)
            clip = bank.get(number, walking.get(number) if ref in walk_refs else None)
            if clip is None:
                set_missing.append(ref); continue
            path = output/"clips"/f"{ref}.bin.gz"
            # A separate folder keeps the file name that gzip stores in its header.
            temporary = output/".compare"/f"{ref}.bin.gz"
            info = write_clip(temporary, clip, clip_rig)
            data_bytes = gzip.decompress(temporary.read_bytes())
            if ref in clip_bytes and clip_bytes[ref] != data_bytes: raise ValueError(f"clip {ref} differs between animation sets")
            clip_bytes[ref] = data_bytes
            temporary.replace(path)
            set_clips[ref] = clips[ref] = info
        for table in (groups, layers):
            for field in list(table):
                table[field] = [ref for ref in table[field] if ref in set_clips]
                if not table[field]: del table[field]
        library["animations"][name] = {"animSet": f"{profile[key('anim_set')]:08x}", "groups": groups, "layers": layers,
                                       "clips": set_clips, "missingClips": set_missing}
        missing[name] = set_missing
        print(f"animation set {name}: {len(set_clips)} clips, {len(groups)} groups, {len(layers)} layers, {len(set_missing)} unavailable references")
        return name

    seen = {}
    for profile in guitarists(profiles):
        name = profile[key("name")]
        animations = add_set(profile)
        ik_name = profile.get(key("ik_params"), key("Hero_Ik_params"))
        ik = "satan" if ik_name == key("Satan_Ik_params") else "hero"
        if ik not in library["ik"]: library["ik"][ik] = ik_chains(anim_tree, ik_name)
        scale = profile.get(key("scale"), [1, 1, 1])
        if len(set(scale)) != 1: raise ValueError("non-uniform profile scale")
        for outfit, rig_name, ragdoll_name, second in outfits(profile):
            identifier = profile_id(name, second)
            signature = (rig_name, ragdoll_name, ik, scale[0], animations)
            if identifier in seen:
                if seen[identifier] != signature: raise ValueError(f"profile {identifier} has two definitions")
                continue
            seen[identifier] = signature
            if identifier == CLIP_RIG_PROFILE:
                if rig_name != key(CLIP_RIG) or ragdoll_name != 0xeaebb800: raise ValueError("Axel's first outfit is not the library profile")
                continue
            rig = skeleton(rigs[rig_name])
            if name in ACCESSORY_SKELETONS: rig = with_accessories(rig, skeleton(rigs[key(ACCESSORY_SKELETONS[name])]), accessories)
            hair = None
            if name not in NO_ACCESSORY_PHYSICS:
                hair = f"{ragdoll_name:08x}"
                if hair not in library["hair"]:
                    member = hair_export.ragdoll_member(Ragdoll(ragdolls[ragdoll_name]), rig, accessories, all_chains=True)
                    library["hair"][hair] = {**member, "ragdoll": hair}
            library["profiles"][identifier] = {
                "role": "guitarist", "name": profile[key("fullname")], "outfit": outfit, "skeleton": f"{rig_name:08x}",
                "scale": float(scale[0]), "animations": animations, "hair": hair, "ik": ik, **unity_rig(rig)}
            print(f"{identifier}: skeleton {rig_name:08x}, animations {animations}, hair {hair or '-'}, IK {ik}, scale {float(scale[0]):g}")
    shutil.rmtree(output/".compare")
    library["sources"] = source.hashes
    (output/"characters.json").write_text(json.dumps(library, separators=(",", ":")) + "\n")
    print(f"{len(library['profiles'])} profiles, {len(library['animations'])} animation sets, {len(clips)} clips -> {output}")


def main(argv=None):
    parser = argparse.ArgumentParser(prog="python -m pipeline.character_library", description=__doc__.splitlines()[0])
    parser.add_argument("--data", type=Path, default=GH3_DATA)
    parser.add_argument("--output", type=Path, default=CHARACTER_LIBRARY)
    args = parser.parse_args(argv)
    build(args.data, args.output)


if __name__ == "__main__":
    main()
