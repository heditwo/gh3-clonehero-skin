import json
import textwrap

import bpy
from bpy.types import Panel, UIList

CATEGORY = "CH Stage"
HIDDEN_FIELDS = {"rna_type", "kept", "kept_extras", "source_name"}


def fields(item):
    names = []
    for p in item.bl_rna.properties:
        if p.identifier not in HIDDEN_FIELDS and p.type != "COLLECTION" and not p.identifier.endswith("_index") and p.identifier not in names:
            names.append(p.identifier)
    return names


class CHSTAGE_UL_rows(UIList):
    """One row for each item: its first fields. The fields of the active item are below the list."""

    def draw_item(self, context, layout, data, item, icon, active_data, active_property, index=0, flt_flag=0):
        row = layout.row(align=True)
        for name in fields(item)[:2]: row.prop(item, name, text="", emboss=False)


class CHSTAGE_UL_issues(UIList):
    def draw_item(self, context, layout, data, item, icon, active_data, active_property, index=0, flt_flag=0):
        row = layout.row(align=True)
        row.label(text=item.message, icon="ERROR" if item.severity == "ERROR" else "INFO")
        if item.object_name:
            row.operator("chstage.select_issue", text="", icon="RESTRICT_SELECT_OFF").index = index


def edit_list(layout, owner, holder, name, path, rows=3):
    """A list with add and remove buttons and the fields of the active item. path: data path from the owner ID."""
    row = layout.row()
    row.template_list("CHSTAGE_UL_rows", path, holder, name, holder, f"{name}_index", rows=rows)
    buttons = row.column(align=True)
    for action, icon in (("ADD", "ADD"), ("REMOVE", "REMOVE")):
        op = buttons.operator("chstage.list_edit", text="", icon=icon)
        op.owner, op.path, op.action = owner, path, action
    collection = getattr(holder, name)
    index = getattr(holder, f"{name}_index")
    if 0 <= index < len(collection):
        item = collection[index]
        names = fields(item)
        if len(names) > 1:
            column = layout.column(align=True)
            column.use_property_split = True
            for field in names: column.prop(item, field)


class SidebarPanel:
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = CATEGORY


class CHSTAGE_PT_stage(SidebarPanel, Panel):
    bl_label = "Stage Package"

    def draw(self, context):
        layout = self.layout
        row = layout.row(align=True)
        row.operator("chstage.new_stage", text="New", icon="FILE_NEW")
        row.operator("chstage.open_package", text="Open", icon="FILE_FOLDER")
        layout.operator_menu_enum("chstage.add_marker", "kind", text="Add Marker", icon="EMPTY_ARROWS")
        row = layout.row(align=True)
        row.operator("chstage.validate", icon="CHECKMARK")
        row.operator("chstage.export_package", icon="EXPORT")
        props = context.scene.chstage
        if props.export_path: layout.label(text=f"Last export: {props.export_path}", icon="PACKAGE")
        if props.validated:
            errors = sum(1 for i in props.issues if i.severity == "ERROR")
            layout.label(text=f"{errors} errors, {len(props.issues) - errors} warnings" if props.issues else "No issues",
                         icon="ERROR" if errors else "CHECKMARK")
            if props.issues:
                layout.template_list("CHSTAGE_UL_issues", "", props, "issues", props, "issues_index", rows=4)
                if 0 <= props.issues_index < len(props.issues):
                    issue = props.issues[props.issues_index]
                    box = layout.box()
                    width = max(20, int(context.region.width / (7 * context.preferences.view.ui_scale)))
                    for line in textwrap.wrap(issue.message, width): box.label(text=line)
                    if issue.object_name: box.operator("chstage.select_issue", text=f"Select {issue.object_name}", icon="RESTRICT_SELECT_OFF").index = props.issues_index


