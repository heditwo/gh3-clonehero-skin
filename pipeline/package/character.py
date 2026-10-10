"""Characters on the fixed GH3 rigs and on the profiles of a character library (docs/PACKAGE-FORMAT.md sections 9, 10.1).

The fixed profile rigs come from the library (band/<role>/model.glb); a character library profile has its rig in
characters.json, and its GH3 character package (gh3.<name>...chchar, next to the library) has its body. This module splits a library band model into the body
and the attachment (instrument, microphone, drum kit), finds the hair chains of a profile, turns q48 clips into local
joint poses, limits vertex weights, builds and reads character.json, and converts a library band member into a
.chchar package (the round-trip reference of the Blender add-on).

numpy only and relative imports, so the Blender add-on bundles it (blender/build.py).

Usage: python -m pipeline.package.character --library assets/packages/gh3-band-v1.chlib --output DIR [--role ROLE ...]
"""

import argparse
import json
import struct
import sys
from pathlib import Path

import numpy as np

from . import container, decisions
from .glb import Glb
from .validate import CHARACTER_LIBRARY_ID, LIBRARY_ID, PROFILES, Report, Source, gh3_key

FORMAT = "chchar/1.0"
FORMAT_1_1 = "chchar/1.1"
MODEL_FILE = "character.glb"
CHARACTER_FILE = "character.json"
ROLE_PROFILES = {role: profile for profile, role in PROFILES.items()}
ATTACHMENTS = {"guitarist": "guitar", "bassist": "bass", "singer": "microphone and stand", "drummer": "drum kit and sticks"}
MAX_INFLUENCES = 4
# A primitive is hair when this part of its vertices has weight on the hair joints of the profile (band/hair.json).
HAIR_SHARE = 0.3
# GH3 bone names that the rigs use (the joints are named by checksum). Labels for the add-on panels only.
BONE_NAMES = (
    "control_root", "bone_pelvis", "bone_stomach_lower", "bone_stomach_upper", "bone_chest", "bone_neck", "bone_head", "bone_jaw",
    "bone_collar_l", "bone_collar_r", "bone_bicep_l", "bone_bicep_r", "bone_forearm_l", "bone_forearm_r", "bone_palm_l", "bone_palm_r",
    "bone_thigh_l", "bone_thigh_r", "bone_knee_l", "bone_knee_r", "bone_ankle_l", "bone_ankle_r", "bone_toe_l", "bone_toe_r",
    "bone_twist_bicep_top_l", "bone_split_ass_l", "bone_lip_upper_mid", "bone_brow_l", "bone_eye_l", "bone_cheek_l", "bone_tongue",
    "bone_guitar_body", "bone_guitar_neck", "bone_mic",
)
BONE_LABELS = {f"{gh3_key(name):08x}": name for name in BONE_NAMES}
GLB_MAGIC, JSON_CHUNK, BIN_CHUNK = 0x46546C67, 0x4E4F534A, 0x004E4942


def bone_label(joint):
    """'bone_head' for 'bone-ddec28af'; the checksum for a bone without a known name."""
    checksum = joint.removeprefix("bone-").lower()
    return BONE_LABELS.get(checksum, checksum)


def joint_name(checksum):
    return f"bone-{checksum}"


