"""Write a character package (.chchar zip and development folder) from a character scene, validate it, and open one.

The bundled package validator is the source of truth (with the library, it checks the bind pose against the profile:
PACKAGE-FORMAT 9.3). The add-on checks before it give errors that name the object: rig transform, mesh transform,
vertices without weights, more than four influences, weight sums, images that are not PNG.
"""

import json
import os
import re
import shutil
import tempfile
from pathlib import Path

import bpy
import numpy as np

from . import (
    chain_tools,
    character_tools,
    collect,
    glb_tools,
    gltf_io,
    materials,
    package_export,
    package_open,
    sidecars,
    skeleton_tools,
    textures,
    toon,
)
from .vendor.package_format import character, container
from .vendor.package_format.package_files import Report, Source
from .vendor.package_format.validate import Validator

WEIGHT_SUM = 1e-3
IDENTITY = 1e-4


def is_character(scene):
    return scene.chstage.character.enabled


def package_file_name(scene):
    return f"{package_export.safe_name(scene.chstage.package.package_id or 'character')}.chchar"


def development_folder(target):
    target = Path(target)
    return target.parent / "unpacked" / target.name


def hair_materials(scene):
    return [item.material for item in scene.chstage.character.hair_materials if item.material is not None]


def image_nodes(material):
    if material is None or not material.use_nodes or material.node_tree is None:
        return []
    return [node for node in material.node_tree.nodes if node.type == "TEX_IMAGE" and node.image is not None]


# Surface nodes that the glTF exporter converts (Principled BSDF, and the unlit and emission forms that it detects).
GLTF_SURFACES = {"BSDF_PRINCIPLED", "EMISSION", "BACKGROUND", "MIX_SHADER", "ADD_SHADER", "BSDF_DIFFUSE"}


def unsupported_surface(material):
    """The surface node of a material that the glTF exporter does not read (for example the node group of an imported
    MMD or VRM toon shader), or None."""
    if material is None or not material.use_nodes or material.node_tree is None:
        return None
    # The toon preview output (toon.py) is off while the exporter runs.
    outputs = [
        n
        for n in material.node_tree.nodes
        if n.type == "OUTPUT_MATERIAL" and n.target in ("ALL", "EEVEE") and not n.get(toon.PREVIEW)
    ]
    output = next((n for n in outputs if n.is_active_output), outputs[0] if outputs else None)
    if output is None or not output.inputs["Surface"].is_linked:
        return None
    link = output.inputs["Surface"].links[0]
    node, socket = link.from_node, link.from_socket
    # The exporter follows node groups to the node at their group output.
    for _ in range(16):
        if node.type != "GROUP" or node.node_tree is None:
            break
        inner = next((n for n in node.node_tree.nodes if n.type == "GROUP_OUTPUT" and n.is_active_output), None)
        target = inner.inputs.get(socket.name) if inner is not None else None
        if target is None or not target.is_linked:
            return node
        node, socket = target.links[0].from_node, target.links[0].from_socket
    return None if node.type in GLTF_SURFACES else node


