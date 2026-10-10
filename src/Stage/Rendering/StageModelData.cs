using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json;
using UnityEngine;

namespace Gh3Skin;

// One mesh part of meshes.bin (GH3STG04 static or GH3BND02 skinned), as StageAssets gives it to Unity.
internal sealed class StageMeshPart
{
    public uint MaterialId;
    public int VertexCount;
    public Vector3[] Vertices = Array.Empty<Vector3>();
    public Vector3[] Normals = Array.Empty<Vector3>();
    public Vector2[] Uv = Array.Empty<Vector2>();
    public Vector2[]? LightmapUv, LayerUv, MaskUv;
    public Color32[] Colors = Array.Empty<Color32>();
    // GH3 vertex streams: UV sets 0-3, texcoord4, texcoord5.
    public Vector2[][] Streams = Array.Empty<Vector2[]>();
    public Vector4[] Binormals = Array.Empty<Vector4>();
    public Vector2[] PivotXy = Array.Empty<Vector2>(), PivotZ = Array.Empty<Vector2>();
    public Vector2[] Binormal1Xy = Array.Empty<Vector2>(), Binormal1Z = Array.Empty<Vector2>();
    public Vector4[]? Tangents;
    public byte[]? BoneCounts;
    public StageAssets.SkinWeight[]? Weights;
    // The largest bone index of all influences (also those with weight 0); the caller checks it against its rig.
    public int MaxBone = -1;
    public int[] Indices = Array.Empty<int>();
}

// The Unity-free part of a model folder: stage.json and meshes.bin. The stage load reads the folders that it will need on
// worker threads while the main thread builds the scene; only plain struct fields are set here (no Unity calls, which are
// not allowed off the main thread).
internal sealed class StageModelData
{    private const int MaximumMeshes = 10000;
    private const int MaximumIndices = 1000000;
    private const float WeightTolerance = 0.001f;

    public StageDefinition Definition { get; private init; } = new();
    public StageMeshPart?[] Parts { get; private init; } = Array.Empty<StageMeshPart?>();

    public static StageDefinition ReadDefinition(string directory) =>
        JsonSerializer.Deserialize<StageDefinition>(File.ReadAllText(Path.Combine(directory, "stage.json")), Packages.PackageJson.CaseInsensitive)
            ?? throw new InvalidDataException("Missing stage definition");

    // parts: the mesh parts to read (null: all). Skipped parts are not checked, as before.
    public static StageModelData Read(string directory, bool skinned, IReadOnlyCollection<int>? parts = null) =>
        new() { Definition = ReadDefinition(directory), Parts = ReadMeshes(Path.Combine(directory, "meshes.bin"), skinned, parts) };

