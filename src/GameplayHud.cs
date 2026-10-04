using UnityEngine;
using UnityEngine.UI;

namespace Gh3Skin;

internal sealed class GameplayHud
{
    private static readonly float[] ComboEntryOffsets =
    {
        62, 50, 32, 20, 10, -6, -20, -30, -37, -41, -40, -20,
        -13, -4, 2, 6, 7, 7, 6, 6, 6, 4, 2, 0
    };
    private const float ComboCarryDuration = 5f / 60f;
    private readonly HudTuning _tuning;
    private readonly IReadOnlyDictionary<string, Texture2D> _hudTextures;
    private readonly IReadOnlyList<AnimationFrame> _readyFrames;
    private readonly IReadOnlyList<AnimationFrame> _readyLightningFrames;
    private readonly Dictionary<string, RawImage> _hudImages = new();
    private readonly Dictionary<string, Rect> _hudLayouts = new();
    private readonly RawImage[] _comboRollNew = new RawImage[4];
    private readonly bool[] _comboRolling = new bool[4];
    private readonly bool[] _comboCarry = new bool[4];
    private readonly float[] _comboRollStarted = new float[4];
    private readonly float[] _comboRollDurations = new float[4];
    private readonly ReadyAnimationTrigger _readyAnimation = new();
    private Canvas? _canvas;
    private int _score;
    private int _combo;
    private float _rock = 0.5f;
    private float _starPower;
    private int _shownCombo;
    private float _comboEntryStarted = float.NegativeInfinity;

    public GameplayHud(SkinAssets assets, HudTuning tuning)
    {
        _tuning = tuning;
        _hudTextures = assets.Textures;
        _readyFrames = assets.ReadyFrames;
        _readyLightningFrames = assets.ReadyLightningFrames;
    }

    public void Reset()
    {
        _score = 0;
        _combo = 0;
        _rock = 0.5f;
        _starPower = 0;
        _readyAnimation.Reset();
        _shownCombo = 0;
        _comboEntryStarted = float.NegativeInfinity;
        Array.Fill(_comboRolling, false);
        Array.Fill(_comboCarry, false);
        _hudImages.Clear();
        _hudLayouts.Clear();
        _canvas = null;
    }

    public void EnsureCreated()
    {
        if (_canvas == null)
            CreateHud();
    }

    public void Update(GameplayState snapshot)
    {
        _score = snapshot.Score;
        _combo = snapshot.Combo;
        _rock = snapshot.Rock;
        _starPower = snapshot.StarPower;
        UpdateStarPower();
        UpdateRockMeter();
        UpdateScore();
        var streakLength = UpdateCombo();
        UpdateReadyAnimation();
        PositionHud();
        UpdateComboRolls(streakLength);
    }

    private void UpdateStarPower()
    {
        _hudImages["highwayEdges"].texture = _hudTextures[_starPower >= 0.5f ? "highway-edges-blue" : "highway-edges-normal"];
        _readyAnimation.Update(_starPower, Time.time);
        for (var bulb = 0; bulb < 6; bulb++)
        {
            var visible = bulb < 3 || _starPower >= 0.5f;
            var fill = Math.Clamp(_starPower * 6 - bulb, 0, 1);
            _hudImages[$"bulb{bulb}"].enabled = visible && fill < 1;
            var glow = _hudImages[$"bulbGlow{bulb}"];
            glow.enabled = visible && fill > 0;
            glow.color = new Color(1, 1, 1, fill);
            glow.texture = _hudTextures[_starPower >= 0.5f ? "HUD_rock_tube_glow_full_b" : "HUD_rock_tube_glow_full"];
        }
    }

    private void UpdateRockMeter()
    {
        var gaugeColor = _rock < 0.35f ? "red" : _rock < 0.58f ? "yellow" : "green";
        foreach (var color in new[] { "green", "yellow", "red" })
        {
            _hudImages["rockBackground" + color].enabled = color == gaugeColor;
            _hudImages["rockLight" + color].enabled = color == gaugeColor;
        }
        _hudImages["rockNeedle"].rectTransform.localEulerAngles = new Vector3(0, 0, 55 - 90 * _rock);
    }

