"""Take screenshots of the add-on panels. Run Blender with a window (not --background) and a user folder whose
preferences turn off the splash screen:

blender --window-geometry 0 0 1920 1080 --python blender/tests/screenshots.py -- ZIP OUTPUT_DIR
"""

import sys
from pathlib import Path

import bpy

args = sys.argv[sys.argv.index("--") + 1 :]
archive, output = Path(args[0]), Path(args[1])
output.mkdir(parents=True, exist_ok=True)
module = "bl_ext.user_default.chstage_tools"


def areas(kind):
    window = bpy.context.window_manager.windows[0]
    return window, [a for a in window.screen.areas if a.type == kind]


def open_panels(*names):
    """Show panels that start closed, and only the add-on tab in the sidebar (screenshots only).

    The API cannot select a sidebar tab (Region.active_panel_category is read-only), so the other sidebar panels are hidden.
    """
    panels = sys.modules[module + ".panels"]
    ours = set(panels.CLASSES)
    for cls in list(bpy.types.Panel.__subclasses__()):
        if (
            cls not in ours
            and getattr(cls, "bl_space_type", "") == "VIEW_3D"
            and getattr(cls, "bl_region_type", "") == "UI"
            and getattr(cls, "is_registered", False)
        ):
            bpy.utils.unregister_class(cls)
    for cls in reversed(panels.CLASSES):
        bpy.utils.unregister_class(cls)
    for cls in panels.CLASSES:
        if cls.__name__ in names:
            cls.bl_options = set()
        bpy.utils.register_class(cls)


def select(name):
    scene = bpy.context.window_manager.windows[0].scene
    for obj in scene.objects:
        obj.select_set(False)
    obj = scene.objects[name]
    obj.select_set(True)
    bpy.context.window_manager.windows[0].view_layer.objects.active = obj


def shot(name):
    window, _ = areas("VIEW_3D")
    with bpy.context.temp_override(window=window, screen=window.screen, area=window.screen.areas[0]):
        bpy.ops.screen.screenshot(filepath=str(output / f"{name}.png"))
    print("CHSTAGE-SHOT", output / f"{name}.png")


def setup():
    bpy.ops.extensions.package_install_files(filepath=str(archive), repo="user_default", enable_on_install=True)
    open_panels("CHSTAGE_PT_manifest", "CHSTAGE_PT_lighting", "CHSTAGE_PT_crowd_cameras")
    window, views = areas("VIEW_3D")
    with bpy.context.temp_override(window=window, screen=window.screen):
        bpy.ops.chstage.new_stage()
    window.scene = bpy.data.scenes["CH Stage"]
    for area in views:
        space = area.spaces.active
        space.show_region_ui = True
        space.shading.type = "SOLID"
        space.shading.color_type = "MATERIAL"
        region = next(r for r in area.regions if r.type == "WINDOW")
        with bpy.context.temp_override(window=window, screen=window.screen, area=area, region=region):
            bpy.ops.view3d.view_all()
    scene = window.scene
    scene.objects["right"].chstage.fixture_id = "missing-beam"
    with bpy.context.temp_override(window=window, screen=window.screen, area=views[0], scene=scene):
        bpy.ops.chstage.validate()


def hide_manifest():
    """A drawn panel keeps its open state, so the next screenshots hide the package information panel."""
    bpy.utils.unregister_class(sys.modules[module + ".panels"].CHSTAGE_PT_manifest)


def material_tab():
    """Show the add-on material panel near the top of the material tab (screenshots only)."""
    for cls in list(bpy.types.Panel.__subclasses__()):
        if (
            getattr(cls, "bl_context", "") == "material"
            and cls.__name__ != "CHSTAGE_PT_material"
            and "context_material" not in cls.__name__
            and getattr(cls, "is_registered", False)
        ):
            bpy.utils.unregister_class(cls)
    for area in areas("PROPERTIES")[1]:
        area.spaces.active.context = "MATERIAL"


steps = [
    setup,
    lambda: (select("guitar"), None)[1],
    lambda: shot("panel-package"),
    hide_manifest,
    lambda: shot("panel-camera-shot"),
    lambda: (select("right"), None)[1],
    material_tab,
    lambda: shot("panel-object-material"),
    lambda: (select("Stage"), None)[1],
    lambda: shot("panel-light-lighting"),
    lambda: (select("band-guitarist"), None)[1],
    lambda: shot("panel-band-marker"),
    lambda: bpy.ops.wm.quit_blender(),
]


def tick():
    step = steps.pop(0)
    step()
    return 1.0 if steps else None


bpy.app.timers.register(tick, first_interval=2.0)
