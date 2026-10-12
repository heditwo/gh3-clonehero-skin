"""The Tda Miku tutorial of docs/CREATOR-GUIDE.md section 5, step by step with the operators of the user interface, and the
screenshots of docs/images/creator/miku-*.png. The model is not in the repository: download it as the guide says.

With a window (screenshots), as blender/tests/screenshots_guide.py:
blender --window-geometry 0 0 1600 900 --python blender/tests/screenshots_miku.py -- ZIP OUTPUT_DIR LIBRARY MODEL_DIR MMD_TOOLS_ZIP

With --background, the script does the same steps without screenshots (a check of the tutorial). MODEL_DIR is the folder
of the unpacked download with the logo-free body00_MikuAp.tga copied over the original. Output: OUTPUT_DIR/*.png, the
package OUTPUT_DIR/yourname.tda-miku.chchar, the textures that the tutorial saves as PNG, and TUTORIAL lines in the log.
"""

import sys
import traceback
from pathlib import Path

import bpy

args = sys.argv[sys.argv.index("--") + 1 :]
archive, output, library, model_dir, mmd_tools = (Path(a) for a in args[:5])
output.mkdir(parents=True, exist_ok=True)
module = "bl_ext.user_default.chstage_tools"
PROFILE = "gh3-guitarist-midori-v1"
WINDOWED = not bpy.app.background
state = {}

# Material settings of the tutorial table (CH Stage Material): transparency and two-sided.
TRANSPARENCY = {
    "face01": "CUTOUT",
    "eye_hi": "CUTOUT",
    "eye_hi2": "CUTOUT",
    "cheek": "BLEND",
    "hairshadow": "BLEND",
    "body02": "BLEND",
    "body_green2": "BLEND",
    "hair00": "BLEND",
    "hair01": "BLEND",
    "lens": "BLEND",
    "wing": "BLEND",
}
HAIR = ("hair00", "hair01")
# The chains to remove (the first joint), the presets, and the colliders of each chain (by the first joint).
STIFF = ("胸", "アホ毛", "もみあげ", "前髪")
PRESETS = {"髪": "long_hair", "しっぽ": "tail", "腰羽": "skirt", "ﾈｸﾀｲ": "hair"}
COLLIDERS = {
    "髪": "head, chest, stomach, hips, thigh_l, thigh_r",
    "しっぽ": "floor, hips, thigh_l, thigh_r, shin_l, shin_r",
    "腰羽": "thigh_l, thigh_r, shin_l, shin_r",
    "ﾈｸﾀｲ": "chest, stomach",
}


def log(*items):
    print("TUTORIAL", *items, flush=True)


def tools(name):
    return sys.modules[f"{module}.{name}"]


def window():
    windows = bpy.context.window_manager.windows
    return windows[0] if windows else None


def scene():
    return window().scene if WINDOWED else bpy.context.scene


def view_layer():
    return window().view_layer if WINDOWED else bpy.context.view_layer


def area(kind):
    return next((a for a in window().screen.areas if a.type == kind), None) if WINDOWED else None


def window_override():
    return {"window": window(), "screen": window().screen} if WINDOWED else {}


def call(label, operator, **kwargs):
    """An operator as the button calls it, in the 3D view of the window."""
    override = window_override() or {"scene": scene(), "view_layer": view_layer()}
    view = area("VIEW_3D")
    if view is not None:
        override |= {"area": view, "region": next(r for r in view.regions if r.type == "WINDOW")}
    try:
        with bpy.context.temp_override(**override):
            result = operator(**kwargs)
    except Exception as error:
        traceback.print_exc()
        result = f"EXCEPTION {error}"
    log(label, result)
    return result


def select(objects, active=None):
    for obj in scene().objects:
        obj.select_set(False)
    for obj in objects:
        obj.select_set(True)
    view_layer().objects.active = active or (objects[0] if objects else None)


