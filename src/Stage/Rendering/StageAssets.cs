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
    public bool Billboard { get; set; }
    public bool SortedTransparency { get; set; }
    public string Shading { get; set; } = "";
    public string Lightmap { get; set; } = "";
    public float ColorScale { get; set; } = 1;
    public bool? DoubleSided { get; set; }
    public string CrowdRole { get; set; } = "";
    public string Mask { get; set; } = "";
    public string Layer { get; set; } = "";
    public float[] LayerColor { get; set; } = Array.Empty<float>();
    public float[] UvWibble { get; set; } = Array.Empty<float>();
    public StageGh3Material? Gh3 { get; set; }
    public float[] Color { get; set; } = Array.Empty<float>();
    public StageToon? Toon { get; set; }
    public string HairLayer { get; set; } = "";
    public int ShadingMode => Shading switch { "unlit" => 0, "ambient" => 1, "lit" => 2, "toon" => 3, "" => -1, _ => throw new InvalidDataException("Unknown stage shading mode") };
}

internal sealed class StageAssets : IDisposable
{
    internal sealed record LitMesh(Mesh Mesh, string Group, Vector3[] Positions, Vector3[] Normals, Color32[] Colors, Transform Transform);
    internal sealed record LitBandMesh(SkinnedMeshRenderer Renderer, Color32[] Colors, string Group);
    // LightAt overrides the lighting sample position (stage-local) for meshes rendered away from where they appear.
    internal sealed record ShadedSurface(Renderer Renderer, int Mode, string Group, bool Dynamic, System.Numerics.Vector3? LightAt = null);
    [StructLayout(LayoutKind.Sequential)]
    internal struct SkinWeight
    {
        public float Weight;
        public int Bone;
    }
    private readonly List<UnityEngine.Object> _resources = new();
    public StageDefinition Definition { get; private set; } = new();
    public List<Material> SurfaceMaterials { get; } = new();
    public List<Material> CrowdMaterials { get; } = new();
    public List<Material> BillboardMaterials { get; } = new();
    public List<Renderer> CrowdCards { get; } = new();
    public Dictionary<Material, string> CrowdRoles { get; } = new();
    public List<LitMesh> LitMeshes { get; } = new();
    public List<LitBandMesh> BandMeshes { get; } = new();
    public List<ShadedSurface> ShadedSurfaces { get; } = new();
    public List<StageTransparency> TransparentMeshes { get; } = new();
    private readonly Dictionary<string, List<Renderer>> _visibility = new();
    public Dictionary<string, Transform> Objects { get; } = new();
    public List<(uint Object, uint Material, Renderer Renderer, StageMaterial Definition)> MaterialSurfaces { get; } = new();
    public List<(int Slot, Material Material, Texture? Original)> MovieSurfaces { get; } = new();
    public List<(Material Material, StageUvWibble Motion)> WibbleMaterials { get; } = new();
    // Materials drawn by their translated GH3 template; their meshes carry the original GH3 vertex streams.
    public Dictionary<Material, StageShaderTemplates.Template> TemplateMaterials { get; } = new();
    public static StageShaderTemplates? Templates { get; set; }
    private static readonly string[] SamplerNames = { "_MainTex", "_Gh3Lightmap", "_Gh3NormalTex", "_Gh3SpecularTex", "_Gh3LayerTex", "_Gh3MaskTex",
        "_Gh3Tex6", "_Gh3Tex7", "_Gh3Tex8", "_Gh3Tex9" };
    private static readonly int TemplateId = Shader.PropertyToID("_Gh3Template");
    // Each loaded model with hair layers gets the next stencil value (StageHairLayer).
    private static int _hairLayerModels;
    // Check aid: GH3_SKIN_DEBUG_HAIR_LAYER=mark draws the over-hair features in one flat color for each model (magenta, green,
    // yellow); `off` also ignores the hair layers.
    private static readonly string DebugHairLayer = Environment.GetEnvironmentVariable("GH3_SKIN_DEBUG_HAIR_LAYER") ?? "";
    private static readonly HashSet<string> ReportedFallbacks = new();

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

