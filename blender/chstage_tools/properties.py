"""Blender data for one venue package. Each group maps to one file of package format v1 (docs/PACKAGE-FORMAT.md):

| Blender data | Package file |
| --- | --- |
| scene.chstage.package | manifest.json |
| object.chstage (marker) | placements.json band, waypoints, effects |
| scene.chstage.crowd | placements.json crowd |
| object.chstage.shot, object.chstage.point, scene.chstage.camera_notes | cameras.json |
| object.chstage.light, scene.chstage.snapshots, moods, colors | lighting.json |
| object.chstage (node flags) | stage.glb node extras gh3* |
| material.chstage | stage.glb material extras gh3* and glTF material fields |
| object.chstage.actor (actor root empty) | actors.json row and its model GLB |
| scene.chstage.kept_files | Package files that the add-on keeps (scripts.json as a text block, other files packed in the .blend file) |
| scene.chstage.character | character.json of a character package (.chchar) and the character tools (rig, helpers, clip preview) |

The 'kept' text properties hold the source rows of an opened package (see sidecars.Keeper).
"""

import bpy
from bpy.props import (
    BoolProperty,
    CollectionProperty,
    EnumProperty,
    FloatProperty,
    FloatVectorProperty,
    IntProperty,
    PointerProperty,
    StringProperty,
)
from bpy.types import AddonPreferences, PropertyGroup

from . import sidecars
from .vendor.package_format.profiles import LIBRARY_ID

ROLES = [(r, r.title(), "") for r in sidecars.ROLES]
CROWD_ROLES = [(f"crowd{i}", f"Crowd {i}", "") for i in range(1, 9)]


PROFILE_ITEMS = [
    ("gh3-guitarist-axel-v1", "Guitarist (Axel)", "GH3 guitarist rig, band role guitarist"),
    ("gh3-bassist-v1", "Bassist", "GH3 bassist rig, band role bassist"),
    ("gh3-singer-male-v1", "Singer (male)", "GH3 singer rig, band role singer"),
    ("gh3-drummer-v1", "Drummer", "GH3 drummer rig, band role drummer"),
]


def items(values, descriptions=None):
    return [(v, v, (descriptions or {}).get(v, "")) for v in values]


def live_lighting(self, context):
    from . import lighting_tools

    lighting_tools.refresh(self.id_data)


def toon_preview(self, context):
    from . import toon

    toon.refresh(self.id_data)


def preview_selected_shot(self, context):
    from . import camera_tools

    camera_tools.preview_index(context, self.id_data)


def rebuild_scripts(self, context):
    from . import scripts_ui

    scripts_ui.rebuild(self.id_data)


def light_group_overlay(self, context):
    from . import groups

    groups.overlay(self.id_data, context)


_MOOD_ITEMS = []


def scene_moods(self, context):
    # Blender needs Python to keep the strings of dynamic enum items alive.
    _MOOD_ITEMS[:] = [("NONE", "Snapshot", "Preview the active snapshot")] + [
        (n, n, f"Preview the mood '{n}'") for n in dict.fromkeys(m.mood for m in self.moods)
    ]
    return _MOOD_ITEMS


class CHSTAGE_PG_name(PropertyGroup):
    name: StringProperty(name="Name")


class CHSTAGE_PG_image_ref(PropertyGroup):
    """A texture reference of the opened package: the GLB image name and its Blender image."""

    name: StringProperty(name="Name")
    image: PointerProperty(name="Image", type=bpy.types.Image)


class CHSTAGE_PG_kept_file(PropertyGroup):
    """A package file that the add-on does not make (for example scripts.json). Export writes it again.

    JSON files are Blender text blocks. Other files are packed into the .blend file (blobs.py). Files of .blend files
    from add-on 0.1 can be references to the source package: export reads them from there and checks the SHA-256."""

    path: StringProperty(name="Package path")
    entry: StringProperty(name="Entry", description="manifest.json entry key of this file, or empty")
    text: PointerProperty(name="Text", type=bpy.types.Text)
    blob: PointerProperty(
        name="Packed data", type=bpy.types.Text, description="Text block that holds the packed file bytes"
    )
    source: StringProperty(name="Source package", subtype="FILE_PATH")
    sha256: StringProperty(name="SHA-256")
    size: IntProperty(name="Size")


class CHSTAGE_PG_number(PropertyGroup):
    value: FloatProperty(name="Value", min=0, max=86400)


class CHSTAGE_PG_author(PropertyGroup):
    name: StringProperty(name="Name")
    role: StringProperty(name="Role", description="For example 'modeling' or 'original venue'")
    url: StringProperty(name="URL", description="https:// or mailto: address")


class CHSTAGE_PG_requirement(PropertyGroup):
    package_id: StringProperty(name="Package ID", default=LIBRARY_ID)
    version: StringProperty(name="Version", default="1.0.0", description="Lowest compatible version")


