"""Validate a package (zip or unpacked folder) against package format v1 (docs/PACKAGE-FORMAT.md).

Usage: python -m pipeline.package.validate PACKAGE [--library FOLDER] [--character-library PACKAGE] [--json]

The kinds: venue (.chstage), character (.chchar), instrument (.chinst), and library (.chlib).
"""

import argparse
import hashlib
import json
import math
import re
import stat
import struct
import sys
import zipfile
import zlib
from pathlib import Path, PurePosixPath

import numpy as np

# Relative imports: the Blender add-on bundles this package (blender/build.py).
from . import q48
from .glb import Glb, GlbError, decompose, quaternion_angle
from .schema import Schemas

FORMAT_MINOR = {"chstage": 0, "chchar": 1, "chinst": 0, "chlib": 2}
HAIR_FRAME_FIELDS = ("twistAxis", "coneAxis", "planeAxis")
EXTENSIONS = {".chstage": "chstage", ".chchar": "chchar", ".chinst": "chinst", ".chlib": "chlib"}
STORED = {".glb", ".png", ".br", ".ogg", ".mp3", ".opus"}
DEFLATED = {".json", ".wav"}
WINDOWS_RESERVED = re.compile(r"^(con|prn|aux|nul|com[0-9]|lpt[0-9])(\..*)?$", re.IGNORECASE)
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
PROFILE_ID = re.compile(r"[a-z0-9][a-z0-9-]{0,63}")
PROFILES = {"gh3-guitarist-axel-v1": "guitarist", "gh3-bassist-v1": "bassist", "gh3-singer-male-v1": "singer", "gh3-drummer-v1": "drummer"}
# GH3 draws the highway texture 512 x 1024 (one repeat is 1024 rows); other sizes stretch.
HIGHWAY_SIZE = (512, 1024)
LIBRARY_ID = "gh3-band-v1"
CHARACTER_LIBRARY_ID = "gh3-characters-v1"
LIBRARY_SCHEMAS = {"catalog": "catalog.schema.json", "band": "band.schema.json", "performance": "performance.schema.json",
                   "hair": "hair.schema.json", "crowd": "crowd.schema.json", "handFire": "hand-fire.schema.json",
                   "audio": "audio.schema.json", "scripts": "scripts.schema.json", "songs": "songs.schema.json"}
EFFECT_SCHEMAS = {"effects/particle-materials.json": "particle-materials.schema.json", "effects/tesla-material.json": "tesla-material.schema.json"}
KNOWN_EXTENSIONS = {"KHR_materials_unlit"}
# Texture references in gh3Toon (PACKAGE-FORMAT 6.5).
TOON_TEXTURES = ("shadeTexture", "shadeMaskTexture", "sphereTexture")
IGNORED_EXTENSIONS = {"KHR_lights_punctual", "KHR_materials_emissive_strength", "KHR_texture_transform", "KHR_materials_specular", "KHR_materials_ior"}
STATIC_ATTRIBUTES = {"POSITION", "NORMAL", "TEXCOORD_0", "TEXCOORD_1", "TEXCOORD_2", "TEXCOORD_3", "COLOR_0", "TANGENT",
                     *(f"_GH3_UV{i}" for i in range(6)), "_GH3_BINORMAL", "_GH3_POSITION1", "_GH3_BINORMAL1"}
SKINNED_ATTRIBUTES = {"POSITION", "NORMAL", "TEXCOORD_0", "COLOR_0", "TANGENT", "JOINTS_0", "WEIGHTS_0",
                      *(f"_GH3_UV{i}" for i in range(6)), "_GH3_BINORMAL", "_GH3_TANGENT"}
# Tolerances from docs/PACKAGING-PLAN.md section 9.6.
JOINT_POSITION = 1e-4
JOINT_ROTATION = 0.01
UNIT_SCALE = 1e-4
INVERSE_BIND = 1e-4
WEIGHT_SUM = 1e-3
MAX_TEXTURE = 4096


class Report:
    def __init__(self):
        self.errors = []
        self.warnings = []

    def error(self, where, message): self.errors.append(f"{where}: {message}")

    def warn(self, where, message): self.warnings.append(f"{where}: {message}")

    @property
    def ok(self): return not self.errors


def path_problem(name):
    """Return why a package path is not portable, or None."""
    if not name or len(name.encode("utf-8")) > 240: return "path is empty or longer than 240 bytes"
    if "\\" in name or name.startswith("/") or "//" in name: return "path must be relative and use single '/' separators"
    for part in name.split("/"):
        if not re.fullmatch(r"[A-Za-z0-9_-][A-Za-z0-9._-]*", part): return f"invalid path segment '{part}'"
        if part.endswith(".") or WINDOWS_RESERVED.match(part): return f"path segment '{part}' is not valid on Windows"
    return None


class Source:
    """The files of a zip package or an unpacked folder."""

    def __init__(self, path, report):
        self.path = Path(path)
        self.zip = None
        self.names = []
        self.infos = {}
        if self.path.is_dir():
            for file in sorted(self.path.rglob("*")):
                relative = file.relative_to(self.path).as_posix()
                if file.is_symlink(): report.error(relative, "symbolic links are not allowed"); continue
                if file.is_file(): self.names.append(relative)
        else:
            try:
                self.zip = zipfile.ZipFile(self.path)
            except (zipfile.BadZipFile, OSError) as error:
                report.error(self.path.name, f"not a zip file: {error}"); return
            for info in self.zip.infolist():
                if info.is_dir(): continue
                if info.flag_bits & 0x1: report.error(info.filename, "encrypted entries are not allowed")
                if info.compress_type not in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED):
                    report.error(info.filename, "compression must be STORE or DEFLATE")
                if stat.S_ISLNK(info.external_attr >> 16): report.error(info.filename, "symbolic links are not allowed")
                if info.filename in self.infos: report.error(info.filename, "duplicate zip entry")
                suffix = PurePosixPath(info.filename).suffix.lower()
                if suffix in STORED and info.compress_type != zipfile.ZIP_STORED and info.file_size > 0:
                    report.warn(info.filename, "use STORE for this file type")
                if suffix in DEFLATED and info.compress_type != zipfile.ZIP_DEFLATED and info.file_size > 1024:
                    report.warn(info.filename, "use DEFLATE for this file type")
                self.infos[info.filename] = info
                self.names.append(info.filename)
        folded = {}
        for name in self.names:
            problem = path_problem(name)
            if problem: report.error(name, problem)
            if name.lower() in folded: report.error(name, f"differs only in letter case from {folded[name.lower()]}")
            folded.setdefault(name.lower(), name)
        self.set = set(self.names)

    def read(self, name):
        if self.zip is not None: return self.zip.read(self.infos[name])
        return (self.path / name).read_bytes()

    def size(self, name):
        if self.zip is not None: return self.infos[name].file_size
        return (self.path / name).stat().st_size