class CHSTAGE_PT_manifest(SidebarPanel, Panel):
    bl_label = "Package Information"
    bl_parent_id = "CHSTAGE_PT_stage"
    bl_options = {"DEFAULT_CLOSED"}

    def draw(self, context):
        layout = self.layout
        package = context.scene.chstage.package
        for name in ("package_id", "version", "name", "description", "license", "homepage", "min_mod_version"):
            layout.prop(package, name)
        layout.prop(package, "preview_mode")
        if package.preview_mode == "IMAGE": layout.template_ID(package, "preview_image", open="image.open")
        row = layout.row(align=True)
        for name in ("redistribute", "modify", "commercial"): row.prop(package, name, toggle=True)
        layout.label(text="Authors")
        edit_list(layout, "SCENE", package, "authors", "chstage.package.authors", rows=2)
        layout.label(text="Credits")
        edit_list(layout, "SCENE", package, "credits", "chstage.package.credits", rows=2)
        layout.label(text="Required packages")
        edit_list(layout, "SCENE", package, "requires", "chstage.package.requires", rows=1)


class CHSTAGE_PT_object(SidebarPanel, Panel):
    bl_label = "Stage Object"

    @classmethod
    def poll(cls, context):
        return context.object is not None

    def draw(self, context):
        layout = self.layout
        obj = context.object; props = obj.chstage
        layout.prop(props, "marker")
        if props.marker == "BAND": layout.prop(props, "band_role")
        elif props.marker == "WAYPOINT": layout.prop(props, "waypoint_name")
        elif props.marker == "EFFECT": layout.prop(props, "effect")
        if props.marker != "NONE" or obj.type in ("CAMERA", "LIGHT"): return
        box = layout.box(); box.label(text="Lighting and visibility")
        box.prop(props, "light_group")
        box.prop(props, "visibility_group")
        if props.visibility_group: box.prop(props, "initially_visible")
        box = layout.box(); box.label(text="Light fixture")
        box.prop(props, "fixture_id")
        if props.fixture_id:
            box.prop(props, "fixture_fixed_position")
            box.prop(props, "fixture_object")
            row = box.row(); row.prop(props, "use_fixture_created"); sub = row.row(); sub.active = props.use_fixture_created; sub.prop(props, "fixture_created")
        box = layout.box(); box.label(text="Script object")
        box.prop(props, "script_object")
        if props.script_object:
            box.prop(props, "object_pivot_mode")
            if props.object_pivot_mode == "CUSTOM":
                box.prop(props, "object_pivot"); box.prop(props, "object_rotation")
            row = box.row(); row.prop(props, "use_object_created"); sub = row.row(); sub.active = props.use_object_created; sub.prop(props, "object_created")
            box.prop(props, "object_script")


def draw_point(layout, point, empty=False):
    row = layout.row(align=True); row.prop(point, "anchor_role"); row.prop(point, "anchor_part", text="")
    if point.anchor_part == "bone" and point.anchor_role != "world": layout.prop(point, "anchor_bone")
    if point.anchor_role not in ("world", "none") and point.anchor_part != "root":
        layout.label(text="Blender shows the point relative to the performer root", icon="INFO")
    row = layout.row(align=True); row.prop(point, "look_at_role")
    if point.look_at_role not in ("none", "world"): row.prop(point, "look_at_part", text="")
    if point.look_at_part == "bone" and point.look_at_role not in ("none", "world"): layout.prop(point, "look_at_bone")
    row = layout.row(); row.prop(point, "use_screen_offset", text=""); sub = row.row(); sub.active = point.use_screen_offset; sub.prop(point, "screen_offset")
    layout.prop(point, "use_rotation")
    if empty: layout.prop(point, "fov")


