"""Actors (animated scenery): import the actor GLBs as Blender armatures and meshes, keep them packed, and export them.

An actor root is an empty with the marker ACTOR (object.chstage.actor holds the actors.json row). Its world transform
and scale are the actor transform and scale. The imported model (armature, meshes, clips as actions) is its child.
Export writes the packed GLB of the opened package while the Blender model did not change (the fingerprint is the
same as after the import), else it exports the Blender model with the safe glTF settings and the actor rules
(PACKAGE-FORMAT 6.7): unit scale channels dropped, clips that Blender did not import added from the packed GLB,
images of GH3 texture references embedded.
"""

import contextlib
import hashlib
import json
import tempfile
from pathlib import Path

import bpy
import numpy as np
from mathutils import Matrix

from . import blobs, glb_tools, gltf_io, sidecars, textures

ACTOR_IMPORT = {"disable_bone_shape": True}
# The importer puts each clip on an NLA strip that starts at the scene start frame (1), not at the clip time 0. Slide
# to zero gives the clips their source times again (GH3 and package clips start at 0 s).
ACTOR_EXPORT = {
    "export_animations": True,
    "export_cameras": False,
    "export_lights": False,
    "use_selection": True,
    "export_apply": True,
    "export_anim_slide_to_zero": True,
}


@contextlib.contextmanager
def shown(objects, view_layer):
    """Show objects that the viewport hides (eye icon and monitor icon) while the glTF exporter runs: the exporter
    exports only selected objects, and a hidden object cannot be selected. The viewport state is not package data."""
    hidden = [obj for obj in objects if obj.hide_get(view_layer=view_layer)]
    disabled = [obj for obj in objects if obj.hide_viewport]
    try:
        for obj in hidden:
            obj.hide_set(False, view_layer=view_layer)
        for obj in disabled:
            obj.hide_viewport = False
        yield
    finally:
        for obj in hidden:
            obj.hide_set(True, view_layer=view_layer)
        for obj in disabled:
            obj.hide_viewport = True


def is_actor_root(obj):
    return obj is not None and obj.chstage.marker == "ACTOR"


def actor_root(obj):
    """The actor root of an object (itself or a parent), or None."""
    while obj is not None:
        if is_actor_root(obj):
            return obj
        obj = obj.parent
    return None


def descendants(root):
    result = []
    stack = list(root.children)
    while stack:
        obj = stack.pop(0)
        result.append(obj)
        stack.extend(obj.children)
    return result


def actor_name(root):
    return root.chstage.actor.actor_name or root.name


def model_path(root):
    return root.chstage.actor.model_path or f"{actor_name(root)}.glb"


def action_fcurves(action):
    """The F-curves of an action (Blender 4.4+ layered actions, and the legacy API of 4.2)."""
    layers = getattr(action, "layers", None)
    if layers:
        for layer in layers:
            for strip in layer.strips:
                for bag in getattr(strip, "channelbags", []):
                    yield from bag.fcurves
        return
    yield from getattr(action, "fcurves", [])


def actions_of(obj):
    """The actions of an object: the active action, then the NLA strip actions (as the glTF exporter finds them)."""
    data = obj.animation_data
    if data is None:
        return []
    found = [data.action] if data.action else []
    for track in data.nla_tracks:
        for strip in track.strips:
            if strip.action is not None and strip.action not in found:
                found.append(strip.action)
    return found


CLIP = "chstage_clip"


def clip_name(action):
    """The clip name of an action: Blender action names are unique in the .blend file, so an import of a second GLB
    with the same clip names gives 'name.001'. The import stores the clip name on the action."""
    return action.get(CLIP) or action.name


def clip_names(root):
    names = []
    for obj in descendants(root):
        for action in actions_of(obj):
            if clip_name(action) not in names:
                names.append(clip_name(action))
    return names


def clip_action(root, name):
    return next((a for obj in descendants(root) for a in actions_of(obj) if clip_name(a) == name), None)


def _array(collection, attribute, width, dtype=np.float32):
    values = np.empty(len(collection) * width, dtype=dtype)
    if len(collection):
        collection.foreach_get(attribute, values)
    return values