class MappedSource(Source):
    """Package files given as a mapping of package path to file path or bytes (dry conversion checks)."""

    def __init__(self, label, files, report):
        self.path = Path(label)
        self.zip = None
        self.files = files
        self.names = sorted(files)
        self.infos = {}
        for name in self.names:
            problem = path_problem(name)
            if problem: report.error(name, problem)
        self.set = set(self.names)

    def read(self, name):
        value = self.files[name]
        return value if isinstance(value, bytes) else Path(value).read_bytes()

    def size(self, name):
        value = self.files[name]
        return len(value) if isinstance(value, bytes) else Path(value).stat().st_size


def load_json(source, name, report):
    def reject(value): raise ValueError(f"non-finite number {value}")
    try:
        return json.loads(source.read(name).decode("utf-8"), parse_constant=reject)
    except (UnicodeDecodeError, ValueError) as error:
        report.error(name, f"invalid JSON: {error}")
        return None


def check_schema(schemas, value, schema, name, report):
    errors = schemas.validate(value, schema)
    for message in errors[:50]: report.error(name, message)
    if len(errors) > 50: report.error(name, f"{len(errors) - 50} more schema errors")
    return not errors


def unit_quaternions(value, name, report, location=""):
    """Check every 'rotation' quaternion in a JSON tree for unit length."""
    if isinstance(value, dict):
        for key, item in value.items():
            if key in ("rotation", "gh3ObjectRotation") and isinstance(item, list) and len(item) == 4 and all(isinstance(v, (int, float)) for v in item):
                if abs(math.sqrt(sum(v * v for v in item)) - 1) > 0.001: report.error(name, f"{location}.{key}: rotation must have unit length")
            else:
                unit_quaternions(item, name, report, f"{location}.{key}")
    elif isinstance(value, list):
        for index, item in enumerate(value): unit_quaternions(item, name, report, f"{location}[{index}]")


def png_size(data):
    """Width and height of a PNG file, or None when the data is not a PNG file."""
    if len(data) < 29 or data[:8] != PNG_SIGNATURE or data[12:16] != b"IHDR": return None
    return struct.unpack(">II", data[16:24])


def png_problem(data):
    """Why a PNG file is not allowed in a package, or None."""
    size = png_size(data)
    if size is None: return "not a PNG file"
    depth, color, _, _, interlace = struct.unpack(">5B", data[24:29])
    if depth != 8 or color not in (0, 2, 3, 4, 6): return "PNG files must use 8 bits for each channel (or an 8-bit palette)"
    if interlace: return "PNG files must not be interlaced"
    if not 1 <= min(size) or max(size) > MAX_TEXTURE: return f"PNG size must be 1 to {MAX_TEXTURE} pixels"
    return None