    // GH3's material bind applied to the translated template (see StageGh3RenderState).
    private Material MakeTemplateMaterial(Shader shader, StageGh3Material source, StageShaderTemplates.Template template,
        Func<string, bool, bool, Texture2D> load, bool skinned, bool billboard)
    {
        var state = StageGh3RenderState.From(source.Flags, source.Blend, source.AlphaRef, source.DrawOrder, billboard);
        var material = Own(new Material(shader));
        material.SetInt("_SrcBlend", state.SrcBlend);
        material.SetInt("_DstBlend", state.DstBlend);
        material.SetInt("_BlendOp", state.BlendOp);
        material.SetInt("_ZWrite", state.ZWrite ? 1 : 0);
        material.SetInt("_ZTest", state.ZTest);
        material.SetInt("_Cull", state.Cull);
        material.SetColor("_Color", Color.white);
        material.SetVector(TemplateId, new Vector4 { x = template.Index, y = state.AlphaTest ? 1 : 0, z = state.AlphaRef, w = skinned ? 1 : 0 });
        for (var i = 0; i < Math.Min(source.VertexConstants.Length, 16); i++)
        {
            var c = source.VertexConstants[i];
            material.SetVector("_Gh3V" + i, new Vector4 { x = c[0], y = c[1], z = c[2], w = c[3] });
        }
        for (var i = 0; i < Math.Min(source.PixelConstants.Length, 16); i++)
        {
            var c = source.PixelConstants[i];
            material.SetVector("_Gh3P" + i, new Vector4 { x = c[0], y = c[1], z = c[2], w = c[3] });
        }
        for (var i = 0; i < Math.Min(source.Textures.Length, SamplerNames.Length); i++)
        {
            if (source.Textures[i].Length == 0) continue;
            var (clampU, clampV) = StageGh3RenderState.Address(source.Addressing[i]);
            material.SetTexture(SamplerNames[i], load(source.Textures[i], clampU, clampV));
        }
        material.renderQueue = state.Queue;
        return material;
    }

    private Material MakeSurfaceMaterial(Shader shader, StageMaterial source)
    {
        var transparent = source.Blend != 0;
        var material = Own(new Material(shader));
        material.SetInt("_SrcBlend", (int)(transparent ? BlendMode.SrcAlpha : BlendMode.One));
        material.SetInt("_DstBlend", (int)(source.Blend == 1 ? BlendMode.One : transparent ? BlendMode.OneMinusSrcAlpha : BlendMode.Zero));
        material.SetInt("_ZWrite", transparent ? 0 : 1);
        // GH3 culls back faces unless the material is two-sided; older exports without the flag stay double-sided.
        material.SetInt("_Cull", (int)(source.DoubleSided == false ? CullMode.Back : CullMode.Off));
        var cutout = source.Cutoff > 0 && !source.SortedTransparency;
        if (!float.IsFinite(source.ColorScale) || source.ColorScale is < 1 or > 1000) throw new InvalidDataException("Invalid material color scale");
        material.SetVector("_Gh3Surface", new(source.ShadingMode, cutout ? source.Cutoff : 0, source.Lightmap.Length > 0 ? 1 : 0, source.ColorScale));
        material.SetColor("_Color", Color.white);
        material.renderQueue = transparent ? 3000 : cutout ? 2450 : 2000;
        return material;
    }

    private static readonly int CamRightId = Shader.PropertyToID("_Gh3CamRight");
    private static readonly int CamUpId = Shader.PropertyToID("_Gh3CamUp");

    // GH3's Flare shader draws each quad around its centre along the camera axes, like Billboard_Fast.
    public void FaceBillboards(Camera view)
    {
        var right = view.transform.right;
        var up = view.transform.up;
        foreach (var material in BillboardMaterials)
        {
            material.SetVector(CamRightId, new Vector4 { x = right.x, y = right.y, z = right.z, w = 1 });
            material.SetVector(CamUpId, new Vector4 { x = up.x, y = up.y, z = up.z, w = 0 });
        }
    }

    // Each crowd card is an independent quad; its pivot is the centre of its connected vertices.
    private static Vector3[] CardPivots(Vector3[] vertices, int[] indices)
    {
        var parent = Enumerable.Range(0, vertices.Length).ToArray();
        int Root(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            parent[Root(indices[i + 1])] = Root(indices[i]);
            parent[Root(indices[i + 2])] = Root(indices[i]);
        }
        var sums = new Dictionary<int, (float X, float Y, float Z, int Count)>();
        for (var i = 0; i < vertices.Length; i++)
        {
            var root = Root(i);
            var s = sums.GetValueOrDefault(root);
            sums[root] = (s.X + vertices[i].x, s.Y + vertices[i].y, s.Z + vertices[i].z, s.Count + 1);
        }
        var pivots = new Vector3[vertices.Length];
        for (var i = 0; i < vertices.Length; i++)
        {
            var s = sums[Root(i)];
            pivots[i] = new Vector3 { x = s.X / s.Count, y = s.Y / s.Count, z = s.Z / s.Count };
        }
        return pivots;
    }

