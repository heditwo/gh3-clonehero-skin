using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;

namespace Gh3Skin;

// Spike (GH3_SKIN_SPIKE=skinned): build one band member from a skinned GLB, drive it with the band animation, and compare it
// with the GH3BND02 meshes in the same frame. GH3_SKIN_SPIKE_GLB selects the file, GH3_SKIN_SPIKE_MODE=original keeps the
// GH3BND02 meshes on screen, GH3_SKIN_SPIKE_TIMES lists song times for the comparisons, GH3_SKIN_SPIKE_OUT the image folder.
internal sealed class SkinnedGlbSpike : IDisposable
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("GH3_SKIN_SPIKE") == "skinned";
    private const string Prefix = "[spike-skinned] ";
    private sealed record BuiltPart(string Material, Mesh Mesh, Matrix4x4[] Bindposes, int Vertices);
    // Two renderers per part: swapping sharedMesh on one renderer and rendering in the same frame draws nothing (GPU skinning).
    private sealed record Part(SkinnedMeshRenderer Original, SkinnedMeshRenderer Glb, Mesh GlbMesh, Il2CppReferenceArray<Transform> GlbBones, bool Sorted);
    private readonly MaterialPropertyBlock _block = new();
    private readonly List<Part> _parts = new();
    private readonly List<UnityEngine.Object> _owned = new();
    private readonly Transform _root;
    private readonly bool _showGlb = Environment.GetEnvironmentVariable("GH3_SKIN_SPIKE_MODE") != "original";
    private readonly double[] _times = (Environment.GetEnvironmentVariable("GH3_SKIN_SPIKE_TIMES") ?? "5,15,25")
        .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(v => double.Parse(v, CultureInfo.InvariantCulture)).OrderBy(v => v).ToArray();
    private readonly string _output = Environment.GetEnvironmentVariable("GH3_SKIN_SPIKE_OUT") ?? "/tmp/opencode/spike-skinned";
    private int _next;
    private bool _blendTested;

    public static SkinnedGlbSpike? Attach(Transform root, Transform[] bones, string[] boneNames, StageAssets assets)
    {
        try
        {
            return new SkinnedGlbSpike(root, bones, boneNames, assets);
        }
        catch (Exception error)
        {
            MelonLogger.Warning(Prefix + "failed: " + error);
            return null;
        }
    }

    private SkinnedGlbSpike(Transform root, Transform[] bones, string[] boneNames, StageAssets assets)
    {
        _root = root;
        var path = Environment.GetEnvironmentVariable("GH3_SKIN_SPIKE_GLB") ?? throw new InvalidOperationException("Set GH3_SKIN_SPIKE_GLB");
        LogNativeMethods();
        var boneIndex = boneNames.Select((name, index) => (name, index)).ToDictionary(p => p.name, p => p.index);
        var originals = new List<SkinnedMeshRenderer>();
        for (var i = 0; i < root.childCount; i++)
        {
            var child = root.GetChild(i);
            if (!child.name.StartsWith("GH3 Stage Mesh ", StringComparison.Ordinal)) continue;
            var skin = child.GetComponent<SkinnedMeshRenderer>();
            if (skin != null) originals.Add(skin);
        }
        bool Template(string material) => originals.Any(r => r.sharedMaterial.name.StartsWith("GH3 Stage " + material, StringComparison.Ordinal) &&
            // Il2Cpp wrappers are new objects per call, so compare the native pointers.
            assets.TemplateMaterials.Keys.Any(k => k.Pointer == r.sharedMaterial.Pointer));

        var repeat = int.TryParse(Environment.GetEnvironmentVariable("GH3_SKIN_SPIKE_REPEAT"), out var r) ? Math.Max(1, r) : 5;
        List<BuiltPart>? built = null;
        int[] joints = Array.Empty<int>();
        for (var run = 0; run < repeat; run++)
        {
            var total = Stopwatch.StartNew();
            var data = File.ReadAllBytes(path);
            var read = total.Elapsed.TotalMilliseconds;
            using var glb = GlbFile.Parse(data);
            var parse = total.Elapsed.TotalMilliseconds - read;
            var decode = new Stopwatch();
            var unity = new Stopwatch();
            var parts = Build(glb, boneIndex, Template, decode, unity, out joints);
            total.Stop();
            MelonLogger.Msg(Prefix + $"load run {run}: total {total.Elapsed.TotalMilliseconds:F2} ms; read {read:F2}; json+chunks {parse:F2}; " +
                $"accessor decode {decode.Elapsed.TotalMilliseconds:F2}; unity mesh build {unity.Elapsed.TotalMilliseconds:F2}; " +
                $"{parts.Count} primitives; {parts.Sum(p => p.Vertices)} vertices; {data.Length} bytes");
            if (built == null) built = parts;
            else foreach (var part in parts) UnityEngine.Object.Destroy(part.Mesh);
        }
        _owned.AddRange(built!.Select(p => p.Mesh));
        MeasureTextures(path);

        var glbBones = new Il2CppReferenceArray<Transform>(joints.Select(j => bones[j]).ToArray());
        MelonLogger.Msg(Prefix + $"joint map identity with band bones: {joints.Select((b, i) => b == i).All(x => x)}; {joints.Length} joints");
        if (originals.Count != built!.Count) throw new InvalidDataException($"Primitive count {built.Count} differs from GH3BND02 parts {originals.Count}");
        var bindError = 0f;
        for (var i = 0; i < built.Count; i++)
        {
            var renderer = originals[i];
            if (!renderer.sharedMaterial.name.StartsWith("GH3 Stage " + built[i].Material, StringComparison.Ordinal))
                throw new InvalidDataException($"GLB material {built[i].Material} differs from {renderer.sharedMaterial.name}");
            var original = renderer.sharedMesh;
            var originalBind = original.bindposes;
            for (var j = 0; j < joints.Length; j++) bindError = Math.Max(bindError, MaxDifference(built[i].Bindposes[j], originalBind[joints[j]]));
            if (original.vertexCount != built[i].Vertices) throw new InvalidDataException("Vertex count differs");
            // Diagnostic: GH3_SKIN_SPIKE_BINDPOSES=gh3bnd02 copies the float32 bindposes of the GH3BND02 path into the GLB mesh.
            if (Environment.GetEnvironmentVariable("GH3_SKIN_SPIKE_BINDPOSES") == "gh3bnd02") built[i].Mesh.bindposes = originalBind;
            var holder = new GameObject("GH3 GLB Spike " + built[i].Material) { layer = renderer.gameObject.layer };
            _owned.Add(holder);
            holder.transform.SetParent(root, false);
            var skin = holder.AddComponent<SkinnedMeshRenderer>();
            // Diagnostic: GH3_SKIN_SPIKE_CONTROL=1 gives the second renderer the GH3BND02 mesh, to measure the renderer-only difference.
            var control = Environment.GetEnvironmentVariable("GH3_SKIN_SPIKE_CONTROL") == "1";
            skin.sharedMesh = control ? original : built[i].Mesh;
            skin.bones = control ? renderer.bones : glbBones;
            skin.rootBone = root;
            skin.updateWhenOffscreen = true;
            skin.localBounds = renderer.localBounds;
            skin.sharedMaterial = renderer.sharedMaterial;
            skin.shadowCastingMode = renderer.shadowCastingMode;
            skin.receiveShadows = false;
            skin.lightProbeUsage = renderer.lightProbeUsage;
            skin.reflectionProbeUsage = renderer.reflectionProbeUsage;
            _parts.Add(new Part(renderer, skin, built[i].Mesh, glbBones, assets.Definition.Materials[built[i].Material].SortedTransparency));
        }
        MelonLogger.Msg(Prefix + $"bindposes from GLB inverse bind matrices vs GH3BND02 path: max element difference {bindError:G3}");
        CompareChannels();
        CompareRig(path, bones, boneIndex);
        Show(_showGlb);
        MelonLogger.Msg(Prefix + $"on screen: {(_showGlb ? "GLB meshes" : "GH3BND02 meshes")}; materials reused from the GH3BND02 renderers by GLB material name");
    }

    private static List<BuiltPart> Build(GlbFile glb, Dictionary<string, int> boneIndex, Func<string, bool> template, Stopwatch decode,
        Stopwatch unity, out int[] joints)
    {
        decode.Start();
        var skin = glb.Array("skins", 0);
        var nodes = glb.Root.GetProperty("nodes");
        var jointBones = skin.GetProperty("joints").EnumerateArray().Select(j =>
        {
            var name = nodes[j.GetInt32()].GetProperty("name").GetString() ?? "";
            if (!name.StartsWith("bone-", StringComparison.Ordinal) || !boneIndex.TryGetValue(name[5..], out var bone))
                throw new InvalidDataException("GLB joint is not a band bone: " + name);
            return bone;
        }).ToArray();
        joints = jointBones;
        var inverse = glb.Floats(skin.GetProperty("inverseBindMatrices").GetInt32(), 16);
        var bindposes = new Matrix4x4[jointBones.Length];
        for (var j = 0; j < jointBones.Length; j++) bindposes[j] = MirrorZ(inverse, j * 16);
        var meshNode = nodes.EnumerateArray().First(n => n.TryGetProperty("mesh", out _) && n.TryGetProperty("skin", out _));
        var materials = glb.Root.GetProperty("materials");
        decode.Stop();
        var result = new List<BuiltPart>();
        foreach (var primitive in glb.Array("meshes", meshNode.GetProperty("mesh").GetInt32()).GetProperty("primitives").EnumerateArray())
        {
            decode.Start();
            var attributes = primitive.GetProperty("attributes");
            int A(string name) => attributes.GetProperty(name).GetInt32();
            var material = materials[primitive.GetProperty("material").GetInt32()].GetProperty("name").GetString() ?? "";
            var positions = glb.Floats(A("POSITION"), 3);
            var normalData = glb.Floats(A("NORMAL"), 3);
            var count = positions.Length / 3;
            var vertices = new Vector3[count];
            var normals = new Vector3[count];
            for (var v = 0; v < count; v++)
            {
                vertices[v] = new Vector3(positions[v * 3], positions[v * 3 + 1], -positions[v * 3 + 2]);
                normals[v] = new Vector3(normalData[v * 3], normalData[v * 3 + 1], -normalData[v * 3 + 2]);
            }
            var streams = template(material) && attributes.TryGetProperty("_GH3_UV0", out _);
            var uvs = new Vector2[streams ? 6 : 1][];
            for (var s = 0; s < uvs.Length; s++)
            {
                var uv = glb.Floats(A(streams ? $"_GH3_UV{s}" : "TEXCOORD_0"), 2);
                uvs[s] = new Vector2[count];
                // GH3 streams keep D3D's top-down V; TEXCOORD_0 is glTF bottom-up.
                for (var v = 0; v < count; v++) uvs[s][v] = new Vector2(uv[v * 2], streams ? uv[v * 2 + 1] : 1 - uv[v * 2 + 1]);
            }
            Vector4[]? tangents = null;
            if (streams)
            {
                var t = glb.Floats(A("_GH3_TANGENT"), 3);
                var b = glb.Floats(A("_GH3_BINORMAL"), 3);
                tangents = new Vector4[count];
                for (var v = 0; v < count; v++)
                {
                    var tangent = new Vector3(t[v * 3], t[v * 3 + 1], -t[v * 3 + 2]);
                    var binormal = new Vector3(b[v * 3], b[v * 3 + 1], -b[v * 3 + 2]);
                    var sign = Vector3.Dot(binormal, Vector3.Cross(normals[v], tangent)) < 0 ? -1f : 1f;
                    tangents[v] = new Vector4 { x = tangent.x, y = tangent.y, z = tangent.z, w = sign };
                }
            }
            var colorData = glb.Floats(A("COLOR_0"), 4);
            var colors = new Color32[count];
            for (var v = 0; v < count; v++)
                colors[v] = new Color32((byte)MathF.Round(colorData[v * 4] * 255), (byte)MathF.Round(colorData[v * 4 + 1] * 255),
                    (byte)MathF.Round(colorData[v * 4 + 2] * 255), (byte)MathF.Round(colorData[v * 4 + 3] * 255));
            var jointData = glb.Integers(A("JOINTS_0"), 4);
            var weightData = glb.Floats(A("WEIGHTS_0"), 4);
            var counts = new byte[count];
            var weights = new List<StageAssets.SkinWeight>(count * 3);
            var influences = new StageAssets.SkinWeight[4];
            for (var v = 0; v < count; v++)
            {
                for (var i = 0; i < 4; i++) influences[i] = new StageAssets.SkinWeight { Bone = jointData[v * 4 + i], Weight = weightData[v * 4 + i] };
                if (influences.Any(w => w.Bone >= jointBones.Length || w.Weight < 0) || Math.Abs(influences.Sum(w => w.Weight) - 1) > 0.001f)
                    throw new InvalidDataException("Invalid GLB skin weights");
                var used = influences.Where(w => w.Weight > 0).OrderByDescending(w => w.Weight).ToArray();
                counts[v] = (byte)used.Length;
                weights.AddRange(used);
            }
            var indices = glb.Integers(primitive.GetProperty("indices").GetInt32(), 1);
            if (indices.Length % 3 != 0 || indices.Any(i => i >= count)) throw new InvalidDataException("Invalid GLB triangles");
            // GlbWriter reverses the winding for the z mirror.
            for (var i = 0; i < indices.Length; i += 3) (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
            decode.Stop();

            unity.Start();
            var mesh = new Mesh { name = "GH3 GLB Spike " + material };
            mesh.vertices = new Il2CppStructArray<Vector3>(vertices);
            mesh.normals = new Il2CppStructArray<Vector3>(normals);
            mesh.uv = new Il2CppStructArray<Vector2>(uvs[0]);
            if (streams)
            {
                mesh.uv2 = new Il2CppStructArray<Vector2>(uvs[1]);
                mesh.uv3 = new Il2CppStructArray<Vector2>(uvs[2]);
                mesh.uv4 = new Il2CppStructArray<Vector2>(uvs[3]);
                mesh.uv5 = new Il2CppStructArray<Vector2>(uvs[4]);
                mesh.uv6 = new Il2CppStructArray<Vector2>(uvs[5]);
                mesh.tangents = new Il2CppStructArray<Vector4>(tangents!);
            }
            mesh.colors32 = new Il2CppStructArray<Color32>(colors);
            mesh.triangles = new Il2CppStructArray<int>(indices);
            StageAssets.SetSkinWeights(mesh, counts, weights.ToArray());
            mesh.bindposes = new Il2CppStructArray<Matrix4x4>(bindposes);
            mesh.RecalculateBounds();
            mesh.MarkDynamic();
            mesh.UploadMeshData(false);
            unity.Stop();
            result.Add(new BuiltPart(material, mesh, bindposes, count));
        }
        return result;
    }

    // glTF to Unity: the z mirror on both sides (M' = S M S, S = diag(1, 1, -1, 1)). glTF stores columns first.
    private static Matrix4x4 MirrorZ(float[] a, int o) => new()
    {
        m00 = a[o], m10 = a[o + 1], m20 = -a[o + 2], m30 = a[o + 3],
        m01 = a[o + 4], m11 = a[o + 5], m21 = -a[o + 6], m31 = a[o + 7],
        m02 = -a[o + 8], m12 = -a[o + 9], m22 = a[o + 10], m32 = -a[o + 11],
        m03 = a[o + 12], m13 = a[o + 13], m23 = -a[o + 14], m33 = a[o + 15],
    };

    private void CompareChannels()
    {
        static float Max<T>(Il2CppStructArray<T> a, Il2CppStructArray<T> b, Func<T, float[]> f) where T : unmanaged
        {
            if (a.Length != b.Length) return float.PositiveInfinity;
            var max = 0f;
            for (var i = 0; i < a.Length; i++) max = Math.Max(max, f(a[i]).Zip(f(b[i]), (x, y) => Math.Abs(x - y)).Max());
            return max;
        }
        var rows = new List<string>();
        foreach (var part in _parts)
        {
            var a = part.Original.sharedMesh;
            var b = part.GlbMesh;
            var uv = new[] { Max(a.uv, b.uv, V2), Max(a.uv2, b.uv2, V2), Max(a.uv3, b.uv3, V2), Max(a.uv4, b.uv4, V2), Max(a.uv5, b.uv5, V2), Max(a.uv6, b.uv6, V2) };
            rows.Add($"{b.name[15..]}: position {Max(a.vertices, b.vertices, V3):G3}; normal {Max(a.normals, b.normals, V3):G3}; uv0-5 {uv.Max():G3}; " +
                $"tangent {Max(a.tangents, b.tangents, V4):G3}; color {Max(a.colors32, b.colors32, C):G3}; uv lengths {a.uv.Length}/{b.uv.Length} {a.uv2.Length}/{b.uv2.Length}");
        }
        MelonLogger.Msg(Prefix + "mesh channels GLB vs GH3BND02 (max abs difference): " + string.Join(" | ", rows));
    }

    private static float[] V2(Vector2 v) => new[] { v.x, v.y };
    private static float[] V3(Vector3 v) => new[] { v.x, v.y, v.z };
    private static float[] V4(Vector4 v) => new[] { v.x, v.y, v.z, v.w };
    private static float[] C(Color32 c) => new float[] { c.r, c.g, c.b, c.a };

    private static float[] Values(Matrix4x4 m) => new[] { m.m00, m.m01, m.m02, m.m03, m.m10, m.m11, m.m12, m.m13, m.m20, m.m21, m.m22, m.m23, m.m30, m.m31, m.m32, m.m33 };
    private static float MaxDifference(Matrix4x4 a, Matrix4x4 b) => Values(a).Zip(Values(b), (x, y) => Math.Abs(x - y)).Max();

    private static void CompareRig(string path, Transform[] bones, Dictionary<string, int> boneIndex)
    {
        using var glb = GlbFile.Parse(File.ReadAllBytes(path));
        float position = 0, angle = 0;
        foreach (var node in glb.Root.GetProperty("nodes").EnumerateArray())
        {
            var name = node.GetProperty("name").GetString() ?? "";
            if (!name.StartsWith("bone-", StringComparison.Ordinal) || !boneIndex.TryGetValue(name[5..], out var bone)) continue;
            var t = node.GetProperty("translation").EnumerateArray().Select(v => v.GetSingle()).ToArray();
            var q = node.GetProperty("rotation").EnumerateArray().Select(v => v.GetSingle()).ToArray();
            var local = bones[bone];
            var p = local.localPosition;
            var r = local.localRotation;
            position = Math.Max(position, new[] { p.x - t[0], p.y - t[1], p.z + t[2] }.Max(Math.Abs));
            var u = new[] { -q[0], -q[1], q[2], q[3] };
            var sign = r.x * u[0] + r.y * u[1] + r.z * u[2] + r.w * u[3] < 0 ? -1 : 1;
            angle = Math.Max(angle, new[] { r.x - sign * u[0], r.y - sign * u[1], r.z - sign * u[2], r.w - sign * u[3] }.Max(Math.Abs));
        }
        MelonLogger.Msg(Prefix + $"GLB joint node TRS vs band bind pose: max position difference {position:G3} m; max quaternion component difference {angle:G3}");
    }

    private void MeasureTextures(string path)
    {
        using var glb = GlbFile.Parse(File.ReadAllBytes(path));
        var images = glb.Root.GetProperty("images").GetArrayLength();
        var watch = Stopwatch.StartNew();
        long pixels = 0;
        for (var i = 0; i < images; i++)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, false);
            if (!texture.LoadImage(new Il2CppStructArray<byte>(glb.Image(i)), true)) throw new InvalidDataException("Invalid GLB image");
            pixels += (long)texture.width * texture.height;
            UnityEngine.Object.Destroy(texture);
        }
        MelonLogger.Msg(Prefix + $"embedded PNG decode (Texture2D.LoadImage, main thread): {images} images; {pixels} pixels; {watch.Elapsed.TotalMilliseconds:F1} ms");
    }

    // Lists the native il2cpp methods of the class that match the names; a missing name means the IL2CPP build stripped it.
    private static void LogNativeMethods()
    {
        var names = new[] { "bindposes", "Bindposes", "BoneWeight", "boneWeights", "BonesPerVertex", "BlendShape", "blendShape", "BakeMesh", "Triangles", "triangles" };
        foreach (var (label, klass) in new[] { ("Mesh", Il2CppClassPointerStore<Mesh>.NativeClassPtr), ("SkinnedMeshRenderer", Il2CppClassPointerStore<SkinnedMeshRenderer>.NativeClassPtr) })
        {
            var found = new List<string>();
            var iterator = IntPtr.Zero;
            var total = 0;
            for (IntPtr method; (method = IL2CPP.il2cpp_class_get_methods(klass, ref iterator)) != IntPtr.Zero; total++)
            {
                var name = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(IL2CPP.il2cpp_method_get_name(method)) ?? "";
                if (names.Any(name.Contains)) found.Add($"{name}/{IL2CPP.il2cpp_method_get_param_count(method)}");
            }
            MelonLogger.Msg(Prefix + $"native il2cpp {label} methods ({total} total): {string.Join(", ", found.OrderBy(n => n, StringComparer.Ordinal))}");
        }
    }

    private void Show(bool glb)
    {
        foreach (var part in _parts)
        {
            part.Original.forceRenderingOff = glb;
            part.Glb.forceRenderingOff = !glb;
        }
    }

    public void Update(double time, Camera camera)
    {
        // Same lighting and the same depth-sorted triangle order as the GH3BND02 renderer of the part.
        foreach (var part in _parts)
        {
            part.Original.GetPropertyBlock(_block);
            part.Glb.SetPropertyBlock(_block);
            if (part.Sorted && part.Glb.sharedMesh.Pointer == part.GlbMesh.Pointer) part.GlbMesh.triangles = part.Original.sharedMesh.GetTrianglesImpl(0, true);
        }
        if (!_blendTested && time > 0)
        {
            _blendTested = true;
            try { TestBlendShapes(); }
            catch (Exception error) { MelonLogger.Warning(Prefix + "blend shape test failed: " + error); }
        }
        if (_next >= _times.Length || time < _times[_next]) return;
        var index = _next++;
        try { Compare(time, camera, index); }
        catch (Exception error) { MelonLogger.Warning(Prefix + "comparison failed: " + error); }
    }

    private float[] Bake(SkinnedMeshRenderer renderer, Mesh target, bool normals)
    {
        renderer.BakeMesh(target);
        var values = normals ? target.normals : target.vertices;
        var result = new float[values.Length * 3];
        for (var i = 0; i < values.Length; i++) { var v = values[i]; result[i * 3] = v.x; result[i * 3 + 1] = v.y; result[i * 3 + 2] = v.z; }
        return result;
    }

    private void Compare(double time, Camera camera, int index)
    {
        var baked = new Mesh { name = "GH3 GLB Spike Bake" };
        _owned.Add(baked);
        float position = 0, normal = 0;
        double sum = 0;
        var count = 0;
        var watch = Stopwatch.StartNew();
        foreach (var part in _parts)
        {
            var a = Bake(part.Original, baked, false);
            var an = Bake(part.Original, baked, true);
            var b = Bake(part.Glb, baked, false);
            var bn = Bake(part.Glb, baked, true);
            for (var i = 0; i < a.Length; i += 3)
            {
                var d = MathF.Sqrt((a[i] - b[i]) * (a[i] - b[i]) + (a[i + 1] - b[i + 1]) * (a[i + 1] - b[i + 1]) + (a[i + 2] - b[i + 2]) * (a[i + 2] - b[i + 2]));
                position = Math.Max(position, d);
                sum += d;
                count++;
                normal = Math.Max(normal, Math.Max(Math.Abs(an[i] - bn[i]), Math.Max(Math.Abs(an[i + 1] - bn[i + 1]), Math.Abs(an[i + 2] - bn[i + 2]))));
            }
        }
        MelonLogger.Msg(Prefix + $"compare {index} at song time {time:F3}: skinned (BakeMesh) {count} vertices; max position difference {position:G3} m; " +
            $"mean {sum / Math.Max(1, count):G3} m; max normal component difference {normal:G3}; bake time {watch.Elapsed.TotalMilliseconds:F1} ms");
        Directory.CreateDirectory(_output);
        var width = Screen.width;
        var height = Screen.height;
        var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
        var image = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
        var previous = camera.targetTexture;
        try
        {
            // Two passes: the repeat shows the render-to-render noise of each mesh source.
            foreach (var (glb, pass) in new[] { (false, 0), (true, 0), (false, 1), (true, 1) })
            {
                Show(glb);
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                image.Apply();
                RenderTexture.active = null;
                var file = Path.Combine(_output, $"compare-{index}-{(glb ? "glb" : "gh3bnd02")}-{pass}.png");
                File.WriteAllBytes(file, ImageConversion.EncodeToPNG(image).ToArray());
                MelonLogger.Msg(Prefix + "wrote " + file);
            }
        }
        finally
        {
            camera.targetTexture = previous;
            RenderTexture.active = null;
            Show(_showGlb);
            UnityEngine.Object.Destroy(target);
            UnityEngine.Object.Destroy(image);
        }
    }

    private void TestBlendShapes()
    {
        var part = _parts.OrderByDescending(p => p.GlbMesh.vertexCount).First();
        var copy = UnityEngine.Object.Instantiate(part.GlbMesh);
        copy.name = "GH3 GLB Spike Blend";
        _owned.Add(copy);
        var count = copy.vertexCount;
        var deltas = new Vector3[count];
        for (var i = 0; i < count; i++) deltas[i] = new Vector3(0, 0.05f, 0);
        copy.AddBlendShapeFrame("spike", 100, new Il2CppStructArray<Vector3>(deltas), null, null);
        var holder = new GameObject("GH3 GLB Spike Blend") { layer = part.Original.gameObject.layer };
        _owned.Add(holder);
        holder.transform.SetParent(_root, false);
        var renderer = holder.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = copy;
        renderer.bones = part.GlbBones;
        renderer.rootBone = _root;
        renderer.sharedMaterial = part.Original.sharedMaterial;
        renderer.enabled = false;
        var baked = new Mesh { name = "GH3 GLB Spike Blend Bake" };
        _owned.Add(baked);
        var rest = Bake(renderer, baked, false);
        var results = new List<string>();
        foreach (var weight in new[] { 100f, 50f })
        {
            renderer.SetBlendShapeWeight(0, weight);
            var moved = Bake(renderer, baked, false);
            float min = float.MaxValue, max = 0;
            for (var i = 0; i < rest.Length; i += 3)
            {
                var d = MathF.Sqrt((moved[i] - rest[i]) * (moved[i] - rest[i]) + (moved[i + 1] - rest[i + 1]) * (moved[i + 1] - rest[i + 1]) +
                    (moved[i + 2] - rest[i + 2]) * (moved[i + 2] - rest[i + 2]));
                min = Math.Min(min, d);
                max = Math.Max(max, d);
            }
            results.Add($"weight {renderer.GetBlendShapeWeight(0)}: baked offset {min:G4}..{max:G4} m (expected {0.05f * weight / 100:G4})");
        }
        MelonLogger.Msg(Prefix + $"blend shapes: blendShapeCount {copy.blendShapeCount}; name '{copy.GetBlendShapeName(0)}'; " +
            $"frames {copy.GetBlendShapeFrameCount(0)}; frame weight {copy.GetBlendShapeFrameWeight(0, 0)}; {string.Join("; ", results)}; " +
            $"bone weight array size {copy.GetAllBoneWeightsArraySize()}");
    }

    public void Dispose()
    {
        foreach (var part in _parts)
            if (part.Original != null) part.Original.forceRenderingOff = false;
        foreach (var item in _owned) if (item != null) UnityEngine.Object.Destroy(item);
        _owned.Clear();
        _parts.Clear();
    }
}