class Validator:
    def __init__(self, path, library=None, schemas=None, files=None, character_library=None):
        self.report = Report()
        self.schemas = schemas or Schemas()
        self.library = Path(library) if library else None
        self.character_library = Path(character_library) if character_library else None
        self.source = MappedSource(path, files, self.report) if files is not None else Source(path, self.report)
        self.manifest = None
        self.kind = None
        self.glbs = {}

    def run(self):
        if not self.source.names and not self.report.errors: self.report.error(self.source.path.name, "package is empty")
        if "manifest.json" not in self.source.set:
            if self.source.names: self.report.error("manifest.json", "missing at the package root")
            return self.report
        manifest = load_json(self.source, "manifest.json", self.report)
        if manifest is None or not check_schema(self.schemas, manifest, "manifest.schema.json", "manifest.json", self.report):
            return self.report
        self.manifest = manifest
        self.kind, version = manifest["format"].split("/")
        minor = self.minor = int(version.split(".")[1])
        if minor > FORMAT_MINOR[self.kind]:
            self.report.error("manifest.json", f"format {manifest['format']} needs a newer mod (this validator reads {self.kind}/1.{FORMAT_MINOR[self.kind]})")
            return self.report
        suffix = self.source.path.suffix.lower()
        if suffix in EXTENSIONS and EXTENSIONS[suffix] != self.kind:
            self.report.error("manifest.json", f"format {self.kind} does not match the file extension {suffix}")
        elif suffix not in EXTENSIONS and self.source.zip is not None:
            self.report.error(self.source.path.name, "a package file must use the extension .chstage, .chchar, .chinst, or .chlib")
        self.check_files()
        for key, name in manifest["entry"].items():
            if name not in self.source.set: self.report.error("manifest.json", f"entry '{key}' refers to a missing file: {name}")
        if "preview" in manifest: self.check_preview(manifest["preview"])
        for name in self.source.names:
            if name.lower().endswith(".png") and name != manifest.get("preview"):
                problem = png_problem(self.source.read(name))
                if problem: self.report.error(name, problem)
        if self.kind in ("chstage", "chchar", "chinst") and not any(r["id"] == LIBRARY_ID for r in manifest.get("requires", [])):
            self.report.warn("manifest.json", f"'requires' does not list the band library {LIBRARY_ID}")
        if any(name not in self.source.set for name in manifest["entry"].values()): return self.report
        {"chstage": self.check_stage, "chchar": self.check_character, "chinst": self.check_instrument, "chlib": self.check_library}[self.kind]()
        return self.report

    def check_files(self):
        files = self.manifest["files"]
        if not files:
            if self.source.zip is not None: self.report.error("manifest.json", "'files' must list every file of a zip package")
            else: self.report.warn("manifest.json", "'files' is empty: hashes are not checked (development folder)")
            return
        for name in self.source.names:
            if name != "manifest.json" and name not in files: self.report.error(name, "file is not listed in manifest.json 'files'")
        for name, entry in files.items():
            if name == "manifest.json": self.report.error("manifest.json", "'files' must not list manifest.json"); continue
            if name not in self.source.set: self.report.error(name, "listed in manifest.json but missing"); continue
            if self.source.size(name) != entry["size"]: self.report.error(name, "size differs from manifest.json"); continue
            if hashlib.sha256(self.source.read(name)).hexdigest() != entry["sha256"]: self.report.error(name, "SHA-256 differs from manifest.json")

    def check_preview(self, name):
        if name not in self.source.set: self.report.error("manifest.json", f"preview file is missing: {name}"); return
        size = png_size(self.source.read(name))
        if size is None: self.report.error(name, "preview must be a PNG file")
        elif max(size) > 1024: self.report.error(name, "preview must be at most 1024 x 1024 pixels")

    def json_entry(self, key, schema):
        name = self.manifest["entry"].get(key)
        if name is None: return None
        value = load_json(self.source, name, self.report)
        if value is None or not check_schema(self.schemas, value, schema, name, self.report): return None
        unit_quaternions(value, name, self.report)
        return value

    def glb(self, name):
        if name not in self.glbs:
            try:
                self.glbs[name] = Glb(self.source.read(name))
            except GlbError as error:
                self.report.error(name, str(error)); self.glbs[name] = None
        return self.glbs[name]

    # Shared GLB rules.
    def check_glb(self, name, skinned):
        glb = self.glb(name)
        if glb is None: return None
        data = glb.json
        if data.get("asset", {}).get("version") != "2.0": self.report.error(name, "asset.version must be '2.0'")
        if data.get("extensionsRequired"): self.report.error(name, f"required glTF extensions are not supported: {data['extensionsRequired']}")
        for extension in data.get("extensionsUsed", []):
            if extension not in KNOWN_EXTENSIONS: self.report.warn(name, f"glTF extension {extension} is ignored")
        buffers = glb.list("buffers")
        if len(buffers) > 1 or any("uri" in b for b in buffers) or (buffers and buffers[0].get("byteLength", 0) > len(glb.binary)):
            self.report.error(name, "a package GLB has one embedded buffer")
        image_names = set()
        for index, image in enumerate(glb.list("images")):
            if "bufferView" not in image or "uri" in image: self.report.error(name, f"image {index} must be embedded"); continue
            if image.get("mimeType") != "image/png": self.report.error(name, f"image {index} must be PNG")
            try:
                problem = png_problem(glb.view(image["bufferView"]))
            except GlbError as error:
                self.report.error(name, f"image {index}: {error}"); continue
            if problem: self.report.error(name, f"image {index}: {problem}")
            if "name" in image:
                if image["name"] in image_names: self.report.error(name, f"image name '{image['name']}' is not unique")
                image_names.add(image["name"])
        for index, node in enumerate(glb.list("nodes")):
            if "extras" in node:
                for message in self.schemas_validate(node["extras"], "node"):
                    self.report.error(name, f"node {index} ({node.get('name', '')}) extras{message}")
        names = set()
        for index, material in enumerate(glb.list("materials")):
            label = f"material {index} ({material.get('name', '')})"
            if material.get("name") in names: self.report.warn(name, f"{label}: material name is not unique")
            names.add(material.get("name"))
            extras = material.get("extras", {})
            if not isinstance(extras, dict): self.report.error(name, f"{label}: extras must be an object"); continue
            for message in self.schemas_validate(extras, "material"):
                self.report.error(name, f"{label} extras{message}")
            refs = [extras.get("gh3MaskTexture"), extras.get("gh3LayerTexture")]
            toon = extras.get("gh3Toon")
            if isinstance(toon, dict): refs += [toon.get(key) for key in TOON_TEXTURES]
            if self.kind not in ("chchar", "chinst") and (extras.get("gh3Shading") == "toon" or "gh3Toon" in extras or "gh3HairLayer" in extras):
                self.report.error(name, f"{label}: toon shading and hair layers are only for characters and instruments")
            if "gh3Toon" in extras and extras.get("gh3Shading") != "toon":
                self.report.warn(name, f"{label}: gh3Toon has no effect without gh3Shading 'toon'")
            if "gh3HairLayer" in extras and "gh3Shading" not in extras:
                self.report.warn(name, f"{label}: gh3HairLayer has no effect without gh3Shading")
            shader = extras.get("gh3Shader")
            if isinstance(shader, dict) and isinstance(shader.get("textures"), list):
                refs += shader["textures"]
                if isinstance(shader.get("addressing"), list) and len(shader["addressing"]) != len(shader["textures"]):
                    self.report.error(name, f"{label}: gh3Shader.addressing and gh3Shader.textures differ in length")
            for ref in refs:
                if isinstance(ref, int): self.report.error(name, f"{label}: texture references must be image names or package paths, not glTF indices")
                elif isinstance(ref, str) and ref:
                    if ref.startswith("#"):
                        if ref[1:] not in image_names: self.report.error(name, f"{label}: no GLB image named '{ref[1:]}'")
                    elif ref not in self.source.set: self.report.error(name, f"{label}: texture file is missing: {ref}")
        allowed = SKINNED_ATTRIBUTES if skinned else STATIC_ATTRIBUTES
        for mesh_index, mesh in enumerate(glb.list("meshes")):
            for primitive in mesh.get("primitives", []):
                if primitive.get("mode", 4) != 4: self.report.error(name, f"mesh {mesh_index}: primitives must be triangle lists")
                attributes = primitive.get("attributes", {})
                for attribute in ("POSITION", "NORMAL"):
                    if attribute not in attributes: self.report.error(name, f"mesh {mesh_index}: {attribute} is required")
                for attribute in attributes:
                    if attribute not in allowed: self.report.warn(name, f"mesh {mesh_index}: attribute {attribute} is ignored")
                if skinned and not {"JOINTS_0", "WEIGHTS_0"} <= set(attributes): self.report.error(name, f"mesh {mesh_index}: skinned meshes need JOINTS_0 and WEIGHTS_0")
                if primitive.get("targets") and not (skinned and self.kind == "chchar"):
                    self.report.error(name, f"mesh {mesh_index}: morph targets are allowed only in character GLBs")
        for index, accessor in enumerate(glb.list("accessors")):
            if "sparse" in accessor: self.report.error(name, f"accessor {index}: sparse accessors are not allowed")
        return glb

    def schemas_validate(self, value, definition):
        return self.schemas.validate_definition(value, "glb-extras.schema.json", definition)

    def check_skin(self, name, glb):
        """Check one skin, its inverse bind matrices, and the weights. Returns (joints, worlds, parents) or None."""
        skins = glb.list("skins")
        if len(skins) != 1: self.report.error(name, "the GLB must have exactly one skin"); return None
        skin = skins[0]
        joints = skin.get("joints", [])
        if not 1 <= len(joints) <= 255 or len(set(joints)) != len(joints): self.report.error(name, "the skin needs 1 to 255 unique joints"); return None
        if "inverseBindMatrices" not in skin: self.report.error(name, "inverse bind matrices are required"); return None
        try:
            parents = glb.parents()
            cache = {}
            worlds = [glb.world(j, parents, cache) for j in joints]
            inverse = glb.accessor(skin["inverseBindMatrices"])
        except GlbError as error:
            self.report.error(name, str(error)); return None
        if inverse.shape != (len(joints), 16): self.report.error(name, "inverse bind matrix count differs from the joint count"); return None
        inverse = inverse.reshape(-1, 4, 4).transpose(0, 2, 1)
        meshes = [i for i, node in enumerate(glb.list("nodes")) if "mesh" in node]
        for index in meshes:
            node = glb.list("nodes")[index]
            if node.get("skin") != 0: self.report.error(name, f"node {index}: every mesh must use the skin"); continue
            mesh_world = glb.world(index, parents, cache)
            error = max(float(np.abs(w @ m - mesh_world).max()) for w, m in zip(worlds, inverse))
            if error > INVERSE_BIND:
                self.report.error(name, f"inverse bind matrices differ from the joints by {error:.3g} (limit {INVERSE_BIND}); export at rest position")
            for primitive in glb.list("meshes")[node["mesh"]].get("primitives", []):
                attributes = primitive.get("attributes", {})
                if "JOINTS_0" not in attributes or "WEIGHTS_0" not in attributes: continue
                if "JOINTS_1" in attributes or "WEIGHTS_1" in attributes: self.report.error(name, "at most four influences for each vertex")
                try:
                    indices = glb.accessor(attributes["JOINTS_0"]); weights = glb.accessor(attributes["WEIGHTS_0"])
                except GlbError as error:
                    self.report.error(name, str(error)); continue
                if indices.shape != weights.shape or indices.shape[1] != 4: self.report.error(name, "invalid JOINTS_0 or WEIGHTS_0"); continue
                if np.any(indices >= len(joints)): self.report.error(name, "JOINTS_0 refers to a joint outside the skin")
                if np.any(weights < 0) or np.abs(weights.sum(1) - 1).max() > WEIGHT_SUM:
                    self.report.error(name, f"vertex weights must be positive and sum to 1 within {WEIGHT_SUM}")
        joint_parents = [joints.index(parents[j]) if parents.get(j) in joints else -1 for j in joints]
        return joints, worlds, joint_parents, parents, cache

    def check_animations(self, name, glb, joints):
        names = []
        for index, animation in enumerate(glb.list("animations")):
            names.append(animation.get("name", f"clip-{index}"))
            samplers = animation.get("samplers", [])
            for channel in animation.get("channels", []):
                target = channel.get("target", {}); path = target.get("path")
                sampler = samplers[channel["sampler"]] if type(channel.get("sampler")) is int and channel["sampler"] < len(samplers) else None
                if sampler is None: self.report.error(name, f"animation {index}: invalid sampler"); continue
                if sampler.get("interpolation", "LINEAR") not in ("LINEAR", "STEP"):
                    self.report.error(name, f"animation {index}: use LINEAR or STEP interpolation")
                if target.get("node") not in joints: self.report.error(name, f"animation {index}: channels must target skin joints"); continue
                if path == "scale":
                    try:
                        values = glb.accessor(sampler["output"])
                    except (GlbError, KeyError) as error:
                        self.report.error(name, f"animation {index}: {error}"); continue
                    if np.abs(values - 1).max() > UNIT_SCALE:
                        self.report.error(name, f"animation {index}: scale keys must be within {UNIT_SCALE} of 1 (they are dropped)")
                elif path not in ("translation", "rotation"):
                    self.report.error(name, f"animation {index}: '{path}' channels are not supported")
        if len(set(names)) != len(names): self.report.error(name, "animation names must be unique")
        if len(names) > 256: self.report.error(name, "at most 256 animation clips")
        return names

    # Venue.
    def check_stage(self):
        entry = self.manifest["entry"]
        placements = self.json_entry("placements", "placements.schema.json")
        cameras = self.json_entry("cameras", "cameras.schema.json")
        lighting = self.json_entry("lighting", "lighting.schema.json")
        scripts = self.json_entry("scripts", "scripts.schema.json")
        actors = self.json_entry("actors", "actors.schema.json")
        if placements and cameras: self.check_cameras(entry["cameras"], cameras, placements)
        if lighting: self.check_lighting(entry["lighting"], lighting)
        if scripts: self.check_script_files(entry["scripts"], scripts)
        if scripts and lighting: self.check_script_lights(entry["lighting"], lighting, scripts)
        glb = self.check_glb(entry["model"], skinned=False)
        if glb is not None:
            data = glb.json
            if glb.list("skins") or glb.list("animations"): self.report.error(entry["model"], "put skinned or animated scenery in an actor GLB")
            if not glb.list("meshes"): self.report.error(entry["model"], "the stage has no scenery meshes")
            beams = set(next(iter(lighting["snapshots"].values()))["beams"]) if lighting else set()
            fixtures = set()
            for node in data.get("nodes", []):
                extras = node.get("extras")
                fixture = extras.get("gh3FixtureId") if isinstance(extras, dict) else None
                if isinstance(fixture, str):
                    if fixture in fixtures: self.report.error(entry["model"], f"duplicate light fixture '{fixture}'")
                    fixtures.add(fixture)
                    if fixture not in beams: self.report.error(entry["model"], f"fixture '{fixture}' has no lighting beam")
        for actor_name, actor in (actors or {}).get("actors", {}).items():
            model = actor["model"]
            if model not in self.source.set: self.report.error(entry["actors"], f"actor '{actor_name}': model file is missing: {model}"); continue
            glb = self.check_glb(model, skinned=True)
            if glb is None: continue
            skin = self.check_skin(model, glb)
            if skin is None: continue
            clips = self.check_animations(model, glb, skin[0]) or ["bind"]
            for clip in [actor.get("clip", clips[0]), *(c for choices in actor.get("actions", {}).values() for c in choices)]:
                if clip not in clips: self.report.error(entry["actors"], f"actor '{actor_name}': GLB has no clip '{clip}'")

    def check_cameras(self, name, cameras, placements):
        shots = cameras["shots"]
        waypoints = set(placements.get("waypoints", {})) | {f"{role}_start" for role in placements["band"]}
        for group, members in cameras["groups"].items():
            for shot in members:
                if shot not in shots: self.report.error(name, f"group '{group}' refers to a missing shot '{shot}'")
        if not any(shots.get(shot, {}).get("waypoints") in (None, []) for shot in cameras["groups"]["wide"]):
            self.report.error(name, "the 'wide' group needs a shot without waypoint restriction")
        for shot_name, shot in shots.items():
            for waypoint in shot.get("waypoints", []):
                if waypoint not in waypoints: self.report.error(name, f"shot '{shot_name}' refers to a missing waypoint '{waypoint}'")
            times = shot.get("times")
            if times and (times[0] != 0 or any(b <= a for a, b in zip(times, times[1:])) or len(times) != len(shot["points"])):
                self.report.error(name, f"shot '{shot_name}': keyframe times must start at zero, increase, and match the points")
            for point in shot["points"]:
                for anchor in (point["anchor"], point.get("lookAt", "world")):
                    role = anchor.split(".")[0]
                    if role != "world" and role not in placements["band"]:
                        self.report.error(name, f"shot '{shot_name}' refers to an absent performer: {anchor}")
        for note, selector in cameras.get("notes", {}).items():
            if selector.get("group") and selector["group"] not in cameras["groups"]:
                self.report.error(name, f"camera note {note} refers to a missing group '{selector['group']}'")

    def check_lighting(self, name, lighting):
        ids = [light["id"] for light in lighting["lights"]]
        if len(set(ids)) != len(ids): self.report.error(name, "light IDs must be unique")
        beams = None
        for snapshot_name, snapshot in lighting["snapshots"].items():
            if len(snapshot["lights"]) != len(ids): self.report.error(name, f"snapshot '{snapshot_name}' must list every light")
            if beams is not None and set(snapshot["beams"]) != beams: self.report.error(name, f"snapshot '{snapshot_name}' has other beam IDs")
            beams = set(snapshot["beams"])
            for light in snapshot["lights"]:
                if light["far"] < light["near"]: self.report.error(name, f"snapshot '{snapshot_name}': far must not be less than near")
        for mood, snapshots in lighting["moods"].items():
            for snapshot in snapshots:
                if snapshot not in lighting["snapshots"]: self.report.error(name, f"mood '{mood}' refers to a missing snapshot '{snapshot}'")

    def check_script_lights(self, name, lighting, scripts):
        # The mod lights the stage through the GH3 light scene when scripts.json has LevelLight nodes; then a light
        # without a script object that names one of these nodes does not light the scenery or the band.
        definitions = scripts.get("definitions", {})
        level_lights = set()
        for array in scripts.get("nodes", []):
            value = definitions.get(f"{name_key(array):08x}")
            if not (isinstance(value, list) and len(value) == 2 and value[0] == 12 and isinstance(value[1], list)): continue
            for node in value[1]:
                fields = dict(field for field in node[1] if isinstance(field, list) and len(field) == 2) \
                    if isinstance(node, list) and len(node) == 2 and node[0] == 10 and isinstance(node[1], list) else {}
                node_class, node_name = fields.get(f"0x{gh3_key('Class'):08x}"), fields.get(f"0x{gh3_key('Name'):08x}")
                if node_class == [13, f"0x{gh3_key('LevelLight'):08x}"] and isinstance(node_name, list) and len(node_name) == 2:
                    level_lights.add(name_key(node_name[1]))
        if not level_lights: return
        for light in lighting["lights"]:
            if not light.get("object") or name_key(light["object"]) not in level_lights:
                self.report.warn(name, f"light '{light['id']}' has no GH3 light object: scripts.json has LevelLight nodes, so only these "
                                 "light the stage. Set the script object of the light, or remove the LevelLight nodes")

    def check_script_files(self, name, scripts):
        for texture, file in scripts.get("textures", {}).items():
            if file not in self.source.set: self.report.error(name, f"script texture '{texture}' file is missing: {file}")
        for movie, file in scripts.get("movies", {}).items():
            if file not in self.source.set: self.report.error(name, f"movie '{movie}' file is missing: {file}")

    # Character.
    def check_character(self):
        entry = self.manifest["entry"]
        character = self.json_entry("character", "character.schema.json")
        glb = self.check_glb(entry["model"], skinned=True)
        if character is None or glb is None: return
        own = None
        if "animations" in character and self.minor < 1:
            self.report.error(entry["character"], "'animations' needs format chchar/1.1")
        if character["profile"] not in PROFILES:
            if self.minor < 1:
                self.report.error(entry["character"], f"unknown profile '{character['profile']}'")
            elif character["library"] == LIBRARY_ID:
                self.report.error(entry["character"], f"the band library has no profile '{character['profile']}'")
            elif not any(r["id"] == character["library"] for r in self.manifest.get("requires", [])):
                self.report.error("manifest.json", f"'requires' must list the character library {character['library']}")
            if self.minor >= 1 and self.character_library is not None:
                try:
                    library = load_character_library(self.character_library)
                except (OSError, ValueError) as error:
                    self.report.error("--character-library", f"cannot read the character library: {error}"); return
                if library.get("library") != character["library"]:
                    self.report.warn(entry["character"], f"the profile is not checked: --character-library is {library.get('library')}, not {character['library']}")
                elif character["profile"] not in library["profiles"]:
                    self.report.error(entry["character"], f"library {character['library']} has no profile '{character['profile']}'")
                else:
                    own = character_profile(library, character["profile"])
                    if "animations" in character and character["animations"] not in library["animations"]:
                        self.report.error(entry["character"], f"library {character['library']} has no animation set '{character['animations']}'")
        if "highway" in character: self.check_highway(entry["character"], character["highway"])
        materials = {m.get("name") for m in glb.list("materials")}
        for material in character.get("hairMaterials", []):
            if material not in materials: self.report.error(entry["character"], f"hair material '{material}' is not in the GLB")
        if glb.list("animations"): self.report.warn(entry["model"], "character animations are ignored; the profile supplies the clips")
        skin = self.check_skin(entry["model"], glb)
        if skin is None: return
        joints, worlds, joint_parents, parents, cache = skin
        names = []
        for joint in joints:
            label = glb.list("nodes")[joint].get("name", "")
            match = re.fullmatch(r"bone-([0-9a-f]{8})", label)
            names.append(match.group(1) if match else f"{gh3_key(label):08x}" if label else "")
        if "" in names or len(set(names)) != len(names): self.report.error(entry["model"], "joint names must be unique GH3 bone names"); return
        root = [j for j, p in zip(joints, joint_parents) if p < 0]
        for joint in root:
            if joint in parents:
                above = glb.world(parents[joint], parents, cache)
                if np.abs(above - np.eye(4)).max() > JOINT_POSITION:
                    self.report.error(entry["model"], "nodes above the root joint must have the identity transform")
        if own is not None:
            self.check_profile(entry["model"], glb, joints, names, joint_parents, own)
        elif character["profile"] not in PROFILES or self.library is None:
            self.report.warn(entry["model"], "joints are not checked against the profile (no --library or --character-library)")
            return
        else:
            try:
                profile = load_profile(self.library, PROFILES[character["profile"]])
            except (OSError, ValueError) as error:
                self.report.error("--library", f"cannot read the profile rig: {error}"); return
            self.check_profile(entry["model"], glb, joints, names, joint_parents, profile)
        if self.library is None: return
        instrument = character.get("attachments", {}).get("instrument")
        known = library_instruments(self.library)
        if instrument and known and instrument not in known:
            self.report.warn(entry["character"], f"attachments.instrument '{instrument}' is not a library instrument ({', '.join(sorted(known))}); "
                             "the game uses the instrument of the band role")

    def check_highway(self, where, name):
        if name not in self.source.set: self.report.error(where, f"highway file is missing: {name}"); return
        size = png_size(self.source.read(name))
        if size is not None and size != HIGHWAY_SIZE:
            self.report.warn(name, f"the highway is {size[0]} x {size[1]} pixels; GH3 highways are {HIGHWAY_SIZE[0]} x {HIGHWAY_SIZE[1]} (other sizes stretch)")

    # Instrument.
    def check_instrument(self):
        entry = self.manifest["entry"]
        instrument = self.json_entry("instrument", "instrument.schema.json")
        glb = self.check_glb(entry["model"], skinned=True)
        if instrument is None or glb is None: return
        if "highway" in instrument: self.check_highway(entry["instrument"], instrument["highway"])
        if glb.list("animations"): self.report.warn(entry["model"], "instrument animations are ignored")
        skin = self.check_skin(entry["model"], glb)
        if skin is None: return
        joints, worlds, joint_parents, parents, cache = skin
        names = self.joint_names(entry["model"], glb, joints)
        if names is None: return
        # As BandAttachments: the body is the subtree of the parent of bone_pelvis; an instrument follows the other joints.
        pelvis = names.index(f"{gh3_key('bone_pelvis'):08x}") if f"{gh3_key('bone_pelvis'):08x}" in names else -1
        root = joint_parents[pelvis] if pelvis >= 0 and joint_parents[pelvis] >= 0 else -1
        body, stack = set(), [root] if root >= 0 else []
        while stack:
            joint = stack.pop()
            if joint in body: continue
            body.add(joint)
            stack.extend(i for i, p in enumerate(joint_parents) if p == joint)
        for mesh_index, mesh in enumerate(glb.list("meshes")):
            for index, primitive in enumerate(mesh.get("primitives", [])):
                attributes = primitive.get("attributes", {})
                if "JOINTS_0" not in attributes or "WEIGHTS_0" not in attributes: continue
                dominant = glb.accessor(attributes["JOINTS_0"]).astype(int)
                dominant = dominant[np.arange(len(dominant)), glb.accessor(attributes["WEIGHTS_0"]).argmax(1)]
                if np.mean([j in body for j in dominant]) >= 0.5:
                    self.report.error(entry["model"], f"mesh {mesh_index} primitive {index} follows the body joints; weight the instrument to bone_guitar_body")
        if self.library is None:
            self.report.warn(entry["model"], "joints are not checked against the profile (no --library)"); return
        try:
            profile = load_profile(self.library, PROFILES[instrument["profile"]])
        except (OSError, ValueError) as error:
            self.report.error("--library", f"cannot read the profile rig: {error}"); return
        self.check_profile(entry["model"], glb, joints, names, joint_parents, profile)

    def joint_names(self, name, glb, joints):
        """GH3 checksums of the joints ('bone-<checksum>' or a GH3 bone name), or None after an error."""
        names = []
        for joint in joints:
            label = glb.list("nodes")[joint].get("name", "")
            match = re.fullmatch(r"bone-([0-9a-f]{8})", label)
            names.append(match.group(1) if match else f"{gh3_key(label):08x}" if label else "")
        if "" in names or len(set(names)) != len(names): self.report.error(name, "joint names must be unique GH3 bone names"); return None
        return names

    def check_profile(self, name, glb, joints, names, joint_parents, profile):
        profile_names, profile_parents, translations, rotations = profile
        index = {n: i for i, n in enumerate(names)}
        missing = [n for n in profile_names if n not in index]
        extra = [n for n in names if n not in profile_names]
        if missing: self.report.error(name, f"{len(missing)} profile joints are missing, for example bone-{missing[0]}")
        if extra: self.report.error(name, f"{len(extra)} joints are not in the profile, for example bone-{extra[0]}")
        if missing or extra: return
        worst = [0.0, 0.0, 0.0]
        for p, bone in enumerate(profile_names):
            i = index[bone]
            parent = joint_parents[i]
            expected = profile_parents[p]
            if (parent < 0) != (expected < 0) or parent >= 0 and names[parent] != profile_names[expected]:
                self.report.error(name, f"joint bone-{bone} has another parent than the profile"); continue
            local = glb.node_matrix(glb.list("nodes")[joints[i]])
            translation, rotation, scale = decompose(local)
            worst[0] = max(worst[0], float(np.abs(translation - translations[p]).max()))
            worst[1] = max(worst[1], quaternion_angle(rotation, rotations[p]))
            worst[2] = max(worst[2], float(np.abs(scale - 1).max()))
        if worst[0] > JOINT_POSITION: self.report.error(name, f"joint positions differ from the profile by {worst[0] * 1000:.3g} mm (limit 0.1 mm)")
        if worst[1] > JOINT_ROTATION: self.report.error(name, f"joint rotations differ from the profile by {worst[1]:.3g} degrees (limit 0.01); check the export options: Blender bone direction, +Y up, rest position")
        if worst[2] > UNIT_SCALE: self.report.error(name, f"joint scale differs from 1 by {worst[2]:.3g} (limit {UNIT_SCALE}); apply scale before export")

    # Library.
    def check_library(self):
        entry = self.manifest["entry"]
        if "characters" in entry:
            if self.minor < 1: self.report.error("manifest.json", "a character library needs format chlib/1.1")
            if self.manifest["id"] != CHARACTER_LIBRARY_ID: self.report.warn("manifest.json", f"the mod uses the character library {CHARACTER_LIBRARY_ID}")
            if any(key in entry for key in LIBRARY_SCHEMAS): self.report.error("manifest.json", "a character library has only the entry 'characters'")
            self.check_character_library(entry["characters"])
        elif self.manifest["id"] != LIBRARY_ID: self.report.warn("manifest.json", f"the mod v1 uses the library {LIBRARY_ID}")
        data = {key: self.json_entry(key, schema) for key, schema in LIBRARY_SCHEMAS.items() if key in entry}
        if data.get("scripts"): self.check_script_files(entry["scripts"], data["scripts"])
        for role, member in (data.get("hair") or {}).get("members", {}).items(): self.check_hair_joints(f"{entry['hair']}: {role}", member["joints"])
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
                if glb is not None and glb.list("skins"): self.check_skin(name, glb)

    def check_character_library(self, name):
        """characters.json (PACKAGE-FORMAT.md section 10.1): references between profiles, sets, hair, IK, and clip files."""
        library = self.json_entry("characters", "characters.schema.json")
        if library is None: return
        rig = library["clipRig"]
        if not len(rig["bones"]) == len(rig["parents"]) == len(rig["bind"]): self.report.error(name, "clipRig: bones, parents, and bind differ in length")
        for set_name, animations in library["animations"].items():
            for table in ("groups", "layers"):
                for field, clips in animations[table].items():
                    for clip in clips:
                        if clip not in animations["clips"]: self.report.error(name, f"animation set {set_name}: {table} {field} names clip {clip}, which is not in its clips")
            for clip in animations["clips"]:
                if f"clips/{clip}.q48.br" not in self.source.set: self.report.error(name, f"animation set {set_name}: clip file is missing: clips/{clip}.q48.br")
        for profile_id, profile in library["profiles"].items():
            where = f"{name}: profile {profile_id}"
            if not PROFILE_ID.fullmatch(profile_id): self.report.error(where, "invalid profile ID")
            if profile_id in PROFILES: self.report.error(where, "the band library has a profile with this ID")
            if profile["animations"] not in library["animations"]: self.report.error(where, f"no animation set {profile['animations']}")
            if profile["hair"] is not None and profile["hair"] not in library["hair"]: self.report.error(where, f"no hair {profile['hair']}")
            if profile["ik"] not in library["ik"]: self.report.error(where, f"no IK chains {profile['ik']}")
            if profile["bones"] != rig["bones"]: self.report.error(where, "the bones differ from clipRig (names and order)")
            if not len(profile["bones"]) == len(profile["parents"]) == len(profile["bind"]): self.report.error(where, "bones, parents, and bind differ in length")
            elif any(p >= len(profile["bones"]) or p == i for i, p in enumerate(profile["parents"])): self.report.error(where, "invalid parent")
            if any(abs(math.sqrt(sum(v * v for v in row[3:])) - 1) > 0.001 for row in profile["bind"]): self.report.error(where, "bind rotations must have unit length")
            hair = library["hair"].get(profile["hair"]) if profile["hair"] else None
            if hair:
                self.check_hair_joints(where, hair["joints"])
                joints = {j["bone"] for j in hair["joints"]}
                for joint in hair["joints"]:
                    if max(joint["bone"], joint["parent"]) >= len(profile["bones"]): self.report.error(where, f"hair joint {joint['bone']} is outside the rig")
                if hair["head"] >= len(profile["bones"]) or hair["head"] in joints: self.report.error(where, "invalid hair head bone")
            bones = set(profile["bones"])
            for chain in library["ik"].get(profile["ik"], []):
                if not set(chain["bones"]) <= bones or chain["target"] not in bones: self.report.error(where, "an IK chain names a bone that is not in the rig")

    def check_hair_joints(self, where, joints):
        """Format 1.2 hair fields (PACKAGE-FORMAT.md section 10.1): the Havok limit axes come together, on ragdoll joints."""
        for joint in joints:
            frames = [field for field in HAIR_FRAME_FIELDS if field in joint]
            if (frames or "maxAngularVelocity" in joint) and self.minor < 2:
                self.report.error(where, f"hair joint {joint['bone']}: the Havok limit axes and speed limit need format chlib/1.2")
            if frames and (len(frames) != len(HAIR_FRAME_FIELDS) or joint.get("type", "ragdoll") != "ragdoll"):
                self.report.error(where, f"hair joint {joint['bone']}: twistAxis, coneAxis, and planeAxis come together, on a ragdoll joint")
            if any(abs(math.sqrt(sum(v * v for v in joint[field])) - 1) > 0.01 for field in frames):
                self.report.error(where, f"hair joint {joint['bone']}: a limit axis must have unit length")

    def check_library_files(self, entry, data):
        """The fixed-path files of a library (PACKAGE-FORMAT.md section 10) that its JSON files refer to."""
        def need(where, name, what):
            if name not in self.source.set: self.report.error(where, f"{what} file is missing: {name}")

        for key, member in (data.get("performance") or {}).get("members", {}).items():
            for clip in member["clips"]: need(entry["performance"], f"direction/{key}/{clip}.q48.br", "clip")
        for member in (data.get("band") or {}).get("members", []):
            need(entry["band"], f"band/{member['role']}/model.glb", "band model")
        crowd = data.get("crowd") or {}
        for model in crowd.get("models", []): need(entry["crowd"], f"crowd/{model}/model.glb", "crowd model")
        for hand in crowd.get("hands", []): need(entry["crowd"], f"crowd/hands/{hand}/model.glb", "crowd hand model")
        for clip in crowd.get("performanceClips", {}): need(entry["crowd"], f"crowd/clips/{clip}.q48.br", "clip")
        for emitter in (data.get("handFire") or {}).get("emitters", []): need(entry["handFire"], f"hand-fire/{emitter['texture']}", "hand-fire texture")
        for sample in (data.get("audio") or {}).get("samples", {}).values():
            if "file" not in sample: self.report.error(entry["audio"], "each sample needs 'file'")
            else: need(entry["audio"], sample["file"], "audio sample")
        songs = data.get("songs")
        if songs:
            song_folder = entry["songs"].rsplit("/", 1)[0] + "/" if "/" in entry["songs"] else ""
            for song in songs["songs"]: need(entry["songs"], song_folder + song["file"], "song direction")
        for name, schema in EFFECT_SCHEMAS.items():
            if name in self.source.set:
                value = load_json(self.source, name, self.report)
                if value is not None: check_schema(self.schemas, value, schema, name, self.report)
        frames = {}
        for name in self.source.names:
            match = re.fullmatch(r"(movies/.+)/frames/(\d{6})\.png", name)
            if match: frames.setdefault(match.group(1), set()).add(int(match.group(2)))
        movies = set((data.get("scripts") or {}).get("movies", {}).values())
        movies |= {name for name in self.source.names if name.startswith("movies/") and name.endswith("/movie.json")}
        for name in sorted(movies):
            if name not in self.source.set: continue
            movie = load_json(self.source, name, self.report)
            if movie is None or not check_schema(self.schemas, movie, "movie.schema.json", name, self.report): continue
            folder = name.rsplit("/", 1)[0]
            if frames.pop(folder, set()) != set(range(movie["frames"])): self.report.error(name, "frames/000000.png and onward must exist for each frame, without gaps or extra frames")
        for folder in frames: self.report.error(folder, "movie frames without movie.json")
        if songs:
            for name in self.source.names:
                if name.startswith(song_folder) and name.endswith(".json") and name != entry["songs"] and "/" not in name[len(song_folder):]:
                    value = load_json(self.source, name, self.report)
                    if value is not None: check_schema(self.schemas, value, "song.schema.json", name, self.report)


