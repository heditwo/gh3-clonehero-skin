"""Operators and panels of the character tools (sidebar tab CH Character)."""

import traceback
from pathlib import Path

import bpy
from bpy.props import BoolProperty, EnumProperty, StringProperty
from bpy.types import Operator, Panel, UIList
from bpy_extras.io_utils import ExportHelper, ImportHelper

from . import camera_tools, character_package, character_tools, panels, properties, sidecars
from .operators import select_only, store_issues

CATEGORY = "CH Character"


def character_scene(context):
    return context.scene is not None and context.scene.chstage.character.enabled


_PROFILE_ITEMS = []


def profile_items(self, context):
    """The fixed profiles, then the profiles of the GH3 character library (when the add-on finds it)."""
    items = list(properties.PROFILE_ITEMS)
    path = (
        bpy.path.abspath(self.character_library)
        if getattr(self, "character_library", "")
        else camera_tools.find_character_library()
    )
    try:
        clib = character_tools.character_library(path) if path else None
    except (OSError, ValueError):
        clib = None
    if clib is not None:
        items += [
            (profile, f"{clib.label(profile)}", f"{clib.id} profile {profile}, band role {clib.role(profile)}")
            for profile in sorted(clib.profiles)
        ]
    _PROFILE_ITEMS[:] = [
        (identifier, name, description, index) for index, (identifier, name, description) in enumerate(items)
    ]
    return _PROFILE_ITEMS


class CHSTAGE_OT_new_character(Operator):
    """Make a new character scene on a GH3 rig: the rig of the profile (a fixed profile from the library, or a GH3 guitarist of the character library), the GH3 body as a reference, and the attachment preview"""

    bl_idname = "chstage.new_character"
    bl_label = "New Character from GH3 Rig"
    bl_options = {"REGISTER", "UNDO"}

    profile: EnumProperty(name="Profile", items=profile_items)
    library: StringProperty(
        name="GH3 library", subtype="FILE_PATH", description="gh3-band-v1.chlib. Empty: the add-on preference"
    )
    character_library: StringProperty(
        name="GH3 character library",
        subtype="FILE_PATH",
        description="gh3-characters-v1.chlib, for the profiles of the GH3 guitarists. Empty: the add-on preference, "
        "or the file next to the GH3 library",
    )
    use_gh3_body: BoolProperty(
        name="Start from the GH3 body",
        description="The GH3 body becomes the character mesh (else it is only a reference)",
    )
    own_skeleton: BoolProperty(
        name="Use my model's skeleton",
        description="Keep the skeleton of your model (MMD, VRM, Mixamo, Unreal names): "
        "the game retargets the GH3 clips of the profile to it. Then add your model and click Use Selected Armature",
    )

    def invoke(self, context, event):
        if not self.library:
            self.library = camera_tools.find_library() or ""
        return context.window_manager.invoke_props_dialog(self, width=420)

    def execute(self, context):
        library = bpy.path.abspath(self.library) if self.library else camera_tools.find_library()
        if not library or not Path(library).exists():
            self.report({"WARNING"}, "No GH3 library: select gh3-band-v1.chlib, or set it in the add-on preferences")
            return {"CANCELLED"}
        characters = bpy.path.abspath(self.character_library) if self.character_library else None
        try:
            scene = character_tools.new_character(
                self.profile,
                library,
                self.use_gh3_body and not self.own_skeleton,
                character_library_file=characters,
                own_skeleton=self.own_skeleton,
            )
        except Exception as error:
            traceback.print_exc()
            self.report({"ERROR"}, f"New character stopped: {error}")
            return {"CANCELLED"}
        if context.window is not None:
            context.window.scene = scene
        self.report({"INFO"}, f"Made the scene '{scene.name}'")
        return {"FINISHED"}


