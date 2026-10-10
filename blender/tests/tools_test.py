"""Headless tests of the venue tools (part of blender/tests/blender_test.py): packed files, actors, the script bank
browser, the shot browser and the proxy rigs, the lighting preview and the beams, and the groups.

run() gets the template scene (with the edits of blender_test.py), the opened venue scene, and its export.

Blender 5.2.1 crashes when a script runs an operator under a view_layer context override for a scene that is older
than the last scene that an operator made (BLENDER-ADDON.md 2.9). The tests therefore use the operators only on the
newest scene, and the add-on functions on the template scene.
"""

import json
import os
import shutil
import sys
import zipfile
from pathlib import Path

import bpy

ROOT = Path(__file__).resolve().parents[2]
from pipeline.package import compare  # noqa: E402

PACKAGES = ROOT / "assets" / "packages"


def export(scene, path, **options):
    with bpy.context.temp_override(scene=scene, view_layer=scene.view_layers[0]):
        status = bpy.ops.chstage.export_package(filepath=str(path), **options)
    return status, [(i.severity, i.message, i.object_name) for i in scene.chstage.issues]


def export_function(scene, path):
    """Export without the operator (for scenes that are not the newest scene)."""
    package_export = sys.modules[__package_name__ + ".package_export"]
    issues, written = package_export.export_package(scene, path)
    return ({"FINISHED"} if written else {"CANCELLED"}), [(s, m, o) for s, m, o, _ in issues]


def read(path, name):
    with zipfile.ZipFile(path) as archive:
        return archive.read(name)


def json_paths(a, b):
    """The paths of the differences between two JSON values."""
    return sorted({d[0] for d in compare.json_differences(a, b)})


def opened_scene(path):
    return next(s for s in bpy.data.scenes if s.chstage.source_package == str(Path(path).resolve()))


__package_name__ = ""


def run(check, module, template, venue, venue_export, output, summary):
    global __package_name__
    __package_name__ = module
    tools = {
        name: sys.modules[f"{module}.{name}"]
        for name in ("actor_tools", "camera_tools", "groups", "lighting_tools", "scripts_ui", "blobs", "collect")
    }
    validator = sys.modules[module + ".vendor.package_format.validate"]
    skip = os.environ.get("CHSTAGE_TEST_SKIP", "").split(",")
    if "scripts" not in skip:
        scripts(check, tools, venue, venue_export, output)
    if "lighting" not in skip:
        lighting(check, tools, venue, venue_export, output)
    if "actors" not in skip:
        actors(check, tools, validator, output)
    if "cameras" not in skip:
        cameras(check, tools, template, output)
    if "groups" not in skip:
        groups(check, tools, template, output)