class CHSTAGE_PG_package(PropertyGroup):
    package_id: StringProperty(
        name="ID",
        default="author.my-stage",
        description="Stable ID: lowercase letters, digits, '.', and '-'. Do not change it between versions",
    )
    version: StringProperty(name="Version", default="1.0.0", description="Semantic version of this release")
    name: StringProperty(name="Name", default="My Stage", description="Display name in the game")
    description: StringProperty(name="Description")
    authors: CollectionProperty(type=CHSTAGE_PG_author)
    authors_index: IntProperty()
    license: StringProperty(
        name="License",
        default="CC-BY-4.0",
        description="SPDX license expression. Use LicenseRef-<name> for other licenses",
    )
    redistribute: BoolProperty(name="Redistribute", default=True, description="Other people can share the package")
    modify: BoolProperty(name="Modify", default=True, description="Other people can change the package")
    commercial: BoolProperty(name="Commercial use", default=False)
    credits: CollectionProperty(type=CHSTAGE_PG_name)
    credits_index: IntProperty()
    homepage: StringProperty(name="Homepage", description="https:// address")
    requires: CollectionProperty(type=CHSTAGE_PG_requirement)
    requires_index: IntProperty()
    min_mod_version: StringProperty(
        name="Minimum mod version", description="Empty, or the lowest mod version that can load the package"
    )
    preview_mode: EnumProperty(
        name="Preview",
        items=[
            ("RENDER", "Render", "Render the scene camera at 512 x 288 when you export"),
            ("IMAGE", "Image", "Use an image (PNG, maximum 1024 x 1024; recommended 512 x 288)"),
            ("NONE", "None", "No preview image"),
        ],
        default="RENDER",
    )
    preview_image: PointerProperty(name="Preview image", type=bpy.types.Image)


class CHSTAGE_PG_point(PropertyGroup):
    """One camera point: the camera or empty that has these settings gives the position and rotation."""

    anchor_role: EnumProperty(
        name="Anchor",
        items=[("world", "World", "")] + ROLES,
        default="world",
        description="Position and rotation are relative to this frame",
    )
    anchor_part: EnumProperty(name="Part", items=items(sidecars.ANCHOR_PARTS), default="root")
    anchor_bone: StringProperty(name="Bone", description="Rig template bone name")
    look_at_role: EnumProperty(
        name="Look at",
        items=[("none", "None", "Use the object rotation"), ("world", "World", "")] + ROLES,
        default="none",
    )
    look_at_part: EnumProperty(name="Part", items=items(sidecars.ANCHOR_PARTS), default="head")
    look_at_bone: StringProperty(name="Bone")
    use_rotation: BoolProperty(
        name="Write rotation",
        default=True,
        description="Off: the point has no rotation in cameras.json and the game aims it at the look-at target",
    )
    fov: FloatProperty(
        name="Horizontal FOV",
        default=60,
        min=1,
        max=169,
        description="Field of view of an empty point. A camera point uses its camera lens",
    )
    use_screen_offset: BoolProperty(name="Screen offset")
    screen_offset: FloatVectorProperty(
        name="Screen offset", size=2, description="Framing offset in GH3 normalized screen units"
    )


class CHSTAGE_PG_object_ref(PropertyGroup):
    target: PointerProperty(name="Point", type=bpy.types.Object, description="Camera or empty with point settings")


class CHSTAGE_PG_shot(PropertyGroup):
    enabled: BoolProperty(name="Camera shot", description="Export this camera as a shot in cameras.json")
    shot_name: StringProperty(name="Shot name", description="Empty: the object name")
    type: EnumProperty(name="Type", items=items(sidecars.SHOT_TYPES), default="stage")
    control: EnumProperty(name="Control", items=items(sidecars.SHOT_CONTROLS), default="TwoCam")
    tag: StringProperty(name="Tag")
    groups: CollectionProperty(type=CHSTAGE_PG_name)
    groups_index: IntProperty()
    waypoints: CollectionProperty(type=CHSTAGE_PG_name, description="Guitarist destinations where the shot is eligible")
    waypoints_index: IntProperty()
    more_points: CollectionProperty(type=CHSTAGE_PG_object_ref, description="Points after this camera, in order")
    more_points_index: IntProperty()
    times: CollectionProperty(
        type=CHSTAGE_PG_number, description="Keyframes control: seconds from the cut for each point"
    )
    times_index: IntProperty()
    interpolation: EnumProperty(
        name="Interpolation", items=[("default", "Default", ""), ("smooth", "Smooth", ""), ("linear", "Linear", "")]
    )
    use_orbit_angle: BoolProperty(name="Orbit angle")
    orbit_angle: FloatProperty(name="Orbit angle", min=-36000, max=36000)
    use_angular_speed: BoolProperty(name="Angular speed")
    angular_speed: FloatProperty(name="Angular speed", min=-3600, max=3600, description="Degrees per second")
    use_step_time: BoolProperty(name="Step time")
    step_time: FloatProperty(name="Step time", default=1, min=0.001, max=86400)
    use_step_count: BoolProperty(name="Step count")
    step_count: FloatProperty(name="Step count", min=0, max=10000)
    use_step_direction: BoolProperty(name="Step direction")
    step_direction: FloatVectorProperty(name="Step direction", size=3, description="Runtime coordinates")
    use_height_ease: BoolProperty(name="Height ease")
    height_ease: FloatVectorProperty(
        name="Height ease", size=3, description="Start height, end height, and ease (0-100)"
    )
    hand_cam: BoolProperty(name="Hand camera")
    disable_orbit1: BoolProperty(name="Disable orbit 1")
    disable_orbit2: BoolProperty(name="Disable orbit 2")


