import traceback

import bpy
from bpy.props import EnumProperty, IntProperty, StringProperty
from bpy.types import Operator
from mathutils import Vector

from . import collect, sidecars, template

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


class CHSTAGE_OT_validate(Operator):
    """Check the stage with the package validator. Click an issue to select its object"""
    bl_idname = "chstage.validate"
    bl_label = "Validate"
    bl_options = {"REGISTER"}

    def execute(self, context):
        scene = context.scene
        try:
            issues = collect.validate_scene(scene)
        except Exception as error:  # Show the cause in the interface; the console gets the traceback.
            traceback.print_exc()
            self.report({"ERROR"}, f"Validation stopped: {error}")
            return {"CANCELLED"}
        store = scene.chstage.issues
        store.clear()
        for severity, message, obj, material in issues:
            item = store.add()
            item.severity, item.message, item.object_name, item.material_name = severity, message, obj or "", material or ""
        scene.chstage.validated = True
        errors = sum(1 for i in issues if i[0] == "ERROR")
        # An ERROR report makes bpy.ops raise in scripts; the issue list holds the errors.
        self.report({"WARNING" if errors else "INFO"}, f"{errors} errors, {len(issues) - errors} warnings")
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


CLASSES = (CHSTAGE_OT_list_edit, CHSTAGE_OT_new_stage, CHSTAGE_OT_add_marker, CHSTAGE_OT_validate, CHSTAGE_OT_select_issue,
           CHSTAGE_OT_snapshot, CHSTAGE_OT_add_shot_point)


def register():
    for cls in CLASSES: bpy.utils.register_class(cls)


def unregister():
    for cls in reversed(CLASSES): bpy.utils.unregister_class(cls)
