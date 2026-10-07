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
    public string[] VisibilityGroups { get; set; } = Array.Empty<string>();
    public string[] HiddenGroups { get; set; } = Array.Empty<string>();
    public string[] ObjectIds { get; set; } = Array.Empty<string>();
    public Dictionary<string, StageObjectPlacement> Objects { get; set; } = new();
}

internal sealed class StageMaterial
{
    public string Source { get; set; } = "";
    public int MovieSlot { get; set; } = -1;
    public StageTextureAnimation? Animation { get; set; }
    public string Texture { get; set; } = "";
    public int Blend { get; set; }
    public float Cutoff { get; set; }
    public bool Crowd { get; set; }
    public bool SortedTransparency { get; set; }
    public string Shading { get; set; } = "";
    public string Lightmap { get; set; } = "";
    public float ColorScale { get; set; } = 1;
    public int ShadingMode => Shading switch { "unlit" => 0, "ambient" => 1, "lit" => 2, "" => -1, _ => throw new InvalidDataException("Unknown stage shading mode") };
}

internal sealed class StageAssets : IDisposable
{
    internal sealed record LitMesh(Mesh Mesh, string Group, Vector3[] Positions, Vector3[] Normals, Color32[] Colors, Transform Transform);
    internal sealed record LitBandMesh(SkinnedMeshRenderer Renderer, Color32[] Colors, string Group);
    internal sealed record ShadedSurface(Renderer Renderer, int Mode, string Group);
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
    public List<ShadedSurface> ShadedSurfaces { get; } = new();
    public List<StageTransparency> TransparentMeshes { get; } = new();
    private readonly Dictionary<string, List<Renderer>> _visibility = new();
    public Dictionary<string, Transform> Objects { get; } = new();
    public List<(uint Object, uint Material, Renderer Renderer, StageMaterial Definition)> MaterialSurfaces { get; } = new();
    public List<(int Slot, Material Material, Texture? Original)> MovieSurfaces { get; } = new();

    public void SetVisible(string group, bool? visible)
    {
        if (!_visibility.TryGetValue(group, out var renderers)) return;
        var enabled = visible ?? !Definition.HiddenGroups.Contains(group);
        foreach (var renderer in renderers) renderer.enabled = enabled;
    }

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

    private Material MakeSurfaceMaterial(Shader shader, StageMaterial source)
    {
        var transparent = source.Blend != 0;
        var material = Own(new Material(shader));
        material.SetInt("_SrcBlend", (int)(transparent ? BlendMode.SrcAlpha : BlendMode.One));
        material.SetInt("_DstBlend", (int)(source.Blend == 1 ? BlendMode.One : transparent ? BlendMode.OneMinusSrcAlpha : BlendMode.Zero));
        material.SetInt("_ZWrite", transparent ? 0 : 1);
        material.SetInt("_Cull", (int)CullMode.Off);
        var cutout = source.Cutoff > 0 && !source.SortedTransparency;
        if (!float.IsFinite(source.ColorScale) || source.ColorScale is < 1 or > 1000) throw new InvalidDataException("Invalid material color scale");
        material.SetVector("_Gh3Surface", new(source.ShadingMode, cutout ? source.Cutoff : 0, source.Lightmap.Length > 0 ? 1 : 0, source.ColorScale));
        material.SetColor("_Color", Color.white);
        material.renderQueue = transparent ? 3000 : cutout ? 2450 : 2000;
        return material;
    }

