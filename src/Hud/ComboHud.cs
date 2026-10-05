using UnityEngine;
using UnityEngine.UI;

namespace Gh3Skin;

internal sealed class ComboHud
{
    private const float ReelBottom = 97;
    private static readonly float[] EntryOffsets =
    {
        62, 50, 32, 20, 10, -6, -20, -30, -37, -41, -40, -20,
        -13, -4, 2, 6, 7, 7, 6, 6, 6, 4, 2, 0
    };
    private readonly HudTuning _tuning;
    private readonly RectTransform _motion;
    private readonly RawImage _iconBack;
    private readonly RawImage _icon;
    private readonly RawImage _backing;
    private readonly ComboReelHud[] _reels = new ComboReelHud[4];
    private int _shownCombo;
    private float _entryStarted = float.NegativeInfinity;
    private bool _first = true;

    public RectTransform Root { get; }

    public ComboHud(Transform parent, HudTuning tuning, IReadOnlyDictionary<string, Texture2D> textures)
    {
        _tuning = tuning;
        Root = HudVisual.Group("Combo meter", parent);
        Root.localScale = new Vector3(1.06f, 1, 1);
        _motion = HudVisual.Group("Combo entrance", Root);
        var frame = new Rect(0, 0, 256, 128);
        HudVisual.Image(_motion, "comboBody", textures["HUD_counter_body"], frame);
        _iconBack = HudVisual.Image(_motion, "comboIconBack", textures["HUD_counter_drum_icon_back"], new Rect(36, 40, 76, 59));
        _icon = HudVisual.Image(_motion, "comboIcon", textures["HUD_counter_drum_icon"], new Rect(44.5f, 42.5f, 68, 66));
        _backing = HudVisual.Image(_motion, "comboBacking0", textures["combo-reel-0"], new Rect());
        for (var slot = 0; slot < _reels.Length; slot++)
            _reels[slot] = new ComboReelHud(_motion, slot, tuning, textures);
        HudVisual.Image(_motion, "comboFrame", textures["HUD_counter_frame"], frame);
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
            _iconBack.enabled = _icon.enabled = digits.Length == 3;
            var start = digits.Length == 4 ? 53 : 91;
            for (var slot = 0; slot < _reels.Length; slot++)
            {
                var reel = _reels[slot];
                reel.Root.gameObject.SetActive(slot < digits.Length);
                if (slot >= digits.Length) continue;
                var layout = SlotLayout(start, slot, digits.Length);
                reel.SetDigit(digits[slot] - '0', digits.Length, layout, ReelBottom);
                if (slot == 0) HudVisual.Layout(_backing, new Rect(layout.x, layout.y, 38, ReelBottom - layout.y));
                var lastCarried = slot == digits.Length - 1 && slot > 0 && previous[slot - 1] != digits[slot - 1];
                var roll = visible && _shownCombo >= 25 && combo > _shownCombo && previous[slot] != digits[slot] && !lastCarried;
                var carry = slot < digits.Length - 1 && previous[slot + 1] != digits[slot + 1];
                reel.Change(roll, carry, time);
            }
            _shownCombo = combo;
        }
        HudVisual.Position(_motion, new Vector2(0, EntryOffset(time)));
        foreach (var reel in _reels) reel.Update(time);
    }

    private Rect SlotLayout(int start, int slot, int length)
    {
        var width = slot switch
        {
            0 => _tuning.FirstReelWidth,
            1 => 36,
            2 when length == 3 => _tuning.LastReelWidth,
            2 => 34,
            _ => 38
        };
        return new Rect(start + slot * 38 + (slot == 0 ? 5 : slot == 1 ? 2 : 1),
            slot == 2 ? 49 : 47, width, slot == 2 ? 48 : 45);
    }

    private float EntryOffset(float time)
    {
        if (_entryStarted < 0) return 0;
        var frame = Math.Max(0, (time - _entryStarted) * 60);
        var index = (int)frame;
        if (index >= EntryOffsets.Length - 1) return 0;
        var fraction = frame - index;
        return (EntryOffsets[index] * (1 - fraction) + EntryOffsets[index + 1] * fraction) * 1.5f;
    }
}
