import json
import textwrap

import bpy
from bpy.types import Panel, UIList

from . import actor_tools, camera_tools, groups, lighting_tools, scripts_ui, sidecars

CATEGORY = "CH Stage"
HIDDEN_FIELDS = {"rna_type", "kept", "kept_extras", "source_name"}


def fields(item):
    names = []
    for p in item.bl_rna.properties:
        if (
            p.identifier not in HIDDEN_FIELDS
            and p.type != "COLLECTION"
            and not p.identifier.endswith("_index")
            and p.identifier not in names
        ):
            names.append(p.identifier)
    return names


class CHSTAGE_UL_rows(UIList):
    """One row for each item: its first fields. The fields of the active item are below the list."""

    def draw_item(self, context, layout, data, item, icon, active_data, active_property, index=0, flt_flag=0):
        row = layout.row(align=True)
        for name in fields(item)[:2]:
            row.prop(item, name, text="", emboss=False)


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
            for field in names:
                column.prop(item, field)


def stage_scene(context):
    """Venue panels show in stage scenes only; a character scene has the CH Character tab."""
    return context.scene is not None and not context.scene.chstage.character.enabled


class SidebarPanel:
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = CATEGORY

    @classmethod
    def poll(cls, context):
        return stage_scene(context)


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
        draw_issues(layout, context)


def draw_issues(layout, context):
    """The last export and the issue list of Validate and Export (stage and character scenes)."""
    props = context.scene.chstage
    if props.export_path:
        layout.label(text=f"Last export: {props.export_path}", icon="PACKAGE")
    if props.validated:
        errors = sum(1 for i in props.issues if i.severity == "ERROR")
        layout.label(
            text=f"{errors} errors, {len(props.issues) - errors} warnings" if props.issues else "No issues",
            icon="ERROR" if errors else "CHECKMARK",
        )
        if props.issues:
            layout.template_list("CHSTAGE_UL_issues", "", props, "issues", props, "issues_index", rows=4)
            if 0 <= props.issues_index < len(props.issues):
                issue = props.issues[props.issues_index]
                box = layout.box()
                width = max(20, int(context.region.width / (7 * context.preferences.view.ui_scale)))
                for line in textwrap.wrap(issue.message, width):
                    box.label(text=line)
                if issue.object_name:
                    box.operator(
                        "chstage.select_issue", text=f"Select {issue.object_name}", icon="RESTRICT_SELECT_OFF"
                    ).index = props.issues_index


class CHSTAGE_PT_manifest(SidebarPanel, Panel):
    bl_label = "Package Information"
    bl_parent_id = "CHSTAGE_PT_stage"
    bl_options = {"DEFAULT_CLOSED"}

    def draw(self, context):
        draw_package_info(self.layout, context.scene.chstage.package)


def draw_package_info(layout, package):
    for name in ("package_id", "version", "name", "description", "license", "homepage", "min_mod_version"):
        layout.prop(package, name)
    layout.prop(package, "preview_mode")
    if package.preview_mode == "IMAGE":
        layout.template_ID(package, "preview_image", open="image.open")
    row = layout.row(align=True)
    for name in ("redistribute", "modify", "commercial"):
        row.prop(package, name, toggle=True)
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
        return context.object is not None and stage_scene(context)

    def draw(self, context):
        layout = self.layout
        obj = context.object
        props = obj.chstage
        if actor_tools.actor_root(obj) not in (None, obj):
            layout.label(
                text=f"Part of the actor '{actor_tools.actor_root(obj).name}' (see Actor)", icon="ARMATURE_DATA"
            )
            return
        layout.prop(props, "marker")
        if props.marker == "BAND":
            layout.prop(props, "band_role")
        elif props.marker == "WAYPOINT":
            layout.prop(props, "waypoint_name")
        elif props.marker == "EFFECT":
            layout.prop(props, "effect")
        if props.marker != "NONE" or obj.type in ("CAMERA", "LIGHT"):
            return
        box = layout.box()
        box.label(text="Lighting and visibility")
        box.prop(props, "light_group")
        box.prop(props, "visibility_group")
        if props.visibility_group:
            box.prop(props, "initially_visible")
        box = layout.box()
        box.label(text="Light fixture")
        box.prop(props, "fixture_id")
        if props.fixture_id:
            box.prop(props, "fixture_fixed_position")
            box.prop(props, "fixture_object")
            row = box.row()
            row.prop(props, "use_fixture_created")
            sub = row.row()
            sub.active = props.use_fixture_created
            sub.prop(props, "fixture_created")
        box = layout.box()
        box.label(text="Script object")
        box.prop(props, "script_object")
        if props.script_object:
            box.prop(props, "object_pivot_mode")
            if props.object_pivot_mode == "CUSTOM":
                box.prop(props, "object_pivot")
                box.prop(props, "object_rotation")
            row = box.row()
            row.prop(props, "use_object_created")
            sub = row.row()
            sub.active = props.use_object_created
            sub.prop(props, "object_created")
            box.prop(props, "object_script")


