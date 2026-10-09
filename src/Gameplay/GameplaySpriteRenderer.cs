using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace Gh3Skin;

internal sealed partial class GameplaySpriteRenderer
{
    private static readonly string[] FretNames = { "Fret_Green", "Fret_Red", "Fret_Yellow", "Fret_Blue", "Fret_Orange" };
    private readonly SkinAssets _assets;
    private readonly SkinOptions _options;
    private readonly List<GemVisual> _notes = new();
    private readonly List<BeatlineVisual> _beatlines = new();
    private readonly List<(SpriteRenderer Renderer, Transform Transform, GameObject Object, string Name)> _effects = new();
    private readonly List<SpriteRenderer> _hidden = new();
    private readonly List<Renderer> _stockHitEffects = new();
    private readonly List<(Renderer Renderer, bool ForceRenderingOff)> _stockStarPowerEffects = new();
    private readonly Dictionary<IntPtr, string> _spriteNames = new();
    private readonly Transform?[] _frets = new Transform?[5];
    private Transform? _player;
    private Transform? _camera;
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
        _stockHitEffects.Clear();
        foreach (var (renderer, forceRenderingOff) in _stockStarPowerEffects)
            if (renderer != null)
                renderer.forceRenderingOff = forceRenderingOff;
        _stockStarPowerEffects.Clear();
        _spriteNames.Clear();
        _sustainFrames.Clear();
        _sustainWidths.Clear();
        _sustainBends.Clear();
        _sustainMaterials.Clear();
        _sustainMaterialIds.Clear();
        _highwayEndZ = 0;
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
            _stockHitEffects.Clear();
            CachePlayer(_player);
            _childCount = _player.childCount;
            ShowNotesToHighwayTop();
            MelonLogger.Msg($"Cached {_notes.Count} note objects, {_effects.Count} gameplay renderers, and {_stockHitEffects.Count} stock hit effects");
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
        if (!_options.KeepStockFlames)
            foreach (var renderer in _stockHitEffects)
                if (renderer != null && renderer.enabled)
                    renderer.enabled = false;
        if (!_options.KeepStockGems)
        {
            var gemFrame = GemAnimation.Frame(Time.timeAsDouble);
            foreach (var note in _notes)
                if (note.Object != null && note.Object.activeSelf)
                    ReplaceGem(note, starPowerActive, gemFrame);
        }
        foreach (var line in _beatlines)
            line.Update(player.mainCamera);
        foreach (var note in _notes)
            if (note.Object != null && note.Object.activeSelf)
                _highwayEndZ = Math.Max(_highwayEndZ, note.Transform.position.z);
        if (_options.ForceWhammyRate > 0)
            WhammyHistory.Force(0.5f - 0.5f * MathF.Cos(2 * MathF.PI * _options.ForceWhammyRate * (float)Time.timeAsDouble));
        _whammyReady = _whammy.Begin();
        UpdateStarWhammy();
        foreach (var entry in _effects)
        {
            var renderer = entry.Renderer;
            if (renderer == null || !entry.Object.activeInHierarchy)
                continue;
            var name = SpriteName(renderer.sprite);
            if (entry.Name is "Sustain(Clone)" or "Sustain Glow")
            {
                TrackSustainMaterial(renderer);
                if (entry.Name == "Sustain Glow")
                    renderer.enabled = false;
                else
                    UpdateSustain(entry.Transform, renderer, name, starPowerActive);
                continue;
            }
            if (name.StartsWith("GH3_", StringComparison.Ordinal))
                continue;
            if (entry.Name == "Neck" && renderer.sprite != null)
                ApplyHighway(entry.Object);
            else if (entry.Name is "sidebar_l" or "sidebar_r")
                renderer.enabled = false;
        }
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
        if (name is "Sustain(Clone)" or "Sustain Glow" or "Neck" or "sidebar_l" or "sidebar_r")
        {
            var renderer = transform.GetComponent<SpriteRenderer>();
            if (renderer != null)
                _effects.Add((renderer, transform, transform.gameObject, name));
        }
        // Clone Hero's hit flames, held-note sparks, Star Power sparks and lightning, hit particles, and open-note
        // flames. GH3's own effects (HitEffectsHud) replace them; GH3 has no open-note effect.
        if (name is "Flames" or "SPSparks" or "SustainSpark" or "SustainSpark Overlay" or "lightning" or "lightning_white" or
            "Particles_NoteHit" or "Particles_Sustain" or "open_hitflames" or "open_hitflames_oval")
        {
            var renderer = transform.GetComponent<Renderer>();
            if (renderer != null)
                _stockHitEffects.Add(renderer);
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


}
