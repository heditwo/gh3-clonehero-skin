import traceback
from pathlib import Path

import bpy
from bpy.props import BoolProperty, EnumProperty, IntProperty, StringProperty
from bpy.types import Operator
from bpy_extras.io_utils import ExportHelper, ImportHelper
from mathutils import Vector

from . import actor_tools, camera_tools, groups, lighting_tools, package_export, package_open, scripts_ui, sidecars, template, toon

OWNERS = {"SCENE": lambda c: c.scene, "OBJECT": lambda c: c.object, "MATERIAL": lambda c: c.material}


class CHSTAGE_OT_list_edit(Operator):
    """Add or remove an item of a list"""
    bl_idname = "chstage.list_edit"
    bl_label = "Edit list"
    bl_options = {"REGISTER", "UNDO", "INTERNAL"}

    owner: EnumProperty(items=[(k, k.title(), "") for k in OWNERS])
    path: StringProperty()
    action: EnumProperty(items=[("ADD", "Add", ""), ("REMOVE", "Remove", "")])

    def execute(self, context):
        data = OWNERS[self.owner](context)
        if data is None: return {"CANCELLED"}
        holder, _, name = self.path.rpartition(".")
        parent = data.path_resolve(holder) if holder else data
        collection = getattr(parent, name)
        index_name = f"{name}_index"
        if self.action == "ADD":
            collection.add()
            if hasattr(parent, index_name): setattr(parent, index_name, len(collection) - 1)
        elif len(collection):
            index = min(getattr(parent, index_name, len(collection) - 1), len(collection) - 1)
            collection.remove(index)
            if hasattr(parent, index_name): setattr(parent, index_name, max(0, index - 1))
        return {"FINISHED"}


class CHSTAGE_OT_new_stage(Operator):
    """Make a new scene with a minimal stage: scenery, band placements, cameras, lights, and lighting moods"""
    bl_idname = "chstage.new_stage"
    bl_label = "New Stage from Template"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        scene = template.create()
        if context.window is not None: context.window.scene = scene
        self.report({"INFO"}, f"Made the scene '{scene.name}'")
        return {"FINISHED"}


class CHSTAGE_OT_add_marker(Operator):
    """Add a band placement (with a proxy body), a waypoint, or an effect emitter at the 3D cursor"""
    bl_idname = "chstage.add_marker"
    bl_label = "Add Stage Marker"
    bl_options = {"REGISTER", "UNDO"}

    kind: EnumProperty(name="Kind", items=[("BAND", "Band placement", ""), ("WAYPOINT", "Waypoint", ""), ("EFFECT", "Effect emitter", "")])
    role: EnumProperty(name="Role", items=[(r, r.title(), "") for r in sidecars.ROLES])
    effect: EnumProperty(name="Effect", items=[(e, e.title(), "") for e in sidecars.EFFECTS])

    def execute(self, context):
        scene = context.scene; location = scene.cursor.location.copy()
        if self.kind == "BAND": obj = template.add_band_marker(scene, self.role, location)
        elif self.kind == "WAYPOINT": obj = template.add_waypoint(scene, f"{self.role}_walk01", location)
        else: obj = template.add_effect(scene, self.effect, location)
        for other in scene.objects: other.select_set(False)
        obj.select_set(True)
        if context.view_layer: context.view_layer.objects.active = obj
        return {"FINISHED"}


def store_issues(scene, issues):
    store = scene.chstage.issues
    store.clear()
    for severity, message, obj, material in issues:
        item = store.add()
        item.severity, item.message, item.object_name, item.material_name = severity, message, obj or "", material or ""
    scene.chstage.validated = True
    return sum(1 for i in issues if i[0] == "ERROR")


class CHSTAGE_OT_validate(Operator):
    """Check the stage with the package validator. Click an issue to select its object"""
    bl_idname = "chstage.validate"
    bl_label = "Validate"
    bl_options = {"REGISTER"}

    def execute(self, context):
        scene = context.scene
        try:
            issues = package_export.validate_scene(scene)
        except Exception as error:  # Show the cause in the interface; the console gets the traceback.
            traceback.print_exc()
            self.report({"ERROR"}, f"Validation stopped: {error}")
            return {"CANCELLED"}
        errors = store_issues(scene, issues)
        # An ERROR report makes bpy.ops raise in scripts; the issue list holds the errors.
        self.report({"WARNING" if errors else "INFO"}, f"{errors} errors, {len(issues) - errors} warnings")
        return {"FINISHED"}