    private static readonly int LayerTexId = Shader.PropertyToID("_Gh3LayerTex");
    private static readonly int MaskTexId = Shader.PropertyToID("_Gh3MaskTex");
    private static readonly int LayerColorId = Shader.PropertyToID("_Gh3LayerColor");
    private static readonly int LayersId = Shader.PropertyToID("_Gh3Layers");

    private static readonly int ToonId = Shader.PropertyToID("_Gh3Toon");
    private static readonly int ToonShadeId = Shader.PropertyToID("_Gh3ToonShade");
    private static readonly int ToonRimId = Shader.PropertyToID("_Gh3ToonRim");
    private static readonly int ToonOutlineId = Shader.PropertyToID("_Gh3ToonOutline");
    private static readonly int ToonOptionsId = Shader.PropertyToID("_Gh3ToonOptions");
    private static readonly int SurfaceId = Shader.PropertyToID("_Gh3Surface");
    private const int OutlineMode = 4;

    private static Vector4 Unity(System.Numerics.Vector4 v) => new() { x = v.X, y = v.Y, z = v.Z, w = v.W };

    // Toon shading (Stage.glsl toon()): the shade, mask, and sphere textures use the template samplers 6-8.
    private static void ConfigureToon(Material material, StageToon toon, Func<string, Texture2D> load)
    {
        toon.Validate();
        material.SetVector(ToonId, Unity(toon.Parameters));
        material.SetVector(ToonShadeId, Unity(toon.Shade));
        material.SetVector(ToonRimId, Unity(toon.Rim));
        if (toon.ShadeTexture.Length > 0) material.SetTexture("_Gh3Tex6", load(toon.ShadeTexture));
        if (toon.ShadeMaskTexture.Length > 0) material.SetTexture("_Gh3Tex7", load(toon.ShadeMaskTexture));
        if (toon.SphereTexture.Length > 0) material.SetTexture("_Gh3Tex8", load(toon.SphereTexture));
    }

    // The inverted hull of a toon outline: a second material on the same renderer draws the back faces, pushed out along the
    // normal (Stage.glsl), opaque, with the alpha test of the base texture.
    private Material MakeOutlineMaterial(Shader shader, StageMaterial source, Material main, StageToonOutline outline)
    {
        var material = Own(new Material(shader));
        material.SetInt("_SrcBlend", (int)BlendMode.One);
        material.SetInt("_DstBlend", (int)BlendMode.Zero);
        material.SetInt("_ZWrite", 1);
        material.SetInt("_Cull", (int)CullMode.Front);
        var cutoff = source.Cutoff > 0 ? source.Cutoff : source.Blend != 0 ? 0.5f : 0;
        material.SetVector(SurfaceId, new Vector4 { x = OutlineMode, y = cutoff, w = 1 });
        material.SetVector(ToonOutlineId, Unity(outline.Parameters));
        material.SetVector(ToonOptionsId, new Vector4 { x = outline.Textured ? 1 : 0 });
        material.mainTexture = main.mainTexture;
        material.color = main.color;
        // A transparent surface writes no depth: its hull must draw first, so the surface blends over it.
        material.renderQueue = source.Blend != 0 ? main.renderQueue - 1 : main.renderQueue;
        return material;
    }

    private static void ConfigureHairLayer(Material material, string layer, int stencil)
    {
        material.SetInt("_StencilRef", stencil);
        material.SetInt("_StencilReadMask", StageHairLayer.StencilMask);
        material.SetInt("_StencilWriteMask", StageHairLayer.StencilMask);
        if (layer == StageHairLayer.OverHair)
        {
            material.SetInt("_StencilComp", (int)CompareFunction.Always);
            material.SetInt("_StencilPass", (int)StencilOp.Replace);
            material.SetVector(ToonOptionsId, new Vector4 { y = 1 });
        }
        else material.SetInt("_StencilComp", (int)CompareFunction.NotEqual);
        material.renderQueue = StageHairLayer.Queue(layer, material.renderQueue);
    }

