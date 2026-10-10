"""Character tools on the fixed GH3 rigs and on character library profiles (docs/PACKAGE-FORMAT.md sections 9 and
10.1, docs/BLENDER-ADDON.md section 1.15).

A character scene has the profile rig (an armature, scene.chstage.character.rig), the skinned meshes of the creator
(children of the rig with an Armature modifier), and helpers that are never exported: the GH3 reference body and the
attachment preview (instrument, microphone, or drum kit) of the profile, both from the library band model. A character
library profile (for example gh3-guitarist-midori-v1) takes its rig from characters.json and its helpers
from the GH3 character package of the profile (gh3.<name>...chchar next to the character library). The clip preview
converts library clips (q48) to actions of the rig at the clip rate (30 fps); it has the fixed profiles only.
"""

import json
import tempfile
from pathlib import Path

import bpy
import numpy as np
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree

from . import brotli_decode, camera_tools, glb_tools, gltf_io, package_open
from .vendor.package_format import character, q48
from .vendor.package_format.glb import Glb

HELPER = "chstage_helper"
PREVIEW = "chstage_clip_preview"
CLIP_FPS = q48.FPS
# glTF (Y up) to Blender (Z up), as the glTF importer converts node transforms.
AXES = np.array([[1., 0, 0, 0], [0, 0, -1, 0], [0, 1, 0, 0], [0, 0, 0, 1]])
_libraries = {}
_character_libraries = {}


def props(scene):
    return scene.chstage.character


def role_of(profile):
    return character.PROFILES[profile]


def library_path(scene=None, package_path=None):
    """The library of a character scene: its stored library, the add-on preference, or gh3-band-v1.chlib next to the package."""
    if scene is not None and props(scene).library:
        path = Path(bpy.path.abspath(props(scene).library))
        if path.exists(): return str(path)
    return camera_tools.find_library(package_path)


def library(path):
    """A cached character.Library for a library path."""
    path = str(Path(path).resolve())
    if path not in _libraries: _libraries[path] = character.Library(path)
    return _libraries[path]


def character_library_path(scene=None, package_path=None):
    """The character library of a scene: its stored file, the add-on preference, or gh3-characters-v1.chlib next to the
    GH3 library or the package."""
    if scene is not None and props(scene).character_library:
        path = Path(bpy.path.abspath(props(scene).character_library))
        if path.exists(): return str(path)
    return camera_tools.find_character_library(package_path)


def character_library(path):
    """A cached character.CharacterLibrary for a character library path."""
    path = str(Path(path).resolve())
    if path not in _character_libraries: _character_libraries[path] = character.CharacterLibrary(path)
    return _character_libraries[path]


def profile_id(scene):
    """The profile of character.json: the character library profile, else the fixed profile."""
    p = props(scene)
    return p.library_profile or p.profile


def library_profile(scene):
    """(CharacterLibrary, profile) of a scene on a character library profile, else (None, None)."""
    p = props(scene)
    if not p.library_profile: return None, None
    path = character_library_path(scene)
    if not path: raise ValueError(f"no GH3 character library for the profile {p.library_profile}: set 'GH3 character library (.chlib)' "
                                  "in the add-on preferences")
    clib = character_library(path)
    if p.library_profile not in clib.profiles: raise ValueError(f"the character library {clib.id} has no profile {p.library_profile}")
    return clib, p.library_profile


def profile_package(clib, profile):
    """The GH3 character package of a character library profile (the first gh3.<name>...chchar next to the library)."""
    found = character.profile_packages(clib.path.resolve().parent, profile)
    if not found: raise ValueError(f"no character package for the profile {profile} next to {clib.path.name} (for example gh3.<name>.outfit1.style1.chchar)")
    return found[0]


def scene_rig_data(scene, lib=None):
    """The profile rig (character.Rig) of a character scene."""
    clib, profile = library_profile(scene)
    if clib is not None: return clib.rig(profile)
    return profile_rig(lib or library(library_path(scene)), role_of(props(scene).profile))