class CHSTAGE_OT_open_character(Operator, ImportHelper):
    """Make a new scene from a character package (.chchar zip, or the manifest.json of a development folder)"""

    bl_idname = "chstage.open_character"
    bl_label = "Open .chchar"
    bl_options = {"REGISTER"}

    filename_ext = ".chchar"
    filter_glob: StringProperty(default="*.chchar;manifest.json", options={"HIDDEN"})

    def execute(self, context):
        try:
            scene, issues = character_package.open_package(bpy.path.abspath(self.filepath))
        except Exception as error:
            traceback.print_exc()
            self.report({"ERROR"}, f"Open stopped: {error}")
            return {"CANCELLED"}
        if context.window is not None:
            context.window.scene = scene
        store_issues(scene, issues)
        scene.chstage.validated = bool(issues)
        self.report({"INFO"}, f"Opened '{scene.name}' ({len(issues)} messages)")
        return {"FINISHED"}


class CHSTAGE_OT_validate_character(Operator):
    """Check the character with the package validator (bind pose against the profile, weights, materials). Click an issue to select its object"""

    bl_idname = "chstage.validate_character"
    bl_label = "Validate"
    bl_options = {"REGISTER"}

    def execute(self, context):
        try:
            issues = character_package.validate_scene(context.scene)
        except Exception as error:
            traceback.print_exc()
            self.report({"ERROR"}, f"Validation stopped: {error}")
            return {"CANCELLED"}
        errors = store_issues(context.scene, issues)
        self.report({"WARNING" if errors else "INFO"}, f"{errors} errors, {len(issues) - errors} warnings")
        return {"FINISHED"}


def custom_characters_folder(context):
    prefs = camera_tools.preferences()
    return prefs.custom_characters if prefs is not None else ""


class CHSTAGE_OT_export_character(Operator, ExportHelper):
    """Write the character as a character package (.chchar). The add-on validates the package and writes it only when it is valid"""

    bl_idname = "chstage.export_character"
    bl_label = "Export .chchar"
    bl_options = {"REGISTER"}

    filename_ext = ".chchar"
    filter_glob: StringProperty(default="*.chchar", options={"HIDDEN"})
    write_folder: BoolProperty(
        name="Development folder",
        description="Also write the unpacked package to unpacked/<name>.chchar next to the zip",
    )
    copy_to_game: BoolProperty(
        name="Copy to Custom/Characters", description="Also copy the package into the Custom/Characters folder"
    )
    custom_folder: StringProperty(
        name="Custom/Characters folder", subtype="DIR_PATH", description="Empty: the folder of the add-on preferences"
    )
    keep_source: BoolProperty(name="Keep unchanged source rows", default=True, options={"HIDDEN"})

    @classmethod
    def poll(cls, context):
        return character_scene(context)

    def invoke(self, context, event):
        scene = context.scene
        folder = (
            Path(bpy.path.abspath(scene.chstage.export_path)).parent
            if scene.chstage.export_path
            else Path(bpy.path.abspath("//") or Path.home())
        )
        self.filepath = str(folder / character_package.package_file_name(scene))
        if not self.custom_folder:
            self.custom_folder = custom_characters_folder(context)
        return ExportHelper.invoke(self, context, event)

    def draw(self, context):
        layout = self.layout
        layout.prop(self, "write_folder")
        layout.prop(self, "copy_to_game")
        if self.copy_to_game:
            layout.prop(self, "custom_folder", text="")
        layout.prop(context.scene.chstage.package, "preview_mode")

    def execute(self, context):
        scene = context.scene
        target = Path(bpy.path.abspath(self.filepath))
        if target.suffix != ".chchar":
            target = target.with_name(target.name + ".chchar")
        custom = None
        if self.copy_to_game:
            custom = bpy.path.abspath(self.custom_folder or custom_characters_folder(context))
            if not custom:
                self.report(
                    {"WARNING"}, "Set the Custom/Characters folder in the add-on preferences or in the export options"
                )
                return {"CANCELLED"}
        try:
            issues, written = character_package.export_package(
                scene,
                target,
                character_package.development_folder(target) if self.write_folder else None,
                custom,
                sidecars.Keeper("keep" if self.keep_source else "compute"),
            )
        except Exception as error:
            traceback.print_exc()
            self.report({"ERROR"}, f"Export stopped: {error}")
            return {"CANCELLED"}
        errors = store_issues(scene, issues)
        if errors:
            self.report({"WARNING"}, f"Not written: {errors} errors (see the CH Character panel)")
            return {"CANCELLED"}
        self.report({"INFO"}, f"Wrote {', '.join(str(p) for p in written)} ({len(issues)} warnings)")
        return {"FINISHED"}


