"""Compare the `[trace] band` lines of a game log with the GH3 clips sampled on the GH3 skeleton of each profile.

Run the game with GH3_SKIN_TRACE=1 and a GH3 guitarist character (docs/VERIFICATION.md, GH3 characters), then:
    MISE_QUIET=1 uv run --with numpy --with pillow python tools/check-character-trace.py <game>/MelonLoader/Latest.log
It prints the largest position and rotation difference over all logged bones. Lines of the band library members
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
)


def main():
    log = Path(sys.argv[1]).read_text(errors="replace")
    library = json.loads((CHARACTER_LIBRARY / "characters.json").read_text())
    source = Source(GH3_DATA)
    rigs = {n: b for k, n, b in source.load("ZONES/global.pak.xen") if k == key(".ske")}
    scripts = source.scripts()
    profiles = {p[key("name")]: p for p in qb_items(script(scripts, "guitar_band_profile"))[key("Musician_Profiles")]}
    bodies = {
        row[key("desc_id")]: row for row in qb_items(script(scripts, "guitar_band_profile_data"))[key("musician_body")]
    }
    banks = {}

    def bank(set_name):
        if set_name not in banks:
            name = next(
                n
                for n, p in profiles.items()
                if f"{p[key('anim_set')]:08x}" == library["animations"][set_name]["animSet"]
            )
            body = bodies[key(f"Guitarist_{name}_Outfit1_Style1")]
            clips = {}
            for path in (body[key("anims_pak")], body[key("pak")], "perm_anims.pak", "Judy_1_anims.pak"):
                for kind, clip, data in source.load(f"PAK/{pak_name(source.directory, path)}"):
                    if kind == key(".ska"):
                        clips.setdefault(clip, data)
            banks[set_name] = clips
        return banks[set_name]

    worst_position = worst_rotation = 0.0
    samples = lines = 0
    for match in LINE.finditer(log):
        _, _, profile, set_name, _, clip, clip_time, _, bones = match.groups()
        if profile == "library":
            continue
        rig = skeleton(rigs[int(library["profiles"][profile]["skeleton"], 16)])
        p, q = sample_animation(
            animation(bank(set_name)[int(clip, 16)], len(rig[0])), rig[2], rig[3], np.array([float(clip_time)])
        )
        for item in bones.split(";"):
            index, values = item.split(":")
            value = np.array([float(v) for v in values.split(",")])
            position = p[0, int(index)] * [1, 1, -1]
            rotation = q[0, int(index)] * [1, 1, -1, 1]
            chord = min(np.linalg.norm(value[3:] - rotation), np.linalg.norm(value[3:] + rotation))
            worst_position = max(worst_position, float(np.linalg.norm(value[:3] - position)))
            worst_rotation = max(worst_rotation, float(4 * np.degrees(np.arcsin(min(1, chord / 2)))))
            samples += 1
        lines += 1
    print(
        f"{lines} trace lines, {samples} bone samples: largest difference {worst_position * 1000:.3f} mm, {worst_rotation:.4f} degrees"
    )
    return 0 if lines else 1


if __name__ == "__main__":
    sys.exit(main())
