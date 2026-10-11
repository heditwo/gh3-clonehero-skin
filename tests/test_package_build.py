import gzip
import json
import tempfile
import unittest
from pathlib import Path

import numpy as np

from pipeline.gh3.formats import key
from pipeline.package import build, roundtrip, runtime
from pipeline.package.validate import Validator
from pipeline.stagepack import template
from pipeline.stagepack.gltf import SKINNED_GH3_VERTEX, STATIC_VERTEX, GlbReader, GlbWriter
from pipeline.stagepack.pack import compile_stage, load_json
from tests.test_package_format import png

try:
    import brotli
except ImportError:
    brotli = None


def write(path, data):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data if isinstance(data, bytes) else (json.dumps(data, indent=2) + "\n").encode())


def gh3(textures):
    return {
        "template": "11112222",
        "vertexConstants": [[1.0, 0.0, 0.0, 0.0]],
        "pixelConstants": [],
        "textures": textures,
        "addressing": [0] * len(textures),
        "flags": 0,
        "blend": 0,
        "alphaRef": 0,
        "drawOrder": 0.0,
    }


def skinned_model(folder, material, name=None, color=None):
    vertices = np.zeros(3, dtype=SKINNED_GH3_VERTEX)
    vertices["position"] = [[0, 0, 0], [1, 0, 0], [0, 1, 0]]
    vertices["normal"] = [0, 0, 1]
    vertices["color"] = 200
    vertices["uv"] = [[0, 0.25], [1, 0.5], [0, 0.75]]
    vertices["gh3uv"][:, 1] = [[0.1, 0.2]] * 3
    vertices["joints"] = [[0, 1, 0, 0]] * 3
    vertices["weights"] = [[0.5, 0.5, 0, 0]] * 3
    write(folder / "meshes.bin", runtime.write_meshes([(material, 3, vertices.tobytes(), [[0, 1, 2]])]))
    definition = {
        "textures": ["0000aaaa"],
        "template": "11112222",
        "blend": 3,
        "cutoff": 0.0625,
        "doubleSided": False,
        "gh3": {**gh3(["0000aaaa"]), "blend": 3, "alphaRef": 16},
        "shading": "lit",
        "texture": "0000aaaa",
    }
    if color:
        definition["color"] = color
    write(
        folder / "stage.json",
        {
            "version": 1,
            "name": name or folder.name,
            "cameraPosition": [0, 0, 0],
            "cameraTarget": [0, 1, 0],
            "materials": {
                material: definition,
                "00000000": {"blend": 0, "cutoff": 0.0, "doubleSided": False, "gh3": gh3([]), "texture": ""},
            },
        },
    )
    write(folder / "textures/0000aaaa.png", png(2, 2))
    write(folder / "textures/0000bbbb.png", png(3, 3))
    return vertices


def rig():
    parents = [-1, 0]
    bind = np.array([[0, 1, 0, 0, 0, 0, 1], [0, 0.5, 0.25, 0, 0.6, 0, 0.8]], dtype=np.float32)
    frames = np.repeat(bind[None], 31, 0).copy()
    frames[:, 1, 3:] = [[0, np.sin(t), 0, np.cos(t)] for t in np.linspace(0, 1, 31)]
    return parents, bind, [(1.0, frames)]


def clip_bytes():
    poses = np.zeros((31, 2, 7), dtype=np.float32)
    poses[:, 0, :3] = np.linspace(0, 1, 31)[:, None] * [1, 0, 0.5]
    poses[:, :, 3:] = [0, 0, 0, 1]
    poses[:, 1, 3:] = [[0, np.sin(t), 0, np.cos(t)] for t in np.linspace(0, 1, 31)]
    return gzip.compress(runtime.write_clip(2, 1.0, [(0, 3), (1, 2)], poses), mtime=0)


