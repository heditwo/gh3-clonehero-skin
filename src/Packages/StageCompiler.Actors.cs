using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Gh3Skin.Packages.PackageJson;

namespace Gh3Skin.Packages;

// The venue's scripts and skinned actors: the script bank copy, and the actor GLBs sampled to clips at 30 fps.
internal sealed partial class StageCompiler
{
    // Returns the GH3 zone name of the script bank, or "".
    private string CopyScripts(string path, string folder)
    {
        var scripts = ReadJson(path) as JsonObject ?? throw new PackageException("scripts.json must be an object");
        Fields(scripts, "scripts.json", new[] { "version", "zone", "definitions", "startup", "nodes", "textures", "performanceChanged", "prefixes", "lightVolumes", "movies" }, "version", "definitions");
        var zone = scripts["zone"] == null ? "" : Text(scripts["zone"]) ?? throw new PackageException("invalid stage zone name");
        if (zone.Length > 0 && !Regex.IsMatch(zone, @"\A[A-Za-z0-9_]{1,64}\z")) throw new PackageException("invalid stage zone name");
        if (Number(scripts["version"], "version") != 1 || scripts["definitions"] is not JsonObject definitions || definitions.Count > 50000)
            throw new PackageException("invalid stage script definitions");
        foreach (var (name, value) in definitions)
            if (!Regex.IsMatch(name, @"\A[0-9a-f]{8}\z") || value is not JsonArray { Count: 2 }) throw new PackageException("invalid typed script value");
        if (scripts["startup"] is JsonArray startup && (startup.Count > 256 || startup.Any(n => Text(n) is not { Length: <= 256 }))) throw new PackageException("invalid stage startup scripts");
        Write(Path.Combine(folder, "scripts.json"), scripts);
        if (scripts["textures"] is JsonObject textures)
        {
            if (textures.Count > 2048) throw new PackageException("invalid script texture library");
            foreach (var (name, file) in textures)
            {
                if (name.Length is 0 or > 256 || Text(file) is not { } package || !_package.Contains(package)) throw new PackageException("invalid script texture");
                var target = Path.Combine(folder, package.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(target)) throw new PackageException("script texture replaces another pack file");
                _sources.Add(package);
                _package.CopyTo(package, target);
                Png.DecodeRgba(File.ReadAllBytes(target), out _, out _);
            }
        }
        if (scripts["movies"] is JsonObject movies)
        {
            if (movies.Count > 256) throw new PackageException("invalid stage movie library");
            foreach (var (name, file) in movies)
            {
                if (name.Length is 0 or > 256 || Text(file) is not { } package || !_package.Contains(package)) throw new PackageException("invalid stage movie");
                var target = Path.Combine(folder, package.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(target)) throw new PackageException("stage movie replaces another pack file");
                _sources.Add(package);
                _package.CopyTo(package, target);
                using var stream = File.OpenRead(target);
                var magic = new byte[8];
                if (stream.Read(magic, 0, 8) != 8 || System.Text.Encoding.ASCII.GetString(magic) != "GH3VID01") throw new PackageException("invalid movie frame bank");
            }
        }
        return zone;
    }