def gh3_key(name):
    """GH3 checksum of a name (pipeline.gh3.formats.key, repeated here so the add-on bundle has no other dependency)."""
    return ~zlib.crc32(name.lower().encode()) & 0xFFFFFFFF


def name_key(name):
    """A GH3 name as the mod reads it (Gh3QbText.Key): a '0x' checksum or a name text."""
    return int(name[2:], 16) if isinstance(name, str) and name.lower().startswith("0x") else gh3_key(str(name))


def library_instruments(library):
    """The instrument IDs of band/band.json in a library package (zip or folder) or the current library folder."""
    library = Path(library)
    try:
        if library.is_file() or (library/"manifest.json").is_file():
            source = Source(library, Report())
            band = json.loads(source.read("band/band.json")) if "band/band.json" in source.set else {}
        else:
            band = json.loads((library / "band" / "band.json").read_text())
    except (OSError, ValueError):
        return set()
    return {m.get("instrument") for m in band.get("members", []) if m.get("instrument")}


def load_profile(library, role):
    """Profile rig: joint checksums, parents, and glTF-space local bind transforms. `library` is a library package
    (zip or unpacked folder; the rig comes from band/<role>/model.glb) or the current library folder."""
    library = Path(library)
    if library.is_file() or (library/"manifest.json").is_file():
        report = Report()
        source = Source(library, report)
        if report.errors or f"band/{role}/model.glb" not in source.set: raise ValueError(f"the library has no band/{role}/model.glb")
        glb = Glb(source.read(f"band/{role}/model.glb"))
        nodes = glb.list("nodes"); joints = glb.list("skins")[0]["joints"]; parents = glb.parents()
        names = [nodes[j]["name"].removeprefix("bone-").lower() for j in joints]
        local = [decompose(glb.node_matrix(nodes[j])) for j in joints]
        return names, [joints.index(parents[j]) if parents.get(j) in joints else -1 for j in joints], \
            np.array([t for t, _, _ in local]), np.array([r for _, r, _ in local])
    bones = json.loads((library / "direction" / "performance.json").read_text())["members"][role]["bones"]
    data = (library / "band" / role / "animation.bin").read_bytes()
    if data[:8] != b"GH3ANM01": raise ValueError("expected GH3ANM01")
    count, = struct.unpack_from("<I", data, 8)
    rows = [struct.unpack_from("<i7f", data, 16 + i * 32) for i in range(count)]
    if len(rows) != len(bones): raise ValueError("bone names differ from the skeleton")
    # animation.bin is in Unity space: glTF space negates z of positions and x, y of rotations (see GlbWriter.actor).
    translations = np.array([r[1:4] for r in rows]) * [1, 1, -1]
    rotations = np.array([r[4:8] for r in rows]) * [-1, -1, 1, 1]
    return [b.lower() for b in bones], [r[0] for r in rows], translations, rotations


