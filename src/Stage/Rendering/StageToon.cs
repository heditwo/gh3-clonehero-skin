using System.Numerics;

namespace Gh3Skin;

// Toon (cel) material of a character (stage.json `toon`, from the GLB extra gh3Toon; PACKAGE-FORMAT.md section 6.5).
// Texture names are runtime texture names of the cache folder.
internal sealed class StageToon
{    // The softness of a toon material without one (PACKAGE-FORMAT.md section 6.5).
    private const float DefaultSoftness = 0.05f;

    public float Threshold { get; set; } = 0.5f;
    public float Softness { get; set; } = DefaultSoftness;
    public string ShadeTexture { get; set; } = "";
    public float[] ShadeColor { get; set; } = { 1, 1, 1 };
    public string ShadeMaskTexture { get; set; } = "";
    public float[] RimColor { get; set; } = { 0, 0, 0 };
    public float RimWidth { get; set; }
    public string SphereTexture { get; set; } = "";
    public string SphereMode { get; set; } = "add";
    public StageToonOutline? Outline { get; set; }

    // The shader divides by the softness band; a zero band would be undefined in smoothstep.
    public const float MinimumSoftness = 0.001f;

    public void Validate()
    {
        static bool Color(float[] c) => c.Length == 3 && c.All(v => float.IsFinite(v) && v is >= 0 and <= 4);
        if (!float.IsFinite(Threshold) || Threshold is < 0 or > 1 || !float.IsFinite(Softness) || Softness is < 0 or > 0.5f ||
            !Color(ShadeColor) || !Color(RimColor) || !float.IsFinite(RimWidth) || RimWidth is < 0 or > 1 || SphereMode is not ("add" or "multiply"))
            throw new InvalidDataException("Invalid toon material");
        Outline?.Validate();
    }

    // _Gh3Toon: threshold, softness, shade texture, mask texture.
    public Vector4 Parameters => new(Threshold, Math.Max(Softness, MinimumSoftness), ShadeTexture.Length > 0 ? 1 : 0, ShadeMaskTexture.Length > 0 ? 1 : 0);
    // _Gh3ToonShade: shade color, sphere mode (0 none, 1 multiply, 2 add).
    public Vector4 Shade => new(ShadeColor[0], ShadeColor[1], ShadeColor[2], SphereTexture.Length == 0 ? 0 : SphereMode == "multiply" ? 1 : 2);
    // _Gh3ToonRim: rim color, rim width.
    public Vector4 Rim => new(RimColor[0], RimColor[1], RimColor[2], RimWidth);
}

internal sealed class StageToonOutline
{    // The widest outline (metres) that the format allows.
    private const float MaximumWidth = 0.05f;

    public float Width { get; set; }
    public float[] Color { get; set; } = { 0, 0, 0 };
    public bool Textured { get; set; }

    public void Validate()
    {
        if (!float.IsFinite(Width) || Width is < 0 or > MaximumWidth || Color.Length != 3 || !Color.All(v => float.IsFinite(v) && v is >= 0 and <= 4))
            throw new InvalidDataException("Invalid toon outline");
    }

    // _Gh3ToonOutline: outline color, width (metres at 1 m from the camera).
    public Vector4 Parameters => new(Color[0], Color[1], Color[2], Width);
}

// Draw order of the hair layers of one character (stage.json `hairLayer`, from gh3HairLayer). An `overHair` feature (eyebrows,
// eyelashes) draws after the other opaque parts and marks its pixels in the stencil buffer; the character's `hair` draws
// after it and skips the marked pixels. Each character has its own stencil value, so a feature never shows through the hair
// of another band member. The feature shader discards faces that turn away from the camera, so the back of the head never
// shows it.
internal static class StageHairLayer
{
    public const string OverHair = "overHair";
    public const string Hair = "hair";
    public const int FeatureQueue = 2001;
    public const int HairQueue = 2002;
    // Stencil bits 1-3; the GH3 outlines (StageOutlines) use bit 0.
    public const int StencilMask = 0x0E;
    public const int Characters = 7;

    public static bool Valid(string layer) => layer is "" or OverHair or Hair;

    public static int StencilValue(int character) => (character % Characters + 1) << 1;

    // Opaque and cutout materials keep their queue order inside the layer; transparent hair stays in the transparent queue.
    public static int Queue(string layer, int queue) => layer switch
    {
        OverHair => FeatureQueue,
        Hair => queue < HairQueue ? HairQueue : queue,
        _ => queue,
    };
}