def actors(check, tools, validator, output):
    studio = PACKAGES / "example.studio.chstage"
    if not studio.is_file():
        return
    actor_tools = tools["actor_tools"]
    moved = output / "moved" / studio.name
    moved.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(studio, moved)
    with bpy.context.temp_override(scene=bpy.data.scenes[0]):
        bpy.ops.chstage.open_package(filepath=str(moved))
    scene = opened_scene(moved)
    moved.unlink()
    roots = actor_tools.actor_roots(scene)
    root = roots[0] if roots else None
    parts = actor_tools.descendants(root) if root else []
    armature = next((o for o in parts if o.type == "ARMATURE"), None)
    check(
        "actor: imported as armature and mesh with its clip",
        len(roots) == 1
        and root.chstage.actor.actor_name == "sign"
        and armature is not None
        and any(o.type == "MESH" for o in parts)
        and actor_tools.clip_name(armature.animation_data.action) == "turn"
        and scene.render.fps == 60
        and root.chstage.actor.blob is not None
        and scene.frame_end > scene.frame_start,
        [(o.name, o.type) for o in parts],
    )
    check("actor: not in stage.glb", not any(obj in tools["collect"].exported_objects(scene) for obj in [root, *parts]))
    target = output / "actors" / "unchanged.chstage"
    status, issues = export(scene, target)
    differences = compare.compare_packages(studio, target) if target.is_file() else {"failures": ["not written"]}
    check(
        "packed files: export without the source package",
        status == {"FINISHED"} and not differences["failures"],
        (differences["failures"], issues),
    )

    root.chstage.actor.role = "sign/1"
    root.chstage.actor.playback = "hold"
    root.location.x += 1
    scene.view_layers[0].update()
    target = output / "actors" / "fields.chstage"
    export(scene, target)
    source_actors, actors_json = json.loads(read(studio, "actors.json")), json.loads(read(target, "actors.json"))
    paths = json_paths(source_actors, actors_json)
    check(
        "actor: placement and fields edit changes only that row",
        read(target, "sign.glb") == read(studio, "sign.glb")
        and actors_json["actors"]["sign"]["role"] == "sign/1"
        and actors_json["actors"]["sign"]["playback"] == "hold"
        and all(p.startswith(".actors.sign.") for p in paths)
        and ".actors.sign.transform.position[0]" in paths,
        paths,
    )

    mesh = next(o for o in parts if o.type == "MESH")
    mesh.data.vertices[0].co.x += 0.05
    mesh.data.update()
    target = output / "actors" / "model.chstage"
    status, issues = export(scene, target)
    model = read(target, "sign.glb") if target.is_file() else b""
    gltf = json.loads(model[20 : 20 + int.from_bytes(model[12:16], "little")]) if model else {}
    paths = [c["target"]["path"] for a in gltf.get("animations", []) for c in a["channels"]]
    check(
        "actor: a changed model is exported from Blender",
        status == {"FINISHED"}
        and model != read(studio, "sign.glb")
        and validator.validate(target).ok
        and [a["name"] for a in gltf.get("animations", [])] == ["turn"]
        and "scale" not in paths
        and len(gltf.get("skins", [])) == 1,
        (issues, paths),
    )
    target = output / "actors" / "blender.chstage"
    mesh.data.vertices[0].co.x -= 0.05
    mesh.data.update()
    status, issues = export(scene, target, actor_models="BLENDER")
    check(
        "actor: Blender model export of an unchanged actor is valid",
        status == {"FINISHED"} and validator.validate(target).ok,
        issues,
    )
    check("actor: unchanged again after the edit is undone", not actor_tools.model_changed(root))

    # The active object of the view layer is the context object (an object override would stay during the glTF import).
    scene.view_layers[0].objects.active = root
    with bpy.context.temp_override(scene=scene, view_layer=scene.view_layers[0]):
        status = bpy.ops.chstage.actor_clips(all_clips=True)
    check(
        "actor: load all clips",
        status == {"FINISHED"} and actor_tools.clip_names(root) == ["turn"] and not actor_tools.model_changed(root),
        actor_tools.clip_names(root),
    )
    armature = next(o for o in actor_tools.descendants(root) if o.type == "ARMATURE")

    # A new actor from an armature.
    copy = armature.copy()
    copy.data = armature.data.copy()
    scene.collection.objects.link(copy)
    copy.parent = None
    scene.view_layers[0].objects.active = copy
    with bpy.context.temp_override(scene=scene, view_layer=scene.view_layers[0]):
        status = bpy.ops.chstage.make_actor(name="new-actor")
    new_root = actor_tools.actor_root(copy)
    check(
        "actor: make actor from an armature",
        status == {"FINISHED"}
        and new_root is not None
        and new_root.chstage.actor.blob is None
        and actor_tools.model_path(new_root) == "new-actor.glb",
    )
    bpy.data.objects.remove(copy)
    bpy.data.objects.remove(new_root)


