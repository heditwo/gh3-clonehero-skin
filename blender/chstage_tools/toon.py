"""Toon materials: an EEVEE preview of the toon settings, and the conversion of MMD Tools materials (toon ramp, sphere map,
edge) to toon settings. The game shader is in src/Stage/Shaders/Stage.glsl (toon()); the preview is an approximation."""

import contextlib
import os

import bpy
import numpy as np

from . import sidecars

PREVIEW = "chstage_toon"
# MMD edge size to outline width (metres at 1 m from the camera). MMD draws an edge of size 1 about 2 pixels wide in a
# 1080-pixel view; at the GH3 field of view (about 45°) 1.5 mm at 1 m is about 1.7 pixels at 900 pixels.
MMD_EDGE_WIDTH = 0.0025


def preview_nodes(tree):
    return [node for node in tree.nodes if node.get(PREVIEW)]


def principled_output(tree):
    outputs = [n for n in tree.nodes if n.type == "OUTPUT_MATERIAL" and not n.get(PREVIEW)]
    return next((n for n in outputs if n.target == "ALL"), outputs[0] if outputs else None)


def base_color_source(tree):
    """(image node or None, constant color) of the Principled BSDF base color."""
    shader = next((n for n in tree.nodes if n.type == "BSDF_PRINCIPLED"), None)
    if shader is None:
        return None, (1, 1, 1, 1)
    socket = shader.inputs["Base Color"]
    node = socket.links[0].from_node if socket.is_linked else None
    while node is not None and node.type != "TEX_IMAGE":
        inputs = [i for i in node.inputs if i.is_linked and i.type == "RGBA"]
        node = inputs[0].links[0].from_node if inputs else None
    return node, tuple(socket.default_value)


def remove_preview(material):
    tree = material.node_tree
    if tree is None:
        return
    for node in preview_nodes(tree):
        tree.nodes.remove(node)
    output = principled_output(tree)
    if output is not None:
        output.is_active_output = True


def add_preview(material):
    """Make an EEVEE node tree that shows the toon settings: the lit side, the shade, the mask, the sphere map, and the rim."""
    props = material.chstage
    if material.node_tree is None:
        material.use_nodes = True
    tree = material.node_tree
    remove_preview(material)
    nodes, links = tree.nodes, tree.links

    def node(kind, x, y, **settings):
        item = nodes.new(kind)
        item[PREVIEW] = True
        item.location = (x, y - 900)
        for key, value in settings.items():
            setattr(item, key, value)
        return item

    def image(picture, x, y, non_color=False):
        item = node("ShaderNodeTexImage", x, y, image=picture)
        if non_color and picture is not None:
            picture.colorspace_settings.name = "Non-Color"
        return item

    base_node, base_value = base_color_source(tree)
    base = image(base_node.image, -900, 0) if base_node is not None else node("ShaderNodeRGB", -900, 0)
    if base_node is None:
        base.outputs[0].default_value = base_value
    diffuse = node("ShaderNodeBsdfDiffuse", -900, -300)
    to_rgb = node("ShaderNodeShaderToRGB", -700, -300)
    links.new(diffuse.outputs[0], to_rgb.inputs[0])
    gray = node("ShaderNodeRGBToBW", -500, -300)
    links.new(to_rgb.outputs[0], gray.inputs[0])
    light = gray.outputs[0]
    if props.toon_mask_image is not None:
        mask = image(props.toon_mask_image, -700, -500, non_color=True)
        separate = node("ShaderNodeSeparateColor", -500, -500)
        links.new(mask.outputs["Color"], separate.inputs[0])
        bias = node("ShaderNodeMath", -300, -500, operation="MULTIPLY_ADD")
        links.new(separate.outputs[0], bias.inputs[0])
        bias.inputs[1].default_value, bias.inputs[2].default_value = 2.0, -1.0
        add = node("ShaderNodeMath", -100, -400, operation="ADD")
        links.new(light, add.inputs[0])
        links.new(bias.outputs[0], add.inputs[1])
        light = add.outputs[0]
    step = node("ShaderNodeMapRange", 100, -300, interpolation_type="SMOOTHSTEP")
    links.new(light, step.inputs["Value"])
    softness = max(props.toon_softness, 0.001)
    step.inputs["From Min"].default_value = props.toon_threshold - softness
    step.inputs["From Max"].default_value = props.toon_threshold + softness
    shade_source = (
        image(props.toon_shade_image, -500, 200).outputs["Color"] if props.toon_shade_image else base.outputs[0]
    )
    shade = node("ShaderNodeMix", -100, 200, data_type="RGBA", blend_type="MULTIPLY")
    shade.inputs["Factor"].default_value = 1.0
    links.new(shade_source, shade.inputs[6])
    shade.inputs[7].default_value = (*props.toon_shade_color, 1.0)
    mix = node("ShaderNodeMix", 300, 0, data_type="RGBA")
    links.new(step.outputs[0], mix.inputs["Factor"])
    links.new(shade.outputs[2], mix.inputs[6])
    links.new(base.outputs[0], mix.inputs[7])
    color = mix.outputs[2]
    if props.toon_sphere_image is not None:
        coordinates = node("ShaderNodeTexCoord", -100, 600)
        to_view = node(
            "ShaderNodeVectorTransform", 100, 600, vector_type="NORMAL", convert_from="WORLD", convert_to="CAMERA"
        )
        links.new(coordinates.outputs["Normal"], to_view.inputs[0])
        scale = node("ShaderNodeVectorMath", 300, 600, operation="MULTIPLY_ADD")
        links.new(to_view.outputs[0], scale.inputs[0])
        scale.inputs[1].default_value, scale.inputs[2].default_value = (0.5, 0.5, 0), (0.5, 0.5, 0)
        sphere = image(props.toon_sphere_image, 500, 600)
        links.new(scale.outputs[0], sphere.inputs["Vector"])
        combine = node(
            "ShaderNodeMix",
            700,
            300,
            data_type="RGBA",
            blend_type="ADD" if props.toon_sphere_mode == "add" else "MULTIPLY",
        )
        combine.inputs["Factor"].default_value = 1.0
        links.new(color, combine.inputs[6])
        links.new(sphere.outputs["Color"], combine.inputs[7])
        color = combine.outputs[2]
    if props.toon_rim_width > 0:
        weight = node("ShaderNodeLayerWeight", 500, -300)
        rim = node("ShaderNodeMapRange", 700, -300, interpolation_type="SMOOTHSTEP")
        links.new(weight.outputs["Facing"], rim.inputs["Value"])
        rim.inputs["From Min"].default_value = 1 - props.toon_rim_width
        tint = node("ShaderNodeMix", 900, -200, data_type="RGBA", blend_type="ADD")
        links.new(rim.outputs[0], tint.inputs["Factor"])
        links.new(color, tint.inputs[6])
        tint.inputs[7].default_value = (*props.toon_rim_color, 1.0)
        color = tint.outputs[2]
    emission = node("ShaderNodeEmission", 1100, 0)
    links.new(color, emission.inputs["Color"])
    output = node("ShaderNodeOutputMaterial", 1300, 0, target="EEVEE")
    links.new(emission.outputs[0], output.inputs["Surface"])
    output.is_active_output = True


