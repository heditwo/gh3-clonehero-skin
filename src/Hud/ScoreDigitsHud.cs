using UnityEngine;
using UnityEngine.UI;

namespace Gh3Skin;

internal sealed class ScoreDigitsHud
{
    private readonly Rect _display;
    private readonly RawImage[] _digits = new RawImage[8];
    private readonly Texture2D[] _textures;
    private int _score = -1;

    public ScoreDigitsHud(Transform parent, Rect display, IReadOnlyDictionary<string, Texture2D> textures)
    {
        _display = display;
        _textures = Enumerable.Range(0, 10).Select(digit => textures[$"score-digit-{digit}"]).ToArray();
        var root = HudVisual.Group("Score digits", parent);
        HudVisual.Position(root, display.position);
        for (var slot = 0; slot < _digits.Length; slot++)
            _digits[slot] = HudVisual.Image(root, $"score-{slot}", _textures[0], new Rect());
    }

    public void Update(int score)
    {
        score = Math.Clamp(score, 0, 99999999);
        if (_score == score) return;
        _score = score;
        var digits = score.ToString();
        var width = Math.Min(31, _display.width / digits.Length);
        var start = _display.width - digits.Length * width;
        for (var slot = 0; slot < _digits.Length; slot++)
        {
            var image = _digits[slot];
            image.enabled = slot < digits.Length;
            if (image.enabled) image.texture = _textures[digits[slot] - '0'];
            HudVisual.Layout(image, new Rect(start + slot * width, 0, width, _display.height));
        }
    }
}