class CHSTAGE_PG_light(PropertyGroup):
    enabled: BoolProperty(name="Stage light", description="Export this light in lighting.json")
    light_id: StringProperty(name="Light ID", description="Empty: the object name")
    group: StringProperty(
        name="Group", default="Stage", description="Light group: Stage, Band, Crowd, Periph, or a stage-local name"
    )
    type: EnumProperty(
        name="Type", items=[("AUTO", "From Blender light", "")] + items(sidecars.LIGHT_TYPES), default="AUTO"
    )
    script_object: StringProperty(name="Script object")
    override_color: BoolProperty(name="Override color")
    intensity: FloatProperty(
        name="Intensity", default=1, min=-100, max=100, description="GH3 intensity (snapshots capture it)"
    )
    near: FloatProperty(name="Near", default=0, min=0, max=100000)
    far: FloatProperty(name="Far", default=200, min=0, max=100001)
    use_cone: BoolProperty(name="Hotspot and field")
    hotspot: FloatProperty(name="Hotspot", min=0, max=180, default=30)
    field: FloatProperty(name="Field", min=0, max=180, default=45)


class CHSTAGE_PG_actor_action(PropertyGroup):
    name: StringProperty(name="Action", description="Cue action name, for example 'jump'")
    clips: StringProperty(name="Clips", description="Clip names of the action, separated by commas")


class CHSTAGE_PG_actor(PropertyGroup):
    """One actors.json row. The actor root is an empty: its world transform and scale are the actor transform and scale.
    The armature and the meshes of the actor model are its children."""

    actor_name: StringProperty(name="Actor name", description="Key in actors.json. Empty: the object name")
    model_path: StringProperty(name="Model file", description="Package path of the actor GLB. Empty: <actor name>.glb")
    blob: PointerProperty(
        name="Packed model",
        type=bpy.types.Text,
        description="The GLB of the opened package, packed into the .blend file",
    )
    clip: StringProperty(name="Clip", description="Start clip. Empty: the first clip of the GLB")
    playback: EnumProperty(
        name="Playback",
        items=[("DEFAULT", "Default (loop)", "No playback key"), ("loop", "Loop", ""), ("hold", "Hold", "")],
        default="DEFAULT",
    )
    light_group: StringProperty(name="Light group", description="Empty: Stage")
    role: StringProperty(name="Cue role", description="Cue target, for example stage-diver/1")
    use_initially_visible: BoolProperty(name="Set start visibility")
    initially_visible: BoolProperty(name="Initially visible", default=True)
    script_object: StringProperty(name="Script object")
    script: StringProperty(name="Startup script")
    use_created: BoolProperty(name="Set created state")
    created: BoolProperty(name="Created", default=True)
    animation_events: StringProperty(
        name="Animation events", description="Typed animation-event table in the script bank"
    )
    write_scale: BoolProperty(
        name="Write scale", description="Write the scale also when it is 1 (as the opened package)"
    )
    actions: CollectionProperty(type=CHSTAGE_PG_actor_action)
    actions_index: IntProperty()
    fps: IntProperty(
        name="Clip rate",
        default=60,
        min=1,
        max=240,
        description="Frames per second of the clips (scene rate for import and export)",
    )
    model_mode: EnumProperty(
        name="Model export",
        items=[
            (
                "AUTO",
                "Automatic",
                "Write the packed GLB while the Blender model did not change, else export the Blender model",
            ),
            ("PACKED", "Packed GLB", "Always write the packed GLB of the opened package"),
            ("BLENDER", "Blender model", "Always export the Blender model"),
        ],
        default="AUTO",
    )
    clips_json: StringProperty(description="Clip names of the packed GLB (JSON list)")
    loaded_json: StringProperty(description="Clip names that Blender has (JSON list)")
    fingerprint: StringProperty(description="Hash of the Blender model just after open or import")


class CHSTAGE_PG_object(PropertyGroup):
    marker: EnumProperty(
        name="Marker",
        items=[
            ("NONE", "None", "Scenery or other object"),
            ("BAND", "Band placement", "Performer root (placements.json band)"),
            ("WAYPOINT", "Waypoint", "Walking destination (placements.json waypoints)"),
            ("EFFECT", "Effect emitter", "Pyro, sparks, fireworks, or smoke (placements.json effects)"),
            ("ACTOR", "Actor", "Animated scenery root (actors.json); its children are the actor model"),
            ("PROXY", "Proxy body", "Preview only; never exported"),
        ],
        default="NONE",
    )
    band_role: EnumProperty(name="Role", items=ROLES, default="guitarist")
    waypoint_name: StringProperty(name="Waypoint name", description="Empty: the object name. Example: guitarist_walk01")
    effect: EnumProperty(name="Effect", items=items(sidecars.EFFECTS), default="pyro")
    light_group: StringProperty(name="Light group", description="gh3LightGroup. Empty: Stage")
    visibility_group: StringProperty(
        name="Visibility group", description="gh3VisibilityGroup. 'crowd-lighters' gets crowd lighter commands"
    )
    initially_visible: BoolProperty(name="Initially visible", default=True)
    fixture_id: StringProperty(
        name="Fixture beam ID",
        description="gh3FixtureId: movable housing for this lighting beam. Beam axis: local -Y in Blender",
    )
    fixture_fixed_position: BoolProperty(name="Fixed position")
    fixture_object: StringProperty(name="Fixture script object")
    use_fixture_created: BoolProperty(name="Set created state")
    fixture_created: BoolProperty(name="Created", default=True)
    script_object: StringProperty(
        name="Script object", description="gh3Object: script object name or 0x and 8 hex digits"
    )
    object_pivot_mode: EnumProperty(
        name="Pivot",
        items=[
            ("NONE", "None", "No gh3ObjectPivot and gh3ObjectRotation"),
            ("ORIGIN", "Object origin", "Write the object origin as gh3ObjectPivot and gh3ObjectRotation"),
            ("CUSTOM", "Custom", "Write the values below (runtime coordinates)"),
        ],
        default="NONE",
    )
    object_pivot: FloatVectorProperty(name="Pivot", size=3, description="gh3ObjectPivot, runtime coordinates")
    object_rotation: FloatVectorProperty(
        name="Rotation", size=4, default=(0, 0, 0, 1), description="gh3ObjectRotation [x, y, z, w], runtime coordinates"
    )
    use_object_created: BoolProperty(name="Set created state")
    object_created: BoolProperty(name="Created", default=True)
    object_script: StringProperty(name="Startup script")
    source_name: StringProperty(
        name="Package name",
        description="Node name in the opened package. Export uses it while Blender only added a number suffix",
    )
    order: IntProperty(
        name="Order",
        default=1000000,
        description="Export order of markers, shots, and lights (the opened package order)",
    )
    kept: StringProperty(name="Kept rows", description="Source rows of the opened package (see sidecars.Keeper)")
    shot: PointerProperty(type=CHSTAGE_PG_shot)
    point: PointerProperty(type=CHSTAGE_PG_point)
    light: PointerProperty(type=CHSTAGE_PG_light)
    actor: PointerProperty(type=CHSTAGE_PG_actor)


