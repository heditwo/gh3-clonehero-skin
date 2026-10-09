"""A small JSON Schema (draft 2020-12) evaluator for the package schemas in schemas/.

It supports only the keywords that the package schemas use and rejects other keywords, so that the Blender add-on
can bundle the validator without third-party packages.
"""

import json
import math
import re
from pathlib import Path

# The Blender add-on bundle keeps a copy of the schemas next to this file (blender/build.py).
SCHEMA_DIR = next((p for p in (Path(__file__).resolve().parent / "schemas", Path(__file__).resolve().parents[2] / "schemas") if p.is_dir()), None)
ANNOTATIONS = {"$schema", "$id", "$defs", "title", "description", "examples", "default", "$comment"}
SUPPORTED = ANNOTATIONS | {
    "$ref", "type", "enum", "const", "properties", "required", "additionalProperties", "propertyNames", "minProperties",
    "maxProperties", "items", "prefixItems", "minItems", "maxItems", "uniqueItems", "pattern", "minLength", "maxLength",
    "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "allOf", "anyOf", "oneOf", "not", "if", "then", "else",
    "dependentRequired",
}


def _is_type(value, kind):
    if kind == "object": return isinstance(value, dict)
    if kind == "array": return isinstance(value, list)
    if kind == "string": return isinstance(value, str)
    if kind == "boolean": return isinstance(value, bool)
    if kind == "null": return value is None
    if isinstance(value, bool) or not isinstance(value, (int, float)): return False
    if kind == "number": return True
    if kind == "integer": return isinstance(value, int) or value.is_integer()
    raise ValueError(f"Unknown schema type: {kind}")


def _equal(a, b):
    if isinstance(a, bool) or isinstance(b, bool): return type(a) is type(b) and a == b
    if isinstance(a, (int, float)) and isinstance(b, (int, float)): return a == b
    if isinstance(a, list) and isinstance(b, list): return len(a) == len(b) and all(_equal(x, y) for x, y in zip(a, b))
    if isinstance(a, dict) and isinstance(b, dict): return a.keys() == b.keys() and all(_equal(a[k], b[k]) for k in a)
    return type(a) is type(b) and a == b


def _location(path):
    return "".join(f"[{p}]" if isinstance(p, int) else f".{p}" for p in path) or "."


