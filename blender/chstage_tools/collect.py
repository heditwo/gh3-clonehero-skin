"""Read the Blender scene into the plain descriptions of sidecars.py, export the stage GLB, and validate the result."""

import json
import math
import re
import tempfile
from pathlib import Path

import bpy

from . import gltf_io, sidecars
from .vendor.package_format.validate import Validator


def matrix(obj):
    return [list(row) for row in obj.matrix_world]


def is_marker(obj):
    return obj.chstage.marker != "NONE"


def scene_objects(scene):
    return [obj for obj in scene.objects if obj.chstage.marker != "PROXY"]


def package_description(scene):
    p = scene.chstage.package
    return {
        "id": p.package_id, "version": p.version, "name": p.name, "description": p.description,
        "authors": [{"name": a.name, "role": a.role, "url": a.url} for a in p.authors],
        "license": p.license, "permissions": {"redistribute": p.redistribute, "modify": p.modify, "commercial": p.commercial},
        "credits": [c.name for c in p.credits if c.name], "homepage": p.homepage,
        "requires": [{"id": r.package_id, "version": r.version} for r in p.requires], "minModVersion": p.min_mod_version,
    }


def markers(scene):
    result = []
    for obj in scene_objects(scene):
        props = obj.chstage
        if props.marker in ("BAND", "WAYPOINT", "EFFECT"):
            result.append({"object": obj.name, "kind": props.marker, "role": props.band_role,
                           "name": props.waypoint_name or obj.name, "effect": props.effect, "matrix": matrix(obj)})
    return result


def horizontal_fov(obj, scene):
    if obj.type != "CAMERA": return 60.0
    camera = obj.data
    aspect = (scene.render.resolution_x * scene.render.pixel_aspect_x) / max(1, scene.render.resolution_y * scene.render.pixel_aspect_y)
    fit = camera.sensor_fit
    if fit == "AUTO": fit = "HORIZONTAL" if aspect >= 1 else "VERTICAL"
    if fit == "HORIZONTAL": return math.degrees(camera.angle_x)
    return math.degrees(2 * math.atan(math.tan(camera.angle_y / 2) * aspect))


def point_description(obj, scene):
    point = obj.chstage.point
    look_at = None if point.look_at_role == "none" else sidecars.anchor_name(point.look_at_role, point.look_at_part, point.look_at_bone)
    return {"object": obj.name, "matrix": matrix(obj), "fov": horizontal_fov(obj, scene),
            "anchor": sidecars.anchor_name(point.anchor_role, point.anchor_part, point.anchor_bone), "lookAt": look_at,
            "screenOffset": list(point.screen_offset) if point.use_screen_offset else None}


def shots(scene, issues):
    result = []
    for obj in scene_objects(scene):
        shot = obj.chstage.shot
        if obj.type != "CAMERA" or not shot.enabled: continue
        points = [point_description(obj, scene)]
        for ref in shot.more_points:
            if ref.target is None: issues.error(f"shot '{shot.shot_name or obj.name}' has an empty point", obj.name); continue
            points.append(point_description(ref.target, scene))
        result.append({
            "object": obj.name, "name": shot.shot_name or obj.name, "type": shot.type, "control": shot.control, "tag": shot.tag,
            "groups": [g.name for g in shot.groups if g.name], "waypoints": [w.name for w in shot.waypoints if w.name],
            "times": [t.value for t in shot.times], "interpolation": None if shot.interpolation == "default" else shot.interpolation,
            "orbitAngle": shot.orbit_angle if shot.use_orbit_angle else None,
            "angularSpeed": shot.angular_speed if shot.use_angular_speed else None,
            "handCam": shot.hand_cam, "disableOrbit1": shot.disable_orbit1, "disableOrbit2": shot.disable_orbit2, "points": points})
    return result


def camera_notes(scene):
    return [{"note": n.note, "group": n.group, "tag": n.tag, "type": n.type, "enable": n.enable if n.use_enable else None}
            for n in scene.chstage.camera_notes]


def light_type(obj, issues):
    props = obj.chstage.light
    if props.type != "AUTO": return props.type
    if obj.data.type == "AREA": issues.warn("area lights export as point lights", obj.name)
    return sidecars.BLENDER_LIGHT_TYPES[obj.data.type]