class CHSTAGE_PG_material(PropertyGroup):
    shading: EnumProperty(
        name="Shading",
        items=[
            ("lit", "Lit", "Stage lights light the surface"),
            ("ambient", "Ambient", "Ambient light only"),
            ("unlit", "Unlit", "Full brightness"),
            ("toon", "Toon", "Cel shading that the stage lights color (characters and instruments only)"),
            (
                "NONE",
                "Not set",
                "No gh3Shading: the mod draws the material without the stage surface model (as some GH3 materials)",
            ),
        ],
        default="lit",
    )
    blend: EnumProperty(
        name="Transparency",
        items=[
            ("OPAQUE", "Opaque", ""),
            ("CUTOUT", "Cutout", "Alpha test (glTF MASK)"),
            ("BLEND", "Blend", "Alpha blend"),
            ("ADDITIVE", "Additive", "Additive blend"),
        ],
        default="OPAQUE",
    )
    alpha_cutoff: FloatProperty(name="Alpha cutoff", default=0.5, min=0, max=1)
    use_blend_cutoff: BoolProperty(name="Alpha test", description="gh3AlphaCutoff for blend materials")
    lightmap: PointerProperty(
        name="Lightmap", type=bpy.types.Image, description="glTF occlusion texture on the second UV map"
    )
    lightmap_uv: StringProperty(
        name="Lightmap UV", default="UVMap.001", description="UV map of the lightmap (glTF TEXCOORD_1)"
    )
    two_sided: BoolProperty(name="Two-sided", description="glTF doubleSided")
    opaque_alpha: FloatProperty(
        name="Color alpha",
        default=1,
        min=0,
        max=1,
        description="Alpha of the glTF base color factor of an opaque material (GH3 layer materials use it)",
    )
    color_scale: FloatProperty(
        name="Color scale", default=1, min=1, max=1000, description="gh3ColorScale: multiplier for the base color"
    )
    crowd_atlas: BoolProperty(name="Crowd atlas", description="Shared animated crowd atlas (8 columns, 3 rows)")
    crowd_role: EnumProperty(name="Crowd role", items=[("NONE", "None", "")] + CROWD_ROLES, default="NONE")
    billboard: BoolProperty(name="Billboard", description="Camera-facing card")
    movie_slot: IntProperty(name="Movie slot", default=-1, min=-1, max=31, description="-1: no movie")
    sorted_transparency: BoolProperty(name="Sorted transparency")
    use_flipbook: BoolProperty(name="Flipbook")
    flipbook_columns: IntProperty(name="Columns", default=1, min=1, max=256)
    flipbook_rows: IntProperty(name="Rows", default=1, min=1, max=256)
    flipbook_fps: FloatProperty(name="FPS", default=10, min=-100000, max=100000)
    flipbook_offset: FloatProperty(name="Offset", default=0, min=-100000, max=100000)
    toon_threshold: FloatProperty(
        name="Threshold",
        default=0.5,
        min=0,
        max=1,
        update=toon_preview,
        description="Light amount (half Lambert, 0-1) where the shade starts",
    )
    toon_softness: FloatProperty(
        name="Softness",
        default=0.05,
        min=0,
        max=0.5,
        update=toon_preview,
        description="Half width of the blend between the lit and the shade colors",
    )
    toon_shade_image: PointerProperty(
        name="Shade texture",
        type=bpy.types.Image,
        update=toon_preview,
        description="Color of the shaded side (first UV map). Empty: the base color",
    )
    toon_shade_color: FloatVectorProperty(
        name="Shade color",
        subtype="COLOR",
        size=3,
        default=(1, 1, 1),
        min=0,
        max=4,
        soft_max=1,
        update=toon_preview,
        description="Multiplier for the shade texture or the base color",
    )
    toon_mask_image: PointerProperty(
        name="Shade mask",
        type=bpy.types.Image,
        update=toon_preview,
        description="Red channel: 0 always shaded, 0.5 by the light, 1 always lit",
    )
    toon_rim_color: FloatVectorProperty(
        name="Rim color",
        subtype="COLOR",
        size=3,
        default=(0, 0, 0),
        min=0,
        max=4,
        soft_max=1,
        update=toon_preview,
        description="Rim light color (the key light color multiplies it)",
    )
    toon_rim_width: FloatProperty(
        name="Rim width", default=0, min=0, max=1, update=toon_preview, description="0: no rim light"
    )
    toon_sphere_image: PointerProperty(
        name="Sphere map",
        type=bpy.types.Image,
        update=toon_preview,
        description="Sphere map (matcap) on the view-space normal, as MMD .sph and .spa files",
    )
    toon_sphere_mode: EnumProperty(
        name="Sphere mode",
        items=[("add", "Add", "MMD .spa"), ("multiply", "Multiply", "MMD .sph")],
        default="add",
        update=toon_preview,
    )
    use_outline: BoolProperty(name="Outline", description="Inverted-hull outline")
    outline_width: FloatProperty(
        name="Width",
        default=0.0015,
        min=0,
        max=0.05,
        precision=4,
        subtype="DISTANCE",
        description="Width in metres at 1 m from the camera (from 0.5 m to 4 m the line keeps its screen width)",
    )
    outline_color: FloatVectorProperty(
        name="Color", subtype="COLOR", size=3, default=(0, 0, 0), min=0, max=4, soft_max=1
    )
    outline_textured: BoolProperty(name="Textured", description="Multiply the outline color by the base texture")
    hair_layer: EnumProperty(
        name="Hair layer",
        items=[
            ("NONE", "None", ""),
            (
                "overHair",
                "Over hair",
                "Eyebrows and eyelashes: draw over the hair of the same character, as Project SEKAI does",
            ),
            ("hair", "Hair", "Hair that the over-hair features show through"),
        ],
        default="NONE",
    )
    kept: StringProperty(name="Kept rows")
    source_name: StringProperty(name="Package name", description="Material name in the opened package")
    kept_extras: StringProperty(
        name="Other GH3 data",
        description="gh3* extras that the panel does not edit (JSON), for example the GH3 shader record",
    )
    kept_images: CollectionProperty(type=CHSTAGE_PG_image_ref)


