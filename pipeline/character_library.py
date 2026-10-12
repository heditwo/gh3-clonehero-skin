"""Build the GH3 character library gh3-characters-v1: the rig profiles, animation sets, hair, and IK of the playable GH3
guitarists and singers (docs/PACKAGE-FORMAT.md section 10.1).

GH3 gives each guitarist profile (`Musician_Profiles`, guitar_band_profile.qb) a skeleton and a ragdoll for each outfit
(`Skeleton`, `skeleton2`, `ragdoll`, `ragdoll2`), IK parameters (`ik_params`, default `Hero_Ik_params`), a model scale,
and an animation set: `anim_set` (the `<name>_animations` struct of guitar_animation_data_<name>.qb), `strum_anims`,
`finger_anims`, and `fret_anims`. create_band_member (guitar_anim_tree.qb) uses the outfit 2 skeleton and ragdoll when
the outfit is 2. create_guitarist (guitar_character.qb) copies the animation set fields of the profile; the bassist of a
co-op game is made by create_guitarist too, so a character plays the bass with its own set.

- Profiles: one for each distinct (skeleton, ragdoll, IK, scale, animation set), also Axel's first outfit (its rig is
  the rig of the fixed library profile gh3-guitarist-axel-v1, so its ID has `-outfit1`).
- Animation sets: one for each distinct (anim_set, strum_anims, finger_anims, fret_anims); the God of Rock uses Axel's,
  the Grim Ripper Morello's clips with the `Normal` strums. The clips of a set come from its anim PAK, the model PAK
  (intro and win clips), and the hand clips of perm_anims. The walk groups are the set's own `walking` struct
  (pipeline.library.direction.walk_groups; GH3 walk_to_pos plays it). All sets share one clip folder: GH3 shares clips by
  name, and a name has the same data in every PAK.
- Clips are stored as local transforms on the rig of the library profile gh3-guitarist-axel-v1 (`clipRig`), as the
  library band clips are. GH3 keys translations relative to the skeleton rest pose (pipeline.gh3.band_formats.sample_animation), so a
  profile plays a clip with the offset (profile rest position - clip rig rest position) on each position track; this is
  the GH3 result on the profile's own skeleton.
- Singers: create_band (guitar_character.qb) selects the vocalist profile from the `singer` field of the song (Singer,
  Singer_Female, Singer_Bret; the *_Alt profiles in z_wikker, z_budokan, and z_hell only change the microphone), and
  create_band_member gives it the profile's `Skeleton` and `ragdoll`. Singer_Female has GH3_Singer_Female,
  ragdoll_female_singer, and singer_female_animations; Singer_Bret has GH3_Singer_BretMichaels, the male ragdoll, and
  singer_male_animations. vocalist_static_tree uses singer_IK_Params for every vocalist. The singer clips are stored on
  the GH3_Singer_Male rig (the clip rig `singer` of `clipRigs`, the rig of gh3-singer-male-v1), so that the song facial
  clips of the band library member and the set clips take the same offsets.
- Hair: all accessory chains of the ragdoll (pipeline.library.hair, all_chains). GH3 has a skeleton named for each
  guitarist profile (`GH3_Guitarist_<name>`, `_2` for outfit 2). When the profile names another skeleton (the God of Rock
  names Axel's), the ragdoll and the mesh fit the accessory bones of the skeleton of its name: the rig takes them. On the
  PC port, create_guitarist does not call ragdoll_setaccessorybones for the characters that its `IsWinPort` branch
  excludes (the Grim Ripper), so their profiles have no hair (pc_port_without_accessories).

Output: assets/libraries/gh3-characters-v1/ (characters.json, clips/<clip>.bin.gz in GH3CLP01).

Usage: python -m pipeline.character_library [--data DATA] [--output DIR]
"""

import argparse
import gzip
import json
import shutil
from pathlib import Path

import numpy as np

