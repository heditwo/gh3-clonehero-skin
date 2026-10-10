"""Screenshots of the character panels. Run Blender with a window, as blender/tests/screenshots_tools.py:

blender --window-geometry 0 0 1920 1080 --python blender/tests/screenshots_character.py -- ZIP OUTPUT_DIR CHARACTER LIBRARY

A CHARACTER with chains (the package test-chains/tester.chains.chchar of the character test) gives the chains panel only.
"""

import sys
from pathlib import Path

import bpy

args = sys.argv[sys.argv.index("--") + 1:]
archive, output, package, library = Path(args[0]), Path(args[1]), Path(args[2]), Path(args[3])
output.mkdir(parents=True, exist_ok=True)
module = "bl_ext.user_default.chstage_tools"


def window():
    return bpy.context.window_manager.windows[0]


def scene():
    return window().scene


def view3d():
    area = next(a for a in window().screen.areas if a.type == "VIEW_3D")
    return area, next(r for r in area.regions if r.type == "WINDOW")


def panels(*names):
    """Show only these character panels, open, in the Item tab (the API cannot select a sidebar tab; screenshots only)."""
    classes = [c for c in sys.modules[module + ".character_ui"].CLASSES + sys.modules[module + ".chain_tools"].CLASSES if issubclass(c, bpy.types.Panel)]
    for cls in list(bpy.types.Panel.__subclasses__()):
        if getattr(cls, "bl_space_type", "") == "VIEW_3D" and getattr(cls, "bl_region_type", "") == "UI" and getattr(cls, "is_registered", False):
            bpy.utils.unregister_class(cls)
    for name in names:
        cls = next(c for c in classes if c.__name__ == name)
        cls.bl_options = set()
        cls.bl_category = "Item"
        cls.bl_order = -10
        if "bl_parent_id" in cls.__dict__: del cls.bl_parent_id
        bpy.utils.register_class(cls)


def select(objects):
    view_layer = window().view_layer
    for obj in scene().objects: obj.select_set(False)
    for obj in objects: obj.select_set(True)
    view_layer.objects.active = objects[0] if objects else None


def frame(objects):
    area, region = view3d()
    area.spaces.active.region_3d.view_perspective = "PERSP"
    select(objects)
    with bpy.context.temp_override(window=window(), screen=window().screen, area=area, region=region):
        bpy.ops.view3d.view_selected()


def shot(name):
    with bpy.context.temp_override(window=window(), screen=window().screen, area=window().screen.areas[0]):
        bpy.ops.screen.screenshot(filepath=str(output / f"{name}.png"))
    print("CHSTAGE-SHOT", output / f"{name}.png")


def setup():
    bpy.ops.extensions.package_install_files(filepath=str(archive), repo="user_default", enable_on_install=True)
    bpy.context.preferences.addons[module].preferences.library = str(library)
    bpy.context.preferences.view.ui_scale = 0.85
    for area in window().screen.areas:
        if area.type == "VIEW_3D":
            area.spaces.active.show_region_ui = True
            area.spaces.active.shading.type = "SOLID"
            area.spaces.active.shading.color_type = "TEXTURE"
    bpy.ops.chstage.open_character(filepath=str(package))


def package_panel():
    props = scene().chstage.character
    bpy.ops.chstage.validate_character()
    panels("CHSTAGE_PT_character", "CHSTAGE_PT_character_info")
    bpy.ops.chstage.character_helpers(action="REFERENCE")
    frame([props.rig])
    select([])


def issues():
    rig = scene().chstage.character.rig
    body = next(o for o in scene().objects if o.name.startswith("TestBody"))
    body.location.x += 0.3
    group = body.vertex_groups[0]
    group.remove([0, 1, 2])
    for other in body.vertex_groups: other.remove([0, 1, 2])
    bpy.ops.chstage.validate_character()
    props = scene().chstage
    props.issues_index = next((i for i, issue in enumerate(props.issues) if issue.object_name), 0)
    bpy.ops.chstage.select_issue(index=props.issues_index)
    panels("CHSTAGE_PT_character")


def weights():
    body = next(o for o in scene().objects if o.name.startswith("TestBody"))
    body.location.x -= 0.3
    bpy.ops.chstage.character_helpers(action="REFERENCE")
    hair = next(o for o in scene().objects if o.name.startswith("TestHair"))
    select([hair])
    bpy.ops.object.mode_set(mode="WEIGHT_PAINT")
    group = next((g for g in hair.vertex_groups if g.name.startswith("bone-")), None)
    if group is not None: hair.vertex_groups.active_index = group.index
    panels("CHSTAGE_PT_character_weights")
    frame([hair])


def clips():
    bpy.ops.object.mode_set(mode="OBJECT")
    props = scene().chstage.character
    props.clips_index = next(i for i, c in enumerate(props.clips) if c.label.startswith("stance_a/solo/good/fast"))
    scene().frame_set(int(scene().frame_end * 0.4))
    bpy.ops.chstage.character_helpers(action="ATTACHMENT") if props.attachment and props.attachment.hide_get() else None
    panels("CHSTAGE_PT_character_clips", "CHSTAGE_PT_character_weights")
    area, _ = view3d()
    area.spaces.active.shading.type = "MATERIAL"
    frame([props.rig])
    select([])


def chains():
    props = scene().chstage.character
    rig = props.rig
    panels("CHSTAGE_PT_character_chains")
    joint = next(b for b in rig.data.bones if b.chstage_joint.enabled)
    rig.data.bones.active = joint
    for obj in scene().objects:
        if obj.chstage_collider.enabled: obj.hide_set(False)
    rig.show_in_front = True
    rig.data.display_type = "STICK"
    for bone in rig.data.bones: bone.hide = not (bone.chstage_joint.enabled or bone.name == joint.parent.name)
    tail = next(o for o in scene().objects if o.name.startswith("TestTwinTail"))
    frame([tail])
    area, region = view3d()
    region_3d = area.spaces.active.region_3d
    region_3d.view_rotation = __import__("mathutils").Euler((1.35, 0, 0.5)).to_quaternion()
    region_3d.view_distance *= 2.6
    select([rig])
    bpy.ops.object.mode_set(mode="POSE")


steps = [setup, lambda: None, package_panel, lambda: shot("panel-character"), issues, lambda: shot("panel-character-issues"),
         weights, lambda: shot("panel-character-weights"), clips, lambda: None, lambda: shot("panel-character-clips"), lambda: bpy.ops.wm.quit_blender()]


def tick():
    step = steps.pop(0)
    try:
        step()
    except Exception:
        import traceback
        traceback.print_exc()
    return 3.0 if steps else None


if "chains" in package.name:
    steps = [setup, lambda: None, chains, lambda: None, lambda: shot("panel-character-chains"), lambda: bpy.ops.wm.quit_blender()]
bpy.app.timers.register(tick, first_interval=2.0)