def custom_stages_folder(context):
    addon = context.preferences.addons.get(__package__)
    return addon.preferences.custom_stages if addon is not None and addon.preferences is not None else ""


class CHSTAGE_OT_export(Operator, ExportHelper):
    """Write the stage as a venue package (.chstage). The add-on validates the package and writes it only when it is valid"""
    bl_idname = "chstage.export_package"
    bl_label = "Export .chstage"
    bl_options = {"REGISTER"}

    filename_ext = ".chstage"
    filter_glob: StringProperty(default="*.chstage", options={"HIDDEN"})
    write_folder: BoolProperty(name="Development folder", default=False,
                               description="Also write the unpacked package to unpacked/<name>.chstage next to the zip")
    copy_to_game: BoolProperty(name="Copy to Custom/Stages", default=False,
                               description="Also copy the package into the Custom/Stages folder of the add-on preferences")
    custom_folder: StringProperty(name="Custom/Stages folder", subtype="DIR_PATH", description="Empty: the folder of the add-on preferences")
    keep_source: BoolProperty(name="Keep unchanged source rows", default=True, options={"HIDDEN"},
                              description="Write the rows of the opened package again where the Blender data did not change (tests turn it off)")
    actor_models: EnumProperty(name="Actor models", options={"HIDDEN"}, default="SETTING",
                               items=[("SETTING", "Actor setting", ""), ("PACKED", "Packed GLB", ""), ("BLENDER", "Blender model", "")])

    def invoke(self, context, event):
        scene = context.scene
        folder = Path(bpy.path.abspath(scene.chstage.export_path)).parent if scene.chstage.export_path else Path(bpy.path.abspath("//") or Path.home())
        self.filepath = str(folder/package_export.package_file_name(scene))
        if not self.custom_folder: self.custom_folder = custom_stages_folder(context)
        return ExportHelper.invoke(self, context, event)

    def draw(self, context):
        layout = self.layout
        layout.prop(self, "write_folder")
        layout.prop(self, "copy_to_game")
        if self.copy_to_game: layout.prop(self, "custom_folder", text="")
        layout.prop(context.scene.chstage.package, "preview_mode")

    def execute(self, context):
        scene = context.scene
        target = Path(bpy.path.abspath(self.filepath))
        if target.suffix != ".chstage": target = target.with_name(target.name + ".chstage")
        custom = None
        if self.copy_to_game:
            custom = bpy.path.abspath(self.custom_folder or custom_stages_folder(context))
            if not custom:
                self.report({"WARNING"}, "Set the Custom/Stages folder in the add-on preferences or in the export options")
                return {"CANCELLED"}
        keeper = sidecars.Keeper("keep" if self.keep_source else "compute")
        try:
            issues, written = package_export.export_package(scene, target, package_export.development_folder(target) if self.write_folder else None,
                                                            custom, keeper, None if self.actor_models == "SETTING" else self.actor_models)
        except Exception as error:
            traceback.print_exc()
            self.report({"ERROR"}, f"Export stopped: {error}")
            return {"CANCELLED"}
        errors = store_issues(scene, issues)
        if errors:
            self.report({"WARNING"}, f"Not written: {errors} errors (see the CH Stage panel)")
            return {"CANCELLED"}
        self.report({"INFO"}, f"Wrote {', '.join(str(p) for p in written)} ({len(issues)} warnings)")
        return {"FINISHED"}


class CHSTAGE_OT_open(Operator, ImportHelper):
    """Make a new scene from a venue package (.chstage zip, or the manifest.json of a development folder)"""
    bl_idname = "chstage.open_package"
    bl_label = "Open .chstage"
    # No undo step: the open makes a new scene, and an undo step of a whole venue costs much memory.
    bl_options = {"REGISTER"}

    filename_ext = ".chstage"
    filter_glob: StringProperty(default="*.chstage;manifest.json", options={"HIDDEN"})

    def execute(self, context):
        prefs = camera_tools.preferences()
        try:
            scene, issues = package_open.open_package(bpy.path.abspath(self.filepath), all_clips=bool(prefs and prefs.import_all_clips))
        except Exception as error:
            traceback.print_exc()
            self.report({"ERROR"}, f"Open stopped: {error}")
            return {"CANCELLED"}
        if context.window is not None: context.window.scene = scene
        store_issues(scene, issues)
        scene.chstage.validated = bool(issues)
        self.report({"INFO"}, f"Opened '{scene.name}' ({len(issues)} messages)")
        return {"FINISHED"}