from pipeline.characters import SINGERS, outfits, profile_id, qb_items, slug
from pipeline.gh3.band_formats import clip_bone_count, skeleton
from pipeline.gh3.data import Source, script
from pipeline.gh3.formats import find_array, find_struct, key
from pipeline.gh3.havok import Ragdoll
from pipeline.gh3.script_bank import tokens
from pipeline.library import hair as hair_export, rigs
from pipeline.library.direction import flatten, hand_layers, walk_groups, without_walking, write_clip
from pipeline.package.profiles import CHARACTER_LIBRARY_ID
from pipeline.paths import CHARACTER_LIBRARY, GH3_DATA

CLIP_RIG = "GH3_Guitarist_Axel"
# The fixed profile of the band library on CLIP_RIG (the band library guitarist), for the clipRig label.
CLIP_RIG_PROFILE = "gh3-guitarist-axel-v1"
SINGER_CLIP_RIG = "GH3_Singer_Male"
SINGER_CLIP_RIG_ID = "singer"
SINGER_CLIP_RIG_PROFILE = "gh3-singer-male-v1"
# GH3 IK parameter arrays (guitar_anim_tree.qb) of the profiles: `ik_params` of a guitarist profile (default
# Hero_Ik_params), and singer_IK_Params of vocalist_static_tree for every vocalist. The IK ID is the start of the name.
IK_ARRAYS = ("Hero_Ik_params", "Satan_Ik_params", "singer_IK_Params")
DEFAULT_IK = "Hero_Ik_params"
SINGER_IK = "singer_IK_Params"
# The IDs of the singer animation sets (the `<name>_animations` structs of guitar_animation_data_singer_*.qb).
SINGER_SETS = {"singer_male_animations": "singer-male", "singer_female_animations": "singer-female"}
# GH3 script opcodes (src/Gh3/Gh3QbConstants.cs) of the IsWinPort branch of create_guitarist.
OP_FAST_IF = "47"
OP_NOT = "39"
OP_OPEN = "0e"
OP_ARGUMENT = "2d"
OP_EQUALS = "07"
OP_NAME = "16"
QB_TYPE_CHECKSUM = 13


def guitarists(profiles):
    return [p for p in profiles if p.get(key("Type")) == key("Guitarist") and not p.get(key("debug_only"))]


def singers(profiles):
    return [p for p in profiles if p.get(key("Type")) == key("vocalist") and p[key("name")] in SINGERS]


def unity_rig(rig):
    """Bones (checksums), parents, and bind rows [x, y, z, qx, qy, qz, qw] in runtime coordinates (float32 values)."""
    names, parents, positions, rotations = rig
    bind = np.concatenate((positions * [1, 1, -1], rotations * [1, 1, -1, 1]), axis=1).astype(np.float32)
    return {
        "bones": [f"{n:08x}" for n in names],
        "parents": [int(p) for p in parents],
        "bind": [[float(v) for v in row] for row in bind],
    }


def with_accessories(rig, source, accessories):
    """`rig` with the parents and rest transforms of the accessory bones of `source` (a skeleton with the same bones)."""
    names, parents, positions, rotations = rig
    if list(names) != list(source[0]):
        raise ValueError("the accessory skeleton has other bones")
    parents, positions, rotations = list(parents), positions.copy(), rotations.copy()
    for i, name in enumerate(names):
        if name not in accessories:
            continue
        parents[i] = source[1][i]
        positions[i], rotations[i] = source[2][i], source[3][i]
    if any(names[p] in accessories for i, p in enumerate(parents) if p >= 0 and names[i] not in accessories):
        raise ValueError("a body bone hangs from an accessory bone")
    return names, parents, positions, rotations