def check_scene(scene, issues):  # noqa: C901
    """The add-on checks of a character scene. Returns (rig, meshes); rig is None after a blocking error."""
    p = scene.chstage.character
    rig = p.rig
    scene.view_layers[0].update()
    if rig is None or rig.type != "ARMATURE" or rig.name not in scene.objects:
        issues.error("the character has no rig: click New, or open a .chchar package")
        return None, []
    if np.abs(np.array(rig.matrix_world) - np.eye(4)).max() > IDENTITY:
        issues.error(
            "the rig must stay at the origin without rotation or scale (the profile fixes the rig): clear its transform",
            rig.name,
        )
    library = character_tools.library_path(scene)
    profile = character_tools.profile_id(scene)
    if p.own_skeleton:
        skeleton_tools.check(scene, issues)
        chain_tools.check(scene, issues)
    elif library or p.library_profile:
        try:
            rig_data = character_tools.scene_rig_data(scene)
            bones = set(rig.data.bones.keys())
            missing = [n for n in rig_data.names if n not in bones]
            if missing:
                issues.error(
                    f"the rig has no bone {missing[0]} ({character.bone_label(missing[0])}) of the profile {profile} ({len(missing)} missing)",
                    rig.name,
                )
            # Bones that are not in the profile are added bones (character.json bones), for the custom chains.
            chain_tools.check(scene, issues)
        except (OSError, ValueError, KeyError) as error:
            issues.warn(f"no rig of the profile {profile} to check: {error}")
    meshes = character_tools.character_meshes(scene)
    if not meshes:
        issues.error(
            "the character has no mesh: add a mesh and click Transfer Weights (or parent it to the rig with an Armature modifier)",
            rig.name,
        )
    names = character_tools.profile_groups(rig)
    for obj in meshes:
        if not any(m.type == "ARMATURE" and m.object == rig for m in obj.modifiers):
            issues.error(f"mesh '{obj.name}' has no Armature modifier for the rig", obj.name)
        if np.abs(np.array(obj.matrix_world) - np.array(rig.matrix_world)).max() > IDENTITY:
            issues.error(
                f"mesh '{obj.name}' has its own transform: apply it (Object > Apply > All Transforms) or click Transfer Weights",
                obj.name,
            )
        weights = character_tools.vertex_weights(obj, names)
        influences = (weights > 0).sum(1)
        sums = weights.sum(1)
        if (influences == 0).any():
            issues.error(
                f"mesh '{obj.name}': {int((influences == 0).sum())} vertices have no weight on a rig bone", obj.name
            )
        if (influences > character.MAX_INFLUENCES).any():
            issues.error(
                f"mesh '{obj.name}': {int((influences > 4).sum())} vertices have more than 4 influences: click Limit and Normalize",
                obj.name,
            )
        bad = (influences > 0) & (np.abs(sums - 1) > WEIGHT_SUM)
        if bad.any():
            issues.error(
                f"mesh '{obj.name}': the weights of {int(bad.sum())} vertices do not sum to 1: click Limit and Normalize",
                obj.name,
            )
        others = [g.name for g in obj.vertex_groups if g.name not in names]
        if others:
            issues.warn(
                f"mesh '{obj.name}': export ignores the vertex groups that are not rig bones, for example '{others[0]}'",
                obj.name,
            )
        for slot in obj.material_slots:
            surface = unsupported_surface(slot.material)
            if surface is not None:
                issues.warn(
                    f"material '{slot.material.name}': the glTF export does not read its surface node '{surface.name}' ({surface.bl_idname}), "
                    "so the package gets no texture for it: connect a Principled BSDF with the image on Base Color",
                    obj.name,
                    slot.material.name,
                )
            for node in image_nodes(slot.material):
                if node.image.source == "FILE" and node.image.file_format not in ("PNG",):
                    issues.error(
                        f"material '{slot.material.name}': image '{node.image.name}' is {node.image.file_format}; packages use PNG only "
                        "(save the image as PNG)",
                        obj.name,
                        slot.material.name,
                    )
    used = {slot.material for obj in meshes for slot in obj.material_slots if slot.material is not None}
    for material in hair_materials(scene):
        if material not in used:
            issues.error(f"hair material '{material.name}' is not on a character mesh", rig.name)
    return rig, meshes