def panels(*names, opened=()):
    """Show only these sidebar panels of the add-on (in their own tab). The API cannot select a sidebar tab, so the other
    sidebar panels are removed (screenshots only)."""
    if not WINDOWED:
        return
    ours = [
        c
        for c in (
            *tools("panels").CLASSES,
            *tools("character_ui").CLASSES,
            *tools("skeleton_tools").CLASSES,
            *tools("chain_tools").CLASSES,
        )
        if issubclass(c, bpy.types.Panel)
    ]
    for cls in list(bpy.types.Panel.__subclasses__()):
        if (
            getattr(cls, "bl_space_type", "") == "VIEW_3D"
            and getattr(cls, "bl_region_type", "") == "UI"
            and getattr(cls, "is_registered", False)
        ):
            bpy.utils.unregister_class(cls)
    for cls in ours:
        if cls.bl_space_type != "VIEW_3D" or cls.__name__ not in names:
            continue
        if cls.__name__ in opened:
            cls.bl_options = set()
        bpy.utils.register_class(cls)


def material_panels():
    if not WINDOWED:
        return
    for cls in list(bpy.types.Panel.__subclasses__()):
        if (
            getattr(cls, "bl_context", "") == "material"
            and cls.__name__ != "CHSTAGE_PT_material"
            and "context_material" not in cls.__name__
            and getattr(cls, "is_registered", False)
        ):
            bpy.utils.unregister_class(cls)


def view(location, rotation, distance, shading="SOLID", perspective="PERSP"):
    if not WINDOWED:
        return
    space = area("VIEW_3D").spaces.active
    space.show_region_ui = True
    space.shading.type = shading
    space.shading.color_type = "TEXTURE"
    space.overlay.show_relationship_lines = False
    r3d = space.region_3d
    r3d.view_perspective = perspective
    r3d.view_location = location
    r3d.view_rotation = rotation
    r3d.view_distance = distance


def turn(z_degrees, x_degrees):
    """A view rotation: look along -Y (the front of the model) turned about Z, then tilted."""
    from math import radians

    from mathutils import Euler

    return Euler((radians(x_degrees), 0, radians(z_degrees)), "XYZ").to_quaternion()


def scroll_sidebar(steps):
    if not WINDOWED:
        return
    view3d = area("VIEW_3D")
    region = next(r for r in view3d.regions if r.type == "UI")
    with bpy.context.temp_override(window=window(), screen=window().screen, area=view3d, region=region):
        for _ in range(steps):
            bpy.ops.view2d.scroll_down(deltax=0, deltay=40)


def shot(name):
    if not WINDOWED:
        return
    with bpy.context.temp_override(window=window(), screen=window().screen, area=window().screen.areas[0]):
        bpy.ops.screen.screenshot(filepath=str(output / f"{name}.png"))
    print("CHSTAGE-SHOT", output / f"{name}.png")


def props():
    return scene().chstage.character


# The tutorial steps.
def install():
    for path in (archive, mmd_tools):
        bpy.ops.extensions.package_install_files(filepath=str(path), repo="user_default", enable_on_install=True)
    prefs = bpy.context.preferences.addons[module].preferences
    prefs.library = str(library)
    log("installed", sorted(a.module for a in bpy.context.preferences.addons if a.module.startswith("bl_ext")))


def new_character():
    call("new character", bpy.ops.chstage.new_character, profile=PROFILE, own_skeleton=True)
    log("scene", scene().name, props().library_profile, [o.name for o in scene().objects])


def import_model():
    before = set(bpy.data.objects)
    pmx = next(model_dir.glob("*.pmx"))
    call("import pmx", bpy.ops.mmd_tools.import_model, filepath=str(pmx), scale=0.08)
    made = [o for o in bpy.data.objects if o not in before]
    state["armature"] = next(o for o in made if o.type == "ARMATURE")
    state["mesh"] = max((o for o in made if o.type == "MESH" and o.data.vertices), key=lambda o: len(o.data.vertices))
    mesh = state["mesh"]
    log(
        "model",
        state["armature"].name,
        mesh.name,
        len(mesh.data.vertices),
        "vertices",
        len(mesh.material_slots),
        "materials",
        len(mesh.data.shape_keys.key_blocks),
        "shape keys",
    )
    anklet = next(i for i in bpy.data.images if i.name.startswith("body00_MikuAp"))
    log("anklet texture", bpy.path.abspath(anklet.filepath))
    select([state["armature"]])
    panels("CHSTAGE_PT_character", "CHSTAGE_PT_character_skeleton")
    view((0.35, 0, 0.85), turn(25, 80), 4.2)