class CHSTAGE_PG_snapshot_light(PropertyGroup):
    light: PointerProperty(name="Light", type=bpy.types.Object, update=live_lighting)
    color: FloatVectorProperty(
        name="Color", subtype="COLOR", size=3, default=(1, 1, 1), min=0, soft_max=1, update=live_lighting
    )
    intensity: FloatProperty(name="Intensity", default=1, min=-100, max=100, update=live_lighting)
    near: FloatProperty(name="Near", default=0, min=0, max=100000, update=live_lighting)
    far: FloatProperty(name="Far", default=200, min=0, max=100001, update=live_lighting)
    inherit: BoolProperty(name="Inherit", update=live_lighting)
    use_specular: BoolProperty(name="Specular")
    specular: FloatProperty(name="Specular", default=1)


class CHSTAGE_PG_beam(PropertyGroup):
    fixture_id: StringProperty(name="Beam ID")
    target: PointerProperty(
        name="Beam object",
        type=bpy.types.Object,
        description="Capture takes the position and the direction (local -Z) from it",
    )
    position: FloatVectorProperty(name="Position", size=3, subtype="TRANSLATION", update=live_lighting)
    rotation: FloatVectorProperty(
        name="Rotation", size=4, subtype="QUATERNION", default=(1, 0, 0, 0), update=live_lighting
    )
    color: FloatVectorProperty(
        name="Color", subtype="COLOR", size=3, default=(1, 1, 1), min=0, soft_max=1, update=live_lighting
    )
    density: FloatProperty(name="Density", default=0.5, min=0, max=100000, update=live_lighting)
    start_radius: FloatProperty(name="Start radius", default=0.1, min=0, max=100000, update=live_lighting)
    end_radius: FloatProperty(name="End radius", default=1, min=0, max=100000, update=live_lighting)
    range: FloatProperty(name="Range", default=5, min=0, max=100000, update=live_lighting)
    inherit: BoolProperty(name="Inherit")


class CHSTAGE_PG_snapshot(PropertyGroup):
    name: StringProperty(name="Name")
    lights: CollectionProperty(type=CHSTAGE_PG_snapshot_light)
    lights_index: IntProperty()
    beams: CollectionProperty(type=CHSTAGE_PG_beam)
    beams_index: IntProperty()
    kept: StringProperty()


class CHSTAGE_PG_mood(PropertyGroup):
    mood: EnumProperty(name="Mood", items=items(sidecars.MOODS), default="intro")
    snapshots: CollectionProperty(type=CHSTAGE_PG_name)
    snapshots_index: IntProperty()
    kept: StringProperty()


class CHSTAGE_PG_color(PropertyGroup):
    name: StringProperty(name="Name")
    color: FloatVectorProperty(name="Color", subtype="COLOR", size=3, default=(1, 1, 1), min=0, soft_max=1)
    kept: StringProperty()


class CHSTAGE_PG_camera_note(PropertyGroup):
    note: IntProperty(name="Note", min=0, max=127, default=77)
    group: StringProperty(name="Group")
    tag: StringProperty(name="Tag")
    type: StringProperty(name="Type")
    use_enable: BoolProperty(name="Set enable")
    enable: BoolProperty(name="Enable", default=True)
    kept: StringProperty()


class CHSTAGE_PG_crowd(PropertyGroup):
    role: EnumProperty(name="Role", items=CROWD_ROLES, default="crowd1")
    model: StringProperty(name="Model", description="Library crowd model name")
    yaw: FloatProperty(name="Yaw", min=-360, max=360)
    kept: StringProperty()


class CHSTAGE_PG_issue(PropertyGroup):
    severity: EnumProperty(items=[("ERROR", "Error", ""), ("WARNING", "Warning", "")])
    message: StringProperty()
    object_name: StringProperty()
    material_name: StringProperty()


