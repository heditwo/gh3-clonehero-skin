using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace Gh3Skin;

internal sealed class GameplaySpriteRenderer
{
    private static readonly string[] FretNames = { "Fret_Green", "Fret_Red", "Fret_Yellow", "Fret_Blue", "Fret_Orange" };
    private readonly SkinAssets _assets;
    private readonly SkinOptions _options;
    private readonly List<GemVisual> _notes = new();
    private readonly List<BeatlineVisual> _beatlines = new();
    private readonly List<(SpriteRenderer Renderer, Transform Transform, GameObject Object, string Name)> _effects = new();
    private readonly List<SpriteRenderer> _hidden = new();
    private readonly List<(Renderer Renderer, bool ForceRenderingOff)> _stockStarPowerEffects = new();
    private readonly Dictionary<IntPtr, string> _spriteNames = new();
    private readonly Transform?[] _frets = new Transform?[5];
    private Transform? _player;
    private Transform? _camera;
    private Transform? _neck;
    private bool _highwayApplied;
    private HighwayScroll? _scroll;
    private float _stockNeckHeight;
    private float _speedSet;
    private bool _noteRenderersSet;
    private SpriteRenderer? _neckRenderer;
    private static readonly int FadeParams = Shader.PropertyToID("_FadeParams");
    private ParticleSystemRenderer? _starParticles;
    private int _childCount = -1;

    public GameplaySpriteRenderer(SkinAssets assets, SkinOptions options)
    {
        _assets = assets;
        _options = options;
    }

    public void Reset()
    {
        _notes.Clear();
        _beatlines.Clear();
        _effects.Clear();
        _hidden.Clear();
        foreach (var (renderer, forceRenderingOff) in _stockStarPowerEffects)
            if (renderer != null)
                renderer.forceRenderingOff = forceRenderingOff;
        _stockStarPowerEffects.Clear();
        _spriteNames.Clear();
        Array.Clear(_frets, 0, _frets.Length);
        _player = null;
        _camera = null;
        _neck = null;
        _highwayApplied = false;
        _scroll = null;
        _stockNeckHeight = 0;
        _speedSet = 0;
        _noteRenderersSet = false;
        _neckRenderer = null;
        _starParticles = null;
        _childCount = -1;
    }

    public void Update(BasePlayer player, ScoreManager score, bool starPowerActive)
    {
        if (_player == null)
        {
            _player = player.transform;
            _camera = _player.Find("PlayerCamera");
            _neck = _player.Find("Neck");
            for (var lane = 0; lane < _frets.Length; lane++)
                _frets[lane] = _player.Find(FretNames[lane]);
            var hud = GameObject.Find("HUDCam")?.transform.Find("HUD");
            if (hud != null)
                CacheHidden(hud);
            _starParticles = score.starProgress?.starParticles?.GetComponent<ParticleSystemRenderer>();
            var stockEffects = player.spNeckRenderer?.spController;
            if (stockEffects != null)
                foreach (var renderer in stockEffects.GetComponentsInChildren<Renderer>(true))
                    _stockStarPowerEffects.Add((renderer, renderer.forceRenderingOff));
        }
        if (_childCount != _player.childCount)
        {
            _notes.Clear();
            _beatlines.Clear();
            _effects.Clear();
            CachePlayer(_player);
            _childCount = _player.childCount;
            ShowNotesToHighwayTop();
            MelonLogger.Msg($"Cached {_notes.Count} note objects and {_effects.Count} gameplay renderers");
        }
        if (_camera != null)
        {
            _camera.localPosition = new Vector3(0, HighwayView.CameraHeight, HighwayView.CameraZ);
            _camera.localEulerAngles = new Vector3(HighwayView.CameraPitch, 0, 0);
            player.mainCamera.fieldOfView = HighwayView.FieldOfView;
        }
        for (var lane = 0; lane < _frets.Length; lane++)
        {
            var fret = _frets[lane];
            if (fret == null) continue;
            var position = fret.localPosition;
            position.x = (lane - 2) * HighwayView.LaneWidth;
            fret.localPosition = position;
        }
        if (_neck != null)
        {
            if (_stockNeckHeight == 0) _stockNeckHeight = _neck.localScale.y;
            _neck.localScale = new Vector3(HighwayView.NeckWidth, HighwayView.NeckLength, 1);
        }
        KeepHighwayAtNoteSpeed();
        FadeHighwayTop();
        if (_starParticles != null)
            _starParticles.enabled = false;
        foreach (var (renderer, _) in _stockStarPowerEffects)
            if (renderer != null)
                renderer.forceRenderingOff = true;
        foreach (var renderer in _hidden)
            if (renderer != null)
                renderer.sprite = _assets.GetSprite("clear");
        if (!_options.KeepStockGems)
        {
            var gemFrame = GemAnimation.Frame(Time.timeAsDouble);
            foreach (var note in _notes)
                if (note.Object != null && note.Object.activeSelf)
                    ReplaceGem(note, starPowerActive, gemFrame);
        }
        foreach (var line in _beatlines)
            line.Update(player.mainCamera);
        foreach (var entry in _effects)
        {
            var renderer = entry.Renderer;
            if (renderer == null || !entry.Object.activeInHierarchy)
                continue;
            var name = SpriteName(renderer.sprite);
            if (name.StartsWith("GH3_", StringComparison.Ordinal))
                continue;
            if (entry.Name is "Sustain(Clone)" or "Sustain Glow" && name.StartsWith("spr_sustain_strip6_", StringComparison.Ordinal))
            {
                var color = renderer.color;
                renderer.color = new Color(color.r, color.g, color.b, Math.Min(color.a, 0.55f));
            }
            else if (entry.Name == "Neck" && renderer.sprite != null)
                ApplyHighway(entry.Object);
            else if (entry.Name is "sidebar_l" or "sidebar_r")
                renderer.enabled = false;
            else
                ReplaceEffect(renderer, name, entry.Name);
        }
    }