def load_character_library(path):
    """characters.json of a character library package (zip or folder) or of the pipeline folder."""
    path = Path(path)
    if path.is_file() or (path/"manifest.json").is_file():
        report = Report()
        source = Source(path, report)
        if report.errors or "characters.json" not in source.set: raise ValueError("the package has no characters.json")
        return json.loads(source.read("characters.json"))
    return json.loads((path/"characters.json").read_text())


def character_profile(library, profile):
    """A character library profile as load_profile returns it: names, parents, glTF-space translations and rotations."""
    row = library["profiles"][profile]
    bind = np.array(row["bind"], dtype=np.float64)
    return [b.lower() for b in row["bones"]], row["parents"], bind[:, :3] * [1, 1, -1], bind[:, 3:] * [-1, -1, 1, 1]


def validate(path, library=None, character_library=None):
    return Validator(path, library, character_library=character_library).run()


def main(argv=None):
    parser = argparse.ArgumentParser(prog="python -m pipeline.package.validate", description=__doc__.splitlines()[0])
    parser.add_argument("package", type=Path, nargs="+")
    parser.add_argument("--library", type=Path, help="library package (gh3-band-v1.chlib) or current library folder for the character profile check")
    parser.add_argument("--character-library", type=Path, help="character library package (gh3-characters-v1.chlib) or folder for the profile check of format 1.1 characters")
    parser.add_argument("--json", action="store_true", help="print one JSON report for each package")
    args = parser.parse_args(argv)
    failed = False
    for package in args.package:
        report = validate(package, args.library, args.character_library)
        failed |= not report.ok
        if args.json:
            print(json.dumps({"package": str(package), "errors": report.errors, "warnings": report.warnings}))
            continue
        print(f"{package}: {'valid' if report.ok else 'INVALID'} ({len(report.errors)} errors, {len(report.warnings)} warnings)")
        for message in report.errors: print(f"  error: {message}")
        for message in report.warnings: print(f"  warning: {message}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
