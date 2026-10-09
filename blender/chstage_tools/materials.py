"""Material node trees for package export: the lightmap as the glTF occlusion texture, and image bytes."""

import contextlib
import os
import tempfile

import bpy

from . import glb_tools

# The Blender glTF exporter reads the occlusion texture from a node group with one of these names (lower case prefix).
SETTINGS_GROUPS = ("gltf material output", "gltf settings")
SETTINGS_GROUP = "glTF Material Output"
OWN_GROUP = "glTF Material Output (CH Stage)"


def occlusion_socket(tree):
    for node in tree.nodes:
        if node.type == "GROUP" and node.node_tree is not None and node.node_tree.name.lower().startswith(SETTINGS_GROUPS):
            socket = node.inputs.get("Occlusion")
            if socket is not None: return socket
    return None


def settings_group():
    for name in (SETTINGS_GROUP, OWN_GROUP):
        group = bpy.data.node_groups.get(name)
        if group is not None and group.bl_idname == "ShaderNodeTree" and "Occlusion" in group.interface.items_tree: return group
    # Not the importer's name: the importer reuses a group with its name and needs its other sockets.
    group = bpy.data.node_groups.new(OWN_GROUP, "ShaderNodeTree")
    group.interface.new_socket("Occlusion", in_out="INPUT", socket_type="NodeSocketFloat")
    group.nodes.new("NodeGroupInput")
    group.nodes.new("NodeGroupOutput")
    return group


def lightmap_chain(socket):
    """(image node, UV map name or None) that drive the occlusion socket, or None."""
    if socket is None or not socket.is_linked: return None
    node = socket.links[0].from_node
    if node.type in ("SEPARATE_COLOR", "SEPRGB"):
        if not node.inputs[0].is_linked: return None
        node = node.inputs[0].links[0].from_node
    if node.type != "TEX_IMAGE": return None
    vector = node.inputs["Vector"]
    uv = vector.links[0].from_node.uv_map if vector.is_linked and vector.links[0].from_node.type == "UVMAP" else None
    return node, uv


def sync_lightmap(material):
    """Make the node tree give the material lightmap (material.chstage.lightmap) to the glTF exporter as the occlusion
    texture on the lightmap UV map. The glTF importer makes the same nodes, so an opened package keeps its nodes."""
    props = material.chstage
    if props.lightmap is None:
        socket = occlusion_socket(material.node_tree) if material.node_tree else None
        if socket is not None:
            for link in list(socket.links): material.node_tree.links.remove(link)
        return
    if material.node_tree is None: material.use_nodes = True
    tree = material.node_tree
    socket = occlusion_socket(tree)
    chain = lightmap_chain(socket)
    if chain is not None and chain[0].image == props.lightmap and chain[1] == props.lightmap_uv: return
    if socket is None:
        group = tree.nodes.new("ShaderNodeGroup")
        group.node_tree = settings_group()
        group.label = SETTINGS_GROUP
        group.location = (300, -300)
        socket = group.inputs["Occlusion"]
    for link in list(socket.links): tree.links.remove(link)
    image = tree.nodes.new("ShaderNodeTexImage")
    image.image, image.label, image.location = props.lightmap, "Lightmap", (-200, -300)
    separate = tree.nodes.new("ShaderNodeSeparateColor")
    separate.location = (100, -300)
    uv = tree.nodes.new("ShaderNodeUVMap")
    uv.uv_map, uv.location = props.lightmap_uv, (-400, -300)
    tree.links.new(uv.outputs[0], image.inputs["Vector"])
    tree.links.new(image.outputs["Color"], separate.inputs[0])
    tree.links.new(separate.outputs[0], socket)


@contextlib.contextmanager
def vertex_alpha_for_export(materials):
    """Link the vertex color alpha to the shader alpha while the glTF exporter runs.

    The Blender 4.2 exporter writes COLOR_0 without its alpha channel when the alpha of the material node tree is not
    linked (an opaque material, as the glTF importer makes it). GH3 opaque materials can have vertex alpha. The
    add-on writes alphaMode itself, so the temporary link changes only the vertex color channels.
    """
    links = []
    try:
        for material in materials:
            tree = material.node_tree
            if tree is None: continue
            shader = next((n for n in tree.nodes if n.type == "BSDF_PRINCIPLED"), None)
            color = next((n for n in tree.nodes if n.type == "VERTEX_COLOR"), None)
            if shader is None or color is None or shader.inputs["Alpha"].is_linked: continue
            links.append((tree, tree.links.new(color.outputs["Alpha"], shader.inputs["Alpha"])))
        yield
    finally:
        for tree, link in links: tree.links.remove(link)


def lightmap_problem(material, objects):
    """Why the lightmap of the material cannot go to TEXCOORD_1 of the objects that use it, or None."""
    props = material.chstage
    if props.lightmap is None: return None
    for obj in objects:
        if obj.type != "MESH" or not any(slot.material == material for slot in obj.material_slots): continue
        # The exporter gives a texture the TEXCOORD index of its UV map in the mesh.
        if obj.data.uv_layers.find(props.lightmap_uv) != 1:
            return obj, f"material '{material.name}': the lightmap UV map '{props.lightmap_uv}' must be the second UV map of '{obj.name}' (glTF TEXCOORD_1)"
    return None


def image_png_bytes(image):
    """PNG bytes of a Blender image: the packed or file bytes when they are PNG, else the image saved as PNG."""
    if image.packed_file is not None and image.packed_file.data[:8] == glb_tools.PNG_SIGNATURE: return bytes(image.packed_file.data)
    path = bpy.path.abspath(image.filepath) if image.filepath else ""
    if path and os.path.isfile(path):
        with open(path, "rb") as stream: data = stream.read()
        if data[:8] == glb_tools.PNG_SIGNATURE: return data
    with tempfile.TemporaryDirectory(prefix="chstage-image-") as folder:
        target = os.path.join(folder, "image.png")
        copy = image.copy()
        try:
            copy.filepath_raw = target
            copy.file_format = "PNG"
            copy.save()
        finally:
            bpy.data.images.remove(copy)
        with open(target, "rb") as stream: return stream.read()