def fingerprint(root):
    """SHA-256 of the Blender model of an actor: hierarchy, transforms, mesh data, weights, materials, bones, clips."""
    digest = hashlib.sha256()
    objects = descendants(root)
    index = {obj: i for i, obj in enumerate(objects)}

    def add(*values):
        for value in values:
            digest.update(value.tobytes() if isinstance(value, np.ndarray) else repr(value).encode())

    for obj in objects:
        add(
            obj.type,
            index.get(obj.parent, -1),
            obj.parent_type,
            obj.parent_bone,
            np.array(obj.matrix_local, dtype=np.float32),
            [m.type for m in obj.modifiers],
            [(m.object.name if getattr(m, "object", None) else None) for m in obj.modifiers],
        )
        add([slot.material.name if slot.material else None for slot in obj.material_slots])
        for slot in obj.material_slots:
            if slot.material is not None:
                add(
                    sorted(
                        (k, repr(v.to_dict() if hasattr(v, "to_dict") else v.to_list() if hasattr(v, "to_list") else v))
                        for k, v in slot.material.items()
                        if k.startswith("gh3")
                    )
                )
        if obj.type == "MESH":
            mesh = obj.data
            add(
                _array(mesh.vertices, "co", 3),
                _array(mesh.loops, "vertex_index", 1, np.int32),
                _array(mesh.polygons, "loop_total", 1, np.int32),
                _array(mesh.polygons, "material_index", 1, np.int32),
            )
            for attribute in mesh.attributes:
                width = {
                    "FLOAT": 1,
                    "INT": 1,
                    "FLOAT_VECTOR": 3,
                    "FLOAT_COLOR": 4,
                    "BYTE_COLOR": 4,
                    "FLOAT2": 2,
                    "INT8": 1,
                    "BOOLEAN": 1,
                    "QUATERNION": 4,
                    "INT32_2D": 2,
                }.get(attribute.data_type)
                field = {
                    "FLOAT_VECTOR": "vector",
                    "FLOAT2": "vector",
                    "FLOAT_COLOR": "color",
                    "BYTE_COLOR": "color",
                    "QUATERNION": "value",
                    "INT32_2D": "value",
                }.get(attribute.data_type, "value")
                if width is None:
                    continue
                dtype = (
                    np.int32
                    if attribute.data_type in ("INT", "INT8", "INT32_2D")
                    else bool
                    if attribute.data_type == "BOOLEAN"
                    else np.float32
                )
                add(attribute.name, attribute.domain, _array(attribute.data, field, width, dtype))
            add([group.name for group in obj.vertex_groups])
            for vertex in mesh.vertices:
                if vertex.groups:
                    add(vertex.index, [(g.group, round(g.weight, 7)) for g in vertex.groups])
        elif obj.type == "ARMATURE":
            bones = obj.data.bones
            add(
                [(b.name, b.parent.name if b.parent else None) for b in bones],
                _array(bones, "head_local", 3),
                _array(bones, "tail_local", 3),
                np.array([np.array(b.matrix_local, dtype=np.float32) for b in bones]),
            )
        for action in actions_of(obj):
            add(clip_name(action))
            for curve in action_fcurves(action):
                add(
                    curve.data_path,
                    curve.array_index,
                    _array(curve.keyframe_points, "co", 2),
                    [k.interpolation for k in curve.keyframe_points[:1]],
                )
    return digest.hexdigest()


def frame_range(root):
    ranges = [action.frame_range for obj in descendants(root) for action in actions_of(obj)[:1]]
    return (min(r[0] for r in ranges), max(r[1] for r in ranges)) if ranges else None


def frame_scene(scene, roots):
    """Set the scene frame range to the longest start clip of the actors, so that Play shows them."""
    ranges = [r for r in (frame_range(root) for root in roots) if r]
    if ranges:
        scene.frame_start = int(min(r[0] for r in ranges))
        scene.frame_end = max(scene.frame_start + 1, int(max(r[1] for r in ranges)))


def used_clips(props, names):
    """The clips that actors.json uses (start clip and action clips), in the GLB order; the first clip without a start clip."""
    wanted = {props.clip or (names[0] if names else "")}
    for action in props.actions:
        wanted.update(c.strip() for c in action.clips.split(",") if c.strip())
    return [name for name in names if name in wanted]