class CHSTAGE_PT_shot(SidebarPanel, Panel):
    bl_label = "Camera Shot"

    @classmethod
    def poll(cls, context):
        obj = context.object
        return obj is not None and (obj.type == "CAMERA" or (obj.type == "EMPTY" and obj.chstage.marker == "NONE"))

    def draw(self, context):
        layout = self.layout
        obj = context.object
        if obj.type == "EMPTY":
            layout.label(text="Settings when this empty is a shot point:")
            draw_point(layout, obj.chstage.point, empty=True)
            return
        shot = obj.chstage.shot
        layout.prop(shot, "enabled")
        if not shot.enabled:
            layout.label(text="Settings when this camera is a shot point:")
            draw_point(layout, obj.chstage.point)
            return
        layout.prop(shot, "shot_name")
        layout.prop(shot, "type"); layout.prop(shot, "control"); layout.prop(shot, "tag")
        layout.label(text="Groups (wide, guitarist, bassist, singer, drummer, orbit, ...)")
        edit_list(layout, "OBJECT", shot, "groups", "chstage.shot.groups", rows=2)
        box = layout.box(); box.label(text="First point: this camera")
        draw_point(box, obj.chstage.point)
        layout.label(text="More points")
        edit_list(layout, "OBJECT", shot, "more_points", "chstage.shot.more_points", rows=1)
        layout.operator("chstage.add_shot_point", icon="EMPTY_SINGLE_ARROW")
        col = layout.column(heading="Options")
        for flag, value in (("use_orbit_angle", "orbit_angle"), ("use_angular_speed", "angular_speed"), ("use_step_time", "step_time"),
                            ("use_step_count", "step_count"), ("use_step_direction", "step_direction"), ("use_height_ease", "height_ease")):
            row = col.row(); row.prop(shot, flag, text=""); sub = row.row(); sub.active = getattr(shot, flag); sub.prop(shot, value)
        col.prop(shot, "hand_cam"); col.prop(shot, "disable_orbit1"); col.prop(shot, "disable_orbit2")
        if shot.control == "Keyframes":
            layout.prop(shot, "interpolation")
            layout.label(text="Key times (seconds)")
            edit_list(layout, "OBJECT", shot, "times", "chstage.shot.times", rows=2)
        layout.label(text="Waypoints (empty: all)")
        edit_list(layout, "OBJECT", shot, "waypoints", "chstage.shot.waypoints", rows=1)


class CHSTAGE_PT_light(SidebarPanel, Panel):
    bl_label = "Stage Light"

    @classmethod
    def poll(cls, context):
        return context.object is not None and context.object.type == "LIGHT"

    def draw(self, context):
        layout = self.layout
        light = context.object.chstage.light
        layout.prop(light, "enabled")
        if not light.enabled: return
        for name in ("light_id", "group", "type", "script_object", "override_color", "intensity", "near", "far"):
            layout.prop(light, name)
        row = layout.row(); row.prop(light, "use_cone", text=""); sub = row.row(align=True); sub.active = light.use_cone
        sub.prop(light, "hotspot"); sub.prop(light, "field")


class CHSTAGE_PT_lighting(SidebarPanel, Panel):
    bl_label = "Lighting"
    bl_options = {"DEFAULT_CLOSED"}

    def draw(self, context):
        layout = self.layout
        props = context.scene.chstage
        layout.label(text="Snapshots")
        row = layout.row()
        row.template_list("UI_UL_list", "snapshots", props, "snapshots", props, "snapshots_index", rows=3)
        buttons = row.column(align=True)
        for action, icon in (("ADD", "ADD"), ("REMOVE", "REMOVE")):
            op = buttons.operator("chstage.list_edit", text="", icon=icon); op.owner, op.path, op.action = "SCENE", "chstage.snapshots", action
        if props.snapshots:
            index = min(props.snapshots_index, len(props.snapshots) - 1)
            snapshot = props.snapshots[index]
            layout.prop(snapshot, "name")
            row = layout.row(align=True)
            row.operator("chstage.snapshot", text="Capture", icon="IMPORT").action = "CAPTURE"
            row.operator("chstage.snapshot", text="Apply", icon="EXPORT").action = "APPLY"
            layout.label(text="Lights")
            edit_list(layout, "SCENE", snapshot, "lights", f"chstage.snapshots[{index}].lights", rows=2)
            layout.label(text="Beams")
            edit_list(layout, "SCENE", snapshot, "beams", f"chstage.snapshots[{index}].beams", rows=2)
        layout.label(text="Moods (intro and blackout are required)")
        edit_list(layout, "SCENE", props, "moods", "chstage.moods", rows=3)
        if props.moods:
            index = min(props.moods_index, len(props.moods) - 1)
            layout.label(text=f"Snapshots of the mood '{props.moods[index].mood}'")
            edit_list(layout, "SCENE", props.moods[index], "snapshots", f"chstage.moods[{index}].snapshots", rows=2)
        layout.label(text="Color presets")
        edit_list(layout, "SCENE", props, "colors", "chstage.colors", rows=1)


