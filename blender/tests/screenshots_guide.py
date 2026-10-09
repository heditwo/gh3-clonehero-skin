"""Screenshots for docs/CREATOR-GUIDE.md (docs/images/creator/). Run Blender with a window, as blender/tests/screenshots.py:

blender --window-geometry 0 0 1600 900 --python blender/tests/screenshots_guide.py -- ZIP OUTPUT_DIR LIBRARY VENUE

The stage and character shots use the example .blend files in examples/. """

import sys
from pathlib import Path

import bpy

args = sys.argv[sys.argv.index("--") + 1:]
archive, output, library, venue = Path(args[0]), Path(args[1]), Path(args[2]), Path(args[3])
output.mkdir(parents=True, exist_ok=True)
ROOT = Path(__file__).resolve().parents[2]
module = "bl_ext.user_default.chstage_tools"
state = {}


def window():
    return bpy.context.window_manager.windows[0]


def scene():
    return window().scene


def area(kind):
    return next(a for a in window().screen.areas if a.type == kind)


def our_panels():
    return [c for c in (*sys.modules[module + ".panels"].CLASSES, *sys.modules[module + ".character_ui"].CLASSES) if issubclass(c, bpy.types.Panel)]


def panels(*names, opened=()):
    """Show only these sidebar panels of the add-on (in their own tab), the `opened` ones open. The API cannot select a
    sidebar tab, so the other sidebar panels are removed (screenshots only)."""
    for cls in list(bpy.types.Panel.__subclasses__()):
        if getattr(cls, "bl_space_type", "") == "VIEW_3D" and getattr(cls, "bl_region_type", "") == "UI" and getattr(cls, "is_registered", False):
            bpy.utils.unregister_class(cls)
    for cls in our_panels():
        if cls.bl_space_type != "VIEW_3D" or cls.__name__ not in names: continue
        if cls.__name__ in opened: cls.bl_options = set()
        bpy.utils.register_class(cls)


def material_panels():
    for cls in list(bpy.types.Panel.__subclasses__()):
        if (getattr(cls, "bl_context", "") == "material" and cls.__name__ != "CHSTAGE_PT_material" and "context_material" not in cls.__name__
                and getattr(cls, "is_registered", False)):
            bpy.utils.unregister_class(cls)


def select(objects):
    view_layer = window().view_layer
    for obj in scene().objects: obj.select_set(False)
    for obj in objects: obj.select_set(True)
    view_layer.objects.active = objects[0] if objects else None


def view(perspective="CAMERA", shading="SOLID", objects=None):
    space = area("VIEW_3D").spaces.active
    space.show_region_ui = True
    space.shading.type = shading
    space.shading.color_type = "TEXTURE"
    space.region_3d.view_perspective = perspective
    if objects:
        region = next(r for r in area("VIEW_3D").regions if r.type == "WINDOW")
        select(objects)
        with bpy.context.temp_override(window=window(), screen=window().screen, area=area("VIEW_3D"), region=region):
            bpy.ops.view3d.view_selected()


def shot(name):
    with bpy.context.temp_override(window=window(), screen=window().screen, area=window().screen.areas[0]):
        bpy.ops.screen.screenshot(filepath=str(output / f"{name}.png"))
    print("CHSTAGE-SHOT", output / f"{name}.png")


def append_scene(blend):
    with bpy.data.libraries.load(str(blend), link=False) as (source, target):
        target.scenes = list(source.scenes)
    return target.scenes[0]


def setup():
    bpy.ops.extensions.package_install_files(filepath=str(archive), repo="user_default", enable_on_install=True)
    prefs = bpy.context.preferences.addons[module].preferences
    # Example paths for the preference shot; the real library path follows.
    prefs.custom_stages = "/home/player/.clonehero/Custom/Stages/"
    prefs.custom_characters = "/home/player/.clonehero/Custom/Characters/"
    prefs.library = "/home/player/Clone Hero/Mods/GH3Skin/Packages/gh3-band-v1.chlib"
    view_3d = area("VIEW_3D")
    state["view"] = view_3d
    view_3d.ui_type = "PREFERENCES"
    bpy.context.preferences.active_section = "ADDONS"
    bpy.context.window_manager.addon_search = "CH Stage"
    try:
        bpy.ops.preferences.addon_expand(module=module)
    except RuntimeError as error:
        print("addon_expand", error)


