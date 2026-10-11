"""Validator checks of character (.chchar) and instrument (.chinst) packages: skeleton, profile, chains, highway."""

import math
import re

import numpy as np

from .checks import HAIR_FRAME_FIELDS, JOINT_POSITION, JOINT_ROTATION, UNIT_SCALE, png_size
from .glb import decompose, quaternion_angle
from .profiles import (
    LIBRARY_ID,
    PROFILES,
    bone_key,
    character_profile,
    gh3_key,
    library_instruments,
    load_character_library,
    load_profile,
)

# Custom chains of a character (PACKAGE-FORMAT.md section 9.5): the runtime solver steps every joint 120 times a second.
MAX_CHAIN_JOINTS = 64
CHAIN_JOINTS_WARNING = 32
CHAIN_ALIGNMENT = 0.002
# GH3 draws the highway texture 512 x 1024 (one repeat is 1024 rows); other sizes stretch.
HIGHWAY_SIZE = (512, 1024)


class CharacterChecks:
    """Character and instrument checks of the Validator; they use its report, source, and GLB helpers."""

    def check_character(self):
        entry = self.manifest["entry"]
        character = self.json_entry("character", "character.schema.json")
        glb = self.check_glb(entry["model"], skinned=True)
        if character is None or glb is None:
            return
        own = None
        if "animations" in character and character["profile"] in PROFILES:
            self.report.error(entry["character"], "'animations' needs a profile of a character library")
        if character["profile"] not in PROFILES:
            if character["library"] == LIBRARY_ID:
                self.report.error(entry["character"], f"the band library has no profile '{character['profile']}'")
            elif not any(r["id"] == character["library"] for r in self.manifest.get("requires", [])):
                self.report.error("manifest.json", f"'requires' must list the character library {character['library']}")
            if self.character_library is not None:
                try:
                    library = load_character_library(self.character_library)
                except (OSError, ValueError) as error:
                    self.report.error("--character-library", f"cannot read the character library: {error}")
                    return
                if library.get("library") != character["library"]:
                    self.report.warn(
                        entry["character"],
                        f"the profile is not checked: --character-library is {library.get('library')}, not {character['library']}",
                    )
                elif character["profile"] not in library["profiles"]:
                    self.report.error(
                        entry["character"], f"library {character['library']} has no profile '{character['profile']}'"
                    )
                else:
                    own = character_profile(library, character["profile"])
                    if "animations" in character and character["animations"] not in library["animations"]:
                        self.report.error(
                            entry["character"],
                            f"library {character['library']} has no animation set '{character['animations']}'",
                        )
        if "highway" in character:
            self.check_highway(entry["character"], character["highway"])
        materials = {m.get("name") for m in glb.list("materials")}
        for material in character.get("hairMaterials", []):
            if material not in materials:
                self.report.error(entry["character"], f"hair material '{material}' is not in the GLB")
        if glb.list("animations"):
            self.report.warn(entry["model"], "character animations are ignored; the profile supplies the clips")
        skin = self.check_skin(entry["model"], glb)
        if skin is None:
            return
        joints, worlds, joint_parents, parents, cache = skin
        names = []
        for joint in joints:
            label = glb.list("nodes")[joint].get("name", "")
            match = re.fullmatch(r"bone-([0-9a-f]{8})", label)
            names.append(match.group(1) if match else f"{gh3_key(label):08x}" if label else "")
        if "" in names or len(set(names)) != len(names):
            self.report.error(entry["model"], "joint names must be unique GH3 bone names")
            return
        root = [j for j, p in zip(joints, joint_parents) if p < 0]
        for joint in root:
            if joint in parents:
                above = glb.world(parents[joint], parents, cache)
                if np.abs(above - np.eye(4)).max() > JOINT_POSITION:
                    self.report.error(entry["model"], "nodes above the root joint must have the identity transform")
        if "skeleton" in character:
            self.check_own_skeleton(entry, character, glb)
            return
        if "face" in character:
            self.report.error(
                entry["character"], "'face' needs 'skeleton': characters on a GH3 rig move the face with bones"
            )
        if own is None and character["profile"] in PROFILES and self.library is not None:
            try:
                own = load_profile(self.library, PROFILES[character["profile"]])
            except (OSError, ValueError) as error:
                self.report.error("--library", f"cannot read the profile rig: {error}")
                return
        if own is None:
            added = [bone_key(b["name"]) for b in character.get("bones", [])]
            unknown = [n for n in names if n not in added]
            self.check_chains(entry["character"], character, (unknown, None, None, None))
            self.report.warn(
                entry["model"], "joints are not checked against the profile (no --library or --character-library)"
            )
            return
        rig = self.check_chains(entry["character"], character, own)
        if rig is None:
            return
        self.check_profile(entry["model"], glb, joints, names, joint_parents, rig)
        if self.library is None:
            return
        instrument = character.get("attachments", {}).get("instrument")
        known = library_instruments(self.library)
        if instrument and known and instrument not in known:
            self.report.warn(
                entry["character"],
                f"attachments.instrument '{instrument}' is not a library instrument ({', '.join(sorted(known))}); "
                "the game uses the instrument of the band role",
            )

    def check_own_skeleton(self, entry, character, glb):
        """character.json skeleton and face (PACKAGE-FORMAT.md section 9.6): the GLB joints are the skeleton."""
        from . import skeleton

        where = entry["character"]
        if "bones" in character:
            self.report.error(
                where, "a character with its own skeleton has no 'bones': its chain bones are skeleton joints"
            )
        names, parents, bind, local = skeleton.glb_skeleton(glb)
        scale = np.linalg.norm(local[:, :3, :3], axis=1)
        if np.abs(scale - 1).max() > UNIT_SCALE:
            self.report.error(
                entry["model"],
                f"joint scale differs from 1 by {np.abs(scale - 1).max():.3g}; apply the scale before export",
            )
        if len(set(names)) != len(names) or "" in names:
            self.report.error(entry["model"], "skeleton joints need unique names")
            return
        own = character["skeleton"]
        errors, warnings, chain_bones = skeleton.check_map(
            names, parents, bind, own["humanoid"], own.get("followers", []), own.get("instrument")
        )
        for message in errors:
            self.report.error(where, message)
        for message in warnings:
            self.report.warn(where, message)
        morphs = skeleton.morph_names(glb)
        for slot, name in character.get("face", {}).items():
            if name not in morphs:
                self.report.error(where, f"face.{slot}: no mesh has the morph target '{name}'")
        if errors:
            return
        keys = [name[5:] if re.fullmatch(r"bone-[0-9a-f]{8}", name) else f"{gh3_key(name):08x}" for name in names]
        local_glb = np.array(local)
        translations = local_glb[:, :3, 3]
        rotations = np.array([decompose(m)[1] for m in local_glb])
        self.check_chains(
            where, character, (keys, parents, translations, rotations), chain_bones={bone_key(n) for n in chain_bones}
        )

    def check_chains(self, where, character, profile, chain_bones=None):
        """character.json bones, colliders, and chains (PACKAGE-FORMAT.md section 9.5). Returns the profile rig with the
        added bones (load_profile layout), or None after an error. Without profile parents (no library), only the
        references are checked."""
        profile_names, profile_parents, translations, rotations = profile
        error_count = len(self.report.errors)
        names, parents = list(profile_names), list(profile_parents or [0] * len(profile_names))
        index = {n: i for i, n in enumerate(names)}
        added = []
        for bone in character.get("bones", []):
            key, parent = bone_key(bone["name"]), bone_key(bone["parent"])
            if key in index:
                self.report.error(where, f"bone '{bone['name']}' has the name of a profile bone or of another bone")
                continue
            if parent not in index:
                self.report.error(
                    where,
                    f"bone '{bone['name']}': the parent '{bone['parent']}' is not a bone of the rig (an added parent comes first)",
                )
                continue
            bind = bone["bind"]
            if not all(math.isfinite(v) for v in bind) or abs(math.sqrt(sum(v * v for v in bind[3:])) - 1) > 0.001:
                self.report.error(where, f"bone '{bone['name']}': the bind rotation must have unit length")
            if math.sqrt(sum(v * v for v in bind[:3])) > 5:
                self.report.error(where, f"bone '{bone['name']}' is more than 5 m from its parent")
            index[key] = len(names)
            names.append(key)
            parents.append(index[parent])
            added.append(bone)
        added_keys = {bone_key(b["name"]) for b in added} if chain_bones is None else chain_bones
        joint_bones = [bone_key(j["bone"]) for chain in character.get("chains", []) for j in chain["joints"]]
        colliders = {}
        for collider in character.get("colliders", []):
            key = bone_key(collider["bone"])
            if collider["name"] in colliders:
                self.report.error(where, f"two colliders have the name '{collider['name']}'")
            colliders[collider["name"]] = collider
            if key not in index:
                self.report.error(
                    where, f"collider '{collider['name']}': '{collider['bone']}' is not a bone of the rig"
                )
            elif key in joint_bones:
                self.report.error(
                    where,
                    f"collider '{collider['name']}' is on the chain bone '{collider['bone']}'; put colliders on body bones",
                )
            if any(math.sqrt(sum(v * v for v in collider.get(f, [0, 0, 0]))) > 10 for f in ("a", "b")):
                self.report.error(where, f"collider '{collider['name']}' is more than 10 m from its bone")
        if len(joint_bones) > MAX_CHAIN_JOINTS:
            self.report.error(where, f"the chains have {len(joint_bones)} joints (limit {MAX_CHAIN_JOINTS})")
        elif len(joint_bones) > CHAIN_JOINTS_WARNING:
            self.report.warn(
                where,
                f"the chains have {len(joint_bones)} joints; more than {CHAIN_JOINTS_WARNING} cost frame time on slow computers",
            )
        seen, chain_names, rows = set(), set(), {}
        for chain in character.get("chains", []):
            if chain["name"] in chain_names:
                self.report.error(where, f"two chains have the name '{chain['name']}'")
            chain_names.add(chain["name"])
            for name in chain.get("colliders", []):
                if name not in colliders:
                    self.report.error(where, f"chain '{chain['name']}': no collider '{name}'")
            for joint in chain["joints"]:
                label = f"chain '{chain['name']}' joint '{joint['bone']}'"
                key = bone_key(joint["bone"])
                if key not in added_keys:
                    self.report.error(
                        where,
                        f"{label}: the joint bone must be a bone of 'bones'"
                        if chain_bones is None
                        else f"{label}: the joint bone must be a skeleton joint without a humanoid role, not a parent of one, and not a follower",
                    )
                    continue
                if key in seen:
                    self.report.error(where, f"{label}: the bone is a joint of an earlier chain or joint")
                    continue
                parent = bone_key(joint["parent"]) if "parent" in joint else names[parents[index[key]]]
                if parent not in index or parent == key:
                    self.report.error(
                        where, f"{label}: the parent '{joint.get('parent')}' is not another bone of the rig"
                    )
                elif parent in joint_bones and parent not in seen:
                    self.report.error(where, f"{label}: the parent joint must come before it")
                seen.add(key)
                rows[key] = (joint, parent)
                self.check_chain_joint(where, label, joint)
        bind = {bone_key(b["name"]): b["bind"] for b in added}
        if chain_bones is not None and translations is not None:
            bind.update({name: list(np.asarray(translations[i]) * [1, 1, -1]) for i, name in enumerate(profile_names)})
        for key, (joint, parent) in rows.items():
            if parent not in rows:
                continue
            upper = rows[parent][0]
            direction = np.array(upper.get("direction", [0, 1, 0]), dtype=float)
            if (
                np.linalg.norm(
                    np.array(bind[key][:3]) - direction / max(np.linalg.norm(direction), 1e-9) * upper["length"]
                )
                > CHAIN_ALIGNMENT
                and parent in bind
                and parents[index[key]] == index[parent]
            ):
                self.report.warn(
                    where,
                    f"joint '{joint['bone']}' does not start at the end of joint '{upper['bone']}' (length and direction): the two joints do not pull on each other",
                )
        if len(self.report.errors) > error_count or profile_parents is None:
            return None
        if not added:
            return profile
        # The bind is in runtime coordinates; the GLB check uses glTF coordinates (negate z, and x and y of the rotation).
        bind = np.array([b["bind"] for b in added], dtype=np.float64)
        return (
            names,
            parents,
            np.vstack([translations, bind[:, :3] * [1, 1, -1]]),
            np.vstack([rotations, bind[:, 3:] * [-1, -1, 1, 1]]),
        )

    def check_chain_joint(self, where, label, joint):
        def unit(field):
            vector = joint.get(field)
            if vector is not None and abs(math.sqrt(sum(v * v for v in vector)) - 1) > 0.01:
                self.report.error(where, f"{label}: '{field}' must have unit length")

        for field in ("direction", "axis", *HAIR_FRAME_FIELDS):
            unit(field)
        frames = [field for field in HAIR_FRAME_FIELDS if field in joint]
        if joint.get("type", "ragdoll") == "hinge":
            if frames or "limits" in joint:
                self.report.error(where, f"{label}: a hinge has 'axis' and 'range', not the ragdoll limits")
            low, high = joint["range"]
            if not -math.pi <= low <= high <= math.pi:
                self.report.error(where, f"{label}: the hinge range must be in -pi to pi, minimum first")
            return
        if "axis" in joint or "range" in joint:
            self.report.error(where, f"{label}: 'axis' and 'range' are for hinge joints")
        if frames and len(frames) != len(HAIR_FRAME_FIELDS):
            self.report.error(where, f"{label}: twistAxis, coneAxis, and planeAxis come together")
        cone = joint["limits"]["coneLimit"][1]
        if not 0 < cone <= math.pi / 2:
            self.report.error(
                where, f"{label}: the cone limit (coneLimit maximum) must be more than 0 and at most pi/2"
            )
        planes = joint["limits"].get("planesLimit")
        if frames and planes is None:
            self.report.error(where, f"{label}: the limit axes need planesLimit")
        if planes is not None and not -math.pi / 2 <= planes[0] <= planes[1] <= math.pi / 2:
            self.report.error(where, f"{label}: planesLimit must be in -pi/2 to pi/2, minimum first")

    def check_highway(self, where, name):
        if name not in self.source.set:
            self.report.error(where, f"highway file is missing: {name}")
            return
        size = png_size(self.source.read(name))
        if size is not None and size != HIGHWAY_SIZE:
            self.report.warn(
                name,
                f"the highway is {size[0]} x {size[1]} pixels; GH3 highways are {HIGHWAY_SIZE[0]} x {HIGHWAY_SIZE[1]} (other sizes stretch)",
            )

    # Instrument.
    def check_instrument(self):
        entry = self.manifest["entry"]
        instrument = self.json_entry("instrument", "instrument.schema.json")
        glb = self.check_glb(entry["model"], skinned=True)
        if instrument is None or glb is None:
            return
        if "highway" in instrument:
            self.check_highway(entry["instrument"], instrument["highway"])
        if glb.list("animations"):
            self.report.warn(entry["model"], "instrument animations are ignored")
        skin = self.check_skin(entry["model"], glb)
        if skin is None:
            return
        joints, worlds, joint_parents, parents, cache = skin
        names = self.joint_names(entry["model"], glb, joints)
        if names is None:
            return
        # As BandAttachments: the body is the subtree of the parent of bone_pelvis; an instrument follows the other joints.
        pelvis = names.index(f"{gh3_key('bone_pelvis'):08x}") if f"{gh3_key('bone_pelvis'):08x}" in names else -1
        root = joint_parents[pelvis] if pelvis >= 0 and joint_parents[pelvis] >= 0 else -1
        body, stack = set(), [root] if root >= 0 else []
        while stack:
            joint = stack.pop()
            if joint in body:
                continue
            body.add(joint)
            stack.extend(i for i, p in enumerate(joint_parents) if p == joint)
        for mesh_index, mesh in enumerate(glb.list("meshes")):
            for index, primitive in enumerate(mesh.get("primitives", [])):
                attributes = primitive.get("attributes", {})
                if "JOINTS_0" not in attributes or "WEIGHTS_0" not in attributes:
                    continue
                dominant = glb.accessor(attributes["JOINTS_0"]).astype(int)
                dominant = dominant[np.arange(len(dominant)), glb.accessor(attributes["WEIGHTS_0"]).argmax(1)]
                if np.mean([j in body for j in dominant]) >= 0.5:
                    self.report.error(
                        entry["model"],
                        f"mesh {mesh_index} primitive {index} follows the body joints; weight the instrument to bone_guitar_body",
                    )
        if self.library is None:
            self.report.warn(entry["model"], "joints are not checked against the profile (no --library)")
            return
        try:
            profile = load_profile(self.library, PROFILES[instrument["profile"]])
        except (OSError, ValueError) as error:
            self.report.error("--library", f"cannot read the profile rig: {error}")
            return
        self.check_profile(entry["model"], glb, joints, names, joint_parents, profile)

    def joint_names(self, name, glb, joints):
        """GH3 checksums of the joints ('bone-<checksum>' or a GH3 bone name), or None after an error."""
        names = []
        for joint in joints:
            label = glb.list("nodes")[joint].get("name", "")
            match = re.fullmatch(r"bone-([0-9a-f]{8})", label)
            names.append(match.group(1) if match else f"{gh3_key(label):08x}" if label else "")
        if "" in names or len(set(names)) != len(names):
            self.report.error(name, "joint names must be unique GH3 bone names")
            return None
        return names

    def check_profile(self, name, glb, joints, names, joint_parents, profile):
        profile_names, profile_parents, translations, rotations = profile
        index = {n: i for i, n in enumerate(names)}
        missing = [n for n in profile_names if n not in index]
        extra = [n for n in names if n not in profile_names]
        if missing:
            self.report.error(name, f"{len(missing)} profile joints are missing, for example bone-{missing[0]}")
        if extra:
            self.report.error(
                name,
                f"{len(extra)} joints are not in the profile or in character.json bones, for example bone-{extra[0]}",
            )
        if missing or extra:
            return
        worst = [0.0, 0.0, 0.0]
        for p, bone in enumerate(profile_names):
            i = index[bone]
            parent = joint_parents[i]
            expected = profile_parents[p]
            if (parent < 0) != (expected < 0) or parent >= 0 and names[parent] != profile_names[expected]:
                self.report.error(name, f"joint bone-{bone} has another parent than the profile")
                continue
            local = glb.node_matrix(glb.list("nodes")[joints[i]])
            translation, rotation, scale = decompose(local)
            worst[0] = max(worst[0], float(np.abs(translation - translations[p]).max()))
            worst[1] = max(worst[1], quaternion_angle(rotation, rotations[p]))
            worst[2] = max(worst[2], float(np.abs(scale - 1).max()))
        if worst[0] > JOINT_POSITION:
            self.report.error(
                name, f"joint positions differ from the profile by {worst[0] * 1000:.3g} mm (limit 0.1 mm)"
            )
        if worst[1] > JOINT_ROTATION:
            self.report.error(
                name,
                f"joint rotations differ from the profile by {worst[1]:.3g} degrees (limit 0.01); check the export options: Blender bone direction, +Y up, rest position",
            )
        if worst[2] > UNIT_SCALE:
            self.report.error(
                name, f"joint scale differs from 1 by {worst[2]:.3g} (limit {UNIT_SCALE}); apply scale before export"
            )