# GLB edits.
def write_glb(gltf, binary):
    text = json.dumps(gltf, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    text += b" " * (-len(text) % 4)
    chunks = struct.pack("<2I", len(text), JSON_CHUNK) + text
    if binary:
        binary = bytes(binary) + b"\0" * (-len(binary) % 4)
        chunks += struct.pack("<2I", len(binary), BIN_CHUNK) + binary
    return struct.pack("<3I", GLB_MAGIC, 2, 12 + len(chunks)) + chunks


def texture_reference_names(material):
    """Image names of the '#<name>' texture references of a material (PACKAGE-FORMAT 6.6)."""
    extras = material.get("extras", {}) if isinstance(material.get("extras"), dict) else {}
    refs = [extras.get("gh3MaskTexture"), extras.get("gh3LayerTexture")]
    toon = extras.get("gh3Toon")
    if isinstance(toon, dict): refs += [toon.get(key) for key in ("shadeTexture", "shadeMaskTexture", "sphereTexture")]
    shader = extras.get("gh3Shader")
    if isinstance(shader, dict) and isinstance(shader.get("textures"), list): refs += shader["textures"]
    return {ref[1:] for ref in refs if isinstance(ref, str) and ref.startswith("#")}


def _texture_indices(value, found):
    if isinstance(value, dict):
        for key, item in value.items():
            if key.endswith("Texture") and isinstance(item, dict) and isinstance(item.get("index"), int): found.add(item["index"])
            _texture_indices(item, found)
    elif isinstance(value, list):
        for item in value: _texture_indices(item, found)


def _renumber_textures(value, mapping):
    if isinstance(value, dict):
        for key, item in value.items():
            if key.endswith("Texture") and isinstance(item, dict) and isinstance(item.get("index"), int): item["index"] = mapping[item["index"]]
            _renumber_textures(item, mapping)
    elif isinstance(value, list):
        for item in value: _renumber_textures(item, mapping)


def compact(gltf, binary):
    """Remove the materials, textures, images, samplers, accessors, and buffer views that nothing uses, and rewrite the
    binary chunk with the used buffer views only. Returns (glTF JSON, binary)."""
    meshes = gltf.get("meshes", [])
    used = sorted({p["material"] for m in meshes for p in m.get("primitives", []) if "material" in p})
    mapping = {old: new for new, old in enumerate(used)}
    gltf["materials"] = [gltf["materials"][i] for i in used]
    for mesh in meshes:
        for primitive in mesh.get("primitives", []):
            if "material" in primitive: primitive["material"] = mapping[primitive["material"]]
    textures, images = gltf.get("textures", []), gltf.get("images", [])
    referenced = set().union(*(texture_reference_names(m) for m in gltf["materials"])) if gltf["materials"] else set()
    used_textures = set()
    _texture_indices(gltf["materials"], used_textures)
    used_textures |= {i for i, t in enumerate(textures) if t.get("source") is not None and images[t["source"]].get("name") in referenced}
    used_images = {textures[i]["source"] for i in used_textures if "source" in textures[i]}
    used_images |= {i for i, image in enumerate(images) if image.get("name") in referenced}
    texture_map = {old: new for new, old in enumerate(sorted(used_textures))}
    image_map = {old: new for new, old in enumerate(sorted(used_images))}
    sampler_list = sorted({textures[i]["sampler"] for i in used_textures if "sampler" in textures[i]})
    sampler_map = {old: new for new, old in enumerate(sampler_list)}
    new_textures = []
    for old in sorted(used_textures):
        texture = dict(textures[old])
        if "source" in texture: texture["source"] = image_map[texture["source"]]
        if "sampler" in texture: texture["sampler"] = sampler_map[texture["sampler"]]
        new_textures.append(texture)
    _renumber_textures(gltf["materials"], texture_map)
    for key, values in (("textures", new_textures), ("images", [images[i] for i in sorted(used_images)]),
                        ("samplers", [gltf.get("samplers", [])[i] for i in sampler_list])):
        if values: gltf[key] = values
        else: gltf.pop(key, None)
    if not gltf["materials"]: gltf.pop("materials")

    accessors = set()
    for mesh in meshes:
        for primitive in mesh.get("primitives", []):
            accessors.update(primitive.get("attributes", {}).values())
            if "indices" in primitive: accessors.add(primitive["indices"])
            for target in primitive.get("targets", []): accessors.update(target.values())
    for skin in gltf.get("skins", []):
        if "inverseBindMatrices" in skin: accessors.add(skin["inverseBindMatrices"])
    for animation in gltf.get("animations", []):
        for sampler in animation.get("samplers", []): accessors.update((sampler["input"], sampler["output"]))
    accessor_map = {old: new for new, old in enumerate(sorted(accessors))}
    views = {gltf["accessors"][i]["bufferView"] for i in accessors if "bufferView" in gltf["accessors"][i]}
    views |= {image["bufferView"] for image in gltf.get("images", []) if "bufferView" in image}
    view_map, new_views, out = {}, [], bytearray()
    for old in sorted(views):
        view = dict(gltf["bufferViews"][old])
        start = view.get("byteOffset", 0)
        out += b"\0" * (-len(out) % 4)
        view["byteOffset"] = len(out)
        out += binary[start:start + view["byteLength"]]
        view_map[old] = len(new_views)
        new_views.append(view)
    gltf["accessors"] = [dict(gltf["accessors"][i]) for i in sorted(accessors)]
    for accessor in gltf["accessors"]:
        if "bufferView" in accessor: accessor["bufferView"] = view_map[accessor["bufferView"]]
    gltf["bufferViews"] = new_views
    for image in gltf.get("images", []):
        if "bufferView" in image: image["bufferView"] = view_map[image["bufferView"]]
    for mesh in meshes:
        for primitive in mesh.get("primitives", []):
            primitive["attributes"] = {k: accessor_map[v] for k, v in primitive["attributes"].items()}
            if "indices" in primitive: primitive["indices"] = accessor_map[primitive["indices"]]
            if "targets" in primitive: primitive["targets"] = [{k: accessor_map[v] for k, v in t.items()} for t in primitive["targets"]]
    for skin in gltf.get("skins", []):
        if "inverseBindMatrices" in skin: skin["inverseBindMatrices"] = accessor_map[skin["inverseBindMatrices"]]
    for animation in gltf.get("animations", []):
        for sampler in animation.get("samplers", []): sampler["input"], sampler["output"] = accessor_map[sampler["input"]], accessor_map[sampler["output"]]
    gltf["buffers"] = [{"byteLength": len(out)}]
    return gltf, bytes(out)


# Profile rigs.
class Rig:
    """The joints of a skinned GLB: names ('bone-<checksum>'), parent joint indices, and glTF local matrices."""

    def __init__(self, glb=None):
        if glb is None: return
        nodes = glb.list("nodes")
        skin = glb.list("skins")[0]
        self.nodes = list(skin["joints"])
        parents = glb.parents()
        self.names = [nodes[j].get("name", "") for j in self.nodes]
        self.parents = [self.nodes.index(parents[j]) if parents.get(j) in self.nodes else -1 for j in self.nodes]
        self.local = np.array([glb.node_matrix(nodes[j]) for j in self.nodes])
        self._world()

    @classmethod
    def from_bind(cls, bones, parents, bind):
        """The rig of a character library profile: bone checksums, parent indices, and the local bind transforms
        [x, y, z, qx, qy, qz, qw] in runtime coordinates (glTF negates z of positions and x, y of rotations)."""
        rig = cls()
        rig.nodes = list(range(len(bones)))
        rig.names = [joint_name(bone.lower()) for bone in bones]
        rig.parents = list(parents)
        bind = np.asarray(bind, dtype=np.float64)
        local = np.tile(np.eye(4), (len(bones), 1, 1))
        local[:, :3, :3] = quaternion_matrices(bind[:, 3:] * [-1, -1, 1, 1])
        local[:, :3, 3] = bind[:, :3] * [1, 1, -1]
        rig.local = local
        rig._world()
        return rig

    def _world(self):
        world = []
        for index, parent in enumerate(self.parents):
            world.append(self.local[index] if parent < 0 else world[parent] @ self.local[index])
        self.world = np.array(world)

    def index(self, name):
        return self.names.index(name) if name in self.names else -1

    def descendants(self, root):
        found, stack = set(), [root]
        while stack:
            joint = stack.pop()
            found.add(joint)
            stack.extend(i for i, p in enumerate(self.parents) if p == joint)
        return found

    def body(self):
        """The joints of the performer body: the subtree of the parent of bone_pelvis. The other subtrees of the root
        (instrument, microphone stand, drum kit, and IK helpers) are attachments."""
        pelvis = self.index(joint_name(f"{gh3_key('bone_pelvis'):08x}"))
        root = self.parents[pelvis] if pelvis >= 0 and self.parents[pelvis] >= 0 else min(1, len(self.names) - 1)
        return self.descendants(root)


def hair_joints(hair, role):
    """Joint indices of the hair chains of a role (band/hair.json)."""
    member = (hair or {}).get("members", {}).get(role)
    return [joint["bone"] for joint in member["joints"]] if member else []


def hair_chains(hair, role, rig):
    """The hair chains of a role: lists of joint names from the chain root (a child of the head) to the tip."""
    member = (hair or {}).get("members", {}).get(role)
    return joint_chains(member["joints"], rig) if member else []


def joint_chains(hair_joints, rig):
    """Chains of hair joints ({'bone', 'parent'} rows of band/hair.json or characters.json): lists of joint names
    from the chain root to the tip."""
    joints = {joint["bone"]: joint["parent"] for joint in hair_joints}
    chains = []
    for root in sorted(j for j, p in joints.items() if p not in joints):
        chain, stack = [], [root]
        while stack:
            joint = stack.pop(0)
            chain.append(rig.names[joint])
            stack.extend(sorted(j for j, p in joints.items() if p == joint))
        chains.append(chain)
    return chains


def primitive_kinds(glb, rig, hair):
    """'body', 'hair', or 'attachment' for each primitive: {(mesh index, primitive index): kind}. A primitive is an
    attachment when most of its vertices have their largest weight outside the body joints."""
    body = rig.body()
    hair = set(hair)
    kinds = {}
    for mesh_index, mesh in enumerate(glb.list("meshes")):
        for index, primitive in enumerate(mesh.get("primitives", [])):
            attributes = primitive.get("attributes", {})
            if "JOINTS_0" not in attributes:
                kinds[mesh_index, index] = "attachment"; continue
            joints = glb.accessor(attributes["JOINTS_0"]).astype(int)
            weights = glb.accessor(attributes["WEIGHTS_0"])
            dominant = joints[np.arange(len(joints)), weights.argmax(1)]
            if np.mean([j not in body for j in dominant]) > 0.5: kinds[mesh_index, index] = "attachment"
            elif hair and np.mean(((np.isin(joints, list(hair))) & (weights > 0)).any(1)) > HAIR_SHARE: kinds[mesh_index, index] = "hair"
            else: kinds[mesh_index, index] = "body"
    return kinds


def keep_primitives(data, keep, animations=False):
    """GLB bytes with only the primitives for which keep(mesh index, primitive index) is true, without animations
    (unless animations), and without the data that nothing uses. A mesh without primitives leaves its nodes."""
    glb = Glb(data)
    gltf = json.loads(json.dumps(glb.json))
    for mesh_index, mesh in enumerate(gltf.get("meshes", [])):
        mesh["primitives"] = [p for i, p in enumerate(mesh.get("primitives", [])) if keep(mesh_index, i)]
    empty = {i for i, m in enumerate(gltf.get("meshes", [])) if not m["primitives"]}
    if empty:
        mesh_map = {old: new for new, old in enumerate(i for i in range(len(gltf["meshes"])) if i not in empty)}
        gltf["meshes"] = [m for i, m in enumerate(gltf["meshes"]) if i not in empty]
        for node in gltf.get("nodes", []):
            if "mesh" in node:
                if node["mesh"] in empty:
                    node.pop("mesh"); node.pop("skin", None)
                else:
                    node["mesh"] = mesh_map[node["mesh"]]
    if not animations: gltf.pop("animations", None)
    gltf, binary = compact(gltf, glb.binary)
    return write_glb(gltf, binary)


class Library:
    """Read a library package (zip or folder) for the character tools."""

    def __init__(self, path):
        report = Report()
        self.source = Source(Path(path), report)
        if report.errors or "manifest.json" not in self.source.set: raise ValueError(f"{path}: not a library package ({'; '.join(report.errors) or 'no manifest.json'})")
        self.path = Path(path)
        self.manifest = json.loads(self.source.read("manifest.json"))
        self._json = {}

    def json(self, name):
        if name not in self._json:
            self._json[name] = json.loads(self.source.read(name)) if name in self.source.set else None
        return self._json[name]

    def model(self, role):
        name = f"band/{role}/model.glb"
        if name not in self.source.set: raise ValueError(f"the library has no {name}")
        return self.source.read(name)

    def member(self, role):
        return next((m for m in (self.json("band/band.json") or {}).get("members", []) if m["role"] == role), {})

    def clip(self, role, clip):
        return self.source.read(f"direction/{role}/{clip}.q48.br")

    def has_clip(self, role, clip):
        return f"direction/{role}/{clip}.q48.br" in self.source.set


class CharacterLibrary:
    """Read a character library package (format chlib/1.1, PACKAGE-FORMAT 10.1) for the character tools."""

    def __init__(self, path):
        report = Report()
        self.source = Source(Path(path), report)
        if report.errors or "manifest.json" not in self.source.set:
            raise ValueError(f"{path}: not a library package ({'; '.join(report.errors) or 'no manifest.json'})")
        self.path = Path(path)
        self.manifest = json.loads(self.source.read("manifest.json"))
        entry = self.manifest.get("entry", {}).get("characters")
        if entry not in self.source.set: raise ValueError(f"{path}: not a character library (no 'characters' entry)")
        self.data = json.loads(self.source.read(entry))
        self.id = self.data.get("library") or self.manifest.get("id", "")
        self._rigs = {}

    @property
    def profiles(self):
        return self.data.get("profiles", {})

    def role(self, profile):
        return self.profiles[profile]["role"]

    def label(self, profile):
        row = self.profiles[profile]
        return f"{row.get('name', profile)} (outfit {row['outfit']})" if row.get("outfit") else row.get("name", profile)

    def rig(self, profile):
        if profile not in self._rigs:
            row = self.profiles[profile]
            self._rigs[profile] = Rig.from_bind(row["bones"], row["parents"], row["bind"])
        return self._rigs[profile]

    def hair(self, profile):
        hair = self.profiles[profile].get("hair")
        return (self.data.get("hair") or {}).get(hair, {}).get("joints", []) if hair else []

    def hair_joints(self, profile):
        return [joint["bone"] for joint in self.hair(profile)]

    def hair_chains(self, profile):
        return joint_chains(self.hair(profile), self.rig(profile))

    def animation_set(self, profile, animations=""):
        return self.data["animations"][animations or self.profiles[profile]["animations"]]

    def clip_items(self, profile, animations=""):
        """(clip ID, label, duration, frame count) of the clips of the animation set of a profile (see clip_items)."""
        role = self.role(profile)
        return clip_items({"members": {role: self.animation_set(profile, animations)}}, None, role)

    def clip(self, clip):
        return self.source.read(f"clips/{clip}.q48.br")

    def has_clip(self, clip):
        return f"clips/{clip}.q48.br" in self.source.set

    def clip_offsets(self, profile):
        """The position offset of each bone for the clips (PACKAGE-FORMAT 10.1): the profile rest position minus the
        clipRig rest position of the bone with the same name (zero without one), runtime coordinates (CharacterMotion.RestOffsets)."""
        row, clip_rig = self.profiles[profile], self.data["clipRig"]
        index = {}
        for i, bone in enumerate(clip_rig["bones"]): index.setdefault(bone, i)
        bind, other = np.asarray(row["bind"], dtype=np.float64), np.asarray(clip_rig["bind"], dtype=np.float64)
        offsets = np.zeros((len(row["bones"]), 3))
        for i, bone in enumerate(row["bones"]):
            if bone in index: offsets[i] = bind[i, :3] - other[index[bone], :3]
        return offsets


def profile_packages(folder, profile):
    """The character packages (.chchar) in a folder whose character.json names the profile: the GH3 character packages
    (ID gh3.<name>.outfit<n>.style<m>) first, then the others, each sorted by file name. The first gives the body of a
    character library profile; a custom character on the same profile in the folder must not replace it."""
    found = []
    for path in sorted(Path(folder).glob("*.chchar")) if folder and Path(folder).is_dir() else []:
        report = Report()
        try:
            source = Source(path, report)
            manifest = json.loads(source.read("manifest.json"))
            row = json.loads(source.read(manifest["entry"]["character"]))
        except (OSError, ValueError, KeyError):
            continue
        if not report.errors and row.get("profile") == profile and manifest.get("entry", {}).get("model") in source.set:
            found.append((not str(manifest.get("id", "")).startswith("gh3."), path.name, path))
    return [path for _, _, path in sorted(found)]


def profile_model(path):
    """The model GLB bytes of a character package."""
    source = Source(Path(path), Report())
    return source.read(json.loads(source.read("manifest.json"))["entry"]["model"])


def split_model(data, hair):
    """The parts of a library band model: {'body': GLB bytes (body and hair), 'attachment': GLB bytes or None,
    'hairMaterials': material names of the hair primitives, 'kinds': primitive kinds}."""
    glb = Glb(data)
    rig = Rig(glb)
    kinds = primitive_kinds(glb, rig, hair)
    materials = glb.list("materials")
    hair_materials = sorted({materials[glb.list("meshes")[m]["primitives"][p]["material"]].get("name", "") for (m, p), kind in kinds.items()
                             if kind == "hair" and "material" in glb.list("meshes")[m]["primitives"][p]})
    attachment = None
    if any(kind == "attachment" for kind in kinds.values()):
        attachment = keep_primitives(data, lambda m, p: kinds[m, p] == "attachment")
    return {"body": keep_primitives(data, lambda m, p: kinds[m, p] != "attachment"), "attachment": attachment,
            "hairMaterials": hair_materials, "kinds": kinds}


# Clips.
def clip_items(catalog, band, role):
    """(clip ID, label, duration, frame count) of every library clip of a role, sorted by label. The label is the GH3
    clip name for the start clips of band.json, else the first action group of the clip (catalog.json groups)."""
    member = (catalog or {}).get("members", {}).get(role)
    if not member: return []
    names = {}
    for item in (band or {}).get("members", []):
        if item["role"] == role:
            for name in item.get("clips", []): names[f"{gh3_key(name):08x}"] = name
    groups = {}
    for group, clips in member.get("groups", {}).items():
        for clip in clips: groups.setdefault(clip, group)
    items = []
    for clip, info in member.get("clips", {}).items():
        label = names.get(clip) or (f"{groups[clip]} ({clip})" if clip in groups else clip)
        items.append((clip, label, float(info.get("duration", 0)), int(info.get("frames", 0))))
    return sorted(items, key=lambda item: (item[0] not in names, item[1]))


def clip_local_poses(decoded, rig, role=None, offsets=None):
    """Local joint translations (frames, joints, 3) and rotations (frames, joints, 4, [x, y, z, w]) in glTF coordinates
    of a decoded q48 clip (q48.decode). A channel that the clip does not animate keeps the rig bind pose. With the role
    of a guitarist, bassist, or drummer, the arm and leg IK and the twist bones of the game follow (GuitarRig).
    offsets: the position offsets of a character library profile (CharacterLibrary.clip_offsets), added to the
    position tracks."""
    header, positions, rotations = decoded
    if header["bones"] != len(rig.names): raise ValueError(f"the clip has {header['bones']} bones, the rig {len(rig.names)}")
    frames = header["frames"]
    # Clips and the game use Unity runtime coordinates: glTF negates z of positions and x, y of rotations (PACKAGE-FORMAT 5).
    flip_t, flip_q = np.array([1, 1, -1]), np.array([-1, -1, 1, 1])
    bind_t = rig.local[:, :3, 3] * flip_t
    bind_q = matrix_quaternions(rig.local[:, :3, :3]) * flip_q
    translations = np.repeat(bind_t[None], frames, 0)
    quaternions = np.repeat(bind_q[None], frames, 0)
    for track, (bone, mask) in enumerate(header["tracks"]):
        if mask & 1: translations[:, bone] = positions[:, track] + (offsets[bone] if offsets is not None else 0)
        if mask & 2: quaternions[:, bone] = rotations[:, track]
    target = IK_TARGETS.get(role)
    if target: GuitarRig(rig, target).apply(translations, quaternions)
    return translations * flip_t, quaternions * flip_q


# The game IK (src/Stage/Band/StageGuitarArms.cs StageGuitarRig, GH3 guitarist_static_tree and drummer_static_tree):
# Havok two-joint IK of the arms and legs, then hero_ConstraintBones. Unity runtime coordinates, quaternions [x, y, z, w].
IK_TARGETS = {"guitarist": "Bone_IK_Hand_Guitar", "bassist": "Bone_IK_Hand_Guitar", "drummer": "Bone_IK_Hand_Slave"}
COS_ARM, COS_LEG = (-0.97, 0.97), (-0.98999995, 0.97)
TWIST_CONSTRAINTS = (
    (4, "Bone_Twist_Wrist_L", "Bone_Palm_L", 0.5), (4, "Bone_Twist_Wrist_R", "Bone_Palm_R", 0.5),
    (3, "Bone_Twist_Bicep_Mid_R", "Bone_Bicep_R", 0.5), (3, "Bone_Twist_Bicep_Mid_L", "Bone_Bicep_L", 0.5),
    (3, "Bone_Twist_Bicep_Top_R", "Bone_Bicep_R", 1), (3, "Bone_Twist_Bicep_Top_L", "Bone_Bicep_L", 1),
    (3, "Bone_Twist_Thigh_R", "Bone_Thigh_R", 0.5), (3, "Bone_Twist_Thigh_L", "Bone_Thigh_L", 0.5),
    (1, "Bone_Split_Knee_R", "Bone_Knee_R", 0.5), (1, "Bone_Split_Knee_L", "Bone_Knee_L", 0.5),
    (2, "Bone_Split_Ass_R", "Bone_Twist_Thigh_R", 0.5), (2, "Bone_Split_Ass_L", "Bone_Twist_Thigh_L", 0.5),
    (1, "Bone_Split_Elbow_R", "Bone_Forearm_R", 0.5), (1, "Bone_Split_Elbow_L", "Bone_Forearm_L", 0.5),
)


def qmul(a, b):
    ax, ay, az, aw = np.moveaxis(a, -1, 0)
    bx, by, bz, bw = np.moveaxis(b, -1, 0)
    return np.stack([aw * bx + ax * bw + ay * bz - az * by, aw * by - ax * bz + ay * bw + az * bx,
                     aw * bz + ax * by - ay * bx + az * bw, aw * bw - ax * bx - ay * by - az * bz], -1)


def qconj(q):
    return q * [-1, -1, -1, 1]


def qnormalize(q):
    return q / np.linalg.norm(q, axis=-1, keepdims=True)


def qrotate(q, v):
    return qmul(qmul(q, np.concatenate([v, np.zeros(v.shape[:-1] + (1,))], -1)), qconj(q))[..., :3]


def qslerp(a, b, t):
    """System.Numerics Quaternion.Slerp: the shortest path, linear when the rotations are nearly equal."""
    dot = np.sum(a * b, -1, keepdims=True)
    sign = np.where(dot < 0, -1.0, 1.0)
    dot = np.abs(dot)
    angle = np.arccos(np.clip(dot, -1, 1))
    small = dot > 1 - 1e-6
    sin = np.where(small, 1, np.sin(angle))
    s1 = np.where(small, 1 - t, np.sin((1 - t) * angle) / sin)
    s2 = np.where(small, t, np.sin(t * angle) / sin) * sign
    return a * s1 + b * s2


def normalized(v):
    return v / np.maximum(np.linalg.norm(v, axis=-1, keepdims=True), 1e-30)


def roll(q, axis):
    """GH3 keeps one Euler angle of a ZYX decomposition and rebuilds a rotation about that axis only."""
    x, y, z, w = np.moveaxis(q, -1, 0)
    if axis == 0: angle = np.arctan2(2 * (y * z + w * x), w * w - x * x - y * y + z * z)
    elif axis == 1: angle = np.arcsin(np.clip(-2 * (x * z - y * w), -1, 1))
    else: angle = np.arctan2(2 * (x * y + z * w), w * w + x * x - y * y - z * z)
    result = np.zeros(q.shape)
    result[..., axis] = np.sin(angle / 2)
    result[..., 3] = np.cos(angle / 2)
    return result


def gh3_quaternion(q):
    """Unity (x, y, -z, w) is the mirrored conjugate of the GH3 quaternion; the map is its own inverse."""
    return q * [1, 1, -1, 1]


class GuitarRig:
    """The arm and leg IK and the twist bones that the game applies after the body clip (StageGuitarRig.Apply with
    full arm strength). The note-driven strum, fret, and finger animations are not part of it."""

    def __init__(self, rig, hand_target):
        index = {name.removeprefix("bone-").lower(): i for i, name in enumerate(rig.names)}

        def bone(name): return index.get(f"{gh3_key(name):08x}", -1)

        self.parents = rig.parents
        self.bind_q = matrix_quaternions(rig.local[:, :3, :3]) * [-1, -1, 1, 1]
        hinge_arm, hinge_leg = np.array([0, 0, -1.0]), np.array([0, 0, 1.0])
        chains = [(bone("Bone_Bicep_R"), bone("Bone_Forearm_R"), bone("Bone_Palm_R"), bone(hand_target + "_R"), hinge_arm, COS_ARM),
                  (bone("Bone_Bicep_L"), bone("Bone_Forearm_L"), bone("Bone_Palm_L"), bone(hand_target + "_L"), hinge_arm, COS_ARM),
                  (bone("Bone_Thigh_L"), bone("Bone_Knee_L"), bone("Bone_Ankle_L"), bone("Bone_IK_Foot_Slave_L"), hinge_leg, COS_LEG),
                  (bone("Bone_Thigh_R"), bone("Bone_Knee_R"), bone("Bone_Ankle_R"), bone("Bone_IK_Foot_Slave_R"), hinge_leg, COS_LEG)]
        self.chains = [c for c in chains if min(c[:4]) >= 0 and self.parents[c[1]] == c[0] and self.parents[c[2]] == c[1]]
        self.constraints = [(kind, bone(a), bone(b), amount) for kind, a, b, amount in TWIST_CONSTRAINTS if bone(a) >= 0 and bone(b) >= 0]
        self.order = sorted(range(len(self.parents)), key=self.depth)

    def depth(self, joint):
        depth = 0
        while self.parents[joint] >= 0: joint, depth = self.parents[joint], depth + 1
        return depth

    def apply(self, translations, quaternions):
        """Change the local poses (frames, joints, ...) in place (Unity runtime coordinates)."""
        self.t, self.q = translations, quaternions
        self.position = np.zeros_like(translations)
        self.rotation = np.zeros_like(quaternions)
        for joint in self.order: self.model(joint)
        for chain in self.chains: self.solve(*chain)
        for constraint in self.constraints: self.constrain(*constraint)

    def model(self, joint):
        parent = self.parents[joint]
        if parent < 0:
            self.position[:, joint], self.rotation[:, joint] = self.t[:, joint], self.q[:, joint]
            return
        self.position[:, joint] = self.position[:, parent] + qrotate(self.rotation[:, parent], self.t[:, joint])
        self.rotation[:, joint] = qnormalize(qmul(self.rotation[:, parent], self.q[:, joint]))

    def set_model_rotation(self, joint, rotation, frames):
        parent = self.parents[joint]
        local = rotation if parent < 0 else qmul(qconj(self.rotation[:, parent]), rotation)
        self.q[frames, joint] = qnormalize(local)[frames]
        self.model(joint)

    def solve(self, first, second, end, target, hinge, limits):
        a, b, c, goal = (self.position[:, j] for j in (first, second, end, target))
        upper, lower = np.linalg.norm(a - b, axis=-1), np.linalg.norm(b - c, axis=-1)
        frames = (upper >= 1e-5) & (lower >= 1e-5)
        reach = np.sum((a - goal) ** 2, -1)
        cosine = np.maximum(np.minimum((reach - upper ** 2 - lower ** 2) / np.where(frames, -2 * upper * lower, 1), limits[1]), limits[0])
        current = np.arccos(np.clip(np.sum(normalized(a - b) * normalized(c - b), -1), -1, 1))
        axis = normalized(qrotate(self.rotation[:, second], np.broadcast_to(hinge, a.shape)))
        half = (np.arccos(cosine) - current) / 2
        turn = np.concatenate([axis * np.sin(half)[:, None], np.cos(half)[:, None]], -1)
        self.set_model_rotation(second, qmul(turn, self.rotation[:, second]), frames)
        self.model(end)
        source, to = normalized(self.position[:, end] - a), normalized(goal - a)
        damped = np.sum(source * to, -1)
        turning = frames & (damped < 1)
        half = np.sqrt(np.maximum((damped + 1) * 0.5, 0))
        cross = np.cross(source, to) * (0.5 / np.maximum(half, 1e-12))[:, None]
        rotation = qnormalize(np.concatenate([cross, half[:, None]], -1))
        opposite = half < 1e-6
        if np.any(opposite & turning):
            other = np.where((np.abs(source[:, 0]) < 0.9)[:, None], [1.0, 0, 0], [0, 1.0, 0])
            axis = normalized(np.cross(source, other))
            rotation[opposite] = np.concatenate([axis, np.zeros((len(axis), 1))], -1)[opposite]
        self.set_model_rotation(first, qmul(rotation, self.rotation[:, first]), turning)
        self.model(second)
        self.model(end)
        self.set_model_rotation(end, self.rotation[:, target].copy(), frames)

    def constrain(self, kind, joint, target, amount):
        source = gh3_quaternion(self.q[:, target])
        if kind == 1:
            driven = source
        elif kind == 2:
            stop = self.parents[joint]
            bone = self.parents[target]
            while bone >= 0 and bone != stop:
                source = qmul(gh3_quaternion(self.q[:, bone]), source)
                bone = self.parents[bone]
            driven = qmul(np.broadcast_to([0, 1.0, 0, 0], source.shape), source)
        elif kind == 3:
            driven = roll(qmul(source, qconj(gh3_quaternion(np.broadcast_to(self.bind_q[target], source.shape)))), 0)
        else:
            axis = self.t[:, target] * [1, 1, -1]
            choice = np.where((axis[:, 0] >= axis[:, 1]) & (axis[:, 0] >= axis[:, 2]), 0, np.where((axis[:, 1] >= axis[:, 0]) & (axis[:, 1] >= axis[:, 2]), 1, 2))
            driven = np.choose(choice[:, None], [roll(source, 0), roll(source, 1), roll(source, 2)])
        identity = np.broadcast_to([0, 0, 0, 1.0], driven.shape)
        result = qmul(np.broadcast_to(gh3_quaternion(self.bind_q[joint]), driven.shape), qslerp(identity, driven, amount))
        self.q[:, joint] = qnormalize(gh3_quaternion(result))


def matrix_quaternions(matrices):
    """[x, y, z, w] quaternions (w >= 0 where possible) of rotation matrices [..., 3, 3]."""
    m = np.asarray(matrices, dtype=np.float64)
    shape = m.shape[:-2]
    m = m.reshape(-1, 3, 3)
    q = np.empty((len(m), 4))
    trace = m[:, 0, 0] + m[:, 1, 1] + m[:, 2, 2]
    cases = [trace > 0, (m[:, 0, 0] >= m[:, 1, 1]) & (m[:, 0, 0] >= m[:, 2, 2]), m[:, 1, 1] >= m[:, 2, 2], np.ones(len(m), bool)]
    done = np.zeros(len(m), bool)
    for case, select in enumerate(cases):
        rows = select & ~done
        done |= rows
        r = m[rows]
        if case == 0:
            s = np.sqrt(trace[rows] + 1) * 2
            q[rows] = np.stack([(r[:, 2, 1] - r[:, 1, 2]) / s, (r[:, 0, 2] - r[:, 2, 0]) / s, (r[:, 1, 0] - r[:, 0, 1]) / s, s / 4], -1)
            continue
        i = case - 1; j = (i + 1) % 3; k = (i + 2) % 3
        s = np.sqrt(np.maximum(0, 1 + r[:, i, i] - r[:, j, j] - r[:, k, k])) * 2
        values = np.empty((len(r), 4))
        values[:, i] = s / 4
        values[:, j] = (r[:, j, i] + r[:, i, j]) / s
        values[:, k] = (r[:, k, i] + r[:, i, k]) / s
        values[:, 3] = (r[:, k, j] - r[:, j, k]) / s
        q[rows] = values
    q /= np.linalg.norm(q, axis=-1, keepdims=True)
    return q.reshape(*shape, 4)


def quaternion_matrices(q):
    """3x3 rotation matrices of quaternions [..., 4] ([x, y, z, w])."""
    x, y, z, w = (q[..., i] for i in range(4))
    return np.stack([np.stack([1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)], -1),
                     np.stack([2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)], -1),
                     np.stack([2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)], -1)], -2)


