using System.Text.Json;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.UI;

namespace Gh3Skin;

// The end-of-song "You Rock!" text and the Fret_Flames movie that GuitarEvent_SongWon_Spawned plays over the screen.
internal sealed class YouRockHud
{
    // GH3 screen elements use 1280 x 720 units; this canvas is 1920 x 1080.
    private const float Units = 1.5f;
    // pipeline/hud/fonts.py writes the 45 movie frames (29.97 fps) into a 9-column atlas.
    private const int FlameFrames = 45;
    private const int FlameColumns = 9;
    private const float FlameRate = 29.97f;
    private static readonly Color TextColor = new() { r = 223 / 255f, g = 223 / 255f, b = 223 / 255f, a = 1 };
    private readonly RectTransform _root;
    private readonly HudFont _font;
    private readonly float _lineHeight;
    private readonly YouRockAnimation _animation;
    private readonly RawImage? _flames;
    private readonly List<RawImage> _letters = new();

    public YouRockHud(Transform parent, string fonts, string flames)
    {
        _root = HudVisual.Group("You Rock", parent);
        _font = new HudFont(fonts, "yourock-font");
        using (var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(fonts, "yourock-font.json"))))
            _lineHeight = json.RootElement.GetProperty("lineHeight").GetSingle();
        var fit = new HudFont(fonts, "fit-font");
        var width = 0f;
        foreach (var character in YouRockAnimation.Text)
            if (fit.TryGet(character, out var glyph)) width += glyph.Width;
        UnityEngine.Object.Destroy(fit.Atlas);
        _animation = new YouRockAnimation(width);
        if (File.Exists(flames))
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!texture.LoadImage(new Il2CppStructArray<byte>(File.ReadAllBytes(flames)), true)) throw new InvalidDataException(flames);
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
            _flames = HudVisual.Image(_root, "flames", texture, new Rect(0, 0, 1920, 1080));
            _flames.enabled = false;
        }
        for (var i = 0; i < _animation.Count * 2; i++)
        {
            var image = HudVisual.Image(_root, "letter", _font.Atlas, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
            image.enabled = false;
            _letters.Add(image);
        }
    }

    public void Update(double age)
    {
        var frame = (int)Math.Floor(age * FlameRate);
        if (_flames != null)
        {
            _flames.enabled = age >= 0 && frame < FlameFrames;
            if (_flames.enabled)
            {
                const int rows = (FlameFrames + FlameColumns - 1) / FlameColumns;
                _flames.uvRect = new Rect((float)(frame % FlameColumns) / FlameColumns, 1 - (float)(frame / FlameColumns + 1) / rows,
                    1f / FlameColumns, 1f / rows);
            }
        }
        var index = 0;
        foreach (var letter in _animation.Sample(age))
        {
            var image = _letters[index++];
            if (!_font.TryGet(letter.Character, out var glyph) || glyph.Uv.width <= 0)
            {
                image.enabled = false;
                continue;
            }
            var scale = letter.Shadow ? YouRockAnimation.ShadowScale * letter.Scale : new System.Numerics.Vector2(letter.Scale);
            // Each character is centred (just = [center center]) in a box one font line high; glyphs sit on its bottom.
            var size = new Vector2(glyph.Width * scale.X, glyph.Height * scale.Y);
            var centreY = letter.Position.Y + _lineHeight * scale.Y / 2 - size.y / 2;
            image.enabled = true;
            image.uvRect = glyph.Uv;
            image.color = letter.Shadow ? Color.black : TextColor;
            image.rectTransform.sizeDelta = size * Units;
            HudVisual.Position(image.rectTransform, new Vector2(letter.Position.X * Units, centreY * Units));
            image.rectTransform.localEulerAngles = new Vector3(0, 0, -letter.Rotation);
        }
        for (; index < _letters.Count; index++) _letters[index].enabled = false;
    }
}
