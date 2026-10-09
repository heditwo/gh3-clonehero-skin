"""Read-only index of a typed script bank (scripts.json, PACKAGE-FORMAT 8.1) and edits of its simple typed values.

The bank keeps GH3 QB values as [type, value] (src/Native/Gh3QbText.cs). Script code (type 7) is compiled GH3 byte
code: the add-on shows its size, never its content, and does not change it. No bpy; the tests use it without Blender.
"""

import json
import re

from .sidecars import checksum_text, qb_key

TYPE_NAMES = {1: "integer", 2: "float", 3: "string", 4: "wide string", 5: "vector 2", 6: "vector 3", 7: "script", 10: "struct",
              12: "array", 13: "checksum", 26: "checksum", 28: "checksum"}
SIMPLE_TYPES = {1, 2, 3, 4, 5, 6, 13, 26, 28}
# Names of common GH3 node fields and classes, so that the browser can show them instead of checksums.
KNOWN_NAMES = ("name", "pos", "angles", "class", "type", "createdatstart", "triggerscript", "suspenddistance", "lod_dist1", "lod_dist2",
               "lod_dist_min", "lod_dist_max", "profile", "lightgroup", "light_group", "links", "abs", "spawnobjscript", "model",
               "meshname", "collision", "levelobject", "levelgeometry", "gameobject", "particleobject", "pedestrian", "default",
               "node_flags", "visiblefromstart", "lightvolume", "light", "level_light", "lightvolumeobject", "dummy", "animationevents",
               "params", "time", "scr", "texture", "color", "radius", "length", "height", "width", "speed", "intensity")
LOCKED_FIELDS = {qb_key("name")}
# Node fields that hold the scripts that GH3 runs for the node.
SCRIPT_FIELDS = {qb_key("triggerscript"): "trigger script", qb_key("spawnobjscript"): "spawn script"}


def key_text(value):
    return f"0x{value:08x}"


class ScriptIndex:
    """Objects (node array entries) and events (scripts that the bank, the stage, and the actors start) of a bank."""

    def __init__(self, bank, names=()):
        self.bank = bank
        self.definitions = bank.get("definitions", {})
        self.names = {key_text(qb_key(name)): name for name in (*KNOWN_NAMES, *bank.get("startup", []), *bank.get("nodes", []),
                                                                 *([bank["performanceChanged"]] if bank.get("performanceChanged") else []))}
        for name in names: self.names.setdefault(checksum_text(name), name)
        self.nodes = []
        for array in bank.get("nodes", []):
            value = self.definition(array)
            if not value or value[0] != 12: continue
            for index, item in enumerate(value[1]):
                if item[0] != 10: continue
                fields = {field: typed for field, typed in item[1]}
                node_id = fields.get(key_text(qb_key("name")), [13, ""])[1]
                self.nodes.append({"id": node_id, "array": array, "index": index, "fields": fields,
                                   "class": self.label(fields.get(key_text(qb_key("class")), [0, ""])[1]),
                                   "type": self.label(fields.get(key_text(qb_key("type")), [0, ""])[1])})

    def definition(self, name_or_key):
        """The [type, value] of a definition by name, '0x' checksum, or 8 hex digits, or None."""
        text = name_or_key[2:] if name_or_key.startswith("0x") else name_or_key
        if not re.fullmatch(r"[0-9a-f]{8}", text): text = f"{qb_key(name_or_key):08x}"
        return self.definitions.get(text)

    def label(self, value):
        """A checksum with its name when the name is known."""
        if not isinstance(value, str) or not value: return value or ""
        name = self.names.get(value.lower())
        return f"{name} ({value})" if name else value

    def describe(self, script):
        """'script, 1234 bytes' (or the type and size) of a definition, or 'missing'."""
        if not script: return ""
        value = self.definition(script)
        if value is None: return "missing"
        if value[0] == 7: return f"script, {len(value[1]) * 3 // 4} bytes"
        return TYPE_NAMES.get(value[0], f"type {value[0]}")

    def events(self, objects=()):
        """Rows (event, object ID, script, description) of the scripts that start: the bank startup scripts, the
        performance script, node scripts, and the scripts of stage objects and actors. objects: (kind, object ID,
        field, script) of the Blender scene, for example ('object startup', '0x57192f76', 'gh3ObjectScript', '0x774b50b0')."""
        rows = [("startup", "", script, self.describe(script)) for script in self.bank.get("startup", [])]
        if self.bank.get("performanceChanged"):
            rows.append(("performance changed", "", self.bank["performanceChanged"], self.describe(self.bank["performanceChanged"])))
        for node in self.nodes:
            for field, event in SCRIPT_FIELDS.items():
                value = node["fields"].get(key_text(field))
                if value and value[0] in (13, 26, 28) and value[1]: rows.append((event, node["id"], value[1], self.describe(value[1])))
        for kind, object_id, _, script in objects:
            if script: rows.append((kind, object_id, script, self.describe(script)))
        return rows

    def node(self, node_id):
        return next((n for n in self.nodes if n["id"] == node_id), None)

    def simple_values(self, node=None):
        """Editable rows (path, field label, type, value text) of a node, or of the top-level simple definitions."""
        rows = []
        if node is not None:
            for field, typed in node["fields"].items():
                if typed[0] in SIMPLE_TYPES:
                    rows.append((json.dumps(["node", node["array"], node["index"], field]), self.label(field), typed[0], value_text(typed),
                                 int(field, 16) not in LOCKED_FIELDS))
            return rows
        for name, typed in self.definitions.items():
            if typed[0] in SIMPLE_TYPES:
                rows.append((json.dumps(["definition", name]), self.label("0x" + name), typed[0], value_text(typed), True))
        return rows


