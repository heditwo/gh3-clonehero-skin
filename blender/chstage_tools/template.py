"""The minimal stage template: the example.studio layout of pipeline/stagepack/template.py, made from Blender data."""

import math

import bpy
from mathutils import Matrix, Quaternion, Vector

from . import sidecars
from .vendor.package_format import container

# A new stage has no scripts. The file is a text block (a kept package file), so creators can add script definitions.
MINIMAL_SCRIPTS = {"version": 1, "definitions": {}}

BOX_FACES = [(0, 1, 2, 3), (4, 7, 6, 5), (0, 4, 5, 1), (1, 5, 6, 2), (2, 6, 7, 3), (3, 7, 4, 0)]
# Runtime positions of the example.studio pack; Blender position = (x, z, y).
BAND = {"guitarist": (2, 0, 0), "bassist": (-3, 0, 0), "singer": (0, 0, -0.5), "drummer": (0, 0, 3)}
SCENERY = [("floor", (0, -0.15, 1), (18, 0.3, 16), (70, 80, 100)), ("backdrop", (0, 3, 8), (18, 6, 0.4), (40, 30, 75)),
           ("left", (-7, 2, 5), (0.8, 4, 0.8), (30, 180, 220)), ("right", (7, 2, 5), (0.8, 4, 0.8), (230, 70, 150))]


def blender(runtime):
    x, y, z = runtime
    return Vector((x, z, y))


def box_mesh(name, size):
    sx, sy, sz = (s / 2 for s in size)
    vertices = [(-sx, -sy, -sz), (sx, -sy, -sz), (sx, sy, -sz), (-sx, sy, -sz), (-sx, -sy, sz), (sx, -sy, sz), (sx, sy, sz), (-sx, sy, sz)]
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(vertices, [], BOX_FACES)
    mesh.uv_layers.new(name="UVMap")
    mesh.update()
    return mesh


def material(name, color):
    result = bpy.data.materials.new(name)
    result.use_nodes = True
    principled = next((n for n in result.node_tree.nodes if n.type == "BSDF_PRINCIPLED"), None)
    if principled is not None:
        principled.inputs["Base Color"].default_value = (*[(c / 255) ** 2.2 for c in color], 1)
    result.diffuse_color = (*[(c / 255) ** 2.2 for c in color], 1)
    return result


def proxy_body_mesh():
    """A 1.8 m body with a nose toward the audience (runtime -Z, Blender -Y). Preview only."""
    mesh = bpy.data.meshes.get("chstage-proxy-body")
    if mesh is not None: return mesh
    vertices, faces = [], []
    for z0, z1, r in ((0, 1.45, 0.25), (1.5, 1.8, 0.12)):
        base = len(vertices)
        for z in (z0, z1):
            for i in range(8):
                a = i * math.pi / 4
                vertices.append((math.cos(a) * r, math.sin(a) * r, z))
        faces += [(base + i, base + (i + 1) % 8, base + 8 + (i + 1) % 8, base + 8 + i) for i in range(8)]
        faces += [tuple(base + i for i in range(8))[::-1], tuple(base + 8 + i for i in range(8))]
    base = len(vertices)
    vertices += [(-0.05, -0.1, 1.6), (0.05, -0.1, 1.6), (0, -0.25, 1.62)]
    faces.append((base, base + 1, base + 2))
    mesh = bpy.data.meshes.new("chstage-proxy-body")
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    return mesh


def link(scene, obj, collection=None):
    (collection or scene.collection).objects.link(obj)
    return obj


def add_band_marker(scene, role, location, rotation=None, collection=None):
    marker = link(scene, bpy.data.objects.new(f"band-{role}", None), collection)
    marker.empty_display_type = "ARROWS"
    marker.empty_display_size = 0.5
    marker.location = location
    if rotation is not None: marker.rotation_euler = rotation
    marker.chstage.marker = "BAND"
    marker.chstage.band_role = role
    proxy = link(scene, bpy.data.objects.new(f"proxy-{role}", proxy_body_mesh()), collection)
    proxy.parent = marker
    proxy.chstage.marker = "PROXY"
    proxy.display_type = "WIRE"
    proxy.hide_select = True
    proxy.hide_render = True
    return marker


def add_waypoint(scene, name, location, collection=None):
    marker = link(scene, bpy.data.objects.new(name, None), collection)
    marker.empty_display_type = "SINGLE_ARROW"
    marker.location = location
    marker.chstage.marker = "WAYPOINT"
    return marker


def add_effect(scene, kind, location, collection=None):
    marker = link(scene, bpy.data.objects.new(f"effect-{kind}", None), collection)
    marker.empty_display_type = "CONE"
    marker.empty_display_size = 0.3
    marker.location = location
    marker.chstage.marker = "EFFECT"
    marker.chstage.effect = kind
    return marker


def add_shot(scene, name, location, target, fov, groups, anchor="world", look_at=("none", "head")):
    data = bpy.data.cameras.new(name)
    data.sensor_fit = "HORIZONTAL"
    data.angle = math.radians(fov)
    camera = link(scene, bpy.data.objects.new(name, data))
    camera.location = location
    camera.rotation_mode = "QUATERNION"
    camera.rotation_quaternion = (Vector(target) - Vector(location)).to_track_quat("-Z", "Y")
    shot = camera.chstage.shot
    shot.enabled = True
    for group in groups: shot.groups.add().name = group
    point = camera.chstage.point
    point.anchor_role, _, part = anchor.partition(".")
    if part: point.anchor_part = part
    point.look_at_role, point.look_at_part = look_at
    return camera