def draw_point(layout, point, empty=False, scene=None):
    row = layout.row(align=True)
    row.prop(point, "anchor_role")
    row.prop(point, "anchor_part", text="")
    if point.anchor_part == "bone" and point.anchor_role != "world":
        layout.prop(point, "anchor_bone")
    if point.anchor_role not in ("world", "none") and point.anchor_part != "root":
        frames = json.loads(scene.chstage.anchor_frames) if scene is not None and scene.chstage.anchor_frames else {}
        joint = (
            sidecars.joint_name(point.anchor_part, point.anchor_bone)
            if point.anchor_part != "bone" or point.anchor_bone
            else ""
        )
        if joint in frames.get(point.anchor_role, {}):
            layout.label(text="Blender shows the point relative to the rig bone", icon="ARMATURE_DATA")
        else:
            layout.label(text="Blender shows the point relative to the performer root", icon="INFO")
    row = layout.row(align=True)
    row.prop(point, "look_at_role")
    if point.look_at_role not in ("none", "world"):
        row.prop(point, "look_at_part", text="")
    if point.look_at_part == "bone" and point.look_at_role not in ("none", "world"):
        layout.prop(point, "look_at_bone")
    row = layout.row()
    row.prop(point, "use_screen_offset", text="")
    sub = row.row()
    sub.active = point.use_screen_offset
    sub.prop(point, "screen_offset")
    layout.prop(point, "use_rotation")
    if empty:
        layout.prop(point, "fov")


