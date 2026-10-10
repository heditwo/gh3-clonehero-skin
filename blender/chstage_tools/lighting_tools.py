"""Lighting preview: show a snapshot or a mood with the Blender lights in Material Preview (EEVEE), and draw the beams.

The preview changes only the Blender light color, power, and cutoff distance, and the scene world. Export does not
read them: lighting.json comes from the snapshot settings and the light transforms. Turn the preview off to get the
scene world back.
"""

import math

import bpy
import gpu
from gpu_extras.batch import batch_for_shader
from mathutils import Matrix, Quaternion, Vector

from . import sidecars

PREVIEW_WORLD = "CH Stage Lighting Preview"
_HANDLES = []


def preview_snapshot(scene):
    """The snapshot that the preview shows: the active snapshot, or a snapshot of the selected mood."""
    props = scene.chstage
    if not props.snapshots:
        return None
    if props.preview_mood in ("", "NONE"):
        return props.snapshots[min(props.snapshots_index, len(props.snapshots) - 1)]
    mood = next((m for m in props.moods if m.mood == props.preview_mood), None)
    if mood is None or not mood.snapshots:
        return None
    name = mood.snapshots[props.preview_step % len(mood.snapshots)].name
    return next((s for s in props.snapshots if s.name == name), None)


def light_kind(obj):
    kind = obj.chstage.light.type
    if kind != "AUTO":
        return kind
    return {"SUN": "Dir", "SPOT": "Spot"}.get(obj.data.type, "Point")


def preview_values(snapshot):
    """(light object, color, power, cutoff) for each light of a snapshot, and the ambient color of the Stage group."""
    rows, ambient = [], [0.0, 0.0, 0.0]
    for row in snapshot.lights:
        obj = row.light
        if obj is None or obj.type != "LIGHT" or row.inherit:
            continue
        power, fill = sidecars.preview_light(light_kind(obj), row.intensity, row.near, row.far)
        if fill and obj.chstage.light.group in ("Stage", ""):
            for i in range(3):
                ambient[i] += row.color[i] * fill
        rows.append((obj, tuple(row.color), power, row.far))
    return rows, tuple(min(1.0, v) for v in ambient)


def preview_world(color):
    world = bpy.data.worlds.get(PREVIEW_WORLD) or bpy.data.worlds.new(PREVIEW_WORLD)
    world.use_nodes = True
    background = next((n for n in world.node_tree.nodes if n.type == "BACKGROUND"), None)
    if background is not None:
        background.inputs["Color"].default_value = (*color, 1)
        background.inputs["Strength"].default_value = 1.0
    world.color = color
    return world


def apply_preview(scene, snapshot):
    rows, ambient = preview_values(snapshot)
    for obj, color, power, cutoff in rows:
        obj.data.color = color
        obj.data.energy = power
        # GH3 stage lights cast no shadows; many shadowed lights also fill the EEVEE shadow buffer.
        obj.data.use_shadow = False
        if hasattr(obj.data, "use_custom_distance"):
            obj.data.use_custom_distance = cutoff > 0
            obj.data.cutoff_distance = max(0.01, cutoff)
    if scene.world is None or scene.world.name != PREVIEW_WORLD:
        scene.chstage.preview_world = scene.world
    scene.world = preview_world(ambient)


def set_viewports(screen, on):
    for area in screen.areas if screen else []:
        if area.type != "VIEW_3D":
            continue
        shading = area.spaces.active.shading
        if on:
            shading.type = "MATERIAL"
            shading.use_scene_lights = True
            shading.use_scene_world = True
        area.tag_redraw()


def group_receivers(scene):
    """Light group name to the objects that its lights light: the scenery of the group; for Band also the proxy rigs
    and proxy bodies (the performers)."""
    from . import actor_tools, groups

    receivers = {}
    for name, objects in groups.light_groups(scene).items():
        for obj in objects:
            receivers.setdefault(name, []).extend(
                [obj] + (actor_tools.descendants(obj) if obj.chstage.marker == "ACTOR" else [])
            )
    receivers.setdefault("Band", []).extend(
        obj for obj in scene.objects if obj.chstage.marker == "PROXY" and obj.type == "MESH"
    )
    return receivers


def link_light_groups(scene):
    """Light linking (Blender 4.0 and later): a stage light lights only the objects of its light group, as in the game."""
    lights = [
        obj
        for obj in scene.objects
        if obj.type == "LIGHT" and obj.chstage.light.enabled and hasattr(obj, "light_linking")
    ]
    if not lights:
        return
    collections = {}
    for name, objects in group_receivers(scene).items():
        collection = bpy.data.collections.new(f"CH Light Group {name}")
        collection["chstage_light_group"] = scene.name
        for obj in objects:
            collection.objects.link(obj)
        collections[name] = collection
    empty = bpy.data.collections.new("CH Light Group (none)")
    empty["chstage_light_group"] = scene.name
    for obj in lights:
        obj.light_linking.receiver_collection = collections.get(obj.chstage.light.group or "Stage", empty)


def unlink_light_groups(scene):
    for obj in scene.objects:
        if (
            obj.type == "LIGHT"
            and hasattr(obj, "light_linking")
            and obj.light_linking.receiver_collection is not None
            and obj.light_linking.receiver_collection.get("chstage_light_group") == scene.name
        ):
            obj.light_linking.receiver_collection = None
    for collection in [c for c in bpy.data.collections if c.get("chstage_light_group") == scene.name]:
        bpy.data.collections.remove(collection)


def linked(scene):
    return any(c.get("chstage_light_group") == scene.name for c in bpy.data.collections)