class CHSTAGE_PG_script_row(PropertyGroup):
    """One row of the script browser: an object (node array entry), an event (a script that starts), or a value."""

    kind: StringProperty()
    label: StringProperty(name="Name")
    node_id: StringProperty(name="Object")
    script: StringProperty(name="Script")
    info: StringProperty(name="Details")
    objects: StringProperty(name="Blender objects")
    path: StringProperty()
    value_type: IntProperty()
    editable: BoolProperty()


_HAIR_ITEMS = []


def hair_chain_items(self, context):
    import json

    chains = json.loads(self.hair_chains) if self.hair_chains else []
    _HAIR_ITEMS[:] = [("ALL", "All hair bones", "Each vertex goes to the nearest hair bone of all chains")] + [
        (str(i), f"Chain {i + 1} ({len(chain)} bones)", ", ".join(chain)) for i, chain in enumerate(chains)
    ]
    return _HAIR_ITEMS


def preview_selected_clip(self, context):
    from . import character_tools

    character_tools.preview_clip_index(self.id_data)


def _source_view_layer(character):
    scene = character.id_data
    layer = getattr(bpy.context, "view_layer", None)
    return layer if layer is not None and layer in scene.view_layers.values() else scene.view_layers[0]


def get_show_source_rig(self):
    source = self.source_rig
    return (
        source is not None
        and source.name in self.id_data.objects
        and not source.hide_get(view_layer=_source_view_layer(self))
    )


def set_show_source_rig(self, value):
    # The eye of the outliner: this toggle and the outliner show the same state.
    source = self.source_rig
    if source is not None and source.name in self.id_data.objects:
        source.hide_set(not value, view_layer=_source_view_layer(self))


def toggle_map_overlay(self, context):
    from . import skeleton_tools

    skeleton_tools.overlay(self.show_map)


class CHSTAGE_PG_material_ref(PropertyGroup):
    material: PointerProperty(name="Material", type=bpy.types.Material)


class CHSTAGE_PG_clip(PropertyGroup):
    clip: StringProperty(name="Clip ID")
    label: StringProperty(name="Clip")
    duration: FloatProperty(name="Duration")
    frames: IntProperty(name="Frames")


class CHSTAGE_PG_role(PropertyGroup):
    """A humanoid role of the character's own skeleton (character.json skeleton.humanoid)."""

    role: StringProperty(name="Role")
    bone: StringProperty(
        name="Bone", description="The bone of the character's armature that has this role. Empty: no bone"
    )
    confidence: FloatProperty(
        name="Confidence",
        min=0.0,
        max=1.0,
        description="How sure the automatic map is (1: sure). A bone that you set gets 1",
    )


class CHSTAGE_PG_check(PropertyGroup):
    """A finding of Check Clips (the retarget of the GH3 clips on the character's own skeleton)."""

    severity: StringProperty()
    kind: StringProperty()
    role: StringProperty()
    clip: StringProperty()
    message: StringProperty()
    fix: StringProperty()


