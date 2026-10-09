using System.Text.Json;

namespace Gh3Skin;

// One GH3 scene material as its template, constant banks, sampler textures, and render-state fields (see prepare-stage.material_record).
internal sealed class StageGh3Material
{
    public string Template { get; set; } = "";
    public float[][] VertexConstants { get; set; } = Array.Empty<float[]>();
    public float[][] PixelConstants { get; set; } = Array.Empty<float[]>();
    public string[] Textures { get; set; } = Array.Empty<string>();
    public int[] Addressing { get; set; } = Array.Empty<int>();
    public long Flags { get; set; }
    public int Blend { get; set; }
    public int AlphaRef { get; set; }
    public float DrawOrder { get; set; }

    public void Validate()
    {
        if (Template.Length != 8 || !Template.All(Uri.IsHexDigit) || VertexConstants.Length > 64 || PixelConstants.Length > 64 ||
            VertexConstants.Concat(PixelConstants).Any(row => row.Length != 4 || !row.All(float.IsFinite)) ||
            Textures.Length > 16 || Addressing.Length != Textures.Length || Addressing.Any(value => value is < 0 or > 255) ||
            Textures.Any(name => name.Length != 0 && (name.Length != 8 || !name.All(Uri.IsHexDigit))) ||
            Flags is < 0 or > uint.MaxValue || Blend is < 0 or > 255 || AlphaRef is < 0 or > 255 || !float.IsFinite(DrawOrder))
            throw new InvalidDataException("Invalid GH3 material");
    }
}

// GH3's material bind (GH3.exe 0x648f20): flags +0xcc, blend mode +0xd8, alpha reference +0xe8.
// Values use Unity's BlendMode, BlendOp, CompareFunction, and CullMode numbering.
internal readonly record struct StageGh3RenderState(int SrcBlend, int DstBlend, int BlendOp, bool ZWrite, int ZTest, int Cull,
    bool AlphaTest, int AlphaRef, bool Blended, int Queue)
{
    // D3DBLEND (1 ZERO .. 10 INVDESTCOLOR) to Unity BlendMode.
    private static readonly int[] BlendFactors = { 0, 0, 1, 3, 6, 5, 10, 7, 8, 2, 4 };
    // D3DBLENDOP (1 ADD .. 5 MAX) to Unity BlendOp.
    private static readonly int[] BlendOps = { 0, 0, 1, 2, 3, 4 };
    // Packed colour blend per mode: bits 0-4 source factor, 5-7 operation, 8-15 destination factor (GH3.exe 0x6491d2).
    private static readonly int[] Modes = { 0x122, 0x225, 0x265, 0x625, 0x521, 0x229, 0x129, 0x222, 0x625, 0x622, 0x229, 0x26a, 0x262 };

    public static StageGh3RenderState From(long flags, int blend, int alphaRef, float drawOrder)
    {
        var packed = Modes[blend is >= 0 and < 13 ? blend : 0];
        var blended = packed != Modes[0];
        var alphaTest = (flags & 0x40) == 0;
        // Bit 3 disables culling; otherwise bit 8 selects counter-clockwise culling. The stage meshes reverse GH3's winding.
        var cull = (flags & 0x08) != 0 ? 0 : (flags & 0x100) != 0 ? 1 : 2;
        var order = (int)Math.Clamp(Math.Round(drawOrder), -400, 400);
        return new(BlendFactors[packed & 31], BlendFactors[packed >> 8 & 255], BlendOps[packed >> 5 & 7], (flags & 0x20) == 0,
            (flags & 0x10) != 0 ? 8 : 4, cull, alphaTest, alphaRef, blended, (blended ? 3000 : alphaTest ? 2450 : 2000) + order);
    }

    // Sampler address byte (+0xea + sampler): bit 0 clamps U, bit 1 clamps V.
    public static (bool ClampU, bool ClampV) Address(int value) => ((value & 1) != 0, (value & 2) != 0);
}

// The templates compiled into render/stage.bundle (written by pipeline/render/shaders.py).
internal sealed class StageShaderTemplates
{
    // Extra: the vertex stream that texture coordinates 6-7 carry ("position1", "binormal1", or none).
    public sealed record Template(int Index, string Name, string Shading, string Extra);
    private readonly Dictionary<string, Template> _templates;
    public IReadOnlyDictionary<string, string> Untranslated { get; }

    private StageShaderTemplates(Dictionary<string, Template> templates, Dictionary<string, string> untranslated)
    {
        _templates = templates;
        Untranslated = untranslated;
    }

    public bool TryGet(string checksum, out Template template) => _templates.TryGetValue(checksum, out template!);

    public static StageShaderTemplates Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("version").GetInt32() != 1) throw new InvalidDataException("Unknown stage template table");
        var templates = new Dictionary<string, Template>();
        foreach (var row in root.GetProperty("templates").EnumerateObject())
        {
            var index = row.Value.GetProperty("index").GetInt32();
            if (row.Name.Length != 8 || !row.Name.All(Uri.IsHexDigit) || index is < 1 or > 4096) throw new InvalidDataException("Invalid stage template");
            templates.Add(row.Name, new(index, row.Value.GetProperty("name").GetString() ?? "", row.Value.GetProperty("shading").GetString() ?? "",
                row.Value.TryGetProperty("extraStream", out var extra) ? extra.GetString() ?? "" : ""));
        }
        var untranslated = root.TryGetProperty("untranslated", out var failures)
            ? failures.EnumerateObject().ToDictionary(row => row.Name, row => row.Value.GetString() ?? "") : new();
        return new(templates, untranslated);
    }
}
