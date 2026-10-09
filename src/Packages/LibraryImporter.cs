using System.Buffers.Binary;
using System.Text.Json.Nodes;
using static Gh3Skin.Packages.PackageJson;

namespace Gh3Skin.Packages;

// Library package (`.chlib`, PACKAGE-FORMAT.md section 10) to the runtime library folder that the mod loads. The C# side of
// pipeline/package/roundtrip.py library_round_trip: model GLBs become GH3BND02 meshes, GH3ANM01 rigs, and stage.json;
// movies become GH3VID01; WAV samples become raw PCM (`<sample>.pcm`). Clips stay q48 + Brotli (`.q48.br`) and are decoded
// by the clip prefetch (PerformanceClip). Songs go to `songs/`, the effect files to `render/`. A character library keeps
// characters.json and `clips/`.
internal sealed class LibraryImporter
{
    private static readonly Dictionary<string, string> EffectFiles = new()
    {
        ["effects/particle-materials.json"] = "render/particle-materials.json",
        ["effects/tesla-material.json"] = "render/tesla-material.json",
        ["effects/tesla-noise.png"] = "render/tesla-noise.png",
    };
    private readonly VerifiedPackage _package;
    private readonly TextureJobs _textures;

    public LibraryImporter(VerifiedPackage package, TextureJobs textures)
    {
        _package = package;
        _textures = textures;
    }