def pc_port_without_accessories(scripts):
    """The `character_id` checksums for which create_guitarist (guitar_character.qb) does not call
    ragdoll_setaccessorybones on the PC port: `if IsWinPort` then `if NOT (<character_id> = X)` before the call."""
    code = tokens(script(scripts, "guitar_character"), "create_guitarist")

    def name(index):
        token = code[index] if index < len(code) else None
        return token["value"][1] if token and token["op"] == OP_NAME and token["value"][0] == QB_TYPE_CHECKSUM else None

    port = next(
        i for i in range(1, len(code)) if code[i - 1]["op"] == OP_FAST_IF and name(i) == f"0x{key('IsWinPort'):08x}"
    )
    call = next(i for i in range(port, len(code)) if name(i) == f"0x{key('ragdoll_setaccessorybones'):08x}")
    pattern = [OP_FAST_IF, OP_NOT, OP_OPEN, OP_ARGUMENT, OP_NAME, OP_EQUALS, OP_NAME]
    for i in range(port, call - len(pattern)):
        ops = [code[i + k]["op"] for k in range(len(pattern))]
        if ops == pattern and name(i + 4) == f"0x{key('character_id'):08x}":
            return {int(name(i + 6), 16)}
    raise ValueError("create_guitarist has no IsWinPort exclusion before ragdoll_setaccessorybones")


def struct_source(scripts, checksum):
    """The QB file that defines a struct."""
    found = [data for data in scripts.values() if find_struct(data, checksum) is not None]
    if not found:
        raise ValueError(f"no QB struct {checksum:08x}")
    return found[0]


def pak_name(directory, path):
    name = path.replace("\\", "/").rsplit("/", 1)[-1].lower().removesuffix(".pak") + ".pak.xen"
    found = [p.name for p in (directory / "PAK").iterdir() if p.name.lower() == name]
    if len(found) != 1:
        raise ValueError(f"no PAK for {path}")
    return found[0]


