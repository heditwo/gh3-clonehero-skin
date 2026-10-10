"""Camera tools: the shot preview, and the proxy GH3 rigs that show camera bone anchors.

A proxy rig is the band model of the GH3 library (band/<role>/model.glb in gh3-band-v1.chlib) in the first frame of
its first clip, parented to the band placement. Its bone frames (rig.anchor_frames) are stored in the scene
(scene.chstage.anchor_frames), so that Open and Export use the same frames also when the library is gone later.
"""

import json
import tempfile
from pathlib import Path

import bpy
from mathutils import Matrix

from . import actor_tools, collect, glb_tools, gltf_io, rig, sidecars, textures
from .vendor.package_format.validate import Report, Source

LIBRARY_NAME = "gh3-band-v1.chlib"
CHARACTER_LIBRARY_NAME = "gh3-characters-v1.chlib"
RIG_COLLECTION = "GH3 Rig Proxies"


def preferences():
    addon = bpy.context.preferences.addons.get(__package__)
    return addon.preferences if addon is not None else None


def find_library(package_path=None):
    """The GH3 library package: the add-on preference, else gh3-band-v1.chlib next to the package (or its folder). '' if none."""
    prefs = preferences()
    if prefs is not None and prefs.library:
        path = Path(bpy.path.abspath(prefs.library))
        if path.name == "manifest.json": path = path.parent
        return str(path) if path.exists() else ""
    if package_path:
        folder = Path(package_path).resolve().parent
        for candidate in (folder/LIBRARY_NAME, folder.parent/LIBRARY_NAME, folder/"unpacked"/LIBRARY_NAME):
            if candidate.exists(): return str(candidate)
    return ""


def find_character_library(package_path=None):
    """The GH3 character library: the add-on preference, else gh3-characters-v1.chlib next to the GH3 library or next
    to the package (or its folder). '' if none."""
    prefs = preferences()
    if prefs is not None and prefs.character_library:
        path = Path(bpy.path.abspath(prefs.character_library))
        if path.name == "manifest.json": path = path.parent
        return str(path) if path.exists() else ""
    folders = []
    library = find_library(package_path)
    if library: folders.append(Path(library).resolve().parent)
    if package_path:
        folder = Path(package_path).resolve().parent
        folders += [folder, folder.parent, folder/"unpacked"]
    for folder in folders:
        if (folder/CHARACTER_LIBRARY_NAME).exists(): return str(folder/CHARACTER_LIBRARY_NAME)
    return ""


def band_markers(scene):
    return [obj for obj in scene.objects if obj.chstage.marker == "BAND"]


def proxy_bodies(marker):
    return [c for c in marker.children if c.chstage.marker == "PROXY" and c.type == "MESH" and c.data.name.startswith("chstage-proxy-body")]


def freeze_first_pose(armature, imported_actions):
    """Set the pose of the first key of the active action, then remove the animation (the proxy shows one pose)."""
    data = armature.animation_data
    if data is None or data.action is None: return
    start = data.action.frame_range[0]
    values = [(curve.data_path, curve.array_index, curve.evaluate(start)) for curve in actor_tools.action_fcurves(data.action)]
    armature.animation_data_clear()
    for path, index, value in values:
        owner_path, _, attribute = path.rpartition(".")
        try:
            owner = armature.path_resolve(owner_path) if owner_path else armature
            getattr(owner, attribute)[index] = value
        except (ValueError, AttributeError, TypeError, IndexError):
            continue
    for action in imported_actions:
        if action.users == 0: bpy.data.actions.remove(action)


def import_rig(scene, marker, data, collection):
    before_objects, before_actions = set(bpy.data.objects), set(bpy.data.actions)
    data = textures.decode_glb(glb_tools.keep_animations(data, glb_tools.animation_names(data)[:1]))
    with tempfile.TemporaryDirectory(prefix="chstage-rig-") as folder:
        path = Path(folder)/"rig.glb"
        path.write_bytes(data)
        with bpy.context.temp_override(scene=scene, view_layer=scene.view_layers[0], collection=collection):
            gltf_io.import_glb(path, disable_bone_shape=True)
    new = [obj for obj in bpy.data.objects if obj not in before_objects]
    actions = [action for action in bpy.data.actions if action not in before_actions]
    for obj in new:
        if obj.type == "ARMATURE": freeze_first_pose(obj, actions)
    for obj in new:
        obj.chstage.marker = "PROXY"
        obj.hide_render = True
        obj.hide_select = True
        if obj.type == "ARMATURE": obj.hide_set(True, view_layer=scene.view_layers[0])
        if obj.parent is None:
            obj.parent = marker
            obj.matrix_parent_inverse = Matrix.Identity(4)
            obj.matrix_basis = Matrix.Identity(4)
    return new


def point_descriptions(scene):
    """(object, point description) of every camera point with an anchor that is not world."""
    result = []
    for obj in scene.objects:
        if obj.type in ("CAMERA", "EMPTY") and obj.chstage.marker == "NONE" and obj.chstage.point.anchor_role != "world":
            description = collect.point_description(obj, scene)
            result.append((obj, description))
    return result