def import_model(scene, root, data, clips, collection):
    """Import an actor GLB with the given clips under the actor root. Returns the new objects."""
    props = root.chstage.actor
    before, before_actions = set(bpy.data.objects), set(bpy.data.actions)
    # The same fixes as for stage.glb (package_open.import_stage): the importer drops repeated triangles.
    data, _ = glb_tools.separate_faces(glb_tools.keep_animations(textures.decode_glb(data), clips))
    data, _ = glb_tools.fill_texcoord_gaps(data)
    data, _ = glb_tools.normalize_normals(data)
    with tempfile.TemporaryDirectory(prefix="chstage-actor-") as folder:
        path = Path(folder) / "actor.glb"
        path.write_bytes(data)
        with bpy.context.temp_override(scene=scene, view_layer=scene.view_layers[0], collection=collection):
            gltf_io.import_glb(path, fps=props.fps, **ACTOR_IMPORT)
    new = [obj for obj in bpy.data.objects if obj not in before]
    # The importer puts each clip on an NLA track with the clip name. The action name can differ: 'name.001' when the
    # name is in use, '<clip>_<object>' in Blender 4.2.
    for obj in new:
        for track in obj.animation_data.nla_tracks if obj.animation_data else []:
            for strip in track.strips:
                if strip.action is not None and strip.action not in before_actions:
                    strip.action[CLIP] = track.name
    for action in bpy.data.actions:
        if action not in before_actions and CLIP not in action:
            action[CLIP] = max(
                (c for c in clips if action.name == c or action.name.startswith((c + ".", c + "_"))),
                key=len,
                default=action.name,
            )
    saved = root.matrix_world.copy()
    root.matrix_world = Matrix.Identity(4)
    for obj in new:
        if obj.parent is None:
            obj.parent = root
            obj.matrix_parent_inverse = Matrix.Identity(4)
    root.matrix_world = saved
    for obj in new:
        if obj.type == "ARMATURE":
            # The importer shows the bones in front; GH3 rigs then hide the model.
            obj.show_in_front = False
            obj.data.display_type = "WIRE"
        if obj.animation_data and obj.animation_data.action is None:
            first = next((a for a in actions_of(obj)), None)
            if first is not None:
                obj.animation_data.action = first
    if clips and props.clip:
        for obj in new:
            if obj.type == "ARMATURE" and obj.animation_data:
                set_active_clip(obj, props.clip)
    props.loaded_json = json.dumps(clip_names(root))
    return new


def set_active_clip(armature, name):
    """Make a clip the active action of an armature (the others stay on NLA tracks)."""
    data = armature.animation_data
    for track in data.nla_tracks:
        for strip in track.strips:
            if strip.action is not None and clip_name(strip.action) == name:
                data.action = strip.action
                if hasattr(data, "action_slot") and getattr(strip, "action_slot", None) is not None:
                    data.action_slot = strip.action_slot
                return


def duplicate_model(source_root, root):
    """Linked copies of the model objects of another actor with the same GLB (shared meshes, armature data, and actions)."""
    mapping = {}
    for obj in descendants(source_root):
        copy = obj.copy()
        mapping[obj] = copy
        for collection in obj.users_collection:
            collection.objects.link(copy)
    for obj, copy in mapping.items():
        copy.parent = mapping.get(obj.parent, root)
        copy.matrix_parent_inverse = obj.matrix_parent_inverse.copy()
        for modifier in copy.modifiers:
            if getattr(modifier, "object", None) in mapping:
                modifier.object = mapping[modifier.object]
    root.chstage.actor.loaded_json = source_root.chstage.actor.loaded_json
    return list(mapping.values())


def new_root(scene, name, collection):
    root = bpy.data.objects.new(name, None)
    collection.objects.link(root)
    root.empty_display_type = "PLAIN_AXES"
    root.empty_display_size = 0.5
    root.chstage.marker = "ACTOR"
    root.chstage.actor.actor_name = name
    return root


def open_actors(scene, actors_json, read, collection, all_clips=False):
    """Make the actor roots and import their models. read: package path to bytes. Returns the actor roots."""
    roots = []
    imported = {}
    for actor in sidecars.open_actors(actors_json):
        root = new_root(scene, actor["name"], collection)
        props = root.chstage.actor
        props.model_path = actor["model"]
        props.clip, props.light_group, props.role = actor["clip"], actor["lightGroup"], actor["role"]
        props.playback = actor["playback"] or "DEFAULT"
        props.script_object, props.script, props.animation_events = (
            actor["scriptObject"],
            actor["script"],
            actor["animationEvents"],
        )
        if actor["created"] is not None:
            props.use_created, props.created = True, actor["created"]
        if actor["initiallyVisible"] is not None:
            props.use_initially_visible, props.initially_visible = True, actor["initiallyVisible"]
        props.write_scale = actor["writeScale"]
        for name, clips in actor["actions"].items():
            item = props.actions.add()
            item.name, item.clips = name, ", ".join(clips)
        root.matrix_world = Matrix([list(row) for row in actor["matrix"]])
        root.chstage.order = actor["order"]
        root.chstage.kept = sidecars.kept_text(actor=actor["source"])
        data = read(actor["model"])
        props.blob = blobs.store(f"{scene.chstage.package.package_id}/{actor['model']}", data)
        names = glb_tools.animation_names(data)
        props.clips_json = json.dumps(names)
        sha = blobs.sha256(props.blob)
        if sha in imported:
            duplicate_model(imported[sha], root)
        else:
            import_model(scene, root, data, names if all_clips else used_clips(props, names), collection)
            imported[sha] = root
        roots.append(root)
    return roots


