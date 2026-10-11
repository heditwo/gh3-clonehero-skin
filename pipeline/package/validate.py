"""Validate a package (zip or unpacked folder) against package format v1 (docs/PACKAGE-FORMAT.md).

Usage: python -m pipeline.package.validate PACKAGE [--library FOLDER] [--character-library PACKAGE] [--json]

The kinds: venue (.chstage), character (.chchar), instrument (.chinst), and library (.chlib).
"""

import argparse
import hashlib
import json
import sys
from pathlib import Path

import numpy as np

# Relative imports: the Blender add-on bundles this package (blender/build.py).
from . import texture
from .checks import INVERSE_BIND, UNIT_SCALE, WEIGHT_SUM, check_schema, png_problem, png_size, unit_quaternions
from .glb import Glb, GlbError
from .profiles import LIBRARY_ID
from .schema import Schemas
from .source import MappedSource, Report, Source, load_json
from .validate_character import CharacterChecks
from .validate_library import LibraryChecks
from .validate_stage import StageChecks

# Every kind is format 1.0 (PACKAGING-PLAN decision 12). A higher minor comes from a newer mod.
FORMAT_MINOR = 0
EXTENSIONS = {".chstage": "chstage", ".chchar": "chchar", ".chinst": "chinst", ".chlib": "chlib"}
KNOWN_EXTENSIONS = {"KHR_materials_unlit", texture.EXTENSION}
EXPORT_AGAIN = "package format 1.0 stores textures pre-encoded (GH3TEX02, PACKAGE-FORMAT.md section 7): export the package again with the current CH Stage Tools add-on, or run the converter (python -m pipeline.package.build) again"
# Texture references in gh3Toon (PACKAGE-FORMAT 6.5).
TOON_TEXTURES = ("shadeTexture", "shadeMaskTexture", "sphereTexture")
IGNORED_EXTENSIONS = {
    "KHR_lights_punctual",
    "KHR_materials_emissive_strength",
    "KHR_texture_transform",
    "KHR_materials_specular",
    "KHR_materials_ior",
}
STATIC_ATTRIBUTES = {
    "POSITION",
    "NORMAL",
    "TEXCOORD_0",
    "TEXCOORD_1",
    "TEXCOORD_2",
    "TEXCOORD_3",
    "COLOR_0",
    "TANGENT",
    *(f"_GH3_UV{i}" for i in range(6)),
    "_GH3_BINORMAL",
    "_GH3_POSITION1",
    "_GH3_BINORMAL1",
}
SKINNED_ATTRIBUTES = {
    "POSITION",
    "NORMAL",
    "TEXCOORD_0",
    "COLOR_0",
    "TANGENT",
    "JOINTS_0",
    "WEIGHTS_0",
    *(f"_GH3_UV{i}" for i in range(6)),
    "_GH3_BINORMAL",
    "_GH3_TANGENT",
}