def reanchor(scene, change):
    """Run change() (which changes the anchor frames) and keep the offset of every anchored point from its anchor."""
    band = sidecars.band_frames(collect.markers(scene))
    old = collect.anchor_frames(scene)
    offsets = [(obj, d["anchor"], *sidecars.point_offset(d, band, old)) for obj, d in point_descriptions(scene)]
    change()
    new = collect.anchor_frames(scene)
    for obj, anchor, position, rotation in offsets:
        matrix = sidecars.offset_matrix(anchor, position, rotation, band, new)
        scale = obj.matrix_world.to_scale()
        obj.matrix_world = Matrix([list(row) for row in matrix]) @ Matrix.Diagonal((*scale, 1))


def rig_collection(scene, create=False):
    """The proxy rig collection of a scene (a child of the scene collection with the custom property chstage_rigs)."""
    collection = next((c for c in scene.collection.children if c.get("chstage_rigs")), None)
    if collection is None and create:
        collection = bpy.data.collections.new(RIG_COLLECTION)
        collection["chstage_rigs"] = True
        scene.collection.children.link(collection)
    return collection


def remove_rig_objects(scene):
    collection = rig_collection(scene)
    if collection is not None:
        for obj in list(collection.all_objects): bpy.data.objects.remove(obj, do_unlink=True)
        for child in list(collection.children_recursive): bpy.data.collections.remove(child)
    for marker in band_markers(scene):
        for body in proxy_bodies(marker): body.hide_set(False, view_layer=scene.view_layers[0])


def load_rigs(scene, library, keep_offsets=False):
    """Load the band rigs of the library for the band placements of the scene. Returns the roles."""
    report = Report()
    source = Source(Path(library), report)
    if report.errors: raise ValueError("; ".join(report.errors))
    frames = {}

    def change():
        remove_rig_objects(scene)
        collection = rig_collection(scene, create=True)
        for marker in band_markers(scene):
            role = marker.chstage.band_role
            path = f"band/{role}/model.glb"
            if path not in source.set or role in frames: continue
            data = source.read(path)
            frames[role] = rig.anchor_frames(data)
            import_rig(scene, marker, data, collection)
            for body in proxy_bodies(marker): body.hide_set(True, view_layer=scene.view_layers[0])
        if not frames: raise ValueError("no band/<role>/model.glb for the band placements")
        scene.chstage.anchor_frames = json.dumps(frames, separators=(",", ":"))
        scene.chstage.rig_library = str(library)

    if keep_offsets: reanchor(scene, change)
    else: change()
    return sorted(frames)


def unload_rigs(scene):
    def change():
        remove_rig_objects(scene)
        scene.chstage.anchor_frames = ""
        scene.chstage.rig_library = ""
    reanchor(scene, change)


# Shot browser.
def shot_groups(scene):
    groups = []
    for obj in scene.objects:
        if obj.type == "CAMERA" and obj.chstage.shot.enabled:
            for group in obj.chstage.shot.groups:
                if group.name and group.name not in groups: groups.append(group.name)
    return groups


def shot_visible(obj, scene):
    props = scene.chstage
    shot = obj.chstage.shot
    if obj.type != "CAMERA" or not shot.enabled: return False
    if props.shot_type != "ALL" and shot.type != props.shot_type: return False
    if props.shot_group and not any(g.name == props.shot_group for g in shot.groups): return False
    return True


def view_through(context, scene, obj):
    """Make obj the scene camera and look through it in every 3D view of the window."""
    scene.camera = obj
    screen = context.screen if context is not None else None
    for area in (screen.areas if screen else []):
        if area.type == "VIEW_3D":
            area.spaces.active.region_3d.view_perspective = "CAMERA"
            area.tag_redraw()


def preview_index(context, scene):
    index = scene.chstage.shots_index
    if not 0 <= index < len(scene.objects): return
    obj = scene.objects[index]
    if obj.type != "CAMERA" or not obj.chstage.shot.enabled: return
    view_through(context, scene, obj)
    view_layer = context.view_layer if context is not None and context.view_layer is not None else scene.view_layers[0]
    if view_layer in scene.view_layers.values():
        for other in scene.objects:
            if other.select_get(view_layer=view_layer): other.select_set(False, view_layer=view_layer)
        obj.select_set(True, view_layer=view_layer)
        view_layer.objects.active = obj


def anchor_point_count(scene):
    """(bone-anchored points, of them shown on a rig bone)."""
    frames = collect.anchor_frames(scene)
    band = sidecars.band_frames(collect.markers(scene))
    total = exact = 0
    for _, description in point_descriptions(scene):
        if description["anchor"].endswith(".root"): continue
        frame = sidecars.anchor_frame(description["anchor"], band, frames)
        if frame is None: continue
        total += 1
        exact += bool(frame[2])
    return total, exact