def record_fingerprints(roots):
    for root in roots:
        root.chstage.actor.fingerprint = fingerprint(root)


def actor_roots(scene):
    return sorted((obj for obj in scene.objects if is_actor_root(obj)), key=lambda o: (o.chstage.order, o.name))


def descriptions(scene):
    """sidecars.build_actors descriptions of the actor roots of a scene."""
    result = []
    for root in actor_roots(scene):
        props = root.chstage.actor
        result.append(
            {
                "object": root.name,
                "owner": root.chstage,
                "name": actor_name(root),
                "model": model_path(root),
                "clip": props.clip,
                "playback": "" if props.playback == "DEFAULT" else props.playback,
                "lightGroup": props.light_group,
                "role": props.role,
                "scriptObject": props.script_object,
                "script": props.script,
                "animationEvents": props.animation_events,
                "created": props.created if props.use_created else None,
                "initiallyVisible": props.initially_visible if props.use_initially_visible else None,
                "writeScale": props.write_scale,
                "actions": {
                    a.name: [c.strip() for c in a.clips.split(",") if c.strip()] for a in props.actions if a.name
                },
                "matrix": [list(row) for row in root.matrix_world],
            }
        )
    return result


def has_model(root):
    return any(obj.type in ("ARMATURE", "MESH") for obj in descendants(root))


def model_changed(root):
    props = root.chstage.actor
    if props.blob is None:
        return True
    if not has_model(root):
        return False
    return fingerprint(root) != props.fingerprint


@contextlib.contextmanager
def clips_on_tracks(objects):
    """Put an active action that is on no NLA track on a temporary track with its clip name (the export uses tracks)."""
    added = []
    try:
        for obj in objects:
            data = obj.animation_data
            if data is None or data.action is None:
                continue
            if any(strip.action == data.action for track in data.nla_tracks for strip in track.strips):
                continue
            track = data.nla_tracks.new()
            track.name = clip_name(data.action)
            start = data.action.frame_range[0]
            strip = track.strips.new(track.name, int(start), data.action)
            if hasattr(strip, "action_slot") and hasattr(data, "action_slot") and data.action_slot is not None:
                strip.action_slot = data.action_slot
            added.append((data, track))
        yield
    finally:
        for data, track in added:
            data.nla_tracks.remove(track)