def value_text(typed):
    kind, value = typed
    # repr: the shortest text that gives the same number again, so that a value set back is the source value.
    if kind in (5, 6): return ", ".join(repr(v) for v in value)
    if kind == 2: return repr(value)
    return str(value)


def parse_value(kind, text):
    """The JSON value of a typed QB value from text, or ValueError."""
    text = text.strip()
    if kind == 1:
        value = int(text, 0)
        if not 0 <= value <= 0xFFFFFFFF: raise ValueError("an integer is 0 to 4294967295")
        return value
    if kind == 2:
        value = float(text)
        if value != value or value in (float("inf"), float("-inf")): raise ValueError("the number must be finite")
        return value
    if kind in (3, 4): return text
    if kind in (5, 6):
        values = [float(v) for v in re.split(r"[,\s]+", text) if v]
        if len(values) != (2 if kind == 5 else 3): raise ValueError(f"give {2 if kind == 5 else 3} numbers")
        return values
    if kind in (13, 26, 28):
        if not text: raise ValueError("give a name or 0x and 8 hex digits")
        return checksum_text(text)
    raise ValueError(f"type {kind} values cannot be edited")


def set_value(bank, path, text):
    """Set a simple typed value. path: ['definition', key] or ['node', array name, index, field]. Returns the new
    [type, value]. Raises ValueError for a wrong path, a locked field, a type that is not simple, or a wrong value."""
    definitions = bank["definitions"]
    if path[0] == "definition":
        typed = definitions.get(path[1])
        if typed is None or typed[0] not in SIMPLE_TYPES: raise ValueError(f"definition {path[1]} is not a simple value")
        typed[1] = parse_value(typed[0], text)
        return typed
    if path[0] == "node":
        _, array, index, field = path
        array_key = array[2:] if array.startswith("0x") else f"{qb_key(array):08x}"
        item = definitions[array_key][1][index]
        if int(field, 16) in LOCKED_FIELDS: raise ValueError("the node name is the identity of the node; it cannot be edited")
        for entry in item[1]:
            if entry[0] == field:
                if entry[1][0] not in SIMPLE_TYPES: raise ValueError("this field is not a simple value")
                entry[1][1] = parse_value(entry[1][0], text)
                return entry[1]
        raise ValueError(f"the node has no field {field}")
    raise ValueError(f"unknown value path {path}")


def set_top_level(bank, key, value):
    """Set a top-level field of the bank that the format types: 'startup' (list of names), 'performanceChanged' (name),
    'lightVolumes', 'textures', or 'movies' (name to value). An empty value removes an optional field."""
    if key == "startup":
        names = [v.strip() for v in value.split(",") if v.strip()] if isinstance(value, str) else list(value)
        if len(names) > 256: raise ValueError("at most 256 startup scripts")
        if names or "startup" in bank: bank["startup"] = names
    elif key == "performanceChanged":
        if value: bank["performanceChanged"] = value
        else: bank.pop("performanceChanged", None)
    elif key in ("lightVolumes", "textures", "movies"):
        if not isinstance(value, dict): raise ValueError(f"{key} is a name-to-value table")
        bank[key] = dict(value)
    else:
        raise ValueError(f"{key} cannot be edited")


def problems(bank, package_files=()):
    """Reference problems that the game rejects (StageScriptBank.Load): missing startup and performance scripts,
    light volumes of missing nodes, and texture and movie files that are not in the package."""
    index = ScriptIndex(bank)
    result = []
    for script in bank.get("startup", []):
        value = index.definition(script)
        if value is None or value[0] != 7: result.append(f"startup script '{script}' is missing")
    if bank.get("performanceChanged"):
        value = index.definition(bank["performanceChanged"])
        if value is None or value[0] != 7: result.append(f"performance script '{bank['performanceChanged']}' is missing")
    nodes = {n["id"] for n in index.nodes}
    for node in bank.get("lightVolumes", {}):
        if checksum_text(node) not in nodes: result.append(f"light volume node '{node}' is not in the node arrays")
    files = set(package_files)
    for key in ("textures", "movies"):
        for name, path in bank.get(key, {}).items():
            if files and path not in files: result.append(f"{key} '{name}': the package has no file {path}")
    return result
