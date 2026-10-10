using UnityEngine;
using UnityEngine.UI;

namespace Gh3Skin;

// GH3's note container in GH3 units: the drum (z 8) under the body (z 9), the num_a7 digits (z 25), and the note
// icon (z 26) that covers the fourth drum below a 1000-note streak. GH3 stops at four digits; for longer streaks
// the counter opens at ComboReelLayout.Split and inserts copies of a middle drum reel and a tileable body strip.
internal sealed class ComboHud
{
    // GH3: the counter shows from a 25-note streak.
    private const int FirstShownCombo = 25;
    // GH3: the drum strip and the note icon in the container (HUD_counter_drum at y 40, the icon 64 x 64 at (44, 40)).
    private const float DrumY = 40;
    private const float IconX = 44, IconSize = 64;
    // Measured: 720p screen pixels per GH3 unit in the recordings (the GH3 HUD is at 0.7 scale).
    private const float RecordingPixelsPerUnit = 0.7f;
    // Measured: the counter's entrance offsets (720p screen pixels), one per 60 fps frame.
    private static readonly float[] EntryOffsets =
    {
        62, 50, 32, 20, 10, -6, -20, -30, -37, -41, -40, -20,
        -13, -4, 2, 6, 7, 7, 6, 6, 6, 4, 2, 0
    };
    private const float DrumSplit = ComboReelLayout.Split - ComboReelLayout.DrumX;
    private readonly float _leftLimit;
    private readonly RectTransform _motion;
    private readonly RectTransform _fit;
    private readonly RawImage _drumLeft;
    private readonly RawImage _bodyLeft;
    private readonly RawImage _icon;
    private readonly RawImage[] _drumReels = new RawImage[ComboReelLayout.MaxDigits - 3];
    private readonly RawImage[] _bodyReels = new RawImage[ComboReelLayout.MaxDigits - 3];
    private readonly RawImage _flash;
    private readonly ComboDigitHud[] _digits = new ComboDigitHud[ComboReelLayout.MaxDigits];
    private readonly ComboReelAnimation _reel = new();
    private int _shownCombo;
    private int _shownDigits = 3;
    private int _popPlace = -1;
    private float _entryStarted = float.NegativeInfinity;
    private bool _first = true;
    private bool _dirty = true;

    public RectTransform Root { get; }

    // leftLimit is the leftmost container x the body may reach without covering the score lamps.
    public ComboHud(Transform parent, HudTuning tuning, IReadOnlyDictionary<string, Texture2D> textures, HudFont font,
        float leftLimit)
    {
        _leftLimit = leftLimit;
        Root = HudVisual.Group("Combo meter", parent);
        Root.localScale = new Vector3(HudVisual.Gh3Scale, HudVisual.Gh3Scale, 1);
        _motion = HudVisual.Group("Combo entrance", Root);
        _fit = HudVisual.Group("Combo fit", _motion);
        var drum = textures["HUD_counter_drum"];
        var body = textures["HUD_counter_body"];
        var tile = textures["HUD_counter_body_tile"];
        _drumLeft = Slice(_fit, "comboDrumLeft", drum, 0, DrumSplit, ComboReelLayout.DrumX, DrumY);
        Slice(_fit, "comboDrumRight", drum, DrumSplit, drum.width, ComboReelLayout.Split, DrumY);
        for (var k = 0; k < _drumReels.Length; k++)
            _drumReels[k] = Slice(_fit, $"comboDrumReel{k}", drum, DrumSplit, DrumSplit + ComboReelLayout.Pitch, 0, DrumY);
        _bodyLeft = Slice(_fit, "comboBodyLeft", body, 0, ComboReelLayout.Split, 0, 0);
        Slice(_fit, "comboBodyRight", body, ComboReelLayout.Split, body.width, ComboReelLayout.Split, 0);
        for (var k = 0; k < _bodyReels.Length; k++)
            _bodyReels[k] = Slice(_fit, $"comboBodyReel{k}", tile, 0, tile.width, 0, 0);
        for (var place = 0; place < _digits.Length; place++)
            _digits[place] = new ComboDigitHud(_fit, place, font, tuning);
        _icon = HudVisual.Image(_fit, "comboIcon", textures["HUD_counter_drum_icon"], new Rect(IconX, DrumY, IconSize, IconSize));
        _flash = HudVisual.Image(_motion, "comboFlash", textures["hud-glowburst"], new Rect(), new Vector2(0.5f, 0.5f));
        // GH3: hud_glowburst_alert's color (245, 255, 200).
        _flash.color = new Color(245 / 255f, 1, 200 / 255f, 0);
        _flash.enabled = false;
    }

    // A horizontal slice [left, right) of a texture at container position (x, y).
    private static RawImage Slice(Transform parent, string name, Texture2D texture, float left, float right, float x, float y)
    {
        var image = HudVisual.Image(parent, name, texture, new Rect(x, y, right - left, texture.height));
        image.uvRect = new Rect(left / texture.width, 0, (right - left) / texture.width, 1);
        return image;
    }