def library(root):
    """A small library folder in the current runtime layout, with every part that the converter reads."""
    root = Path(root)
    clip = {"duration": 1.0, "frames": 31, "rootDistance": 0.0}
    member = {
        "bones": ["0000000a", "0000000b"],
        "groups": {"idle": ["c1c1c1c1"]},
        "layers": {},
        "clips": {"c1c1c1c1": clip},
    }
    write(root / "catalog.json", {"version": 1, "library": "gh3-band-v1", "members": {"singer": member}})
    write(
        root / "direction/performance.json",
        {"version": 1, "members": {"singer": {**member, "missingClips": []}}, "constants": {"slow_tempo": 75.0}},
    )
    write(root / "direction/singer/c1c1c1c1.bin.gz", clip_bytes())
    write(
        root / "band/band.json",
        {
            "version": 1,
            "members": [
                {
                    "role": "singer",
                    "model": "Singer_Male",
                    "instrument": "Microphone001",
                    "position": [0, 0, 0],
                    "yaw": 0.0,
                    "clips": ["idle"],
                    "bones": 2,
                    "vertices": 3,
                }
            ],
        },
    )
    write(root / "band/hair.json", {"version": 1, "members": {}})
    skinned_model(root / "band/singer", "0000abcd", "Singer_Male", color=[1, 1, 1, 1])
    write(root / "band/singer/animation.bin", runtime.write_animation(*rig()))
    write(
        root / "crowd/crowd.json",
        {
            "version": 2,
            "models": ["crowd_a"],
            "members": {"crowd1": {"model": "crowd_a", "groups": {"idle/bad/slow": ["d1d1d1d1"]}}},
            "bones": ["0000000a", "0000000b"],
            "hands": ["left-clap"],
            "performanceClips": {"d1d1d1d1": clip},
            "constants": {},
            "clips": {"crowd_a": ["cheer"]},
        },
    )
    skinned_model(root / "crowd/crowd_a", "0000cdef", color=[0.5, 0.25, 1, 1])
    write(root / "crowd/crowd_a/animation.bin", runtime.write_animation(*rig()))
    skinned_model(root / "crowd/hands/left-clap", "0000beef")
    write(root / "crowd/clips/d1d1d1d1.bin.gz", clip_bytes())
    emitter = {
        "name": "flames",
        "texture": "flames.png",
        "textureId": "1bd73987",
        "scriptSha256": "0" * 64,
        "imageSha256": "1" * 64,
        "rate": 24.0,
        "life": [0.3, 0.4],
        "speed": [0.3, 0.6],
        "size": [[0.2, 0.2], [0, 0]],
        "spread": 45.0,
        "range": [0, 0, 0],
        "force": [0, 0.2, 0],
        "direction": [0, 1, 0],
        "follow": 0.0,
        "color": [1, 1, 1, 1],
        "colors": [[1, 1, 1, 1]],
        "atlas": 2,
        "fps": 15,
    }
    write(root / "hand-fire/hand-fire.json", {"version": 1, "bones": ["0000000a", "0000000b"], "emitters": [emitter]})
    write(root / "hand-fire/flames.png", png(4, 4))
    pcm = np.arange(8, dtype="<i2").tobytes()
    (root / "audio").mkdir(parents=True)
    # As pipeline.library.audio writes it: the gzip header holds the file name.
    with (root / "audio/5a5a5a5a.pcm.gz").open("wb") as file, gzip.GzipFile(fileobj=file, mode="wb", mtime=0) as stream:
        stream.write(pcm)
    write(
        root / "audio/audio.json",
        {
            "version": 1,
            "definitions": {"41aca0a3": [1, 90]},
            "countoffs": {},
            "constants": {},
            "samples": {
                "5a5a5a5a": {"rate": 22050, "channels": 2, "frames": 4, "loopStart": 0, "loopEnd": 4, "loop": False}
            },
        },
    )
    write(root / "particle-textures/1234abcd.png", png(2, 2))
    write(root / "movies/clip.gh3video", runtime.write_movie(2, 2, [30, 1], [png(2, 2), png(2, 2)]))
    write(
        root / "scripts.json",
        {
            "version": 1,
            "definitions": {},
            "startup": [],
            "textures": {"0x1234abcd": "particle-textures/1234abcd.png"},
            "movies": {"clip": "movies/clip.gh3video"},
        },
    )
    write(
        root / "render/particle-materials.json",
        {"version": 1, "lit": [{"name": "ParticleQuickMaterial", "sortId": 0, "flags": 0}], "unlit": []},
    )
    write(
        root / "render/tesla-material.json",
        {
            "material": "01488227",
            "shader": "d7287426",
            "texture": "e936482c",
            "sourceSha256": "2" * 64,
            "imageSha256": "3" * 64,
            "size": [4, 4],
        },
    )
    write(root / "render/tesla-noise.png", png(4, 4))
    write(root / "render/stage.bundle", b"platform data")
    events = root.parent / "events"
    write(events / "index.json", {"version": 1, "songs": [{"title": "Song", "artist": "Artist", "file": "song.json"}]})
    write(
        events / "song.json",
        {
            "version": 1,
            "offsetSeconds": 0,
            "sourceSong": "song",
            "sourceSha256": "4" * 64,
            "events": [{"time": 0, "type": "beat"}],
            "playback": [],
        },
    )
    write(events / "export-report.json", {"exported": 1})
    return root, events