def target_meshes(context):
    scene = context.scene
    objects = [o for o in (context.selected_objects or []) if o.type == "MESH" and not character_tools.is_helper(o)]
    if (
        not objects
        and context.object is not None
        and context.object.type == "MESH"
        and not character_tools.is_helper(context.object)
    ):
        objects = [context.object]
    return [o for o in objects if o.name in scene.objects]


class CHSTAGE_OT_transfer_weights(Operator):
    """Give the selected meshes the weights of the GH3 reference body (nearest face, interpolated), then at most 4 influences with a sum of 1. The mesh transform is applied and the rig becomes the parent"""

    bl_idname = "chstage.transfer_weights"
    bl_label = "Transfer Weights from the GH3 Body"
    bl_options = {"REGISTER", "UNDO"}

    exclude_hair: BoolProperty(
        name="Skip the GH3 hair cards",
        default=True,
        description="Do not take weights from the hair cards of the reference body",
    )

    @classmethod
    def poll(cls, context):
        return character_scene(context) and context.mode == "OBJECT"

    def execute(self, context):
        meshes = target_meshes(context)
        if not meshes:
            self.report({"WARNING"}, "Select the meshes of the character")
            return {"CANCELLED"}
        total = empty = 0
        try:
            for obj in meshes:
                count, without = character_tools.transfer_weights(context.scene, obj, exclude_hair=self.exclude_hair)
                total += count
                empty += without
        except ValueError as error:
            self.report({"WARNING"}, str(error))
            return {"CANCELLED"}
        self.report(
            {"WARNING" if empty else "INFO"},
            f"Weights for {total} vertices of {len(meshes)} meshes"
            + (f"; {empty} vertices without weight" if empty else ""),
        )
        return {"FINISHED"}


class CHSTAGE_OT_limit_weights(Operator):
    """Keep the 4 largest rig bone weights of each vertex and make their sum 1"""

    bl_idname = "chstage.limit_weights"
    bl_label = "Limit and Normalize"
    bl_options = {"REGISTER", "UNDO"}

    @classmethod
    def poll(cls, context):
        return character_scene(context) and context.scene.chstage.character.rig is not None and context.mode == "OBJECT"

    def execute(self, context):
        rig = context.scene.chstage.character.rig
        empty = sum(character_tools.limit_and_normalize(obj, rig) for obj in target_meshes(context))
        self.report({"WARNING" if empty else "INFO"}, f"{empty} vertices without weight" if empty else "Done")
        return {"FINISHED"}


class CHSTAGE_OT_assign_hair(Operator):
    """Give the selected vertices (or the whole mesh) full weight on the nearest GH3 hair bone of the chosen chain"""

    bl_idname = "chstage.assign_hair"
    bl_label = "Assign to Hair Bones"
    bl_options = {"REGISTER", "UNDO"}

    selected_only: BoolProperty(name="Selected vertices only", default=True)

    @classmethod
    def poll(cls, context):
        obj = context.object
        return (
            character_scene(context) and obj is not None and obj.type == "MESH" and not character_tools.is_helper(obj)
        )

    def execute(self, context):
        obj = context.object
        mode = obj.mode
        if mode == "EDIT":
            bpy.ops.object.mode_set(mode="OBJECT")
        try:
            count = character_tools.assign_hair(
                context.scene, obj, context.scene.chstage.character.hair_chain, self.selected_only
            )
        except ValueError as error:
            self.report({"WARNING"}, str(error))
            return {"CANCELLED"}
        finally:
            if mode == "EDIT":
                bpy.ops.object.mode_set(mode="EDIT")
        self.report({"INFO"}, f"{count} vertices on hair bones")
        return {"FINISHED"}


