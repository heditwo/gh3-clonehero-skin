using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(Gh3Skin.SkinMod), "GH3 Gameplay Skin", Gh3Skin.ModInfo.Version, "hedi")]
[assembly: MelonGame("srylain Inc.", "Clone Hero")]

namespace Gh3Skin;

public sealed class SkinMod : MelonMod
{
    private SkinOptions? _options;
    private ComboOverride? _comboOverride;
    private GameplaySpriteRenderer? _renderer;
    private GameplayHud? _hud;
    private PreviewCapture? _capture;
    private GameplayStage? _stage;
    private readonly PickerOverlay _picker = new();
    private PickerTest? _pickerTest;
    private SkinAssets? _assets;
    private readonly GameplayScene _scene = new();
    private bool _gameplay;
    private double _lastSongTime = double.NegativeInfinity;

    public override void OnEarlyInitializeMelon()
    {
        // MelonLoader installs its Il2Cpp hooks after this call, so the guard is in place for them.
        var guard = HookPageGuard.Install() ?? $"off (Linux only; this is {System.Runtime.InteropServices.RuntimeInformation.OSDescription})";
        MelonLogger.Msg($"Linux hook page guard: {guard}");
        LoadTiming.Mark("mod early init");
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
        var steps = new LoadTiming.Steps();
        var placed = HookPageGuard.TakePlaced();
        if (placed.Length > 0) MelonLogger.Msg($"Linux hook page guard: {placed.Length} hook page(s) moved to safe pages ({string.Join(", ", placed)})");
        var problem = GameCompatibility.Problem(Application.version, Application.platform.ToString());
        if (problem != null)
        {
            MelonLogger.Warning(problem);
            return;
        }
        var bindingError = CloneHeroBindings.Bind();
        steps.Step("bindings");
        if (bindingError != null)
        {
            MelonLogger.Warning($"GH3 skin cannot bind to Clone Hero {Application.version} on {Application.platform}: {bindingError}. The skin is off.");
            return;
        }
        var assetsDirectory = Path.Combine(Packages.PackageRuntime.ModDirectory, "assets");
        _options = SkinOptions.FromEnvironment();
        _comboOverride = new ComboOverride(_options.ForceCombo, _options.ForceComboBreak);
        var tuning = HudTuning.Load(SkinAssetPaths.HudTuning(assetsDirectory));
        var assets = _assets = new SkinAssets(assetsDirectory);
        steps.Step("skin texture plan");
        _renderer = new GameplaySpriteRenderer(assets, _options);
        _hud = new GameplayHud(assets, tuning, !_options.KeepStockFlames);
        _capture = new PreviewCapture(_options);
        _stage = new GameplayStage(assetsDirectory);
        _pickerTest = PickerTest.FromEnvironment();
        steps.Step("renderers");
        steps.Finish("mod init");
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        _gameplay = sceneName == "Gameplay";
        LoadTiming.SceneLoaded(sceneName);
        if (_gameplay) UploadSkin(double.PositiveInfinity);
        _songClockMarked = 0;
        if (_stage != null) _picker.SceneChanged(!_gameplay);
        _scene.Reset();
        _lastSongTime = double.NegativeInfinity;
        _renderer?.Reset();
        _hud?.Reset();
        _capture?.Reset();
        _comboOverride?.Reset();
        _stage?.Reset();
        BindingTrace.Reset();
        if (_gameplay) MelonLogger.Msg("GH3 gameplay scene loaded");
    }

    public override void OnUpdate()
    {
        LoadTiming.Frame();
        if (_stage == null) return;
        if (_assets is { Ready: false }) UploadSkin(4);
        Packages.PackageRuntime.Update();
        _picker.Update();
        _pickerTest?.Update(_picker);
    }

    public override void OnGUI()
    {
        if (_stage != null) _picker.OnGUI();
    }

    public override void OnLateUpdate()
    {
        if (_renderer == null || _hud == null || _options == null || _capture == null ||
            !_gameplay || !_scene.TryGetPlayer(out var player, out var score))
            return;
        var songTime = player.gameManager.songTime;
        if (LoadTiming.Enabled) MarkSongClock(songTime);
        if (songTime < _lastSongTime - 0.1)
        {
            _renderer.Reset();
            _hud.Reset();
            _capture.Reset();
            _comboOverride?.Reset();
        }
        _lastSongTime = songTime;
        _hud.EnsureCreated();
        GameplayState state = default;
        FrameProfile.Measure("state", () => state = GameplayStateReader.Read(player, score, _options, _comboOverride!));
        FrameProfile.Measure("notes", () => _renderer.Update(player, score, state.StarPowerActive));
        _stage?.Update(player, state.Combo, state.Rock);
        FrameProfile.Measure("hud", () => _hud.Update(state, player.mainCamera, songTime, _stage?.ShowFlow == true, _stage?.WinTime ?? double.PositiveInfinity));
        FrameProfile.Measure("hits", () => _hud.UpdateHitEffects(player, state, songTime));
        _capture.Update(songTime);
        if (BindingTrace.Enabled) BindingTrace.Update(player, state, songTime, _stage?.AudioVoices ?? "none");
        FrameProfile.EndFrame(songTime);
    }

    // The skin textures upload a few milliseconds in each menu frame; the gameplay scene waits for the rest.
    private void UploadSkin(double milliseconds)
    {
        if (_assets is not { Ready: false } assets) return;
        try
        {
            if (assets.Upload(milliseconds)) LoadTiming.Mark("skin textures uploaded");
        }
        catch (Exception error)
        {
            MelonLogger.Error($"GH3 skin textures could not load; the skin is off: {error}");
            _assets = null;
            _renderer = null;
            _hud = null;
            _stage = null;
        }
    }

    private int _songClockMarked;

    // First frame with a running song clock (the notes move), and the song start (the audio plays).
    private void MarkSongClock(double songTime)
    {
        if (_songClockMarked == 0 && double.IsFinite(_lastSongTime) && songTime > _lastSongTime)
        {
            _songClockMarked = 1;
            LoadTiming.Mark($"song clock runs at song time {songTime:0.00} s");
        }
        if (_songClockMarked == 1 && songTime >= 0)
        {
            _songClockMarked = 2;
            LoadTiming.Mark("song time 0");
            _reloadAt = LoadTiming.Reloads > 0 ? Time.realtimeSinceStartup + 4 : 0;
        }
        if (_reloadAt > 0 && Time.realtimeSinceStartup >= _reloadAt)
        {
            // GH3_SKIN_TIMING_RELOADS: load the gameplay scene again (a song change without the menus) to measure the
            // stage load of the next song and the memory after each song.
            _reloadAt = 0;
            LoadTiming.Reloads--;
            LoadTiming.Mark("gameplay scene reloads");
            UnityEngine.SceneManagement.SceneManager.LoadScene("Gameplay");
        }
    }

    private float _reloadAt;

    public override void OnApplicationQuit() => CloseStage();

    public override void OnDeinitializeMelon() => CloseStage();

    private void CloseStage()
    {
        _gameplay = false;
        _stage?.Reset();
        _stage = null;
    }
}