    // GH3 two-layer and masked templates (D_3ChkBox_Pass0_Pass1, D_3ChkBox_Pass1, Wibble_Simple); see pipeline/gh3/scene.py.
    private void ConfigureLayers(Material material, StageMaterial source, Func<string, Texture2D> load)
    {
        if (source.Layer.Length > 0 && (source.LayerColor.Length != 4 || !source.LayerColor.All(float.IsFinite)))
            throw new InvalidDataException("Invalid GH3 layer colour");
        if (source.UvWibble.Length is not (0 or 11) || !source.UvWibble.All(float.IsFinite)) throw new InvalidDataException("Invalid GH3 UV wibble");
        if (source.Layer.Length > 0)
        {
            material.SetTexture(LayerTexId, load(source.Layer));
            var c = source.LayerColor;
            material.SetVector(LayerColorId, new Vector4 { x = c[0], y = c[1], z = c[2], w = c[3] });
        }
        if (source.Mask.Length > 0) material.SetTexture(MaskTexId, load(source.Mask));
        material.SetVector(LayersId, new Vector4 { x = source.Layer.Length > 0 ? 1 : 0, y = source.Mask.Length > 0 ? 1 : 0 });
        if (source.UvWibble.Length == 0) return;
        var motion = new StageUvWibble(source.UvWibble);
        material.SetVector(UvPivotId, new Vector4 { x = motion.PivotU, y = motion.PivotV, z = 1 });
        material.SetVector(UvWibbleId, new Vector4 { x = 1 });
        WibbleMaterials.Add((material, motion));
    }

    private static readonly int UvWibbleId = Shader.PropertyToID("_Gh3UvWibble");
    private static readonly int UvPivotId = Shader.PropertyToID("_Gh3UvPivot");

    public void UpdateUvWibble(double time)
    {
        foreach (var (material, motion) in WibbleMaterials)
        {
            var transform = motion.Transform(time);
            material.SetVector(UvWibbleId, new Vector4 { x = transform.X, y = transform.Y, z = transform.Z, w = transform.W });
        }
    }

    // The face blend shapes of a character with its own skeleton (morphs.bin): for each renderer, the blend shape index
    // of each face slot (-1: the part does not have the slot).
    public List<(SkinnedMeshRenderer Renderer, int[] Slots)> BlendShapes { get; } = new();