class CHSTAGE_PT_shot(SidebarPanel, Panel):
    bl_label = "Camera Shot"

    @classmethod
    def poll(cls, context):
        obj = context.object
        return (
            obj is not None
            and (obj.type == "CAMERA" or (obj.type == "EMPTY" and obj.chstage.marker == "NONE"))
            and stage_scene(context)
        )

    def draw(self, context):
        layout = self.layout
        obj = context.object
        if obj.type == "EMPTY":
            layout.label(text="Settings when this empty is a shot point:")
            draw_point(layout, obj.chstage.point, empty=True, scene=context.scene)
            return
        shot = obj.chstage.shot
        layout.prop(shot, "enabled")
        if not shot.enabled:
            layout.label(text="Settings when this camera is a shot point:")
            draw_point(layout, obj.chstage.point, scene=context.scene)
            return
        layout.prop(shot, "shot_name")
        layout.prop(shot, "type")
        layout.prop(shot, "control")
        layout.prop(shot, "tag")
        layout.label(text="Groups (wide, guitarist, bassist, singer, drummer, orbit, ...)")
        edit_list(layout, "OBJECT", shot, "groups", "chstage.shot.groups", rows=2)
        box = layout.box()
        box.label(text="First point: this camera")
        draw_point(box, obj.chstage.point, scene=context.scene)
        layout.label(text="More points")
        edit_list(layout, "OBJECT", shot, "more_points", "chstage.shot.more_points", rows=1)
        layout.operator("chstage.add_shot_point", icon="EMPTY_SINGLE_ARROW")
        col = layout.column(heading="Options")
        for flag, value in (
            ("use_orbit_angle", "orbit_angle"),
            ("use_angular_speed", "angular_speed"),
            ("use_step_time", "step_time"),
            ("use_step_count", "step_count"),
            ("use_step_direction", "step_direction"),
            ("use_height_ease", "height_ease"),
        ):
            row = col.row()
            row.prop(shot, flag, text="")
            sub = row.row()
            sub.active = getattr(shot, flag)
            sub.prop(shot, value)
        col.prop(shot, "hand_cam")
        col.prop(shot, "disable_orbit1")
        col.prop(shot, "disable_orbit2")
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
        return context.object is not None and context.object.type == "LIGHT" and stage_scene(context)

    def draw(self, context):
        layout = self.layout
        light = context.object.chstage.light
        layout.prop(light, "enabled")
        if not light.enabled:
            return
        for name in ("light_id", "group", "type", "script_object", "override_color", "intensity", "near", "far"):
            layout.prop(light, name)
        row = layout.row()
        row.prop(light, "use_cone", text="")
        sub = row.row(align=True)
        sub.active = light.use_cone
        sub.prop(light, "hotspot")
        sub.prop(light, "field")


class CHSTAGE_PT_lighting(SidebarPanel, Panel):
    bl_label = "Lighting"
    bl_options = {"DEFAULT_CLOSED"}

    def draw(self, context):
        layout = self.layout
        props = context.scene.chstage
        box = layout.box()
        box.prop(props, "lighting_preview", icon="LIGHT_SUN")
        row = box.row(align=True)
        row.active = props.lighting_preview
        row.prop(props, "preview_mood", text="")
        if props.preview_mood not in ("", "NONE"):
            row.operator("chstage.preview_step", text="", icon="FRAME_NEXT")
        snapshot = lighting_tools.preview_snapshot(context.scene)
        if props.lighting_preview and snapshot is not None:
            box.label(text=f"Showing the snapshot '{snapshot.name}'", icon="RENDER_STILL")
        box.prop(props, "beam_overlay")
        layout.label(text="Snapshots")
        row = layout.row()
        row.template_list("UI_UL_list", "snapshots", props, "snapshots", props, "snapshots_index", rows=3)
        buttons = row.column(align=True)
        for action, icon in (("ADD", "ADD"), ("REMOVE", "REMOVE")):
            op = buttons.operator("chstage.list_edit", text="", icon=icon)
            op.owner, op.path, op.action = "SCENE", "chstage.snapshots", action
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