def helper_parts(scene, lib, role):
    """The parts of the GH3 model of the scene profile (character.split_model): the library band model for a fixed
    profile, the GH3 character package for a character library profile."""
    clib, profile = library_profile(scene)
    if clib is None:
        return character.split_model(lib.model(role), character.hair_joints(lib.json("band/hair.json"), role))
    return character.split_model(character.profile_model(profile_package(clib, profile)), clib.hair_joints(profile))


def profile_rig(lib, role):
    key = ("rig", role)
    if key not in lib._json: lib._json[key] = character.Rig(Glb(lib.model(role)))
    return lib._json[key]


def is_helper(obj):
    return obj is not None and HELPER in obj


def character_meshes(scene):
    """The exported meshes: meshes skinned to the rig (Armature modifier or parent), without helpers."""
    rig = props(scene).rig
    if rig is None: return []
    result = []
    for obj in scene.objects:
        if obj.type != "MESH" or is_helper(obj) or obj.chstage.marker == "PROXY": continue
        if obj.parent == rig or any(m.type == "ARMATURE" and m.object == rig for m in obj.modifiers): result.append(obj)
    return sorted(result, key=lambda o: o.name)


def import_glb(scene, data, collection, mapped=False):
    """Import GLB bytes with the safe settings and the import fixes. Returns (new objects, glTF JSON, binary, node
    objects, materials, images by glTF index); the last three only when mapped (the import hooks of package_open)."""
    prepared, _ = glb_tools.separate_faces(data)
    prepared, _ = glb_tools.fill_texcoord_gaps(prepared)
    prepared, _ = glb_tools.normalize_normals(prepared)
    before = set(bpy.data.objects)
    hooks = package_open._OPEN
    if mapped: hooks.update(active=True, nodes={}, materials={}, images={})
    try:
        with tempfile.TemporaryDirectory(prefix="chstage-character-") as folder:
            path = Path(folder)/"character.glb"
            path.write_bytes(prepared)
            with bpy.context.temp_override(scene=scene, view_layer=scene.view_layers[0], collection=collection):
                gltf_io.import_glb(path, fps=CLIP_FPS, disable_bone_shape=True)
        nodes, mats, images = dict(hooks["nodes"]), dict(hooks["materials"]), dict(hooks["images"])
    finally:
        if mapped: hooks.update(active=False, nodes={}, materials={}, images={})
    gltf, binary = glb_tools.read_glb(data)
    new = [obj for obj in bpy.data.objects if obj not in before]
    for obj in new:
        if obj.type == "ARMATURE":
            obj.show_in_front = False
            obj.data.display_type = "STICK"
    return new, gltf, binary, nodes, mats, images


def retarget(meshes, rig):
    """Skin meshes of another armature (with the same bone names) to the rig."""
    for obj in meshes:
        obj.parent = rig
        obj.matrix_parent_inverse = Matrix.Identity(4)
        obj.matrix_basis = Matrix.Identity(4)
        for modifier in obj.modifiers:
            if modifier.type == "ARMATURE": modifier.object = rig


def make_helper(obj, kind, name):
    obj.name = name
    obj[HELPER] = kind
    obj.chstage.marker = "PROXY"
    obj.hide_render = True
    obj.hide_select = True


def remove_helpers(scene):
    for obj in [o for o in scene.objects if is_helper(o)]:
        data = obj.data
        bpy.data.objects.remove(obj, do_unlink=True)
        if data is not None and data.users == 0 and isinstance(data, bpy.types.Mesh): bpy.data.meshes.remove(data)
    props(scene).reference = None
    props(scene).attachment = None


