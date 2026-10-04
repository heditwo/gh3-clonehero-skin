using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using Il2Cpp;
using System.Text.Json;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

[assembly: MelonInfo(typeof(Gh3Skin.SkinMod), "GH3 Gameplay Skin", "0.8.85", "hedi")]
[assembly: MelonGame("srylain Inc.", "Clone Hero")]

namespace Gh3Skin;

public sealed class SkinMod : MelonMod
{
    private static readonly string[] Lanes = { "green", "red", "yellow", "blue", "orange" };
    private static readonly float[] ComboEntryOffsets =
    {
        62, 50, 32, 20, 10, -6, -20, -30, -37, -41, -40, -20,
        -13, -4, 2, 6, 7, 7, 6, 6, 6, 4, 2, 0
    };
    private const float ComboCarryDuration = 5f / 60f;
    private float[] _comboRollAlphas = Array.Empty<float>();
    private float[] _comboRollOffsets = Array.Empty<float>();
    private int _lastReelWidth;
    private int _firstReelWidth;
    private float _counterOffsetX;
    private float _counterOffsetY;
    private float _scoreOffsetX;
    private float _scoreOffsetY;
    private float _rockScale;
    private float _rockVerticalScale;
    private float _rockOffsetX;
    private float _rockOffsetY;
    private readonly Dictionary<string, Sprite> _sprites = new();
    private readonly Dictionary<string, Texture2D> _hudTextures = new();
    private readonly Dictionary<string, RawImage> _hudImages = new();
    private readonly Dictionary<string, Rect> _hudLayouts = new();
    private readonly List<float> _captureTimes = new();
    private readonly RawImage[] _comboRollNew = new RawImage[4];
    private readonly bool[] _comboRolling = new bool[4];
    private readonly bool[] _comboCarry = new bool[4];
    private readonly float[] _comboRollStarted = new float[4];
    private readonly float[] _comboRollDurations = new float[4];
    private Canvas? _canvas;
    private bool _ready;
    private bool _captured;
    private float _gameplayStart;
    private float _nextInspection;
    private float _captureAfter;
    private int _captureIndex;
    private bool _keepStockGems;
    private bool _keepStockFlames;
    private string _captureDirectory = "/tmp/opencode/gh3-matrix-mod";
    private float? _previewPower;
    private float? _previewRock;
    private float _rock = 0.5f;
    private float _starPower;
    private int _score;
    private int _combo;
    private int _shownCombo;
    private float _comboEntryStarted = float.NegativeInfinity;

