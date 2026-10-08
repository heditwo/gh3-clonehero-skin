using System.Text.Json;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace Gh3Skin;

// A GH3 font atlas and glyph table exported by prepare-fonts.py. Sizes and offsets are in atlas texels.
internal sealed class HudFont
{
    internal sealed record Glyph(Rect Uv, float Width, float Height, float X, float Y);

    private readonly Dictionary<char, Glyph> _glyphs = new();

    public Texture2D Atlas { get; }
    public float Baseline { get; }

    public HudFont(string assets, string name)
    {
        Atlas = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!Atlas.LoadImage(new Il2CppStructArray<byte>(File.ReadAllBytes(Path.Combine(assets, name + ".png"))), true))
            throw new InvalidDataException($"Invalid font atlas: {name}");
        Atlas.filterMode = FilterMode.Bilinear;
        Atlas.wrapMode = TextureWrapMode.Clamp;
        Atlas.hideFlags = HideFlags.DontUnloadUnusedAsset;
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(assets, name + ".json")));
        Baseline = json.RootElement.TryGetProperty("baseline", out var baseline) ? baseline.GetSingle() : 0;
        foreach (var glyph in json.RootElement.GetProperty("glyphs").EnumerateObject())
        {
            float F(string property, int index) => glyph.Value.GetProperty(property)[index].GetSingle();
            var u0 = F("uv", 0); var v0 = F("uv", 1); var u1 = F("uv", 2); var v1 = F("uv", 3);
            _glyphs[glyph.Name[0]] = new Glyph(new Rect(u0, 1 - v1, u1 - u0, v1 - v0), F("size", 0), F("size", 1), F("offset", 0), F("offset", 1));
        }
    }

    public bool TryGet(char character, out Glyph glyph) => _glyphs.TryGetValue(character, out glyph!);
}