class CHSTAGE_OT_select_issue(Operator):
    """Select the object of this issue"""
    bl_idname = "chstage.select_issue"
    bl_label = "Select"
    bl_options = {"REGISTER", "UNDO", "INTERNAL"}

    index: IntProperty()

    def execute(self, context):
        scene = context.scene
        issue = scene.chstage.issues[self.index]
        obj = scene.objects.get(issue.object_name)
        if obj is None:
            self.report({"WARNING"}, "This issue has no object")
            return {"CANCELLED"}
        view_layer = context.view_layer if context.view_layer in scene.view_layers.values() else scene.view_layers[0]
        for other in scene.objects: other.select_set(False, view_layer=view_layer)
        if obj.hide_get(view_layer=view_layer): obj.hide_set(False, view_layer=view_layer)
        obj.select_set(True, view_layer=view_layer)
        view_layer.objects.active = obj
        if issue.material_name:
            index = next((i for i, s in enumerate(obj.material_slots) if s.material and s.material.name == issue.material_name), None)
            if index is not None: obj.active_material_index = index
        return {"FINISHED"}


class CHSTAGE_OT_snapshot(Operator):
    """Capture the light settings into the active snapshot, or apply the snapshot to the lights"""
    bl_idname = "chstage.snapshot"
    bl_label = "Snapshot"
    bl_options = {"REGISTER", "UNDO"}

    action: EnumProperty(items=[("CAPTURE", "Capture", ""), ("APPLY", "Apply", "")])

    def execute(self, context):
        props = context.scene.chstage
        if not props.snapshots: return {"CANCELLED"}
        entry = props.snapshots[min(props.snapshots_index, len(props.snapshots) - 1)]
        if self.action == "CAPTURE": template.capture_snapshot(entry, context.scene)
        else: template.apply_snapshot(entry)
        lighting_tools.refresh(context.scene)
        return {"FINISHED"}


class CHSTAGE_OT_add_shot_point(Operator):
    """Add an empty as the next point of the active camera shot"""
    bl_idname = "chstage.add_shot_point"
    bl_label = "Add Point"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        camera = context.object
        if camera is None or camera.type != "CAMERA": return {"CANCELLED"}
        point = bpy.data.objects.new(f"{camera.name}-point{len(camera.chstage.shot.more_points) + 2}", None)
        point.empty_display_type = "CONE"
        point.matrix_world = camera.matrix_world.copy()
        point.location += Vector((0.5, 0, 0))
        context.scene.collection.objects.link(point)
        for name in ("anchor_role", "anchor_part", "anchor_bone", "look_at_role", "look_at_part", "look_at_bone"):
            setattr(point.chstage.point, name, getattr(camera.chstage.point, name))
        camera.chstage.shot.more_points.add().target = point
        return {"FINISHED"}


def view_layer_of(context, scene):
    return context.view_layer if context.view_layer in scene.view_layers.values() else scene.view_layers[0]


def select_only(context, scene, objects):
    view_layer = view_layer_of(context, scene)
    for other in scene.objects:
        if other.select_get(view_layer=view_layer): other.select_set(False, view_layer=view_layer)
    for obj in objects:
        if obj.name not in view_layer.objects: continue
        if obj.hide_get(view_layer=view_layer): obj.hide_set(False, view_layer=view_layer)
        obj.select_set(True, view_layer=view_layer)
    if objects: view_layer.objects.active = objects[0]


def frame_selected(context):
    """Show the selection in the 3D views of the window (not in background mode)."""
    screen = context.screen
    for area in (screen.areas if screen else []):
        if area.type != "VIEW_3D": continue
        region = next((r for r in area.regions if r.type == "WINDOW"), None)
        if region is None: continue
        if area.spaces.active.region_3d.view_perspective == "CAMERA": area.spaces.active.region_3d.view_perspective = "PERSP"
        with context.temp_override(area=area, region=region):
            bpy.ops.view3d.view_selected()