    public void Load(string directory, GameObject root, int layer, Transform[]? bones = null, bool lighting = false, string? lightGroup = null, Shader? surfaceShader = null,
        System.Numerics.Vector3? lightAt = null, Matrix4x4[]? bindposes = null, IReadOnlyCollection<int>? parts = null, string? morphs = null)
    {
        if (bindposes != null && (bones == null || bindposes.Length != bones.Length)) throw new InvalidDataException("Bind poses do not match the bones");
        if (parts != null && bones == null) throw new InvalidDataException("Only band models can load some of their parts");
        var section = LoadTiming.Now;
        var model = (parts == null ? StageModelPrefetch.Take(directory, bones != null) : null) ?? StageModelData.Read(directory, bones != null, parts);
        Definition = model.Definition;
        if (Definition.Version != 1 || Definition.Materials.Count > 2048 ||
            Definition.CameraPosition.Length != 3 || Definition.CameraTarget.Length != 3 ||
            !Definition.CameraPosition.Concat(Definition.CameraTarget).All(float.IsFinite) ||
            !float.IsFinite(Definition.FieldOfView) || Definition.FieldOfView < 10 || Definition.FieldOfView > 120 ||
            Definition.PyroPositions.Length > 16 || Definition.PyroPositions.Any(p => p.Length != 3 || !p.All(float.IsFinite)))
            throw new InvalidDataException("Invalid stage definition");
        var shader = Shader.Find("Particles/Standard Unlit");
        if (shader == null) throw new InvalidOperationException("The stage shader is not available");
        var meshPath = Path.Combine(directory, "meshes.bin");
        // GH3STG04 static meshes and GH3BND02 skinned meshes carry the original GH3 vertex streams for the translated templates.
        var textures = new Dictionary<string, Texture2D>();
        Texture2D LoadAddressedTexture(string name, bool clampU, bool clampV)
        {
            var texture = LoadTexture(name, TextureWrapMode.Repeat, cacheSuffix: $":{(clampU ? 1 : 0)}{(clampV ? 1 : 0)}");
            texture.wrapModeU = clampU ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
            texture.wrapModeV = clampV ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
            return texture;
        }
        Texture2D LoadTexture(string name, TextureWrapMode wrap, string cacheSuffix = "")
        {
            if (name.Length != 8 || !name.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid stage texture name");
            var cacheKey = name + cacheSuffix;
            if (textures.TryGetValue(cacheKey, out var texture)) return texture;
            texture = LoadCachedTexture(Path.Combine(directory, "textures", name + ".tex"));
            if (texture == null)
            {
                texture = Own(new Texture2D(2, 2, TextureFormat.RGBA32, true, false));
                if (!texture.LoadImage(new Il2CppStructArray<byte>(File.ReadAllBytes(Path.Combine(directory, "textures", name + ".png"))), true))
                    throw new InvalidDataException("Invalid stage texture");
            }
            texture.wrapMode = wrap;
            texture.filterMode = FilterMode.Trilinear;
            textures.Add(cacheKey, texture);
            return texture;
        }
        LoadTiming.Add("assets: stage.json", section);
        section = LoadTiming.Now;
        var materials = new Dictionary<uint, Material>();
        var outlines = new Dictionary<uint, Material>();
        if (Definition.Materials.Values.Any(m => !StageHairLayer.Valid(m.HairLayer))) throw new InvalidDataException("Unknown hair layer");
        var hairStencil = surfaceShader != null && Definition.Materials.Values.Any(m => m.HairLayer.Length > 0) ? StageHairLayer.StencilValue(_hairLayerModels++) : 0;
        // A part subset (a band model's attachment) loads only the materials and textures of its parts.
        var partMaterials = parts == null ? null : Packages.BandAttachments.PartMaterials(meshPath);
        var usedMaterials = partMaterials == null ? null : parts!.Where(p => p >= 0 && p < partMaterials.Length).Select(p => partMaterials[p]).ToHashSet();
        foreach (var (key, source) in Definition.Materials)
        {
            if (usedMaterials != null && !usedMaterials.Contains(Convert.ToUInt32(key, 16))) continue;
            source.Gh3?.Validate();
            StageShaderTemplates.Template? template = null;
            // Template 00000000 marks GH3's unused placeholder material slot; no mesh draws with it.
            if (source.Gh3 != null && source.Gh3.Template != "00000000" && surfaceShader != null)
            {
                if (Templates != null && Templates.TryGet(source.Gh3.Template, out var found)) template = found;
                else if (ReportedFallbacks.Add(source.Gh3.Template))
                    MelonLoader.MelonLogger.Warning($"GH3 material template {source.Gh3.Template} is not in the stage shader; " +
                        $"{source.Source} uses the approximate surface model. " + (Templates?.Untranslated.GetValueOrDefault(source.Gh3.Template) ?? "Run scripts/regenerate.sh render."));
            }
            if (template != null)
            {
                var generic = MakeTemplateMaterial(surfaceShader!, source.Gh3!, template, LoadAddressedTexture, bones != null, source.Billboard);
                generic.name = "GH3 Stage " + key + " " + template.Name;
                materials.Add(Convert.ToUInt32(key, 16), generic);
                SurfaceMaterials.Add(generic);
                TemplateMaterials.Add(generic, template);
                if (source.Crowd && source.CrowdRole.Length > 0)
                {
                    // Billboard_Fast crowd cards: StageCrowd replaces the card texture with its role's live ped render.
                    CrowdMaterials.Add(generic);
                    CrowdRoles[generic] = source.CrowdRole;
                }
                if (source.MovieSlot is < -1 or > 31) throw new InvalidDataException("Invalid movie surface slot");
                if (source.MovieSlot >= 0) MovieSurfaces.Add((source.MovieSlot, generic, generic.mainTexture));
                continue;
            }
            var shaded = surfaceShader != null && source.ShadingMode >= 0;
            // GH3 draws crowd cards (Billboard_Fast) as opaque alpha-tested quads with depth; the lit peds are baked into the atlas.
            // The cards' baked vertex colours (black feet to ~0.66 at the head) treat 0x80 as full brightness.
            var crowdCard = surfaceShader != null && source.Crowd;
            var material = crowdCard ? MakeSurfaceMaterial(surfaceShader!, new StageMaterial { Shading = "unlit", Cutoff = 0.5f, Blend = 0, ColorScale = 2 }) :
                shaded ? MakeSurfaceMaterial(surfaceShader!, source) : Own(MakeMaterial(shader, source.Blend != 0, source.Blend == 1));
            if (source.Color.Length == 4 && source.Color.All(float.IsFinite))
                material.color = new Color(source.Color[0], source.Color[1], source.Color[2], source.Color[3]);
            material.name = "GH3 Stage " + key;
            if (bones != null && shaded) material.SetInt("_Cull", (int)CullMode.Off);
            if (!shaded && !crowdCard && source.Cutoff > 0 && !source.SortedTransparency)
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
            if (shaded) ConfigureLayers(material, source, name => LoadTexture(name, TextureWrapMode.Repeat));
            if (shaded && source.ShadingMode == 3) ConfigureToon(material, source.Toon ?? new StageToon(), name => LoadTexture(name, TextureWrapMode.Repeat));
            var hairLayer = DebugHairLayer == "off" ? "" : source.HairLayer;
            if (shaded && hairLayer.Length > 0) ConfigureHairLayer(material, hairLayer, hairStencil);
            if (shaded && source.HairLayer == StageHairLayer.OverHair && DebugHairLayer is "mark" or "off")
            {
                material.mainTexture = Texture2D.whiteTexture;
                // One color for each model: magenta, green, yellow, ...
                material.color = hairStencil switch { 2 => new Color(1, 0, 1, 1), 4 => new Color(0, 1, 0, 1), _ => new Color(1, 1, 0, 1) };
                material.SetVector(SurfaceId, new Vector4 { w = 1 });
            }
            if (shaded && source.ShadingMode == 3 && source.Toon?.Outline is { Width: > 0 } line)
            {
                var outline = MakeOutlineMaterial(surfaceShader!, source, material, line);
                outline.name = "GH3 Stage Outline " + key;
                if (hairLayer == StageHairLayer.Hair) ConfigureHairLayer(outline, hairLayer, hairStencil);
                outlines.Add(Convert.ToUInt32(key, 16), outline);
            }
            materials.Add(Convert.ToUInt32(key, 16), material);
            SurfaceMaterials.Add(material);
            if (source.Crowd) CrowdMaterials.Add(material);
            if (source.Billboard && shaded) BillboardMaterials.Add(material);
            if (source.Crowd && source.CrowdRole.Length > 0) CrowdRoles[material] = source.CrowdRole;
            if (source.MovieSlot is < -1 or > 31) throw new InvalidDataException("Invalid movie surface slot");
            if (source.MovieSlot >= 0) MovieSurfaces.Add((source.MovieSlot, material, material.mainTexture));
        }
        LoadTiming.Add("assets: materials and textures", section);
        section = LoadTiming.Now;
        var meshCount = model.Parts.Length;
        if (Definition.LightGroups.Length != 0 && Definition.LightGroups.Length != meshCount)
            throw new InvalidDataException("Stage light groups do not match the meshes");
        if (Definition.VisibilityGroups.Length != 0 && Definition.VisibilityGroups.Length != meshCount ||
            Definition.VisibilityGroups.Any(group => group == null || group.Length > 128) ||
            Definition.HiddenGroups.Any(group => string.IsNullOrEmpty(group) || !Definition.VisibilityGroups.Contains(group)))
            throw new InvalidDataException("Invalid scenery visibility groups");
        if (lighting && bones == null && Definition.LightGroups.Length == 0)
            throw new InvalidDataException("Stage light groups are missing. Run scripts/regenerate.sh venues again.");
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
        var shapes = morphs != null && bones != null ? ReadMorphs(morphs, meshCount) : null;
        for (var part = 0; part < meshCount; part++)
        {
            LoadTiming.Add("assets: objects and renderers", section);
            section = LoadTiming.Now;
            if (model.Parts[part] is not { } data) continue;
            var materialId = data.MaterialId;
            var vertexCount = data.VertexCount;
            if (bones != null && data.MaxBone >= bones.Length) throw new InvalidDataException("Invalid band skin weights");
            var material = materials[materialId];
            var sorted = bones != null && Definition.Materials[materialId.ToString("x8")].SortedTransparency;
            var (vertices, normals, uv, lightmapUv, layerUv, maskUv, colors) = (data.Vertices, data.Normals, data.Uv, data.LightmapUv, data.LayerUv, data.MaskUv, data.Colors);
            var (streams, binormals, pivotXy, pivotZ, binormal1Xy, binormal1Z) = (data.Streams, data.Binormals, data.PivotXy, data.PivotZ, data.Binormal1Xy, data.Binormal1Z);
            var (tangents, boneCounts, weights, indices) = (data.Tangents, data.BoneCounts, data.Weights, data.Indices);
            LoadTiming.Add("assets: mesh read", section);
            section = LoadTiming.Now;
            var mesh = Own(new Mesh());
            mesh.name = "GH3 Stage Mesh " + part;
            mesh.vertices = new Il2CppStructArray<Vector3>(vertices);
            var crowdCards = bones == null && surfaceShader != null && Definition.Materials[materialId.ToString("x8")].Crowd;
            var cards = crowdCards || bones == null && surfaceShader != null && Definition.Materials[materialId.ToString("x8")].Billboard;
            mesh.normals = new Il2CppStructArray<Vector3>(cards ? CardPivots(vertices, indices) : normals);
            var partMaterial = Definition.Materials[materialId.ToString("x8")];
            var templateMesh = TemplateMaterials.ContainsKey(material);
            if (templateMesh)
            {
                // The translated GH3 vertex shader reads the original streams (see pipeline/gh3/shader.py VERTEX_MAIN).
                mesh.uv = new Il2CppStructArray<Vector2>(streams[0]);
                mesh.uv2 = new Il2CppStructArray<Vector2>(streams[1]);
                mesh.uv3 = new Il2CppStructArray<Vector2>(streams[2]);
                mesh.uv4 = new Il2CppStructArray<Vector2>(streams[3]);
                mesh.uv5 = new Il2CppStructArray<Vector2>(streams[4]);
                mesh.uv6 = new Il2CppStructArray<Vector2>(streams[5]);
                var binormal1 = TemplateMaterials[material].Extra == "binormal1";
                if (tangents == null)
                {
                    mesh.uv7 = new Il2CppStructArray<Vector2>(binormal1 ? binormal1Xy : pivotXy);
                    mesh.uv8 = new Il2CppStructArray<Vector2>(binormal1 ? binormal1Z : pivotZ);
                }
                mesh.tangents = new Il2CppStructArray<Vector4>(tangents ?? binormals);
            }
            else
            {
                mesh.uv = new Il2CppStructArray<Vector2>(uv);
                if (lightmapUv != null) mesh.uv2 = new Il2CppStructArray<Vector2>(lightmapUv);
                if (layerUv != null && partMaterial.Layer.Length > 0) mesh.uv3 = new Il2CppStructArray<Vector2>(layerUv);
                if (maskUv != null && partMaterial.Mask.Length > 0) mesh.uv4 = new Il2CppStructArray<Vector2>(maskUv);
            }
            mesh.colors32 = new Il2CppStructArray<Color32>(colors);
            mesh.triangles = new Il2CppStructArray<int>(indices);
            if (weights != null && bones != null)
            {
                SetSkinWeights(mesh, boneCounts!, weights);
                // Characters bring the inverse bind matrices of their GLB; the library members use their rig at bind time.
                mesh.bindposes = new Il2CppStructArray<Matrix4x4>(bindposes ?? bones.Select(bone =>
                    bone.worldToLocalMatrix * root.transform.localToWorldMatrix).ToArray());
            }
            var slots = shapes?[part].Count > 0 ? Enumerable.Repeat(-1, Packages.CharacterSkeleton.FaceSlots.Length).ToArray() : null;
            if (slots != null)
                foreach (var (slot, deltas) in shapes![part])
                {
                    if (deltas.Length != vertexCount) throw new InvalidDataException("Invalid character blend shape");
                    slots[slot] = mesh.blendShapeCount;
                    mesh.AddBlendShapeFrame(Packages.CharacterSkeleton.FaceSlots[slot], 100, new Il2CppStructArray<Vector3>(deltas), null, null);
                }
            mesh.RecalculateBounds();
            if (templateMesh && TemplateMaterials[material].Extra == "position1")
            {
                // Billboard templates place each corner around its pivot along the camera axes.
                var reach = 0f;
                for (var v = 0; v < vertexCount; v++)
                    reach = Math.Max(reach, (vertices[v] - new Vector3(pivotXy[v].x, pivotXy[v].y, pivotZ[v].x)).magnitude);
                var bounds = mesh.bounds;
                bounds.Expand(2 * reach);
                mesh.bounds = bounds;
            }
            if (cards && !templateMesh)
            {
                var bounds = mesh.bounds;
                bounds.Expand(crowdCards ? 4 : bounds.size.magnitude);
                mesh.bounds = bounds;
            }
            var shadedMode = templateMesh ? TemplateMaterials[material].Shading switch { "lit" => 2, "ambient" => 1, _ => 0 } :
                surfaceShader != null ? Definition.Materials[materialId.ToString("x8")].ShadingMode : -1;
            var dynamicColors = lighting && shadedMode < 0 && (bones != null || Definition.LightGroups.Length > 0);
            if (dynamicColors || sorted) mesh.MarkDynamic();
            mesh.UploadMeshData(!dynamicColors && !sorted);
            LoadTiming.Add("assets: mesh upload", section);
            section = LoadTiming.Now;
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
                if (crowdCards) CrowdCards.Add(renderer);
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
                if (slots != null) BlendShapes.Add((skin, slots));
            }
            renderer.sharedMaterial = material;
            if (outlines.TryGetValue(materialId, out var outlineMaterial))
                renderer.sharedMaterials = new Il2CppReferenceArray<Material>(new[] { material, outlineMaterial });
            if (shadedMode > 0) ShadedSurfaces.Add(new(renderer, shadedMode, bones != null ? lightGroup ?? Gh3Checksum.Of("Band") :
                Definition.LightGroups.Length == 0 ? "" : Definition.LightGroups[part], bones != null || objectId.Length > 0, lightAt));
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
        LoadTiming.Add("assets: objects and renderers", section);
    }

    // morphs.bin of CharacterImporter: for each mesh part, its shapes (face slot, position deltas).
    private static List<(int Slot, Vector3[] Deltas)>[] ReadMorphs(string path, int meshCount)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (System.Text.Encoding.ASCII.GetString(reader.ReadBytes(8)) != "GH3MRP01" || reader.ReadInt32() != meshCount)
            throw new InvalidDataException("Invalid character blend shapes");
        // A shape: the face slot (byte), the vertex count, and the position deltas.
        var result = new List<(int, Vector3[])>[meshCount];
        for (var part = 0; part < meshCount; part++)
        {
            result[part] = new();
            var count = reader.ReadInt32();
            if (count is < 0 or > 6) throw new InvalidDataException("Invalid character blend shapes");
            for (var k = 0; k < count; k++)
            {
                var slot = reader.ReadByte();
                var vertices = reader.ReadInt32();
                if (slot >= Packages.CharacterSkeleton.FaceSlots.Length || vertices is < 1 or > 65535) throw new InvalidDataException("Invalid character blend shape");
                var deltas = new Vector3[vertices];
                for (var v = 0; v < vertices; v++) deltas[v] = new Vector3(ReadFloat(reader), ReadFloat(reader), ReadFloat(reader));
                result[part].Add((slot, deltas));
            }
        }
        return result;
    }

