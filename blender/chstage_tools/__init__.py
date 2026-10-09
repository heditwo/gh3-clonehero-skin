"""CH Stage Tools: make venue packages (.chstage, docs/PACKAGE-FORMAT.md) in Blender. See docs/BLENDER-ADDON.md."""

from . import collect, operators, panels, properties


class glTF2ExportUserExtension(collect.ExportExtras):
    """The Blender glTF exporter finds this class by name in enabled add-ons and calls its hooks."""


def menu_new_stage(self, context):
    self.layout.operator("chstage.new_stage", icon="FILE_NEW")


def register():
    import bpy
    properties.register()
    operators.register()
    panels.register()
    bpy.types.TOPBAR_MT_file_new.append(menu_new_stage)


def unregister():
    import bpy
    bpy.types.TOPBAR_MT_file_new.remove(menu_new_stage)
    panels.unregister()
    operators.unregister()
    properties.unregister()
