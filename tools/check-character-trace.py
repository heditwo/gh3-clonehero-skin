"""Compare the `[trace] band` lines of a game log with the GH3 clips sampled on the GH3 skeleton of each profile.

Run the game with GH3_SKIN_TRACE=1 and a GH3 character on a character library profile (docs/VERIFICATION.md, GH3
characters), then:
    MISE_QUIET=1 uv run --with numpy --with pillow python tools/check-character-trace.py <game>/MelonLoader/Latest.log
It prints the largest position and rotation difference over all logged bones of the body clips, and of the song facial
clips of a singer (`face=`, the clip of the song PAK on the singer's skeleton). Lines of the band library members
(`profile=library`) are skipped."""

import json
import re
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from pipeline.character_library import pak_name  # noqa: E402
from pipeline.characters import qb_items  # noqa: E402
from pipeline.gh3.band_formats import animation, sample_animation, skeleton  # noqa: E402
from pipeline.gh3.data import Source, script  # noqa: E402
from pipeline.gh3.formats import key  # noqa: E402
from pipeline.paths import CHARACTER_LIBRARY, GH3_DATA  # noqa: E402

LINE = re.compile(
    r"\[trace\] band t=(\S+) role=(\S+) profile=(\S+) set=(\S+) stance=(\S+) clip=(\S+) clipTime=(\S+) scale=(\S+) bones=(\S+)"
    r"(?:.*? face=(\S+) faceTime=(\S+) faceBones=(\S+))?"
)


class Clips:
    """The GH3 clips of the animation sets and the song facial clips, by checksum."""

    def __init__(self, source, library):
        self.source, self.library = source, library
        scripts = source.scripts()
        self.profiles = {
            p[key("name")]: p for p in qb_items(script(scripts, "guitar_band_profile"))[key("Musician_Profiles")]
        }
        self.bodies = {
            row[key("desc_id")]: row
            for row in qb_items(script(scripts, "guitar_band_profile_data"))[key("musician_body")]
        }
        self.banks, self.songs = {}, None

    def body(self, name, profile):
        if profile.get(key("Type")) == key("Guitarist"):
            return self.bodies[key(f"Guitarist_{name}_Outfit1_Style1")]
        return self.bodies[profile[key("musician_body")][key("desc_id")]]

    def bank(self, set_name):
        if set_name not in self.banks:
            anim_set = self.library["animations"][set_name]["animSet"]
            name, profile = next((n, p) for n, p in self.profiles.items() if f"{p[key('anim_set')]:08x}" == anim_set)
            body = self.body(name, profile)
            clips = {}
            for path in (body[key("anims_pak")], body[key("pak")], "perm_anims.pak", "Judy_1_anims.pak"):
                for kind, clip, data in self.source.load(f"PAK/{pak_name(self.source.directory, path)}"):
                    if kind == key(".ska"):
                        clips.setdefault(clip, data)
            self.banks[set_name] = clips
        return self.banks[set_name]

    def face(self, clip):
        if self.songs is None:
            self.songs = {}
            for pak in sorted((self.source.directory / "SONGS").glob("*_song.pak.xen")):
                for kind, name, data in self.source.load(f"SONGS/{pak.name}"):
                    if kind == key(".ska"):
                        self.songs.setdefault(name, data)
        return self.songs[clip]


def compare(clip, clip_time, rig, bones, worst):
    p, q = sample_animation(animation(clip, len(rig[0])), rig[2], rig[3], np.array([clip_time]))
    count = 0
    for item in bones.split(";"):
        index, values = item.split(":")
        value = np.array([float(v) for v in values.split(",")])
        position = p[0, int(index)] * [1, 1, -1]
        rotation = q[0, int(index)] * [1, 1, -1, 1]
        chord = min(np.linalg.norm(value[3:] - rotation), np.linalg.norm(value[3:] + rotation))
        worst[0] = max(worst[0], float(np.linalg.norm(value[:3] - position)))
        worst[1] = max(worst[1], float(4 * np.degrees(np.arcsin(min(1, chord / 2)))))
        count += 1
    return count


def main():
    log = Path(sys.argv[1]).read_text(errors="replace")
    library = json.loads((CHARACTER_LIBRARY / "characters.json").read_text())
    source = Source(GH3_DATA)
    rigs = {n: b for k, n, b in source.load("ZONES/global.pak.xen") if k == key(".ske")}
    clips = Clips(source, library)
    body, face = [0.0, 0.0], [0.0, 0.0]
    samples = face_samples = lines = 0
    for match in LINE.finditer(log):
        _, _, profile, set_name, _, clip, clip_time, _, bones, face_clip, face_time, face_bones = match.groups()
        if profile == "library":
            continue
        rig = skeleton(rigs[int(library["profiles"][profile]["skeleton"], 16)])
        samples += compare(clips.bank(set_name)[int(clip, 16)], float(clip_time), rig, bones, body)
        if face_clip:
            face_samples += compare(clips.face(int(face_clip, 16)), float(face_time), rig, face_bones, face)
        lines += 1
    print(
        f"{lines} trace lines, {samples} bone samples: largest difference {body[0] * 1000:.3f} mm, {body[1]:.4f} degrees"
    )
    if face_samples:
        print(
            f"song facial clips: {face_samples} bone samples: largest difference {face[0] * 1000:.3f} mm, {face[1]:.4f} degrees"
        )
    return 0 if lines else 1


if __name__ == "__main__":
    sys.exit(main())