def export_glb(scene, path, rig, meshes, keeper, issues):
    """Export the rig and its meshes with the safe glTF options and the add-on material extras. Returns the Blender
    material of each glTF material name."""
    objects = [rig, *meshes]
    view_layer = scene.view_layers[0]
    window = bpy.context.window
    previous = window.scene if window else None
    if window:
        window.scene = scene
    selected = [obj for obj in scene.objects if obj.select_get(view_layer=view_layer)]
    try:
        with collect.actor_tools.shown(objects, view_layer):
            for obj in scene.objects:
                obj.select_set(obj in objects, view_layer=view_layer)
            collect._EXPORT.update(active=True, keeper=keeper, materials={})
            exported = collect.exported_materials(meshes)
            # Own skeleton: the deform bones are the joints, and the shape keys go out as morph targets (finish_glb keeps
            # the face shapes only). Modifiers stay unapplied: an applied modifier drops the shape keys.
            own = (
                {
                    "export_def_bones": True,
                    "export_morph": True,
                    "export_morph_normal": False,
                    "export_morph_tangent": False,
                    "export_try_sparse_sk": False,
                    "export_try_omit_sparse_sk": False,
                    "export_apply": False,
                }
                if scene.chstage.character.own_skeleton
                else {}
            )
            with (
                bpy.context.temp_override(scene=scene, view_layer=view_layer),
                materials.vertex_alpha_for_export(exported),
                toon.preview_off_for_export(exported),
            ):
                options = {
                    "use_selection": True,
                    "export_animations": False,
                    "export_cameras": False,
                    "export_lights": False,
                    "export_apply": True,
                    "export_vertex_color": "ACTIVE",
                    "export_skins": True,
                    **own,
                }
                gltf_io.export_glb(path, **options)
    finally:
        mapping = dict(collect._EXPORT["materials"])
        collect._EXPORT.update(active=False, keeper=None)
        for obj in scene.objects:
            obj.select_set(obj in selected, view_layer=view_layer)
        if window and previous:
            window.scene = previous
    return mapping


def finish_glb(data, blender_materials, issues, morphs=None):
    """The character GLB after the export: no animations, the package names of the materials, and the images of the
    '#<name>' texture references that Blender does not export (embedded, each with a glTF texture). morphs: the morph
    target names to keep (a character with its own skeleton); None: no morph targets."""
    gltf, binary = glb_tools.read_glb(data)
    if gltf.pop("animations", None) is not None:
        data = glb_tools.write_glb(gltf, binary)
    if morphs is not None:
        gltf, binary = glb_tools.read_glb(data)
        data = glb_tools.write_glb(*character.keep_morphs(gltf, binary, morphs or ()))
    wanted = [m.chstage.source_name for m in blender_materials.values() if m.chstage.source_name]
    data = glb_tools.restore_names(data, wanted)
    gltf, _ = glb_tools.read_glb(data)
    present = {image.get("name") for image in gltf.get("images", [])}
    needed = glb_tools.texture_reference_names(gltf) - present
    images = {}
    for material in blender_materials.values():
        for ref in material.chstage.kept_images:
            if ref.name in needed and ref.image is not None:
                images[ref.name] = materials.image_png_bytes(ref.image)
        for prop in sidecars.TOON_TEXTURES.values():
            image = getattr(material.chstage, prop)
            if image is not None and image.name in needed:
                images[image.name] = materials.image_png_bytes(image)
    data = glb_tools.add_images(data, images)
    for name in sorted(needed - set(images)):
        issues.error(f"no image for the texture reference #{name}")
    return textures.encode_glb(data)


def character_row(scene, blender_materials, glb_data=None):
    p = scene.chstage.character
    by_material = {material: name for name, material in blender_materials.items()}
    names = [by_material.get(material, collect.package_name(material)) for material in hair_materials(scene)]
    row = character.character_json(
        character_tools.profile_id(scene),
        names,
        p.instrument.strip(),
        p.library_id or character.LIBRARY_ID,
        p.animations.strip(),
    )
    if p.own_skeleton:
        row["skeleton"], face = skeleton_tools.skeleton_rows(scene)
        if face:
            row["face"] = face
    if glb_data is not None:
        bones, colliders, chains = chain_tools.export_rows(scene, glb_data)
        for key, value in (("bones", bones), ("colliders", colliders), ("chains", chains)):
            if value:
                row[key] = value
    return row