def lights(scene, issues):
    result = []
    for obj in scene_objects(scene):
        if obj.type != "LIGHT" or not obj.chstage.light.enabled: continue
        props = obj.chstage.light
        kind = light_type(obj, issues)
        row = {"source": obj.name, "id": props.light_id or obj.name, "object": props.script_object, "group": props.group, "type": kind,
               "matrix": matrix(obj), "overrideColor": props.override_color}
        if props.use_cone:
            row.update(hotspot=props.hotspot, field=props.field)
        elif kind == "Spot" and obj.data.type == "SPOT":
            field = math.degrees(obj.data.spot_size)
            row.update(hotspot=field * (1 - obj.data.spot_blend), field=field)
        result.append(row)
    return result


def snapshots(scene):
    return [{"name": s.name, "lights": [{"light": e.light.name if e.light else None, "color": list(e.color), "intensity": e.intensity,
                                          "near": e.near, "far": e.far, "inherit": e.inherit} for e in s.lights],
             "beams": [{"id": b.fixture_id, "position": list(b.position), "rotation": list(b.rotation), "color": list(b.color),
                        "density": b.density, "startRadius": b.start_radius, "endRadius": b.end_radius, "range": b.range,
                        "inherit": b.inherit} for b in s.beams]}
            for s in scene.chstage.snapshots]


def node_description(obj):
    props = obj.chstage
    return {"lightGroup": props.light_group, "visibilityGroup": props.visibility_group, "initiallyVisible": props.initially_visible,
            "fixtureId": props.fixture_id, "fixtureFixedPosition": props.fixture_fixed_position, "fixtureObject": props.fixture_object,
            "fixtureCreated": props.fixture_created if props.use_fixture_created else None, "object": props.script_object,
            "pivot": props.use_object_pivot, "created": props.object_created if props.use_object_created else None,
            "script": props.object_script}


def material_description(material):
    props = material.chstage
    return {"shading": props.shading, "blend": props.blend, "alphaCutoff": props.alpha_cutoff, "blendCutoff": props.use_blend_cutoff,
            "twoSided": props.two_sided, "colorScale": props.color_scale, "crowdAtlas": props.crowd_atlas,
            "crowdRole": None if props.crowd_role == "NONE" else props.crowd_role, "billboard": props.billboard,
            "movieSlot": props.movie_slot, "sortedTransparency": props.sorted_transparency,
            "flipbook": {"columns": props.flipbook_columns, "rows": props.flipbook_rows, "fps": props.flipbook_fps,
                         "offset": props.flipbook_offset} if props.use_flipbook else None,
            "lightmap": props.lightmap.name if props.lightmap else None}


def sidecar_files(scene, issues):
    """Package path to JSON value for the scene, and the object names that each file refers to."""
    marker_list = markers(scene)
    light_list = lights(scene, issues)
    lighting = sidecars.build_lighting(light_list, snapshots(scene), [{"mood": m.mood, "snapshots": [s.name for s in m.snapshots]}
                                       for m in scene.chstage.moods], [{"name": c.name, "color": list(c.color)} for c in scene.chstage.colors], issues)
    files = {
        "manifest.json": sidecars.build_manifest(package_description(scene), lighting is not None, bool(scene.chstage.package.preview)),
        "placements.json": sidecars.build_placements(marker_list, [{"role": c.role, "model": c.model, "yaw": c.yaw} for c in scene.chstage.crowd], issues),
        "cameras.json": sidecars.build_cameras(shots(scene, issues), camera_notes(scene), marker_list, issues),
    }
    if lighting is not None: files["lighting.json"] = lighting
    names = {
        "placements.json": {m["name"] if m["kind"] == "WAYPOINT" else m["role"]: m["object"] for m in marker_list},
        "cameras.json": {s["name"]: s["object"] for s in shots(scene, sidecars.Issues())},
        "lighting.json": {light["id"]: light["source"] for light in light_list},
    }
    return files, names


# glTF export hook: the exporter calls these methods of glTF2ExportUserExtension (see __init__.py) for each node and material.
_EXPORT = {"active": False}