def load_helpers(scene, collection, lib, role, show_reference=True):
    """Import the GH3 reference body and the attachment of a profile from the library as helpers of the rig."""
    rig = props(scene).rig
    remove_helpers(scene)
    parts = helper_parts(scene, lib, role)
    props(scene).hair_reference = json.dumps(parts["hairMaterials"])
    for kind, data in (("reference", parts["body"]), ("attachment", parts["attachment"])):
        if data is None: continue
        new, *_ = import_glb(scene, data, collection)
        armatures = [o for o in new if o.type == "ARMATURE"]
        meshes = [o for o in new if o.type == "MESH"]
        retarget(meshes, rig)
        for armature in armatures: bpy.data.objects.remove(armature, do_unlink=True)
        for index, obj in enumerate(meshes):
            label = "GH3 body (reference)" if kind == "reference" else f"GH3 {character.ATTACHMENTS.get(role, 'attachment')} (preview)"
            make_helper(obj, kind, label if index == 0 else f"{label} {index + 1}")
            if kind == "reference":
                obj.display_type = "WIRE"
                obj.hide_set(not show_reference, view_layer=scene.view_layers[0])
        if meshes: setattr(props(scene), kind, meshes[0])


def load_profile_data(scene, lib, role):
    """Hair chains and the clip list of a profile into the scene (a character library profile has no clip list)."""
    clips = props(scene).clips
    clips.clear()
    props(scene)["clips_index"] = -1
    clib, profile = library_profile(scene)
    if clib is not None:
        props(scene).hair_chains = json.dumps(clib.hair_chains(profile))
        for clip, label, duration, frames in clib.clip_items(profile, props(scene).animations):
            if not clib.has_clip(clip): continue
            item = clips.add()
            item.clip, item.label, item.duration, item.frames = clip, label, duration, frames
        return
    rig_data = profile_rig(lib, role)
    props(scene).hair_chains = json.dumps(character.hair_chains(lib.json("band/hair.json"), role, rig_data))
    for clip, label, duration, frames in character.clip_items(lib.json("catalog.json"), lib.json("band/band.json"), role):
        if not lib.has_clip(role, clip): continue
        item = clips.add()
        item.clip, item.label, item.duration, item.frames = clip, label, duration, frames
    props(scene)["clips_index"] = -1


def add_preview_camera(scene, collection):
    data = bpy.data.cameras.new("character-preview")
    data.lens = 50
    camera = bpy.data.objects.new("Preview Camera", data)
    collection.objects.link(camera)
    # The performer looks toward Blender -Y (docs/BLENDER-ADDON.md 1.13): the camera looks at the face.
    camera.location = (0.0, -4.2, 1.1)
    camera.rotation_euler = (1.5708, 0.0, 0.0)
    scene.camera = camera
    scene.render.resolution_x, scene.render.resolution_y = 1080, 1920
    return camera