def scripts(check, tools, scene, venue_export, output):
    ui = tools["scripts_ui"]
    props = scene.chstage
    props.script_view = "OBJECTS"
    data = ui.bank(scene)
    if data is None:
        check("script bank: venue has scripts.json", False)
        return
    count = sum(
        len(data["definitions"][f"{tools['collect'].sidecars.qb_key(n):08x}"][1]) for n in data.get("nodes", [])
    )
    linked = [i for i, row in enumerate(props.script_rows) if row.objects]
    check(
        "script bank: objects view",
        len(props.script_rows) == count and linked,
        (len(props.script_rows), count, len(linked)),
    )
    if linked:
        props.script_rows_index = linked[0]
        with bpy.context.temp_override(scene=scene, view_layer=scene.view_layers[0]):
            bpy.ops.chstage.script_jump(index=linked[0])
        names = props.script_rows[linked[0]].objects.split(", ")
        check(
            "script bank: jump to object",
            scene.view_layers[0].objects.active is not None and scene.view_layers[0].objects.active.name in names,
            names[:3],
        )
    props.script_view = "EVENTS"
    kinds = [row.label for row in props.script_rows]
    check("script bank: events view", kinds and kinds[0] == "startup" and "object startup" in kinds, sorted(set(kinds)))
    props.script_view = "OBJECTS"
    node = props.script_rows[0].node_id
    rows = ui.node_rows(scene, node)
    position = next(r for r in rows if r[1].startswith("pos "))
    locked = next(r for r in rows if r[1].startswith("name "))
    with bpy.context.temp_override(scene=scene, view_layer=scene.view_layers[0]):
        status = bpy.ops.chstage.script_set(path=position[0], value="1.5, 2.5, 3.5")
        refused = bpy.ops.chstage.script_set(path=locked[0], value="other")
    target = output / "scripts" / "edited.chstage"
    export(scene, target)
    before, after = json.loads(read(venue_export, "scripts.json")), json.loads(read(target, "scripts.json"))
    paths = json_paths(before, after)
    check(
        "script bank: set a simple value",
        status == {"FINISHED"}
        and refused == {"CANCELLED"}
        and len(paths) == 3
        and all(".definitions." in p for p in paths),
        paths,
    )
    with bpy.context.temp_override(scene=scene, view_layer=scene.view_layers[0]):
        bpy.ops.chstage.script_set(path=position[0], value=position[3])
    export(scene, target)
    check(
        "script bank: value set back gives the source bank",
        json_paths(before, json.loads(read(target, "scripts.json"))) == [],
    )


def lighting(check, tools, scene, venue_export, output):
    lt = tools["lighting_tools"]
    props = scene.chstage
    saved_world = scene.world
    lights = [o for o in scene.objects if o.type == "LIGHT" and o.chstage.light.enabled]
    props.preview_mood = "NONE"
    props.lighting_preview = True
    snapshot = lt.preview_snapshot(scene)
    powered = [o for o in lights if o.data.energy > 0]
    check(
        "lighting preview: snapshot on the Blender lights",
        snapshot is not None
        and scene.world.name == lt.PREVIEW_WORLD
        and powered
        and props.preview_world == saved_world,
        (len(lights), len(powered)),
    )
    row = next(r for r in snapshot.lights if r.light in powered)
    energy = row.light.data.energy
    row.intensity *= 2
    check(
        "lighting preview: live update",
        abs(row.light.data.energy - 2 * energy) < 1e-3 * energy,
        (energy, row.light.data.energy),
    )
    row.intensity /= 2
    mood = props.moods[0]
    props.preview_mood = mood.mood
    check("lighting preview: mood", lt.preview_snapshot(scene).name == mood.snapshots[0].name, mood.mood)
    lines = lt.overlay_lines(scene)
    beams = sum(1 for b in lt.preview_snapshot(scene).beams if b.range > 0)
    check(
        "beam overlay: one cone for each beam",
        len([l for l in lines if l[0][:3] != (1.0, 0.8, 0.1)]) == beams,
        (len(lines), beams),
    )
    props.lighting_preview = False
    check(
        "lighting preview: off gives the scene world back", scene.world == saved_world and props.preview_world is None
    )
    target = output / "lighting" / "after-preview.chstage"
    export(scene, target)
    check("lighting preview: export does not change", not compare.compare_packages(venue_export, target)["failures"])