def world_poses(rig, translations, quaternions):
    """World matrices (frames, joints, 4, 4) of local poses (glTF coordinates, relative to the rig root)."""
    frames, joints = translations.shape[:2]
    local = np.zeros((frames, joints, 4, 4))
    local[..., :3, :3] = quaternion_matrices(quaternions / np.linalg.norm(quaternions, axis=-1, keepdims=True))
    local[..., :3, 3] = translations
    local[..., 3, 3] = 1
    world = np.zeros_like(local)
    for joint, parent in enumerate(rig.parents):
        world[:, joint] = local[:, joint] if parent < 0 else world[:, parent] @ local[:, joint]
    return world


# Weights.
def limit_weights(weights, limit=MAX_INFLUENCES):
    """Dense vertex weights (vertices, groups): keep the largest `limit` weights of each vertex and make their sum 1.
    Vertices without weight stay zero."""
    weights = np.clip(np.asarray(weights, dtype=np.float64), 0, None)
    if weights.shape[1] > limit:
        order = np.argsort(-weights, axis=1, kind="stable")
        mask = np.zeros_like(weights, dtype=bool)
        np.put_along_axis(mask, order[:, :limit], True, 1)
        weights = np.where(mask, weights, 0)
    total = weights.sum(1, keepdims=True)
    return np.divide(weights, total, out=np.zeros_like(weights), where=total > 0)


