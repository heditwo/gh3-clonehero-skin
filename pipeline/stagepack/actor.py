"""Compile ordinary glTF skeletons and looping stage-prop animations."""

import json
import re
import struct

import numpy as np

from pipeline.gh3.formats import key
from pipeline.gh3.mesh_cutout import alpha_region, trim_mesh
from pipeline.stagepack.gltf import (
    REFLECTION,
    SKINNED_GH3_VERTEX,
    VERTEX,
    GlbReader,
    matrix_quaternion,
    transform_matrix,
)

SKIN_VERTEX = np.dtype(VERTEX.descr + [("joints", "u1", 4), ("weights", "<f4", 4)])
UNIT_SCALE = 1e-4


def compile_actor(path, folder, material_import, mesh_write, mesh_split, root=None):
    glb = GlbReader(path, root)
    nodes = list(glb.nodes())
    if len(glb.data.get("skins", [])) != 1: raise ValueError("An actor GLB needs one skin")
    skin = glb.data["skins"][0]
    joints = skin["joints"]
    if not 1 <= len(joints) <= 255 or len(set(joints)) != len(joints): raise ValueError("Invalid actor joints")
    world = {i:m for i,n,m in nodes}
    if any(j not in world for j in joints): raise ValueError("Actor joint is outside the scene")
    parent_nodes = {child:i for i,node,_ in nodes for child in node.get("children", [])}
    parents = [joints.index(parent_nodes[j]) if parent_nodes.get(j) in joints else -1 for j in joints]
    bind = []
    for i,j in enumerate(joints):
        local = np.linalg.inv(world[joints[parents[i]]]) @ world[j] if parents[i] >= 0 else world[j]
        if not np.allclose(local[:3, :3].T @ local[:3, :3], np.eye(3), atol=1e-4): raise ValueError("Apply actor joint scale before export")
        bind.append([*local[:3, 3], *matrix_quaternion(local)])
    inverse = glb.accessor(skin["inverseBindMatrices"]).reshape(-1,4,4).transpose(0,2,1)
    if len(inverse) != len(joints): raise ValueError("Actor inverse bind count differs")
    materials, material_ids, images = material_import(glb, folder)
    meshes = []
    for index,node,matrix in nodes:
        if "mesh" not in node: continue
        if node.get("skin") != 0: raise ValueError("All actor meshes must use its skin")
        if any(not np.allclose(world[j] @ REFLECTION @ inverse[i] @ REFLECTION, matrix, atol=1e-4) for i,j in enumerate(joints)):
            raise ValueError("Actor inverse bind matrices differ from its rig")
        for primitive in glb.data["meshes"][node["mesh"]]["primitives"]:
            packed, faces = glb.primitive(primitive, matrix)
            attrs = primitive["attributes"]
            weights = glb.accessor(attrs["WEIGHTS_0"])
            indices = glb.accessor(attrs["JOINTS_0"])
            if indices.shape != (len(packed),4) or weights.shape != indices.shape or np.any(indices >= len(joints)) or np.any(weights < 0) or not np.allclose(weights.sum(1), 1, atol=1e-4):
                raise ValueError("Invalid actor skin weights")
            # Actor meshes always use the extended GH3BND02 layout; meshes without GH3 streams keep zero streams.
            vertices = np.zeros(len(packed), dtype=SKINNED_GH3_VERTEX)
            for field in VERTEX.names: vertices[field] = packed[field]
            vertices["gh3uv"] = packed["gh3uv"]; vertices["gh3binormal"] = packed["gh3binormal"]
            if "_GH3_TANGENT" in attrs:
                tangents = glb.accessor(attrs["_GH3_TANGENT"])
                if tangents.shape != (len(packed), 3): raise ValueError("Invalid GH3 tangent stream")
                vertices["gh3tangent"] = tangents @ (matrix @ REFLECTION)[:3, :3].T
            vertices["joints"] = indices; vertices["weights"] = weights
            material = material_ids[primitive.get("material", len(material_ids)-1)]
            meshes.extend(mesh_split(material, vertices, faces))
    if not meshes: raise ValueError("Actor has no visible geometry")
    trimmed = []; masks = {}
    for mesh in meshes:
        material = materials[mesh[0]]
        if material["texture"] and material["cutoff"] > 0 and not material["crowd"] and not material.get("gh3"):
            pair = material["texture"], material["cutoff"]
            if pair not in masks: masks[pair] = alpha_region(images[pair[0]], pair[1])
            mesh = trim_mesh(mesh, images[pair[0]], pair[1], masks[pair])
        if mesh[3]: trimmed.extend(mesh_split(mesh[0], np.frombuffer(mesh[2],dtype=SKINNED_GH3_VERTEX), np.asarray(mesh[3])))
    meshes = trimmed
    if not meshes: raise ValueError("Actor alpha masks remove all geometry")
    mesh_write(folder, meshes, materials, skinned=True)
    bones = {}
    for index, joint in enumerate(joints):
        name = glb.data["nodes"][joint].get("name", f"joint-{index}")
        checksum = int(name[5:], 16) if re.fullmatch(r"bone-[0-9a-f]{8}", name) else key(name)
        if f"{checksum:08x}" in bones: raise ValueError("Actor bone names have the same checksum")
        bones[f"{checksum:08x}"] = index
    (folder/"bones.json").write_text(json.dumps(bones, indent=2)+"\n")
    animations = glb.data.get("animations", [])
    if len(animations) > 256: raise ValueError("Actor has too many animation clips")
    names = [a.get("name", f"clip-{i}") for i,a in enumerate(animations)]
    if len(set(names)) != len(names): raise ValueError("Duplicate actor animation names")
    sampled = []
    for track in animations:
        tracks = {}
        duration = 0.
        for channel in track["channels"]:
            target = channel["target"]; sampler = track["samplers"][channel["sampler"]]
            if target["path"] == "scale" and target["node"] in joints and sampler.get("interpolation", "LINEAR") in ("LINEAR", "STEP"):
                # Blender writes a constant scale channel for every joint; unit scale keys carry no motion (PACKAGE-FORMAT 6.7).
                error = float(np.abs(glb.accessor(sampler["output"]).astype(float) - 1).max(initial=0))
                if error > UNIT_SCALE:
                    joint = glb.data["nodes"][target["node"]].get("name", f"node {target['node']}")
                    raise ValueError(f"Actor clip '{track.get('name', '')}' scales joint '{joint}' (|scale - 1| = {error:.3g}, limit {UNIT_SCALE}); "
                                     "animate only joint translation and rotation")
                continue
            if target["node"] not in joints or target["path"] not in ("translation", "rotation") or sampler.get("interpolation", "LINEAR") not in ("LINEAR", "STEP"):
                raise ValueError("Bake actor animation to joint translation/rotation with LINEAR or STEP interpolation")
            pair = target["node"], target["path"]
            if pair in tracks: raise ValueError("Duplicate actor animation channel")
            times = glb.accessor(sampler["input"]).reshape(-1)
            values = glb.accessor(sampler["output"])
            if np.any(np.diff(times) <= 0) or times[0] < 0 or len(times) != len(values): raise ValueError("Invalid actor key times")
            if values.shape[1] != (4 if pair[1] == "rotation" else 3): raise ValueError("Invalid actor key values")
            if pair[1] == "rotation" and not np.allclose(np.linalg.norm(values,axis=1),1,atol=0.001): raise ValueError("Invalid actor key rotation")
            tracks[pair] = times, values, sampler.get("interpolation", "LINEAR")
            duration = max(duration, float(times[-1]))
        if not 0 < duration <= 120: raise ValueError("Actor clip must be longer than zero and at most 120 seconds")
        duration = max(0.5, duration)
        samples = np.linspace(0, duration, max(2, round(duration*60)+1))
        poses = np.tile(np.asarray(bind), (len(samples),1,1))
        for bone,j in enumerate(joints):
            parent_transform = world[parent_nodes[j]] if parents[bone] < 0 and j in parent_nodes else None
            if parent_transform is not None:
                local = REFLECTION @ transform_matrix(glb.data["nodes"][j]) @ REFLECTION
                poses[:,bone,:3] = local[:3,3]
                poses[:,bone,3:] = matrix_quaternion(local)
            for prop in ("translation", "rotation"):
                if (j,prop) not in tracks: continue
                times, values, mode = tracks[j,prop]
                values = values.astype(float)
                if prop == "rotation":
                    for i in range(1,len(values)):
                        if np.dot(values[i-1],values[i]) < 0: values[i] *= -1
                if mode == "STEP":
                    result = values[np.maximum(0,np.searchsorted(times,samples,side="right")-1)]
                else:
                    result = np.column_stack([np.interp(samples,times,values[:,c]) for c in range(values.shape[1])])
                if prop == "rotation":
                    result /= np.linalg.norm(result,axis=1)[:,None]
                    poses[:,bone,3:] = result*[-1,-1,1,1]
                else: poses[:,bone,:3] = result*[1,1,-1]
            if parent_transform is not None:
                poses[:,bone,:3] = poses[:,bone,:3] @ parent_transform[:3,:3].T + parent_transform[:3,3]
                q = np.asarray(matrix_quaternion(parent_transform))
                v, w = poses[:,bone,3:6].copy(), poses[:,bone,6:7].copy()
                poses[:,bone,3:6] = q[3]*v+w*q[:3]+np.cross(q[:3],v)
                poses[:,bone,6] = q[3]*w[:,0]-np.sum(q[:3]*v,axis=1)
        sampled.append((duration, poses))
    if not sampled:
        names = ["bind"]
        sampled = [(1., np.tile(np.asarray(bind), (2,1,1)))]
    with (folder/"animation.bin").open("wb") as stream:
        stream.write(b"GH3ANM01"+struct.pack("<II",len(joints),len(sampled)))
        for parent,pose in zip(parents,bind): stream.write(struct.pack("<i7f",parent,*pose))
        for duration,poses in sampled:
            stream.write(struct.pack("<fI",duration,len(poses)))
            stream.write(poses.astype("<f4").tobytes())
    return names