def new_character(profile, library_file, use_gh3_body=False, name=None, character_library_file=None):
    """Make a character scene for a profile: the rig from the library, the helpers, and the clip list. With
    use_gh3_body, the GH3 body of the library becomes the character mesh (a start for an edit). A profile that is not
    a fixed profile is a profile of the character library (character_library_file, else the preference or the file
    next to the GH3 library)."""
    clib = None
    if profile in character.PROFILES:
        role = role_of(profile)
    else:
        path = character_library_file or camera_tools.find_character_library()
        if not path: raise ValueError(f"{profile} is not a fixed profile, and there is no GH3 character library (gh3-characters-v1.chlib)")
        clib = character_library(path)
        if profile not in clib.profiles: raise ValueError(f"the character library {clib.id} has no profile {profile}")
        role = clib.role(profile)
    lib = library(library_file)
    scene = bpy.data.scenes.new(name or f"CH Character ({role})")
    scene.render.fps, scene.render.fps_base = CLIP_FPS, 1.0
    p = props(scene)
    p.enabled, p.profile, p.library = True, character.ROLE_PROFILES[role], str(Path(library_file).resolve())
    package = scene.chstage.package
    package.package_id, package.name = f"author.my-{role}", f"My {role.title()}"
    package.preview_mode = "RENDER"
    author = package.authors.add()
    author.name = "Me"
    if clib is not None:
        p.library_profile, p.library_id, p.character_library = profile, clib.id, str(clib.path.resolve())
    description = character.complete_requirements({"requires": [], "minModVersion": ""}, p.library_id or character.LIBRARY_ID)
    for row in description["requires"]:
        requirement = package.requires.add()
        requirement.package_id, requirement.version = row["id"], row["version"]
    package.min_mod_version = description["minModVersion"]
    collection = bpy.data.collections.new("Character")
    scene.collection.children.link(collection)
    helpers = bpy.data.collections.new("Character Helpers")
    scene.collection.children.link(helpers)
    parts = helper_parts(scene, lib, role)
    new, gltf, binary, nodes, mats, images = import_glb(scene, parts["body"], collection, mapped=use_gh3_body)
    rig = next(o for o in new if o.type == "ARMATURE")
    rig.name = f"{role}-rig"
    p.rig = rig
    meshes = [o for o in new if o.type == "MESH"]
    if use_gh3_body:
        package_open.open_nodes(gltf, nodes)
        package_open.open_materials(gltf, binary, mats, images, _NoFiles(), _Ignore())
        for name in parts["hairMaterials"]:
            for material in mats_by_name(gltf, mats).get(name, []): p.hair_materials.add().material = material
    else:
        for obj in meshes: bpy.data.objects.remove(obj, do_unlink=True)
    load_helpers(scene, helpers, lib, role, show_reference=not use_gh3_body)
    load_profile_data(scene, lib, role)
    add_preview_camera(scene, helpers)
    return scene


class _NoFiles:
    set = frozenset()

    def read(self, name): raise KeyError(name)


class _Ignore:
    def warn(self, *args, **kwargs): pass

    def error(self, *args, **kwargs): pass


def mats_by_name(gltf, mats):
    result = {}
    for index, variants in mats.items(): result.setdefault(gltf["materials"][index].get("name", ""), []).extend(variants)
    return result


# Weights.
def profile_groups(rig):
    return [bone.name for bone in rig.data.bones]


def vertex_weights(obj, names):
    """Dense weights (vertices, len(names)) of the vertex groups with these names."""
    index = {group.index: names.index(group.name) for group in obj.vertex_groups if group.name in names}
    weights = np.zeros((len(obj.data.vertices), len(names)))
    for vertex in obj.data.vertices:
        for element in vertex.groups:
            column = index.get(element.group)
            if column is not None: weights[vertex.index, column] = element.weight
    return weights


def set_vertex_weights(obj, names, weights, vertices=None):
    """Write dense weights into the vertex groups (the named groups of the given vertices are replaced)."""
    vertices = np.arange(len(obj.data.vertices)) if vertices is None else np.asarray(vertices)
    groups = {}
    for column, name in enumerate(names):
        group = obj.vertex_groups.get(name)
        if group is None:
            if not weights[:, column].any(): continue
            group = obj.vertex_groups.new(name=name)
        groups[column] = group
        group.remove([int(v) for v in vertices])
    for row, vertex in enumerate(vertices):
        for column in np.flatnonzero(weights[row]):
            groups[column].add([int(vertex)], float(weights[row, column]), "REPLACE")


def fit_to_rig(obj, rig):
    """Apply the object transform to the mesh and make the rig its parent, with an Armature modifier."""
    if obj.parent != rig or obj.matrix_world != rig.matrix_world:
        world = obj.matrix_world.copy()
        relative = rig.matrix_world.inverted() @ world
        mesh = obj.data
        try:
            mesh.transform(relative, shape_keys=True)
        except TypeError:
            mesh.transform(relative)
        obj.parent = rig
        obj.matrix_parent_inverse = Matrix.Identity(4)
        obj.matrix_basis = Matrix.Identity(4)
    modifier = next((m for m in obj.modifiers if m.type == "ARMATURE"), None)
    if modifier is None: modifier = obj.modifiers.new("Armature", "ARMATURE")
    modifier.object = rig
    modifier.use_vertex_groups = True


