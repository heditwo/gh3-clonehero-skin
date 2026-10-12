"""Validator checks of library packages (.chlib): the band library and the character library."""

import math
import re

from . import q48
from .checks import HAIR_FRAME_FIELDS, check_schema
from .package_files import load_json
from .profiles import CHARACTER_LIBRARY_ID, LIBRARY_ID, PROFILE_ID, PROFILES

LIBRARY_SCHEMAS = {
    "catalog": "catalog.schema.json",
    "band": "band.schema.json",
    "performance": "performance.schema.json",
    "hair": "hair.schema.json",
    "crowd": "crowd.schema.json",
    "handFire": "hand-fire.schema.json",
    "audio": "audio.schema.json",
    "scripts": "scripts.schema.json",
}
EFFECT_SCHEMAS = {
    "effects/particle-materials.json": "particle-materials.schema.json",
    "effects/tesla-material.json": "tesla-material.schema.json",
}


class LibraryChecks:
    """Library checks of the Validator; they use its report, source, and GLB helpers."""

    def check_library(self):
        entry = self.manifest["entry"]
        if "characters" in entry:
            if self.manifest["id"] != CHARACTER_LIBRARY_ID:
                self.report.warn("manifest.json", f"the mod uses the character library {CHARACTER_LIBRARY_ID}")
            if any(key in entry for key in LIBRARY_SCHEMAS):
                self.report.error("manifest.json", "a character library has only the entry 'characters'")
            self.check_character_library(entry["characters"])
        elif self.manifest["id"] != LIBRARY_ID:
            self.report.warn("manifest.json", f"the mod v1 uses the library {LIBRARY_ID}")
        data = {key: self.json_entry(key, schema) for key, schema in LIBRARY_SCHEMAS.items() if key in entry}
        if data.get("scripts"):
            self.check_script_files(entry["scripts"], data["scripts"])
        for role, member in (data.get("hair") or {}).get("members", {}).items():
            self.check_hair_joints(f"{entry['hair']}: {role}", member["joints"])
        self.check_library_files(entry, data)
        try:
            import brotli
        except ImportError:
            brotli = None
            self.report.warn("library", "the Brotli module is not installed: clip files are not decoded")
        for name in self.source.names:
            if name.endswith(".q48.br") and brotli is not None:
                try:
                    q48.decode(brotli.decompress(self.source.read(name)))
                except (ValueError, brotli.error) as error:
                    self.report.error(name, f"invalid clip: {error}")
            elif name.endswith(".glb"):
                glb = self.check_glb(name, skinned=True)
                if glb is not None and glb.list("skins"):
                    self.check_skin(name, glb)

    def check_character_library(self, name):  # noqa: C901
        """characters.json (PACKAGE-FORMAT.md section 10.1): references between profiles, sets, hair, IK, and clip files."""
        library = self.json_entry("characters", "characters.schema.json")
        if library is None:
            return
        rigs = {None: library["clipRig"], **library.get("clipRigs", {})}
        for rig_id, rig in rigs.items():
            if not len(rig["bones"]) == len(rig["parents"]) == len(rig["bind"]):
                self.report.error(
                    name, f"{'clipRigs ' + rig_id if rig_id else 'clipRig'}: bones, parents, and bind differ in length"
                )
        for set_name, animations in library["animations"].items():
            if animations.get("clipRig") not in rigs:
                self.report.error(name, f"animation set {set_name}: no clip rig {animations['clipRig']} in clipRigs")
            for table in ("groups", "layers"):
                for field, clips in animations[table].items():
                    for clip in clips:
                        if clip not in animations["clips"]:
                            self.report.error(
                                name,
                                f"animation set {set_name}: {table} {field} names clip {clip}, which is not in its clips",
                            )
            for clip in animations["clips"]:
                if f"clips/{clip}.q48.br" not in self.source.set:
                    self.report.error(name, f"animation set {set_name}: clip file is missing: clips/{clip}.q48.br")
        for profile_id, profile in library["profiles"].items():
            where = f"{name}: profile {profile_id}"
            if not PROFILE_ID.fullmatch(profile_id):
                self.report.error(where, "invalid profile ID")
            if profile_id in PROFILES:
                self.report.error(where, "the band library has a profile with this ID")
            if profile["animations"] not in library["animations"]:
                self.report.error(where, f"no animation set {profile['animations']}")
            if profile["hair"] is not None and profile["hair"] not in library["hair"]:
                self.report.error(where, f"no hair {profile['hair']}")
            if profile["ik"] not in library["ik"]:
                self.report.error(where, f"no IK chains {profile['ik']}")
            animations = library["animations"].get(profile["animations"], {})
            rig = rigs.get(animations.get("clipRig"))
            if rig is not None and profile["bones"] != rig["bones"]:
                self.report.error(where, "the bones differ from the clip rig of its animation set (names and order)")
            if not len(profile["bones"]) == len(profile["parents"]) == len(profile["bind"]):
                self.report.error(where, "bones, parents, and bind differ in length")
            elif any(p >= len(profile["bones"]) or p == i for i, p in enumerate(profile["parents"])):
                self.report.error(where, "invalid parent")
            if any(abs(math.sqrt(sum(v * v for v in row[3:])) - 1) > 0.001 for row in profile["bind"]):
                self.report.error(where, "bind rotations must have unit length")
            hair = library["hair"].get(profile["hair"]) if profile["hair"] else None
            if hair:
                self.check_hair_joints(where, hair["joints"])
                joints = {j["bone"] for j in hair["joints"]}
                for joint in hair["joints"]:
                    if max(joint["bone"], joint["parent"]) >= len(profile["bones"]):
                        self.report.error(where, f"hair joint {joint['bone']} is outside the rig")
                if hair["head"] >= len(profile["bones"]) or hair["head"] in joints:
                    self.report.error(where, "invalid hair head bone")
            bones = set(profile["bones"])
            for chain in library["ik"].get(profile["ik"], []):
                if not set(chain["bones"]) <= bones or chain["target"] not in bones:
                    self.report.error(where, "an IK chain names a bone that is not in the rig")

    def check_hair_joints(self, where, joints):
        """The Havok limit fields of the hair joints (PACKAGE-FORMAT.md section 10.1) come together, on ragdoll joints."""
        for joint in joints:
            frames = [field for field in HAIR_FRAME_FIELDS if field in joint]
            if frames and (len(frames) != len(HAIR_FRAME_FIELDS) or joint.get("type", "ragdoll") != "ragdoll"):
                self.report.error(
                    where,
                    f"hair joint {joint['bone']}: twistAxis, coneAxis, and planeAxis come together, on a ragdoll joint",
                )
            if any(abs(math.sqrt(sum(v * v for v in joint[field])) - 1) > 0.01 for field in frames):
                self.report.error(where, f"hair joint {joint['bone']}: a limit axis must have unit length")

    def check_library_files(self, entry, data):  # noqa: C901
        """The fixed-path files of a library (PACKAGE-FORMAT.md section 10) that its JSON files refer to."""

        def need(where, name, what):
            if name not in self.source.set:
                self.report.error(where, f"{what} file is missing: {name}")

        for key, member in (data.get("performance") or {}).get("members", {}).items():
            for clip in member["clips"]:
                need(entry["performance"], f"direction/{key}/{clip}.q48.br", "clip")
        for member in (data.get("band") or {}).get("members", []):
            need(entry["band"], f"band/{member['role']}/model.glb", "band model")
        crowd = data.get("crowd") or {}
        for model in crowd.get("models", []):
            need(entry["crowd"], f"crowd/{model}/model.glb", "crowd model")
        for hand in crowd.get("hands", []):
            need(entry["crowd"], f"crowd/hands/{hand}/model.glb", "crowd hand model")
        for clip in crowd.get("performanceClips", {}):
            need(entry["crowd"], f"crowd/clips/{clip}.q48.br", "clip")
        for emitter in (data.get("handFire") or {}).get("emitters", []):
            need(entry["handFire"], f"hand-fire/{emitter['texture']}", "hand-fire texture")
        for sample in (data.get("audio") or {}).get("samples", {}).values():
            if "file" not in sample:
                self.report.error(entry["audio"], "each sample needs 'file'")
            else:
                need(entry["audio"], sample["file"], "audio sample")
        for name, schema in EFFECT_SCHEMAS.items():
            if name in self.source.set:
                value = load_json(self.source, name, self.report)
                if value is not None:
                    check_schema(self.schemas, value, schema, name, self.report)
        frames = {}
        for name in self.source.names:
            match = re.fullmatch(r"(movies/.+)/frames/(\d{6})\.png", name)
            if match:
                frames.setdefault(match.group(1), set()).add(int(match.group(2)))
        movies = set((data.get("scripts") or {}).get("movies", {}).values())
        movies |= {name for name in self.source.names if name.startswith("movies/") and name.endswith("/movie.json")}
        for name in sorted(movies):
            if name not in self.source.set:
                continue
            movie = load_json(self.source, name, self.report)
            if movie is None or not check_schema(self.schemas, movie, "movie.schema.json", name, self.report):
                continue
            folder = name.rsplit("/", 1)[0]
            if frames.pop(folder, set()) != set(range(movie["frames"])):
                self.report.error(
                    name, "frames/000000.png and onward must exist for each frame, without gaps or extra frames"
                )
        for folder in frames:
            self.report.error(folder, "movie frames without movie.json")