class Schemas:
    """Loads schema files from one folder and resolves references between them."""

    def __init__(self, folder=SCHEMA_DIR):
        self.folder = Path(folder)
        self.documents = {}
        self.patterns = {}

    def document(self, name):
        if name not in self.documents:
            self.documents[name] = json.loads((self.folder / name).read_text())
        return self.documents[name]

    def resolve(self, ref, base):
        file, _, pointer = ref.partition("#")
        file = file or base
        node = self.document(file)
        for part in filter(None, pointer.split("/")):
            node = node[part.replace("~1", "/").replace("~0", "~")]
        return node, file

    def validate(self, value, name):
        """Return a list of error strings; empty when the value is valid against schema file `name`."""
        errors = []
        self._check(value, self.document(name), name, [], errors)
        return errors

    def validate_definition(self, value, name, definition):
        """Validate against `$defs/<definition>` of schema file `name`."""
        errors = []
        self._check(value, self.document(name)["$defs"][definition], name, [], errors)
        return errors

    def _pattern(self, pattern):
        if pattern not in self.patterns: self.patterns[pattern] = re.compile(pattern)
        return self.patterns[pattern]

    def _valid(self, value, schema, base, path):
        errors = []
        self._check(value, schema, base, path, errors)
        return not errors

    def _check(self, value, schema, base, path, errors):
        if schema is True: return
        if schema is False:
            errors.append(f"{_location(path)}: not allowed"); return
        unknown = set(schema) - SUPPORTED
        if unknown: raise ValueError(f"Unsupported schema keywords: {sorted(unknown)}")
        where = _location(path)
        if isinstance(value, float) and not math.isfinite(value):
            errors.append(f"{where}: number is not finite"); return
        if "$ref" in schema:
            target, file = self.resolve(schema["$ref"], base)
            self._check(value, target, file, path, errors)
        if "type" in schema:
            kinds = schema["type"] if isinstance(schema["type"], list) else [schema["type"]]
            if not any(_is_type(value, k) for k in kinds):
                errors.append(f"{where}: expected {' or '.join(kinds)}"); return
        if "enum" in schema and not any(_equal(value, option) for option in schema["enum"]):
            errors.append(f"{where}: value {json.dumps(value)[:80]} is not one of {json.dumps(schema['enum'])[:200]}")
        if "const" in schema and not _equal(value, schema["const"]):
            errors.append(f"{where}: expected {json.dumps(schema['const'])}")
        if isinstance(value, str):
            if "minLength" in schema and len(value) < schema["minLength"]: errors.append(f"{where}: string is too short")
            if "maxLength" in schema and len(value) > schema["maxLength"]: errors.append(f"{where}: string is too long")
            if "pattern" in schema and not self._pattern(schema["pattern"]).search(value):
                errors.append(f"{where}: {json.dumps(value)[:80]} does not match {schema['pattern']}")
        if _is_type(value, "number"):
            if "minimum" in schema and value < schema["minimum"]: errors.append(f"{where}: {value} is less than {schema['minimum']}")
            if "maximum" in schema and value > schema["maximum"]: errors.append(f"{where}: {value} is more than {schema['maximum']}")
            if "exclusiveMinimum" in schema and value <= schema["exclusiveMinimum"]: errors.append(f"{where}: {value} must be more than {schema['exclusiveMinimum']}")
            if "exclusiveMaximum" in schema and value >= schema["exclusiveMaximum"]: errors.append(f"{where}: {value} must be less than {schema['exclusiveMaximum']}")
        if isinstance(value, dict):
            self._object(value, schema, base, path, errors)
        if isinstance(value, list):
            self._array(value, schema, base, path, errors)
        for sub in schema.get("allOf", []):
            self._check(value, sub, base, path, errors)
        if "anyOf" in schema and not any(self._valid(value, sub, base, path) for sub in schema["anyOf"]):
            errors.append(f"{where}: does not match any allowed form")
        if "oneOf" in schema and sum(self._valid(value, sub, base, path) for sub in schema["oneOf"]) != 1:
            errors.append(f"{where}: must match exactly one allowed form")
        if "not" in schema and self._valid(value, schema["not"], base, path):
            errors.append(f"{where}: matches a form that is not allowed")
        if "if" in schema:
            branch = "then" if self._valid(value, schema["if"], base, path) else "else"
            if branch in schema: self._check(value, schema[branch], base, path, errors)

    def _object(self, value, schema, base, path, errors):
        where = _location(path)
        for key in schema.get("required", []):
            if key not in value: errors.append(f"{where}: missing field '{key}'")
        if "minProperties" in schema and len(value) < schema["minProperties"]: errors.append(f"{where}: too few fields")
        if "maxProperties" in schema and len(value) > schema["maxProperties"]: errors.append(f"{where}: too many fields")
        for key, needed in schema.get("dependentRequired", {}).items():
            if key in value:
                for other in needed:
                    if other not in value: errors.append(f"{where}: '{key}' requires '{other}'")
        properties = schema.get("properties", {})
        for key, item in value.items():
            if "propertyNames" in schema:
                names = []
                self._check(key, schema["propertyNames"], base, path + [key], names)
                errors.extend(f"{where}: invalid field name '{key}'" for _ in names[:1])
            if key in properties:
                self._check(item, properties[key], base, path + [key], errors)
            elif "additionalProperties" in schema:
                extra = schema["additionalProperties"]
                if extra is False: errors.append(f"{where}: unknown field '{key}'")
                else: self._check(item, extra, base, path + [key], errors)

    def _array(self, value, schema, base, path, errors):
        where = _location(path)
        if "minItems" in schema and len(value) < schema["minItems"]: errors.append(f"{where}: too few items")
        if "maxItems" in schema and len(value) > schema["maxItems"]: errors.append(f"{where}: too many items")
        prefix = schema.get("prefixItems", [])
        for index, item in enumerate(value):
            if index < len(prefix): self._check(item, prefix[index], base, path + [index], errors)
            elif "items" in schema: self._check(item, schema["items"], base, path + [index], errors)
        if schema.get("uniqueItems"):
            for i in range(len(value)):
                if any(_equal(value[i], value[j]) for j in range(i)):
                    errors.append(f"{where}: items are not unique"); break