class _Builder:
    """The state of one library build: the GH3 data, the output folder, and the library JSON."""

    def __init__(self, data_directory, output):
        self.source = Source(data_directory)
        self.scripts = self.source.scripts()
        entries = self.source.load("ZONES/global.pak.xen")
        self.rigs = {n: b for k, n, b in entries if k == key(".ske")}
        self.ragdolls = {n: b for k, n, b in entries if k == key(".rag")}
        del entries
        self.profiles = find_array(script(self.scripts, "guitar_band_profile"), "Musician_Profiles")
        self.bodies = {
            row[key("desc_id")]: row
            for row in qb_items(script(self.scripts, "guitar_band_profile_data"))[key("musician_body")]
        }
        self.common = script(self.scripts, "guitar_animation_data")
        self.anim_tree = script(self.scripts, "guitar_anim_tree")
        self.no_accessories = pc_port_without_accessories(self.scripts)
        self.accessories = hair_export.accessory_bones(self.scripts)
        self.clip_rig = skeleton(self.rigs[key(CLIP_RIG)])
        self.singer_rig = skeleton(self.rigs[key(SINGER_CLIP_RIG)])
        self.hands = {n: b for k, n, b in self.source.load("PAK/perm_anims.pak.xen") if k == key(".ska")}
        # The clips of normal_walk_data (Judy's PAK), the walk of a set without `walking` (walk_groups).
        self.default_walk_clips = {n: b for k, n, b in self.source.load("PAK/Judy_1_anims.pak.xen") if k == key(".ska")}
        self.default_walk_refs = {
            ref for refs in flatten(find_struct(self.common, "normal_walk_data")).values() for ref in refs
        }
        self.output = output
        self.library = {
            "version": 1,
            "library": CHARACTER_LIBRARY_ID,
            "clipRig": {
                "profile": CLIP_RIG_PROFILE,
                **unity_rig(self.clip_rig),
                "points": rigs.points(self.clip_rig[0]),
            },
            "clipRigs": {
                SINGER_CLIP_RIG_ID: {
                    "profile": SINGER_CLIP_RIG_PROFILE,
                    **unity_rig(self.singer_rig),
                    "points": rigs.points(self.singer_rig[0]),
                }
            },
            "profiles": {},
            "animations": {},
            "hair": {},
            "ik": {},
            "constraints": rigs.constraints(self.anim_tree),
        }
        self.sets, self.clips, self.clip_bytes = {}, {}, {}

    def body_bank(self, body, rig):
        """The clips of the anim and model PAKs of a musician body and the perm_anims hand clips of the rig."""
        bank = {}
        for path in (body[key("anims_pak")], body[key("pak")]):
            bank.update(
                {
                    n: b
                    for k, n, b in self.source.load(f"PAK/{pak_name(self.source.directory, path)}")
                    if k == key(".ska")
                }
            )
        bank.update({n: b for n, b in self.hands.items() if clip_bone_count(b) == len(rig[0])})
        return bank

    def write_set(self, name, anim_set, groups, layers, bank, rig, clip_rig_id=None, walking=None):
        """Write the clips of one animation set (on `rig`, the clip rig of the set) and add the set to the library.
        `walking`: the walk scalars of the set (walk_groups)."""
        set_clips, set_missing = {}, []
        for ref in sorted({r for refs in [*groups.values(), *layers.values()] for r in refs}):
            number = int(ref, 16)
            clip = bank.get(number, self.default_walk_clips.get(number) if ref in self.default_walk_refs else None)
            if clip is None:
                set_missing.append(ref)
                continue
            path = self.output / "clips" / f"{ref}.bin.gz"
            # A separate folder keeps the file name that gzip stores in its header.
            temporary = self.output / ".compare" / f"{ref}.bin.gz"
            info = write_clip(temporary, clip, rig)
            data_bytes = gzip.decompress(temporary.read_bytes())
            if ref in self.clip_bytes and self.clip_bytes[ref] != data_bytes:
                raise ValueError(f"clip {ref} differs between animation sets")
            self.clip_bytes[ref] = data_bytes
            temporary.replace(path)
            set_clips[ref] = self.clips[ref] = info
        for table in (groups, layers):
            for field in list(table):
                table[field] = [ref for ref in table[field] if ref in set_clips]
                if not table[field]:
                    del table[field]
        self.library["animations"][name] = {
            "animSet": f"{anim_set:08x}",
            **({"clipRig": clip_rig_id} if clip_rig_id else {}),
            "groups": groups,
            "layers": layers,
            "clips": set_clips,
            "missingClips": set_missing,
            **({"walking": walking} if walking else {}),
        }
        print(
            f"animation set {name}: {len(set_clips)} clips, {len(groups)} groups, {len(layers)} layers, {len(set_missing)} unavailable references"
        )

    def guitarist_set(self, profile):
        signature = tuple(profile[key(f)] for f in ("anim_set", "strum_anims", "finger_anims", "fret_anims"))
        if signature not in self.sets:
            name = self.sets[signature] = slug(profile[key("name")])
            data = struct_source(self.scripts, profile[key("anim_set")])
            anim_set = find_struct(data, profile[key("anim_set")])
            walk, walking = walk_groups(anim_set, self.common)
            groups = {**flatten(without_walking(anim_set)), **walk}
            layers = hand_layers(data, self.common, profile, self.profiles)
            body = self.bodies[key(f"Guitarist_{profile[key('name')]}_Outfit1_Style1")]
            bank = self.body_bank(body, self.clip_rig)
            self.write_set(name, profile[key("anim_set")], groups, layers, bank, self.clip_rig, walking=walking)
        return self.sets[signature]

    def singer_set(self, profile):
        """The animation set of a vocalist profile: the `anim_set` struct without hand layers (vocalist_static_tree has
        none), on the singer clip rig."""
        signature = (profile[key("anim_set")],)
        if signature not in self.sets:
            name = self.sets[signature] = next(v for n, v in SINGER_SETS.items() if key(n) == profile[key("anim_set")])
            data = struct_source(self.scripts, profile[key("anim_set")])
            groups = flatten(without_walking(find_struct(data, profile[key("anim_set")])))
            body = self.bodies[profile[key("musician_body")][key("desc_id")]]
            bank = self.body_bank(body, self.singer_rig)
            self.write_set(name, profile[key("anim_set")], groups, {}, bank, self.singer_rig, SINGER_CLIP_RIG_ID)
        return self.sets[signature]

    def add_hair(self, ragdoll_name, rig):
        """The hair ID of a ragdoll on a rig: all its accessory chains (pipeline.library.hair, all_chains)."""
        hair = f"{ragdoll_name:08x}"
        if hair not in self.library["hair"]:
            member = hair_export.ragdoll_member(
                Ragdoll(self.ragdolls[ragdoll_name]), rig, self.accessories, all_chains=True
            )
            self.library["hair"][hair] = {**member, "ragdoll": hair}
        return hair

    def add_ik(self, qb_name):
        """The IK ID of a GH3 IK parameter array (by checksum or name): the start of its name, for example `hero`."""
        name = next(n for n in IK_ARRAYS if key(n) == (key(qb_name) if isinstance(qb_name, str) else qb_name))
        ik = name.lower().split("_ik_")[0]
        if ik not in self.library["ik"]:
            self.library["ik"][ik] = rigs.ik_chains(self.anim_tree, name)
        return ik

    def add_guitarists(self):
        seen = {}
        for profile in guitarists(self.profiles):
            name = profile[key("name")]
            animations = self.guitarist_set(profile)
            ik = self.add_ik(profile.get(key("ik_params"), DEFAULT_IK))
            scale = uniform_scale(profile)
            for outfit, rig_name, ragdoll_name, second in outfits(profile):
                identifier = profile_id(name, second)
                signature = (rig_name, ragdoll_name, ik, scale, animations)
                if identifier in seen:
                    if seen[identifier] != signature:
                        raise ValueError(f"profile {identifier} has two definitions")
                    continue
                seen[identifier] = signature
                rig = skeleton(self.rigs[rig_name])
                named = key(f"GH3_Guitarist_{name}{'_2' if second else ''}")
                if named != rig_name and named in self.rigs:
                    rig = with_accessories(rig, skeleton(self.rigs[named]), self.accessories)
                hair = None if key(name) in self.no_accessories else self.add_hair(ragdoll_name, rig)
                self.add_profile(
                    identifier,
                    "guitarist",
                    profile[key("fullname")],
                    outfit,
                    rig_name,
                    rig,
                    scale,
                    animations,
                    hair,
                    ik,
                )

    def add_singers(self):
        for profile in singers(self.profiles):
            identifier, label = SINGERS[profile[key("name")]]
            rig_name, ragdoll_name = profile[key("Skeleton")], profile[key("ragdoll")]
            rig = skeleton(self.rigs[rig_name])
            animations = self.singer_set(profile)
            ik = self.add_ik(SINGER_IK)
            hair = self.add_hair(ragdoll_name, rig)
            self.add_profile(
                identifier, "singer", label, 1, rig_name, rig, uniform_scale(profile), animations, hair, ik
            )

    def add_profile(self, identifier, role, name, outfit, rig_name, rig, scale, animations, hair, ik):
        self.library["profiles"][identifier] = {
            "role": role,
            "name": name,
            "outfit": outfit,
            "skeleton": f"{rig_name:08x}",
            "scale": scale,
            "animations": animations,
            "hair": hair,
            "ik": ik,
            **unity_rig(rig),
        }
        print(
            f"{identifier}: skeleton {rig_name:08x}, animations {animations}, hair {hair or '-'}, IK {ik}, scale {scale:g}"
        )


def uniform_scale(profile):
    scale = profile.get(key("scale"), [1, 1, 1])
    if len(set(scale)) != 1:
        raise ValueError("non-uniform profile scale")
    return float(scale[0])


def build(data_directory, output):
    if output.exists():
        shutil.rmtree(output)
    (output / "clips").mkdir(parents=True)
    builder = _Builder(data_directory, output)
    builder.add_guitarists()
    builder.add_singers()
    shutil.rmtree(output / ".compare")
    library = builder.library
    library["sources"] = builder.source.hashes
    (output / "characters.json").write_text(json.dumps(library, separators=(",", ":")) + "\n")
    print(
        f"{len(library['profiles'])} profiles, {len(library['animations'])} animation sets, {len(builder.clips)} clips -> {output}"
    )


def main(argv=None):
    parser = argparse.ArgumentParser(prog="python -m pipeline.character_library", description=__doc__.splitlines()[0])
    parser.add_argument("--data", type=Path, default=GH3_DATA)
    parser.add_argument("--output", type=Path, default=CHARACTER_LIBRARY)
    args = parser.parse_args(argv)
    build(args.data, args.output)


if __name__ == "__main__":
    main()