class CHSTAGE_OT_make_actor(Operator):
    """Make an actor (animated scenery) of the selected armature and its skinned meshes: a new actor root becomes their parent"""
    bl_idname = "chstage.make_actor"
    bl_label = "Make Actor"
    bl_options = {"REGISTER", "UNDO"}

    name: StringProperty(name="Actor name", default="actor")

    @classmethod
    def poll(cls, context):
        obj = context.object
        return obj is not None and obj.type == "ARMATURE" and actor_tools.actor_root(obj) is None

    def execute(self, context):
        root = actor_tools.make_actor(context.scene, context.object, self.name)
        root.chstage.actor.fps = context.scene.render.fps
        select_only(context, context.scene, [root])
        return {"FINISHED"}


class CHSTAGE_OT_actor_clips(Operator):
    """Import the packed actor GLB again with all its clips (or only the clips that actors.json uses). Changes to the Blender model of this actor are lost"""
    bl_idname = "chstage.actor_clips"
    bl_label = "Load All Clips"
    bl_options = {"REGISTER", "UNDO"}

    all_clips: BoolProperty(name="All clips", default=True)

    def execute(self, context):
        root = actor_tools.actor_root(context.object)
        if root is None or root.chstage.actor.blob is None:
            self.report({"WARNING"}, "Select an actor of an opened package")
            return {"CANCELLED"}
        roots = actor_tools.reload_model(context.scene, root, self.all_clips)
        self.report({"INFO"}, f"Imported {len(actor_tools.clip_names(root))} clips for {len(roots)} actors")
        return {"FINISHED"}


class CHSTAGE_OT_actor_play_clip(Operator):
    """Show this clip on the actor (the active action of its armature)"""
    bl_idname = "chstage.actor_play_clip"
    bl_label = "Show Clip"
    bl_options = {"REGISTER", "UNDO", "INTERNAL"}

    clip: StringProperty()

    def execute(self, context):
        root = actor_tools.actor_root(context.object)
        if root is None: return {"CANCELLED"}
        for obj in actor_tools.descendants(root):
            if obj.type == "ARMATURE" and obj.animation_data: actor_tools.set_active_clip(obj, self.clip)
        action = actor_tools.clip_action(root, self.clip)
        if action is not None:
            context.scene.frame_start, context.scene.frame_end = int(action.frame_range[0]), max(int(action.frame_range[0]) + 1, int(action.frame_range[1]))
        return {"FINISHED"}


class CHSTAGE_OT_select_actor(Operator):
    """Select this actor"""
    bl_idname = "chstage.select_actor"
    bl_label = "Select Actor"
    bl_options = {"REGISTER", "UNDO", "INTERNAL"}

    name: StringProperty()

    def execute(self, context):
        obj = context.scene.objects.get(self.name)
        if obj is None: return {"CANCELLED"}
        select_only(context, context.scene, [obj])
        return {"FINISHED"}


class CHSTAGE_OT_scripts(Operator):
    """Read scripts.json again (after an edit in the text editor)"""
    bl_idname = "chstage.scripts_reload"
    bl_label = "Reload Script Bank"
    bl_options = {"REGISTER"}

    def execute(self, context):
        scripts_ui.rebuild(context.scene)
        return {"FINISHED"}


class CHSTAGE_OT_script_jump(Operator):
    """Select the Blender objects of this script object and show them"""
    bl_idname = "chstage.script_jump"
    bl_label = "Jump to Object"
    bl_options = {"REGISTER", "UNDO", "INTERNAL"}

    index: IntProperty(default=-1)

    def execute(self, context):
        scene = context.scene
        rows = scene.chstage.script_rows
        index = self.index if self.index >= 0 else scene.chstage.script_rows_index
        if not 0 <= index < len(rows) or not rows[index].objects:
            self.report({"WARNING"}, "No Blender object has this script object")
            return {"CANCELLED"}
        objects = [scene.objects[n] for n in rows[index].objects.split(", ") if n in scene.objects]
        select_only(context, scene, objects)
        frame_selected(context)
        return {"FINISHED"}


class CHSTAGE_OT_script_set(Operator):
    """Write the value into scripts.json. Only simple typed values (numbers, text, vectors, checksums) can change"""
    bl_idname = "chstage.script_set"
    bl_label = "Set Value"
    bl_options = {"REGISTER", "UNDO", "INTERNAL"}

    path: StringProperty()
    value: StringProperty(name="Value")

    def invoke(self, context, event):
        return context.window_manager.invoke_props_dialog(self)

    def execute(self, context):
        try:
            result = scripts_ui.set_value(context.scene, self.path, self.value)
        except (ValueError, KeyError, IndexError, TypeError) as error:
            self.report({"WARNING"}, f"Not changed: {error}")
            return {"CANCELLED"}
        self.report({"INFO"}, f"New value: {result}")
        return {"FINISHED"}


