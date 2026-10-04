using MelonLoader;
using UnityEngine.SceneManagement;

[assembly: MelonInfo(typeof(Gh3Skin.SkinMod), "GH3 Gameplay Skin", "0.8.98", "hedi")]
[assembly: MelonGame("srylain Inc.", "Clone Hero")]

namespace Gh3Skin;

public sealed class SkinMod : MelonMod
{
    private SkinOptions? _options;
    private GameplaySpriteRenderer? _renderer;
    private GameplayHud? _hud;
    private PreviewCapture? _capture;
    private GameplayState _state = new(0, 0, 0.5f, 0);

    public override void OnInitializeMelon()
    {
        var assetsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Mods", "GH3Skin", "assets");
        _options = SkinOptions.FromEnvironment();
        var tuning = HudTuning.Load(Path.Combine(assetsDirectory, "..", "counter-tuning.json"));
        var assets = new SkinAssets(assetsDirectory);
        _renderer = new GameplaySpriteRenderer(assets, _options);
        _hud = new GameplayHud(assets, tuning);
        _capture = new PreviewCapture(_options);
        MelonLogger.Msg($"Loaded {assets.GameplaySpriteCount} GH3 skin gameplay sprites");
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        if (sceneName != "Gameplay" || _hud == null || _capture == null)
            return;
        _state = new GameplayState(0, 0, 0.5f, 0);
        _hud.Reset();
        _capture.Reset();
        MelonLogger.Msg("GH3 gameplay scene loaded");
    }

    public override void OnLateUpdate()
    {
        if (_renderer == null || _hud == null || _options == null || _capture == null ||
            SceneManager.GetActiveScene().name != "Gameplay")
            return;
        _hud.EnsureCreated();
        _renderer.Update();
        _state = GameplayStateReader.Read(_state, _options);
        _hud.Update(_state);
        _capture.Update();
    }
}