class CHSTAGE_OT_hair_material(Operator):
    """Add the active material to the hair materials (sorted hair transparency), or remove the selected hair material"""

    bl_idname = "chstage.hair_material"
    bl_label = "Hair Material"
    bl_options = {"REGISTER", "UNDO", "INTERNAL"}

    action: EnumProperty(items=[("ADD", "Add", ""), ("REMOVE", "Remove", "")])

    def execute(self, context):
        p = context.scene.chstage.character
        if self.action == "ADD":
            obj = context.object
            material = obj.active_material if obj is not None else None
            if material is None:
                self.report({"WARNING"}, "Select a mesh with the hair material active")
                return {"CANCELLED"}
            if any(item.material == material for item in p.hair_materials):
                return {"CANCELLED"}
            p.hair_materials.add().material = material
            material.chstage.sorted_transparency = True
            if material.chstage.blend == "OPAQUE":
                material.chstage.blend = "BLEND"
            p.hair_materials_index = len(p.hair_materials) - 1
        elif p.hair_materials:
            p.hair_materials.remove(min(p.hair_materials_index, len(p.hair_materials) - 1))
            p.hair_materials_index = max(0, p.hair_materials_index - 1)
        return {"FINISHED"}


class CHSTAGE_OT_clear_pose(Operator):
    """Remove the clip preview and show the bind pose"""

    bl_idname = "chstage.clear_pose"
    bl_label = "Bind Pose"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        character_tools.clear_pose(context.scene)
        return {"FINISHED"}


class CHSTAGE_OT_preview_clip(Operator):
    """Show a library clip on the rig (an action at 30 fps; the scene rate becomes 30)"""

    bl_idname = "chstage.preview_clip"
    bl_label = "Preview Clip"
    bl_options = {"REGISTER", "INTERNAL"}

    clip: StringProperty()

    def execute(self, context):
        try:
            action = character_tools.clip_action(context.scene, self.clip)
        except (OSError, ValueError, KeyError) as error:
            self.report({"WARNING"}, f"No preview: {error}")
            return {"CANCELLED"}
        self.report({"INFO"}, f"Showing {action.name}")
        return {"FINISHED"}


class CHSTAGE_OT_character_helpers(Operator):
    """Show or hide a helper (the GH3 reference body or the attachment preview), or load the helpers from the library again"""

    bl_idname = "chstage.character_helpers"
    bl_label = "Helpers"
    bl_options = {"REGISTER", "UNDO", "INTERNAL"}

    action: EnumProperty(
        items=[("REFERENCE", "Reference body", ""), ("ATTACHMENT", "Attachment", ""), ("RELOAD", "Reload", "")]
    )

    def execute(self, context):
        scene = context.scene
        p = scene.chstage.character
        view_layer = context.view_layer if context.view_layer in scene.view_layers.values() else scene.view_layers[0]
        if self.action == "RELOAD":
            library = character_tools.library_path(scene, scene.chstage.source_package or None)
            if not library or p.rig is None:
                self.report({"WARNING"}, "No GH3 library or no rig")
                return {"CANCELLED"}
            collection = next(
                (c for c in scene.collection.children if c.name.startswith("Character Helpers")), scene.collection
            )
            lib = character_tools.library(library)
            role = character_tools.role_of(p.profile)
            try:
                character_tools.load_helpers(scene, collection, lib, role)
                character_tools.load_profile_data(scene, lib, role)
            except (OSError, ValueError, KeyError) as error:
                self.report({"WARNING"}, str(error))
                return {"CANCELLED"}
            p.library = str(Path(library).resolve())
            return {"FINISHED"}
        obj = p.reference if self.action == "REFERENCE" else p.attachment
        if obj is None or obj.name not in view_layer.objects:
            return {"CANCELLED"}
        obj.hide_set(not obj.hide_get(view_layer=view_layer), view_layer=view_layer)
        return {"FINISHED"}