    public static StageMeshPart?[] ReadMeshes(string path, bool skinned, IReadOnlyCollection<int>? parts)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 12 || System.Text.Encoding.ASCII.GetString(data, 0, 8) != (skinned ? "GH3BND02" : "GH3STG04"))
            throw new InvalidDataException("Invalid stage mesh header");
        var at = 8;
        var meshCount = Int(data, ref at);
        if (meshCount < 1 || meshCount > MaximumMeshes) throw new InvalidDataException("Invalid stage mesh count");
        var stride = skinned ? Packages.StageGeometry.SkinnedStride : Packages.StageGeometry.StaticStride;
        var result = new StageMeshPart?[meshCount];
        for (var index = 0; index < meshCount; index++)
        {
            if (data.Length - at < 12) throw new InvalidDataException("Truncated stage mesh");
            var materialId = (uint)Int(data, ref at);
            var vertexCount = Int(data, ref at);
            var indexCount = Int(data, ref at);
            if (vertexCount < 1 || vertexCount > ushort.MaxValue || indexCount < 0 || indexCount > MaximumIndices || indexCount % 3 != 0)
                throw new InvalidDataException("Invalid stage mesh size");
            var size = (long)vertexCount * stride + indexCount * 4L;
            if (data.Length - at < size) throw new InvalidDataException("Truncated stage mesh");
            if (parts != null && !parts.Contains(index))
            {
                at += (int)size;
                continue;
            }
            result[index] = skinned ? ReadSkinned(data, ref at, materialId, vertexCount, indexCount) : ReadStatic(data, ref at, materialId, vertexCount, indexCount);
        }
        if (at != data.Length) throw new InvalidDataException("Extra stage mesh data");
        return result;
    }

    private static StageMeshPart ReadStatic(byte[] data, ref int at, uint materialId, int count, int indexCount)
    {
        var part = NewPart(materialId, count);
        part.LightmapUv = new Vector2[count];
        part.LayerUv = new Vector2[count];
        part.MaskUv = new Vector2[count];
        for (var v = 0; v < count; v++)
        {
            ReadCommon(data, ref at, part, v);
            part.LightmapUv[v] = Vector2(data, ref at);
            part.LayerUv[v] = Vector2(data, ref at);
            part.MaskUv[v] = Vector2(data, ref at);
            for (var i = 0; i < 6; i++) part.Streams[i][v] = Vector2(data, ref at);
            var b = Vector3(data, ref at);
            part.Binormals[v] = new Vector4 { x = b.x, y = b.y, z = b.z, w = 1 };
            var p = Vector3(data, ref at);
            part.PivotXy[v] = new Vector2 { x = p.x, y = p.y };
            part.PivotZ[v] = new Vector2 { x = p.z };
            var b1 = Vector3(data, ref at);
            part.Binormal1Xy[v] = new Vector2 { x = b1.x, y = b1.y };
            part.Binormal1Z[v] = new Vector2 { x = b1.z };
        }
        ReadIndices(data, ref at, part, indexCount);
        return part;
    }

    private static StageMeshPart ReadSkinned(byte[] data, ref int at, uint materialId, int count, int indexCount)
    {
        var part = NewPart(materialId, count);
        var tangents = part.Tangents = new Vector4[count];
        var boneCounts = part.BoneCounts = new byte[count];
        var weights = new List<StageAssets.SkinWeight>(count * 3);
        Span<StageAssets.SkinWeight> influences = stackalloc StageAssets.SkinWeight[4];
        Span<StageAssets.SkinWeight> sorted = stackalloc StageAssets.SkinWeight[4];
        for (var v = 0; v < count; v++)
        {
            ReadCommon(data, ref at, part, v);
            for (var i = 0; i < 4; i++)
            {
                influences[i].Bone = data[at + i];
                part.MaxBone = Math.Max(part.MaxBone, data[at + i]);
            }
            at += 4;
            double sum = 0;
            for (var i = 0; i < 4; i++)
            {
                influences[i].Weight = Float(data, ref at);
                if (influences[i].Weight < 0) throw new InvalidDataException("Invalid band skin weights");
                sum += influences[i].Weight;
            }
            // As Enumerable.Sum of floats (a double total, then float).
            if (Math.Abs((float)sum - 1) > WeightTolerance) throw new InvalidDataException("Invalid band skin weights");
            // The influences with weight, largest first; equal weights keep their order (a stable sort, as OrderByDescending).
            var used = 0;
            for (var i = 0; i < 4; i++)
            {
                if (influences[i].Weight <= 0) continue;
                var slot = used++;
                while (slot > 0 && sorted[slot - 1].Weight < influences[i].Weight)
                {
                    sorted[slot] = sorted[slot - 1];
                    slot--;
                }
                sorted[slot] = influences[i];
            }
            boneCounts[v] = (byte)used;
            for (var i = 0; i < used; i++) weights.Add(sorted[i]);
            for (var i = 0; i < 6; i++) part.Streams[i][v] = Vector2(data, ref at);
            var b = Vector3(data, ref at);
            var t = Vector3(data, ref at);
            var n = part.Normals[v];
            // The translated vertex shader rebuilds binormal 0 as normal x tangent with this sign (see pipeline/gh3/shader.py
            // VERTEX_MAIN); Vector3.Dot and Vector3.Cross in the same float order.
            float cx = n.y * t.z - n.z * t.y, cy = n.z * t.x - n.x * t.z, cz = n.x * t.y - n.y * t.x;
            var sign = b.x * cx + b.y * cy + b.z * cz < 0 ? -1f : 1f;
            tangents[v] = new Vector4 { x = t.x, y = t.y, z = t.z, w = sign };
        }
        part.Weights = weights.ToArray();
        ReadIndices(data, ref at, part, indexCount);
        return part;
    }

    private static StageMeshPart NewPart(uint materialId, int count)
    {
        var part = new StageMeshPart
        {
            MaterialId = materialId, VertexCount = count,
            Vertices = new Vector3[count], Normals = new Vector3[count], Uv = new Vector2[count], Colors = new Color32[count],
            Streams = new Vector2[6][],
        };
        for (var i = 0; i < 6; i++) part.Streams[i] = new Vector2[count];
        // Skinned meshes have no binormal or pivot streams; they stay zero, as the stage load had them.
        part.Binormals = new Vector4[count];
        part.PivotXy = new Vector2[count];
        part.PivotZ = new Vector2[count];
        part.Binormal1Xy = new Vector2[count];
        part.Binormal1Z = new Vector2[count];
        return part;
    }

    private static void ReadCommon(byte[] data, ref int at, StageMeshPart part, int v)
    {
        part.Vertices[v] = Vector3(data, ref at);
        part.Normals[v] = Vector3(data, ref at);
        part.Uv[v] = Vector2(data, ref at);
        part.Colors[v] = new Color32 { r = data[at], g = data[at + 1], b = data[at + 2], a = data[at + 3] };
        at += 4;
    }

    private static void ReadIndices(byte[] data, ref int at, StageMeshPart part, int count)
    {
        var indices = part.Indices = new int[count];
        for (var i = 0; i < count; i++)
        {
            indices[i] = Int(data, ref at);
            if (indices[i] < 0 || indices[i] >= part.VertexCount) throw new InvalidDataException("Invalid stage triangle");
        }
    }

    private static int Int(byte[] data, ref int at)
    {
        var value = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at));
        at += 4;
        return value;
    }

    private static float Float(byte[] data, ref int at)
    {
        var value = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(at));
        at += 4;
        return float.IsFinite(value) ? value : throw new InvalidDataException("Invalid stage vertex");
    }

    private static Vector2 Vector2(byte[] data, ref int at) => new() { x = Float(data, ref at), y = Float(data, ref at) };
    private static Vector3 Vector3(byte[] data, ref int at) => new() { x = Float(data, ref at), y = Float(data, ref at), z = Float(data, ref at) };
}