    private void UpdateScore()
    {
        var multiplier = Math.Clamp(_combo / 10 + 1, 1, 4);
        for (var value = 1; value <= 4; value++)
            _hudImages["multiplier" + value].enabled = value == multiplier;
        var scoreLightColor = multiplier == 4 ? "_purple" : multiplier == 3 ? "_green" : "";
        var scoreLightProgress = multiplier == 4 || _combo > 0 && _combo % 10 == 0 ? 10 : _combo % 10;
        for (var light = 0; light < 5; light++)
        {
            var fill = scoreLightProgress - (4 - light) * 2;
            var state = fill >= 2 ? 2 : fill == 1 ? 1 : 0;
            var texture = _hudTextures[$"HUD_score_light_{state}{scoreLightColor}"];
            _hudImages[$"scoreLight{light}"].texture = texture;
            var glow = _hudImages[$"scoreLightGlow{light}"];
            glow.texture = texture;
            glow.enabled = state > 0;
            glow.color = Color.white;
        }
        var digits = _score.ToString();
        var start = 473 - digits.Length * 15.5f;
        for (var slot = 0; slot < 8; slot++)
        {
            for (var digit = 0; digit < 10; digit++)
            {
                var key = $"score-{slot}-{digit}";
                _hudImages[key].enabled = slot < digits.Length && digits[slot] - '0' == digit;
                _hudLayouts[key] = new Rect(start + slot * 31, 815, 31, 37);
            }
        }
    }

    private int UpdateCombo()
    {
        var showCombo = _combo >= 25;
        if (showCombo && _shownCombo < 25)
            _comboEntryStarted = Time.time;
        _hudImages["comboBody"].enabled = showCombo;
        _hudImages["comboFrame"].enabled = showCombo;
        _hudImages["comboIconBack"].enabled = showCombo && _combo < 1000;
        _hudImages["comboIcon"].enabled = showCombo && _combo < 1000;
        _hudImages["comboBacking0"].enabled = showCombo;
        var streak = Math.Clamp(_combo, 0, 9999).ToString("D3");
        var comboStart = streak.Length == 4 ? 388 : 426;
        var firstReel = ComboSlotLayout(comboStart, 0, streak.Length);
        _hudLayouts["comboBacking0"] = new Rect(firstReel.x, firstReel.y, 38, firstReel.height);
        if (_combo != _shownCombo)
        {
            var previous = Math.Clamp(_shownCombo, 0, 9999).ToString($"D{streak.Length}");
            for (var slot = 0; slot < 4; slot++)
            {
                var carriedLastDigit = slot == streak.Length - 1 && slot > 0 && previous[slot - 1] != streak[slot - 1];
                _comboRolling[slot] = showCombo && _shownCombo >= 25 && _combo > _shownCombo && slot < streak.Length && previous[slot] != streak[slot] && !carriedLastDigit;
                if (!_comboRolling[slot])
                    continue;
                _comboRollStarted[slot] = Time.time;
                _comboCarry[slot] = slot < streak.Length - 1 && previous[slot + 1] != streak[slot + 1];
                _comboRollDurations[slot] = _comboCarry[slot] ? ComboCarryDuration : _tuning.RollAlphas.Length / 60f;
                var shade = slot == streak.Length - 1 ? "dark" : "light";
                _hudImages[$"comboRoll{slot}"].texture = _hudTextures[ComboReelTexture(slot, streak.Length)];
                _comboRollNew[slot].texture = _hudTextures[$"combo-glyph-{shade}-{streak[slot]}"];
            }
            _shownCombo = _combo;
        }
        for (var slot = 0; slot < 4; slot++)
        {
            if (Time.time - _comboRollStarted[slot] >= _comboRollDurations[slot])
                _comboRolling[slot] = false;
            _hudImages[$"comboRoll{slot}"].gameObject.SetActive(showCombo && _comboRolling[slot]);
            _hudLayouts[$"comboRoll{slot}"] = ComboSlotLayout(comboStart, slot, streak.Length);
            for (var digit = 0; digit < 10; digit++)
            {
                var key = $"combo-{slot}-{digit}";
                _hudImages[key].enabled = showCombo && !_comboRolling[slot] && slot < streak.Length && streak[slot] - '0' == digit;
                if (slot == 2)
                    _hudImages[key].texture = _hudTextures[ComboDigitTexture(slot, digit, streak.Length)];
                _hudLayouts[key] = ComboSlotLayout(comboStart, slot, streak.Length);
            }
        }
        return streak.Length;
    }

