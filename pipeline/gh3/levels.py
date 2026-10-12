"""GH3 venue data from the QB scripts: the venue titles of `LevelZones` and the zones of `UseSmallVenueAnims`.

`LevelZones` (a struct of the level QB file) has one row for each level: `name` (the zone, for example `z_dive`) and
`title` (the venue name of the GH3 menus). `UseSmallVenueAnims` (guitar_character.qb) is a switch on the current zone
(`GetPakManCurrent map=zones`): the cases that set the result to true without a condition use the small-stage intro and
win animations (`intro_smstg`, `win_smstg`). A case with a condition (z_artdeco: `GetNodeFlag`) is not a small venue
here: the venue scripts never set that node flag.
"""

from pipeline.gh3.data import script
from pipeline.gh3.formats import find_struct, key
from pipeline.gh3.script_bank import tokens

# GH3 script opcodes (src/Gh3/Gh3QbConstants.cs).
OP_SWITCH = "3c"
OP_END_SWITCH = "3d"
OP_CASE = "3e"
OP_DEFAULT = "3f"
OP_IF = "25"
OP_FAST_IF = "47"
OP_NAME = "16"
OP_SHORT_BREAK = "49"
QB_TYPE_CHECKSUM = 13


def level_titles(scripts: dict[int, bytes]) -> dict[str, str]:
    """{zone name: venue title} of the `LevelZones` struct."""
    for data in scripts.values():
        zones = find_struct(data, "LevelZones")
        if zones is not None:
            return {
                row[key("name")]: row[key("title")] for name, row in zones.items() if name and isinstance(row, dict)
            }
    raise ValueError("no LevelZones struct in the QB files")


def small_venue_zones(scripts: dict[int, bytes]) -> set[int]:
    """The checksums of the zones that `UseSmallVenueAnims` gives the small-stage animations without a condition."""
    code = tokens(script(scripts, "guitar_character"), "UseSmallVenueAnims")
    true = f"0x{key('true'):08x}"
    found = set()
    cases, body, inside = [], [], False

    def close():
        if cases and not any(op in (OP_IF, OP_FAST_IF) for op, _ in body) and any(value == true for _, value in body):
            found.update(cases)

    for index, token in enumerate(code):
        op, value = token["op"], token["value"]
        if op == OP_SWITCH:
            inside = True
        elif not inside:
            continue
        elif op in (OP_CASE, OP_DEFAULT, OP_END_SWITCH):
            close()
            cases, body = [], []
            # A case is followed by its jump offset (a short break) and then its value.
            following = next((t for t in code[index + 1 : index + 3] if t["op"] != OP_SHORT_BREAK), None)
            if op == OP_CASE and following and following["op"] == OP_NAME and following["value"][0] == QB_TYPE_CHECKSUM:
                cases = [int(following["value"][1], 16)]
            inside = op != OP_END_SWITCH
        else:
            body.append((op, value[1] if isinstance(value, list) and value[0] == QB_TYPE_CHECKSUM else None))
    if not found:
        raise ValueError("UseSmallVenueAnims has no small venue")
    return found
