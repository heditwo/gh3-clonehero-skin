"""Groups of scenery objects: light groups (with the color overlay), visibility groups, and node flags.

The show and hide toggles use the viewport visibility of the view layer (the eye icon). Export does not read it.
"""

import colorsys
import zlib

from . import collect

FLAGS = (
    ("HIDDEN_AT_START", "Hidden at start", "Visibility group members with Initially visible off"),
    ("NOT_CREATED", "Not created at start", "Script objects and fixtures with Created off"),
    ("FIXTURES", "Light fixtures", "Movable light housings (gh3FixtureId)"),
    ("SCRIPT_OBJECTS", "Script objects", "Objects with a gh3Object name"),
)


def scenery(scene):
    """Stage objects that export can write into stage.glb (also hidden ones), and the actor roots."""
    return [obj for obj in scene.objects
            if (obj.chstage.marker == "NONE" and obj.type not in ("CAMERA", "LIGHT") and not collect.in_actor_or_proxy(obj))
            or obj.chstage.marker == "ACTOR"]


def light_group(obj):
    if obj.chstage.marker == "ACTOR": return obj.chstage.actor.light_group or "Stage"
    return obj.chstage.light_group or "Stage"


def light_groups(scene):
    """Light group name to its objects."""
    result = {}
    for obj in scenery(scene):
        if obj.type in ("MESH", "CURVE", "SURFACE", "META", "FONT") or obj.chstage.marker == "ACTOR": result.setdefault(light_group(obj), []).append(obj)
    return dict(sorted(result.items()))


def group_color(name):
    """A stable color for a group name (the same in each session)."""
    hue = (zlib.crc32(name.encode()) % 360) / 360
    return (*colorsys.hsv_to_rgb(hue, 0.65, 0.95), 1.0)


def overlay(scene, context=None):
    """Color the scenery by light group in Solid shading (object color), or go back to the material colors."""
    on = scene.chstage.light_group_overlay
    for name, objects in light_groups(scene).items():
        for obj in objects:
            targets = [obj] + (members(obj) if obj.chstage.marker == "ACTOR" else [])
            for target in targets:
                if on: target.color = group_color(name)
    screen = context.screen if context is not None else None
    for area in (screen.areas if screen else []):
        if area.type != "VIEW_3D": continue
        shading = area.spaces.active.shading
        if on:
            if shading.type not in ("SOLID", "WIREFRAME"): shading.type = "SOLID"
            shading.color_type = "OBJECT"
        elif shading.color_type == "OBJECT":
            shading.color_type = "MATERIAL"
        area.tag_redraw()


def members(root):
    from . import actor_tools
    return actor_tools.descendants(root)


def visibility_groups(scene):
    """Visibility group name to (objects, start state: True, False, or None when the members differ)."""
    result = {}
    for obj in scenery(scene):
        name = obj.chstage.visibility_group if obj.chstage.marker != "ACTOR" else ""
        if name: result.setdefault(name, []).append(obj)
    return {name: (objects, start_state(objects)) for name, objects in sorted(result.items())}


def start_state(objects):
    states = {obj.chstage.initially_visible for obj in objects}
    return states.pop() if len(states) == 1 else None


def flag_members(scene, flag):
    objects = scenery(scene)
    if flag == "HIDDEN_AT_START":
        return [o for o in objects if (o.chstage.marker != "ACTOR" and o.chstage.visibility_group and not o.chstage.initially_visible)
                or (o.chstage.marker == "ACTOR" and o.chstage.actor.use_initially_visible and not o.chstage.actor.initially_visible)]
    if flag == "NOT_CREATED":
        return [o for o in objects if (o.chstage.use_object_created and not o.chstage.object_created and o.chstage.script_object)
                or (o.chstage.use_fixture_created and not o.chstage.fixture_created and o.chstage.fixture_id)
                or (o.chstage.marker == "ACTOR" and o.chstage.actor.use_created and not o.chstage.actor.created)]
    if flag == "FIXTURES": return [o for o in objects if o.chstage.fixture_id]
    if flag == "SCRIPT_OBJECTS": return [o for o in objects if o.chstage.script_object]
    return []


def members_of(scene, kind, name):
    if kind == "VISIBILITY": return visibility_groups(scene).get(name, ([], None))[0]
    if kind == "LIGHT": return light_groups(scene).get(name, [])
    return flag_members(scene, name)


def with_children(objects):
    result = []
    for obj in objects:
        result.append(obj)
        if obj.chstage.marker == "ACTOR": result.extend(members(obj))
    return result


def set_hidden(view_layer, objects, hidden):
    for obj in with_children(objects):
        if obj.name in view_layer.objects: obj.hide_set(hidden, view_layer=view_layer)


def is_hidden(view_layer, objects):
    return bool(objects) and all(obj.hide_get(view_layer=view_layer) for obj in objects if obj.name in view_layer.objects)


def show_start_state(scene, view_layer):
    """Hide the objects that the game hides or does not create at the start; show the other scenery."""
    hidden = set(flag_members(scene, "HIDDEN_AT_START")) | set(flag_members(scene, "NOT_CREATED"))
    for obj in scenery(scene): set_hidden(view_layer, [obj], obj in hidden)