    private void UpdateComboRolls(int streakLength)
    {
        for (var slot = 0; slot < 4; slot++)
        {
            if (!_comboRolling[slot])
                continue;
            var viewport = _hudImages[$"comboRoll{slot}"].rectTransform;
            var elapsed = Math.Max(0, Time.time - _comboRollStarted[slot]);
            var progress = Math.Clamp(elapsed / _comboRollDurations[slot], 0, 1);
            var frame = Math.Clamp((int)(elapsed * 60), 0, _tuning.RollAlphas.Length - 1);
            _comboRollNew[slot].rectTransform.sizeDelta = slot == 2 && streakLength == 3
                ? new Vector2(viewport.rect.width * 35f / _tuning.LastReelWidth, viewport.rect.height)
                : viewport.rect.size;
            var offset = _comboCarry[slot] ? 0 : _tuning.RollOffsets[frame] * Screen.height / 720f;
            var opacity = _comboCarry[slot]
                ? Math.Clamp((progress - 0.5f) / 0.5f, 0, 1)
                : _tuning.RollAlphas[frame];
            _comboRollNew[slot].rectTransform.anchoredPosition = new Vector2(0, -offset);
            _comboRollNew[slot].color = new Color(1, 1, 1, opacity);
        }
    }

    private void CreateHud()
    {
        var gameObject = new GameObject("GH3 Gameplay HUD");
        _canvas = gameObject.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.overrideSorting = true;
        _canvas.sortingOrder = 500;
        AddHud("highwayEdges", "highway-edges-normal", new Rect(0, 12, 1920, 1080));
        AddHud("readyFrame", "ready-animation-first", _readyFrames[0].Layout);
        AddHud("readyLightning", "ready-lightning-first", _readyLightningFrames[0].Layout);
        CreateRockMeter();
        CreateScoreDisplay();
        CreateComboDisplay();
    }

    private void CreateRockMeter()
    {
        foreach (var color in new[] { "green", "yellow", "red" })
            AddHud("rockBackground" + color, "HUD_rock_BG_" + color, new Rect(1390, 780, 256, 256));
        var bulbs = new[]
        {
            (1364, 870, 48, 84, 40), (1390, 848, 46, 84, 27), (1422, 842, 50, 100, 14),
            (1459, 839, 62, 130, -3), (1503, 847, 62, 128, -20), (1539, 870, 62, 130, -40)
        };
        for (var bulb = 0; bulb < bulbs.Length; bulb++)
        {
            var (x, y, width, height, angle) = bulbs[bulb];
            var layout = new Rect(x, y, width, height);
            AddHud($"bulb{bulb}", "HUD_rock_tube", layout);
            AddHud($"bulbGlow{bulb}", "HUD_rock_tube_glow_full", layout);
            _hudImages[$"bulb{bulb}"].rectTransform.localEulerAngles = new Vector3(0, 0, angle);
            _hudImages[$"bulbGlow{bulb}"].rectTransform.localEulerAngles = new Vector3(0, 0, angle);
        }
        AddHud("rockBody", "HUD_rock_body", new Rect(1390, 780, 256, 256));
        AddHud("rockLights", "HUD_rock_lights_all", new Rect(1390, 780, 256, 128));
        AddHud("rockLightgreen", "HUD_rock_lights_green", new Rect(1518, 780, 128, 128));
        AddHud("rockLightyellow", "HUD_rock_lights_yellow", new Rect(1452, 780, 128, 128));
        AddHud("rockLightred", "HUD_rock_lights_red", new Rect(1390, 780, 128, 128));
        AddHud("rockNeedle", "HUD_rock_needle", new Rect(1520, 833, 16, 90));
    }

