using System.Text.Json;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gh3Skin;

internal sealed class StageDefinition
{
    public int Version { get; set; }
    public string Name { get; set; } = "";
    public float[] CameraPosition { get; set; } = Array.Empty<float>();
    public float[] CameraTarget { get; set; } = Array.Empty<float>();
    public float FieldOfView { get; set; } = 55;
    public float[][] PyroPositions { get; set; } = Array.Empty<float[]>();
    public Dictionary<string, StageMaterial> Materials { get; set; } = new();
    public string[] LightGroups { get; set; } = Array.Empty<string>();
}

internal sealed class StageMaterial
{
    public string Texture { get; set; } = "";
    public int Blend { get; set; }
    public float Cutoff { get; set; }
    public bool Crowd { get; set; }
    public bool SortedTransparency { get; set; }
}

internal sealed class StageAssets : IDisposable
{
    internal sealed record LitMesh(Mesh Mesh, string Group, Vector3[] Positions, Vector3[] Normals, Color32[] Colors);
    internal sealed record LitBandMesh(SkinnedMeshRenderer Renderer, Color32[] Colors);
    [StructLayout(LayoutKind.Sequential)]
    private struct SkinWeight
    {
        public float Weight;
        public int Bone;
    }
    private readonly List<UnityEngine.Object> _resources = new();
    public StageDefinition Definition { get; private set; } = new();
    public List<Material> SurfaceMaterials { get; } = new();
    public List<Material> CrowdMaterials { get; } = new();
    public List<LitMesh> LitMeshes { get; } = new();
    public List<LitBandMesh> BandMeshes { get; } = new();
    public List<StageTransparency> TransparentMeshes { get; } = new();

    public T Own<T>(T resource) where T : UnityEngine.Object
    {
        resource.hideFlags = HideFlags.DontUnloadUnusedAsset;
        _resources.Add(resource);
        return resource;
    }

    public static Material MakeMaterial(Shader shader, bool transparent, bool additive = false)
    {
        var material = new Material(shader);
        material.SetFloat("_Mode", transparent ? 2 : 0);
        material.SetFloat("_ColorMode", 0);
        material.SetInt("_SrcBlend", (int)(transparent ? BlendMode.SrcAlpha : BlendMode.One));
        material.SetInt("_DstBlend", (int)(additive ? BlendMode.One : transparent ? BlendMode.OneMinusSrcAlpha : BlendMode.Zero));
        material.SetInt("_ZWrite", transparent ? 0 : 1);
        material.SetInt("_Cull", (int)CullMode.Off);
        material.DisableKeyword("_ALPHATEST_ON");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.DisableKeyword("_ALPHAMODULATE_ON");
        if (transparent) material.EnableKeyword("_ALPHABLEND_ON");
        else material.DisableKeyword("_ALPHABLEND_ON");
        material.renderQueue = transparent ? 3000 : 2000;
        material.color = Color.white;
        return material;
    }

