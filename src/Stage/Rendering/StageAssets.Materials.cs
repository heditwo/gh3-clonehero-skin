using UnityEngine;
using UnityEngine.Rendering;

namespace Gh3Skin;

// The stage materials: GH3 templates with GH3's material bind, the surface model, toon materials and outlines, hair
// layers, layered templates, crowd cards, billboards, and UV wibble.
internal sealed partial class StageAssets
{
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
        material.renderQueue = transparent ? RenderQueue.Transparent : RenderQueue.Geometry;
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
        material.renderQueue = transparent ? RenderQueue.Transparent : cutout ? RenderQueue.AlphaTest : RenderQueue.Geometry;
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
}