    private void CreateScoreDisplay()
    {
        for (var light = 0; light < 5; light++)
            AddHud($"scoreLight{light}", "HUD_score_light_0", new Rect(298, 858 + light * 31, 32, 32));
        AddHud("scoreBody", "HUD_score_body", new Rect(310, 780, 264, 256));
        for (var light = 0; light < 5; light++)
            AddHud($"scoreLightGlow{light}", "HUD_score_light_0", new Rect(298, 858 + light * 31, 32, 32));
        AddHud("scoreFrame", "HUD_score_frame", new Rect(310, 780, 264, 256));
        for (var slot = 0; slot < 8; slot++)
            for (var digit = 0; digit < 10; digit++)
                AddHud($"score-{slot}-{digit}", $"score-digit-{digit}", new Rect(0, 817, 29, 34));
        for (var value = 1; value <= 4; value++)
            AddHud("multiplier" + value, $"HUD_score_nixie_{value}a_wide", new Rect(385, 873, 118, 118));
        AddHud("scoreRingFront", "HUD_score_ring_front", new Rect(310, 780, 264, 256));
        AddHud("scoreX", "HUD_score_x", new Rect(378, 912, 48, 43));
    }

    private void CreateComboDisplay()
    {
        AddHud("comboBody", "HUD_counter_body", new Rect(335, 925, 256, 128));
        AddHud("comboIconBack", "HUD_counter_drum_icon_back", new Rect(371, 965, 76, 59));
        AddHud("comboIcon", "HUD_counter_drum_icon", new Rect(379.5f, 967.5f, 68, 66));
        AddHud("comboBacking0", "combo-reel-0", new Rect(0, 967, 38, 50));
        for (var slot = 0; slot < 4; slot++)
            for (var digit = 0; digit < 10; digit++)
                AddHud($"combo-{slot}-{digit}", $"combo-{slot}-digit-{digit}", new Rect(0, 967, 38, 50));
        for (var slot = 0; slot < 4; slot++)
        {
            AddHud($"comboRoll{slot}", $"combo-reel-{slot}", new Rect(0, 967, 38, 50));
            var viewport = _hudImages[$"comboRoll{slot}"].gameObject;
            viewport.AddComponent<RectMask2D>();
            _comboRollNew[slot] = AddRollImage(viewport.transform);
            viewport.SetActive(false);
        }
        AddHud("comboFrame", "HUD_counter_frame", new Rect(335, 925, 256, 128));
    }

    private static RawImage AddRollImage(Transform parent)
    {
        var gameObject = new GameObject("GH3 rolling digit");
        var rect = gameObject.AddComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = new Vector2(0, 1);
        rect.anchorMax = new Vector2(0, 1);
        rect.pivot = new Vector2(0, 1);
        var image = gameObject.AddComponent<RawImage>();
        image.raycastTarget = false;
        return image;
    }

    private static string ComboDigitTexture(int slot, int digit, int length) =>
        slot == 2 && length == 4 ? $"combo-2-dark-digit-{digit}" :
        slot == 2 && length == 3 ? $"combo-2-extended-digit-{digit}" : $"combo-{slot}-digit-{digit}";

    private static string ComboReelTexture(int slot, int length) =>
        slot == 2 && length == 4 ? "combo-reel-2-dark" :
        slot == 2 && length == 3 ? "combo-reel-2-extended" : $"combo-reel-{slot}";

