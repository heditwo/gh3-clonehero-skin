"""Open a venue package: rebuild the Blender scene and the add-on settings from stage.glb and the JSON sidecars."""

import hashlib
import json
import math
import os
import tempfile
from pathlib import Path

import bpy
from mathutils import Matrix

from . import collect, glb_tools, gltf_io, sidecars, template
from .vendor.package_format.validate import Report, Source, Validator

ADDON_FILES = ("model", "placements", "cameras", "lighting")
JSON_SUFFIX = ".json"

# glTF import hooks (glTF2ImportUserExtension in __init__.py): map glTF indices to the Blender data that the importer makes.
_OPEN = {"active": False, "nodes": {}, "materials": {}, "images": {}}


def _index(items, item, cache):
    if id(items) not in cache: cache[id(items)] = {id(value): index for index, value in enumerate(items)}
    return cache[id(items)].get(id(item))


class ImportHooks:
    def __init__(self):
        self.cache = {}

    def gather_import_node_after_hook(self, vnode, gltf_node, blender_object, gltf):
        if _OPEN["active"] and blender_object is not None:
            _OPEN["nodes"][_index(gltf.data.nodes, gltf_node, self.cache)] = blender_object

    def gather_import_material_after_hook(self, gltf_material, vertex_color, blender_material, gltf):
        if _OPEN["active"]:
            _OPEN["materials"].setdefault(_index(gltf.data.materials, gltf_material, self.cache), []).append(blender_material)

    def gather_import_image_after_hook(self, gltf_image, blender_image, gltf):
        if _OPEN["active"] and blender_image is not None:
            _OPEN["images"][_index(gltf.data.images, gltf_image, self.cache)] = blender_image


def packed_image(name, data):
    """A Blender image with packed PNG bytes (as the glTF importer makes them)."""
    image = bpy.data.images.new(name, 8, 8)
    image.pack(data=data, data_len=len(data))
    image.source = "FILE"
    image.alpha_mode = "CHANNEL_PACKED"
    return image


def remove_gh3_properties(data):
    for key in [k for k in data.keys() if k.startswith("gh3")]: del data[key]


def set_properties(holder, values):
    for key, value in values.items(): setattr(holder, key, value)


def link(collection, obj):
    collection.objects.link(obj)
    return obj


def child_collection(scene, name):
    collection = bpy.data.collections.new(name)
    scene.collection.children.link(collection)
    return collection


def import_stage(scene, data, issues):
    """Import stage.glb into the scene. Returns (glTF JSON, binary, node objects, materials, images by glTF index)."""
    # Changes that keep the data that the Blender importer would lose. The exporter writes the same triangles again.
    prepared, faces = glb_tools.separate_faces(data)
    prepared, uv_maps = glb_tools.fill_texcoord_gaps(prepared)
    prepared, normals = glb_tools.normalize_normals(prepared)
    if faces or uv_maps or normals:
        print(f"CH Stage Tools: stage.glb import: {faces} triangles got own vertices, {uv_maps} empty UV maps added, {normals} normal sets normalized")
    if normals: issues.warn(f"stage.glb: {normals} meshes have normals that are not unit length; Blender writes unit normals")
    _OPEN.update(active=True, nodes={}, materials={}, images={})
    try:
        with tempfile.TemporaryDirectory(prefix="chstage-open-") as folder:
            path = Path(folder)/"stage.glb"
            path.write_bytes(prepared)
            view_layer = scene.view_layers[0]
            with bpy.context.temp_override(scene=scene, view_layer=view_layer, collection=scene.collection):
                gltf_io.import_glb(path)
        nodes, mats, images = dict(_OPEN["nodes"]), dict(_OPEN["materials"]), dict(_OPEN["images"])
    finally:
        _OPEN.update(active=False, nodes={}, materials={}, images={})
    gltf, binary = glb_tools.read_glb(data)
    return gltf, binary, nodes, mats, images