    // Clone Hero's own custom-highway path: HighwayScroll sizes the scroll to the texture, so the board moves with the notes.
    // GH3_SKIN_HIGHWAY=native keeps the highway chosen in Clone Hero's settings.
    private void ApplyHighway(GameObject neck)
    {
        if (_highwayApplied || _assets.HighwayTexture == null) return;
        _highwayApplied = true;
        var scroll = neck.GetComponent<HighwayScroll>() ?? neck.GetComponentInParent<HighwayScroll>() ?? _player?.GetComponentInChildren<HighwayScroll>(true);
        if (scroll == null) { MelonLogger.Warning("GH3 highway: HighwayScroll was not found"); return; }
        scroll.Method_Public_Void_Texture2D_0(_assets.HighwayTexture);
        _scroll = scroll;
        MelonLogger.Msg("Applied GH3 highway through Clone Hero's highway scroll");
    }

    // HighwayScroll sizes the texture for the stock neck. The skin sets GH3's repeat length and scrolls it with the notes.
    private void KeepHighwayAtNoteSpeed()
    {
        if (_scroll == null || _stockNeckHeight <= 0) return;
        var tiling = _scroll.field_Private_Vector4_0;
        if (tiling.y != HighwayView.TextureTiling)
        {
            tiling.y = HighwayView.TextureTiling;
            _scroll.field_Private_Vector4_0 = tiling;
        }
        var speed = _scroll.field_Private_Single_1;
        if (Math.Abs(speed - _speedSet) < 1e-6f || speed <= 0) return;
        _speedSet = HighwayView.ScrollSpeed(speed);
        _scroll.field_Private_Single_1 = _speedSet;
    }

    // Clone Hero spawns gems 7.87 units ahead; GH3 shows them up to the top of its highway.
    private void ShowNotesToHighwayTop()
    {
        if (_noteRenderersSet) return;
        foreach (var renderer in UnityEngine.Object.FindObjectsOfType<GuitarNoteRenderer>())
        {
            renderer.noteZPosFarLimit = HighwayView.NeckStartZ + HighwayView.NeckLength * HighwayView.NeckSpriteLength;
            renderer.field_Protected_Double_0 = renderer.noteZPosFarLimit / renderer.noteSpeed;
            _noteRenderersSet = true;
        }
    }

    // The track shader fades between two viewport heights (_FadeParams z and w, from the top).
    private void FadeHighwayTop()
    {
        if (_neckRenderer == null) _neckRenderer = _neck?.GetComponent<SpriteRenderer>();
        var material = _neckRenderer?.sharedMaterial;
        if (material == null || !material.HasProperty(FadeParams)) return;
        var fade = material.GetVector(FadeParams);
        var start = HighwayView.FadeStartY / HighwayView.ScreenHeight;
        var end = (HighwayView.FadeStartY + HighwayView.FadeLength) / HighwayView.ScreenHeight;
        if (fade.z == start && fade.w == end) return;
        fade.z = start;
        fade.w = end;
        material.SetVector(FadeParams, fade);
    }

    private void CachePlayer(Transform transform)
    {
        var name = transform.name;
        if (name == "Note(Clone)")
        {
            _notes.Add(new GemVisual(transform));
            return;
        }
        if (name == "Beatline(Clone)")
        {
            _beatlines.Add(new BeatlineVisual(transform, _assets));
            return;
        }
        if (name is "Sustain(Clone)" or "Sustain Glow" or "Neck" or
            "sidebar_l" or "sidebar_r" or "Flames" or "SPSparks" or "SustainSpark" or "SustainSpark Overlay")
        {
            var renderer = transform.GetComponent<SpriteRenderer>();
            if (renderer != null)
                _effects.Add((renderer, transform, transform.gameObject, name));
        }
        for (var child = 0; child < transform.childCount; child++)
            CachePlayer(transform.GetChild(child));
    }