def triangles(obj, exclude_materials=()):
    """World vertex positions and the triangles (vertex indices) of a mesh, without the faces of some materials."""
    mesh = obj.data
    mesh.calc_loop_triangles()
    count = len(mesh.vertices)
    co = np.empty(count * 3)
    mesh.vertices.foreach_get("co", co)
    matrix = np.array(obj.matrix_world)
    co = co.reshape(-1, 3) @ matrix[:3, :3].T + matrix[:3, 3]
    tris = np.empty(len(mesh.loop_triangles) * 3, dtype=np.int64)
    mesh.loop_triangles.foreach_get("vertices", tris)
    tris = tris.reshape(-1, 3)
    materials = np.empty(len(mesh.loop_triangles), dtype=np.int64)
    mesh.loop_triangles.foreach_get("material_index", materials)
    excluded = [i for i, slot in enumerate(obj.material_slots) if slot.material is not None and _base_name(slot.material.name) in exclude_materials]
    if excluded: tris = tris[~np.isin(materials, excluded)]
    return co, tris


def _base_name(name):
    import re
    return re.sub(r"(\.\d{3,})+$", "", name)


def barycentric(point, a, b, c):
    v0, v1, v2 = b - a, c - a, point - a
    d00, d01, d11, d20, d21 = v0 @ v0, v0 @ v1, v1 @ v1, v2 @ v0, v2 @ v1
    denominator = d00 * d11 - d01 * d01
    if abs(denominator) < 1e-20: return np.array([1.0, 0.0, 0.0])
    v = (d11 * d20 - d01 * d21) / denominator
    w = (d00 * d21 - d01 * d20) / denominator
    result = np.clip([1 - v - w, v, w], 0, 1)
    return result / result.sum()


def transfer_weights(scene, target, source=None, exclude_hair=True):
    """Weights of the reference body on the target mesh: for each vertex, the nearest point on the source surface,
    interpolated from the three corners of its triangle (Blender Data Transfer 'Nearest Face Interpolated'). Then
    four influences at most and a weight sum of 1. Returns (vertices, vertices without weight)."""
    p = props(scene)
    rig = p.rig
    source = source or p.reference
    if rig is None or source is None: raise ValueError("the scene has no rig or no reference body")
    names = profile_groups(rig)
    exclude = set(json.loads(p.hair_reference or "[]")) if exclude_hair else set()
    co, tris = triangles(source, exclude)
    if not len(tris): raise ValueError("the reference body has no triangles")
    source_weights = vertex_weights(source, names)
    tree = BVHTree.FromPolygons([tuple(v) for v in co], [tuple(int(i) for i in t) for t in tris])
    fit_to_rig(target, rig)
    mesh = target.data
    count = len(mesh.vertices)
    target_co = np.empty(count * 3)
    mesh.vertices.foreach_get("co", target_co)
    matrix = np.array(target.matrix_world)
    target_co = target_co.reshape(-1, 3) @ matrix[:3, :3].T + matrix[:3, 3]
    weights = np.zeros((count, len(names)))
    for index, point in enumerate(target_co):
        location, _, face, _ = tree.find_nearest(Vector(point))
        if face is None: continue
        corners = tris[face]
        bary = barycentric(np.array(location), *co[corners])
        weights[index] = bary @ source_weights[corners]
    weights = character.limit_weights(weights)
    for group in [g for g in target.vertex_groups if g.name in names]: target.vertex_groups.remove(group)
    set_vertex_weights(target, names, weights)
    return count, int((weights.sum(1) == 0).sum())