def open_scenery(scene, source, entry, issues):
    gltf, binary, nodes, mats, images = import_stage(scene, source.read(entry["model"]), issues)
    for index, image in enumerate(gltf.get("images", [])):
        if index not in images:
            images[index] = packed_image(image.get("name") or f"image-{index}", glb_tools.image_bytes(gltf, binary, index))
    by_name = {image.get("name"): images[index] for index, image in enumerate(gltf.get("images", [])) if image.get("name")}
    for index, obj in nodes.items():
        node = gltf["nodes"][index]
        set_properties(obj.chstage, sidecars.open_node(node.get("extras", {})))
        obj.chstage.kept = sidecars.kept_text(node={k: v for k, v in node.get("extras", {}).items() if k.startswith("gh3")})
        obj.chstage.source_name = node.get("name", "")
        remove_gh3_properties(obj)
    textures = gltf.get("textures", [])
    for index, variants in mats.items():
        material = gltf["materials"][index]
        props, other = sidecars.open_material(material)
        refs = {}
        for container, key in sidecars.texture_references(other):
            ref = container[key]
            if not ref: continue
            if ref.startswith("#"): image = by_name.get(ref[1:])
            elif ref in source.set: image = bpy.data.images.get(ref) or packed_image(ref, source.read(ref))
            else: image = None
            if image is None: issues.warn(f"material '{material.get('name')}': texture reference {ref} has no image")
            else: refs[ref[1:] if ref.startswith("#") else ref] = image
        occlusion = material.get("occlusionTexture")
        lightmap = images.get(textures[occlusion["index"]].get("source")) if occlusion else None
        for blender_material in variants:
            set_properties(blender_material.chstage, props)
            blender_material.chstage.kept_extras = json.dumps(other, separators=(",", ":")) if other else ""
            for key, image in refs.items():
                item = blender_material.chstage.kept_images.add()
                item.name, item.image = key, image
            if lightmap is not None:
                blender_material.chstage.lightmap = lightmap
                uv = occlusion.get("texCoord", 0)
                blender_material.chstage.lightmap_uv = "UVMap" if uv == 0 else f"UVMap.{uv:03d}"
                if uv != 1: issues.warn(f"material '{blender_material.name}': the lightmap uses TEXCOORD_{uv}, not TEXCOORD_1")
            blender_material.chstage.kept = sidecars.kept_text(material=sidecars.gltf_material_row(material))
            blender_material.chstage.source_name = material.get("name", "")
            remove_gh3_properties(blender_material)
        if len(variants) > 1: issues.warn(f"material '{material.get('name')}': Blender made {len(variants)} materials from it")
    for mesh in {obj.data for obj in nodes.values() if obj.type == "MESH"}: remove_gh3_properties(mesh)


def set_matrix(obj, value):
    obj.matrix_world = Matrix([list(row) for row in value])


def open_markers(scene, placements):
    markers, crowd, keys = sidecars.open_placements(placements)
    collection = child_collection(scene, "Markers")
    for marker in markers:
        if marker["kind"] == "BAND": obj = template.add_band_marker(scene, marker["role"], (0, 0, 0), collection=collection)
        elif marker["kind"] == "WAYPOINT": obj = template.add_waypoint(scene, marker["name"], (0, 0, 0), collection=collection)
        else:
            obj = template.add_effect(scene, marker["effect"], (0, 0, 0), collection=collection)
            obj.name = f"effect-{marker['effect']}-{marker['order']:02d}"
        if marker["kind"] == "WAYPOINT": obj.chstage.waypoint_name = marker["name"]
        set_matrix(obj, marker["matrix"])
        obj.chstage.order = marker["order"]
        obj.chstage.kept = sidecars.kept_text(placement=marker["source"])
    for row in crowd:
        item = scene.chstage.crowd.add()
        item.role, item.model, item.yaw = row["role"], row["model"], row["yaw"]
        item.kept = sidecars.kept_text(crowd=row["source"])
    return keys


def camera_object(collection, name, point):
    data = bpy.data.cameras.new(name)
    data.sensor_fit = "HORIZONTAL"
    data.angle = math.radians(point["fov"])
    data.clip_end = 1000
    data.display_size = 0.3
    obj = link(collection, bpy.data.objects.new(name, data))
    set_matrix(obj, point["matrix"])
    props = obj.chstage.point
    props.anchor_role, props.anchor_part, props.anchor_bone = point["anchorRole"], point["anchorPart"], point["anchorBone"]
    props.look_at_role, props.look_at_part, props.look_at_bone = point["lookAtRole"], point["lookAtPart"], point["lookAtBone"]
    props.use_rotation = point["useRotation"]
    if point["screenOffset"] is not None:
        props.use_screen_offset, props.screen_offset = True, point["screenOffset"]
    return obj