def export_model(scene, root, issues):
    """GLB bytes of the Blender model of an actor (actor rules applied), or None after an error."""
    props = root.chstage.actor
    objects = [obj for obj in descendants(root) if obj.chstage.marker != "PROXY"]
    if not any(obj.type == "ARMATURE" for obj in objects):
        issues.error(
            f"actor '{actor_name(root)}': the actor needs an armature with skinned meshes as children", root.name
        )
        return None
    view_layer = scene.view_layers[0]
    selected = [obj for obj in scene.objects if obj.select_get(view_layer=view_layer)]
    saved_matrix = root.matrix_world.copy()
    saved_fps = (scene.render.fps, scene.render.fps_base)
    try:
        root.matrix_world = Matrix.Identity(4)
        view_layer.update()
        with shown(objects, view_layer), tempfile.TemporaryDirectory(prefix="chstage-actor-") as folder:
            for obj in scene.objects:
                obj.select_set(obj in objects, view_layer=view_layer)
            path = Path(folder) / "actor.glb"
            # NLA_TRACKS: one animation for each NLA track (Open puts each clip on a track with its name). The ACTIONS
            # mode also exports the actions of other actors with the same bone names.
            with clips_on_tracks(objects):
                with bpy.context.temp_override(scene=scene, view_layer=view_layer):
                    gltf_io.export_glb(path, fps=props.fps, export_animation_mode="NLA_TRACKS", **ACTOR_EXPORT)
            data = path.read_bytes()
    finally:
        root.matrix_world = saved_matrix
        scene.render.fps, scene.render.fps_base = saved_fps
        for obj in scene.objects:
            obj.select_set(obj in selected, view_layer=view_layer)
        view_layer.update()
    names = {action.name: clip_name(action) for obj in objects for action in actions_of(obj)}
    names.update(
        {
            track.name: clip_name(strip.action)
            for obj in objects
            if obj.animation_data
            for track in obj.animation_data.nla_tracks
            for strip in track.strips
            if strip.action is not None
        }
    )
    data = glb_tools.rename_animations(data, names)
    data, _, problems = glb_tools.drop_unit_scale_channels(data)
    for problem in problems:
        issues.error(f"actor '{actor_name(root)}': {problem}", root.name)
    source = blobs.load(props.blob)
    if source is not None:
        loaded = set(glb_tools.animation_names(data))
        missing = [
            name
            for name in json.loads(props.clips_json or "[]")
            if name not in loaded and name not in json.loads(props.loaded_json or "[]")
        ]
        if missing:
            data, problems = glb_tools.merge_animations(data, source, set(missing))
            for problem in problems:
                issues.error(f"actor '{actor_name(root)}': {problem}", root.name)
        source_gltf, _ = glb_tools.read_glb(source)
        data = glb_tools.restore_names(
            data,
            [m.get("name") for m in source_gltf.get("materials", []) if m.get("name")],
            [n.get("name") for n in source_gltf.get("nodes", []) if n.get("name")] + ["Armature"],
        )
        gltf, _ = glb_tools.read_glb(data)
        present = {image.get("name") for image in gltf.get("images", [])}
        needed = glb_tools.texture_reference_names(gltf) - present
        if needed:
            images = glb_tools.image_bytes_by_name(source)
            data = glb_tools.add_images(data, {name: images[name] for name in needed if name in images})
    gltf, _ = glb_tools.read_glb(data)
    present = {image.get("name") for image in gltf.get("images", [])}
    for name in sorted(glb_tools.texture_reference_names(gltf) - present):
        issues.error(f"actor '{actor_name(root)}': no image for the texture reference #{name}", root.name)
    return data


def model_bytes(scene, root, issues, mode=None):
    """The GLB of an actor for the package: the packed GLB, or the exported Blender model."""
    props = root.chstage.actor
    mode = mode or props.model_mode
    packed = blobs.load(props.blob)
    if mode == "PACKED" or (mode == "AUTO" and not model_changed(root)):
        if packed is None:
            issues.error(f"actor '{actor_name(root)}': no packed model; set Model export to Blender model", root.name)
        return packed
    return export_model(scene, root, issues)


def reload_model(scene, root, all_clips=True):
    """Import the packed GLB again (with all clips, or the used clips) for this actor and the actors with the same GLB."""
    sha = blobs.sha256(root.chstage.actor.blob)
    data = blobs.load(root.chstage.actor.blob)
    if data is None:
        return []
    roots = [r for r in actor_roots(scene) if blobs.sha256(r.chstage.actor.blob) == sha]
    collection = scene_collection(scene, root)
    old_actions = {action for other in roots for obj in descendants(other) for action in actions_of(obj)}
    for other in roots:
        for obj in reversed(descendants(other)):
            bpy.data.objects.remove(obj, do_unlink=True)
    for action in old_actions:
        if action.users == 0 or (action.users == 1 and action.use_fake_user):
            bpy.data.actions.remove(action)
    names = json.loads(root.chstage.actor.clips_json or "[]") or glb_tools.animation_names(data)
    import_model(scene, roots[0], data, names if all_clips else used_clips(roots[0].chstage.actor, names), collection)
    for other in roots[1:]:
        duplicate_model(roots[0], other)
    scene.view_layers[0].update()
    record_fingerprints(roots)
    return roots


def scene_collection(scene, obj):
    """A collection of the scene that holds the object (not a light group collection of the lighting preview)."""
    in_scene = {scene.collection, *scene.collection.children_recursive}
    return next((c for c in obj.users_collection if c in in_scene), scene.collection)


def make_actor(scene, armature, name):
    """Make an actor of an armature and its child meshes: a new actor root at the armature origin becomes their parent."""
    collection = scene_collection(scene, armature)
    root = new_root(scene, name, collection)
    root.matrix_world = Matrix.Translation(armature.matrix_world.translation)
    world = armature.matrix_world.copy()
    armature.parent = root
    armature.matrix_world = world
    return root