def limit_and_normalize(obj, rig):
    names = profile_groups(rig)
    weights = character.limit_weights(vertex_weights(obj, names))
    set_vertex_weights(obj, names, weights)
    return int((weights.sum(1) == 0).sum())


def hair_bones(scene, chain="ALL"):
    chains = json.loads(props(scene).hair_chains or "[]")
    if chain == "ALL": return [name for c in chains for name in c]
    return chains[int(chain)] if chain.isdigit() and int(chain) < len(chains) else []


def assign_hair(scene, obj, chain="ALL", selected_only=True):
    """Give each vertex (the selected vertices, or all) full weight on the nearest hair bone segment of the chain.
    Returns the count of changed vertices."""
    rig = props(scene).rig
    bones = [rig.data.bones[name] for name in hair_bones(scene, chain) if name in rig.data.bones]
    if not bones: raise ValueError("the profile has no hair bones for this choice")
    fit_to_rig(obj, rig)
    if obj.mode == "EDIT": obj.update_from_editmode()
    vertices = [v.index for v in obj.data.vertices if v.select] if selected_only else []
    if not vertices: vertices = list(range(len(obj.data.vertices)))
    names = profile_groups(rig)
    world = np.array(rig.matrix_world)
    segments = []
    for bone in bones:
        child = next((c for c in bone.children if c in bones), None)
        head = np.array(bone.head_local)
        tail = np.array(child.head_local if child is not None else bone.tail_local)
        segments.append((names.index(bone.name), head @ world[:3, :3].T + world[:3, 3], tail @ world[:3, :3].T + world[:3, 3]))
    matrix = np.array(obj.matrix_world)
    weights = np.zeros((len(vertices), len(names)))
    for row, index in enumerate(vertices):
        point = np.array(obj.data.vertices[index].co) @ matrix[:3, :3].T + matrix[:3, 3]
        best, column = None, 0
        for joint, head, tail in segments:
            axis = tail - head
            t = 0.0 if not axis.any() else float(np.clip((point - head) @ axis / (axis @ axis), 0, 1))
            distance = np.linalg.norm(point - (head + t * axis))
            if best is None or distance < best: best, column = distance, joint
        weights[row, column] = 1.0
    set_vertex_weights(obj, names, weights, vertices)
    return len(vertices)


# Clip preview.
def decompress(data):
    try:
        import brotli
        return brotli.decompress(data)
    except ImportError:
        return brotli_decode.decompress(data)


def clip_actions():
    return [a for a in bpy.data.actions if a.get(PREVIEW)]


def new_fcurve(action, obj, path, index, group):
    if hasattr(action, "fcurve_ensure_for_datablock"):
        return action.fcurve_ensure_for_datablock(obj, path, index=index, group_name=group)
    return action.fcurves.new(path, index=index, action_group=group)


def matrix_quaternions(matrices):
    """[w, x, y, z] quaternions of rotation matrices [..., 3, 3]."""
    return np.roll(character.matrix_quaternions(matrices), 1, axis=-1)


def clip_basis(rig_object, rig_data, translations, quaternions):
    """Pose bone locations (frames, bones, 3) and quaternions (frames, bones, 4, [w, x, y, z]) of glTF local poses,
    for the bones of the rig object (in rig_data joint order)."""
    world = character.world_poses(rig_data, translations, quaternions)
    to_blender = AXES @ world @ np.linalg.inv(AXES)
    rest = AXES @ rig_data.world @ np.linalg.inv(AXES)
    bones = rig_object.data.bones
    bone_rest = np.array([np.array(bones[name].matrix_local) for name in rig_data.names])
    # The importer turns the bone axes (bone heuristic BLENDER); the turn is constant for each bone.
    correction = np.linalg.inv(rest) @ bone_rest
    pose = to_blender @ correction
    basis = np.empty_like(pose)
    index = {name: i for i, name in enumerate(rig_data.names)}
    for joint, name in enumerate(rig_data.names):
        parent = bones[name].parent
        if parent is None:
            basis[:, joint] = np.linalg.inv(bone_rest[joint]) @ pose[:, joint]
        else:
            p = index[parent.name]
            basis[:, joint] = np.linalg.inv(bone_rest[joint]) @ bone_rest[p] @ np.linalg.inv(pose[:, p]) @ pose[:, joint]
    rotation = basis[..., :3, :3] / np.linalg.norm(basis[..., :3, :3], axis=-2, keepdims=True)
    q = matrix_quaternions(rotation)
    for frame in range(1, len(q)):
        flip = np.sum(q[frame] * q[frame - 1], -1) < 0
        q[frame, flip] *= -1
    return basis[..., :3, 3], q