    private Rect ComboSlotLayout(int start, int slot, int length)
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
            slot == 2 ? 974 : 972, width, slot == 2 ? 48 : 45);
    }

    private void AddHud(string key, string spriteName, Rect layout)
    {
        var gameObject = new GameObject("GH3 " + key);
        var rect = gameObject.AddComponent<RectTransform>();
        rect.SetParent(_canvas!.transform, false);
        var image = gameObject.AddComponent<RawImage>();
        image.texture = _hudTextures[spriteName];
        image.raycastTarget = false;
        _hudImages[key] = image;
        _hudLayouts[key] = layout;
    }

    private void PositionHud()
    {
        var scale = Math.Min(Screen.width / 1920f, Screen.height / 1080f);
        var offsetX = (Screen.width - 1920 * scale) / 2;
        var offsetY = (Screen.height - 1080 * scale) / 2;
        var comboEntryOffset = ComboEntryOffset();
        foreach (var (key, image) in _hudImages)
        {
            var layout = _hudLayouts[key];
            var rect = image.rectTransform;
            var bulb = key.StartsWith("bulb", StringComparison.Ordinal);
            var rock = key.StartsWith("rock", StringComparison.Ordinal);
            var combo = key.StartsWith("combo", StringComparison.Ordinal);
            var leftHud = combo || key.StartsWith("score", StringComparison.Ordinal) || key.StartsWith("multiplier", StringComparison.Ordinal);
            var x = bulb ? layout.x : rock ? 1330 + (layout.x - 1390) * _tuning.RockScale + _tuning.RockOffsetX : layout.x;
            var y = bulb ? layout.y - 72 : rock ? 753 + (layout.y - 780) * _tuning.RockVerticalScale + _tuning.RockOffsetY : layout.y - 12;
            if (combo)
            {
                x = 383 + (x - 383) * 1.06f + _tuning.CounterOffsetX;
                y += comboEntryOffset + _tuning.CounterOffsetY;
            }
            else if (leftHud)
            {
                x += _tuning.ScoreOffsetX;
                y += _tuning.ScoreOffsetY;
            }
            if (leftHud)
            {
                x += 20;
                y -= 54;
            }
            if (key.StartsWith("score", StringComparison.Ordinal) && !key.StartsWith("scoreLight", StringComparison.Ordinal) ||
                key.StartsWith("multiplier", StringComparison.Ordinal))
                x += 4;
            if (key.StartsWith("multiplier", StringComparison.Ordinal) ||
                key.StartsWith("score", StringComparison.Ordinal) && !key.StartsWith("scoreLight", StringComparison.Ordinal))
                x -= 12;
            var width = layout.width * (rock ? _tuning.RockScale : combo ? 1.06f : 1);
            var height = layout.height * (rock ? _tuning.RockVerticalScale : 1);
            rect.anchorMin = new Vector2(0, 1);
            rect.anchorMax = new Vector2(0, 1);
            rect.pivot = bulb || key == "rockNeedle" ? new Vector2(0.5f, 0) : new Vector2(0, 1);
            rect.anchoredPosition = bulb
                ? new Vector2(offsetX + x * scale, -offsetY - y * scale)
                : key == "rockNeedle"
                ? new Vector2(offsetX + (x + width / 2) * scale, -offsetY - (y + height) * scale)
                : new Vector2(offsetX + x * scale, -offsetY - y * scale);
            rect.sizeDelta = new Vector2(width * scale, height * scale);
        }
    }

    private void UpdateReadyAnimation()
    {
        var frame = _readyAnimation.Frame(Time.time, _readyFrames.Count);
        var image = _hudImages["readyFrame"];
        var lightning = _hudImages["readyLightning"];
        if (frame < 0)
        {
            image.enabled = false;
            lightning.enabled = false;
            return;
        }
        image.enabled = true;
        image.texture = _readyFrames[frame].Texture;
        _hudLayouts["readyFrame"] = _readyFrames[frame].Layout;
        lightning.enabled = frame < _readyLightningFrames.Count;
        if (lightning.enabled)
        {
            lightning.texture = _readyLightningFrames[frame].Texture;
            _hudLayouts["readyLightning"] = _readyLightningFrames[frame].Layout;
        }
    }

    private float ComboEntryOffset()
    {
        if (_comboEntryStarted < 0)
            return 0;
        var frame = Math.Max(0, (Time.time - _comboEntryStarted) * 60);
        var index = (int)frame;
        if (index >= ComboEntryOffsets.Length - 1)
            return 0;
        var fraction = frame - index;
        return (ComboEntryOffsets[index] * (1 - fraction) + ComboEntryOffsets[index + 1] * fraction) * 1.5f;
    }

}