class CHSTAGE_PT_actor(SidebarPanel, Panel):
    bl_label = "Actor"

    @classmethod
    def poll(cls, context):
        return actor_tools.actor_root(context.object) is not None and stage_scene(context)

    def draw(self, context):
        layout = self.layout
        root = actor_tools.actor_root(context.object)
        props = root.chstage.actor
        layout.label(text=f"Actor root: {root.name}", icon="ARMATURE_DATA")
        layout.prop(props, "actor_name")
        layout.prop(props, "model_path")
        changed = actor_tools.has_model(root) and props.blob is not None and actor_tools.model_changed(root)
        if props.blob is None:
            status, icon = "Export writes the Blender model", "EXPORT"
        elif props.model_mode == "PACKED" or not changed:
            status, icon = "Export writes the packed GLB of the package", "PACKAGE"
        else:
            status, icon = "The model changed: export writes the Blender model", "MODIFIER"
        if props.model_mode == "BLENDER":
            status, icon = "Export writes the Blender model", "EXPORT"
        layout.label(text=status, icon=icon)
        layout.prop(props, "model_mode")
        col = layout.column(align=True)
        col.prop(props, "clip")
        col.prop(props, "playback")
        col.prop(props, "fps")
        clips = json.loads(props.clips_json or "[]") or actor_tools.clip_names(root)
        loaded = set(actor_tools.clip_names(root))
        box = layout.box()
        box.label(text=f"Clips: {len(loaded)} of {len(clips) or len(loaded)} in Blender")
        flow = box.grid_flow(columns=2, align=True)
        for name in clips:
            row = flow.row(align=True)
            row.enabled = name in loaded
            row.operator(
                "chstage.actor_play_clip", text=name, icon="PLAY" if name == props.clip else "ACTION"
            ).clip = name
        if props.blob is not None and len(loaded) < len(clips):
            box.operator("chstage.actor_clips", icon="IMPORT").all_clips = True
        col = layout.column()
        col.prop(props, "light_group")
        col.prop(props, "role")
        row = col.row()
        row.prop(props, "use_initially_visible", text="")
        sub = row.row()
        sub.active = props.use_initially_visible
        sub.prop(props, "initially_visible")
        row = col.row()
        row.prop(props, "use_created", text="")
        sub = row.row()
        sub.active = props.use_created
        sub.prop(props, "created")
        col.prop(props, "script_object")
        col.prop(props, "script")
        col.prop(props, "animation_events")
        col.prop(props, "write_scale")
        layout.label(text="Placement and scale: the transform of the actor root", icon="INFO")
        layout.label(text="Cue actions")
        if root == context.object:
            edit_list(layout, "OBJECT", props, "actions", "chstage.actor.actions", rows=1)
        else:
            for action in props.actions:
                layout.label(text=f"{action.name}: {action.clips}")


class CHSTAGE_PT_actors(SidebarPanel, Panel):
    bl_label = "Actors"
    bl_options = {"DEFAULT_CLOSED"}

    def draw(self, context):
        layout = self.layout
        roots = actor_tools.actor_roots(context.scene)
        if not roots:
            layout.label(text="No actors. Select an armature and click Make Actor")
        for root in roots:
            props = root.chstage.actor
            row = layout.row(align=True)
            row.operator(
                "chstage.select_actor", text=actor_tools.actor_name(root), icon="ARMATURE_DATA", emboss=False
            ).name = root.name
            row.label(text=props.clip or "first clip")
            if props.blob is None:
                row.label(text="", icon="EXPORT")
        layout.operator("chstage.make_actor", icon="ADD")


class CHSTAGE_UL_scripts(UIList):
    def draw_item(self, context, layout, data, item, icon, active_data, active_property, index=0, flt_flag=0):
        row = layout.row(align=True)
        row.label(
            text=item.label,
            icon={"OBJECT": "OBJECT_DATA", "EVENT": "SCRIPT", "SETTING": "PREFERENCES"}.get(item.kind, "DOT"),
        )
        if item.kind == "EVENT" and item.node_id:
            row.label(text=item.node_id)
        row.label(text=item.info)
        if item.objects:
            row.operator("chstage.script_jump", text="", icon="RESTRICT_SELECT_OFF", emboss=False).index = index

    def filter_items(self, context, data, propname):
        rows = getattr(data, propname)
        text = self.filter_name.lower()
        flags = [
            self.bitflag_filter_item if not text or text in f"{r.label} {r.info} {r.node_id} {r.objects}".lower() else 0
            for r in rows
        ]
        return flags, []