class ExportExtras:
    def __init__(self):
        self.Extension = None

    def gather_scene_hook(self, gltf2_scene, blender_scene, export_settings):
        extras = {k: v for k, v in (gltf2_scene.extras or {}).items() if k != "chstage"}
        gltf2_scene.extras = extras or None

    def gather_node_hook(self, gltf2_node, blender_object, export_settings):
        # The add-on settings are never extras, also in other glTF exports.
        extras = {k: v for k, v in (gltf2_node.extras or {}).items() if k != "chstage"}
        if _EXPORT["active"] and blender_object is not None and hasattr(blender_object, "chstage"):
            extras = {k: v for k, v in extras.items() if not k.startswith("gh3")}
            extras.update(sidecars.node_extras(node_description(blender_object), matrix(blender_object)))
        gltf2_node.extras = extras or None

    def gather_material_hook(self, gltf2_material, blender_material, export_settings):
        extras = {k: v for k, v in (gltf2_material.extras or {}).items() if k != "chstage"}
        gltf2_material.extras = extras or None
        if not _EXPORT["active"] or blender_material is None or not hasattr(blender_material, "chstage"): return
        values, fields = sidecars.material_fields(material_description(blender_material))
        extras = {k: v for k, v in extras.items() if not k.startswith("gh3")}
        extras.update(values)
        gltf2_material.extras = extras
        gltf2_material.alpha_mode = fields["alphaMode"]
        gltf2_material.alpha_cutoff = fields.get("alphaCutoff")
        gltf2_material.double_sided = fields["doubleSided"]


def exported_objects(scene):
    """Scenery for stage.glb: meshes and script-object empties; never markers, proxies, cameras, or lights."""
    result = []
    for obj in scene_objects(scene):
        if is_marker(obj) or obj.hide_render: continue
        if obj.type in ("MESH", "CURVE", "SURFACE", "META", "FONT") or (obj.type == "EMPTY" and obj.chstage.script_object):
            result.append(obj)
    return result


def export_stage_glb(scene, path):
    """Export the scenery with the safe glTF options and the gh3* extras. Returns the exported object names."""
    objects = exported_objects(scene)
    window = bpy.context.window
    previous_scene = window.scene if window else None
    if window: window.scene = scene
    view_layer = bpy.context.view_layer if bpy.context.scene == scene else scene.view_layers[0]
    selected = [obj for obj in scene.objects if obj.select_get(view_layer=view_layer)]
    try:
        for obj in scene.objects: obj.select_set(obj in objects, view_layer=view_layer)
        _EXPORT["active"] = True
        gltf_io.export_glb(path, fps=scene.render.fps, use_selection=True, export_cameras=False, export_lights=False,
                           export_apply=True, export_vertex_color="ACTIVE")
    finally:
        _EXPORT["active"] = False
        for obj in scene.objects: obj.select_set(obj in selected, view_layer=view_layer)
        if window and previous_scene: window.scene = previous_scene
    return [obj.name for obj in objects]


def resolve(message, names, scene):
    """(object name, material name) that a validator message refers to, or (None, None)."""
    file, _, text = message.partition(": ")
    if file == "stage.glb":
        node = re.search(r"node \d+ \(([^)]*)\)", text)
        if node and node.group(1) in scene.objects: return node.group(1), None
        material = re.search(r"material \d+ \(([^)]*)\)", text)
        if material and material.group(1) in bpy.data.materials:
            owner = next((obj.name for obj in scene.objects if any(s.material and s.material.name == material.group(1) for s in obj.material_slots)), None)
            return owner, material.group(1)
        fixture = re.search(r"fixture '([^']*)'", text)
        if fixture:
            owner = next((obj.name for obj in scene.objects if obj.chstage.fixture_id == fixture.group(1)), None)
            return owner, None
    for token in re.findall(r"'([^']+)'|\.([A-Za-z0-9_.-]+?)(?=[.\[:]|$)", text):
        token = token[0] or token[1]
        if token in names.get(file, {}): return names[file][token], None
    return None, None


def validate_scene(scene):
    """Run the add-on checks and the bundled package validator. Returns a list of (severity, message, object, material)."""
    issues = sidecars.Issues()
    files, names = sidecar_files(scene, issues)
    with tempfile.TemporaryDirectory(prefix="chstage-validate-") as folder:
        model = Path(folder) / "stage.glb"
        exported = export_stage_glb(scene, model)
        if not exported: issues.error("the stage has no scenery meshes")
        mapped = {path: json.dumps(value, ensure_ascii=False).encode() for path, value in files.items()}
        mapped["stage.glb"] = model.read_bytes()
        package = scene.chstage.package
        if package.preview:
            preview = Path(bpy.path.abspath(package.preview))
            if preview.is_file(): mapped["preview.png"] = preview.read_bytes()
            else: issues.error(f"preview image is missing: {package.preview}")
        report = Validator(f"{package.package_id or 'stage'}.chstage", files=mapped).run()
    known = {message for _, message, _, _ in issues.items}
    for severity, messages in (("ERROR", report.errors), ("WARNING", report.warnings)):
        for message in messages:
            if "'files' is empty" in message or message in known: continue
            obj, material = resolve(message, names, scene)
            issues.items.append((severity, message, obj, material))
    return issues.items
