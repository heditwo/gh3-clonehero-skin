using System.Text.Json;
using UnityEngine;
using UnityEngine.UI;

namespace Gh3Skin;

// GH3's centre-screen gameplay messages ("100 Note Streak!", "Star Power Ready") drawn from the original message font.
internal sealed class MessageHud
{
    private sealed record Glyph(Rect Uv, float Width, float Height, float X, float Y);
    private const float Center = 800;
    private const float Baseline = 300;
    private const float GlyphScale = 1.9f;
    private readonly RectTransform _root;
    private readonly Texture2D _font;
    private readonly Dictionary<char, Glyph> _glyphs = new();
    private readonly List<RawImage> _images = new();
    private string _text = "";
    private Color _color;
    private float _start = float.NegativeInfinity;
    private float _duration;
    private bool _wobble;
    private int _lastCombo;
    private bool _wasReady;

    public MessageHud(Transform parent, string assets)
    {
        _root = HudVisual.Group("Gameplay message", parent);
        _font = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!_font.LoadImage(new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>(File.ReadAllBytes(Path.Combine(assets, "message-font.png"))), true))
            throw new InvalidDataException("Invalid message font atlas");
        _font.filterMode = FilterMode.Bilinear;
        _font.hideFlags = HideFlags.DontUnloadUnusedAsset;
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(assets, "message-font.json")));
        foreach (var glyph in json.RootElement.GetProperty("glyphs").EnumerateObject())
        {
            float F(string name, int index) => glyph.Value.GetProperty(name)[index].GetSingle();
            var u0 = F("uv", 0); var v0 = F("uv", 1); var u1 = F("uv", 2); var v1 = F("uv", 3);
            _glyphs[glyph.Name[0]] = new(new Rect(u0, 1 - v1, u1 - u0, v1 - v0), F("size", 0), F("size", 1), F("offset", 0), F("offset", 1));
        }
    }

    public void Update(GameplayState state, float time)
    {
        if (state.Combo < _lastCombo) _lastCombo = 0;
        if (state.Combo >= 100 && state.Combo / 100 > _lastCombo / 100)
            Show($"{state.Combo / 100 * 100} Note Streak!", new Color(1f, 0.96f, 0.62f, 1), time, 2.2f, false);
        _lastCombo = state.Combo;
        var ready = state.StarPower >= 0.5f && !state.StarPowerActive;
        if (ready && !_wasReady) Show("Star Power Ready", new Color(0.55f, 0.86f, 0.96f, 1), time, 1.8f, true);
        _wasReady = ready;
        Draw(time);
    }

    private void Show(string text, Color color, float time, float duration, bool wobble)
    {
        _text = text;
        _color = color;
        _start = time;
        _duration = duration;
        _wobble = wobble;
        while (_images.Count < text.Length)
        {
            var image = HudVisual.Image(_root, "glyph", _font, new Rect(0, 0, 1, 1));
            _images.Add(image);
        }
    }

    private void Draw(float time)
    {
        var age = time - _start;
        var visible = age >= 0 && age < _duration;
        if (!visible)
        {
            foreach (var image in _images) image.enabled = false;
            return;
        }
        // Enters large and fades in, settles, then fades out; Star Power Ready shrinks away at the end.
        var enter = Math.Clamp(age / 0.25f, 0, 1);
        var leave = Math.Clamp((_duration - age) / 0.35f, 0, 1);
        var scale = GlyphScale * (1 + 0.5f * (1 - enter) * (1 - enter));
        if (_wobble) scale *= 0.55f + 0.45f * leave;
        var alpha = enter * leave;
        var width = 0f;
        foreach (var character in _text) width += _glyphs.TryGetValue(character, out var g) ? g.Width : 14;
        var x = Center - width * scale / 2;
        for (var i = 0; i < _images.Count; i++)
        {
            var image = _images[i];
            if (i >= _text.Length || !_glyphs.TryGetValue(_text[i], out var glyph) || glyph.Uv.width <= 0)
            {
                image.enabled = false;
                if (i < _text.Length) x += (_glyphs.TryGetValue(_text[i], out var space) ? space.Width : 14) * scale;
                continue;
            }
            var tilt = _wobble ? MathF.Sin(age * 9 + i * 0.7f) * 4 * (1 - leave * 0.5f) : 0;
            image.enabled = true;
            image.uvRect = glyph.Uv;
            image.color = new Color(_color.r, _color.g, _color.b, alpha);
            HudVisual.Layout(image, new Rect(x + glyph.X * scale, Baseline - 40 * scale + glyph.Y * scale + tilt, glyph.Width * scale, glyph.Height * scale));
            x += glyph.Width * scale;
        }
    }
}