    private JsonObject CompileActorEntry(string name, JsonNode actor, string folder)
    {
        Fields(actor, "actor " + name, new[] { "model", "transform", "clip", "lightGroup", "playback", "role", "initiallyVisible", "actions", "object", "script", "created", "scale", "animationEvents" }, "model", "transform");
        Transform(actor["transform"], "actor transform");
        var id = Key(name);
        var model = Text(actor["model"]) ?? throw new PackageException("invalid actor model");
        _sources.Add(model);
        var clips = CompileActor(model, Path.Combine(folder, "actors", id));
        var clip = Text(actor["clip"]) ?? clips[0];
        if (!clips.Contains(clip)) throw new PackageException($"actor {name} has no clip {clip}");
        var playback = Text(actor["playback"]) ?? "loop";
        if (playback is not ("loop" or "hold")) throw new PackageException("invalid actor playback mode");
        var role = actor["role"] == null ? "" : Text(actor["role"]) ?? throw new PackageException("invalid actor role");
        var visible = actor["initiallyVisible"] == null || (Bool(actor["initiallyVisible"]) ?? throw new PackageException("invalid actor visibility"));
        var actions = actor["actions"] as JsonObject ?? new JsonObject();
        var sourceObject = actor["object"] == null ? "" : Text(actor["object"]) ?? throw new PackageException("invalid actor script binding");
        var script = actor["script"] == null ? "" : Text(actor["script"]) ?? throw new PackageException("invalid actor script binding");
        var created = actor["created"] == null || (Bool(actor["created"]) ?? throw new PackageException("invalid actor script binding"));
        if (sourceObject.Length > 256 || script.Length > 256) throw new PackageException("invalid actor script binding");
        if (script.Length > 0 && sourceObject.Length == 0) throw new PackageException("a scripted actor needs an object name");
        var events = actor["animationEvents"] == null ? "" : Text(actor["animationEvents"]) ?? throw new PackageException("invalid animation event table");
        var scale = actor["scale"] == null ? new JsonArray(1, 1, 1) : Clone(actor["scale"])!;
        if (Vector(scale, 3, "actor scale").Any(v => v <= 0)) throw new PackageException("actor scale must be positive");
        if (role.Length > 128 || actions.Count > 256) throw new PackageException("invalid actor role or actions");
        var actionRows = new JsonObject();
        foreach (var (action, choices) in actions)
        {
            if (action.Length == 0 || choices is not JsonArray list || list.Count is < 1 or > 256 || list.Any(c => !clips.Contains(Text(c) ?? "\0")))
                throw new PackageException($"invalid actor action: {action}");
            actionRows[action] = new JsonArray(list.Select(c => (JsonNode?)clips.IndexOf(Text(c)!)).ToArray());
        }
        return new JsonObject
        {
            ["id"] = id, ["clip"] = clips.IndexOf(clip), ["clips"] = new JsonObject(clips.Select((c, i) => KeyValuePair.Create(c, (JsonNode?)i))),
            ["actions"] = actionRows, ["role"] = role, ["initiallyVisible"] = visible, ["playback"] = playback, ["transform"] = Clone(actor["transform"]),
            ["object"] = sourceObject, ["script"] = script, ["created"] = created, ["scale"] = scale, ["animationEvents"] = events,
            ["lightGroup"] = Key(Text(actor["lightGroup"]) ?? "Stage"),
        };
    }