def open_cameras(scene, cameras, placements):
    shots, notes = sidecars.open_cameras(cameras, placements)
    collection = child_collection(scene, "Camera Shots")
    for shot in shots:
        camera = camera_object(collection, shot["name"], shot["points"][0])
        props = camera.chstage.shot
        props.enabled, props.shot_name = True, shot["name"]
        props.type, props.control, props.tag = shot["type"], shot["control"], shot["tag"]
        for group in shot["groups"]: props.groups.add().name = group
        for waypoint in shot["waypoints"]: props.waypoints.add().name = waypoint
        for value in shot["times"]: props.times.add().value = value
        props.interpolation = shot["interpolation"]
        for flag, name in (("use_orbit_angle", "orbitAngle"), ("use_angular_speed", "angularSpeed"), ("use_step_time", "stepTime"),
                           ("use_step_count", "stepCount"), ("use_step_direction", "stepDirection"), ("use_height_ease", "heightEase")):
            if shot[name] is not None:
                setattr(props, flag, True)
                setattr(props, flag[4:], shot[name])
        props.hand_cam, props.disable_orbit1, props.disable_orbit2 = shot["handCam"], shot["disableOrbit1"], shot["disableOrbit2"]
        for number, point in enumerate(shot["points"][1:], start=2):
            props.more_points.add().target = camera_object(collection, f"{shot['name']}.point{number}", point)
        camera.chstage.order = shot["order"]
        camera.chstage.kept = sidecars.kept_text(shot=shot["source"])
    for note in notes:
        item = scene.chstage.camera_notes.add()
        item.note, item.group, item.tag, item.type = note["note"], note["group"], note["tag"], note["type"]
        if note["enable"] is not None: item.use_enable, item.enable = True, note["enable"]
        item.kept = sidecars.kept_text(note=note["source"])
    wide = next((obj for obj in collection.objects if obj.chstage.shot.enabled and any(g.name == "wide" for g in obj.chstage.shot.groups)), None)
    if wide is not None: scene.camera = wide
    return cameras.get("groups", {})


def open_lighting(scene, lighting):
    lights, snapshots, moods, colors = sidecars.open_lighting(lighting)
    collection = child_collection(scene, "Lights")
    objects = []
    for light in lights:
        data = bpy.data.lights.new(light["id"], light["blenderType"])
        if light["blenderType"] == "SPOT" and light["field"] > 0:
            data.spot_size = math.radians(min(180, light["field"]))
            data.spot_blend = max(0.0, min(1.0, 1 - light["hotspot"] / light["field"]))
        obj = link(collection, bpy.data.objects.new(light["id"], data))
        set_matrix(obj, light["matrix"])
        props = obj.chstage.light
        props.enabled, props.light_id, props.group, props.type = True, light["id"], light["group"], light["type"]
        props.script_object, props.override_color = light["object"], light["overrideColor"]
        if light["useCone"]: props.use_cone, props.hotspot, props.field = True, light["hotspot"], light["field"]
        obj.chstage.order = light["order"]
        obj.chstage.kept = sidecars.kept_text(light=light["source"])
        objects.append(obj)
    for snapshot in snapshots:
        entry = scene.chstage.snapshots.add()
        entry.name = snapshot["name"]
        for row in snapshot["lights"]:
            item = entry.lights.add()
            item.light = objects[row["index"]] if row["index"] < len(objects) else None
            item.color, item.intensity, item.near, item.far, item.inherit = row["color"], row["intensity"], row["near"], row["far"], row["inherit"]
            if row["specular"] is not None: item.use_specular, item.specular = True, row["specular"]
        for beam in snapshot["beams"]:
            item = entry.beams.add()
            item.fixture_id, item.position, item.rotation, item.color = beam["id"], beam["position"], beam["rotation"], beam["color"]
            item.density, item.start_radius, item.end_radius, item.range = beam["density"], beam["startRadius"], beam["endRadius"], beam["range"]
            item.inherit = beam["inherit"]
        entry.kept = sidecars.kept_text(snapshot=snapshot["source"])
    for mood in moods:
        item = scene.chstage.moods.add()
        item.mood = mood["mood"]
        for name in mood["snapshots"]: item.snapshots.add().name = name
        item.kept = sidecars.kept_text(mood=mood["source"])
    for color in colors:
        item = scene.chstage.colors.add()
        item.name, item.color = color["name"], color["color"]
        item.kept = sidecars.kept_text(color=color["source"])
    intro = next((m for m in scene.chstage.moods if m.mood == "intro" and m.snapshots), None)
    if intro is not None:
        snapshot = next((s for s in scene.chstage.snapshots if s.name == intro.snapshots[0].name), None)
        if snapshot is not None: template.apply_snapshot(snapshot)


