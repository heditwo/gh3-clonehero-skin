"""Screenshots of Open .chstage and Export .chstage. Run Blender with a window, as blender/tests/screenshots.py (the dialog shot needs X11 and ImageMagick 'import'):

blender --window-geometry 0 0 1920 1080 --python blender/tests/screenshots_package.py -- ZIP OUTPUT_DIR VENUE
"""

import sys
from pathlib import Path

import bpy

args = sys.argv[sys.argv.index("--") + 1 :]
archive, output, venue = Path(args[0]), Path(args[1]), Path(args[2])
output.mkdir(parents=True, exist_ok=True)
module = "bl_ext.user_default.chstage_tools"
state = {}


def window():
    return bpy.context.window_manager.windows[0]


def areas(kind):
    return [a for a in window().screen.areas if a.type == kind]


def only_addon_panels(*names):
    """Hide the other sidebar panels and open the given add-on panels (the API cannot select a sidebar tab)."""
    panels = sys.modules[module + ".panels"]
    for cls in list(bpy.types.Panel.__subclasses__()):
        if (
            cls not in panels.CLASSES
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
        if cls.__name__ not in ("CHSTAGE_PT_lighting", "CHSTAGE_PT_crowd_cameras", "CHSTAGE_PT_manifest"):
            bpy.utils.register_class(cls)


def shot(name, target=None):
    target = target or window()
    with bpy.context.temp_override(window=target, screen=target.screen, area=target.screen.areas[0]):
        bpy.ops.screen.screenshot(filepath=str(output / f"{name}.png"))
    print("CHSTAGE-SHOT", output / f"{name}.png")


def setup():
    bpy.ops.extensions.package_install_files(filepath=str(archive), repo="user_default", enable_on_install=True)
    only_addon_panels("CHSTAGE_PT_kept_files")
    for area in areas("VIEW_3D"):
        area.spaces.active.show_region_ui = True
        area.spaces.active.shading.type = "SOLID"
        area.spaces.active.shading.color_type = "TEXTURE"
    bpy.ops.chstage.open_package(filepath=str(venue))


def camera_view():
    """Look through the scene camera (the wide shot); no object is active, so the sidebar shows the add-on tab."""
    for obj in window().scene.objects:
        obj.select_set(False)
    window().view_layer.objects.active = None
    for area in areas("VIEW_3D"):
        area.spaces.active.region_3d.view_perspective = "CAMERA"


def export():
    scene = window().scene
    bpy.ops.chstage.export_package(filepath=str(output / "export" / venue.name), write_folder=True)
    scene.chstage.issues_index = 0


def select_material_object():
    scene = window().scene
    obj = next(
        o for o in scene.objects if o.type == "MESH" and o.active_material and o.active_material.chstage.lightmap
    )
    for other in scene.objects:
        other.select_set(False)
    obj.select_set(True)
    window().view_layer.objects.active = obj
    for cls in list(bpy.types.Panel.__subclasses__()):
        if (
            getattr(cls, "bl_context", "") == "material"
            and cls.__name__ != "CHSTAGE_PT_material"
            and "context_material" not in cls.__name__
            and getattr(cls, "is_registered", False)
        ):
            bpy.utils.unregister_class(cls)


def material_tab():
    for area in areas("PROPERTIES"):
        area.spaces.active.context = "MATERIAL"


def invoke_export():
    # The nested X server window can be narrower than the Blender window; a smaller interface keeps the dialog inside it.
    bpy.context.preferences.view.ui_scale = 0.8
    bpy.ops.chstage.export_package("INVOKE_DEFAULT")


def shot_file_browser():
    """The file browser is a temporary window, which screen.screenshot cannot take: take the X display (ImageMagick)."""
    import subprocess

    subprocess.run(["import", "-window", "root", str(output / "export-dialog.png")], check=False)
    print("CHSTAGE-SHOT", output / "export-dialog.png")


steps = [
    setup,
    camera_view,
    lambda: shot("open-gh3-venue"),
    export,
    lambda: shot("export-result"),
    select_material_object,
    material_tab,
    lambda: shot("material-lightmap-kept"),
    invoke_export,
    lambda: None,
    shot_file_browser,
    lambda: bpy.ops.wm.quit_blender(),
]


def tick():
    step = steps.pop(0)
    step()
    return 1.5 if steps else None


bpy.app.timers.register(tick, first_interval=2.0)