class Validator(StageChecks, CharacterChecks, LibraryChecks):
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
        if not self.source.names and not self.report.errors:
            self.report.error(self.source.path.name, "package is empty")
        if "manifest.json" not in self.source.set:
            if self.source.names:
                self.report.error("manifest.json", "missing at the package root")
            return self.report
        manifest = load_json(self.source, "manifest.json", self.report)
        if manifest is None or not check_schema(
            self.schemas, manifest, "manifest.schema.json", "manifest.json", self.report
        ):
            return self.report
        self.manifest = manifest
        self.kind, version = manifest["format"].split("/")
        if int(version.split(".")[1]) > FORMAT_MINOR:
            self.report.error(
                "manifest.json",
                f"format {manifest['format']} needs a newer mod (this validator reads {self.kind}/1.{FORMAT_MINOR})",
            )
            return self.report
        suffix = self.source.path.suffix.lower()
        if suffix in EXTENSIONS and EXTENSIONS[suffix] != self.kind:
            self.report.error("manifest.json", f"format {self.kind} does not match the file extension {suffix}")
        elif suffix not in EXTENSIONS and self.source.zip is not None:
            self.report.error(
                self.source.path.name, "a package file must use the extension .chstage, .chchar, .chinst, or .chlib"
            )
        self.check_files()
        for key, name in manifest["entry"].items():
            if name not in self.source.set:
                self.report.error("manifest.json", f"entry '{key}' refers to a missing file: {name}")
        if "preview" in manifest:
            self.check_preview(manifest["preview"])
        for name in self.source.names:
            if name.lower().endswith(".png") and name != manifest.get("preview"):
                problem = png_problem(self.source.read(name))
                if problem:
                    self.report.error(name, problem)
            elif name.lower().endswith(".tex"):
                self.check_texture(name, "texture", self.source.read(name), None)
        if self.kind in ("chstage", "chchar", "chinst") and not any(
            r["id"] == LIBRARY_ID for r in manifest.get("requires", [])
        ):
            self.report.warn("manifest.json", f"'requires' does not list the band library {LIBRARY_ID}")
        if any(name not in self.source.set for name in manifest["entry"].values()):
            return self.report
        {
            "chstage": self.check_stage,
            "chchar": self.check_character,
            "chinst": self.check_instrument,
            "chlib": self.check_library,
        }[self.kind]()
        return self.report

    def check_files(self):
        files = self.manifest["files"]
        if not files:
            if self.source.zip is not None:
                self.report.error("manifest.json", "'files' must list every file of a zip package")
            else:
                self.report.warn("manifest.json", "'files' is empty: hashes are not checked (development folder)")
            return
        for name in self.source.names:
            if name != "manifest.json" and name not in files:
                self.report.error(name, "file is not listed in manifest.json 'files'")
        for name, entry in files.items():
            if name == "manifest.json":
                self.report.error("manifest.json", "'files' must not list manifest.json")
                continue
            if name not in self.source.set:
                self.report.error(name, "listed in manifest.json but missing")
                continue
            if self.source.size(name) != entry["size"]:
                self.report.error(name, "size differs from manifest.json")
                continue
            if hashlib.sha256(self.source.read(name)).hexdigest() != entry["sha256"]:
                self.report.error(name, "SHA-256 differs from manifest.json")

    def check_texture(self, name, label, data, linear):
        """GH3TEX02 rules (section 7). linear: True for the source of a glTF normal texture, False for other GLB images,
        None for package files (the flag is not checked)."""
        problems = texture.problems(data)
        for problem in problems:
            self.report.error(name, f"{label}: {problem}")
        if problems:
            return
        fmt, _, _, _, flags = texture.read_header(data)
        if linear is not None and bool(flags & texture.FLAG_LINEAR) != linear:
            self.report.warn(
                name,
                f"{label}: "
                + (
                    "a normal texture should have the linear flag"
                    if linear
                    else "a colour texture should not have the linear flag"
                ),
            )
        if fmt == texture.BC3 and (texture.decode(data)[:, :, 3] == 255).all():
            self.report.warn(name, f"{label}: BC3 texture without alpha; BC1 is half the size")

    def check_preview(self, name):
        if name not in self.source.set:
            self.report.error("manifest.json", f"preview file is missing: {name}")
            return
        size = png_size(self.source.read(name))
        if size is None:
            self.report.error(name, "preview must be a PNG file")
        elif max(size) > 1024:
            self.report.error(name, "preview must be at most 1024 x 1024 pixels")

    def json_entry(self, key, schema):
        name = self.manifest["entry"].get(key)
        if name is None:
            return None
        value = load_json(self.source, name, self.report)
        if value is None or not check_schema(self.schemas, value, schema, name, self.report):
            return None
        unit_quaternions(value, name, self.report)
        return value

    def glb(self, name):
        if name not in self.glbs:
            try:
                self.glbs[name] = Glb(self.source.read(name))
            except GlbError as error:
                self.report.error(name, str(error))
                self.glbs[name] = None
        return self.glbs[name]

    # Shared GLB rules.
    def check_glb(self, name, skinned):
        glb = self.glb(name)
        if glb is None:
            return None
        self._check_glb_header(name, glb)
        image_names = self._check_glb_images(name, glb)
        for index, node in enumerate(glb.list("nodes")):
            if "extras" in node:
                for message in self.schemas_validate(node["extras"], "node"):
                    self.report.error(name, f"node {index} ({node.get('name', '')}) extras{message}")
        self._check_glb_materials(name, glb, image_names)
        self._check_glb_meshes(name, glb, skinned)
        return glb

    def _check_glb_header(self, name, glb):
        data = glb.json
        if data.get("asset", {}).get("version") != "2.0":
            self.report.error(name, "asset.version must be '2.0'")
        unsupported = [e for e in data.get("extensionsRequired", []) if e != texture.EXTENSION]
        if unsupported:
            self.report.error(name, f"required glTF extensions are not supported: {unsupported}")
        for extension in data.get("extensionsUsed", []):
            if extension not in KNOWN_EXTENSIONS:
                self.report.warn(name, f"glTF extension {extension} is ignored")
        buffers = glb.list("buffers")
        if (
            len(buffers) > 1
            or any("uri" in b for b in buffers)
            or (buffers and buffers[0].get("byteLength", 0) > len(glb.binary))
        ):
            self.report.error(name, "a package GLB has one embedded buffer")

    def _check_glb_images(self, name, glb):
        """Images are embedded GH3TEX02 textures with unique names; returns the names."""
        data = glb.json
        image_names = set()
        linear = texture.linear_images(data)
        for index, image in enumerate(glb.list("images")):
            if "bufferView" not in image or "uri" in image:
                self.report.error(name, f"image {index} must be embedded")
                continue
            if image.get("mimeType") == "image/png":
                self.report.error(name, f"image {index} is PNG; {EXPORT_AGAIN}")
            elif image.get("mimeType") != texture.MIME_TYPE:
                self.report.error(name, f"image {index} must have the media type {texture.MIME_TYPE}")
            else:
                try:
                    self.check_texture(
                        name, f"image {index} ({image.get('name', '')})", glb.view(image["bufferView"]), index in linear
                    )
                except GlbError as error:
                    self.report.error(name, f"image {index}: {error}")
                    continue
                if texture.EXTENSION not in data.get("extensionsRequired", []):
                    self.report.error(
                        name,
                        f"GLB images of the media type {texture.MIME_TYPE} need '{texture.EXTENSION}' in extensionsRequired",
                    )
            if "name" in image:
                if image["name"] in image_names:
                    self.report.error(name, f"image name '{image['name']}' is not unique")
                image_names.add(image["name"])
        return image_names

    def _check_glb_materials(self, name, glb, image_names):
        names = set()
        for index, material in enumerate(glb.list("materials")):
            label = f"material {index} ({material.get('name', '')})"
            if material.get("name") in names:
                self.report.warn(name, f"{label}: material name is not unique")
            names.add(material.get("name"))
            extras = material.get("extras", {})
            if not isinstance(extras, dict):
                self.report.error(name, f"{label}: extras must be an object")
                continue
            for message in self.schemas_validate(extras, "material"):
                self.report.error(name, f"{label} extras{message}")
            refs = [extras.get("gh3MaskTexture"), extras.get("gh3LayerTexture")]
            toon = extras.get("gh3Toon")
            if isinstance(toon, dict):
                refs += [toon.get(key) for key in TOON_TEXTURES]
            if self.kind not in ("chchar", "chinst") and (
                extras.get("gh3Shading") == "toon" or "gh3Toon" in extras or "gh3HairLayer" in extras
            ):
                self.report.error(
                    name, f"{label}: toon shading and hair layers are only for characters and instruments"
                )
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
                if isinstance(ref, int):
                    self.report.error(
                        name, f"{label}: texture references must be image names or package paths, not glTF indices"
                    )
                elif isinstance(ref, str) and ref:
                    if ref.startswith("#"):
                        if ref[1:] not in image_names:
                            self.report.error(name, f"{label}: no GLB image named '{ref[1:]}'")
                    elif ref.lower().endswith(".png"):
                        self.report.error(name, f"{label}: texture file {ref} is PNG; {EXPORT_AGAIN}")
                    elif ref not in self.source.set:
                        self.report.error(name, f"{label}: texture file is missing: {ref}")

    def _check_glb_meshes(self, name, glb, skinned):
        allowed = SKINNED_ATTRIBUTES if skinned else STATIC_ATTRIBUTES
        for mesh_index, mesh in enumerate(glb.list("meshes")):
            for primitive in mesh.get("primitives", []):
                if primitive.get("mode", 4) != 4:
                    self.report.error(name, f"mesh {mesh_index}: primitives must be triangle lists")
                attributes = primitive.get("attributes", {})
                for attribute in ("POSITION", "NORMAL"):
                    if attribute not in attributes:
                        self.report.error(name, f"mesh {mesh_index}: {attribute} is required")
                for attribute in attributes:
                    if attribute not in allowed:
                        self.report.warn(name, f"mesh {mesh_index}: attribute {attribute} is ignored")
                if skinned and not {"JOINTS_0", "WEIGHTS_0"} <= set(attributes):
                    self.report.error(name, f"mesh {mesh_index}: skinned meshes need JOINTS_0 and WEIGHTS_0")
                if primitive.get("targets") and not (skinned and self.kind == "chchar"):
                    self.report.error(name, f"mesh {mesh_index}: morph targets are allowed only in character GLBs")
        for index, accessor in enumerate(glb.list("accessors")):
            if "sparse" in accessor:
                self.report.error(name, f"accessor {index}: sparse accessors are not allowed")

    def schemas_validate(self, value, definition):
        return self.schemas.validate_definition(value, "glb-extras.schema.json", definition)

    def check_skin(self, name, glb):
        """Check one skin, its inverse bind matrices, and the weights. Returns (joints, worlds, parents) or None."""
        skins = glb.list("skins")
        if len(skins) != 1:
            self.report.error(name, "the GLB must have exactly one skin")
            return None
        skin = skins[0]
        joints = skin.get("joints", [])
        if not 1 <= len(joints) <= 255 or len(set(joints)) != len(joints):
            self.report.error(name, "the skin needs 1 to 255 unique joints")
            return None
        if "inverseBindMatrices" not in skin:
            self.report.error(name, "inverse bind matrices are required")
            return None
        try:
            parents = glb.parents()
            cache = {}
            worlds = [glb.world(j, parents, cache) for j in joints]
            inverse = glb.accessor(skin["inverseBindMatrices"])
        except GlbError as error:
            self.report.error(name, str(error))
            return None
        if inverse.shape != (len(joints), 16):
            self.report.error(name, "inverse bind matrix count differs from the joint count")
            return None
        inverse = inverse.reshape(-1, 4, 4).transpose(0, 2, 1)
        meshes = [i for i, node in enumerate(glb.list("nodes")) if "mesh" in node]
        for index in meshes:
            node = glb.list("nodes")[index]
            if node.get("skin") != 0:
                self.report.error(name, f"node {index}: every mesh must use the skin")
                continue
            mesh_world = glb.world(index, parents, cache)
            error = max(float(np.abs(w @ m - mesh_world).max()) for w, m in zip(worlds, inverse))
            if error > INVERSE_BIND:
                self.report.error(
                    name,
                    f"inverse bind matrices differ from the joints by {error:.3g} (limit {INVERSE_BIND}); export at rest position",
                )
            for primitive in glb.list("meshes")[node["mesh"]].get("primitives", []):
                attributes = primitive.get("attributes", {})
                if "JOINTS_0" not in attributes or "WEIGHTS_0" not in attributes:
                    continue
                if "JOINTS_1" in attributes or "WEIGHTS_1" in attributes:
                    self.report.error(name, "at most four influences for each vertex")
                try:
                    indices = glb.accessor(attributes["JOINTS_0"])
                    weights = glb.accessor(attributes["WEIGHTS_0"])
                except GlbError as error:
                    self.report.error(name, str(error))
                    continue
                if indices.shape != weights.shape or indices.shape[1] != 4:
                    self.report.error(name, "invalid JOINTS_0 or WEIGHTS_0")
                    continue
                if np.any(indices >= len(joints)):
                    self.report.error(name, "JOINTS_0 refers to a joint outside the skin")
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
                target = channel.get("target", {})
                path = target.get("path")
                sampler = (
                    samplers[channel["sampler"]]
                    if type(channel.get("sampler")) is int and channel["sampler"] < len(samplers)
                    else None
                )
                if sampler is None:
                    self.report.error(name, f"animation {index}: invalid sampler")
                    continue
                if sampler.get("interpolation", "LINEAR") not in ("LINEAR", "STEP"):
                    self.report.error(name, f"animation {index}: use LINEAR or STEP interpolation")
                if target.get("node") not in joints:
                    self.report.error(name, f"animation {index}: channels must target skin joints")
                    continue
                if path == "scale":
                    try:
                        values = glb.accessor(sampler["output"])
                    except (GlbError, KeyError) as error:
                        self.report.error(name, f"animation {index}: {error}")
                        continue
                    if np.abs(values - 1).max() > UNIT_SCALE:
                        self.report.error(
                            name, f"animation {index}: scale keys must be within {UNIT_SCALE} of 1 (they are dropped)"
                        )
                elif path not in ("translation", "rotation"):
                    self.report.error(name, f"animation {index}: '{path}' channels are not supported")
        if len(set(names)) != len(names):
            self.report.error(name, "animation names must be unique")
        if len(names) > 256:
            self.report.error(name, "at most 256 animation clips")
        return names


def validate(path, library=None, character_library=None):
    return Validator(path, library, character_library=character_library).run()


def main(argv=None):
    parser = argparse.ArgumentParser(prog="python -m pipeline.package.validate", description=__doc__.splitlines()[0])
    parser.add_argument("package", type=Path, nargs="+")
    parser.add_argument(
        "--library",
        type=Path,
        help="library package (gh3-band-v1.chlib) or current library folder for the character profile check",
    )
    parser.add_argument(
        "--character-library",
        type=Path,
        help="character library package (gh3-characters-v1.chlib) or folder for the profile check of characters on its profiles",
    )
    parser.add_argument("--json", action="store_true", help="print one JSON report for each package")
    args = parser.parse_args(argv)
    failed = False
    for package in args.package:
        report = validate(package, args.library, args.character_library)
        failed |= not report.ok
        if args.json:
            print(json.dumps({"package": str(package), "errors": report.errors, "warnings": report.warnings}))
            continue
        print(
            f"{package}: {'valid' if report.ok else 'INVALID'} ({len(report.errors)} errors, {len(report.warnings)} warnings)"
        )
        for message in report.errors:
            print(f"  error: {message}")
        for message in report.warnings:
            print(f"  warning: {message}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