    public override void OnInitializeMelon()
    {
        var root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Mods", "GH3Skin", "assets");
        using (var tuning = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "..", "counter-tuning.json"))))
        {
            var settings = tuning.RootElement;
            _lastReelWidth = settings.GetProperty("lastReelWidth").GetInt32();
            _firstReelWidth = settings.GetProperty("firstReelWidth").GetInt32();
            _counterOffsetX = settings.GetProperty("counterOffsetX").GetSingle();
            _counterOffsetY = settings.GetProperty("counterOffsetY").GetSingle();
            _scoreOffsetX = settings.GetProperty("scoreOffsetX").GetSingle();
            _scoreOffsetY = settings.GetProperty("scoreOffsetY").GetSingle();
            _rockScale = settings.GetProperty("rockScale").GetSingle();
            _rockVerticalScale = settings.GetProperty("rockVerticalScale").GetSingle();
            _rockOffsetX = settings.GetProperty("rockOffsetX").GetSingle();
            _rockOffsetY = settings.GetProperty("rockOffsetY").GetSingle();
            _comboRollAlphas = settings.GetProperty("rollAlpha").EnumerateArray().Select(value => value.GetSingle()).ToArray();
            _comboRollOffsets = settings.GetProperty("rollOffset720").EnumerateArray().Select(value => value.GetSingle()).ToArray();
            if (_lastReelWidth is < 30 or > 50 || _firstReelWidth is < 30 or > 50 ||
                _comboRollAlphas.Length < 2 || _comboRollAlphas.Length != _comboRollOffsets.Length)
                throw new InvalidDataException("Invalid counter tuning");
        }
        if (float.TryParse(Environment.GetEnvironmentVariable("GH3_SKIN_CAPTURE_AFTER"), System.Globalization.NumberStyles.Float,
                 System.Globalization.CultureInfo.InvariantCulture, out var captureAfter) && captureAfter > 0)
            _captureAfter = captureAfter;
        var captureTimes = Environment.GetEnvironmentVariable("GH3_SKIN_CAPTURE_TIMES");
        if (!string.IsNullOrWhiteSpace(captureTimes))
            foreach (var value in captureTimes.Split(','))
                if (float.TryParse(value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var time) && time > 0)
                    _captureTimes.Add(time);
        _keepStockGems = Environment.GetEnvironmentVariable("GH3_SKIN_KEEP_STOCK_GEMS") == "1";
        _keepStockFlames = Environment.GetEnvironmentVariable("GH3_SKIN_KEEP_STOCK_FLAMES") == "1";
        _captureDirectory = Environment.GetEnvironmentVariable("GH3_SKIN_CAPTURE_DIR") ?? _captureDirectory;
        if (float.TryParse(Environment.GetEnvironmentVariable("GH3_SKIN_PREVIEW_POWER"), System.Globalization.NumberStyles.Float,
                  System.Globalization.CultureInfo.InvariantCulture, out var previewPower) && _captureAfter > 0)
            _previewPower = Math.Clamp(previewPower, 0, 1);
        if (float.TryParse(Environment.GetEnvironmentVariable("GH3_SKIN_PREVIEW_ROCK"), System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out var previewRock) && _captureAfter > 0)
            _previewRock = Math.Clamp(previewRock, 0, 1);
        if (float.TryParse(Environment.GetEnvironmentVariable("GH3_SKIN_FORCE_POWER"), System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out var forcedPower))
            _previewPower = Math.Clamp(forcedPower, 0, 1);
        if (float.TryParse(Environment.GetEnvironmentVariable("GH3_SKIN_FORCE_ROCK"), System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out var forcedRock))
            _previewRock = Math.Clamp(forcedRock, 0, 1);
        _hudTextures["highway"] = LoadTexture(Path.Combine(root, "highway.png"));

        foreach (var lane in Lanes)
        {
            Load(root, $"{lane}-note", 720, new Vector2(0.5f, 0.16f));
            Load(root, $"{lane}-glow", 720, new Vector2(0.5f, 0.16f));
            for (var frame = 0; frame < 16; frame++)
            {
                Load(root, $"{lane}-star-{frame}", 720, new Vector2(0.5f, 0.16f));
                Load(root, $"{lane}-star-glow-{frame}", 720, new Vector2(0.5f, 0.16f));
            }
        }

        Load(root, "clear", 20, new Vector2(0.5f, 0.16f));
        Load(root, "beatline", 1100, new Vector2(0.5f, 0.2f));
        for (var frame = 0; frame < 16; frame++)
        {
            Load(root, $"hit-{frame:00}", 150, new Vector2(0.5f, 0.2f));
            Load(root, $"sustain-flame-{frame:00}", 400, new Vector2(0.5f, 0.2f));
            Load(root, $"sp-spark-{frame:00}", 333, new Vector2(0.5f, 0.5f));
        }

        var hudNames = new List<string> { "HUD_rock_body", "HUD_rock_BG_green", "HUD_rock_BG_yellow", "HUD_rock_BG_red", "HUD_rock_lights_all", "HUD_rock_lights_green", "HUD_rock_lights_yellow", "HUD_rock_lights_red", "HUD_rock_needle", "HUD_score_body", "HUD_score_frame", "HUD_score_ring_front", "HUD_score_x", "HUD_counter_body", "HUD_counter_frame", "HUD_counter_drum_icon", "HUD_counter_drum_icon_back", "HUD_score_nixie_1a_wide", "HUD_score_nixie_2a_wide", "HUD_score_nixie_3a_wide", "HUD_score_nixie_4a_wide", "HUD_rock_tube", "HUD_rock_tube_glow_full", "HUD_rock_tube_glow_full_b" };
        hudNames.AddRange(new[] { "", "_blue", "_green", "_purple" }.SelectMany(color =>
            Enumerable.Range(0, 3).Select(state => $"HUD_score_light_{state}{color}")));
        hudNames.AddRange(Enumerable.Range(0, 10).Select(digit => $"score-digit-{digit}"));
        hudNames.AddRange(Enumerable.Range(0, 4).SelectMany(slot => Enumerable.Range(0, 10).Select(digit => $"combo-{slot}-digit-{digit}")));
        hudNames.AddRange(Enumerable.Range(0, 10).Select(digit => $"combo-2-dark-digit-{digit}"));
        hudNames.AddRange(Enumerable.Range(0, 10).Select(digit => $"combo-2-extended-digit-{digit}"));
        hudNames.AddRange(Enumerable.Range(0, 4).Select(slot => $"combo-reel-{slot}"));
        hudNames.Add("combo-reel-2-dark");
        hudNames.Add("combo-reel-2-extended");
        hudNames.AddRange(new[] { "light", "dark" }.SelectMany(shade => Enumerable.Range(0, 10).Select(digit => $"combo-glyph-{shade}-{digit}")));

        foreach (var name in hudNames)
        {
            var texture = LoadTexture(Path.Combine(root, name + ".png"));
            if (name.StartsWith("HUD_score_light_", StringComparison.Ordinal))
                texture.wrapMode = TextureWrapMode.Clamp;
            _hudTextures[name] = texture;
        }

        _ready = true;
        MelonLogger.Msg($"Loaded {_sprites.Count} GH3 skin gameplay sprites");
    }

    private void Load(string root, string name, float pixelsPerUnit, Vector2 pivot)
    {
        var texture = LoadTexture(Path.Combine(root, name + ".png"));
        var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), pivot, pixelsPerUnit);
        sprite.name = "GH3_" + name;
        sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
        texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
        _sprites[name] = sprite;
    }

    private static Texture2D LoadTexture(string file)
    {
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!texture.LoadImage(new Il2CppStructArray<byte>(File.ReadAllBytes(file))))
            throw new InvalidDataException(file);

        texture.filterMode = FilterMode.Bilinear;
        texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return texture;
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        if (sceneName == "Gameplay")
        {
            _gameplayStart = Time.time;
            _captured = false;
            _captureIndex = 0;
            _rock = 0.5f;
            _starPower = 0;
            _score = 0;
            _combo = 0;
            _shownCombo = 0;
            _comboEntryStarted = float.NegativeInfinity;
            Array.Fill(_comboRolling, false);
            Array.Fill(_comboCarry, false);
            _hudImages.Clear();
            _hudLayouts.Clear();
            _canvas = null;
            MelonLogger.Msg("GH3 gameplay scene loaded");
        }
    }

    public override void OnLateUpdate()
    {
        if (!_ready || SceneManager.GetActiveScene().name != "Gameplay")
            return;

        if (_canvas == null)
            CreateHud();

        var renderers = UnityEngine.Object.FindObjectsOfType<SpriteRenderer>();
        var changed = 0;
        foreach (var renderer in renderers)
        {
            var original = renderer.sprite;
            if (original != null && original.name.StartsWith("GH3_", StringComparison.Ordinal))
                continue;

            var name = original?.name ?? "";
            var objectName = renderer.gameObject.name;
            if (objectName == "Head" && renderer.transform.parent?.name == "Note(Clone)")
            {
                if (_keepStockGems)
                    continue;
                var note = renderer.transform.parent;
                var open = name.StartsWith("spr_open_notes_strip5_", StringComparison.Ordinal);
                var standard = name.StartsWith("spr_newnotes_strip4_", StringComparison.Ordinal);
                var star = name.StartsWith("spr_star_notes_", StringComparison.Ordinal);
                if (!open && !standard && !star)
                    continue;
                var body = note.Find("Body")?.GetComponent<SpriteRenderer>();
                var anim = note.Find("Anim")?.GetComponent<SpriteRenderer>();
                var openHopo = note.Find("Open_HOPO")?.GetComponent<SpriteRenderer>();
                var gemTransform = note.Find("GH3 Gem");
                var tap = body?.sprite?.name == "spr_newnotes_strip4_3";
                if (open || tap)
                {
                    if (gemTransform != null)
                    {
                        gemTransform.GetComponent<SpriteRenderer>().enabled = false;
                        renderer.enabled = true;
                        if (body != null) body.enabled = body.sprite != null;
                        if (anim != null) anim.enabled = anim.sprite != null;
                        if (openHopo != null) openHopo.enabled = openHopo.sprite != null;
                    }
                    continue;
                }
                if (gemTransform == null)
                {
                    var gem = new GameObject("GH3 Gem");
                    gem.layer = renderer.gameObject.layer;
                    gem.transform.SetParent(note, false);
                    var gemRenderer = gem.AddComponent<SpriteRenderer>();
                    gemRenderer.sortingLayerID = renderer.sortingLayerID;
                    gemRenderer.sortingOrder = 10;
                    gemTransform = gem.transform;
                }
                var laneName = LaneAt(renderer.transform.position.x);
                gemTransform.localPosition = new Vector3(0, 0.008f, 0);
                var display = gemTransform.GetComponent<SpriteRenderer>();
                display.enabled = true;
                display.sprite = star ? _sprites[$"{laneName}-{(name == "spr_star_notes_strip4_1" ? "star-glow" : "star")}-{(int)(Time.time * 12) % 16}"]
                    : _sprites[$"{laneName}-{(name == "spr_newnotes_strip4_1" ? "glow" : "note")}"];
                renderer.enabled = false;
                if (body != null) body.enabled = false;
                if (anim != null) anim.enabled = false;
                if (openHopo != null) openHopo.enabled = false;
                continue;
            }
            if (objectName is "Sustain(Clone)" or "Sustain Glow" && name.StartsWith("spr_sustain_strip6_", StringComparison.Ordinal))
            {
                var color = renderer.color;
                renderer.color = new Color(color.r, color.g, color.b, Math.Min(color.a, 0.55f));
            }
            if (objectName == "Neck" && original != null && renderer.transform.parent?.name == "Guitar Player(Clone)")
            {
                if (!_sprites.TryGetValue("highway", out var highway))
                {
                    var texture = _hudTextures["highway"];
                    highway = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height),
                        new Vector2(original.pivot.x / original.rect.width, original.pivot.y / original.rect.height), original.pixelsPerUnit);
                    highway.name = "GH3_highway";
                    _sprites["highway"] = highway;
                    MelonLogger.Msg($"Applied GH3 highway (original PPU {original.pixelsPerUnit})");
                }
                renderer.sprite = highway;
                continue;
            }
            string? replacement = null;

            if (renderer.transform.position.z < -4.5f && renderer.transform.position.x > 4)
                replacement = "clear";

            else if (objectName == "Beatline(Clone)")
                replacement = "beatline";
            else if (!_keepStockFlames && objectName == "SustainSpark" && name.StartsWith("sustain_spark_anim_color_", StringComparison.Ordinal))
            {
                renderer.enabled = false;
                continue;
            }
            else if (!_keepStockFlames && objectName == "SustainSpark Overlay" &&
                     name.StartsWith("sustain_spark_anim_overlay_", StringComparison.Ordinal) &&
                     int.TryParse(name[27..], out var sustainFrame))
                replacement = $"sustain-flame-{sustainFrame:00}";
            else if (!_keepStockFlames && objectName == "SPSparks" && name.StartsWith("hit_spark_", StringComparison.Ordinal) &&
                     int.TryParse(name[10..], out var sparkFrame))
                replacement = $"sp-spark-{sparkFrame:00}";
            else if (!_keepStockFlames && name.StartsWith("hitflame_anim_16_", StringComparison.Ordinal) && int.TryParse(name[17..], out var frame))
                replacement = $"hit-{Math.Clamp(frame, 0, 15):00}";

            if (replacement == null || !_sprites.TryGetValue(replacement, out var sprite))
                continue;

            renderer.sprite = sprite;
            if (replacement.StartsWith("hit-", StringComparison.Ordinal))
                renderer.color = new Color(0.765f, 0.336f, 0, 1);
            else if (replacement != "clear" && !replacement.StartsWith("sp-spark-", StringComparison.Ordinal))
                renderer.color = Color.white;
            changed++;
        }

        if (changed > 0 && Time.time >= _nextInspection)
        {
            MelonLogger.Msg($"Replaced {changed} active GH3 gameplay sprites");
            _nextInspection = Time.time + 8;
        }

        foreach (var score in UnityEngine.Object.FindObjectsOfType<ScoreManager>())
        {
            _score = score.field_Private_Int32_4;
            _combo = score.field_Private_Int32_2;
            var starParticles = score.starProgress?.starParticles;
            if (starParticles != null)
            {
                var starRenderer = starParticles.GetComponent<ParticleSystemRenderer>();
                if (starRenderer != null)
                    starRenderer.enabled = false;
            }
            break;
        }

        foreach (var player in UnityEngine.Object.FindObjectsOfType<BasePlayer>())
        {
            if (player.engine != null)
            {
                _rock = Math.Clamp(player.engine.field_Protected_Single_0, 0, 1);
                var amount = player.spBar != null && player.spBar.field_Private_Single_0 >= 0
                    ? player.spBar.field_Private_Single_0
                    : player.engine.prop_Single_0;
                _starPower = Math.Clamp(amount > 1 ? amount / 100 : amount, 0, 1);
            }
            break;
        }

        if (_previewPower.HasValue)
            _starPower = _previewPower.Value;
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
        if (_previewRock.HasValue)
            _rock = _previewRock.Value;
        var gaugeColor = _rock < 0.35f ? "red" : _rock < 0.58f ? "yellow" : "green";
        foreach (var color in new[] { "green", "yellow", "red" })
        {
            _hudImages["rockBackground" + color].enabled = color == gaugeColor;
            _hudImages["rockLight" + color].enabled = color == gaugeColor;
        }
        _hudImages["rockNeedle"].rectTransform.localEulerAngles = new Vector3(0, 0, 55 - 90 * _rock);
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
                _comboRollDurations[slot] = _comboCarry[slot] ? ComboCarryDuration : _comboRollAlphas.Length / 60f;
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
        PositionHud();
        for (var slot = 0; slot < 4; slot++)
        {
            if (!_comboRolling[slot])
                continue;
            var viewport = _hudImages[$"comboRoll{slot}"].rectTransform;
            var elapsed = Math.Max(0, Time.time - _comboRollStarted[slot]);
            var progress = Math.Clamp(elapsed / _comboRollDurations[slot], 0, 1);
            var frame = Math.Clamp((int)(elapsed * 60), 0, _comboRollAlphas.Length - 1);
            _comboRollNew[slot].rectTransform.sizeDelta = slot == 2 && streak.Length == 3
                ? new Vector2(viewport.rect.width * 35f / _lastReelWidth, viewport.rect.height)
                : viewport.rect.size;
            var offset = _comboCarry[slot] ? 0 : _comboRollOffsets[frame] * Screen.height / 720f;
            var opacity = _comboCarry[slot]
                ? Math.Clamp((progress - 0.5f) / 0.5f, 0, 1)
                : _comboRollAlphas[frame];
            _comboRollNew[slot].rectTransform.anchoredPosition = new Vector2(0, -offset);
            _comboRollNew[slot].color = new Color(1, 1, 1, opacity);
        }

        if (_captureAfter > 0 && !_captured && Time.time - _gameplayStart > _captureAfter)
        {
            var screenshot = "/home/hedi/Work/gh3-clonehero-skin/preview.png";
            ScreenCapture.CaptureScreenshot(screenshot);
            MelonLogger.Msg($"Captured {screenshot}");
            _captured = true;
        }
        if (_captureIndex < _captureTimes.Count && Time.time - _gameplayStart > _captureTimes[_captureIndex])
        {
            var screenshot = Path.Combine(_captureDirectory, $"frame-{_captureIndex:00}.png");
            ScreenCapture.CaptureScreenshot(screenshot);
            MelonLogger.Msg($"Captured {screenshot} at {_captureTimes[_captureIndex]}");
            _captureIndex++;
        }
    }

    private void CreateHud()
    {
        var gameObject = new GameObject("GH3 Gameplay HUD");
        _canvas = gameObject.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.overrideSorting = true;
        _canvas.sortingOrder = 500;
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
            0 => _firstReelWidth,
            1 => 36,
            2 when length == 3 => _lastReelWidth,
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
            var x = bulb ? layout.x : rock ? 1330 + (layout.x - 1390) * _rockScale + _rockOffsetX : layout.x;
            var y = bulb ? layout.y - 72 : rock ? 753 + (layout.y - 780) * _rockVerticalScale + _rockOffsetY : layout.y - 12;
            if (combo)
            {
                x = 383 + (x - 383) * 1.06f + _counterOffsetX;
                y += comboEntryOffset + _counterOffsetY;
            }
            else if (leftHud)
            {
                x += _scoreOffsetX;
                y += _scoreOffsetY;
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
            var width = layout.width * (rock ? _rockScale : combo ? 1.06f : 1);
            var height = layout.height * (rock ? _rockVerticalScale : 1);
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

    private static string LaneAt(float x)
    {
        var index = Math.Clamp((int)MathF.Round((x + 0.39f) / 0.195f), 0, 4);
        return Lanes[index];
    }

}
