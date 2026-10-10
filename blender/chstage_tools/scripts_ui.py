"""The script bank browser: rows of scene.chstage.script_rows from the scripts.json text block, and value edits."""

import json

from . import script_bank
from .vendor.package_format import container

_CACHE = {}


def script_item(scene):
    """The kept file of the script bank (entry 'scripts'), or None."""
    return next((item for item in scene.chstage.kept_files if item.entry == "scripts" and item.text is not None), None)


def _cached(scene):
    item = script_item(scene)
    if item is None:
        return None
    text = item.text.as_string()
    key = item.text.as_pointer()
    cached = _CACHE.get(key)
    if cached is None or cached["text"] != text:
        try:
            data = json.loads(text)
        except ValueError:
            return None
        cached = _CACHE[key] = {"text": text, "data": data, "index": None}
    return cached


def bank(scene):
    """The parsed script bank of the scene (cached for its text), or None."""
    cached = _cached(scene)
    return cached["data"] if cached else None


def index_of(scene):
    cached = _cached(scene)
    if cached is None:
        return None
    if cached["index"] is None:
        cached["index"] = script_bank.ScriptIndex(cached["data"], names=[obj.name for obj in scene.objects])
    return cached["index"]


def object_links(scene):
    """Script object ID to the Blender objects that have it (scenery, fixtures, lights, actors), and the event rows of
    the scene objects (object startup scripts, actor scripts, actor animation events)."""
    links, events = {}, []
    for obj in scene.objects:
        props = obj.chstage
        ids = []
        if props.script_object:
            ids.append(props.script_object)
            if props.object_script:
                events.append(("object startup", props.script_object, "gh3ObjectScript", props.object_script))
        if props.fixture_object:
            ids.append(props.fixture_object)
        if obj.type == "LIGHT" and props.light.script_object:
            ids.append(props.light.script_object)
        if props.marker == "ACTOR":
            actor = props.actor
            if actor.script_object:
                ids.append(actor.script_object)
            if actor.script:
                events.append(("actor script", actor.script_object, "script", actor.script))
            if actor.animation_events:
                events.append(
                    ("actor animation events", actor.script_object, "animationEvents", actor.animation_events)
                )
        for value in ids:
            links.setdefault(value.lower(), []).append(obj.name)
    return links, sorted(set(events))


def rebuild(scene):
    """Fill scene.chstage.script_rows for the selected view."""
    props = scene.chstage
    rows = props.script_rows
    rows.clear()
    data = bank(scene)
    if data is None:
        return
    links, scene_events = object_links(scene)
    index = index_of(scene)
    if props.script_view == "OBJECTS":
        for node in index.nodes:
            row = rows.add()
            row.kind, row.node_id = "OBJECT", node["id"]
            row.label = index.label(node["id"])
            row.info = " ".join(v for v in (node["class"], node["type"]) if v)
            row.objects = ", ".join(links.get(node["id"].lower(), []))
    elif props.script_view == "EVENTS":
        for event, object_id, script, description in index.events(scene_events):
            row = rows.add()
            row.kind, row.node_id, row.script = "EVENT", object_id, script
            row.label = event
            row.info = f"{index.label(script)}: {description}" if description else index.label(script)
            row.objects = ", ".join(links.get(object_id.lower(), [])) if object_id else ""
    else:
        for key in ("startup", "performanceChanged"):
            row = rows.add()
            row.kind, row.label, row.path, row.editable = "SETTING", key, json.dumps(["setting", key]), True
            value = data.get(key, [] if key == "startup" else "")
            row.info = ", ".join(value) if isinstance(value, list) else value
        for path, label, kind, value, editable in index.simple_values():
            row = rows.add()
            row.kind, row.label, row.path, row.value_type, row.info, row.editable = (
                "VALUE",
                label,
                path,
                kind,
                value,
                editable,
            )
    props.script_rows_index = min(props.script_rows_index, max(0, len(rows) - 1))


def write(scene, data):
    """Write the bank into the text block (the package format JSON layout) and rebuild the rows."""
    item = script_item(scene)
    item.text.from_string(container.json_bytes(data).decode("utf-8"))
    rebuild(scene)


def set_value(scene, path, text):
    """Set a value of the bank (a value row path, or a field of an object). Returns the new value text."""
    data = json.loads(json.dumps(bank(scene)))
    path = json.loads(path)
    if path[0] == "setting":
        script_bank.set_top_level(data, path[1], text)
        result = text
    else:
        result = script_bank.value_text(script_bank.set_value(data, path, text))
    write(scene, data)
    return result


def select_objects(context, scene, names):
    view_layer = context.view_layer if context.view_layer in scene.view_layers.values() else scene.view_layers[0]
    objects = [scene.objects[n] for n in names if n in scene.objects]
    for other in scene.objects:
        if other.select_get(view_layer=view_layer):
            other.select_set(False, view_layer=view_layer)
    for obj in objects:
        if obj.hide_get(view_layer=view_layer):
            obj.hide_set(False, view_layer=view_layer)
        obj.select_set(True, view_layer=view_layer)
    if objects:
        view_layer.objects.active = objects[0]
    return objects


def node_rows(scene, node_id):
    """Value rows (path, label, type, value, editable) of a node of the bank."""
    index = index_of(scene)
    if index is None:
        return []
    node = index.node(node_id)
    return index.simple_values(node) if node is not None else []