class CHSTAGE_OT_preview_shot(Operator):
    """Look through this camera shot in the 3D view"""
    bl_idname = "chstage.preview_shot"
    bl_label = "Preview Shot"
    bl_options = {"REGISTER", "INTERNAL"}

    name: StringProperty()

    def execute(self, context):
        scene = context.scene
        obj = scene.objects.get(self.name) or context.object
        if obj is None or obj.type != "CAMERA": return {"CANCELLED"}
        index = scene.objects.find(obj.name)
        if scene.chstage.shots_index != index: scene.chstage.shots_index = index
        else: camera_tools.preview_index(context, scene)
        return {"FINISHED"}


class CHSTAGE_OT_shot_group(Operator):
    """Show only the shots of a camera group"""
    bl_idname = "chstage.shot_group"
    bl_label = "Camera Group"
    bl_options = {"REGISTER", "INTERNAL"}
    bl_property = "group"

    _items = []

    def group_items(self, context):
        CHSTAGE_OT_shot_group._items[:] = [("*", "All groups", "")] + [(g, g, "") for g in camera_tools.shot_groups(context.scene)]
        return CHSTAGE_OT_shot_group._items

    group: EnumProperty(items=group_items)

    def execute(self, context):
        context.scene.chstage.shot_group = "" if self.group == "*" else self.group
        return {"FINISHED"}


class CHSTAGE_OT_preview_point(Operator):
    """Look through a later point of the active shot (an empty uses the default lens)"""
    bl_idname = "chstage.preview_point"
    bl_label = "Preview Point"
    bl_options = {"REGISTER", "INTERNAL"}

    name: StringProperty()

    def execute(self, context):
        obj = context.scene.objects.get(self.name)
        if obj is None: return {"CANCELLED"}
        camera_tools.view_through(context, context.scene, obj)
        return {"FINISHED"}


class CHSTAGE_OT_rigs(Operator):
    """Load the GH3 band rigs of the library as proxies at the band placements: bone-anchored cameras then follow the rig bones (first frame of the first clip)"""
    bl_idname = "chstage.rigs"
    bl_label = "GH3 Rig Proxies"
    bl_options = {"REGISTER", "UNDO"}

    action: EnumProperty(items=[("LOAD", "Load", ""), ("UNLOAD", "Remove", "")])

    def execute(self, context):
        scene = context.scene
        if self.action == "UNLOAD":
            camera_tools.unload_rigs(scene)
            return {"FINISHED"}
        library = camera_tools.find_library(scene.chstage.source_package or scene.chstage.export_path or None)
        if not library:
            self.report({"WARNING"}, "No GH3 library: set 'GH3 library (.chlib)' in the add-on preferences")
            return {"CANCELLED"}
        try:
            roles = camera_tools.load_rigs(scene, library, keep_offsets=True)
        except (OSError, ValueError, KeyError) as error:
            self.report({"WARNING"}, f"No rigs: {error}")
            return {"CANCELLED"}
        self.report({"INFO"}, f"Loaded the rigs of {', '.join(roles)}")
        return {"FINISHED"}


class CHSTAGE_OT_preview_step(Operator):
    """Show the next snapshot of the previewed mood"""
    bl_idname = "chstage.preview_step"
    bl_label = "Next Snapshot"
    bl_options = {"REGISTER", "INTERNAL"}

    def execute(self, context):
        context.scene.chstage.preview_step += 1
        return {"FINISHED"}


class CHSTAGE_OT_group(Operator):
    """Show, hide, or select the objects of a group (the viewport eye; export does not change)"""
    bl_idname = "chstage.group"
    bl_label = "Group"
    bl_options = {"REGISTER", "UNDO", "INTERNAL"}

    kind: EnumProperty(items=[("VISIBILITY", "Visibility group", ""), ("LIGHT", "Light group", ""), ("FLAG", "Node flag", "")])
    name: StringProperty()
    action: EnumProperty(items=[("TOGGLE", "Show or hide", ""), ("SELECT", "Select", ""), ("ISOLATE", "Show only", "")])

    def execute(self, context):
        scene = context.scene
        view_layer = view_layer_of(context, scene)
        objects = groups.members_of(scene, self.kind, self.name)
        if self.action == "TOGGLE":
            groups.set_hidden(view_layer, objects, not groups.is_hidden(view_layer, objects))
        elif self.action == "SELECT":
            select_only(context, scene, objects)
        else:
            groups.set_hidden(view_layer, groups.scenery(scene), True)
            groups.set_hidden(view_layer, objects, False)
        return {"FINISHED"}


