"""Write a venue package (.chstage zip and development folder) from the Blender scene, and validate it."""

import hashlib
import os
import re
import shutil
import tempfile
from pathlib import Path

import bpy

from . import actor_tools, blobs, collect, glb_tools, materials, sidecars
from .vendor.package_format import container
from .vendor.package_format.validate import Report, Source, Validator

PREVIEW_SIZE = (512, 288)


def safe_name(name):
    """A package path segment for a Blender name."""
    text = re.sub(r"[^A-Za-z0-9_.-]+", "-", name).strip(".-") or "image"
    return text if text[0] not in "._-" else "x" + text


def finish_glb(path, folder, issues, blender_materials):
    """Give the texture references of the material extras their final form (PACKAGE-FORMAT 6.6).

    A reference to an image that Blender exported stays '#<name>'. Blender exports only the images that a material
    socket uses, so the other images (for example the GH3 shader samplers) become PNG files in textures/.
    Returns the written package paths.
    """
    gltf, binary = glb_tools.read_glb(Path(path).read_bytes())
    exported = {image.get("name") for image in gltf.get("images", [])}
    written = {}
    changed = False
    for material in gltf.get("materials", []):
        blender_material = blender_materials.get(material.get("name", ""))
        mapping = {ref.name: ref.image for ref in blender_material.chstage.kept_images} if blender_material else {}
        for container_, key in sidecars.texture_references(material.get("extras", {})):
            ref = container_[key]
            if not ref: continue
            source_key = ref[1:] if ref.startswith("#") else ref
            image = mapping.get(source_key) or (bpy.data.images.get(source_key) if ref.startswith("#") else None)
            if image is None:
                if not ref.startswith("#"): continue
                issues.error(f"material '{material.get('name')}': no Blender image for the texture reference {ref}", None, material.get("name"))
                continue
            if image.name in exported:
                new = "#" + image.name
            else:
                new = written.get(image.name)
                if new is None:
                    new = ref if not ref.startswith("#") else f"textures/{safe_name(image.name)}.png"
                    while new in written.values(): new = new[:-4] + "-1.png"
                    target = Path(folder)/new
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_bytes(materials.image_png_bytes(image))
                    written[image.name] = new
            if new != ref:
                container_[key] = new
                changed = True
    if changed: Path(path).write_bytes(glb_tools.write_glb(gltf, binary))
    return sorted(written.values())


def preview_camera(scene):
    if scene.camera is not None: return scene.camera
    for obj in collect.scene_objects(scene):
        if obj.type == "CAMERA" and obj.chstage.shot.enabled and any(g.name == "wide" for g in obj.chstage.shot.groups): return obj
    return None


def render_preview(scene, issues):
    """PNG bytes of a small Workbench render of the scene camera, or None (with a warning) when Blender cannot render."""
    camera = preview_camera(scene)
    if camera is None:
        issues.warn("no camera for the preview image: set the scene camera, or set Preview to Image or None")
        return None
    render = scene.render
    saved = {"engine": render.engine, "resolution_x": render.resolution_x, "resolution_y": render.resolution_y,
             "resolution_percentage": render.resolution_percentage, "filepath": render.filepath, "film_transparent": render.film_transparent,
             "use_stamp": render.use_stamp, "camera": scene.camera}
    image = render.image_settings
    saved_image = {"file_format": image.file_format, "color_mode": image.color_mode, "color_depth": image.color_depth}
    saved_color = scene.display.shading.color_type
    with tempfile.TemporaryDirectory(prefix="chstage-preview-") as folder:
        target = os.path.join(folder, "preview.png")
        try:
            render.engine = "BLENDER_WORKBENCH"
            render.resolution_x, render.resolution_y = PREVIEW_SIZE
            render.resolution_percentage = 100
            render.film_transparent = False
            render.use_stamp = False
            render.filepath = target
            image.file_format, image.color_mode, image.color_depth = "PNG", "RGB", "8"
            scene.display.shading.color_type = "TEXTURE"
            scene.camera = camera
            with bpy.context.temp_override(scene=scene):
                bpy.ops.render.render(write_still=True, scene=scene.name)
            data = Path(target).read_bytes()
        except (RuntimeError, OSError) as error:
            issues.warn(f"the preview render failed ({error}); export without a preview image")
            return None
        finally:
            for key, value in saved.items():
                if key == "camera": scene.camera = value
                else: setattr(render, key, value)
            for key, value in saved_image.items(): setattr(image, key, value)
            scene.display.shading.color_type = saved_color
    return glb_tools.strip_png_text(data)


def preview_bytes(scene, issues, render=True):
    package = scene.chstage.package
    if package.preview_mode == "IMAGE":
        if package.preview_image is None:
            issues.error("Preview is Image, but no preview image is set")
            return None
        return materials.image_png_bytes(package.preview_image)
    if package.preview_mode == "RENDER" and render: return render_preview(scene, issues)
    return None