    public void Load(string directory, GameObject root, int layer, Transform[]? bones = null, bool lighting = false)
    {
        Definition = JsonSerializer.Deserialize<StageDefinition>(File.ReadAllText(Path.Combine(directory, "stage.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Missing stage definition");
        if (Definition.Version != 1 || Definition.Materials.Count > 2048 ||
            Definition.CameraPosition.Length != 3 || Definition.CameraTarget.Length != 3 ||
            !Definition.CameraPosition.Concat(Definition.CameraTarget).All(float.IsFinite) ||
            !float.IsFinite(Definition.FieldOfView) || Definition.FieldOfView < 10 || Definition.FieldOfView > 120 ||
            Definition.PyroPositions.Length > 16 || Definition.PyroPositions.Any(p => p.Length != 3 || !p.All(float.IsFinite)))
            throw new InvalidDataException("Invalid stage definition");
        var shader = Shader.Find("Particles/Standard Unlit");
        if (shader == null) throw new InvalidOperationException("The stage shader is not available");
        var textures = new Dictionary<string, Texture2D>();
        var materials = new Dictionary<uint, Material>();
        foreach (var (key, source) in Definition.Materials)
        {
            var material = Own(MakeMaterial(shader, source.Blend != 0, source.Blend == 1));
            material.name = "GH3 Stage " + key;
            if (source.Cutoff > 0 && !source.SortedTransparency)
            {
                material.SetFloat("_Cutoff", source.Cutoff);
                material.EnableKeyword("_ALPHATEST_ON");
                material.renderQueue = 2450;
            }
            if (bones != null && source.Blend == 3 && !source.SortedTransparency)
            {
                // Cutout-only rendering loses texture alpha in this game's particle shader.
                material.SetInt("_ZWrite", 1);
                material.renderQueue = 3000;
            }
            if (source.Texture.Length > 0)
            {
                if (source.Texture.Length != 8 || !source.Texture.All(Uri.IsHexDigit))
                    throw new InvalidDataException("Invalid stage texture name");
                if (!textures.TryGetValue(source.Texture, out var texture))
                {
                    texture = Own(new Texture2D(2, 2, TextureFormat.RGBA32, true));
                    if (!texture.LoadImage(new Il2CppStructArray<byte>(File.ReadAllBytes(
                            Path.Combine(directory, "textures", source.Texture + ".png"))), true))
                        throw new InvalidDataException("Invalid stage texture");
                    texture.wrapMode = TextureWrapMode.Repeat;
                    texture.filterMode = FilterMode.Trilinear;
                    textures.Add(source.Texture, texture);
                }
                material.mainTexture = texture;
            }
            materials.Add(Convert.ToUInt32(key, 16), material);
            SurfaceMaterials.Add(material);
            if (source.Crowd) CrowdMaterials.Add(material);
        }
        using var reader = new BinaryReader(File.OpenRead(Path.Combine(directory, "meshes.bin")));
        if (System.Text.Encoding.ASCII.GetString(reader.ReadBytes(8)) != (bones == null ? "GH3STG01" : "GH3BND01"))
            throw new InvalidDataException("Invalid stage mesh header");
        var meshCount = reader.ReadInt32();
        if (meshCount < 1 || meshCount > 10000) throw new InvalidDataException("Invalid stage mesh count");
        if (Definition.LightGroups.Length != 0 && Definition.LightGroups.Length != meshCount)
            throw new InvalidDataException("Stage light groups do not match the meshes");
        if (lighting && bones == null && Definition.LightGroups.Length == 0)
            throw new InvalidDataException("Stage light groups are missing. Run prepare-stage.py again.");
        for (var part = 0; part < meshCount; part++)
        {
            var materialId = reader.ReadUInt32();
            var material = materials[materialId];
            var sorted = bones != null && Definition.Materials[materialId.ToString("x8")].SortedTransparency;
            var vertexCount = reader.ReadInt32();
            var indexCount = reader.ReadInt32();
            if (vertexCount < 1 || vertexCount > 65535 || indexCount < 0 || indexCount > 1000000 || indexCount % 3 != 0)
                throw new InvalidDataException("Invalid stage mesh size");
            var vertices = new Vector3[vertexCount];
            var normals = new Vector3[vertexCount];
            var uv = new Vector2[vertexCount];
            var colors = new Color32[vertexCount];
            var weights = bones == null ? null : new List<SkinWeight>(vertexCount * 3);
            var boneCounts = bones == null ? null : new byte[vertexCount];
            for (var v = 0; v < vertexCount; v++)
            {
                vertices[v] = ReadVector(reader);
                normals[v] = ReadVector(reader);
                uv[v] = new Vector2(ReadFloat(reader), ReadFloat(reader));
                colors[v] = new Color32(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
                if (weights != null && bones != null)
                {
                    var indicesForVertex = reader.ReadBytes(4);
                    var influences = new SkinWeight[4];
                    for (var i = 0; i < 4; i++) influences[i] = new SkinWeight { Bone = indicesForVertex[i], Weight = ReadFloat(reader) };
                    if (influences.Any(w => w.Bone >= bones.Length || w.Weight < 0) ||
                        Math.Abs(influences.Sum(w => w.Weight) - 1) > 0.001f)
                        throw new InvalidDataException("Invalid band skin weights");
                    var used = influences.Where(w => w.Weight > 0).OrderByDescending(w => w.Weight).ToArray();
                    boneCounts![v] = (byte)used.Length;
                    weights.AddRange(used);
                }
            }
            var indices = new int[indexCount];
            for (var i = 0; i < indexCount; i++)
            {
                indices[i] = reader.ReadInt32();
                if (indices[i] < 0 || indices[i] >= vertexCount) throw new InvalidDataException("Invalid stage triangle");
            }
            var mesh = Own(new Mesh());
            mesh.name = "GH3 Stage Mesh " + part;
            mesh.vertices = new Il2CppStructArray<Vector3>(vertices);
            mesh.normals = new Il2CppStructArray<Vector3>(normals);
            mesh.uv = new Il2CppStructArray<Vector2>(uv);
            mesh.colors32 = new Il2CppStructArray<Color32>(colors);
            mesh.triangles = new Il2CppStructArray<int>(indices);
            if (weights != null && bones != null)
            {
                SetSkinWeights(mesh, boneCounts!, weights.ToArray());
                mesh.bindposes = new Il2CppStructArray<Matrix4x4>(bones.Select(bone =>
                    bone.worldToLocalMatrix * root.transform.localToWorldMatrix).ToArray());
            }
            mesh.RecalculateBounds();
            var dynamicColors = lighting && (bones != null || Definition.LightGroups.Length > 0);
            if (dynamicColors || sorted) mesh.MarkDynamic();
            mesh.UploadMeshData(!dynamicColors && !sorted);
            if (dynamicColors && bones == null) LitMeshes.Add(new(mesh, Definition.LightGroups[part], vertices, normals, colors));
            var item = new GameObject(mesh.name) { layer = layer };
            item.transform.SetParent(root.transform, false);
            Renderer renderer;
            if (bones == null)
            {
                item.AddComponent<MeshFilter>().sharedMesh = mesh;
                renderer = item.AddComponent<MeshRenderer>();
            }
            else
            {
                var skin = item.AddComponent<SkinnedMeshRenderer>();
                skin.sharedMesh = mesh;
                skin.bones = new Il2CppReferenceArray<Transform>(bones);
                skin.rootBone = root.transform;
                skin.updateWhenOffscreen = true;
                skin.localBounds = new Bounds(new Vector3(0, 1, 0), new Vector3(8, 6, 8));
                renderer = skin;
                if (lighting) BandMeshes.Add(new(skin, colors));
                if (sorted) TransparentMeshes.Add(new(this, skin, vertexCount, indices));
            }
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }
        if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Extra stage mesh data");
    }

    private static float ReadFloat(BinaryReader reader)
    {
        var value = reader.ReadSingle();
        return float.IsFinite(value) ? value : throw new InvalidDataException("Invalid stage vertex");
    }

    private static void SetSkinWeights(Mesh mesh, byte[] counts, SkinWeight[] weights)
    {
        // Clone Hero strips BoneWeight1; its native mesh entry point remains available.
        var countsHandle = GCHandle.Alloc(counts, GCHandleType.Pinned);
        var weightsHandle = GCHandle.Alloc(weights, GCHandleType.Pinned);
        try
        {
            mesh.InternalSetBoneWeights(countsHandle.AddrOfPinnedObject(), counts.Length,
                weightsHandle.AddrOfPinnedObject(), weights.Length);
            if (!mesh.HasBoneWeights()) throw new InvalidOperationException("Band mesh weights did not load");
        }
        finally
        {
            countsHandle.Free();
            weightsHandle.Free();
        }
    }

    private static Vector3 ReadVector(BinaryReader reader) => new(ReadFloat(reader), ReadFloat(reader), ReadFloat(reader));
    public static Vector3 Vector(float[] value) => new(value[0], value[1], value[2]);

    public void Dispose()
    {
        foreach (var resource in _resources)
            if (resource != null) UnityEngine.Object.Destroy(resource);
        _resources.Clear();
        SurfaceMaterials.Clear();
        CrowdMaterials.Clear();
        LitMeshes.Clear();
        BandMeshes.Clear();
        TransparentMeshes.Clear();
    }
}