class ImageNameTests(unittest.TestCase):
    def test_writer_names_images_and_references_them(self):
        from PIL import Image

        writer = GlbWriter()
        red, blue = Image.new("RGBA", (2, 2), (255, 0, 0, 255)), Image.new("RGBA", (2, 2), (0, 0, 255, 255))
        self.assertEqual(writer.reference(red, "0000aaaa"), "#0000aaaa")
        self.assertEqual(writer.reference(red, "0000bbbb"), "#0000aaaa")
        self.assertEqual(writer.reference(blue, "0000aaaa"), "#0000aaaa-1")
        self.assertEqual(writer.texture(png(), None), 2)
        self.assertEqual([image["name"] for image in writer.data["images"]], ["0000aaaa", "0000aaaa-1", "image-2"])

    def test_names_paths_and_old_indices_compile_alike(self):
        from PIL import Image

        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "source"
            template.create(source)
            vertices = np.zeros(3, dtype=STATIC_VERTEX)
            vertices["position"] = [[0, 0, 0], [1, 0, 0], [0, 1, 0]]
            vertices["normal"] = [0, 0, 1]
            vertices["color"] = 255
            images = {
                "0000aaaa": Image.new("RGBA", (4, 4), (10, 20, 30, 255)),
                "0000bbbb": Image.new("RGBA", (4, 4), (40, 50, 60, 255)),
            }
            materials = {
                "templated": {
                    "blend": 0,
                    "texture": "0000aaaa",
                    "shading": "lit",
                    "mask": "0000bbbb",
                    "gh3": gh3(["0000aaaa", "0000bbbb", "missing"]),
                }
            }
            writer = GlbWriter()
            writer.data = GlbReader(source / "stage.glb").data
            writer.binary = bytearray(GlbReader(source / "stage.glb").binary)
            writer.node(
                "templated",
                writer.mesh("templated", [("templated", 3, vertices.tobytes(), [(0, 1, 2)])], materials, images),
            )
            writer.save(source / "stage.glb")
            extras = next(m for m in GlbReader(source / "stage.glb").data["materials"] if m["name"] == "templated")[
                "extras"
            ]
            self.assertEqual(
                (extras["gh3MaskTexture"], extras["gh3Shader"]["textures"]),
                ("#0000bbbb", ["#0000aaaa", "#0000bbbb", ""]),
            )
            compile_stage(source, root / "named")
            data = GlbReader(source / "stage.glb").data
            indices = {image["name"]: i for i, image in enumerate(data["images"])}
            material = next(m for m in data["materials"] if m["name"] == "templated")
            material["extras"]["gh3MaskTexture"] = indices["0000bbbb"]
            material["extras"]["gh3Shader"]["textures"] = [indices["0000aaaa"], "textures/b.png", -1]
            write(source / "textures/b.png", png(4, 4))
            writer = GlbWriter()
            writer.data = data
            writer.binary = bytearray(GlbReader(source / "stage.glb").binary)
            writer.save(source / "stage.glb")
            compile_stage(source, root / "mixed")
            named = {m["source"]: m for m in load_json(root / "named/stage.json")["materials"].values()}["templated"]
            mixed = {m["source"]: m for m in load_json(root / "mixed/stage.json")["materials"].values()}["templated"]
            self.assertEqual(named["mask"], mixed["mask"])
            self.assertEqual(named["gh3"]["textures"][0], mixed["gh3"]["textures"][0])
            self.assertEqual((named["gh3"]["textures"][2], mixed["gh3"]["textures"][2]), ("", ""))
            self.assertTrue((root / "mixed/textures" / (mixed["gh3"]["textures"][1] + ".png")).is_file())

    def test_named_image_without_gltf_texture_compiles(self):
        from PIL import Image

        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "source"
            template.create(source)
            vertices = np.zeros(3, dtype=STATIC_VERTEX)
            vertices["position"] = [[0, 0, 0], [1, 0, 0], [0, 1, 0]]
            vertices["normal"] = [0, 0, 1]
            vertices["color"] = 255
            images = {"0000aaaa": Image.new("RGBA", (4, 4), (10, 20, 30, 255))}
            materials = {"masked": {"blend": 0, "texture": "0000aaaa", "shading": "lit", "gh3": gh3(["0000aaaa"])}}
            writer = GlbWriter()
            writer.data = GlbReader(source / "stage.glb").data
            writer.binary = bytearray(GlbReader(source / "stage.glb").binary)
            writer.node(
                "masked", writer.mesh("masked", [("masked", 3, vertices.tobytes(), [(0, 1, 2)])], materials, images)
            )
            writer.save(source / "stage.glb")
            # Blender writes a sampler-only image as an image without a glTF texture (PACKAGE-FORMAT.md section 6.6).
            data = GlbReader(source / "stage.glb").data
            data["images"].append(dict(data["images"][-1], name="sampler-only"))
            material = next(m for m in data["materials"] if m["name"] == "masked")
            material["extras"]["gh3MaskTexture"] = "#sampler-only"
            material["extras"]["gh3Shader"]["textures"] = ["#sampler-only"]
            writer = GlbWriter()
            writer.data = data
            writer.binary = bytearray(GlbReader(source / "stage.glb").binary)
            writer.save(source / "stage.glb")
            self.assertIsNone(GlbReader(source / "stage.glb").named_texture("sampler-only"))
            compile_stage(source, root / "out")
            compiled = {m["source"]: m for m in load_json(root / "out/stage.json")["materials"].values()}["masked"]
            expected = f"{key('image-' + str(len(data['images']) - 1)):08x}"
            self.assertEqual((compiled["mask"], compiled["gh3"]["textures"]), (expected, [expected]))
            self.assertEqual(
                Image.open(root / "out/textures" / (expected + ".png")).getpixel((0, 0)), (10, 20, 30, 255)
            )


