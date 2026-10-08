using UnityEngine;
using UnityEngine.UI;

namespace Gh3Skin;

// One line of GH3 font text. Every glyph sits on the same top line, so digits share one baseline and pitch
// regardless of how many there are. Justification follows GH3's just pairs: -1 left/top, 0 center, 1 right/bottom.
internal sealed class HudText
{
    private readonly HudFont _font;
    private readonly Vector2 _anchor;
    private readonly Vector2 _justify;
    private readonly float _scale;
    private readonly float _spacing;
    private readonly float _maxWidth;
    private readonly List<RawImage> _glyphs = new();
    private string _text = "";
    private Color _color = Color.white;

    public RectTransform Root { get; }

    public HudText(Transform parent, string name, HudFont font, Vector2 anchor, Vector2 justify, float scale,
        float spacing = 0, float maxWidth = 0)
    {
        _font = font;
        _anchor = anchor;
        _justify = justify;
        _scale = scale;
        _spacing = spacing;
        _maxWidth = maxWidth;
        Root = HudVisual.Group(name, parent);
        Offset = Vector2.zero;
    }

    public Vector2 Offset
    {
        set => HudVisual.Position(Root, new Vector2(_anchor.x + value.x, _anchor.y + value.y));
    }

    public Color Color
    {
        set
        {
            _color = value;
            foreach (var glyph in _glyphs) glyph.color = value;
        }
    }

    public string Text
    {
        set
        {
            if (_text == value) return;
            _text = value;
            Layout();
        }
    }

    private void Layout()
    {
        var width = 0f;
        foreach (var character in _text)
            if (_font.TryGet(character, out var glyph)) width += (glyph.Width + _spacing) * _scale;
        // GH3 shrinks a line that is wider than its display; six-digit scores are drawn smaller than five-digit ones.
        var scale = _maxWidth > 0 && width > _maxWidth ? _scale * _maxWidth / width : _scale;
        width *= scale / _scale;
        var height = 0f;
        foreach (var character in _text)
            if (_font.TryGet(character, out var glyph)) height = Math.Max(height, glyph.Height * scale);
        var x = -(_justify.x + 1) / 2 * width;
        // GH3 raises glyphs by the font's baseline value; GH3 frames show the counter digits 7 texels above centre.
        var top = -(_justify.y + 1) / 2 * height - _font.Baseline * scale;
        var index = 0;
        foreach (var character in _text)
        {
            if (!_font.TryGet(character, out var glyph)) continue;
            if (index == _glyphs.Count) _glyphs.Add(HudVisual.Image(Root, "glyph", _font.Atlas, new Rect()));
            var image = _glyphs[index++];
            image.enabled = glyph.Uv.width > 0;
            image.uvRect = glyph.Uv;
            image.color = _color;
            HudVisual.Layout(image, new Rect(x, top, glyph.Width * scale, glyph.Height * scale));
            x += (glyph.Width + _spacing) * scale;
        }
        for (; index < _glyphs.Count; index++) _glyphs[index].enabled = false;
    }
}
