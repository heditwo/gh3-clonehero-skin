using UnityEngine;
using UnityEngine.UI;

namespace Gh3Skin;

// GH3's centre-screen gameplay messages drawn from the original message font. Measured: timing and scale keyframes
// from 60 fps recordings of original GH3 at 1600x900 (My Curse, The Dive). "Star Power Ready" is not drawn here: the
// GH3 frames of ReadyBannerHud already contain that text. CenterMessageQueue decides when a message starts.
internal sealed class MessageHud
{
    private readonly record struct Key(float Time, float Scale, float Alpha, float Warm);

    // "N Note Streak!": a faint oversized ghost, a small white flash with a light streak, then a yellow swell and
    // a decaying size pulse. Scale is relative to the font's natural size.
    private static readonly Key[] StreakKeys =
    {
        new(0.00f, 3.3f, 0.30f, 0), new(0.14f, 3.1f, 0.00f, 0), new(0.15f, 1.15f, 0.85f, 0), new(0.40f, 1.95f, 1, 0.6f),
        new(0.55f, 2.22f, 1, 1), new(0.95f, 1.51f, 1, 1), new(1.35f, 1.90f, 1, 1), new(1.55f, 1.49f, 1, 1),
        new(1.80f, 1.73f, 1, 1), new(2.05f, 1.36f, 1, 1), new(2.15f, 1.63f, 1, 1), new(2.30f, 1.26f, 1, 1),
        new(2.45f, 1.15f, 0, 1),
    };
    private const float CenterX = 800;
    // GH3 lays the message out with extra tracking between glyphs and a wide word space.
    private const float Tracking = 1;
    private const float Space = 22;
    private const float CenterY = 345;
    // Tuned: the light streak texture is a horizontal ramp (exponent) with a vertical Gaussian falloff.
    private const float StreakRampExponent = 1.6f;
    private const float StreakFalloff = 5;
    // Tuned: the message scale on this canvas, and the warm tint at Warm = 1 (green and blue reduced by 4 % and 40 %).
    private const float MessageScale = 1.2f;
    private const float WarmGreenLoss = 0.04f;
    private const float WarmBlueLoss = 0.40f;
    // Tuned: the baseline offset.
    private const float BaselineOffset = 15;
    // Measured: the light streak peaks 0.35 s into the message and lasts 0.25 s on each side.
    private const float StreakPeak = 0.35f;
    private const float StreakHalfWidth = 0.25f;
    private const float StreakAlpha = 0.75f;
    // Tuned: the streak image placement: right edge at 0.2 of the text width from the center, 720 x 80 units.
    private const float StreakRightFraction = 0.2f;
    private const float StreakWidth = 720;
    private const float StreakHeight = 80;
    private const float MinimumKeyInterval = 0.0001f;
    private readonly RectTransform _root;
    private readonly HudFont _font;
    private readonly Texture2D _streakTexture;
    private readonly RawImage _streak;
    private readonly List<RawImage> _images = new();
    private string _text = "";
    private Key[] _keys = StreakKeys;
    private float _start = float.NegativeInfinity;

    public static float StreakSeconds => StreakKeys[^1].Time;

    public MessageHud(Transform parent, string fonts)
    {
        _root = HudVisual.Group("Gameplay message", parent);
        _font = new HudFont(fonts, "message-font");
        _streakTexture = StreakTexture();
        _streak = HudVisual.Image(_root, "streak", _streakTexture, new Rect(0, 0, 1, 1));
        _streak.enabled = false;
    }

    // A soft horizontal light bar that is brightest at its right end.
    private static Texture2D StreakTexture()
    {
        const int width = 128, height = 16;
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var pixels = new Color[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var v = (y + 0.5f) / height * 2 - 1;
                var a = MathF.Pow((x + 0.5f) / width, StreakRampExponent) * MathF.Exp(-v * v * StreakFalloff);
                // Tuned: warm white.
                pixels[y * width + x] = new Color { r = 1, g = 0.97f, b = 0.85f, a = a };
            }
        texture.SetPixels(new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color>(pixels));
        texture.Apply(false);
        texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return texture;
    }

    public void Update(CenterMessageQueue messages, float time)
    {
        if (messages.StreakStart != _start) Show($"{messages.StreakCombo} Note Streak!", StreakKeys, messages.StreakStart);
        Draw(time);
    }

    private void Show(string text, Key[] keys, float start)
    {
        _text = text;
        _keys = keys;
        _start = start;
        while (_images.Count < text.Length) _images.Add(HudVisual.Image(_root, "glyph", _font.Atlas, new Rect(0, 0, 1, 1)));
    }

    private float Advance(char character) => character == ' ' ? Space : _font.TryGet(character, out var glyph) ? glyph.Width + Tracking : Space;

    private static Key Sample(Key[] keys, float age)
    {
        if (age <= keys[0].Time) return keys[0];
        for (var i = 1; i < keys.Length; i++)
        {
            if (age > keys[i].Time) continue;
            var a = keys[i - 1];
            var b = keys[i];
            var t = (age - a.Time) / Math.Max(MinimumKeyInterval, b.Time - a.Time);
            t = t * t * (3 - 2 * t);
            return new(age, a.Scale + (b.Scale - a.Scale) * t, a.Alpha + (b.Alpha - a.Alpha) * t, a.Warm + (b.Warm - a.Warm) * t);
        }
        return keys[^1];
    }

    private void Draw(float time)
    {
        var age = time - _start;
        if (age < 0 || age > _keys[^1].Time)
        {
            foreach (var image in _images) image.enabled = false;
            _streak.enabled = false;
            return;
        }
        var key = Sample(_keys, age);
        var scale = key.Scale * MessageScale;
        var color = new Color { r = 1, g = 1 - WarmGreenLoss * key.Warm, b = 1 - WarmBlueLoss * key.Warm, a = key.Alpha };
        var width = 0f;
        foreach (var character in _text) width += Advance(character);
        var x = CenterX - width * scale / 2;
        for (var i = 0; i < _images.Count; i++)
        {
            var image = _images[i];
            if (i >= _text.Length || !_font.TryGet(_text[i], out var glyph) || glyph.Uv.width <= 0)
            {
                image.enabled = false;
                if (i < _text.Length) x += Advance(_text[i]) * scale;
                continue;
            }
            image.enabled = true;
            image.uvRect = glyph.Uv;
            image.color = color;
            // The font stores each glyph's distance below the baseline (descenders are positive).
            var bottom = CenterY + BaselineOffset * scale + glyph.X * scale;
            HudVisual.Layout(image, new Rect(x, bottom - glyph.Height * scale, glyph.Width * scale, glyph.Height * scale));
            x += Advance(_text[i]) * scale;
        }
        // The light streak sweeps in from the left while the streak message flashes white.
        var streak = Math.Clamp(1 - Math.Abs(age - StreakPeak) / StreakHalfWidth, 0, 1);
        _streak.enabled = streak > 0;
        if (_streak.enabled)
        {
            _streak.color = new Color { r = 1, g = 1, b = 1, a = StreakAlpha * streak };
            var right = CenterX + width * scale * StreakRightFraction;
            HudVisual.Layout(_streak, new Rect(right - StreakWidth, CenterY - StreakHeight / 2, StreakWidth, StreakHeight));
        }
    }
}