def mmd_root(obj):
    """The MMD model root that the MMD Tools panel Model Setup finds from the active object (its parents)."""
    while obj is not None and getattr(obj, "mmd_type", "") != "ROOT":
        obj = obj.parent
    return obj


def convert_materials():
    """While MMD Tools still finds the model (before Set Up Skeleton): Toon from MMD, then Convert to Blender."""
    mesh = state["mesh"]
    select([mesh])
    log("MMD Tools panel finds the model root", mmd_root(mesh) is not None)
    call("toon from mmd (selected meshes)", bpy.ops.chstage.toon_from_mmd, scope="SELECTED")
    call("mmd tools convert to blender", bpy.ops.mmd_tools.convert_materials)
    log("materials", [(m.material.name, m.material.chstage.shading) for m in mesh.material_slots][:4], "...")


def set_up_skeleton():
    select([state["armature"]])
    call("set up skeleton", bpy.ops.chstage.use_skeleton)
    p = props()
    roles = {r.role: (r.bone, round(r.confidence, 2)) for r in p.roles}
    log("notes", tools("skeleton_tools").notes(scene()))
    log(
        "roles",
        len([r for r in roles.values() if r[0]]),
        "mapped; not mapped:",
        [k for k, v in roles.items() if not v[0]],
        "to check:",
        [k for k, v in roles.items() if v[0] and v[1] < 0.8],
    )
    log("face", tools("skeleton_tools").face(scene()))
    log("show map", p.show_map, "GH3 rig shown", p.show_source_rig)
    view((0.1, 0, 0.85), turn(20, 85), 3.0, perspective="PERSP")


def clips():
    p = props()
    clip_ids = tools("skeleton_tools").check_clip_ids(scene(), 4)
    log("clip buttons", [label for _, label in clip_ids])
    call("preview clip " + clip_ids[0][1], bpy.ops.chstage.preview_clip, clip=clip_ids[0][0])
    call("check clips", bpy.ops.chstage.check_clips)
    log("check result", p.checked, [(c.severity, c.message) for c in p.checks])
    p.show_map = False
    select([])
    scene().frame_set(min(scene().frame_end, scene().frame_start + 40))
    view((0.15, 0, 0.9), turn(-35, 82), 3.0)
    scroll_sidebar(12)


def materials():
    call("bind pose", bpy.ops.chstage.clear_pose)
    mesh = state["mesh"]
    select([mesh])
    log("after Set Up Skeleton, MMD Tools finds the model root", mmd_root(mesh) is not None)
    # Image > Save As in the Image Editor, File Format PNG (the same result: the image becomes a PNG file).
    folder = output / "textures"
    folder.mkdir(exist_ok=True)
    images = {
        n.image for s in mesh.material_slots for n in s.material.node_tree.nodes if n.type == "TEX_IMAGE" and n.image
    }
    for image in sorted(images, key=lambda i: i.name):
        if image.file_format != "PNG":
            image.file_format = "PNG"
            image.filepath_raw = str(folder / f"{Path(image.name).stem}.png")
            image.save()
            log("saved as PNG", image.name, tuple(image.size))
    for slot in mesh.material_slots:
        slot.material.chstage.blend = TRANSPARENCY.get(slot.material.name, "OPAQUE")
    bpy.data.materials["wing"].chstage.two_sided = True
    for name in HAIR:
        bpy.data.materials[name].chstage.hair_layer = "hair"
        mesh.active_material_index = mesh.material_slots.find(name)
        call("hair material + " + name, bpy.ops.chstage.hair_material, action="ADD")
    eyebrows(mesh)
    call("toon preview (selected meshes)", bpy.ops.chstage.toon_preview, enable=True, scope="SELECTED")
    call("validate after the materials", bpy.ops.chstage.validate_character)
    log("issues", [(i.severity, i.message) for i in scene().chstage.issues])
    mesh.active_material_index = mesh.material_slots.find("hair00")
    props().show_source_rig = False
    state["scroll_properties"] = 9
    panels("CHSTAGE_PT_character_weights", opened=("CHSTAGE_PT_character_weights",))
    material_panels()
    state["properties"] = "MATERIAL"
    head = state["armature"].matrix_world @ state["armature"].data.bones["頭"].head_local
    view((head.x, head.y, head.z + 0.05), turn(20, 88), 0.9, shading="MATERIAL")
    if WINDOWED:
        area("VIEW_3D").spaces.active.overlay.show_bones = False