    public static int TexturesUploaded;
    public static double TextureMilliseconds;

    // Package cache texture (Packages.TextureCodec): BC1, BC3, or RGBA32 with its full mip chain, uploaded without decoding.
    private Texture2D? LoadCachedTexture(string path)
    {
        if (!File.Exists(path)) return null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var data = File.ReadAllBytes(path);
        var (format, width, height, mips, flags) = Packages.TextureCodec.ReadHeader(data);
        var unityFormat = (TextureFormat)format;
        if (!SystemInfo.SupportsTextureFormat(unityFormat)) throw new InvalidDataException($"This GPU does not support {unityFormat}; delete the package cache");
        var texture = Own(new Texture2D(width, height, unityFormat, mips > 1, (flags & Packages.TextureCodec.FlagLinear) != 0));
        SkinAssets.LoadRaw(texture, data, Packages.TextureCodec.HeaderSize);
        texture.Apply(false, true);
        TexturesUploaded++;
        TextureMilliseconds += clock.Elapsed.TotalMilliseconds;
        return texture;
    }

    private static float ReadFloat(BinaryReader reader)
    {
        var value = reader.ReadSingle();
        return float.IsFinite(value) ? value : throw new InvalidDataException("Invalid stage vertex");
    }

    internal static void SetSkinWeights(Mesh mesh, byte[] counts, SkinWeight[] weights)
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

    public static Vector3 Vector(float[] value) => new(value[0], value[1], value[2]);

    public void Dispose()
    {
        foreach (var resource in _resources)
            if (resource != null) UnityEngine.Object.Destroy(resource);
        _resources.Clear();
        SurfaceMaterials.Clear();
        CrowdMaterials.Clear();
        CrowdCards.Clear();
        CrowdRoles.Clear();
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