class CHSTAGE_PT_crowd_cameras(SidebarPanel, Panel):
    bl_label = "Crowd and Camera Notes"
    bl_options = {"DEFAULT_CLOSED"}

    def draw(self, context):
        layout = self.layout
        props = context.scene.chstage
        layout.label(text="Crowd")
        edit_list(layout, "SCENE", props, "crowd", "chstage.crowd", rows=2)
        layout.label(text="Camera notes")
        edit_list(layout, "SCENE", props, "camera_notes", "chstage.camera_notes", rows=2)


class CHSTAGE_UL_kept_files(UIList):
    def draw_item(self, context, layout, data, item, icon, active_data, active_property, index=0, flt_flag=0):
        from .package_open import source_status
        status = source_status(item)
        row = layout.row(align=True)
        row.label(text=item.path, icon="TEXT" if item.text else "FILE")
        if item.entry: row.label(text=item.entry)
        if status not in ("text", "ok"): row.label(text=status, icon="ERROR")


class CHSTAGE_PT_kept_files(SidebarPanel, Panel):
    bl_label = "Kept Package Files"
    bl_parent_id = "CHSTAGE_PT_stage"
    bl_options = {"DEFAULT_CLOSED"}

    @classmethod
    def poll(cls, context):
        return len(context.scene.chstage.kept_files) > 0

    def draw(self, context):
        props = context.scene.chstage
        layout = self.layout
        layout.label(text="Export writes these files again without a change")
        layout.template_list("CHSTAGE_UL_kept_files", "", props, "kept_files", props, "kept_files_index", rows=3)
        if props.source_package: layout.label(text=f"Source: {props.source_package}")


class CHSTAGE_PT_material(Panel):
    bl_label = "CH Stage Material"
    bl_space_type = "PROPERTIES"
    bl_region_type = "WINDOW"
    bl_context = "material"

    @classmethod
    def poll(cls, context):
        return context.material is not None

    def draw(self, context):
        layout = self.layout
        props = context.material.chstage
        layout.use_property_split = True
        layout.prop(props, "shading")
        layout.prop(props, "blend")
        if props.blend == "CUTOUT": layout.prop(props, "alpha_cutoff")
        elif props.blend in ("BLEND", "ADDITIVE"):
            row = layout.row(heading="Alpha test"); row.prop(props, "use_blend_cutoff", text="")
            sub = row.row(); sub.active = props.use_blend_cutoff; sub.prop(props, "alpha_cutoff", text="")
        layout.prop(props, "two_sided")
        if props.blend == "OPAQUE" and props.opaque_alpha != 1: layout.prop(props, "opaque_alpha")
        layout.prop(props, "color_scale")
        layout.prop(props, "lightmap")
        if props.lightmap:
            obj = context.object
            if obj is not None and obj.type == "MESH": layout.prop_search(props, "lightmap_uv", obj.data, "uv_layers")
            else: layout.prop(props, "lightmap_uv")
        layout.prop(props, "movie_slot")
        layout.prop(props, "sorted_transparency")
        if props.kept_extras:
            keys = ", ".join(sorted(json.loads(props.kept_extras)))
            layout.label(text=f"Kept GH3 data: {keys}", icon="LOCKED")
        col = layout.column(heading="Crowd")
        col.prop(props, "crowd_atlas"); col.prop(props, "crowd_role"); col.prop(props, "billboard")
        layout.prop(props, "use_flipbook")
        if props.use_flipbook:
            for name in ("flipbook_columns", "flipbook_rows", "flipbook_fps", "flipbook_offset"): layout.prop(props, name)


CLASSES = (CHSTAGE_UL_rows, CHSTAGE_UL_issues, CHSTAGE_UL_kept_files, CHSTAGE_PT_stage, CHSTAGE_PT_manifest, CHSTAGE_PT_kept_files, CHSTAGE_PT_object, CHSTAGE_PT_shot,
           CHSTAGE_PT_light, CHSTAGE_PT_lighting, CHSTAGE_PT_crowd_cameras, CHSTAGE_PT_material)


def register():
    for cls in CLASSES: bpy.utils.register_class(cls)


def unregister():
    for cls in reversed(CLASSES): bpy.utils.unregister_class(cls)
