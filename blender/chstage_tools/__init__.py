"""CH Stage Tools: make venue packages (.chstage, docs/PACKAGE-FORMAT.md) in Blender. See docs/BLENDER-ADDON.md."""

from . import collect, operators, package_open, panels, properties


class glTF2ExportUserExtension(collect.ExportExtras):
    """The Blender glTF exporter finds this class by name in enabled add-ons and calls its hooks."""


class glTF2ImportUserExtension(package_open.ImportHooks):
    """The Blender glTF importer finds this class by name in enabled add-ons and calls its hooks."""


def menu_new_stage(self, context):
    self.layout.operator("chstage.new_stage", icon="FILE_NEW")


def menu_import(self, context):
    self.layout.operator("chstage.open_package", text="CH Stage Package (.chstage)")


def menu_export(self, context):
    self.layout.operator("chstage.export_package", text="CH Stage Package (.chstage)")


def register():
    import bpy
    properties.register()
    operators.register()
    panels.register()
    bpy.types.TOPBAR_MT_file_new.append(menu_new_stage)
    bpy.types.TOPBAR_MT_file_import.append(menu_import)
    bpy.types.TOPBAR_MT_file_export.append(menu_export)


def unregister():
    import bpy
    bpy.types.TOPBAR_MT_file_export.remove(menu_export)
    bpy.types.TOPBAR_MT_file_import.remove(menu_import)
    bpy.types.TOPBAR_MT_file_new.remove(menu_new_stage)
    panels.unregister()
    operators.unregister()
    properties.unregister()