    private static string Target(string folder, string path) => Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar));

    public (int Models, int Clips, int Samples, int Movies) Import(string folder)
    {
        var copied = new HashSet<string>(StringComparer.Ordinal);
        void Copy(string name, string? target = null)
        {
            _package.CopyTo(name, Target(folder, target ?? name));
            copied.Add(name);
        }
        foreach (var name in new[] { "catalog.json", "band/band.json", "band/hair.json", "direction/performance.json", "crowd/crowd.json", "hand-fire/hand-fire.json" })
            if (_package.Contains(name)) Copy(name);
        foreach (var (name, target) in EffectFiles)
            if (_package.Contains(name)) Copy(name, target);
        foreach (var name in _package.Names.Where(n => (n.StartsWith("hand-fire/") || n.StartsWith("particle-textures/")) && n.EndsWith(".png")).ToList()) Copy(name);

        var movies = 0;
        if (_package.Contains("scripts.json"))
        {
            var scripts = Object(_package.Read("scripts.json"));
            if (scripts["movies"] is JsonObject bank)
                foreach (var (movie, pathNode) in bank.ToList())
                {
                    var path = Text(pathNode) ?? throw new PackageException("invalid movie path");
                    var stem = path.EndsWith("/movie.json") ? path[..^"/movie.json".Length] : throw new PackageException("invalid movie path");
                    var runtime = stem + ".gh3video";
                    WriteMovie(stem, Target(folder, runtime));
                    bank[movie] = runtime;
                    movies++;
                }
            Write(Path.Combine(folder, "scripts.json"), scripts);
        }

        var samples = 0;
        if (_package.Contains("audio/audio.json"))
        {
            var audio = Object(_package.Read("audio/audio.json"));
            foreach (var (sample, rowNode) in (JsonObject)audio["samples"]!)
            {
                var row = (JsonObject)rowNode!;
                var file = Text(row["file"]) ?? throw new PackageException($"audio sample {sample} has no file");
                row.Remove("file");
                var (pcm, rate, channels) = Wave(_package.Read(file), file);
                if (rate != Number(row["rate"], "rate") || channels != Number(row["channels"], "channels") || pcm.Length != Number(row["frames"], "frames") * channels * 2)
                    throw new PackageException($"audio sample {sample} differs from audio.json");
                Directory.CreateDirectory(Path.Combine(folder, "audio"));
                File.WriteAllBytes(Path.Combine(folder, "audio", sample + ".pcm"), pcm);
                samples++;
            }
            Write(Path.Combine(folder, "audio", "audio.json"), audio);
        }

        var models = 0;
        if (_package.Contains("band/band.json"))
        {
            var band = Object(_package.Read("band/band.json"));
            foreach (var member in (JsonArray)band["members"]!)
            {
                var role = Text(member!["role"]) ?? throw new PackageException("invalid band member");
                ImportModel($"band/{role}/model.glb", Target(folder, $"band/{role}"), animated: true);
                models++;
            }
        }
        // A character library (format 1.1, section 10.1): characters.json and its clips, copied.
        if (_package.Manifest.Entry.TryGetValue("characters", out var characters))
        {
            if (characters != CharacterLibrary.FileName) throw new PackageException($"the characters entry must be {CharacterLibrary.FileName}");
            Copy(characters);
        }
        if (_package.Contains("crowd/crowd.json"))
        {
            var crowd = Object(_package.Read("crowd/crowd.json"));
            foreach (var model in Keys(crowd["models"]))
            {
                ImportModel($"crowd/{model}/model.glb", Target(folder, $"crowd/{model}"), animated: true);
                models++;
            }
            foreach (var hand in Keys(crowd["hands"]))
            {
                ImportModel($"crowd/hands/{hand}/model.glb", Target(folder, $"crowd/hands/{hand}"), animated: false);
                models++;
            }
        }

        var clips = 0;
        foreach (var name in _package.Names.Where(n => n.EndsWith(".q48.br") && (n.StartsWith("direction/") || n.StartsWith("crowd/clips/") || n.StartsWith("clips/"))).ToList())
        {
            Copy(name);
            clips++;
        }
        foreach (var name in _package.Names.Where(n => n.StartsWith("songs/") && n.EndsWith(".json")).ToList()) Copy(name);
        _package.VerifyRemaining();
        return (models, clips, samples, movies);
    }

    private static IEnumerable<string> Keys(JsonNode? node) => node switch
    {
        JsonObject value => value.Select(pair => pair.Key).ToList(),
        JsonArray list => list.Select(item => Text(item) ?? throw new PackageException("invalid library name")).ToList(),
        _ => System.Array.Empty<string>(),
    };

    private void WriteMovie(string stem, string target)
    {
        var info = Object(_package.Read(stem + "/movie.json"));
        var width = (int)Integer(info["width"], 1, 4096, "movie width");
        var height = (int)Integer(info["height"], 1, 4096, "movie height");
        var count = (int)Integer(info["frames"], 1, 100000, "movie frames");
        var rate = (JsonArray)info["rate"]!;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var names = Enumerable.Range(0, count).Select(i => $"{stem}/frames/{i:000000}.png").ToArray();
        var headerSize = 28 + 8 * (count + 1);
        using var output = new BinaryWriter(new BufferedStream(File.Create(target), 1 << 20));
        output.Write(System.Text.Encoding.ASCII.GetBytes("GH3VID01"));
        output.Write(width);
        output.Write(height);
        output.Write(count);
        output.Write((int)Integer(rate[0], 1, uint.MaxValue, "movie rate"));
        output.Write((int)Integer(rate[1], 1, uint.MaxValue, "movie rate"));
        long offset = headerSize;
        output.Write(offset);
        foreach (var name in names)
        {
            offset += _package.Source.Size(name);
            output.Write(offset);
        }
        foreach (var name in names) output.Write(_package.Read(name));
    }

    private static (byte[] Pcm, int Rate, int Channels) Wave(byte[] data, string name)
    {
        if (data.Length < 12 || System.Text.Encoding.ASCII.GetString(data, 0, 4) != "RIFF" || System.Text.Encoding.ASCII.GetString(data, 8, 4) != "WAVE")
            throw new PackageException($"{name}: only WAV audio is supported by this importer");
        int rate = 0, channels = 0, bits = 0;
        for (var at = 12; at + 8 <= data.Length;)
        {
            var id = System.Text.Encoding.ASCII.GetString(data, at, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at + 4));
            if (size < 0 || at + 8L + size > data.Length) throw new PackageException($"{name}: invalid WAV chunk");
            if (id == "fmt ")
            {
                if (BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(at + 8)) != 1) throw new PackageException($"{name}: WAV must be PCM");
                channels = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(at + 10));
                rate = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at + 12));
                bits = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(at + 22));
            }
            else if (id == "data")
            {
                if (bits != 16 || channels is < 1 or > 2) throw new PackageException($"{name}: WAV must be 16-bit mono or stereo");
                return (data.AsSpan(at + 8, size).ToArray(), rate, channels);
            }
            at += 8 + size + (size & 1);
        }
        throw new PackageException($"{name}: WAV has no data");
    }

    // pipeline/package/roundtrip.py model_runtime.
    private void ImportModel(string path, string folder, bool animated)
    {
        var glb = new GlbDocument(_package.Read(path));
        var nodes = glb.List("nodes");
        var joints = ((JsonArray)((JsonObject)glb.List("skins")[0]!)["joints"]!).Select(j => j!.GetValue<int>()).ToArray();
        var hierarchy = glb.Parents();
        var parents = joints.Select(j => hierarchy.TryGetValue(j, out var p) ? System.Array.IndexOf(joints, p) : -1).ToArray();
        float[] Pose(JsonNode? translation, JsonNode? rotation)
        {
            var t = Vector(translation, 3, "joint translation");
            var q = Vector(rotation, 4, "joint rotation");
            return new[] { (float)t[0], (float)t[1], (float)-t[2], (float)-q[0], (float)-q[1], (float)q[2], (float)q[3] };
        }
        var bind = joints.Select(j => Pose(nodes[j]!["translation"], nodes[j]!["rotation"])).ToArray();
        Directory.CreateDirectory(folder);
        if (animated)
        {
            using var writer = new BinaryWriter(File.Create(Path.Combine(folder, "animation.bin")));
            var animations = glb.List("animations");
            writer.Write(System.Text.Encoding.ASCII.GetBytes("GH3ANM01"));
            writer.Write(joints.Length);
            writer.Write(animations.Count);
            for (var b = 0; b < joints.Length; b++)
            {
                writer.Write(parents[b]);
                foreach (var value in bind[b]) writer.Write(value);
            }
            foreach (var animation in animations)
            {
                var samplers = (JsonArray)animation!["samplers"]!;
                var channels = new Dictionary<(int, string), int>();
                foreach (var channel in (JsonArray)animation["channels"]!)
                    channels[(channel!["target"]!["node"]!.GetValue<int>(), Text(channel["target"]!["path"])!)] = samplers[channel["sampler"]!.GetValue<int>()]!["output"]!.GetValue<int>();
                var times = glb.Accessor(samplers[0]!["input"]!.GetValue<int>(), normalize: false).Values;
                var frames = times.Length;
                var tracks = joints.Select(j => (glb.Accessor(channels[(j, "translation")], false), glb.Accessor(channels[(j, "rotation")], false))).ToArray();
                writer.Write((float)times[^1]);
                writer.Write(frames);
                for (var f = 0; f < frames; f++)
                    for (var b = 0; b < joints.Length; b++)
                    {
                        var (t, r) = tracks[b];
                        writer.Write((float)t[f, 0]); writer.Write((float)t[f, 1]); writer.Write((float)-t[f, 2]);
                        writer.Write((float)-r[f, 0]); writer.Write((float)-r[f, 1]); writer.Write((float)r[f, 2]); writer.Write((float)r[f, 3]);
                    }
            }
        }
        var model = Enumerable.Range(0, nodes.Count).First(i => nodes[i]!["mesh"] != null);
        var materials = glb.List("materials");
        var parts = new List<MeshPart>();
        foreach (var primitive in (JsonArray)glb.Item("meshes", nodes[model]!["mesh"]!.GetValue<int>())["primitives"]!)
        {
            var attributes = (JsonObject)primitive!["attributes"]!;
            GlbAccessor Raw(string name) => glb.Accessor(attributes[name]!.GetValue<int>(), normalize: false);
            var positions = Raw("POSITION");
            var count = positions.Count;
            var normals = Raw("NORMAL");
            var uv = Raw("TEXCOORD_0");
            var color = Raw("COLOR_0");
            var joint = Raw("JOINTS_0");
            var weight = Raw("WEIGHTS_0");
            var streams = attributes.ContainsKey("_GH3_UV0");
            var gh3Uv = streams ? Enumerable.Range(0, 6).Select(i => Raw($"_GH3_UV{i}")).ToArray() : null;
            var binormal = streams ? Raw("_GH3_BINORMAL") : default;
            var tangent = streams ? Raw("_GH3_TANGENT") : default;
            var vertices = new byte[count * StageGeometry.SkinnedStride];
            for (var v = 0; v < count; v++)
            {
                var offset = v * StageGeometry.SkinnedStride;
                var row = vertices.AsSpan(offset, StageGeometry.SkinnedStride);
                void F(int at, double value) => BinaryPrimitives.WriteSingleLittleEndian(vertices.AsSpan(offset + at), (float)value);
                F(0, positions[v, 0]); F(4, positions[v, 1]); F(8, -positions[v, 2]);
                F(12, normals[v, 0]); F(16, normals[v, 1]); F(20, -normals[v, 2]);
                F(24, uv[v, 0]); BinaryPrimitives.WriteSingleLittleEndian(row[28..], 1f - (float)uv[v, 1]);
                for (var c = 0; c < 4; c++) row[32 + c] = (byte)color[v, c];
                for (var c = 0; c < 4; c++) row[36 + c] = (byte)joint[v, c];
                for (var c = 0; c < 4; c++) F(40 + c * 4, weight[v, c]);
                if (gh3Uv != null)
                {
                    for (var i = 0; i < 6; i++) { F(56 + i * 8, gh3Uv[i][v, 0]); F(60 + i * 8, gh3Uv[i][v, 1]); }
                    F(104, binormal[v, 0]); F(108, binormal[v, 1]); F(112, -binormal[v, 2]);
                    F(116, tangent[v, 0]); F(120, tangent[v, 1]); F(124, -tangent[v, 2]);
                }
            }
            var faces = glb.Accessor(primitive["indices"]!.GetValue<int>(), normalize: false).Values.Select(i => (int)i).ToArray();
            for (var i = 0; i < faces.Length; i += 3) (faces[i + 1], faces[i + 2]) = (faces[i + 2], faces[i + 1]);
            parts.Add(new MeshPart(Text(materials[primitive["material"]!.GetValue<int>()]!["name"])!, count, vertices, faces));
        }
        StageGeometry.WriteMeshes(Path.Combine(folder, "meshes.bin"), parts, skinned: true);
        var images = glb.List("images");
        var textures = glb.List("textures");
        var used = new JsonObject();
        var referenced = new HashSet<string>();
        foreach (var node in materials)
        {
            var material = (JsonObject)node!;
            var pbr = material["pbrMetallicRoughness"] as JsonObject ?? new JsonObject();
            var extras = material["extras"] as JsonObject ?? new JsonObject();
            var shader = extras["gh3Shader"] as JsonObject;
            var mode = Text(material["alphaMode"]) ?? "OPAQUE";
            var texture = pbr["baseColorTexture"] is JsonObject info
                ? Text(images[textures[info["index"]!.GetValue<int>()]!["source"]!.GetValue<int>()]!["name"])! : "";
            var row = new JsonObject
            {
                ["blend"] = shader != null ? Clone(shader["blend"]) : Bool(extras["gh3Additive"]) == true ? 1 : mode == "BLEND" ? 2 : 0,
                ["cutoff"] = mode == "MASK" ? Clone(material["alphaCutoff"]) ?? 0.5 : Clone(extras["gh3AlphaCutoff"]) ?? 0,
                ["doubleSided"] = Bool(material["doubleSided"]) == true,
                ["texture"] = texture,
            };
            referenced.Add(texture);
            if (shader != null)
            {
                var gh3 = (JsonObject)Clone(shader)!;
                gh3["textures"] = new JsonArray(((JsonArray)shader["textures"]!).Select(reference =>
                {
                    var name = Text(reference)!.Length > 0 ? Text(reference)![1..] : "";
                    referenced.Add(name);
                    return (JsonNode?)name;
                }).ToArray());
                row["gh3"] = gh3;
            }
            if (extras["gh3Shading"] != null) row["shading"] = Clone(extras["gh3Shading"]);
            if (Bool(extras["gh3SortedTransparency"]) == true) row["sortedTransparency"] = true;
            if (pbr["baseColorFactor"] is JsonArray factor)
            {
                var scale = extras["gh3ColorScale"] == null ? 1 : Number(extras["gh3ColorScale"], "gh3ColorScale");
                row["color"] = Array(new[] { Number(factor[0], "color") * scale, Number(factor[1], "color") * scale, Number(factor[2], "color") * scale, Number(factor[3], "color") });
            }
            used[Text(material["name"])!] = row;
        }
        Write(Path.Combine(folder, "stage.json"), new JsonObject
        {
            ["version"] = 1, ["name"] = Text(nodes[model]!["name"]) ?? "", ["cameraPosition"] = new JsonArray(0, 0, 0),
            ["cameraTarget"] = new JsonArray(0, 1, 0), ["materials"] = used,
        });
        referenced.Remove("");
        for (var i = images.Count - 1; i >= 0; i--)
        {
            var name = Text(images[i]!["name"]);
            if (name == null || !referenced.Remove(name)) continue;
            var index = i;
            _textures.Add(Path.Combine(folder, "textures", name), () => glb.Image(index));
        }
        _textures.Flush();
    }
}