class CHSTAGE_PG_character(PropertyGroup):
    """character.json of a character package and the state of the character tools."""

    enabled: BoolProperty(description="This scene is a character (.chchar), not a stage")
    profile: EnumProperty(
        name="Profile", items=PROFILE_ITEMS, description="Fixed GH3 rig profile. It also selects the band role"
    )
    library_profile: StringProperty(
        name="Character library profile",
        description="A profile of a character library, for example "
        "gh3-guitarist-midori-v1. Empty: the fixed profile. With a value, 'profile' is the fixed profile of the same role",
    )
    library_id: StringProperty(
        name="Character library",
        description="ID of the character library of the profile (character.json library). Empty: gh3-band-v1",
    )
    character_library: StringProperty(
        name="Character library file",
        subtype="FILE_PATH",
        description="The character library package (gh3-characters-v1.chlib)",
    )
    animations: StringProperty(
        name="Animation set",
        description="character.json animations (character library profiles). Empty: the set of the profile",
    )
    rig: PointerProperty(
        name="Rig",
        type=bpy.types.Object,
        description="The armature of the profile rig. Export writes it and its skinned meshes",
    )
    reference: PointerProperty(
        name="Reference body", type=bpy.types.Object, description="The GH3 body of the profile (helper, never exported)"
    )
    attachment: PointerProperty(
        name="Attachment",
        type=bpy.types.Object,
        description="The instrument, microphone, or drum kit of the profile (helper, never exported)",
    )
    library: StringProperty(
        name="Library", subtype="FILE_PATH", description="The library package of the rig and the clips"
    )
    hair_materials: CollectionProperty(
        type=CHSTAGE_PG_material_ref,
        description="Materials with sorted hair transparency (character.json hairMaterials)",
    )
    hair_materials_index: IntProperty()
    hair_chains: StringProperty(description="Hair chains of the profile (JSON list of joint name lists)")
    hair_chain: EnumProperty(name="Hair bones", items=hair_chain_items)
    hair_reference: StringProperty(description="Material names of the hair cards of the reference body (JSON list)")
    instrument: StringProperty(
        name="Instrument",
        description="Library model ID of the instrument (character.json attachments). Empty: the profile default",
    )
    clips: CollectionProperty(type=CHSTAGE_PG_clip)
    clips_index: IntProperty(default=-1, update=preview_selected_clip)
    clip: StringProperty(name="Previewed clip")
    own_skeleton: BoolProperty(
        name="Own skeleton",
        description="The character keeps the skeleton of its model; the game retargets the GH3 clips of "
        "the profile to it (character.json skeleton)",
    )
    source_rig: PointerProperty(
        name="GH3 rig",
        type=bpy.types.Object,
        description="The GH3 rig of the profile (helper, never exported): "
        "the clip preview plays the GH3 clip on it and retargets it to the character's armature",
    )
    show_source_rig: BoolProperty(
        name="Show GH3 Rig",
        get=get_show_source_rig,
        set=set_show_source_rig,
        description="Show the GH3 rig (animation source) in the viewport, at the size of your model. It shows "
        "what the GH3 clip does; your armature follows it. Same as the eye in the outliner",
    )
    roles: CollectionProperty(type=CHSTAGE_PG_role, description="The humanoid map (character.json skeleton.humanoid)")
    roles_index: IntProperty()
    followers: StringProperty(
        description="character.json skeleton.followers (JSON list): bones that turn with another bone (MMD append rotation)"
    )
    instrument_bone: StringProperty(
        name="Instrument bone", description="The bone whose movement the instrument follows. Empty: the top spine bone"
    )
    instrument_offset: FloatVectorProperty(
        name="Instrument offset",
        size=3,
        subtype="TRANSLATION",
        unit="LENGTH",
        description="Moves the instrument from its place (metres, Blender axes)",
    )
    instrument_scale: FloatProperty(
        name="Instrument scale",
        min=0.0,
        max=1.5,
        description="The size of the instrument and of its distance from the chest. "
        "0: the arm length of the character against the arm length of the GH3 rig",
    )
    map_notes: StringProperty(description="Notes of the automatic map and of Set Up Skeleton (JSON list of text)")
    show_map: BoolProperty(
        name="Show Map",
        description="Draw the humanoid map on the armature in the viewport (colours: green sure, yellow check, red missing)",
        update=toggle_map_overlay,
    )
    target_height: FloatProperty(
        name="Height",
        min=0.0,
        max=5.0,
        unit="LENGTH",
        description="Set Up Skeleton scales the model to this height. "
        "0: keep the height when it is 0.5-3 m, else find the unit (cm, mm) or use 1.7 m",
    )
    checks: CollectionProperty(type=CHSTAGE_PG_check)
    checks_index: IntProperty()
    checked: BoolProperty()
    face_blink: StringProperty(name="Blink", description="Shape key that closes the eyes (MMD まばたき, VRM Blink)")
    face_a: StringProperty(name="A", description="Mouth shape key 'a' (MMD あ, VRM A)")
    face_i: StringProperty(name="I", description="Mouth shape key 'i' (MMD い, VRM I)")
    face_u: StringProperty(name="U", description="Mouth shape key 'u' (MMD う, VRM U)")
    face_e: StringProperty(name="E", description="Mouth shape key 'e' (MMD え, VRM E)")
    face_o: StringProperty(name="O", description="Mouth shape key 'o' (MMD お, VRM O)")
    face_pp: StringProperty(name="PP", description="Optional viseme shape key for p, b, m (VRChat vrc.v_pp)")
    face_ff: StringProperty(name="FF", description="Optional viseme shape key for f, v (VRChat vrc.v_ff)")
    face_th: StringProperty(name="TH", description="Optional viseme shape key for th (VRChat vrc.v_th)")
    face_dd: StringProperty(name="DD", description="Optional viseme shape key for t, d (VRChat vrc.v_dd)")
    face_kk: StringProperty(name="KK", description="Optional viseme shape key for k, g (VRChat vrc.v_kk)")
    face_ch: StringProperty(name="CH", description="Optional viseme shape key for ch, j, sh (VRChat vrc.v_ch)")
    face_ss: StringProperty(name="SS", description="Optional viseme shape key for s, z (VRChat vrc.v_ss)")
    face_nn: StringProperty(name="NN", description="Optional viseme shape key for n, l (VRChat vrc.v_nn)")
    face_rr: StringProperty(name="RR", description="Optional viseme shape key for r, w (VRChat vrc.v_rr)")
    face_brow_up: StringProperty(
        name="Brow up", description="Optional shape key that raises the brows (MMD 上, VRoid Fcl_BRW_Surprised)"
    )
    face_brow_down: StringProperty(
        name="Brow down", description="Optional shape key that lowers the brows (MMD 下, VRoid Fcl_BRW_Angry)"
    )
    face_squint: StringProperty(name="Squint", description="Optional shape key that narrows the eyes")