class CHSTAGE_OT_select_rig(Operator):
    """Select the rig of the character"""

    bl_idname = "chstage.select_rig"
    bl_label = "Select Rig"
    bl_options = {"REGISTER", "UNDO", "INTERNAL"}

    def execute(self, context):
        rig = context.scene.chstage.character.rig
        if rig is None:
            return {"CANCELLED"}
        select_only(context, context.scene, [rig])
        return {"FINISHED"}


class CHSTAGE_UL_clips(UIList):
    def draw_item(self, context, layout, data, item, icon, active_data, active_property, index=0, flt_flag=0):
        row = layout.row(align=True)
        playing = context.scene.chstage.character.clip == item.clip
        row.label(text=item.label, icon="PLAY" if playing else "ACTION")
        row.label(text=f"{item.duration:.1f} s")

    def filter_items(self, context, data, propname):
        rows = getattr(data, propname)
        text = self.filter_name.lower()
        return [self.bitflag_filter_item if not text or text in f"{r.label} {r.clip}".lower() else 0 for r in rows], []


class CHSTAGE_UL_hair_materials(UIList):
    def draw_item(self, context, layout, data, item, icon, active_data, active_property, index=0, flt_flag=0):
        layout.label(text=item.material.name if item.material else "(removed)", icon="MATERIAL")


class CharacterPanel:
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = CATEGORY

    @classmethod
    def poll(cls, context):
        return character_scene(context)


class CHSTAGE_PT_character(Panel):
    bl_label = "Character Package"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = CATEGORY

    def draw(self, context):
        layout = self.layout
        row = layout.row(align=True)
        row.operator("chstage.new_character", text="New", icon="FILE_NEW")
        row.operator("chstage.open_character", text="Open", icon="FILE_FOLDER")
        if not character_scene(context):
            layout.label(text="This scene is not a character", icon="INFO")
            return
        p = context.scene.chstage.character
        box = layout.box()
        box.label(
            text=f"Profile: {character_tools.profile_id(context.scene)}"
            + (" (clips retargeted to its own skeleton)" if p.own_skeleton else ""),
            icon="ARMATURE_DATA",
        )
        row = box.row(align=True)
        row.label(text=f"Rig: {p.rig.name if p.rig else 'none'}")
        if p.rig is not None:
            row.operator("chstage.select_rig", text="", icon="RESTRICT_SELECT_OFF")
        box.label(text=f"Library: {bpy.path.basename(p.library) or 'none'}", icon="PACKAGE" if p.library else "ERROR")
        if p.library_profile:
            box.label(
                text=f"Character library: {bpy.path.basename(p.character_library) or 'none'}",
                icon="PACKAGE" if p.character_library else "ERROR",
            )
        meshes = character_tools.character_meshes(context.scene)
        box.label(text=f"Character meshes: {', '.join(o.name for o in meshes) or 'none'}", icon="MESH_DATA")
        row = layout.row(align=True)
        row.operator("chstage.validate_character", icon="CHECKMARK")
        row.operator("chstage.export_character", icon="EXPORT")
        panels.draw_issues(layout, context)


class CHSTAGE_PT_character_info(CharacterPanel, Panel):
    bl_label = "Package Information"
    bl_parent_id = "CHSTAGE_PT_character"
    bl_options = {"DEFAULT_CLOSED"}

    def draw(self, context):
        panels.draw_package_info(self.layout, context.scene.chstage.package)


