"""Build the example stage and the example character with the CH Stage Tools add-on. Run inside Blender (examples/build.sh):

blender --background --factory-startup --python-exit-code 1 --python examples/build_examples.py -- ADDON_ZIP LIBRARY OUTPUT_DIR stage|character

All geometry and textures come from this script (Blender primitives and numpy patterns). Nothing comes from GH3 except
the fixed rig of the character profile, which the format requires. The .blend files hold no GH3 meshes: the script
removes the character helpers (the GH3 reference body and the guitar) before it saves.
"""

import math
import sys
from pathlib import Path

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Vector

args = sys.argv[sys.argv.index("--") + 1:]
archive, library, output = Path(args[0]), Path(args[1]), Path(args[2])
output.mkdir(parents=True, exist_ok=True)
MODULE = "bl_ext.user_default.chstage_tools"
AUTHOR = "CH Stage Tools contributors"
LICENSE = "CC0-1.0"


def fail(message):
    print(f"EXAMPLES FAIL {message}")
    sys.exit(1)


def run(scene, operator, **options):
    with bpy.context.temp_override(scene=scene, view_layer=scene.view_layers[0]):
        return operator(**options)


def errors(scene):
    return [(i.severity, i.message, i.object_name) for i in scene.chstage.issues if i.severity == "ERROR"]


def package_info(scene, package_id, name, description):
    package = scene.chstage.package
    package.package_id, package.name, package.version, package.description = package_id, name, "1.0.0", description
    package.license = LICENSE
    package.redistribute, package.modify, package.commercial = True, True, True
    package.authors[0].name = AUTHOR
    package.authors[0].role = "example"


def image(name, pixels):
    """A packed PNG image from an (h, w, 4) array of linear-free display values in 0..1 (row 0 is the bottom)."""
    height, width = pixels.shape[:2]
    result = bpy.data.images.new(name, width, height, alpha=True)
    result.pixels.foreach_set(np.clip(pixels, 0, 1).astype(np.float32).reshape(-1))
    result.file_format = "PNG"
    result.pack()
    return result


def noise(size, seed, scale=1.0):
    return np.random.default_rng(seed).random((size, size)) * scale


def planks(size=256):
    y, x = np.mgrid[0:size, 0:size] / size
    board = np.floor(x * 4)
    offset = (board * 0.37) % 1
    joint = ((x * 4) % 1 < 0.03) | (((y + offset) * 2) % 1 < 0.015)
    grain = 0.06 * np.sin((y + offset) * 90 + np.sin(x * 40 + board) * 2)
    tone = 0.30 + 0.05 * ((board * 7) % 3) + grain + noise(size, 1, 0.04)
    value = np.where(joint, 0.08, tone)
    return np.stack([value * 1.0, value * 0.72, value * 0.5, np.ones_like(x)], -1)


def bricks(size=256):
    y, x = np.mgrid[0:size, 0:size] / size
    row = np.floor(y * 8)
    shift = (row % 2) * 0.125
    mortar = ((y * 8) % 1 < 0.08) | (((x + shift) * 4) % 1 < 0.04)
    brick = np.floor((x + shift) * 4) + row * 5
    tone = 0.32 + 0.08 * np.sin(brick * 12.9898) ** 2 + noise(size, 2, 0.06)
    rgb = [np.where(mortar, 0.42, tone * 1.0), np.where(mortar, 0.40, tone * 0.45), np.where(mortar, 0.38, tone * 0.38)]
    return np.stack([*rgb, np.ones_like(x)], -1)


def concrete(size=128):
    value = 0.18 + noise(size, 3, 0.05)
    return np.stack([value, value, value * 1.05, np.ones_like(value)], -1)


def speaker(size=128):
    y, x = np.mgrid[0:size, 0:size] / size
    rings = []
    for cy, radius in ((0.72, 0.2), (0.28, 0.2)):
        distance = np.hypot(x - 0.5, y - cy)
        rings.append((distance < radius) * (0.12 + 0.1 * np.cos(distance * 120)))
    value = 0.05 + np.maximum(*rings) + noise(size, 4, 0.02)
    return np.stack([value, value, value, np.ones_like(x)], -1)


def truss(size=128):
    """A lattice with alpha: square frame and diagonals (cutout material)."""
    y, x = np.mgrid[0:size, 0:size] / size
    width = 0.06
    bars = (x < width) | (x > 1 - width) | (y < width) | (y > 1 - width) | (np.abs(x - y) < width * 0.8) | (np.abs(x + y - 1) < width * 0.8)
    value = 0.55 + 0.2 * y
    return np.stack([value, value, value * 1.05, bars.astype(float)], -1)