    // pipeline/stagepack/actor.py compile_actor: skinned GH3BND02 meshes, bones.json, and GH3ANM01 clips sampled at 60 fps.
    private List<string> CompileActor(string path, string folder)
    {
        var glb = new GlbDocument(_package.Read(path));
        var nodes = glb.Nodes().ToList();
        var skins = glb.List("skins");
        if (skins.Count != 1) throw new PackageException("an actor GLB needs one skin");
        var skin = (JsonObject)skins[0]!;
        var joints = ((JsonArray)skin["joints"]!).Select(j => j!.GetValue<int>()).ToArray();
        if (joints.Length is < 1 or > 255 || joints.Distinct().Count() != joints.Length) throw new PackageException("invalid actor joints");
        var world = nodes.ToDictionary(n => n.Index, n => n.World);
        if (joints.Any(j => !world.ContainsKey(j))) throw new PackageException("actor joint is outside the scene");
        var parentNodes = new Dictionary<int, int>();
        foreach (var (index, node, _) in nodes)
            if (node["children"] is JsonArray children)
                foreach (var child in children) parentNodes[child!.GetValue<int>()] = index;
        var parents = joints.Select(j => parentNodes.TryGetValue(j, out var p) ? System.Array.IndexOf(joints, p) : -1).ToArray();
        var bind = new double[joints.Length][];
        for (var i = 0; i < joints.Length; i++)
        {
            var local = parents[i] >= 0 ? world[joints[parents[i]]].Inverse() * world[joints[i]] : world[joints[i]];
            var m = local.Upper3();
            for (var a = 0; a < 3; a++)
                for (var b = 0; b < 3; b++)
                {
                    var dot = m[a] * m[b] + m[3 + a] * m[3 + b] + m[6 + a] * m[6 + b];
                    if (Math.Abs(dot - (a == b ? 1 : 0)) > 1e-4 + 1e-5 * (a == b ? 1 : 0)) throw new PackageException("apply actor joint scale before export");
                }
            bind[i] = local.Translation.Concat(Matrix4d.Quaternion(m)).ToArray();
        }
        var inverseValues = glb.Accessor(skin["inverseBindMatrices"]!.GetValue<int>());
        if (inverseValues.Count != joints.Length || inverseValues.Columns != 16) throw new PackageException("actor inverse bind count differs");
        var inverse = Enumerable.Range(0, joints.Length).Select(i =>
        {
            var m = new double[16];
            for (var k = 0; k < 16; k++) m[(k % 4) * 4 + k / 4] = inverseValues.Values[i * 16 + k];
            return new Matrix4d(m);
        }).ToArray();
        var (materials, materialIds, _) = ImportMaterials(glb, folder);
        var meshes = new List<MeshPart>();
        foreach (var (_, node, matrix) in nodes)
        {
            if (node["mesh"] == null) continue;
            if (node["skin"] is not JsonValue skinIndex || skinIndex.GetValue<int>() != 0) throw new PackageException("all actor meshes must use its skin");
            for (var i = 0; i < joints.Length; i++)
                if (!(world[joints[i]] * Matrix4d.Reflection * inverse[i] * Matrix4d.Reflection).Close(matrix, 1e-4))
                    throw new PackageException("actor inverse bind matrices differ from its rig");
            foreach (var primitive in (JsonArray)glb.Item("meshes", node["mesh"]!.GetValue<int>())["primitives"]!)
            {
                var data = StageGeometry.Primitive(glb, (JsonObject)primitive!, matrix);
                var attributes = (JsonObject)primitive!["attributes"]!;
                var weights = glb.Accessor(attributes["WEIGHTS_0"]!.GetValue<int>());
                var indices = glb.Accessor(attributes["JOINTS_0"]!.GetValue<int>());
                if (indices.Count != data.Count || indices.Columns != 4 || weights.Count != data.Count || weights.Columns != 4 ||
                    indices.Values.Any(v => v >= joints.Length) || weights.Values.Any(v => v < 0) ||
                    Enumerable.Range(0, data.Count).Any(v => Math.Abs(weights[v, 0] + weights[v, 1] + weights[v, 2] + weights[v, 3] - 1) > 1e-4 + 1e-5))
                    throw new PackageException("invalid actor skin weights");
                double[]? tangents = null;
                if (attributes["_GH3_TANGENT"] is JsonValue tangentIndex)
                {
                    var source = glb.Accessor(tangentIndex.GetValue<int>());
                    if (source.Count != data.Count || source.Columns != 3) throw new PackageException("invalid GH3 tangent stream");
                    var linear = (matrix * Matrix4d.Reflection).Upper3();
                    tangents = new double[data.Count * 3];
                    for (var v = 0; v < data.Count; v++)
                        for (var r = 0; r < 3; r++)
                            tangents[v * 3 + r] = source[v, 0] * linear[r * 3] + source[v, 1] * linear[r * 3 + 1] + source[v, 2] * linear[r * 3 + 2];
                }
                var material = materialIds[primitive["material"] is JsonValue mi ? mi.GetValue<int>() : materialIds.Count - 1];
                var packed = StageGeometry.PackSkinned(data, indices.Values.Select(v => (int)v).ToArray(), weights.Values, tangents);
                if (data.Faces.Length > 0) meshes.AddRange(StageGeometry.Split(material, packed, StageGeometry.SkinnedStride, data.Faces));
            }
        }
        if (meshes.Count == 0) throw new PackageException("actor has no visible geometry");
        foreach (var mesh in meshes)
        {
            var material = materials[mesh.Material];
            if (Text(material["texture"]) is { Length: > 0 } && Number(material["cutoff"], "cutoff") > 0 && Bool(material["crowd"]) != true && material["gh3"] == null)
                Warnings.Add($"actor material {Text(material["source"])}: alpha-mask mesh trimming is not done by the C# importer");
        }
        _textures.Flush();
        WriteModelDefinition(folder, meshes, materials, skinned: true);
        var bones = new JsonObject();
        for (var index = 0; index < joints.Length; index++)
        {
            var name = Text(glb.Item("nodes", joints[index])["name"]) ?? $"joint-{index}";
            var checksum = Regex.IsMatch(name, @"\Abone-[0-9a-f]{8}\z") ? name[5..] : Key(name);
            if (bones.ContainsKey(checksum)) throw new PackageException("actor bone names have the same checksum");
            bones[checksum] = index;
        }
        Write(Path.Combine(folder, "bones.json"), bones);
        var animations = glb.List("animations");
        if (animations.Count > 256) throw new PackageException("actor has too many animation clips");
        var names = animations.Select((a, i) => Text(a?["name"]) ?? $"clip-{i}").ToList();
        if (names.Distinct().Count() != names.Count) throw new PackageException("duplicate actor animation names");
        var sampled = new List<(double Duration, double[,,] Poses)>();
        foreach (var animation in animations)
            sampled.Add(SampleActorClip(glb, (JsonObject)animation!, joints, parents, parentNodes, world, bind));
        if (sampled.Count == 0)
        {
            names = new List<string> { "bind" };
            var poses = new double[2, joints.Length, 7];
            for (var f = 0; f < 2; f++) for (var b = 0; b < joints.Length; b++) for (var c = 0; c < 7; c++) poses[f, b, c] = bind[b][c];
            sampled.Add((1.0, poses));
        }
        using (var writer = new BinaryWriter(File.Create(Path.Combine(folder, "animation.bin"))))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("GH3ANM01"));
            writer.Write(joints.Length);
            writer.Write(sampled.Count);
            for (var b = 0; b < joints.Length; b++)
            {
                writer.Write(parents[b]);
                foreach (var value in bind[b]) writer.Write((float)value);
            }
            foreach (var (duration, poses) in sampled)
            {
                writer.Write((float)duration);
                writer.Write(poses.GetLength(0));
                var floats = new float[poses.Length];
                var at = 0;
                foreach (var value in poses) floats[at++] = (float)value;
                writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(floats.AsSpan()));
            }
        }
        return names;
    }

    private static (double, double[,,]) SampleActorClip(GlbDocument glb, JsonObject track, int[] joints, int[] parents, Dictionary<int, int> parentNodes,
        Dictionary<int, Matrix4d> world, double[][] bind)
    {
        var samplers = (JsonArray)track["samplers"]!;
        var tracks = new Dictionary<(int, string), (double[] Times, GlbAccessor Values, string Mode)>();
        var duration = 0.0;
        foreach (var channel in (JsonArray)track["channels"]!)
        {
            var target = (JsonObject)channel!["target"]!;
            var sampler = (JsonObject)samplers[channel["sampler"]!.GetValue<int>()]!;
            var node = target["node"]!.GetValue<int>();
            var property = Text(target["path"]) ?? "";
            var mode = Text(sampler["interpolation"]) ?? "LINEAR";
            if (property == "scale" && joints.Contains(node) && mode is "LINEAR" or "STEP")
            {
                var error = glb.Accessor(sampler["output"]!.GetValue<int>()).Values.Select(v => Math.Abs(v - 1)).DefaultIfEmpty(0).Max();
                if (error > 1e-4) throw new PackageException($"actor clip '{Text(track["name"])}' scales a joint (|scale - 1| = {error:g3}); animate only joint translation and rotation");
                continue;
            }
            if (!joints.Contains(node) || property is not ("translation" or "rotation") || mode is not ("LINEAR" or "STEP"))
                throw new PackageException("bake actor animation to joint translation/rotation with LINEAR or STEP interpolation");
            if (tracks.ContainsKey((node, property))) throw new PackageException("duplicate actor animation channel");
            var times = glb.Accessor(sampler["input"]!.GetValue<int>()).Values;
            var values = glb.Accessor(sampler["output"]!.GetValue<int>());
            for (var i = 1; i < times.Length; i++) if (times[i] <= times[i - 1]) throw new PackageException("invalid actor key times");
            if (times[0] < 0 || times.Length != values.Count) throw new PackageException("invalid actor key times");
            if (values.Columns != (property == "rotation" ? 4 : 3)) throw new PackageException("invalid actor key values");
            if (property == "rotation")
                for (var i = 0; i < values.Count; i++)
                {
                    var length = Math.Sqrt(values[i, 0] * values[i, 0] + values[i, 1] * values[i, 1] + values[i, 2] * values[i, 2] + values[i, 3] * values[i, 3]);
                    if (Math.Abs(length - 1) > 0.001 + 1e-5) throw new PackageException("invalid actor key rotation");
                }
            tracks[(node, property)] = (times, values, mode);
            duration = Math.Max(duration, times[^1]);
        }
        if (duration is <= 0 or > 120) throw new PackageException("actor clip must be longer than zero and at most 120 seconds");
        duration = Math.Max(0.5, duration);
        var count = Math.Max(2, (int)Math.Round(duration * 60) + 1);
        var step = duration / (count - 1);
        var samples = Enumerable.Range(0, count).Select(i => i * step).ToArray();
        samples[^1] = duration;
        var poses = new double[count, joints.Length, 7];
        for (var f = 0; f < count; f++) for (var b = 0; b < joints.Length; b++) for (var c = 0; c < 7; c++) poses[f, b, c] = bind[b][c];
        for (var bone = 0; bone < joints.Length; bone++)
        {
            var joint = joints[bone];
            Matrix4d? parentTransform = parents[bone] < 0 && parentNodes.TryGetValue(joint, out var parentNode) ? world[parentNode] : null;
            if (parentTransform != null)
            {
                var local = Matrix4d.Reflect(Matrix4d.FromNode(glb.Item("nodes", joint)));
                var q = Matrix4d.Quaternion(local.Upper3());
                for (var f = 0; f < count; f++)
                {
                    for (var c = 0; c < 3; c++) poses[f, bone, c] = local[c, 3];
                    for (var c = 0; c < 4; c++) poses[f, bone, 3 + c] = q[c];
                }
            }
            foreach (var property in new[] { "translation", "rotation" })
            {
                if (!tracks.TryGetValue((joint, property), out var key)) continue;
                var columns = key.Values.Columns;
                var values = (double[])key.Values.Values.Clone();
                if (property == "rotation")
                    for (var i = 1; i < key.Values.Count; i++)
                    {
                        double dot = 0;
                        for (var c = 0; c < 4; c++) dot += values[(i - 1) * 4 + c] * values[i * 4 + c];
                        if (dot < 0) for (var c = 0; c < 4; c++) values[i * 4 + c] *= -1;
                    }
                var result = new double[columns];
                for (var f = 0; f < count; f++)
                {
                    if (key.Mode == "STEP")
                    {
                        var index = Math.Max(0, UpperBound(key.Times, samples[f]) - 1);
                        for (var c = 0; c < columns; c++) result[c] = values[index * columns + c];
                    }
                    else Interpolate(samples[f], key.Times, values, result);
                    if (property == "rotation")
                    {
                        double squares = 0;
                        foreach (var v in result) squares += v * v;
                        var length = Math.Sqrt(squares);
                        poses[f, bone, 3] = -result[0] / length;
                        poses[f, bone, 4] = -result[1] / length;
                        poses[f, bone, 5] = result[2] / length;
                        poses[f, bone, 6] = result[3] / length;
                    }
                    else
                    {
                        poses[f, bone, 0] = result[0];
                        poses[f, bone, 1] = result[1];
                        poses[f, bone, 2] = -result[2];
                    }
                }
            }
            if (parentTransform is { } parent)
            {
                var m = parent.Upper3();
                var t = parent.Translation;
                var q = Matrix4d.Quaternion(m);
                for (var f = 0; f < count; f++)
                {
                    double px = poses[f, bone, 0], py = poses[f, bone, 1], pz = poses[f, bone, 2];
                    for (var r = 0; r < 3; r++) poses[f, bone, r] = px * m[r * 3] + py * m[r * 3 + 1] + pz * m[r * 3 + 2] + t[r];
                    double vx = poses[f, bone, 3], vy = poses[f, bone, 4], vz = poses[f, bone, 5], w = poses[f, bone, 6];
                    poses[f, bone, 3] = q[3] * vx + w * q[0] + (q[1] * vz - q[2] * vy);
                    poses[f, bone, 4] = q[3] * vy + w * q[1] + (q[2] * vx - q[0] * vz);
                    poses[f, bone, 5] = q[3] * vz + w * q[2] + (q[0] * vy - q[1] * vx);
                    poses[f, bone, 6] = q[3] * w - (q[0] * vx + q[1] * vy + q[2] * vz);
                }
            }
        }
        return (duration, poses);
    }

    private static int UpperBound(double[] values, double x)
    {
        int low = 0, high = values.Length;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (values[mid] <= x) low = mid + 1; else high = mid;
        }
        return low;
    }

    // numpy.interp for each column (one key search for all columns).
    private static void Interpolate(double x, double[] xp, double[] fp, double[] result)
    {
        var n = xp.Length;
        var columns = result.Length;
        if (x <= xp[0] || x >= xp[n - 1])
        {
            var row = x <= xp[0] ? 0 : n - 1;
            for (var column = 0; column < columns; column++) result[column] = fp[row * columns + column];
            return;
        }
        var j = UpperBound(xp, x) - 1;
        for (var column = 0; column < columns; column++)
        {
            if (xp[j] == x)
            {
                result[column] = fp[j * columns + column];
                continue;
            }
            var slope = (fp[(j + 1) * columns + column] - fp[j * columns + column]) / (xp[j + 1] - xp[j]);
            var value = slope * (x - xp[j]) + fp[j * columns + column];
            result[column] = double.IsNaN(value) ? slope * (x - xp[j + 1]) + fp[(j + 1) * columns + column] : value;
        }
    }
}