def kept_file_bytes(item, issues, sources):
    """Bytes of a kept package file: its text block, its packed bytes, or (a .blend file of add-on 0.1) the file in its
    source package (SHA-256 checked)."""
    if item.text is not None: return item.text.as_string().encode("utf-8")
    packed = blobs.load(item.blob)
    if packed is not None: return packed
    path = bpy.path.abspath(item.source)
    if path not in sources:
        report = Report()
        sources[path] = Source(path, report) if os.path.exists(path) else None
    source = sources[path]
    if source is None or item.path not in source.set:
        issues.error(f"kept file {item.path}: the source package {item.source} is missing. Open the package again")
        return None
    data = source.read(item.path)
    if item.sha256 and hashlib.sha256(data).hexdigest() != item.sha256:
        issues.error(f"kept file {item.path}: the source package {item.source} has changed. Open the package again")
        return None
    return data


def build_files(scene, folder, issues, keeper=None, render_preview_image=True, actor_models=None):
    """Write all package files into folder. Returns (manifest, names for issue mapping).
    actor_models: None (the setting of each actor), "PACKED", or "BLENDER" (tests)."""
    folder = Path(folder)
    folder.mkdir(parents=True, exist_ok=True)
    keeper = keeper or sidecars.Keeper()
    files, names = collect.sidecar_files(scene, issues, keeper)
    objects, blender_materials = collect.export_stage_glb(scene, folder/"stage.glb", keeper, issues)
    if not objects: issues.error("the stage has no scenery meshes")
    paths = {"stage.glb", *files, *finish_glb(folder/"stage.glb", folder, issues, blender_materials)}
    for name, value in files.items(): (folder/name).write_bytes(container.json_bytes(value))
    entry = {"actors": "actors.json"} if "actors.json" in files else {}
    models = {}
    for root in actor_tools.actor_roots(scene):
        path = actor_tools.model_path(root)
        data = actor_tools.model_bytes(scene, root, issues, actor_models)
        if data is None: continue
        if path in models:
            if models[path] != data: issues.error(f"actors '{root.name}' and others use the model file {path} with different models", root.name)
            continue
        if path in paths: issues.error(f"actor '{root.name}': the model file {path} has the path of another package file", root.name); continue
        models[path] = data
        target = folder/path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
    paths.update(models)
    sources = {}
    for item in scene.chstage.kept_files:
        if item.path in paths:
            issues.error(f"kept file {item.path} has the path of a file that the add-on writes"); continue
        data = kept_file_bytes(item, issues, sources)
        if data is None: continue
        target = folder/item.path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
        paths.add(item.path)
        if item.entry: entry[item.entry] = item.path
    preview = preview_bytes(scene, issues, render_preview_image)
    if preview is not None:
        (folder/"preview.png").write_bytes(preview)
        paths.add("preview.png")
    manifest = sidecars.build_manifest(collect.package_description(scene), sidecars.stage_entry("lighting.json" in files, entry),
                                       "preview.png" if preview is not None else None)
    manifest["files"] = container.file_list(folder, paths)
    (folder/"manifest.json").write_bytes(container.json_bytes(manifest))
    return manifest, names


def add_report(issues, report, names, scene):
    known = {message for _, message, _, _ in issues.items}
    for severity, messages in (("ERROR", report.errors), ("WARNING", report.warnings)):
        for message in messages:
            if message in known: continue
            obj, material = collect.resolve(message, names, scene)
            issues.items.append((severity, message, obj, material))


def validate_scene(scene):
    """Run the add-on checks and the bundled package validator. Returns a list of (severity, message, object, material)."""
    issues = sidecars.Issues()
    with tempfile.TemporaryDirectory(prefix="chstage-validate-") as temporary:
        folder = Path(temporary)/f"{safe_name(scene.chstage.package.package_id or 'stage')}.chstage"
        _, names = build_files(scene, folder, issues, render_preview_image=False)
        add_report(issues, Validator(folder).run(), names, scene)
    return issues.items


def package_file_name(scene):
    return f"{safe_name(scene.chstage.package.package_id or 'stage')}.chstage"


def export_package(scene, target, folder=None, custom_folder=None, keeper=None, actor_models=None):
    """Write the package zip to target (and the development folder and a copy into the Custom/Stages folder) when it is
    valid. Returns (issues, written paths). An invalid package is not written; the issues tell why."""
    issues = sidecars.Issues()
    target = Path(target)
    written = []
    with tempfile.TemporaryDirectory(prefix="chstage-export-") as temporary:
        staging = Path(temporary)/target.name
        _, names = build_files(scene, staging, issues, keeper, actor_models=actor_models)
        archive = Path(temporary)/"zip"/target.name
        archive.parent.mkdir()
        container.write_zip(staging, archive)
        add_report(issues, Validator(archive).run(), names, scene)
        if any(severity == "ERROR" for severity, _, _, _ in issues.items): return issues.items, written
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(archive, target.with_name("." + target.name + ".tmp"))
        os.replace(target.with_name("." + target.name + ".tmp"), target)
        written.append(target)
        if folder is not None:
            folder = Path(folder)
            if folder.exists(): shutil.rmtree(folder)
            shutil.copytree(staging, folder)
            written.append(folder)
        if custom_folder:
            copy = Path(custom_folder)/target.name
            if copy.resolve() != target.resolve():
                copy.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(archive, copy)
                written.append(copy)
    scene.chstage.export_path = str(target)
    return issues.items, written


def development_folder(target):
    """The development folder next to a package zip: <folder>/unpacked/<name>.chstage (the converter layout)."""
    target = Path(target)
    return target.parent/"unpacked"/target.name