def textured(name, picture, blend="OPAQUE", shading="lit", two_sided=False):
    material = bpy.data.materials.new(name)
    material.use_nodes = True
    tree = material.node_tree
    shader = next(n for n in tree.nodes if n.type == "BSDF_PRINCIPLED")
    texture = tree.nodes.new("ShaderNodeTexImage")
    texture.image = picture
    tree.links.new(texture.outputs["Color"], shader.inputs["Base Color"])
    if blend != "OPAQUE": tree.links.new(texture.outputs["Alpha"], shader.inputs["Alpha"])
    material.chstage.blend, material.chstage.shading, material.chstage.two_sided = blend, shading, two_sided
    return material


def colored(name, color, shading="lit", blend="OPAQUE"):
    material = bpy.data.materials.new(name)
    material.use_nodes = True
    shader = next(n for n in material.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    shader.inputs["Base Color"].default_value = (*color, 1)
    material.diffuse_color = (*color, 1)
    material.chstage.shading, material.chstage.blend = shading, blend
    return material


def box_uv(mesh, tile):
    """Planar UVs for each face along its main axis: one texture repeat for each `tile` metres."""
    layer = mesh.uv_layers.new(name="UVMap")
    for polygon in mesh.polygons:
        axis = max(range(3), key=lambda i: abs(polygon.normal[i]))
        u, v = [(1, 2), (0, 2), (0, 1)][axis]
        for loop in polygon.loop_indices:
            co = mesh.vertices[mesh.loops[loop].vertex_index].co
            layer.data[loop].uv = (co[u] / tile, co[v] / tile)


def add_box(scene, collection, name, center, size, material, tile=2.0):
    """An axis-aligned box in Blender coordinates (Z up). The mesh holds the world positions so that UVs tile in metres."""
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    bmesh.ops.transform(bm, matrix=Matrix.Translation(center) @ Matrix.Diagonal((*size, 1)), verts=bm.verts)
    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()
    box_uv(mesh, tile)
    mesh.materials.append(material)
    obj = bpy.data.objects.new(name, mesh)
    collection.objects.link(obj)
    return obj


def add_star(scene, collection, name, center, radius, material):
    """A neon star outline from thin bars on the back wall (faces the audience: Blender -Y)."""
    bm = bmesh.new()
    points = []
    for i in range(10):
        angle = math.pi / 2 + i * math.pi / 5
        r = radius if i % 2 == 0 else radius * 0.45
        points.append(Vector((math.cos(angle) * r, 0, math.sin(angle) * r)))
    for a, b in zip(points, points[1:] + points[:1]):
        geometry = bmesh.ops.create_cube(bm, size=1.0)
        direction = b - a
        rotation = direction.normalized().to_track_quat("X", "Y").to_matrix().to_4x4()
        bmesh.ops.transform(bm, matrix=Matrix.Translation((a + b) / 2) @ rotation @ Matrix.Diagonal((direction.length + 0.08, 0.08, 0.08, 1)),
                            verts=geometry["verts"])
    bmesh.ops.translate(bm, vec=center, verts=bm.verts)
    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()
    mesh.uv_layers.new(name="UVMap")
    mesh.materials.append(material)
    obj = bpy.data.objects.new(name, mesh)
    collection.objects.link(obj)
    return obj


def remove_default_scene():
    for scene in [s for s in bpy.data.scenes if s.name == "Scene"]: bpy.data.scenes.remove(scene)
    bpy.data.orphans_purge(do_local_ids=True, do_linked_ids=True, do_recursive=True)


def save(path):
    bpy.data.orphans_purge(do_local_ids=True, do_linked_ids=True, do_recursive=True)
    bpy.ops.wm.save_as_mainfile(filepath=str(path), compress=True, relative_remap=True)
    print(f"EXAMPLES SAVED {path} {path.stat().st_size}")


def build_stage():
    with bpy.context.temp_override(scene=bpy.context.scene):
        bpy.ops.chstage.new_stage()
    scene = bpy.data.scenes["CH Stage"]
    scene.name = "Example Club"
    package_info(scene, "example.club", "Example Club",
                  "A small club stage made with the CH Stage Tools template. Example content for creators (docs/CREATOR-GUIDE.md).")
    scene.chstage.package.preview_mode = "RENDER"
    for name in ("floor", "backdrop", "left", "right"):
        obj = scene.objects[name]
        bpy.data.objects.remove(obj, do_unlink=True)

    scenery = bpy.data.collections.new("Scenery")
    scene.collection.children.link(scenery)
    wood = textured("stage-wood", image("stage-wood", planks()))
    brick = textured("brick-wall", image("brick-wall", bricks()))
    floor = textured("club-floor", image("club-floor", concrete()))
    grille = textured("speaker-grille", image("speaker-grille", speaker()))
    lattice = textured("truss", image("truss", truss()), blend="CUTOUT", two_sided=True)
    cabinet = colored("cabinet", (0.02, 0.02, 0.025))
    neon = colored("neon", (0.1, 0.9, 1.0), shading="unlit", blend="ADDITIVE")

    # Blender coordinates: X right, Y away from the audience, Z up. The template band stands at Z = 0.
    add_box(scene, scenery, "stage-deck", (0, 2.5, -0.4), (16, 10, 0.8), wood)
    add_box(scene, scenery, "club-floor", (0, -8, -1.0), (26, 12, 0.4), floor, tile=4)
    add_box(scene, scenery, "back-wall", (0, 8, 3.2), (26, 0.4, 9), brick)
    for side in (-1, 1):
        add_box(scene, scenery, f"side-wall-{'left' if side < 0 else 'right'}", (13 * side, -2, 3.2), (0.4, 20, 9), brick)
        for level, height in ((0, 1.6), (1, 1.2)):
            z = 0.8 + level * 1.6
            add_box(scene, scenery, f"speaker-{'left' if side < 0 else 'right'}-{level}-cabinet", (6.6 * side, 4.5, z), (1.4, 1.0, height), cabinet)
            add_box(scene, scenery, f"speaker-{'left' if side < 0 else 'right'}-{level}-front", (6.6 * side, 3.98, z), (1.3, 0.04, height * 0.92), grille, tile=height)
        add_box(scene, scenery, f"truss-{'left' if side < 0 else 'right'}", (7.5 * side, 6.5, 3.0), (0.4, 0.4, 6.0), lattice, tile=0.4)
    add_box(scene, scenery, "truss-top", (0, 6.5, 6.2), (15.4, 0.4, 0.4), lattice, tile=0.4)
    add_star(scene, scenery, "neon-star", Vector((0, 7.75, 4.4)), 1.6, neon)

    housing = scene.objects["moving-light"]
    housing.location = (-4, 6.2, 5.6)
    beam = scene.objects["left-beam"]
    beam.location = housing.location

    stage, band = scene.objects["Stage"], scene.objects["Band"]
    stage.location, band.location = (0, -2, 6), (0, 1, 4)
    looks = {"fill": {"Stage": ((1.0, 0.75, 0.45), 0.7), "Band": ((1.0, 0.95, 0.9), 0.8)},
             "chorus": {"Stage": ((0.9, 0.2, 1.0), 0.9), "Band": ((0.3, 0.9, 1.0), 1.0)}}
    for snapshot in scene.chstage.snapshots:
        for row in snapshot.lights:
            row.color, row.intensity = looks[snapshot.name][row.light.name]
        for row in snapshot.beams:
            row.color = (1.0, 0.8, 0.4) if snapshot.name == "fill" else (1.0, 0.2, 0.9)
    for light in (stage, band):
        light.data.color, light.chstage.light.intensity = looks["fill"][light.name]

    wide = scene.objects["wide"]
    wide.location = (0, -12, 3.2)
    wide.rotation_quaternion = (Vector((0, 2, 1.4)) - wide.location).to_track_quat("-Z", "Y")
    # The guitarist shot turns 22 degrees to the left of the guitarist, so the performer is to the right of the highway.
    guitar = scene.objects["guitar"]
    guitarist = scene.objects["band-guitarist"].location
    guitar.location = guitarist + Vector((0.4, -3.2, 1.5))
    aim = Matrix.Rotation(math.radians(22), 3, "Z") @ (guitarist + Vector((0, 0, 1.2)) - guitar.location)
    guitar.rotation_quaternion = aim.to_track_quat("-Z", "Y")
    guitar.chstage.point.look_at_role = "none"
    remove_default_scene()

    run(scene, bpy.ops.chstage.validate)
    if errors(scene): fail(f"stage validate: {errors(scene)}")
    target = output / "example.club.chstage"
    status = run(scene, bpy.ops.chstage.export_package, filepath=str(target))
    if status != {"FINISHED"}: fail(f"stage export: {[(i.severity, i.message) for i in scene.chstage.issues]}")
    print(f"EXAMPLES WROTE {target} {target.stat().st_size} warnings={len(scene.chstage.issues)}")
    scene.chstage.export_path = "//example.club.chstage"
    save(output / "example-club.blend")


def bone_head(rig, name):
    character = sys.modules[MODULE + ".vendor.package_format.character"]
    return Vector(rig.data.bones[f"bone-{character.gh3_key(name):08x}"].head_local)


def limb(bm, a, b, radius_a, radius_b, segments=12):
    direction = b - a
    geometry = bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=segments, radius1=radius_a, radius2=radius_b, depth=direction.length)
    rotation = direction.normalized().to_track_quat("Z", "Y").to_matrix().to_4x4()
    bmesh.ops.transform(bm, matrix=Matrix.Translation((a + b) / 2) @ rotation, verts=geometry["verts"])