def cameras(check, tools, scene, output):
    ct = tools["camera_tools"]
    props = scene.chstage
    props.shot_group, props.shot_type = "wide", "ALL"
    visible = [o.name for o in scene.objects if ct.shot_visible(o, scene)]
    props.shot_group = ""
    props.shot_type = "stage"
    stage = [o.name for o in scene.objects if ct.shot_visible(o, scene)]
    props.shot_type = "ALL"
    check(
        "shot browser: filter by group and type",
        visible == ["wide"] and set(stage) == {"wide", "guitar"},
        (visible, stage),
    )
    props.shots_index = scene.objects.find("guitar")
    check("shot browser: preview sets the scene camera", scene.camera == scene.objects["guitar"])
    props.shots_index = scene.objects.find("wide")
    library = ct.find_library(PACKAGES / "x.chstage")
    if not library:
        return
    guitar = scene.objects["guitar"]
    before = guitar.matrix_world.copy()
    first = output / "rigs" / "root.chstage"
    _, issues_root = export_function(scene, first)
    roles = ct.load_rigs(scene, library, keep_offsets=True)
    rigged = output / "rigs" / "rig.chstage"
    _, issues_rig = export_function(scene, rigged)
    offsets = [json.loads(read(p, "cameras.json"))["shots"]["guitar"]["points"][0] for p in (first, rigged)]

    def warnings(issues):
        return [m for s, m, o in issues if s == "WARNING" and o == "guitar"]

    moved = (guitar.matrix_world.translation - before.translation).length
    check(
        "proxy rig: bone anchor follows the rig bone and keeps its offset",
        set(roles) == {"guitarist", "bassist", "singer", "drummer"}
        and moved > 0.1
        and not json_paths(offsets[0], offsets[1])
        and warnings(issues_root)
        and not warnings(issues_rig),
        (roles, moved, json_paths(offsets[0], offsets[1]), warnings(issues_rig)),
    )
    proxies = [o for o in scene.objects if o.chstage.marker == "PROXY" and o.type == "ARMATURE"]
    check(
        "proxy rig: rigs are proxies, never exported",
        len(proxies) == 4 and not any(o in tools["collect"].exported_objects(scene) for o in proxies),
    )
    ct.unload_rigs(scene)
    check(
        "proxy rig: remove gives the performer root again",
        (guitar.matrix_world.translation - before.translation).length < 1e-4
        and not ct.rig_collection(scene).all_objects,
    )


def groups(check, tools, scene, output):
    gt = tools["groups"]
    view_layer = scene.view_layers[0]
    visibility = gt.visibility_groups(scene)
    check(
        "groups: visibility group with its start state",
        visibility.get("crowd-lighters", ([], None))[1] is False
        and [o.name for o in visibility["crowd-lighters"][0]] == ["right"],
        {k: [o.name for o in v[0]] for k, v in visibility.items()},
    )
    members = gt.members_of(scene, "VISIBILITY", "crowd-lighters")
    gt.set_hidden(view_layer, members, not gt.is_hidden(view_layer, members))
    hidden = scene.objects["right"].hide_get(view_layer=view_layer)
    gt.set_hidden(view_layer, members, not gt.is_hidden(view_layer, members))
    shown = not scene.objects["right"].hide_get(view_layer=view_layer)
    gt.show_start_state(scene, view_layer)
    start = scene.objects["right"].hide_get(view_layer=view_layer) and not scene.objects["floor"].hide_get(
        view_layer=view_layer
    )
    check("groups: show and hide, start state", hidden and shown and start)
    target = output / "groups" / "hidden.chstage"
    _, issues = export_function(scene, target)
    gt.set_hidden(view_layer, gt.scenery(scene), False)
    names = [
        n.get("name")
        for n in json.loads(
            read(target, "stage.glb")[20 : 20 + int.from_bytes(read(target, "stage.glb")[12:16], "little")]
        )["nodes"]
    ]
    check("groups: hidden objects are still exported", "right" in names, names)
    scene.chstage.light_group_overlay = True

    def same(obj, group):
        return max(abs(a - b) for a, b in zip(obj.color, gt.group_color(group))) < 1e-6

    check(
        "light group overlay: object colors by group",
        same(scene.objects["backdrop"], "Band")
        and same(scene.objects["floor"], "Stage")
        and set(gt.light_groups(scene)) >= {"Band", "Stage"},
    )
    scene.chstage.light_group_overlay = False
    flags = gt.flag_members(scene, "FIXTURES")
    check("node flags: fixtures", [o.name for o in flags] == ["moving-light"], [o.name for o in flags])