def build_files(scene, folder, issues, keeper=None, render_preview_image=True):
    """Write all package files into folder. Returns (manifest, names for the issue map)."""
    folder = Path(folder)
    folder.mkdir(parents=True, exist_ok=True)
    keeper = keeper or sidecars.Keeper()
    rig, meshes = check_scene(scene, issues)
    names = {"objects": {obj.name: obj.name for obj in meshes}}
    if rig is None or not meshes:
        return None, names
    with tempfile.TemporaryDirectory(prefix="chstage-character-") as temporary:
        path = Path(temporary) / character.MODEL_FILE
        blender_materials = export_glb(scene, path, rig, meshes, keeper, issues)
        morphs = list(skeleton_tools.face(scene).values()) if scene.chstage.character.own_skeleton else None
        data = finish_glb(path.read_bytes(), blender_materials, issues, morphs)
    (folder / character.MODEL_FILE).write_bytes(data)
    (folder / character.CHARACTER_FILE).write_bytes(container.json_bytes(character_row(scene, blender_materials, data)))
    paths = {character.MODEL_FILE, character.CHARACTER_FILE}
    entry = {"model": character.MODEL_FILE, "character": character.CHARACTER_FILE}
    sources = {}
    for item in scene.chstage.kept_files:
        if item.path in paths:
            issues.error(f"kept file {item.path} has the path of a file that the add-on writes")
            continue
        data = package_export.kept_file_bytes(item, issues, sources)
        if data is None:
            continue
        target = folder / item.path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
        paths.add(item.path)
        if item.entry:
            entry[item.entry] = item.path
    preview = package_export.preview_bytes(scene, issues, render_preview_image)
    if preview is not None:
        (folder / "preview.png").write_bytes(preview)
        paths.add("preview.png")
    library_id = scene.chstage.character.library_id or character.LIBRARY_ID
    description = character.complete_requirements(collect.package_description(scene), library_id)
    manifest = sidecars.build_manifest(
        description, entry, "preview.png" if preview is not None else None, format=character.package_format(library_id)
    )
    manifest["files"] = container.file_list(folder, paths)
    (folder / "manifest.json").write_bytes(container.json_bytes(manifest))
    names["materials"] = {name: material.name for name, material in blender_materials.items()}
    return manifest, names


def resolve(message, names, scene):
    """(object name, material name) of a validator message about the character GLB, or (None, None)."""
    file, _, text = message.partition(": ")
    if file != character.MODEL_FILE:
        return None, None
    material = re.search(r"material \d+ \(([^)]*)\)", text)
    if material:
        blender_name = names.get("materials", {}).get(material.group(1), material.group(1))
        owner = next(
            (
                obj.name
                for obj in scene.objects
                if any(s.material and s.material.name == blender_name for s in obj.material_slots)
            ),
            None,
        )
        return owner, blender_name
    rig = scene.chstage.character.rig
    if any(word in text for word in ("joint", "skin", "inverse bind", "root")) and rig is not None:
        return rig.name, None
    meshes = list(names.get("objects", {}))
    return (meshes[0], None) if len(meshes) == 1 and ("mesh" in text or "weights" in text) else (None, None)


def add_report(issues, report, names, scene):
    known = {message for _, message, _, _ in issues.items}
    for severity, messages in (("ERROR", report.errors), ("WARNING", report.warnings)):
        for message in messages:
            if message in known:
                continue
            obj, material = resolve(message, names, scene)
            issues.items.append((severity, message, obj, material))


def validator(scene, path):
    library = character_tools.library_path(scene)
    characters = character_tools.character_library_path(scene) if scene.chstage.character.library_profile else ""
    return Validator(path, library=library or None, character_library=characters or None)


def validate_scene(scene):
    issues = sidecars.Issues()
    with tempfile.TemporaryDirectory(prefix="chstage-validate-") as temporary:
        folder = Path(temporary) / package_file_name(scene)
        manifest, names = build_files(scene, folder, issues, render_preview_image=False)
        if manifest is not None:
            add_report(issues, validator(scene, folder).run(), names, scene)
    return issues.items


