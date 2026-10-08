using UnityEngine;

namespace Gh3Skin;

// One note-streak digit: a num_a7 text element on the drum, as created by create_2d_hud_elements.
internal sealed class ComboDigitHud
{
    private readonly HudTuning _tuning;
    private readonly HudText _text;
    private readonly Color _color;
    private readonly float _anchorX;
    private bool _rolling;
    private bool _settled = true;
    private float _started;
    private float _x;
    private float _alpha = 1;
    private (float Scale, float Angle) _pop = (1, 0);

    public ComboDigitHud(Transform parent, int place, HudFont font, HudTuning tuning)
    {
        _tuning = tuning;
        // The ones digit is dark on the white drum; the others are light on the dark drums.
        _color = place == 0 ? new Color(15 / 255f, 15 / 255f, 70 / 255f, 200 / 255f) : new Color(230 / 255f, 230 / 255f, 230 / 255f, 200 / 255f);
        _anchorX = _x = 222 - ComboReelLayout.Pitch * (place + 1);
        // GH3 frames show these digits at one texel per 1080p pixel, not at the HUD's 1.05 scale.
        _text = new HudText(parent, $"Combo digit {place}", font, new Vector2(_anchorX, 78), Vector2.zero, 1 / HudVisual.Gh3Scale)
        {
            Color = _color
        };
    }

    public void SetDigit(char digit, bool visible)
    {
        _text.Root.gameObject.SetActive(visible);
        _text.Text = digit.ToString();
    }

    public void Change(bool roll, float time)
    {
        _rolling = roll;
        if (!roll) return;
        _settled = false;
        _started = time;
    }

    // Placement and effects from the counter's open/close animation; they apply on the next Update.
    public void Animate(float x, float alpha, (float Scale, float Angle) pop)
    {
        if (x == _x && alpha == _alpha && pop == _pop) return;
        _x = x;
        _alpha = alpha;
        _pop = pop;
        _settled = false;
        _text.Root.localScale = new Vector3(pop.Scale, pop.Scale, 1);
        _text.Root.localEulerAngles = new Vector3(0, 0, -pop.Angle);
    }

    public void Update(float time)
    {
        if (_rolling && time - _started >= _tuning.RollAlphas.Length / 60f) _rolling = false;
        if (!_rolling && _settled) return;
        _settled = !_rolling;
        var alpha = 1f;
        var offset = 0f;
        if (_rolling)
        {
            var elapsed = Math.Max(0, time - _started);
            var frame = Math.Clamp((int)(elapsed * 60), 0, _tuning.RollAlphas.Length - 1);
            alpha = _tuning.RollAlphas[frame];
            // Offsets are 720p screen pixels; one GH3 unit is 0.7 of those.
            offset = _tuning.RollOffsets[frame] / 0.7f;
        }
        _text.Offset = new Vector2(_x - _anchorX, offset);
        _text.Color = new Color(_color.r, _color.g, _color.b, _color.a * alpha * _alpha);
    }
}