class CHSTAGE_PT_scripts(SidebarPanel, Panel):
    bl_label = "Script Bank"
    bl_options = {"DEFAULT_CLOSED"}

    @classmethod
    def poll(cls, context):
        return stage_scene(context) and scripts_ui.script_item(context.scene) is not None

    def draw(self, context):
        layout = self.layout
        scene = context.scene
        props = scene.chstage
        row = layout.row(align=True)
        row.prop(props, "script_view", expand=True)
        row.operator("chstage.scripts_reload", text="", icon="FILE_REFRESH")
        layout.template_list("CHSTAGE_UL_scripts", "", props, "script_rows", props, "script_rows_index", rows=6)
        layout.label(text="Read-only view of the GH3 scripts; simple values can change", icon="INFO")
        if not 0 <= props.script_rows_index < len(props.script_rows):
            return
        item = props.script_rows[props.script_rows_index]
        box = layout.box()
        if item.kind == "OBJECT":
            box.label(text=f"Object {item.label}")
            if item.info:
                box.label(text=item.info)
            if item.objects:
                box.label(text=f"Blender: {item.objects}", icon="OBJECT_DATA")
                box.operator("chstage.script_jump", icon="RESTRICT_SELECT_OFF").index = props.script_rows_index
            for path, label, kind, value, editable in scripts_ui.node_rows(scene, item.node_id):
                row = box.row(align=True)
                row.label(text=label)
                row.label(text=value)
                if editable:
                    op = row.operator("chstage.script_set", text="", icon="GREASEPENCIL")
                    op.path, op.value = path, value
        elif item.kind == "EVENT":
            box.label(text=f"Event: {item.label}")
            box.label(text=f"Script: {item.info}")
            if item.node_id:
                box.label(text=f"Object: {item.node_id}")
            if item.objects:
                box.operator("chstage.script_jump", icon="RESTRICT_SELECT_OFF").index = props.script_rows_index
        else:
            box.label(text=f"{item.label}: {item.info}")
            if item.editable:
                op = box.operator("chstage.script_set", icon="GREASEPENCIL")
                op.path, op.value = item.path, item.info


class CHSTAGE_UL_shots(UIList):
    def draw_item(self, context, layout, data, item, icon, active_data, active_property, index=0, flt_flag=0):
        shot = item.chstage.shot
        row = layout.row(align=True)
        row.label(
            text=shot.shot_name or item.name, icon="VIEW_CAMERA" if context.scene.camera == item else "CAMERA_DATA"
        )
        row.label(text=shot.type)
        row.label(text=", ".join(g.name for g in shot.groups))

    def filter_items(self, context, data, propname):
        objects = getattr(data, propname)
        scene = context.scene
        text = self.filter_name.lower()
        flags = [
            self.bitflag_filter_item
            if camera_tools.shot_visible(obj, scene)
            and (not text or text in (obj.chstage.shot.shot_name or obj.name).lower())
            else 0
            for obj in objects
        ]
        order = sorted(range(len(objects)), key=lambda i: (objects[i].chstage.order, objects[i].name))
        return flags, [order.index(i) for i in range(len(objects))]


class CHSTAGE_PT_shots(SidebarPanel, Panel):
    bl_label = "Camera Shots"
    bl_options = {"DEFAULT_CLOSED"}

    def draw(self, context):
        layout = self.layout
        scene = context.scene
        props = scene.chstage
        row = layout.row(align=True)
        row.operator_menu_enum("chstage.shot_group", "group", text=props.shot_group or "All groups", icon="FILTER")
        row.prop(props, "shot_type", text="")
        layout.template_list("CHSTAGE_UL_shots", "", scene, "objects", props, "shots_index", rows=6)
        index = props.shots_index
        if (
            0 <= index < len(scene.objects)
            and scene.objects[index].type == "CAMERA"
            and scene.objects[index].chstage.shot.enabled
        ):
            obj = scene.objects[index]
            row = layout.row(align=True)
            row.operator("chstage.preview_shot", text="Preview", icon="VIEW_CAMERA").name = obj.name
            for number, ref in enumerate(obj.chstage.shot.more_points, start=2):
                if ref.target is not None:
                    row.operator("chstage.preview_point", text=f"Point {number}").name = ref.target.name
        box = layout.box()
        box.label(text="Camera bone anchors")
        total, exact = camera_tools.anchor_point_count(scene)
        if props.anchor_frames:
            box.label(text=f"Rig proxies: {exact} of {total} bone-anchored points on rig bones", icon="ARMATURE_DATA")
            box.label(text=bpy.path.basename(props.rig_library) or props.rig_library)
            box.operator("chstage.rigs", text="Remove Rig Proxies", icon="X").action = "UNLOAD"
        else:
            if total:
                box.label(text=f"{total} bone-anchored points use the performer root", icon="ERROR")
            box.operator("chstage.rigs", text="Load GH3 Rig Proxies", icon="ARMATURE_DATA").action = "LOAD"