def export_package(scene, target, folder=None, custom_folder=None, keeper=None):
    """Write the package zip (and the development folder and a copy into Custom/Characters) when it is valid.
    Returns (issues, written paths)."""
    issues = sidecars.Issues()
    target = Path(target)
    written = []
    with tempfile.TemporaryDirectory(prefix="chstage-export-") as temporary:
        staging = Path(temporary) / target.name
        manifest, names = build_files(scene, staging, issues, keeper)
        if manifest is None:
            return issues.items, written
        archive = Path(temporary) / "zip" / target.name
        archive.parent.mkdir()
        container.write_zip(staging, archive)
        add_report(issues, validator(scene, archive).run(), names, scene)
        if any(severity == "ERROR" for severity, _, _, _ in issues.items):
            return issues.items, written
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(archive, target.with_name("." + target.name + ".tmp"))
        os.replace(target.with_name("." + target.name + ".tmp"), target)
        written.append(target)
        if folder is not None:
            folder = Path(folder)
            if folder.exists():
                shutil.rmtree(folder)
            shutil.copytree(staging, folder)
            written.append(folder)
        if custom_folder:
            copy = Path(custom_folder) / target.name
            if copy.resolve() != target.resolve():
                copy.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(archive, copy)
                written.append(copy)
    scene.chstage.export_path = str(target)
    return issues.items, written


def record_kept(scene):
    """Store the material and node rows that the opened Blender data gives (sidecars.Keeper)."""
    scene.view_layers[0].update()
    keeper = sidecars.Keeper("record")
    meshes = character_tools.character_meshes(scene)
    rig = scene.chstage.character.rig
    for obj in [rig, *meshes]:
        if obj is not None:
            keeper(collect.node_row(obj), obj.chstage, "node")
    for material in collect.exported_materials(meshes):
        keeper(collect.material_row(material), material.chstage, "material")
    keeper.store()


