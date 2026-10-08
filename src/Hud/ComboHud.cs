using UnityEngine;
using UnityEngine.UI;

namespace Gh3Skin;

// GH3's note container in GH3 units: the drum (z 8) under the body (z 9), the num_a7 digits (z 25), and the note
// icon (z 26) that covers the fourth drum below a 1000-note streak.
internal sealed class ComboHud
{
    private static readonly float[] EntryOffsets =
    {
        62, 50, 32, 20, 10, -6, -20, -30, -37, -41, -40, -20,
        -13, -4, 2, 6, 7, 7, 6, 6, 6, 4, 2, 0
    };
    private readonly RectTransform _motion;
    private readonly RawImage _icon;
    private readonly ComboDigitHud[] _digits = new ComboDigitHud[4];
    private int _shownCombo;
    private float _entryStarted = float.NegativeInfinity;
    private bool _first = true;

    public RectTransform Root { get; }

    public ComboHud(Transform parent, HudTuning tuning, IReadOnlyDictionary<string, Texture2D> textures, HudFont font)
    {
        Root = HudVisual.Group("Combo meter", parent);
        Root.localScale = new Vector3(HudVisual.Gh3Scale, HudVisual.Gh3Scale, 1);
        _motion = HudVisual.Group("Combo entrance", Root);
        HudVisual.Image(_motion, "comboDrum", textures["HUD_counter_drum"], new Rect(4, 40, 256, 64));
        HudVisual.Image(_motion, "comboBody", textures["HUD_counter_body"], new Rect(0, 0, 256, 128));
        for (var place = 0; place < _digits.Length; place++)
            _digits[place] = new ComboDigitHud(_motion, place, font, tuning);
        _icon = HudVisual.Image(_motion, "comboIcon", textures["HUD_counter_drum_icon"], new Rect(44, 40, 64, 64));
    }

    public void Update(int combo, float time)
    {
        var visible = combo >= 25;
        if (visible && _shownCombo < 25) _entryStarted = time;
        _motion.gameObject.SetActive(visible);
        if (_first || combo != _shownCombo)
        {
            _first = false;
            var digits = Math.Clamp(combo, 0, 9999).ToString("D3");
            var previous = Math.Clamp(_shownCombo, 0, 9999).ToString($"D{digits.Length}");
            _icon.enabled = digits.Length == 3;
            // GH3 rolls only the highest digit that changed; on a carry the lower digits switch at once.
            var rolling = visible && _shownCombo >= 25 && combo > _shownCombo;
            var highest = Enumerable.Range(0, digits.Length).FirstOrDefault(index => previous[index] != digits[index], -1);
            for (var place = 0; place < _digits.Length; place++)
            {
                var index = digits.Length - 1 - place;
                _digits[place].SetDigit(index >= 0 ? digits[index] : '0', index >= 0);
                _digits[place].Change(rolling && index >= 0 && index == highest, time);
            }
            _shownCombo = combo;
        }
        HudVisual.Position(_motion, new Vector2(0, EntryOffset(time)));
        foreach (var digit in _digits) digit.Update(time);
    }

    private float EntryOffset(float time)
    {
        if (_entryStarted < 0) return 0;
        var frame = Math.Max(0, (time - _entryStarted) * 60);
        var index = (int)frame;
        if (index >= EntryOffsets.Length - 1) return 0;
        var fraction = frame - index;
        // The entrance offsets are 720p screen pixels; one GH3 unit is 0.7 of those.
        return (EntryOffsets[index] * (1 - fraction) + EntryOffsets[index + 1] * fraction) / 0.7f;
    }
}