def clip_action(scene, clip):
    """Make (or reuse) the action of a library clip on the rig of the scene and show it. Returns the action."""
    p = props(scene)
    rig = p.rig
    role = role_of(p.profile)
    clib, profile = library_profile(scene)
    name = f"{profile or role} clip {clip}"
    for action in clip_actions():
        if action.get(PREVIEW) != name: bpy.data.actions.remove(action)
    action = next((a for a in clip_actions() if a.get(PREVIEW) == name), None)
    rig.animation_data_create()
    if clib is not None:
        decoded, rig_data, offsets = q48.decode(decompress(clib.clip(clip))), clib.rig(profile), clib.clip_offsets(profile)
    else:
        lib = library(library_path(scene))
        decoded, rig_data, offsets = q48.decode(decompress(lib.clip(role, clip))), profile_rig(lib, role), None
    frames = decoded[0]["frames"]
    if action is None:
        missing = [n for n in rig_data.names if n not in rig.data.bones]
        if missing: raise ValueError(f"the rig has no bone {missing[0]} of the profile")
        translations, quaternions = character.clip_local_poses(decoded, rig_data, role, offsets)
        locations, rotations = clip_basis(rig, rig_data, translations, quaternions)
        action = bpy.data.actions.new(name)
        action[PREVIEW] = name
        action["chstage_clip"] = clip
        rig.animation_data.action = action
        keys = np.arange(frames, dtype=np.float64)
        for joint, bone in enumerate(rig_data.names):
            pose_bone = rig.pose.bones[bone]
            pose_bone.rotation_mode = "QUATERNION"
            for path, values in (("location", locations[:, joint]), ("rotation_quaternion", rotations[:, joint])):
                for axis in range(values.shape[1]):
                    curve = new_fcurve(action, rig, f'pose.bones["{bone}"].{path}', axis, bone)
                    curve.keyframe_points.add(frames)
                    curve.keyframe_points.foreach_set("co", np.column_stack([keys, values[:, axis]]).reshape(-1))
                    curve.keyframe_points.foreach_set("interpolation", [1] * frames)
                    curve.update()
    else:
        rig.animation_data.action = action
        if hasattr(rig.animation_data, "action_slot") and len(getattr(action, "slots", [])):
            rig.animation_data.action_slot = action.slots[0]
    scene.render.fps, scene.render.fps_base = CLIP_FPS, 1.0
    scene.frame_start, scene.frame_end = 0, max(1, frames - 1)
    scene.frame_set(0)
    p.clip = clip
    return action


def clear_pose(scene):
    p = props(scene)
    rig = p.rig
    if rig is not None:
        if rig.animation_data: rig.animation_data.action = None
        for bone in rig.pose.bones:
            bone.location = (0, 0, 0)
            bone.rotation_quaternion = (1, 0, 0, 0)
            bone.rotation_euler = (0, 0, 0)
            bone.scale = (1, 1, 1)
    for action in clip_actions(): bpy.data.actions.remove(action)
    p.clip = ""
    p["clips_index"] = -1


def preview_clip_index(scene):
    p = props(scene)
    if 0 <= p.clips_index < len(p.clips) and p.rig is not None:
        clip_action(scene, p.clips[p.clips_index].clip)