class CHSTAGE_PG_scene(PropertyGroup):
    package: PointerProperty(type=CHSTAGE_PG_package)
    crowd: CollectionProperty(type=CHSTAGE_PG_crowd)
    crowd_index: IntProperty()
    camera_notes: CollectionProperty(type=CHSTAGE_PG_camera_note)
    camera_notes_index: IntProperty()
    snapshots: CollectionProperty(type=CHSTAGE_PG_snapshot)
    snapshots_index: IntProperty()
    moods: CollectionProperty(type=CHSTAGE_PG_mood)
    moods_index: IntProperty()
    colors: CollectionProperty(type=CHSTAGE_PG_color)
    colors_index: IntProperty()
    issues: CollectionProperty(type=CHSTAGE_PG_issue)
    issues_index: IntProperty()
    validated: BoolProperty()
    kept: StringProperty(description="Kept camera groups and placement keys of the opened package")
    kept_files: CollectionProperty(type=CHSTAGE_PG_kept_file)
    kept_files_index: IntProperty()
    source_package: StringProperty(name="Opened package", subtype="FILE_PATH")
    export_path: StringProperty(name="Last export", subtype="FILE_PATH")
    script_rows: CollectionProperty(type=CHSTAGE_PG_script_row)
    script_rows_index: IntProperty()
    script_view: EnumProperty(
        name="View",
        items=[
            ("OBJECTS", "Objects", "Node array entries of the script bank"),
            ("EVENTS", "Events", "Scripts that start, and their objects"),
            ("VALUES", "Values", "Simple typed definitions and bank settings"),
        ],
        default="OBJECTS",
        update=rebuild_scripts,
    )
    script_value: StringProperty(
        name="Value", description="New value of the active row (Set writes it into scripts.json)"
    )
    anchor_frames: StringProperty(
        description="Bone frames of the proxy rigs relative to the performer roots (JSON, runtime coordinates)"
    )
    rig_library: StringProperty(
        name="Rig library", subtype="FILE_PATH", description="The library package of the proxy rigs"
    )
    shots_index: IntProperty(update=preview_selected_shot)
    shot_group: StringProperty(name="Group", description="Show only the shots of this camera group. Empty: all")
    shot_type: EnumProperty(name="Type", items=[("ALL", "All types", "")] + items(sidecars.SHOT_TYPES), default="ALL")
    lighting_preview: BoolProperty(
        name="Live lighting preview",
        description="Show the active snapshot or mood with the Blender lights (Material Preview, EEVEE)",
        update=live_lighting,
    )
    preview_mood: EnumProperty(name="Preview", items=scene_moods, update=live_lighting)
    preview_step: IntProperty(
        name="Snapshot of the mood", min=0, update=live_lighting, description="Snapshot number in the mood list"
    )
    preview_world: PointerProperty(type=bpy.types.World, description="The scene world before the lighting preview")
    light_group_overlay: BoolProperty(
        name="Color by light group",
        update=light_group_overlay,
        description="Color the scenery by light group (Solid shading, object color)",
    )
    beam_overlay: BoolProperty(
        name="Show beams", default=True, description="Draw the beams of the previewed snapshot and the fixture axes"
    )
    character: PointerProperty(type=CHSTAGE_PG_character)


class CHSTAGE_AP_preferences(AddonPreferences):
    bl_idname = __package__

    custom_stages: StringProperty(
        name="Custom/Stages folder",
        subtype="DIR_PATH",
        description="The Custom/Stages folder of Clone Hero. Export can copy the package into it",
    )
    library: StringProperty(
        name="GH3 library (.chlib)",
        subtype="FILE_PATH",
        description="gh3-band-v1.chlib (or its folder). Open loads the band rigs from it to show camera bone anchors; "
        "the character tools load the profile rigs and clips from it. Empty: look next to the opened package",
    )
    import_all_clips: BoolProperty(
        name="Open: import all actor clips",
        default=False,
        description="Off: Open imports the clips that actors.json uses; the actor panel loads the others",
    )
    custom_characters: StringProperty(
        name="Custom/Characters folder",
        subtype="DIR_PATH",
        description="The Custom/Characters folder of Clone Hero. Export .chchar can copy the package into it",
    )
    character_library: StringProperty(
        name="GH3 character library (.chlib)",
        subtype="FILE_PATH",
        description="gh3-characters-v1.chlib: the rigs of the playable GH3 guitarists. The character "
        "tools take their bodies from the GH3 character packages in the same folder. Empty: look next "
        "to the GH3 library",
    )

    def draw(self, context):
        self.layout.prop(self, "custom_stages")
        self.layout.prop(self, "custom_characters")
        self.layout.prop(self, "library")
        self.layout.prop(self, "character_library")
        self.layout.prop(self, "import_all_clips")


CLASSES = (
    CHSTAGE_PG_name,
    CHSTAGE_PG_image_ref,
    CHSTAGE_PG_kept_file,
    CHSTAGE_PG_number,
    CHSTAGE_PG_author,
    CHSTAGE_PG_requirement,
    CHSTAGE_PG_package,
    CHSTAGE_PG_point,
    CHSTAGE_PG_object_ref,
    CHSTAGE_PG_shot,
    CHSTAGE_PG_light,
    CHSTAGE_PG_actor_action,
    CHSTAGE_PG_actor,
    CHSTAGE_PG_object,
    CHSTAGE_PG_material,
    CHSTAGE_PG_snapshot_light,
    CHSTAGE_PG_beam,
    CHSTAGE_PG_snapshot,
    CHSTAGE_PG_mood,
    CHSTAGE_PG_color,
    CHSTAGE_PG_camera_note,
    CHSTAGE_PG_crowd,
    CHSTAGE_PG_issue,
    CHSTAGE_PG_script_row,
    CHSTAGE_PG_material_ref,
    CHSTAGE_PG_clip,
    CHSTAGE_PG_role,
    CHSTAGE_PG_check,
    CHSTAGE_PG_character,
    CHSTAGE_PG_scene,
    CHSTAGE_AP_preferences,
)


def register():
    for cls in CLASSES:
        bpy.utils.register_class(cls)
    bpy.types.Scene.chstage = PointerProperty(type=CHSTAGE_PG_scene)
    bpy.types.Object.chstage = PointerProperty(type=CHSTAGE_PG_object)
    bpy.types.Material.chstage = PointerProperty(type=CHSTAGE_PG_material)


def unregister():
    del bpy.types.Material.chstage
    del bpy.types.Object.chstage
    del bpy.types.Scene.chstage
    for cls in reversed(CLASSES):
        bpy.utils.unregister_class(cls)