class VenueBuildTests(unittest.TestCase):
    def test_venue_package_is_valid_reproducible_and_round_trips(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "example.studio"
            template.create(source)
            (root / "out").mkdir()
            (root / "again").mkdir()
            folder, archive = build.build_venue(source, root / "out")
            _, again = build.build_venue(source, root / "again")
            self.assertEqual(archive.read_bytes(), again.read_bytes())
            for package in (folder, archive):
                report = Validator(package).run()
                self.assertEqual((report.errors, report.warnings), ([], []))
            compile_stage(source, root / "stages/example.studio")
            result = roundtrip.venue_round_trip(archive, root / "stages/example.studio")
            self.assertTrue(result["ok"], result)
            self.assertEqual(result["byteEqual"], result["files"])

    def test_rejects_texture_indices(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "pack"
            template.create(source)
            data = GlbReader(source / "stage.glb").data
            data["materials"][0]["extras"] = {"gh3MaskTexture": 0}
            writer = GlbWriter()
            writer.data = data
            writer.binary = bytearray(GlbReader(source / "stage.glb").binary)
            writer.save(source / "stage.glb")
            with self.assertRaisesRegex(ValueError, "texture indices"):
                build.build_venue(source, root)


@unittest.skipIf(brotli is None, "brotli is not installed")
class LibraryBuildTests(unittest.TestCase):
    def test_library_package_is_valid_reproducible_and_round_trips(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source, events = library(root / "current/gh3-band-v1")
            (root / "out").mkdir()
            (root / "again").mkdir()
            (folder, archive), clips = build.build_library(source, events, root / "out", jobs=1)
            (_, again), _ = build.build_library(source, events, root / "again", jobs=1)
            self.assertEqual(archive.read_bytes(), again.read_bytes())
            self.assertEqual(len(clips), 2)
            self.assertEqual(
                sorted(p.name for p in (folder / "movies/clip/frames").iterdir()), ["000000.png", "000001.png"]
            )
            self.assertEqual(
                json.loads((folder / "audio/audio.json").read_text())["samples"]["5a5a5a5a"]["file"],
                "audio/5a5a5a5a.wav",
            )
            for package in (folder, archive):
                report = Validator(package).run()
                self.assertEqual((report.errors, report.warnings), ([], []))
            result = roundtrip.library_round_trip(archive, source, events)
            self.assertTrue(result["ok"], json.dumps(result, indent=1))
            self.assertTrue(all(m["meshesBinByteEqual"] and m["texturesByteEqual"] for m in result["models"].values()))
            self.assertEqual(result["audio"]["gzipByteEqual"], 1)
            self.assertEqual(
                result["notCarried"]["unreferenced model textures (the runtime loads textures by material reference)"],
                3,
            )
            # The validator reads the profile rig from the package for the character check.
            from pipeline.package.profiles import load_profile

            names, parents, _, rotations = load_profile(archive, "singer")
            self.assertEqual((names, parents), (["0000000a", "0000000b"], [-1, 0]))
            self.assertLess(np.abs(np.abs(rotations[1]) - [0, 0.6, 0, 0.8]).max(), 1e-6)

    def test_library_schemas_agree_with_reference_implementation(self):
        try:
            import jsonschema
            import referencing
        except ImportError:
            self.skipTest("jsonschema is not installed")
        from pipeline.package.schema import SCHEMA_DIR, Schemas
        from pipeline.package.validate_library import EFFECT_SCHEMAS, LIBRARY_SCHEMAS

        documents = [json.loads(p.read_text()) for p in SCHEMA_DIR.glob("*.schema.json")]
        registry = referencing.Registry().with_resources(
            [(d["$id"], referencing.Resource.from_contents(d)) for d in documents]
        )
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source, events = library(root / "current/gh3-band-v1")
            (root / "out").mkdir()
            (folder, _), _ = build.build_library(source, events, root / "out", jobs=1)
            entry = json.loads((folder / "manifest.json").read_text())["entry"]
            pairs = (
                [(entry[key], schema) for key, schema in LIBRARY_SCHEMAS.items()]
                + list(EFFECT_SCHEMAS.items())
                + [("movies/clip/movie.json", "movie.schema.json"), ("songs/song.json", "song.schema.json")]
            )
            cases = []
            for name, schema in pairs:
                value = json.loads((folder / name).read_text())
                cases += [(schema, value, True), (schema, {**value, "unknown": 1}, False)]
        schemas = Schemas()
        for schema, value, valid in cases:
            reference = jsonschema.Draft202012Validator(
                json.loads((SCHEMA_DIR / schema).read_text()), registry=registry
            ).is_valid(value)
            self.assertEqual(reference, valid, schema)
            self.assertEqual(not schemas.validate(value, schema), reference, schema)

    def test_validator_finds_missing_library_files(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source, events = library(root / "current/gh3-band-v1")
            (root / "out").mkdir()
            (folder, _), _ = build.build_library(source, events, root / "out", jobs=1)
            manifest = json.loads((folder / "manifest.json").read_text())
            manifest["files"] = {}
            write(folder / "manifest.json", manifest)
            for name in ("direction/singer/c1c1c1c1.q48.br", "movies/clip/frames/000001.png", "audio/5a5a5a5a.wav"):
                (folder / name).unlink()
            errors = Validator(folder).run().errors
            self.assertIn("direction/performance.json: clip file is missing: direction/singer/c1c1c1c1.q48.br", errors)
            self.assertIn("audio/audio.json: audio sample file is missing: audio/5a5a5a5a.wav", errors)
            self.assertTrue(any("movies/clip/movie.json: frames" in e for e in errors))


if __name__ == "__main__":
    unittest.main()