def stage():
    state["view"].ui_type = "VIEW_3D"
    bpy.context.preferences.addons[module].preferences.library = str(library)
    club = append_scene(ROOT / "examples" / "example-club.blend")
    window().scene = club
    club.camera = club.objects["wide"]
    select([])
    panels("CHSTAGE_PT_stage", "CHSTAGE_PT_manifest", opened=("CHSTAGE_PT_manifest",))
    view()
    with bpy.context.temp_override(window=window(), screen=window().screen, area=area("VIEW_3D"), scene=club):
        bpy.ops.chstage.validate()


def band_marker():
    panels("CHSTAGE_PT_stage", "CHSTAGE_PT_object")
    select([scene().objects["band-guitarist"]])


def material():
    panels("CHSTAGE_PT_stage", "CHSTAGE_PT_object")
    material_panels()
    select([scene().objects["truss-top"]])


def material_tab():
    area("PROPERTIES").spaces.active.context = "MATERIAL"


def camera():
    panels("CHSTAGE_PT_shot", "CHSTAGE_PT_shots", opened=("CHSTAGE_PT_shots",))
    guitar = scene().objects["guitar"]
    scene().camera = guitar
    select([guitar])
    area("PROPERTIES").spaces.active.context = "OBJECT"


def lighting():
    props = scene().chstage
    scene().camera = scene().objects["wide"]
    panels("CHSTAGE_PT_light", "CHSTAGE_PT_lighting", opened=("CHSTAGE_PT_lighting",))
    select([scene().objects["Stage"]])
    props.snapshots_index = 1
    props.moods_index = 2
    props.lighting_preview = True
    props.preview_mood = "chorus"
    view(shading="MATERIAL")


def venue_open():
    lighting_scene = scene()
    lighting_scene.chstage.lighting_preview = False
    bpy.ops.chstage.open_package(filepath=str(venue))
    select([])
    panels("CHSTAGE_PT_stage", "CHSTAGE_PT_scripts", opened=("CHSTAGE_PT_scripts",))
    view()


def character():
    guitarist = append_scene(ROOT / "examples" / "example-guitarist.blend")
    window().scene = guitarist
    props = guitarist.chstage.character
    props.library = str(library)
    with bpy.context.temp_override(window=window(), screen=window().screen, area=area("VIEW_3D"), scene=guitarist):
        bpy.ops.chstage.character_helpers(action="RELOAD")
    panels("CHSTAGE_PT_character", "CHSTAGE_PT_character_weights")
    body = guitarist.objects["body"]
    view(perspective="PERSP", objects=[body])
    select([body])
    bpy.ops.object.mode_set(mode="WEIGHT_PAINT")
    group = next(g for g in body.vertex_groups if g.name.startswith("bone-"))
    names = [g.name for g in body.vertex_groups]
    # A bone of the upper arm shows the weight transfer well.
    character_format = sys.modules[module + ".vendor.package_format.character"]
    wanted = f"bone-{character_format.gh3_key('bone_bicep_l'):08x}"
    body.vertex_groups.active_index = names.index(wanted) if wanted in names else group.index


def character_package():
    bpy.ops.object.mode_set(mode="OBJECT")
    with bpy.context.temp_override(window=window(), screen=window().screen, area=area("VIEW_3D"), scene=scene()):
        bpy.ops.chstage.validate_character()
    panels("CHSTAGE_PT_character", "CHSTAGE_PT_character_info", opened=("CHSTAGE_PT_character_info",))
    select([])
    view(perspective="PERSP")


steps = [setup, lambda: None, lambda: shot("addon-preferences"),
         stage, lambda: None, lambda: shot("stage-package"),
         band_marker, lambda: shot("stage-band-marker"),
         material, material_tab, lambda: shot("stage-material"),
         camera, lambda: shot("stage-camera"),
         lighting, lambda: None, lambda: None, lambda: None, lambda: shot("stage-lighting"),
         character, lambda: None, lambda: shot("character-weights"),
         character_package, lambda: None, lambda: shot("character-package"),
         venue_open, lambda: None, lambda: shot("venue-open"),
         lambda: bpy.ops.wm.quit_blender()]


def tick():
    step = steps.pop(0)
    try:
        step()
    except Exception:
        import traceback
        traceback.print_exc()
    return 2.0 if steps else None


bpy.app.timers.register(tick, first_interval=2.0)