class CHSTAGE_PT_groups(SidebarPanel, Panel):
    bl_label = "Groups and Flags"
    bl_options = {"DEFAULT_CLOSED"}

    def group_row(self, layout, view_layer, kind, name, objects, label, extra=""):
        row = layout.row(align=True)
        hidden = groups.is_hidden(view_layer, objects)
        op = row.operator("chstage.group", text="", icon="HIDE_ON" if hidden else "HIDE_OFF", emboss=False)
        op.kind, op.name, op.action = kind, name, "TOGGLE"
        row.label(text=f"{label} ({len(objects)}){extra}")
        op = row.operator("chstage.group", text="", icon="RESTRICT_SELECT_OFF", emboss=False)
        op.kind, op.name, op.action = kind, name, "SELECT"
        op = row.operator("chstage.group", text="", icon="SOLO_ON", emboss=False)
        op.kind, op.name, op.action = kind, name, "ISOLATE"
        return row

    def draw(self, context):
        layout = self.layout
        scene = context.scene
        view_layer = context.view_layer
        row = layout.row(align=True)
        row.operator("chstage.start_state", text="Start State", icon="PLAY").action = "START"
        row.operator("chstage.start_state", text="Show All", icon="HIDE_OFF").action = "ALL"
        layout.label(text="Visibility groups")
        visibility = groups.visibility_groups(scene)
        if not visibility:
            layout.label(text="None", icon="DOT")
        for name, (objects, start) in visibility.items():
            state = {True: ", visible at start", False: ", hidden at start", None: ", mixed start state"}[start]
            self.group_row(layout, view_layer, "VISIBILITY", name, objects, name, state)
        layout.label(text="Node flags")
        for flag, label, _ in groups.FLAGS:
            objects = groups.flag_members(scene, flag)
            if objects:
                self.group_row(layout, view_layer, "FLAG", flag, objects, label)
        layout.label(text="Light groups")
        layout.prop(scene.chstage, "light_group_overlay")
        for name, objects in groups.light_groups(scene).items():
            self.group_row(layout, view_layer, "LIGHT", name, objects, name)


class CHSTAGE_UL_kept_files(UIList):
    def draw_item(self, context, layout, data, item, icon, active_data, active_property, index=0, flt_flag=0):
        from .package_open import source_status

        status = source_status(item)
        row = layout.row(align=True)
        row.label(text=item.path, icon="TEXT" if item.text else "PACKAGE" if item.blob else "FILE")
        if item.entry:
            row.label(text=item.entry)
        if status not in ("text", "ok"):
            row.label(text=status, icon="ERROR")