def open_kept_files(scene, source, manifest, path, skip):
    """Keep the package files that the add-on does not make: JSON files as text blocks, other files by reference."""
    entry_of = {value: key for key, value in manifest.get("entry", {}).items()}
    for name in sorted(manifest.get("files", {}) or source.names):
        if name in skip or name == "manifest.json": continue
        item = scene.chstage.kept_files.add()
        item.path, item.entry = name, entry_of.get(name, "")
        data = source.read(name)
        item.sha256, item.size = hashlib.sha256(data).hexdigest(), len(data)
        if name.endswith(JSON_SUFFIX):
            text = bpy.data.texts.new(f"{manifest.get('id', 'stage')}/{name}")
            text.from_string(data.decode("utf-8"))
            item.text = text
        else:
            item.source = str(Path(path).resolve())


def record_kept(scene):
    """Store the rows that the new Blender data gives, next to the source rows (sidecars.Keeper)."""
    scene.view_layers[0].update()
    keeper = sidecars.Keeper("record")
    collect.sidecar_files(scene, sidecars.Issues(), keeper)
    objects = collect.exported_objects(scene)
    for obj in objects: keeper(collect.node_row(obj), obj.chstage, "node")
    for material in collect.exported_materials(objects): keeper(collect.material_row(material), material.chstage, "material")
    keeper.store()


def open_package(path):
    """Make a new scene from a .chstage zip or development folder. Returns (scene, issues)."""
    path = Path(path)
    if path.name == "manifest.json": path = path.parent
    issues = sidecars.Issues()
    report = Report()
    source = Source(path, report)
    if report.errors or "manifest.json" not in source.set: raise ValueError(f"{path}: not a package ({'; '.join(report.errors) or 'no manifest.json'})")
    manifest = json.loads(source.read("manifest.json"))
    if not str(manifest.get("format", "")).startswith("chstage/"): raise ValueError(f"{path}: not a venue package (format {manifest.get('format')})")
    validation = Validator(path).run()
    for message in validation.errors: issues.error(f"package: {message}")
    entry = manifest.get("entry", {})
    for key in ("model", "placements", "cameras"):
        if entry.get(key) not in source.set: raise ValueError(f"{path}: the package has no {key} file")

    scene = bpy.data.scenes.new(manifest.get("name") or manifest.get("id") or "CH Stage")
    scene.render.resolution_x, scene.render.resolution_y = 1920, 1080
    scene.render.fps, scene.render.fps_base = 60, 1.0
    scene.chstage.source_package = str(path.resolve())
    package = scene.chstage.package
    description = sidecars.open_manifest(manifest)
    package.package_id, package.version, package.name = description["id"], description["version"], description["name"]
    package.description, package.license, package.homepage = description["description"], description["license"], description["homepage"]
    package.min_mod_version = description["minModVersion"]
    for key in ("redistribute", "modify", "commercial"): setattr(package, key, bool(description["permissions"].get(key, False)))
    for author in description["authors"]:
        item = package.authors.add()
        item.name, item.role, item.url = author["name"], author["role"], author["url"]
    for line in description["credits"]: package.credits.add().name = line
    for requirement in description["requires"]:
        item = package.requires.add()
        item.package_id, item.version = requirement.get("id", ""), requirement.get("version", "")
    skip = {entry[key] for key in ADDON_FILES if key in entry}
    preview = manifest.get("preview")
    if preview in source.set:
        package.preview_mode = "IMAGE"
        package.preview_image = packed_image(f"{description['id']}-preview", source.read(preview))
        skip.add(preview)
    else:
        package.preview_mode = "NONE"

    open_scenery(scene, source, entry, issues)
    placements = json.loads(source.read(entry["placements"]))
    keys = open_markers(scene, placements)
    groups = open_cameras(scene, json.loads(source.read(entry["cameras"])), placements)
    if entry.get("lighting") in source.set: open_lighting(scene, json.loads(source.read(entry["lighting"])))
    for material in bpy.data.materials:
        for item in material.chstage.kept_images:
            if item.name in source.set: skip.add(item.name)
    open_kept_files(scene, source, manifest, path, skip)
    scene.chstage.kept = json.dumps({"groups": {"source": groups}, "placementKeys": keys}, separators=(",", ":"))
    record_kept(scene)
    return scene, issues.items


def source_status(item):
    """'text', 'ok', or why the kept file cannot be written."""
    if item.text is not None: return "text"
    path = bpy.path.abspath(item.source)
    if not os.path.exists(path): return "source package missing"
    return "ok"