def add_light(scene, name, group, kind, intensity):
    light = link(scene, bpy.data.objects.new(name, bpy.data.lights.new(name, "POINT")))
    props = light.chstage.light
    props.enabled = True
    props.group = group
    props.type = kind
    props.intensity = intensity
    return light


def snapshot(scene, name, lights, beam=None):
    entry = scene.chstage.snapshots.add()
    entry.name = name
    for light in lights:
        row = entry.lights.add()
        row.light = light
        row.color = light.data.color
        row.intensity = light.chstage.light.intensity
        row.near = light.chstage.light.near
        row.far = light.chstage.light.far
    if beam is not None:
        row = entry.beams.add()
        row.fixture_id, row.target, location = beam
        row.position = location
        row.rotation = Quaternion()
        row.color = (0.1, 0.6, 1)
        row.density, row.start_radius, row.end_radius, row.range = 0.6, 0.1, 1.8, 5
    return entry


def create(name="CH Stage"):
    """Make a new scene with the template stage. Returns the scene."""
    scene = bpy.data.scenes.new(name)
    scene.render.resolution_x, scene.render.resolution_y = 1920, 1080
    scene.render.fps, scene.render.fps_base = 60, 1.0
    package = scene.chstage.package
    package.package_id, package.name, package.version = "author.my-stage", "My Stage", "1.0.0"
    package.authors.add().name = "Your name"
    requirement = package.requires.add()
    requirement.package_id, requirement.version = sidecars.LIBRARY_ID, "1.0.0"
    scripts = scene.chstage.kept_files.add()
    scripts.path, scripts.entry = "scripts.json", "scripts"
    scripts.text = bpy.data.texts.new(f"{name}/scripts.json")
    scripts.text.from_string(container.json_bytes(MINIMAL_SCRIPTS).decode())

    for item, center, size, color in SCENERY:
        obj = link(scene, bpy.data.objects.new(item, box_mesh(item, (size[0], size[2], size[1]))))
        obj.location = blender(center)
        obj.data.materials.append(material(item, color))
    housing = link(scene, bpy.data.objects.new("moving-light", box_mesh("moving-light", (0.35, 0.6, 0.35))))
    housing.location = blender((-5, 5, 3))
    housing.data.materials.append(material("fixture", (90, 90, 100)))
    housing.chstage.fixture_id = "left-beam"

    for role, position in BAND.items(): add_band_marker(scene, role, blender(position))
    add_waypoint(scene, "guitarist_walk01", blender((4, 0, 0)))
    for kind in sidecars.EFFECTS:
        for x in (-6, 6): add_effect(scene, kind, blender((x, 0, 0)))

    guitarist = blender(BAND["guitarist"])
    add_shot(scene, "wide", blender((0, 3, -12)), blender(BAND["singer"]) + Vector((0, 0, 1.6)), 72, ["wide", "default"],
             look_at=("singer", "head"))
    add_shot(scene, "guitar", guitarist + blender((-1, 1.5, -2.5)), guitarist + Vector((0, 0, 1.6)), 68, ["guitarist", "default"],
             anchor="guitarist.root", look_at=("guitarist", "head"))
    scene.camera = scene.objects["wide"]

    stage = add_light(scene, "Stage", "Stage", "Ambient", 0.65)
    band = add_light(scene, "Band", "Band", "Ambient", 0.65)
    beam = link(scene, bpy.data.objects.new("left-beam", bpy.data.lights.new("left-beam", "SPOT")))
    beam.location = housing.location
    snapshot(scene, "fill", [stage, band], ("left-beam", beam, blender((-5, 5, 3))))
    snapshot(scene, "chorus", [stage, band], ("left-beam", beam, blender((5, 5, 3))))
    for mood, names in (("intro", ["fill"]), ("blackout", ["fill"]), ("chorus", ["chorus", "fill"])):
        row = scene.chstage.moods.add()
        row.mood = mood
        for item in names: row.snapshots.add().name = item
    return scene


def capture_snapshot(snapshot_entry, scene):
    """Store the current stage light and beam object settings in a snapshot."""
    known = {row.light for row in snapshot_entry.lights if row.light}
    for obj in scene.objects:
        if obj.type == "LIGHT" and obj.chstage.light.enabled and obj not in known:
            snapshot_entry.lights.add().light = obj
    for row in snapshot_entry.lights:
        if row.light is None or row.light.type != "LIGHT": continue
        props = row.light.chstage.light
        row.color, row.intensity, row.near, row.far = row.light.data.color, props.intensity, props.near, props.far
    for beam in snapshot_entry.beams:
        if beam.target is None: continue
        location, rotation, _ = beam.target.matrix_world.decompose()
        beam.position, beam.rotation = location, rotation


def apply_snapshot(snapshot_entry):
    """Set the stage light and beam objects to the settings of a snapshot."""
    for row in snapshot_entry.lights:
        if row.light is None or row.light.type != "LIGHT": continue
        props = row.light.chstage.light
        row.light.data.color = row.color
        props.intensity, props.near, props.far = row.intensity, row.near, row.far
    for beam in snapshot_entry.beams:
        if beam.target is None: continue
        beam.target.matrix_world = Matrix.LocRotScale(Vector(beam.position), Quaternion(beam.rotation), beam.target.matrix_world.to_scale())