    public void Load(string directory, GameObject root, int layer, Transform[]? bones = null, bool lighting = false, string? lightGroup = null, Shader? surfaceShader = null)
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
        Texture2D LoadTexture(string name, TextureWrapMode wrap)
        {
            if (name.Length != 8 || !name.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid stage texture name");
            if (textures.TryGetValue(name, out var texture)) return texture;
            texture = Own(new Texture2D(2, 2, TextureFormat.RGBA32, true));
            if (!texture.LoadImage(new Il2CppStructArray<byte>(File.ReadAllBytes(Path.Combine(directory, "textures", name + ".png"))), true))
                throw new InvalidDataException("Invalid stage texture");
            texture.wrapMode = wrap;
            texture.filterMode = FilterMode.Trilinear;
            textures.Add(name, texture);
            return texture;
        }
        var materials = new Dictionary<uint, Material>();
        foreach (var (key, source) in Definition.Materials)
        {
            var shaded = surfaceShader != null && source.ShadingMode >= 0;
            var material = shaded ? MakeSurfaceMaterial(surfaceShader!, source) : Own(MakeMaterial(shader, source.Blend != 0, source.Blend == 1));
            material.name = "GH3 Stage " + key;
            if (!shaded && source.Cutoff > 0 && !source.SortedTransparency)
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
            if (source.Texture.Length > 0) material.mainTexture = LoadTexture(source.Texture, TextureWrapMode.Repeat);
            if (shaded && source.Lightmap.Length > 0) material.SetTexture("_Gh3Lightmap", LoadTexture(source.Lightmap, TextureWrapMode.Clamp));
            materials.Add(Convert.ToUInt32(key, 16), material);
            SurfaceMaterials.Add(material);
            if (source.Crowd) CrowdMaterials.Add(material);
            if (source.MovieSlot is < -1 or > 31) throw new InvalidDataException("Invalid movie surface slot");
            if (source.MovieSlot >= 0) MovieSurfaces.Add((source.MovieSlot, material, material.mainTexture));
        }
        using var reader = new BinaryReader(File.OpenRead(Path.Combine(directory, "meshes.bin")));
        var header = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(8));
        if (bones == null ? header is not ("GH3STG01" or "GH3STG02") : header != "GH3BND01")
            throw new InvalidDataException("Invalid stage mesh header");
        var lightmapped = header == "GH3STG02";
        var meshCount = reader.ReadInt32();
        if (meshCount < 1 || meshCount > 10000) throw new InvalidDataException("Invalid stage mesh count");
        if (Definition.LightGroups.Length != 0 && Definition.LightGroups.Length != meshCount)
            throw new InvalidDataException("Stage light groups do not match the meshes");
        if (Definition.VisibilityGroups.Length != 0 && Definition.VisibilityGroups.Length != meshCount ||
            Definition.VisibilityGroups.Any(group => group == null || group.Length > 128) ||
            Definition.HiddenGroups.Any(group => string.IsNullOrEmpty(group) || !Definition.VisibilityGroups.Contains(group)))
            throw new InvalidDataException("Invalid scenery visibility groups");
        if (lighting && bones == null && Definition.LightGroups.Length == 0)
            throw new InvalidDataException("Stage light groups are missing. Run prepare-stage.py again.");
        if (Definition.ObjectIds.Length != 0 && Definition.ObjectIds.Length != meshCount ||
            Definition.ObjectIds.Any(id => id.Length > 0 && !Definition.Objects.ContainsKey(id)) || Definition.Objects.Count > 10000)
            throw new InvalidDataException("Invalid stage object groups");
        foreach (var (id, placement) in Definition.Objects)
        {
            if (id.Length != 8 || !id.All(Uri.IsHexDigit) || !placement.Valid) throw new InvalidDataException("Invalid stage object pivot");
            var obj = new GameObject("GH3 Object " + id) { layer = layer };
            obj.transform.SetParent(root.transform, false);
            obj.transform.localPosition = Vector(placement.Position);
            var orientation = placement.Rotation;
            obj.transform.localRotation = new(orientation[0], orientation[1], orientation[2], orientation[3]);
            obj.SetActive(placement.Created);
            Objects.Add(id, obj.transform);
        }
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
            var lightmapUv = lightmapped ? new Vector2[vertexCount] : null;
            var colors = new Color32[vertexCount];
            var weights = bones == null ? null : new List<SkinWeight>(vertexCount * 3);
            var boneCounts = bones == null ? null : new byte[vertexCount];
            for (var v = 0; v < vertexCount; v++)
            {
                vertices[v] = ReadVector(reader);
                normals[v] = ReadVector(reader);
                uv[v] = new Vector2(ReadFloat(reader), ReadFloat(reader));
                colors[v] = new Color32(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
                if (lightmapUv != null) lightmapUv[v] = new Vector2(ReadFloat(reader), ReadFloat(reader));
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
            if (lightmapUv != null) mesh.uv2 = new Il2CppStructArray<Vector2>(lightmapUv);
            mesh.colors32 = new Il2CppStructArray<Color32>(colors);
            mesh.triangles = new Il2CppStructArray<int>(indices);
            if (weights != null && bones != null)
            {
                SetSkinWeights(mesh, boneCounts!, weights.ToArray());
                mesh.bindposes = new Il2CppStructArray<Matrix4x4>(bones.Select(bone =>
                    bone.worldToLocalMatrix * root.transform.localToWorldMatrix).ToArray());
            }
            mesh.RecalculateBounds();
            var shadedMode = surfaceShader != null ? Definition.Materials[materialId.ToString("x8")].ShadingMode : -1;
            var dynamicColors = lighting && shadedMode < 0 && (bones != null || Definition.LightGroups.Length > 0);
            if (dynamicColors || sorted) mesh.MarkDynamic();
            mesh.UploadMeshData(!dynamicColors && !sorted);
            var item = new GameObject(mesh.name) { layer = layer };
            item.transform.SetParent(root.transform, false);
            var objectId = Definition.ObjectIds.Length == 0 ? "" : Definition.ObjectIds[part];
            if (objectId.Length > 0)
            {
                item.transform.SetParent(Objects[objectId], false);
                var inverse = Quaternion.Inverse(Objects[objectId].localRotation);
                item.transform.localPosition = inverse * -Vector(Definition.Objects[objectId].Position);
                item.transform.localRotation = inverse;
            }
            if (dynamicColors && bones == null) LitMeshes.Add(new(mesh, Definition.LightGroups[part], vertices, normals, colors, item.transform));
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
                skin.localBounds = new Bounds(mesh.bounds.center, mesh.bounds.size * 2 + new Vector3(8, 6, 8));
                renderer = skin;
                if (lighting && shadedMode < 0) BandMeshes.Add(new(skin, colors, lightGroup ?? Gh3Checksum.Of("Band")));
                if (sorted) TransparentMeshes.Add(new(this, skin, vertexCount, indices));
            }
            renderer.sharedMaterial = material;
            if (shadedMode > 0) ShadedSurfaces.Add(new(renderer, shadedMode, bones != null ? lightGroup ?? Gh3Checksum.Of("Band") :
                Definition.LightGroups.Length == 0 ? "" : Definition.LightGroups[part]));
            var sourceMaterial = Definition.Materials[materialId.ToString("x8")].Source;
            if (objectId.Length > 0 && sourceMaterial.Length > 0)
                MaterialSurfaces.Add((Convert.ToUInt32(objectId, 16), Gh3QbText.Key(sourceMaterial), renderer, Definition.Materials[materialId.ToString("x8")]));
            Definition.Materials[materialId.ToString("x8")].Animation?.Validate();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            var visibility = Definition.VisibilityGroups.Length == 0 ? "" : Definition.VisibilityGroups[part];
            if (visibility.Length > 0)
            {
                if (!_visibility.TryGetValue(visibility, out var renderers)) _visibility.Add(visibility, renderers = new());
                renderers.Add(renderer);
                renderer.enabled = !Definition.HiddenGroups.Contains(visibility);
            }
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
        Objects.Clear();
        MaterialSurfaces.Clear();
        MovieSurfaces.Clear();
        BandMeshes.Clear();
        ShadedSurfaces.Clear();
        TransparentMeshes.Clear();
        _visibility.Clear();
    }
}