def refresh(material):
    """Rebuild the preview of a material when its preview is on (a property update)."""
    if (
        not isinstance(material, bpy.types.Material)
        or material.node_tree is None
        or not preview_nodes(material.node_tree)
    ):
        return
    if material.chstage.shading == "toon":
        add_preview(material)
    else:
        remove_preview(material)


def set_preview(materials, enabled):
    count = 0
    for material in materials:
        if enabled and material.chstage.shading == "toon":
            add_preview(material)
            count += 1
        elif material.node_tree is not None and preview_nodes(material.node_tree):
            remove_preview(material)
            count += 1
    return count


@contextlib.contextmanager
def preview_off_for_export(materials):
    """The glTF exporter reads the active material output: export the Principled BSDF, then show the preview again."""
    shown = [m for m in materials if m.node_tree is not None and preview_nodes(m.node_tree)]
    try:
        for material in shown:
            output = principled_output(material.node_tree)
            if output is not None:
                output.is_active_output = True
        yield
    finally:
        for material in shown:
            output = next((n for n in preview_nodes(material.node_tree) if n.type == "OUTPUT_MATERIAL"), None)
            if output is not None:
                output.is_active_output = True


# MMD Tools materials.
def image_rows(image):
    """Pixels of a Blender image, rows from the top, RGBA 0-1."""
    width, height = image.size
    pixels = np.array(image.pixels[:], dtype=float).reshape(height, width, image.channels)
    if image.channels < 4:
        pixels = np.concatenate([pixels, np.ones((height, width, 4 - image.channels))], axis=2)
    return pixels[::-1]


def load_image(path):
    if not path or not os.path.isfile(bpy.path.abspath(path)):
        return None
    return bpy.data.images.load(bpy.path.abspath(path), check_existing=True)


def from_mmd(material):
    """Set the toon settings of a material from its MMD Tools data (mmd_material and the mmd_toon_tex and mmd_sphere_tex
    image nodes). Returns a list of notes, or None when the material has no MMD data."""
    mmd = getattr(material, "mmd_material", None)
    if mmd is None:
        return None
    props = material.chstage
    notes = []
    tree = material.node_tree
    named = {n.name: n for n in tree.nodes} if tree is not None else {}
    toon_node = named.get("mmd_toon_tex")
    toon_image = (
        toon_node.image if toon_node is not None and toon_node.image is not None else load_image(mmd.toon_texture)
    )
    props.shading = "toon"
    if toon_image is not None and toon_image.size[0] > 0:
        props.toon_threshold, props.toon_softness, shade = sidecars.toon_ramp_settings(image_rows(toon_image))
        props.toon_shade_color = shade
    else:
        # MMD shades nothing without a toon texture.
        props.toon_threshold, props.toon_softness, props.toon_shade_color = 0.0, 0.001, (1, 1, 1)
        notes.append("no toon texture: always lit")
    sphere_node = named.get("mmd_sphere_tex")
    if mmd.sphere_texture_type in ("1", "2", 1, 2) and sphere_node is not None and sphere_node.image is not None:
        props.toon_sphere_image = sphere_node.image
        props.toon_sphere_mode = "multiply" if str(mmd.sphere_texture_type) == "1" else "add"
    else:
        props.toon_sphere_image = None
        if str(mmd.sphere_texture_type) == "3":
            notes.append("sub-texture sphere maps are not supported")
    props.use_outline = bool(mmd.enabled_toon_edge)
    if props.use_outline:
        props.outline_width = min(0.05, float(mmd.edge_weight) * MMD_EDGE_WIDTH)
        props.outline_color = tuple(float(c) for c in mmd.edge_color[:3])
        props.outline_textured = False
    props.two_sided = bool(mmd.is_double_sided)
    return notes