class CHSTAGE_PT_kept_files(SidebarPanel, Panel):
    bl_label = "Kept Package Files"
    bl_parent_id = "CHSTAGE_PT_stage"
    bl_options = {"DEFAULT_CLOSED"}

    @classmethod
    def poll(cls, context):
        return len(context.scene.chstage.kept_files) > 0 and stage_scene(context)

    def draw(self, context):
        props = context.scene.chstage
        layout = self.layout
        layout.label(text="Export writes these files again without a change")
        layout.template_list("CHSTAGE_UL_kept_files", "", props, "kept_files", props, "kept_files_index", rows=3)
        if props.source_package:
            layout.label(text=f"Source: {props.source_package}")


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
        if props.shading != "toon" and getattr(context.material, "mmd_material", None) is not None:
            layout.operator("chstage.toon_from_mmd", icon="IMPORT").scope = "ACTIVE"
        layout.prop(props, "blend")
        if props.blend == "CUTOUT":
            layout.prop(props, "alpha_cutoff")
        elif props.blend in ("BLEND", "ADDITIVE"):
            row = layout.row(heading="Alpha test")
            row.prop(props, "use_blend_cutoff", text="")
            sub = row.row()
            sub.active = props.use_blend_cutoff
            sub.prop(props, "alpha_cutoff", text="")
        layout.prop(props, "two_sided")
        if props.blend == "OPAQUE" and props.opaque_alpha != 1:
            layout.prop(props, "opaque_alpha")
        layout.prop(props, "color_scale")
        layout.prop(props, "lightmap")
        if props.lightmap:
            obj = context.object
            if obj is not None and obj.type == "MESH":
                layout.prop_search(props, "lightmap_uv", obj.data, "uv_layers")
            else:
                layout.prop(props, "lightmap_uv")
        if props.shading == "toon":
            self.draw_toon(layout, props)
        layout.prop(props, "hair_layer")
        layout.prop(props, "movie_slot")
        layout.prop(props, "sorted_transparency")
        if props.kept_extras:
            keys = ", ".join(sorted(json.loads(props.kept_extras)))
            layout.label(text=f"Kept GH3 data: {keys}", icon="LOCKED")
        col = layout.column(heading="Crowd")
        col.prop(props, "crowd_atlas")
        col.prop(props, "crowd_role")
        col.prop(props, "billboard")
        layout.prop(props, "use_flipbook")
        if props.use_flipbook:
            for name in ("flipbook_columns", "flipbook_rows", "flipbook_fps", "flipbook_offset"):
                layout.prop(props, name)

    @staticmethod
    def draw_toon(layout, props):
        box = layout.box()
        box.label(text="Toon", icon="SHADING_RENDERED")
        for name in (
            "toon_threshold",
            "toon_softness",
            "toon_shade_image",
            "toon_shade_color",
            "toon_mask_image",
            "toon_sphere_image",
        ):
            box.prop(props, name)
        if props.toon_sphere_image:
            box.prop(props, "toon_sphere_mode")
        box.prop(props, "toon_rim_width")
        if props.toon_rim_width > 0:
            box.prop(props, "toon_rim_color")
        box.prop(props, "use_outline")
        if props.use_outline:
            for name in ("outline_width", "outline_color", "outline_textured"):
                box.prop(props, name)
        row = box.row(align=True)
        on = row.operator("chstage.toon_preview", text="Preview", icon="HIDE_OFF")
        on.enable, on.scope = True, "ACTIVE"
        off = row.operator("chstage.toon_preview", text="glTF material", icon="HIDE_ON")
        off.enable, off.scope = False, "ACTIVE"
        box.operator("chstage.toon_from_mmd", icon="IMPORT").scope = "ACTIVE"


CLASSES = (
    CHSTAGE_UL_rows,
    CHSTAGE_UL_issues,
    CHSTAGE_UL_kept_files,
    CHSTAGE_UL_scripts,
    CHSTAGE_UL_shots,
    CHSTAGE_PT_stage,
    CHSTAGE_PT_manifest,
    CHSTAGE_PT_kept_files,
    CHSTAGE_PT_object,
    CHSTAGE_PT_actor,
    CHSTAGE_PT_shot,
    CHSTAGE_PT_light,
    CHSTAGE_PT_lighting,
    CHSTAGE_PT_shots,
    CHSTAGE_PT_actors,
    CHSTAGE_PT_scripts,
    CHSTAGE_PT_groups,
    CHSTAGE_PT_crowd_cameras,
    CHSTAGE_PT_material,
)


def register():
    for cls in CLASSES:
        bpy.utils.register_class(cls)


def unregister():
    for cls in reversed(CLASSES):
        bpy.utils.unregister_class(cls)