def open_package(path, library=None):  # noqa: C901
    """Make a new scene from a .chchar zip or development folder. Returns (scene, issues).
    library: the library for the helpers and the clips (None: the preference or next to the package; "": none)."""
    path = Path(path)
    if path.name == "manifest.json":
        path = path.parent
    issues = sidecars.Issues()
    report = Report()
    source = Source(path, report)
    if report.errors or "manifest.json" not in source.set:
        raise ValueError(f"{path}: not a package ({'; '.join(report.errors) or 'no manifest.json'})")
    manifest = json.loads(source.read("manifest.json"))
    if not str(manifest.get("format", "")).startswith("chchar/"):
        raise ValueError(f"{path}: not a character package (format {manifest.get('format')})")
    entry = manifest.get("entry", {})
    for key in ("model", "character"):
        if entry.get(key) not in source.set:
            raise ValueError(f"{path}: the package has no {key} file")
    library = character_tools.library_path(None, path) if library is None else library
    raw_character = json.loads(source.read(entry["character"]))
    description = character.open_character_json(raw_character)
    characters = ""
    if description["profile"] in character.PROFILES:
        role = character_tools.role_of(description["profile"])
    else:
        characters = character_tools.character_library_path(None, path)
        if not characters:
            raise ValueError(
                f"{path}: the profile '{description['profile']}' needs the GH3 character library "
                f"({description['library']}): set 'GH3 character library (.chlib)' in the add-on preferences"
            )
        clib = character_tools.character_library(characters)
        if description["profile"] not in clib.profiles:
            raise ValueError(f"{path}: the character library {clib.id} has no profile '{description['profile']}'")
        role = clib.role(description["profile"])
    for message in Validator(path, library=library or None, character_library=characters or None).run().errors:
        issues.error(f"package: {message}")

    scene = bpy.data.scenes.new(manifest.get("name") or manifest.get("id") or "CH Character")
    scene.render.fps, scene.render.fps_base = character_tools.CLIP_FPS, 1.0
    scene.chstage.source_package = str(path.resolve())
    p = scene.chstage.character
    p.enabled, p.profile, p.instrument = True, character.ROLE_PROFILES[role], description["instrument"]
    if characters:
        p.library_profile, p.library_id, p.animations = (
            description["profile"],
            description["library"],
            description["animations"],
        )
        p.character_library = str(Path(characters).resolve())
    if library:
        p.library = str(Path(library).resolve())
    skip = {entry["model"], entry["character"]}
    package_open.open_package_info(scene, manifest, source, skip)
    collection = bpy.data.collections.new("Character")
    scene.collection.children.link(collection)
    new, gltf, binary, nodes, mats, images = character_tools.import_glb(
        scene, source.read(entry["model"]), collection, mapped=True
    )
    package_open.open_nodes(gltf, nodes)
    package_open.open_materials(gltf, binary, mats, images, source, issues)
    for mesh in {obj.data for obj in new if obj.type == "MESH"}:
        package_open.remove_gh3_properties(mesh)
    rigs = [obj for obj in new if obj.type == "ARMATURE"]
    if len(rigs) != 1:
        issues.error(f"the character GLB has {len(rigs)} armatures; the format needs one skin")
    if rigs:
        p.rig = rigs[0]
    if "skeleton" in raw_character and rigs:
        # A character with its own skeleton: the GLB armature is the rig; the GH3 rig of the profile becomes the clip source.
        p.own_skeleton = True
        own = raw_character["skeleton"]
        skeleton_tools.fill_roles(scene, own.get("humanoid", {}))
        p.followers = json.dumps(own.get("followers", []), ensure_ascii=False)
        instrument = own.get("instrument", {})
        p.instrument_bone = instrument.get("bone", "")
        offset = instrument.get("offset", [0, 0, 0])
        p.instrument_offset = (offset[0], offset[2], offset[1])
        p.instrument_scale = instrument.get("scale", 0.0)
        for slot, name in raw_character.get("face", {}).items():
            setattr(p, skeleton_tools.FACE_PROPS[slot], name)
    if rigs:
        chain_tools.open_rows(scene, raw_character, source.read(entry["model"]))
    by_name = character_tools.mats_by_name(gltf, mats)
    for name in description["hairMaterials"]:
        found = by_name.get(name)
        if not found:
            issues.warn(f"character.json: hair material '{name}' is not in the GLB")
        for material in found or []:
            p.hair_materials.add().material = material
    for material in bpy.data.materials:
        for item in material.chstage.kept_images:
            if item.name in source.set:
                skip.add(item.name)
    package_open.open_kept_files(scene, source, manifest, path, skip)
    if library and p.rig is not None:
        helpers = bpy.data.collections.new("Character Helpers")
        scene.collection.children.link(helpers)
        try:
            lib = character_tools.library(library)
            if p.own_skeleton:
                parts = character_tools.helper_parts(scene, lib, role)
                made, *_ = character_tools.import_glb(
                    scene, parts["body"], helpers, bone_heuristic=character_tools.SOURCE_BONES
                )
                source = next(o for o in made if o.type == "ARMATURE")
                for obj in made:
                    if obj != source:
                        bpy.data.objects.remove(obj, do_unlink=True)
                character_tools.make_source_rig(source)
                p.source_rig = source
            character_tools.load_helpers(scene, helpers, lib, role, show_reference=False)
            character_tools.load_profile_data(scene, lib, role)
            character_tools.add_preview_camera(scene, helpers)
            if p.own_skeleton:
                skeleton_tools.fit_source_rig(scene)
        except (OSError, ValueError, KeyError) as error:
            issues.warn(f"the GH3 library {library} has no rig for {role} ({error}); no helpers and no clip preview")
    elif not library:
        issues.warn(
            "no GH3 library: set 'GH3 library (.chlib)' in the add-on preferences for the helpers, the clip preview, and the bind pose check"
        )
    record_kept(scene)
    return scene, issues.items
