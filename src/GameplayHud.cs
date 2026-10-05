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
    private static readonly (Rect Layout, float Angle)[] Bulbs =
    {
        (new(1364, 870, 48, 84), 40), (new(1390, 848, 46, 84), 27), (new(1422, 842, 50, 100), 14),
        (new(1459, 839, 62, 130), -3), (new(1503, 847, 62, 128), -20), (new(1539, 870, 62, 130), -40)
    };
    private readonly HudTuning _tuning;
    private readonly IReadOnlyDictionary<string, Texture2D> _hudTextures;
    private readonly IReadOnlyList<AnimationFrame> _readyFrames;
    private readonly IReadOnlyList<AnimationFrame> _readyLightningFrames;
    private readonly Dictionary<string, RawImage> _hudImages = new();
    private readonly Dictionary<string, Rect> _hudLayouts = new();
    private readonly string[] _scoreKeys = Enumerable.Range(0, 8).Select(slot => $"score-{slot}").ToArray();
    private readonly string[] _comboKeys = Enumerable.Range(0, 4).Select(slot => $"combo-{slot}").ToArray();
    private readonly RawImage[] _scoreDigits = new RawImage[8];
    private readonly RawImage[] _comboDigits = new RawImage[4];
    private readonly Texture2D[] _scoreTextures;
    private readonly RawImage[] _comboRollNew = new RawImage[4];
    private readonly bool[] _comboRolling = new bool[4];
    private readonly bool[] _comboCarry = new bool[4];
    private readonly float[] _comboRollStarted = new float[4];
    private readonly float[] _comboRollDurations = new float[4];
    private readonly ReadyAnimationTrigger _readyAnimation = new();
    private readonly RockMeterState _rockMeter = new();
    private readonly StarPowerBulbAnimation _bulbAnimation = new();
    private RectTransform? _rockContainer;
    private RockMeterPose? _lastRockPose;
    private HudViewport _rockAnimationViewport;
    private Canvas? _canvas;
    private int _score;
    private int _combo;
    private string _rockColor = "";
    private float _starPower;
    private int _multiplier = 1;
    private bool _starPowerActive;
    private bool _firstUpdate = true;
    private bool _layoutDirty = true;
    private int _width;
    private int _height;
    private float _lastComboEntryOffset = float.NaN;
    private string _streak = "000";
    private int _readyFrame = -2;
    private HudViewport _highwayViewport;
    private int _shownCombo;
    private float _comboEntryStarted = float.NegativeInfinity;

    public GameplayHud(SkinAssets assets, HudTuning tuning)
    {
        _tuning = tuning;
        _hudTextures = assets.Textures;
        _scoreTextures = Enumerable.Range(0, 10).Select(digit => _hudTextures[$"score-digit-{digit}"]).ToArray();
        _readyFrames = assets.ReadyFrames;
        _readyLightningFrames = assets.ReadyLightningFrames;
    }

    public void Reset()
    {
        if (_canvas != null)
            UnityEngine.Object.Destroy(_canvas.gameObject);
        _score = 0;
        _combo = 0;
        _rockMeter.Reset();
        _rockColor = "";
        _starPower = 0;
        _multiplier = 1;
        _starPowerActive = false;
        _firstUpdate = true;
        _layoutDirty = true;
        _lastComboEntryOffset = float.NaN;
        _streak = "000";
        _readyFrame = -2;
        _readyAnimation.Reset();
        _bulbAnimation.Reset();
        _rockContainer = null;
        _lastRockPose = null;
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

    public void Update(GameplayState snapshot, Camera camera)
    {
        var cameraRect = camera.pixelRect;
        var highwayViewport = HudViewport.ForHighway(Screen.height, cameraRect.x, cameraRect.y,
            cameraRect.width, cameraRect.height, camera.fieldOfView);
        if (_highwayViewport != highwayViewport)
        {
            _highwayViewport = highwayViewport;
            _layoutDirty = true;
        }
        var powerChanged = _firstUpdate || _starPower != snapshot.StarPower || _starPowerActive != snapshot.StarPowerActive;
        if (_firstUpdate)
            _rockMeter.Reset(snapshot.Rock);
        var rockChanged = _firstUpdate || _rockMeter.Update(snapshot.Rock, Time.deltaTime);
        var scoreChanged = _firstUpdate || _score != snapshot.Score || _combo != snapshot.Combo ||
            _multiplier != snapshot.Multiplier || _starPowerActive != snapshot.StarPowerActive;
        _score = snapshot.Score;
        _combo = snapshot.Combo;
        _starPower = snapshot.StarPower;
        _multiplier = snapshot.Multiplier;
        _starPowerActive = snapshot.StarPowerActive;
        _readyAnimation.Update(_starPower, Time.time, _starPowerActive);
        var bulbMotionChanged = _bulbAnimation.Update(_starPower, _starPowerActive, Time.time);
        if (powerChanged || bulbMotionChanged) UpdateStarPower();
        if (rockChanged) UpdateRockMeter();
        if (scoreChanged) UpdateScore();
        var streakLength = UpdateCombo();
        UpdateReadyAnimation();
        PositionHud();
        UpdateRockAnimation();
        UpdateComboRolls(streakLength);
        _firstUpdate = false;
    }

    private void UpdateStarPower()
    {
        var blue = _starPowerActive || _starPower >= 0.5f;
        _hudImages["highwayEdges"].texture = _hudTextures[blue ? "highway-edges-blue" : "highway-edges-normal"];
        for (var bulb = 0; bulb < 6; bulb++)
        {
            var extension = _bulbAnimation.Extension(bulb);
            var visible = extension > 0;
            var fill = HudState.StarPowerBulbFill(_starPower, bulb);
            _hudImages[$"bulb{bulb}"].enabled = visible && fill < 1;
            var glow = _hudImages[$"bulbGlow{bulb}"];
            glow.enabled = visible && fill > 0;
            glow.color = new Color(1, 1, 1, fill);
            glow.texture = _hudTextures[blue ? "HUD_rock_tube_glow_full_b" : "HUD_rock_tube_glow_full"];
            if (bulb >= 3)
            {
                var layout = Bulbs[bulb].Layout;
                var angle = (bulb - 3) * 21 * Mathf.Deg2Rad;
                var hiddenDistance = (1 - extension) * 178.5f;
                layout.x -= Mathf.Sin(angle) * hiddenDistance;
                layout.y += Mathf.Cos(angle) * hiddenDistance;
                SetLayout($"bulb{bulb}", layout);
                SetLayout($"bulbGlow{bulb}", layout);
            }
        }
    }

    private void UpdateRockAnimation()
    {
        var pose = _bulbAnimation.Pose;
        var viewport = HudViewport.For(Screen.width, Screen.height);
        if (_lastRockPose == pose && _rockAnimationViewport == viewport)
            return;
        _lastRockPose = pose;
        _rockAnimationViewport = viewport;
        var pivot = new Vector2(viewport.OffsetX + 1326 * viewport.Scale,
            -viewport.BottomOffsetY - 752.4f * viewport.Scale);
        var rotation = Quaternion.Euler(0, 0, -pose.Angle);
        var rotatedPivot = rotation * (Vector3)(pivot * pose.Scale);
        _rockContainer!.anchoredPosition = pivot - (Vector2)rotatedPivot +
            new Vector2(pose.X, -pose.Y) * (1.2f * viewport.Scale);
        _rockContainer.localScale = new Vector3(pose.Scale, pose.Scale, 1);
        _rockContainer.localRotation = rotation;
    }

    private void UpdateRockMeter()
    {
        var gaugeColor = _rockMeter.Color;
        if (_rockColor != gaugeColor)
        {
            foreach (var color in new[] { "green", "yellow", "red" })
            {
                _hudImages["rockBackground" + color].enabled = color == gaugeColor;
                _hudImages["rockLight" + color].enabled = color == gaugeColor;
            }
            _rockColor = gaugeColor;
        }
        _hudImages["rockNeedle"].rectTransform.localEulerAngles = new Vector3(0, 0, _rockMeter.Angle);
    }

    private void UpdateScore()
    {
        var multiplier = HudState.BaseMultiplier(_multiplier, _starPowerActive);
        _hudImages["multiplier"].texture = _hudTextures[HudState.MultiplierTexture(_multiplier, _starPowerActive)];
        var scoreLightColor = _starPowerActive ? "_blue" : multiplier == 4 ? "_purple" : multiplier == 3 ? "_green" : "";
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
        var digits = Math.Clamp(_score, 0, 99999999).ToString();
        var start = 473 - digits.Length * 15.5f;
        for (var slot = 0; slot < 8; slot++)
        {
            _scoreDigits[slot].enabled = slot < digits.Length;
            if (slot < digits.Length)
                _scoreDigits[slot].texture = _scoreTextures[digits[slot] - '0'];
            SetLayout(_scoreKeys[slot], new Rect(start + slot * 31, 815, 31, 37));
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
        if (_combo != _shownCombo)
            _streak = Math.Clamp(_combo, 0, 9999).ToString("D3");
        var streak = _streak;
        var comboStart = streak.Length == 4 ? 388 : 426;
        var firstReel = ComboSlotLayout(comboStart, 0, streak.Length);
        SetLayout("comboBacking0", new Rect(firstReel.x, firstReel.y, 38, firstReel.height));
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
            var layout = ComboSlotLayout(comboStart, slot, streak.Length);
            SetLayout($"comboRoll{slot}", layout);
            _comboDigits[slot].enabled = showCombo && !_comboRolling[slot] && slot < streak.Length;
            if (slot < streak.Length)
                _comboDigits[slot].texture = _hudTextures[ComboDigitTexture(slot, streak[slot] - '0', streak.Length)];
            SetLayout(_comboKeys[slot], layout);
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
            var offset = _comboCarry[slot] ? 0 : _tuning.RollOffsets[frame] * HudViewport.For(Screen.width, Screen.height).Scale * 1.5f;
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
        _rockContainer = new GameObject("GH3 rock meter animation").AddComponent<RectTransform>();
        _rockContainer.SetParent(_canvas!.transform, false);
        _rockContainer.anchorMin = _rockContainer.anchorMax = _rockContainer.pivot = new Vector2(0, 1);
        _rockContainer.sizeDelta = Vector2.zero;
        for (var bulb = 0; bulb < Bulbs.Length; bulb++)
        {
            var (layout, angle) = Bulbs[bulb];
            AddHud($"bulb{bulb}", "HUD_rock_tube", layout);
            AddHud($"bulbGlow{bulb}", "HUD_rock_tube_glow_full", layout);
            _hudImages[$"bulb{bulb}"].rectTransform.localEulerAngles = new Vector3(0, 0, angle);
            _hudImages[$"bulbGlow{bulb}"].rectTransform.localEulerAngles = new Vector3(0, 0, angle);
        }
        foreach (var color in new[] { "green", "yellow", "red" })
            AddHud("rockBackground" + color, "HUD_rock_BG_" + color, new Rect(1390, 780, 256, 256));
        AddHud("rockLights", "HUD_rock_lights_all", new Rect(1390, 780, 256, 128));
        AddHud("rockLightgreen", "HUD_rock_lights_green", new Rect(1518, 780, 128, 128));
        AddHud("rockLightyellow", "HUD_rock_lights_yellow", new Rect(1452, 780, 128, 128));
        AddHud("rockLightred", "HUD_rock_lights_red", new Rect(1390, 780, 128, 128));
        AddHud("rockNeedle", "HUD_rock_needle", new Rect(1510, 831, 16, 114));
        AddHud("rockBody", "HUD_rock_body", new Rect(1390, 780, 256, 256));
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
            _scoreDigits[slot] = AddHud(_scoreKeys[slot], "score-digit-0", new Rect(0, 817, 29, 34));
        AddHud("multiplier", "HUD_score_nixie_1a_wide", new Rect(385, 873, 118, 118));
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
            _comboDigits[slot] = AddHud(_comboKeys[slot], $"combo-{slot}-digit-0", new Rect(0, 967, 38, 50));
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

    private RawImage AddHud(string key, string spriteName, Rect layout)
    {
        var gameObject = new GameObject("GH3 " + key);
        var rect = gameObject.AddComponent<RectTransform>();
        rect.SetParent(_canvas!.transform, false);
        if (key.StartsWith("rock", StringComparison.Ordinal) || key.StartsWith("bulb", StringComparison.Ordinal))
            rect.SetParent(_rockContainer, false);
        var image = gameObject.AddComponent<RawImage>();
        image.texture = _hudTextures[spriteName];
        image.raycastTarget = false;
        _hudImages[key] = image;
        _hudLayouts[key] = layout;
        return image;
    }

    private void SetLayout(string key, Rect layout)
    {
        if (_hudLayouts[key] == layout)
            return;
        _hudLayouts[key] = layout;
        _layoutDirty = true;
    }

    private void PositionHud()
    {
        var comboEntryOffset = ComboEntryOffset();
        if (!_layoutDirty && _width == Screen.width && _height == Screen.height && _lastComboEntryOffset == comboEntryOffset)
            return;
        _width = Screen.width;
        _height = Screen.height;
        _lastComboEntryOffset = comboEntryOffset;
        _layoutDirty = false;
        var viewport = HudViewport.For(Screen.width, Screen.height);
        foreach (var (key, image) in _hudImages)
        {
            var layout = _hudLayouts[key];
            var rect = image.rectTransform;
            var bulb = key.StartsWith("bulb", StringComparison.Ordinal);
            var rock = key.StartsWith("rock", StringComparison.Ordinal);
            var combo = key.StartsWith("combo", StringComparison.Ordinal);
            var leftHud = combo || key.StartsWith("score", StringComparison.Ordinal) || key.StartsWith("multiplier", StringComparison.Ordinal);
            var highwayEdges = key == "highwayEdges";
            if (highwayEdges)
            {
                var highway = _highwayViewport;
                rect.anchorMin = new Vector2(0, 1);
                rect.anchorMax = new Vector2(0, 1);
                rect.pivot = new Vector2(0, 1);
                rect.anchoredPosition = new Vector2(highway.OffsetX, -highway.CenterOffsetY);
                rect.sizeDelta = new Vector2(1920 * highway.Scale, 1080 * highway.Scale);
                continue;
            }
            var imageViewport = key is "readyFrame" or "readyLightning" ? _highwayViewport : viewport;
            var imageScale = imageViewport.Scale;
            var offsetY = leftHud || rock || bulb ? imageViewport.BottomOffsetY : imageViewport.CenterOffsetY;
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
                ? new Vector2(imageViewport.OffsetX + x * imageScale, -offsetY - y * imageScale)
                : key == "rockNeedle"
                ? new Vector2(imageViewport.OffsetX + (x + width / 2) * imageScale, -offsetY - (y + height) * imageScale)
                : new Vector2(imageViewport.OffsetX + x * imageScale, -offsetY - y * imageScale);
            rect.sizeDelta = new Vector2(width * imageScale, height * imageScale);
        }
    }

    private void UpdateReadyAnimation()
    {
        var frame = _readyAnimation.Frame(Time.time, _readyFrames.Count);
        if (frame == _readyFrame)
            return;
        _readyFrame = frame;
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
        SetLayout("readyFrame", _readyFrames[frame].Layout);
        lightning.enabled = frame < _readyLightningFrames.Count;
        if (lightning.enabled)
        {
            lightning.texture = _readyLightningFrames[frame].Texture;
            SetLayout("readyLightning", _readyLightningFrames[frame].Layout);
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