def eyebrows(mesh):
    """Edit Mode: select the eyebrows (L with Delimit Material on each eyebrow), add a material slot with a copy of
    face01 named eyebrow, Assign."""
    import bmesh

    rig = state["armature"]
    eye = (rig.matrix_world @ rig.data.bones["目.L"].head_local).z
    with bpy.context.temp_override(**window_override(), object=mesh, active_object=mesh, selected_objects=[mesh]):
        bpy.ops.object.mode_set(mode="EDIT")
        data = bmesh.from_edit_mesh(mesh.data)
        face01 = mesh.material_slots.find("face01")
        for face in data.faces:
            face.select = False
        seen, brows, linked = set(), 0, 0
        for face in data.faces:
            if face.material_index != face01 or face.index in seen:
                continue
            island, stack = [], [face]
            seen.add(face.index)
            while stack:
                item = stack.pop()
                island.append(item)
                for edge in item.edges:
                    for other in edge.link_faces:
                        if other.material_index == face01 and other.index not in seen:
                            seen.add(other.index)
                            stack.append(other)
            if min((mesh.matrix_world @ v.co).z for f in island for v in f.verts) >= eye + 0.025:
                brows += 1
                linked += any(o.material_index != face01 for f in island for e in f.edges for o in e.link_faces)
                for f in island:
                    f.select = True
        bmesh.update_edit_mesh(mesh.data)
        log("eyebrow islands", brows, "joined to faces of other materials:", linked)
        bpy.ops.object.material_slot_add()
        brow = bpy.data.materials["face01"].copy()
        brow.name = "eyebrow"
        brow.chstage.hair_layer = "overHair"
        mesh.material_slots[-1].material = brow
        mesh.active_material_index = len(mesh.material_slots) - 1
        bpy.ops.object.material_slot_assign()
        bpy.ops.object.mode_set(mode="OBJECT")
    log("eyebrow faces", sum(1 for p in mesh.data.polygons if p.material_index == len(mesh.material_slots) - 1))


def chains():
    call("toon preview off", bpy.ops.chstage.toon_preview, enable=False, scope="SELECTED")
    rig = state["armature"]
    select([state["mesh"]])
    call("limit and normalize", bpy.ops.chstage.limit_weights)
    select([rig])
    call("chains from mmd physics", bpy.ops.chstage.import_mmd_physics)
    chain_tools = tools("chain_tools")
    first = {c.name: chain_tools.chain_bones(rig, c.name)[0].name for c in scene().chstage_chains}
    log("chains", [(name, bone, len(chain_tools.chain_bones(rig, name))) for name, bone in first.items()])
    for index in reversed(range(len(scene().chstage_chains))):
        if first[scene().chstage_chains[index].name].startswith(STIFF):
            scene().chstage_chains_index = index
            call("remove chain " + first[scene().chstage_chains[index].name], bpy.ops.chstage.chain_remove)
    for index, chain in enumerate(scene().chstage_chains):
        group = next(k for k in PRESETS if first[chain.name].startswith(k))
        scene().chstage_chains_index = index
        chain.preset = PRESETS[group]
        call(f"apply preset {chain.preset} {first[chain.name]}", bpy.ops.chstage.chain_preset)
    call("fit body colliders", bpy.ops.chstage.fit_colliders)
    with bpy.context.temp_override(**window_override(), object=rig, active_object=rig, selected_objects=[rig]):
        bpy.ops.object.mode_set(mode="POSE")
    rig.data.bones.active = rig.data.bones["全ての親"]
    call("add collider on 全ての親", bpy.ops.chstage.add_collider)
    floor = next(o for o in chain_tools.colliders(scene()) if o.parent_bone == "全ての親")
    floor.chstage_collider.label, floor.chstage_collider.radius, floor.chstage_collider.length = "floor", 3.0, 0.0
    # Shift S > Cursor to World Origin, Shift S > Selection to Cursor, then G Z -3: the centre 3 m under the origin.
    floor.matrix_world.translation = (0.0, 0.0, -3.0)
    for chain in scene().chstage_chains:
        chain.all_colliders = False
        chain.colliders = next(v for k, v in COLLIDERS.items() if first[chain.name].startswith(k))
    log(
        "colliders",
        [
            (chain_tools.collider_name(o), o.parent_bone, round(o.chstage_collider.radius, 3))
            for o in chain_tools.colliders(scene())
        ],
    )
    joints = [b for b in rig.data.bones if b.chstage_joint.enabled]
    log("joints", len(joints), "chains", len(scene().chstage_chains))
    props().show_map = False
    props().show_source_rig = False
    twin = next(i for i, c in enumerate(scene().chstage_chains) if first[c.name] == "髪２.L")
    scene().chstage_chains_index = twin
    rig.data.bones.active = rig.data.bones["髪４.L"]
    state["properties"] = "OBJECT"
    if WINDOWED:
        area("VIEW_3D").spaces.active.overlay.show_bones = True
    panels("CHSTAGE_PT_character_chains", opened=("CHSTAGE_PT_character_chains",))
    view((0, 0.1, 0.8), turn(200, 75), 3.0)