def ball(bm, center, radius, scale=(1, 1, 1)):
    geometry = bmesh.ops.create_uvsphere(bm, u_segments=16, v_segments=10, radius=radius)
    bmesh.ops.transform(bm, matrix=Matrix.Translation(center) @ Matrix.Diagonal((*scale, 1)), verts=geometry["verts"])


def body_texture(size=128):
    """Skin on top, a red shirt in the middle, blue jeans at the bottom (V is the height)."""
    y, x = np.mgrid[0:size, 0:size] / (size - 1)
    stripes = (np.floor(x * 16) % 2) * 0.06
    skin = np.array([0.93, 0.72, 0.58])
    shirt = np.array([0.75, 0.12, 0.1])
    jeans = np.array([0.15, 0.25, 0.55])
    rgb = np.where((y > 0.74)[..., None], skin, np.where((y > 0.47)[..., None], shirt, jeans))
    rgb = rgb + stripes[..., None] * (y <= 0.74)[..., None] + noise(size, 5, 0.03)[..., None]
    return np.concatenate([rgb, np.ones((size, size, 1))], -1)


def hair_texture(size=64):
    y, x = np.mgrid[0:size, 0:size] / (size - 1)
    strands = 0.5 + 0.5 * np.sin(x * 60) ** 2
    alpha = np.clip((1 - np.abs(x - 0.5) * 2) * 3, 0, 1) * np.clip(y * 4, 0, 1)
    return np.stack([0.25 * strands, 0.14 * strands, 0.06 * strands, alpha], -1)


