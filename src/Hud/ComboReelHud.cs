using UnityEngine;
using UnityEngine.UI;

namespace Gh3Skin;

internal sealed class ComboReelHud
{
    private const float CarryDuration = 5f / 60;
    private readonly int _slot;
    private readonly HudTuning _tuning;
    private readonly IReadOnlyDictionary<string, Texture2D> _textures;
    private readonly RawImage? _background;
    private readonly RawImage _digit;
    private readonly RawImage _roll;
    private readonly RawImage _incoming;
    private bool _rolling;
    private bool _carry;
    private float _started;
    private float _duration;
    private int _length;

    public RectTransform Root { get; }

    public ComboReelHud(Transform parent, int slot, HudTuning tuning, IReadOnlyDictionary<string, Texture2D> textures)
    {
        _slot = slot;
        _tuning = tuning;
        _textures = textures;
        Root = HudVisual.Group($"Combo reel {slot}", parent);
        if (slot < 3)
            _background = HudVisual.Image(Root, $"comboReelBackground{slot}",
                textures[slot == 2 ? "combo-reel-2-dark" : $"combo-reel-{slot}"], new Rect());
        _digit = HudVisual.Image(Root, $"combo-{slot}", textures[$"combo-{slot}-digit-0"], new Rect());
        _roll = HudVisual.Image(Root, $"comboRoll{slot}", textures[$"combo-reel-{slot}"], new Rect());
        _roll.gameObject.AddComponent<RectMask2D>();
        _incoming = HudVisual.Image(_roll.transform, "rolling digit", textures["combo-glyph-light-0"], new Rect());
        _roll.gameObject.SetActive(false);
    }

    public void SetDigit(int digit, int length, Rect layout, float bottom)
    {
        _length = length;
        HudVisual.Position(Root, layout.position);
        Root.sizeDelta = layout.size;
        var gray = _slot < length - 1;
        var face = new Rect(0, 0, layout.width, layout.height);
        if (_background != null)
        {
            _background.enabled = gray;
            HudVisual.Layout(_background, new Rect(0, 0, layout.width, bottom - layout.y));
        }
        _digit.texture = _textures[gray ? $"combo-glyph-light-{digit}" :
            _slot == 2 && length == 3 ? $"combo-2-extended-digit-{digit}" : $"combo-{_slot}-digit-{digit}"];
        HudVisual.Layout(_digit, face);
        HudVisual.Layout(_roll, face);
        _roll.texture = _textures[_slot == 2 ? length == 3 ? "combo-reel-2-extended" : "combo-reel-2-dark" : $"combo-reel-{_slot}"];
        _roll.color = gray ? Color.clear : Color.white;
        _incoming.texture = _textures[$"combo-glyph-{(gray ? "light" : "dark")}-{digit}"];
    }

    public void Change(bool roll, bool carry, float time)
    {
        _rolling = roll;
        if (!roll) return;
        _carry = carry;
        _started = time;
        _duration = carry ? CarryDuration : _tuning.RollAlphas.Length / 60f;
    }

    public void Update(float time)
    {
        if (time - _started >= _duration) _rolling = false;
        _digit.enabled = !_rolling;
        _roll.gameObject.SetActive(_rolling);
        if (!_rolling) return;
        var elapsed = Math.Max(0, time - _started);
        var progress = Math.Clamp(elapsed / _duration, 0, 1);
        var frame = Math.Clamp((int)(elapsed * 60), 0, _tuning.RollAlphas.Length - 1);
        var size = Root.rect.size;
        if (_slot == 2 && _length == 3) size.x *= 35f / _tuning.LastReelWidth;
        _incoming.rectTransform.sizeDelta = size;
        _incoming.rectTransform.anchoredPosition = new Vector2(0, _carry ? 0 : -_tuning.RollOffsets[frame] * 1.5f);
        var alpha = _carry ? Math.Clamp((progress - 0.5f) / 0.5f, 0, 1) : _tuning.RollAlphas[frame];
        _incoming.color = new Color(1, 1, 1, alpha);
    }
}