# character.json.
def character_json(profile, hair_materials=(), instrument="", library=LIBRARY_ID, animations=""):
    value = {"version": 1, "library": library or LIBRARY_ID, "profile": profile}
    if animations: value["animations"] = animations
    if hair_materials: value["hairMaterials"] = list(hair_materials)
    if instrument: value["attachments"] = {"instrument": instrument}
    return value


def open_character_json(value):
    return {"profile": value.get("profile", ""), "library": value.get("library", LIBRARY_ID), "animations": value.get("animations", ""),
            "hairMaterials": list(value.get("hairMaterials", [])), "instrument": value.get("attachments", {}).get("instrument", "")}


def package_format(library=LIBRARY_ID):
    """The manifest format of a character: chchar/1.0 on a fixed profile, chchar/1.1 on a character library profile."""
    return FORMAT if (library or LIBRARY_ID) == LIBRARY_ID else FORMAT_1_1


def version_tuple(text):
    try:
        return tuple(int(part) for part in str(text).split("."))
    except ValueError:
        return ()


def complete_requirements(description, library=LIBRARY_ID):
    """Add the libraries that a character needs to the 'requires' of a package description, and raise its
    'minModVersion' to the first mod that reads format 1.1 (character library profiles). Returns the description."""
    requires = description.setdefault("requires", [])
    needed = [LIBRARY_ID] + ([library] if library and library != LIBRARY_ID else [])
    for package in needed:
        if not any(row.get("id") == package for row in requires): requires.append({"id": package, "version": decisions.LIBRARY_VERSION})
    if package_format(library) == FORMAT_1_1 and version_tuple(description.get("minModVersion") or "0") < version_tuple(decisions.FORMAT_1_1_MOD_VERSION):
        description["minModVersion"] = decisions.FORMAT_1_1_MOD_VERSION
    return description


