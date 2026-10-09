"""Screenshots of the venue tool panels (actors, script bank, camera shots with proxy rigs, lighting preview, groups).
Run Blender with a window, as blender/tests/screenshots.py:

blender --window-geometry 0 0 1920 1080 --python blender/tests/screenshots_tools.py -- ZIP OUTPUT_DIR VENUE
"""

import sys
from pathlib import Path

import bpy

args = sys.argv[sys.argv.index("--") + 1:]
archive, output, venue = Path(args[0]), Path(args[1]), Path(args[2])
output.mkdir(parents=True, exist_ok=True)
module = "bl_ext.user_default.chstage_tools"


def window():
    return bpy.context.window_manager.windows[0]


def areas(kind):
    return [a for a in window().screen.areas if a.type == kind]


def view3d():
    area = areas("VIEW_3D")[0]
    return area, next(r for r in area.regions if r.type == "WINDOW")


def panels(*names):
    """Show only these add-on panels in the sidebar, open. The API cannot select a sidebar tab, and Blender 4.2 keeps the
    Item tab, so the panels go into the Item tab, above the object transform panel (screenshots only)."""
    classes = sys.modules[module + ".panels"].CLASSES
    for cls in list(bpy.types.Panel.__subclasses__()):
        if getattr(cls, "bl_space_type", "") == "VIEW_3D" and getattr(cls, "bl_region_type", "") == "UI" and getattr(cls, "is_registered", False):
            bpy.utils.unregister_class(cls)
    for cls in classes:
        if not issubclass(cls, bpy.types.Panel) and not getattr(cls, "is_registered", False): bpy.utils.register_class(cls)
    for name in names:
        cls = next(c for c in classes if c.__name__ == name)
        cls.bl_options = set()
        cls.bl_category = "Item"
        cls.bl_order = -10
        if "bl_parent_id" in cls.__dict__: del cls.bl_parent_id
        bpy.utils.register_class(cls)


def scene():
    return window().scene


def select(objects):
    view_layer = window().view_layer
    for obj in scene().objects: obj.select_set(False)
    for obj in objects: obj.select_set(True)
    view_layer.objects.active = objects[0] if objects else None


def frame(objects=None, perspective="PERSP"):
    area, region = view3d()
    space = area.spaces.active
    space.region_3d.view_perspective = perspective
    with bpy.context.temp_override(window=window(), screen=window().screen, area=area, region=region):
        if objects: bpy.ops.view3d.view_selected()
        else: bpy.ops.view3d.view_all()


def shot(name):
    with bpy.context.temp_override(window=window(), screen=window().screen, area=window().screen.areas[0]):
        bpy.ops.screen.screenshot(filepath=str(output / f"{name}.png"))
    print("CHSTAGE-SHOT", output / f"{name}.png")


def setup():
    bpy.ops.extensions.package_install_files(filepath=str(archive), repo="user_default", enable_on_install=True)
    bpy.context.preferences.view.ui_scale = 0.85
    for area in areas("VIEW_3D"):
        area.spaces.active.show_region_ui = True
        area.spaces.active.shading.type = "SOLID"
        area.spaces.active.shading.color_type = "TEXTURE"
    bpy.ops.chstage.open_package(filepath=str(venue))
    # With no active object, the first draw of the sidebar selects the CH Stage tab, and the sidebar keeps it.
    select([])
    panels("CHSTAGE_PT_stage")


def actors():
    actor_tools = sys.modules[module + ".actor_tools"]
    roots = actor_tools.actor_roots(scene())
    root = max(roots, key=lambda r: len(actor_tools.descendants(r)[0].data.bones) if actor_tools.descendants(r) and actor_tools.descendants(r)[0].type == "ARMATURE" else 0)
    scene().frame_set(40)
    select([root])
    panels("CHSTAGE_PT_actor", "CHSTAGE_PT_actors")
    parts = [o for o in actor_tools.descendants(root) if o.type == "MESH"]
    select(parts or [root])
    frame(parts)
    select([root])


def scripts():
    props = scene().chstage
    props.script_view = "OBJECTS"
    index = next((i for i, r in enumerate(props.script_rows) if r.objects and r.info), 0)
    props.script_rows_index = index
    panels("CHSTAGE_PT_scripts")
    bpy.ops.chstage.script_jump(index=index)


def events():
    props = scene().chstage
    props.script_view = "EVENTS"
    props.script_rows_index = next((i for i, r in enumerate(props.script_rows) if r.label == "object startup" and r.objects), 0)
    panels("CHSTAGE_PT_scripts")


def cameras():
    props = scene().chstage
    shot_object = next(o for o in scene().objects if o.type == "CAMERA" and o.chstage.shot.enabled and o.chstage.point.anchor_part == "bone")
    props.shot_group = shot_object.chstage.shot.groups[0].name if shot_object.chstage.shot.groups else ""
    panels("CHSTAGE_PT_shots", "CHSTAGE_PT_shot")
    select([shot_object])
    props.shots_index = scene().objects.find(shot_object.name)


def lighting():
    props = scene().chstage
    wide = next(o for o in scene().objects if o.type == "CAMERA" and o.chstage.shot.enabled and any(g.name == "wide" for g in o.chstage.shot.groups))
    select([])
    props.shots_index = scene().objects.find(wide.name)
    props.lighting_preview = True
    moods = [m.mood for m in props.moods]
    props.preview_mood = "chorus" if "chorus" in moods else moods[0]
    props.beam_overlay = True
    panels("CHSTAGE_PT_lighting")
    area, _ = view3d()
    area.spaces.active.shading.type = "MATERIAL"
    area.spaces.active.shading.use_scene_lights = True
    area.spaces.active.shading.use_scene_world = True


def groups():
    props = scene().chstage
    props.lighting_preview = False
    area, _ = view3d()
    area.spaces.active.shading.type = "SOLID"
    with bpy.context.temp_override(window=window(), screen=window().screen, area=area):
        props.light_group_overlay = True
    area.spaces.active.shading.color_type = "OBJECT"
    panels("CHSTAGE_PT_groups")


steps = [setup, lambda: None, actors, lambda: shot("panel-actor"), scripts, lambda: shot("panel-script-objects"), events, lambda: shot("panel-script-events"),
         cameras, lambda: shot("panel-shots-rig"), lighting, lambda: None, lambda: None, lambda: None, lambda: shot("panel-lighting-preview"),
         groups, lambda: shot("panel-groups-overlay"), lambda: bpy.ops.wm.quit_blender()]


def tick():
    step = steps.pop(0)
    try:
        step()
    except Exception:
        import traceback
        traceback.print_exc()
    return 3.0 if steps else None


bpy.app.timers.register(tick, first_interval=2.0)
