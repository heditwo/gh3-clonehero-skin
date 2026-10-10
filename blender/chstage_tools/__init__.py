"""CH Stage Tools: make venue packages (.chstage) and character packages (.chchar, docs/PACKAGE-FORMAT.md) in Blender.
See docs/BLENDER-ADDON.md."""

from . import (
    chain_tools,
    character_ui,
    collect,
    lighting_tools,
    operators,
    package_open,
    panels,
    properties,
    skeleton_tools,
)


class glTF2ExportUserExtension(collect.ExportExtras):
    """The Blender glTF exporter finds this class by name in enabled add-ons and calls its hooks."""


class glTF2ImportUserExtension(package_open.ImportHooks):
    """The Blender glTF importer finds this class by name in enabled add-ons and calls its hooks."""


def menu_new_stage(self, context):
    self.layout.operator("chstage.new_stage", icon="FILE_NEW")
    self.layout.operator("chstage.new_character", icon="ARMATURE_DATA")


def menu_import(self, context):
    self.layout.operator("chstage.open_package", text="CH Stage Package (.chstage)")
    self.layout.operator("chstage.open_character", text="CH Character Package (.chchar)")


def menu_export(self, context):
    self.layout.operator("chstage.export_package", text="CH Stage Package (.chstage)")
    self.layout.operator("chstage.export_character", text="CH Character Package (.chchar)")


def register():
    import bpy

    properties.register()
    operators.register()
    panels.register()
    character_ui.register()
    chain_tools.register()
    skeleton_tools.register()
    lighting_tools.register()
    bpy.types.TOPBAR_MT_file_new.append(menu_new_stage)
    bpy.types.TOPBAR_MT_file_import.append(menu_import)
    bpy.types.TOPBAR_MT_file_export.append(menu_export)


def unregister():
    import bpy

    bpy.types.TOPBAR_MT_file_export.remove(menu_export)
    bpy.types.TOPBAR_MT_file_import.remove(menu_import)
    bpy.types.TOPBAR_MT_file_new.remove(menu_new_stage)
    lighting_tools.unregister()
    skeleton_tools.unregister()
    chain_tools.unregister()
    character_ui.unregister()
    panels.unregister()
    operators.unregister()
    properties.unregister()