    private void CacheHidden(Transform transform)
    {
        var renderer = transform.GetComponent<SpriteRenderer>();
        if (renderer != null)
            _hidden.Add(renderer);
        for (var child = 0; child < transform.childCount; child++)
            CacheHidden(transform.GetChild(child));
    }

    private string SpriteName(Sprite? sprite)
    {
        if (sprite == null)
            return "";
        if (!_spriteNames.TryGetValue(sprite.Pointer, out var name))
            _spriteNames[sprite.Pointer] = name = sprite.name;
        return name;
    }

    private void ReplaceGem(GemVisual note, bool starPowerActive, int frame)
    {
        var name = SpriteName(note.Head.sprite);
        var kind = GemKind.FromSprites(name, SpriteName(note.Body?.sprite));
        if (kind == GemKind.Stock)
        {
            if (note.Display != null)
            {
                note.Display.enabled = false;
                note.Head.enabled = true;
                if (note.Body != null) note.Body.enabled = note.Body.sprite != null;
                if (note.Anim != null) note.Anim.enabled = note.Anim.sprite != null;
                if (note.OpenHopo != null) note.OpenHopo.enabled = note.OpenHopo.sprite != null;
            }
            return;
        }
        if (note.Display == null)
        {
            var gem = new GameObject("GH3 Gem");
            gem.layer = note.Head.gameObject.layer;
            gem.transform.SetParent(note.Transform, false);
            gem.transform.localPosition = new Vector3(0, 0.008f, 0);
            note.Display = gem.AddComponent<SpriteRenderer>();
            note.Display.sortingLayerID = note.Head.sortingLayerID;
            note.Display.sortingOrder = 10;
        }
        var lane = 0;
        var distance = float.PositiveInfinity;
        var x = note.Transform.localPosition.x;
        for (var candidate = 0; candidate < _frets.Length; candidate++)
        {
            var fret = _frets[candidate];
            if (fret == null)
                continue;
            var candidateDistance = Math.Abs(x - fret.localPosition.x);
            if (candidateDistance < distance)
            {
                distance = candidateDistance;
                lane = candidate;
            }
        }
        note.Display.enabled = true;
        note.Display.sprite = _assets.Gem(lane, kind, frame, starPowerActive);
        note.Display.color = new Color(1, 1, 1, HighwayView.FadeAt(note.Transform.localPosition.z));
        note.Head.enabled = false;
        if (note.Body != null) note.Body.enabled = false;
        if (note.Anim != null) note.Anim.enabled = false;
        if (note.OpenHopo != null) note.OpenHopo.enabled = false;
    }

    private void ReplaceEffect(SpriteRenderer renderer, string name, string objectName)
    {
        if (_options.KeepStockFlames)
            return;
        string? replacement = null;
        if (objectName == "SustainSpark" && name.StartsWith("sustain_spark_anim_color_", StringComparison.Ordinal))
        {
            renderer.enabled = false;
            return;
        }
        if (objectName == "SustainSpark Overlay" && name.StartsWith("sustain_spark_anim_overlay_", StringComparison.Ordinal) &&
            int.TryParse(name[27..], out var sustainFrame))
            replacement = $"sustain-flame-{sustainFrame:00}";
        else if (objectName == "SPSparks" && name.StartsWith("hit_spark_", StringComparison.Ordinal) &&
            int.TryParse(name[10..], out var sparkFrame))
            replacement = $"sp-spark-{sparkFrame:00}";
        else if (name.StartsWith("hitflame_anim_16_", StringComparison.Ordinal) && int.TryParse(name[17..], out var frame))
            replacement = $"hit-{Math.Clamp(frame, 0, 15):00}";
        if (replacement == null || !_assets.TryGetSprite(replacement, out var sprite))
            return;
        renderer.sprite = sprite;
        if (replacement.StartsWith("hit-", StringComparison.Ordinal))
            renderer.color = new Color(0.765f, 0.336f, 0, 1);
        else if (!replacement.StartsWith("sp-spark-", StringComparison.Ordinal))
            renderer.color = Color.white;
    }

    private sealed class GemVisual
    {
        public Transform Transform { get; }
        public GameObject Object { get; }
        public SpriteRenderer Head { get; }
        public SpriteRenderer? Body { get; }
        public SpriteRenderer? Anim { get; }
        public SpriteRenderer? OpenHopo { get; }
        public SpriteRenderer? Display { get; set; }

        public GemVisual(Transform transform)
        {
            Transform = transform;
            Object = transform.gameObject;
            Head = transform.Find("Head").GetComponent<SpriteRenderer>();
            Body = transform.Find("Body")?.GetComponent<SpriteRenderer>();
            Anim = transform.Find("Anim")?.GetComponent<SpriteRenderer>();
            OpenHopo = transform.Find("Open_HOPO")?.GetComponent<SpriteRenderer>();
            Display = transform.Find("GH3 Gem")?.GetComponent<SpriteRenderer>();
        }
    }
}