    public void Update(int combo, float time)
    {
        combo = Math.Clamp(combo, 0, ComboReelLayout.MaxCombo);
        var visible = combo >= FirstShownCombo;
        if (visible && _shownCombo < FirstShownCombo) _entryStarted = time;
        if (_first || combo != _shownCombo)
        {
            var broke = !visible && _shownCombo >= FirstShownCombo;
            if (broke && _shownDigits > 4)
            {
                _reel.Close(time);
                _popPlace = -1;
            }
            else if (!_reel.Closing || visible) ShowDigits(combo, visible, time);
            _first = false;
            _shownCombo = combo;
        }
        if (_reel.Closed(time))
        {
            _reel.Reset(0);
            _dirty = true;
            ShowDigits(combo, visible, time);
        }
        _motion.gameObject.SetActive(visible || _reel.Closing);
        HudVisual.Position(_motion, new Vector2(0, EntryOffset(time)));
        if (_dirty || _reel.Active(time)) Animate(time);
        _dirty = false;
        foreach (var digit in _digits) digit.Update(time);
    }

    private void ShowDigits(int combo, bool visible, float time)
    {
        var digits = combo.ToString("D3");
        var previous = Math.Clamp(_shownCombo, 0, ComboReelLayout.MaxCombo).ToString($"D{digits.Length}");
        if (previous.Length > digits.Length) previous = previous[^digits.Length..];
        _icon.enabled = digits.Length == 3;
        // GH3 rolls only the highest digit that changed; on a carry the lower digits switch at once.
        var counting = visible && _shownCombo >= FirstShownCombo && combo > _shownCombo && !_reel.Closing;
        var highest = Enumerable.Range(0, digits.Length).FirstOrDefault(index => previous[index] != digits[index], -1);
        for (var place = 0; place < _digits.Length; place++)
        {
            var index = digits.Length - 1 - place;
            _digits[place].SetDigit(index >= 0 ? digits[index] : '0', index >= 0);
            _digits[place].Change(counting && index >= 0 && index == highest, time);
        }
        var width = ComboReelLayout.Width(digits.Length);
        if (counting && digits.Length > _shownDigits && digits.Length > 4)
        {
            _reel.Open(width, time);
            _popPlace = digits.Length - 2;
        }
        else if (!counting || width != _reel.Target)
        {
            _reel.Reset(width);
            _popPlace = -1;
        }
        _shownDigits = digits.Length;
        _dirty = true;
    }

    private void Animate(float time)
    {
        var width = _reel.Width(time);
        // The counter shrinks about its right edge only when it would otherwise cover the score lamps.
        var fit = ComboReelLayout.FitScale(width, _leftLimit);
        var squash = _reel.Squash(time);
        _fit.localScale = new Vector3(fit, fit * squash, 1);
        HudVisual.Position(_fit, new Vector2(ComboReelLayout.BodyRight * (1 - fit), ComboReelLayout.CenterY * (1 - fit * squash)));
        HudVisual.Position(_drumLeft.rectTransform, new Vector2(ComboReelLayout.DrumX - width, DrumY));
        HudVisual.Position(_bodyLeft.rectTransform, new Vector2(-width, 0));
        for (var k = 0; k < _drumReels.Length; k++)
        {
            var (x, visible) = ComboReelLayout.Reel(k, width);
            var drum = _drumReels[k];
            var body = _bodyReels[k];
            drum.enabled = body.enabled = visible > 0;
            if (visible <= 0) continue;
            // A partly open reel shows its right side, as if it slides out from under the first reel.
            drum.uvRect = new Rect((DrumSplit + ComboReelLayout.Pitch - visible) / drum.texture.width, 0, visible / drum.texture.width, 1);
            HudVisual.Layout(drum, new Rect(x, DrumY, visible, drum.texture.height));
            body.uvRect = new Rect(1 - visible / ComboReelLayout.Pitch, 0, visible / ComboReelLayout.Pitch, 1);
            HudVisual.Layout(body, new Rect(x, 0, visible, body.texture.height));
        }
        var alpha = _reel.DigitAlpha(time);
        for (var place = 0; place < _digits.Length; place++)
            _digits[place].Animate(ComboReelLayout.DigitX(place, _shownDigits, width), alpha,
                place == _popPlace ? _reel.Pop(time) : (1, 0));
        var flash = _reel.Flash(time);
        _flash.enabled = flash.Alpha > 0;
        if (_flash.enabled)
        {
            // GH3 draws the glowburst across the whole highway; here it is scaled down to the counter (by eye).
            _flash.color = new Color(_flash.color.r, _flash.color.g, _flash.color.b, flash.Alpha);
            _flash.rectTransform.sizeDelta = new Vector2(flash.Width * 4, flash.Height * 10);
            HudVisual.Position(_flash.rectTransform, new Vector2(ComboReelLayout.Split - width / 2, ComboReelLayout.CenterY));
        }
    }

    private float EntryOffset(float time)
    {
        if (_entryStarted < 0) return 0;
        var frame = Math.Max(0, (time - _entryStarted) * 60);
        var index = (int)frame;
        if (index >= EntryOffsets.Length - 1) return 0;
        var fraction = frame - index;
        return (EntryOffsets[index] * (1 - fraction) + EntryOffsets[index + 1] * fraction) / RecordingPixelsPerUnit;
    }
}