# Library band member as a .chchar package.
def member_files(library, role):
    """The package files {path: bytes} of a library band member as a character: the body (and hair) of the band
    model without the attachment and the clips, and character.json."""
    hair = hair_joints(library.json("band/hair.json"), role)
    parts = split_model(library.model(role), hair)
    member = library.member(role)
    files = {MODEL_FILE: parts["body"], CHARACTER_FILE: container.json_bytes(character_json(ROLE_PROFILES[role], parts["hairMaterials"]))}
    manifest = {"format": FORMAT, "id": f"gh3.{role}", "version": decisions.PACKAGE_VERSION,
                "name": f"GH3 {role} ({member.get('model', role)})", "authors": [{"name": "Neversoft", "role": "original character"}],
                "license": decisions.GH3_LICENSE, "permissions": dict(decisions.GH3_PERMISSIONS), "credits": [decisions.CONVERTER_CREDIT],
                "requires": [{"id": LIBRARY_ID, "version": decisions.LIBRARY_VERSION}],
                "entry": {"model": MODEL_FILE, "character": CHARACTER_FILE}, "files": {}}
    return manifest, files


def write_member(library, role, output):
    """Write gh3.<role>.chchar (zip) and unpacked/gh3.<role>.chchar (folder) into output. Returns the zip path."""
    manifest, files = member_files(library, role)
    folder = Path(output) / "unpacked" / f"gh3.{role}.chchar"
    folder.mkdir(parents=True, exist_ok=True)
    for name, data in files.items(): (folder / name).write_bytes(data)
    manifest["files"] = container.file_list(folder, files)
    (folder / "manifest.json").write_bytes(container.json_bytes(manifest))
    target = Path(output) / f"gh3.{role}.chchar"
    container.write_zip(folder, target)
    return target


def main(argv=None):
    parser = argparse.ArgumentParser(prog="python -m pipeline.package.character", description="Write library band members as character packages.")
    parser.add_argument("--library", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--role", action="append", choices=sorted(ROLE_PROFILES))
    args = parser.parse_args(argv)
    library = Library(args.library)
    for role in args.role or sorted(ROLE_PROFILES):
        print(write_member(library, role, args.output))
    return 0


if __name__ == "__main__":
    sys.exit(main())