def stop_preview(scene):
    world = bpy.data.worlds.get(PREVIEW_WORLD)
    if scene.world is not None and scene.world == world:
        scene.world = scene.chstage.preview_world
    scene.chstage.preview_world = None
    unlink_light_groups(scene)


def refresh(scene):
    """Update the preview after a change of the snapshot settings (property update callback)."""
    if not isinstance(scene, bpy.types.Scene):
        return
    props = scene.chstage
    screen = getattr(bpy.context, "screen", None)
    if not props.lighting_preview:
        if (
            props.preview_world is not None
            or (scene.world is not None and scene.world.name == PREVIEW_WORLD)
            or linked(scene)
        ):
            stop_preview(scene)
        tag_redraw(screen)
        return
    if not linked(scene):
        link_light_groups(scene)
    snapshot = preview_snapshot(scene)
    if snapshot is not None:
        apply_preview(scene, snapshot)
    set_viewports(screen, True)


def tag_redraw(screen):
    for area in screen.areas if screen else []:
        if area.type == "VIEW_3D":
            area.tag_redraw()


# Beams and fixtures in the viewport.
def beam_matrix(beam):
    """World matrix of a snapshot beam: its position, and its direction along local -Z (as a Blender spot light)."""
    return Matrix.Translation(Vector(beam.position)) @ Quaternion(beam.rotation).to_matrix().to_4x4()


def cone_lines(matrix, start_radius, end_radius, length, segments=16):
    """Line segments of a beam cone along local -Z."""
    points = []

    def ring(radius, z):
        return [
            matrix @ Vector((math.cos(a) * radius, math.sin(a) * radius, z))
            for a in (2 * math.pi * i / segments for i in range(segments))
        ]

    start, end = ring(max(start_radius, 0.01), 0.0), ring(max(end_radius, 0.01), -length)
    for i in range(segments):
        points += [start[i], start[(i + 1) % segments], end[i], end[(i + 1) % segments]]
        if i % 4 == 0:
            points += [start[i], end[i]]
    points += [matrix @ Vector((0, 0, 0)), matrix @ Vector((0, 0, -length))]
    return points


def fixture_lines(obj, length=1.0):
    """The beam axis of a fixture housing (local -Y in Blender)."""
    origin = obj.matrix_world.translation
    axis = obj.matrix_world.to_3x3().normalized() @ Vector((0, -1, 0))
    return [origin, origin + axis * length]


def overlay_lines(scene):
    """(color, lines) of the beams of the previewed snapshot and of the fixture axes."""
    result = []
    snapshot = preview_snapshot(scene)
    if snapshot is not None:
        for beam in snapshot.beams:
            if beam.range <= 0:
                continue
            result.append(
                ((*beam.color, 0.9), cone_lines(beam_matrix(beam), beam.start_radius, beam.end_radius, beam.range))
            )
    fixtures = [
        line for obj in scene.objects if obj.chstage.fixture_id and obj.visible_get() for line in fixture_lines(obj)
    ]
    if fixtures:
        result.append(((1.0, 0.8, 0.1, 1.0), fixtures))
    return result


def draw_overlay():
    context = bpy.context
    scene = context.scene
    if scene is None or not hasattr(scene, "chstage") or not scene.chstage.beam_overlay:
        return
    shader = gpu.shader.from_builtin("UNIFORM_COLOR")
    gpu.state.blend_set("ALPHA")
    gpu.state.depth_test_set("LESS_EQUAL")
    for color, lines in overlay_lines(scene):
        batch = batch_for_shader(shader, "LINES", {"pos": [tuple(p) for p in lines]})
        shader.uniform_float("color", color)
        batch.draw(shader)
    gpu.state.depth_test_set("NONE")
    gpu.state.blend_set("NONE")


class CHSTAGE_GGT_beam(bpy.types.GizmoGroup):
    """An arrow on the active beam of the previewed snapshot: drag it to change the beam range."""

    bl_idname = "CHSTAGE_GGT_beam"
    bl_label = "CH Stage beam range"
    bl_space_type = "VIEW_3D"
    bl_region_type = "WINDOW"
    bl_options = {"3D", "PERSISTENT"}

    @staticmethod
    def active_beam(context):
        scene = context.scene
        if scene is None or not scene.chstage.beam_overlay:
            return None
        snapshot = preview_snapshot(scene)
        if snapshot is None or not snapshot.beams:
            return None
        return snapshot.beams[min(snapshot.beams_index, len(snapshot.beams) - 1)]

    @classmethod
    def poll(cls, context):
        return cls.active_beam(context) is not None

    def setup(self, context):
        arrow = self.gizmos.new("GIZMO_GT_arrow_3d")
        arrow.color = 1.0, 0.8, 0.1
        arrow.alpha = 0.8
        arrow.color_highlight = 1.0, 1.0, 0.4
        self.arrow = arrow
        self.refresh(context)

    def refresh(self, context):
        beam = self.active_beam(context)
        if beam is None:
            return
        # The arrow points along its local +Z; the beam goes along local -Z.
        self.arrow.matrix_basis = beam_matrix(beam) @ Matrix.Rotation(math.pi, 4, "X")
        self.arrow.target_set_prop("offset", beam, "range")


def register():
    bpy.utils.register_class(CHSTAGE_GGT_beam)
    _HANDLES.append(bpy.types.SpaceView3D.draw_handler_add(draw_overlay, (), "WINDOW", "POST_VIEW"))


def unregister():
    while _HANDLES:
        bpy.types.SpaceView3D.draw_handler_remove(_HANDLES.pop(), "WINDOW")
    bpy.utils.unregister_class(CHSTAGE_GGT_beam)