def build_character():
    with bpy.context.temp_override(scene=bpy.context.scene):
        status = bpy.ops.chstage.new_character(profile="gh3-guitarist-axel-v1", library=str(library))
    if status != {"FINISHED"}: fail("new character")
    scene = next(s for s in bpy.data.scenes if s.name.startswith("CH Character (guitarist)"))
    scene.name = "Example Guitarist"
    props = scene.chstage.character
    rig = props.rig
    package_info(scene, "example.guitarist", "Example Guitarist",
                  "A blocky guitarist made from Blender primitives on the GH3 guitarist rig. Example content for creators (docs/CREATOR-GUIDE.md).")
    collection = next(c for c in scene.collection.children if c.name.startswith("Character") and "Helpers" not in c.name)

    bm = bmesh.new()
    head_of = lambda name: bone_head(rig, name)
    pelvis, neck, head = (head_of(n) for n in ("bone_pelvis", "bone_neck", "bone_head"))
    limb(bm, pelvis + Vector((0, 0, -0.05)), neck, 0.17, 0.2, 16)
    ball(bm, head + Vector((0, -0.02, 0.1)), 0.13, (0.9, 1.0, 1.15))
    limb(bm, neck - Vector((0, 0, 0.05)), head + Vector((0, 0, 0.02)), 0.06, 0.055)
    for side in ("l", "r"):
        bicep, forearm, palm = (head_of(f"bone_{n}_{side}") for n in ("bicep", "forearm", "palm"))
        thigh, knee, ankle, toe = (head_of(f"bone_{n}_{side}") for n in ("thigh", "knee", "ankle", "toe"))
        collar = head_of(f"bone_collar_{side}")
        limb(bm, collar, bicep, 0.07, 0.065)
        ball(bm, bicep, 0.065)
        limb(bm, bicep, forearm + (forearm - bicep).normalized() * 0.03, 0.06, 0.05)
        limb(bm, forearm, palm + (palm - forearm).normalized() * 0.03, 0.05, 0.04)
        ball(bm, palm + (palm - forearm).normalized() * 0.06, 0.05)
        limb(bm, thigh, knee + (knee - thigh).normalized() * 0.03, 0.09, 0.07)
        ball(bm, knee, 0.068)
        limb(bm, knee, ankle, 0.065, 0.045)
        limb(bm, ankle, toe + (toe - ankle).normalized() * 0.05, 0.05, 0.04)
    mesh = bpy.data.meshes.new("body")
    bm.to_mesh(mesh)
    bm.free()
    top = max(v.co.z for v in mesh.vertices)
    layer = mesh.uv_layers.new(name="UVMap")
    for loop in mesh.loops:
        co = mesh.vertices[loop.vertex_index].co
        layer.data[loop.index].uv = (math.atan2(co.y, co.x) / (2 * math.pi) + 0.5, max(0.0, co.z) / top)
    body = bpy.data.objects.new("body", mesh)
    collection.objects.link(body)
    body.data.materials.append(textured("body", image("body-color", body_texture())))

    bm = bmesh.new()
    for index in range(7):
        angle = math.radians(-90 + index * 30)
        root = head + Vector((0.11 * math.sin(angle), 0.11 * math.cos(angle) * 0.6 + 0.04, 0.12))
        geometry = bmesh.ops.create_grid(bm, x_segments=1, y_segments=3, size=0.5)
        bmesh.ops.transform(bm, matrix=Matrix.Translation(root + Vector((0, 0.06, -0.13))) @ Matrix.Diagonal((0.09, 0.02, 0.3, 1))
                            @ Matrix.Rotation(math.radians(90), 4, "X"), verts=geometry["verts"])
    hair_mesh = bpy.data.meshes.new("hair")
    bm.to_mesh(hair_mesh)
    bm.free()
    layer = hair_mesh.uv_layers.new(name="UVMap")
    for polygon in hair_mesh.polygons:
        for loop in polygon.loop_indices:
            co = hair_mesh.vertices[hair_mesh.loops[loop].vertex_index].co
            layer.data[loop].uv = (0.5 + (co.x - polygon.center.x) / 0.09, 0.5 + (co.z - polygon.center.z) / 0.3)
    hair = bpy.data.objects.new("hair", hair_mesh)
    collection.objects.link(hair)
    hair_material = textured("hair", image("hair-color", hair_texture()), blend="BLEND", two_sided=True)
    hair.data.materials.append(hair_material)

    view_layer = scene.view_layers[0]
    for obj in scene.objects: obj.select_set(False, view_layer=view_layer)
    with bpy.context.temp_override(scene=scene, view_layer=view_layer, selected_objects=[body], active_object=body, object=body):
        if bpy.ops.chstage.transfer_weights() != {"FINISHED"}: fail("transfer weights")
    with bpy.context.temp_override(scene=scene, view_layer=view_layer, object=hair, active_object=hair, selected_objects=[hair]):
        if bpy.ops.chstage.assign_hair(selected_only=False) != {"FINISHED"}: fail("assign hair")
        bpy.ops.chstage.hair_material(action="ADD")
    run(scene, bpy.ops.chstage.validate_character)
    if errors(scene): fail(f"character validate: {errors(scene)}")
    target = output / "example.guitarist.chchar"
    status = run(scene, bpy.ops.chstage.export_character, filepath=str(target))
    if status != {"FINISHED"}: fail(f"character export: {[(i.severity, i.message) for i in scene.chstage.issues]}")
    print(f"EXAMPLES WROTE {target} {target.stat().st_size} warnings={len(scene.chstage.issues)}")

    # The .blend must not hold GH3 meshes or a local path: Helpers > Reload loads them from the creator's library.
    sys.modules[MODULE + ".character_tools"].remove_helpers(scene)
    props.library = ""
    props.clips.clear()
    scene.chstage.export_path = "//example.guitarist.chchar"
    for other in [s for s in bpy.data.scenes if s != scene]: bpy.data.scenes.remove(other)
    save(output / "example-guitarist.blend")


result = bpy.ops.extensions.package_install_files(filepath=str(archive), repo="user_default", enable_on_install=True)
if result != {"FINISHED"} or MODULE not in bpy.context.preferences.addons: fail(f"install {result}")
{"stage": build_stage, "character": build_character}[args[3]]()
print("EXAMPLES DONE")
