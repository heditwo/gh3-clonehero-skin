using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(Gh3Skin.SkinMod), "GH3 Gameplay Skin", "0.9.8", "hedi")]
[assembly: MelonGame("srylain Inc.", "Clone Hero")]

namespace Gh3Skin;

public sealed class SkinMod : MelonMod
{
    private SkinOptions? _options;
    private GameplaySpriteRenderer? _renderer;
    private GameplayHud? _hud;
    private PreviewCapture? _capture;
    private GameplayStage? _stage;
    private readonly GameplayScene _scene = new();
    private bool _gameplay;
    private double _lastSongTime = double.NegativeInfinity;

    public override void OnEarlyInitializeMelon()
    {
        // MelonLoader retries a bHaptics WebSocket on a timer; disposing those sockets has crashed the CLR in this game.
        if (Environment.GetEnvironmentVariable("GH3_SKIN_KEEP_BHAPTICS") == "1") return;
        try
        {
            var manager = Type.GetType("bHapticsLib.bHapticsManager, bHapticsLib");
            var disconnected = manager?.GetMethod("Disconnect", Type.EmptyTypes)?.Invoke(null, null);
            MelonLogger.Msg($"MelonLoader bHaptics connection stopped: {disconnected ?? "unavailable"}");
        }
        catch (Exception error)
        {
            MelonLogger.Warning($"MelonLoader bHaptics connection could not stop: {error.Message}");
        }
    }

    public override void OnInitializeMelon()
    {
        if (!GameCompatibility.Supports(Application.version))
        {
            MelonLogger.Warning($"GH3 skin requires Clone Hero {GameCompatibility.Version}; found {Application.version}.");
            return;
        }
        var assetsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Mods", "GH3Skin", "assets");
        _options = SkinOptions.FromEnvironment();
        var tuning = HudTuning.Load(Path.Combine(assetsDirectory, "..", "counter-tuning.json"));
        var assets = new SkinAssets(assetsDirectory);
        _renderer = new GameplaySpriteRenderer(assets, _options);
        _hud = new GameplayHud(assets, tuning);
        _capture = new PreviewCapture(_options);
        _stage = new GameplayStage(assetsDirectory);
        MelonLogger.Msg($"Loaded {assets.GameplaySpriteCount} GH3 skin gameplay sprites");
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        _gameplay = sceneName == "Gameplay";
        _scene.Reset();
        _lastSongTime = double.NegativeInfinity;
        _renderer?.Reset();
        _hud?.Reset();
        _capture?.Reset();
        _stage?.Reset();
        if (_gameplay)
            MelonLogger.Msg("GH3 gameplay scene loaded");
    }

    public override void OnLateUpdate()
    {
        if (_renderer == null || _hud == null || _options == null || _capture == null ||
            !_gameplay || !_scene.TryGetPlayer(out var player, out var score))
            return;
        var songTime = player.gameManager.songTime;
        if (songTime < _lastSongTime - 0.1)
        {
            _renderer.Reset();
            _hud.Reset();
            _capture.Reset();
        }
        _lastSongTime = songTime;
        _hud.EnsureCreated();
        GameplayState state = default;
        FrameProfile.Measure("state", () => state = GameplayStateReader.Read(player, score, _options));
        FrameProfile.Measure("notes", () => _renderer.Update(player, score, state.StarPowerActive));
        _stage?.Update(player, state.Combo, state.Rock);
        FrameProfile.Measure("hud", () => _hud.Update(state, player.mainCamera));
        _capture.Update(songTime);
        FrameProfile.EndFrame(songTime);
    }

    public override void OnApplicationQuit() => CloseStage();

    public override void OnDeinitializeMelon() => CloseStage();

    private void CloseStage()
    {
        _gameplay = false;
        _stage?.Reset();
        _stage = null;
    }
}