// Files that the stage load will need, read ahead on worker threads: the model folders (StageModelData) and the actor
// clips (BandAnimation). The loaders take the data if it is here, else read the files themselves. Clear drops what the load
// did not use.
internal static class StageModelPrefetch
{
    private static readonly ConcurrentDictionary<string, Task<object>> Pending = new();

    private static string ModelKey(string directory, bool skinned) => $"model:{skinned}:{Path.GetFullPath(directory)}";
    private static string AnimationKey(string path) => "animation:" + Path.GetFullPath(path);

    public static void Start(string directory, bool skinned)
    {
        if (!File.Exists(Path.Combine(directory, "meshes.bin")) || !File.Exists(Path.Combine(directory, "stage.json"))) return;
        Pending.TryAdd(ModelKey(directory, skinned), Task.Run(() => (object)StageModelData.Read(directory, skinned)));
    }

    public static void StartAll(string folder, bool skinned)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var directory in Directory.EnumerateDirectories(folder)) Start(directory, skinned);
    }

    public static void StartAnimation(string path)
    {
        if (File.Exists(path)) Pending.TryAdd(AnimationKey(path), Task.Run(() => (object)ReadAnimation(path)));
    }

    public static BandAnimation ReadAnimation(string path)
    {
        using var reader = new BinaryReader(new MemoryStream(File.ReadAllBytes(path)));
        return new BandAnimation(reader);
    }

    // The read errors come back here, on the main thread, as from a direct read.
    public static StageModelData? Take(string directory, bool skinned) => (StageModelData?)Take(ModelKey(directory, skinned));
    public static BandAnimation? TakeAnimation(string path) => (BandAnimation?)Take(AnimationKey(path));

    private static object? Take(string key) => Pending.TryRemove(key, out var task) ? task.GetAwaiter().GetResult() : null;

    public static void Clear() => Pending.Clear();
}