class CHSTAGE_PT_character_weights(CharacterPanel, Panel):
    bl_label = "Weights and Hair"
    bl_order = 2

    def draw(self, context):
        layout = self.layout
        scene = context.scene
        p = scene.chstage.character
        box = layout.box()
        box.label(text="Helpers (never exported)")
        view_layer = context.view_layer
        row = box.row(align=True)
        for action, obj, label in (("REFERENCE", p.reference, "GH3 body"), ("ATTACHMENT", p.attachment, "Attachment")):
            shown = obj is not None and obj.name in view_layer.objects and not obj.hide_get(view_layer=view_layer)
            sub = row.row(align=True)
            sub.enabled = obj is not None
            sub.operator(
                "chstage.character_helpers", text=label, icon="HIDE_OFF" if shown else "HIDE_ON", depress=shown
            ).action = action
        row.operator("chstage.character_helpers", text="", icon="FILE_REFRESH").action = "RELOAD"
        layout.label(text="Weights of the selected meshes")
        if not p.own_skeleton:
            layout.operator("chstage.transfer_weights", icon="MOD_DATA_TRANSFER")
        layout.operator("chstage.limit_weights", icon="MOD_VERTEX_WEIGHT")
        box = layout.box()
        if not p.own_skeleton:
            box.label(text="Hair bones of the profile")
            box.prop(p, "hair_chain", text="")
            box.operator("chstage.assign_hair", icon="CURVES" if bpy.app.version >= (3, 3, 0) else "HAIR")
        box.label(text="Hair materials (sorted transparency)")
        row = box.row()
        row.template_list("CHSTAGE_UL_hair_materials", "", p, "hair_materials", p, "hair_materials_index", rows=2)
        column = row.column(align=True)
        column.operator("chstage.hair_material", text="", icon="ADD").action = "ADD"
        column.operator("chstage.hair_material", text="", icon="REMOVE").action = "REMOVE"
        layout.prop(p, "instrument")


class CHSTAGE_PT_character_clips(CharacterPanel, Panel):
    bl_label = "Clip Preview"
    bl_order = 3

    def draw(self, context):
        layout = self.layout
        p = context.scene.chstage.character
        if not p.clips:
            layout.label(text="No clips: the library is missing", icon="ERROR")
            layout.operator(
                "chstage.character_helpers", text="Load from the Library", icon="FILE_REFRESH"
            ).action = "RELOAD"
            return
        layout.template_list("CHSTAGE_UL_clips", "", p, "clips", p, "clips_index", rows=8)
        row = layout.row(align=True)
        row.operator("chstage.clear_pose", icon="ARMATURE_DATA")
        row.operator("screen.animation_play", text="Play", icon="PLAY")
        if p.clip:
            layout.label(
                text=f"Clip {p.clip} at {context.scene.render.fps} fps, frames {context.scene.frame_start}-{context.scene.frame_end}",
                icon="INFO",
            )


CLASSES = (
    CHSTAGE_OT_new_character,
    CHSTAGE_OT_open_character,
    CHSTAGE_OT_validate_character,
    CHSTAGE_OT_export_character,
    CHSTAGE_OT_transfer_weights,
    CHSTAGE_OT_limit_weights,
    CHSTAGE_OT_assign_hair,
    CHSTAGE_OT_hair_material,
    CHSTAGE_OT_clear_pose,
    CHSTAGE_OT_preview_clip,
    CHSTAGE_OT_character_helpers,
    CHSTAGE_OT_select_rig,
    CHSTAGE_UL_clips,
    CHSTAGE_UL_hair_materials,
    CHSTAGE_PT_character,
    CHSTAGE_PT_character_info,
    CHSTAGE_PT_character_weights,
    CHSTAGE_PT_character_clips,
)


def register():
    for cls in CLASSES:
        bpy.utils.register_class(cls)


def unregister():
    for cls in reversed(CLASSES):
        bpy.utils.unregister_class(cls)