class CHSTAGE_OT_start_state(Operator):
    """Show the scenery as the game starts it (hide the objects that start hidden or not created), or show all scenery"""
    bl_idname = "chstage.start_state"
    bl_label = "Start State"
    bl_options = {"REGISTER", "UNDO"}

    action: EnumProperty(items=[("START", "Start state", ""), ("ALL", "Show all", "")])

    def execute(self, context):
        scene = context.scene
        view_layer = view_layer_of(context, scene)
        if self.action == "START": groups.show_start_state(scene, view_layer)
        else: groups.set_hidden(view_layer, groups.scenery(scene), False)
        return {"FINISHED"}


def chosen_materials(context, scope):
    if scope == "ACTIVE": return [context.material] if context.material is not None else []
    found = {}
    for obj in context.selected_objects:
        for slot in obj.material_slots:
            if slot.material is not None: found[slot.material.name] = slot.material
    return list(found.values())


SCOPES = [("SELECTED", "Selected objects", "All materials of the selected objects"), ("ACTIVE", "Active material", "")]


class CHSTAGE_OT_toon_preview(Operator):
    """Show the toon settings in the viewport (Material Preview or Rendered, EEVEE), or show the glTF material again.
    Export always writes the glTF material"""
    bl_idname = "chstage.toon_preview"
    bl_label = "Toon Preview"
    bl_options = {"REGISTER", "UNDO"}

    enable: BoolProperty(default=True)
    scope: EnumProperty(items=SCOPES, default="SELECTED")

    def execute(self, context):
        count = toon.set_preview(chosen_materials(context, self.scope), self.enable)
        self.report({"INFO"}, f"toon preview {'on' if self.enable else 'off'}: {count} materials")
        return {"FINISHED"}


class CHSTAGE_OT_toon_from_mmd(Operator):
    """Make toon materials from the MMD Tools data: the toon texture gives the threshold, softness, and shade color; the
    sphere map and the edge (color, size) carry over"""
    bl_idname = "chstage.toon_from_mmd"
    bl_label = "Toon from MMD"
    bl_options = {"REGISTER", "UNDO"}

    scope: EnumProperty(items=SCOPES, default="SELECTED")

    def execute(self, context):
        converted, skipped = 0, []
        for material in chosen_materials(context, self.scope):
            notes = toon.from_mmd(material)
            if notes is None: skipped.append(material.name); continue
            converted += 1
            for note in notes: self.report({"INFO"}, f"{material.name}: {note}")
            toon.refresh(material)
        if skipped: self.report({"WARNING"}, f"no MMD data (MMD Tools): {', '.join(skipped[:5])}")
        self.report({"INFO"}, f"{converted} toon materials from MMD")
        return {"FINISHED"} if converted else {"CANCELLED"}


CLASSES = (CHSTAGE_OT_list_edit, CHSTAGE_OT_new_stage, CHSTAGE_OT_add_marker, CHSTAGE_OT_validate, CHSTAGE_OT_export, CHSTAGE_OT_open,
           CHSTAGE_OT_select_issue, CHSTAGE_OT_snapshot, CHSTAGE_OT_add_shot_point, CHSTAGE_OT_make_actor, CHSTAGE_OT_actor_clips,
           CHSTAGE_OT_actor_play_clip, CHSTAGE_OT_select_actor, CHSTAGE_OT_scripts, CHSTAGE_OT_script_jump, CHSTAGE_OT_script_set,
           CHSTAGE_OT_preview_shot, CHSTAGE_OT_shot_group, CHSTAGE_OT_preview_point, CHSTAGE_OT_rigs, CHSTAGE_OT_preview_step, CHSTAGE_OT_group, CHSTAGE_OT_start_state,
           CHSTAGE_OT_toon_preview, CHSTAGE_OT_toon_from_mmd)


def register():
    for cls in CLASSES: bpy.utils.register_class(cls)


def unregister():
    for cls in reversed(CLASSES): bpy.utils.unregister_class(cls)