def validate():
    rig = state["armature"]
    with bpy.context.temp_override(**window_override(), object=rig, active_object=rig, selected_objects=[rig]):
        bpy.ops.object.mode_set(mode="OBJECT")
    package = scene().chstage.package
    package.package_id, package.name, package.version = "yourname.tda-miku", "Hatsune Miku (Tda Append)", "1.0.0"
    package.authors[0].name, package.authors[0].role = "Tda", "model"
    package.license = "LicenseRef-Tda-Miku-Append"
    package.redistribute = package.modify = package.commercial = False
    credit = package.credits.add()
    credit.name = "Hatsune Miku © Crypton Future Media, INC. www.piapro.net"
    call("validate", bpy.ops.chstage.validate_character)
    log("issues", [(i.severity, i.message) for i in scene().chstage.issues])
    select([])
    panels("CHSTAGE_PT_character", "CHSTAGE_PT_character_info", opened=("CHSTAGE_PT_character_info",))
    view((0.3, 0, 0.85), turn(25, 82), 3.6)


def export():
    call("export", bpy.ops.chstage.export_character, filepath=str(output / "yourname.tda-miku.chchar"))
    log("export issues", [(i.severity, i.message) for i in scene().chstage.issues])
    path = output / "yourname.tda-miku.chchar"
    log("package", path, path.stat().st_size if path.exists() else "missing")
    bpy.ops.wm.save_as_mainfile(filepath=str(output / "tda-miku.blend"))


def wait():
    """After a redraw: select the sidebar tab CH Character and the tab of the Properties editor."""
    if not WINDOWED:
        return
    region = next(r for r in area("VIEW_3D").regions if r.type == "UI")
    try:
        region.active_panel_category = "CH Character"
    except TypeError as error:
        print("tab", error)
    space = area("PROPERTIES").spaces.active
    if "properties" in state and space.context != state["properties"]:
        try:
            space.context = state["properties"]
        except TypeError as error:
            print("properties", error)
    elif state.get("scroll_properties"):
        properties = area("PROPERTIES")
        region = next(r for r in properties.regions if r.type == "WINDOW")
        with bpy.context.temp_override(window=window(), screen=window().screen, area=properties, region=region):
            for _ in range(state.pop("scroll_properties")):
                bpy.ops.view2d.scroll_down(deltax=0, deltay=40)


steps = [
    install,
    new_character,
    import_model,
    wait,
    lambda: shot("miku-import"),
    convert_materials,
    set_up_skeleton,
    wait,
    lambda: shot("miku-skeleton"),
    clips,
    wait,
    lambda: shot("miku-clip"),
    materials,
    wait,
    wait,
    lambda: shot("miku-toon"),
    chains,
    wait,
    lambda: shot("miku-chains"),
    validate,
    wait,
    lambda: shot("miku-validate"),
    export,
]


def tick():
    step = steps.pop(0)
    try:
        step()
    except Exception:
        traceback.print_exc()
    if steps:
        return 2.0
    bpy.ops.wm.quit_blender()
    return None


if WINDOWED:
    bpy.app.timers.register(tick, first_interval=2.0)
else:
    for step in steps:
        try:
            step()
        except Exception:
            traceback.print_exc()
