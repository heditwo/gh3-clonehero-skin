using UnityEngine;

namespace Gh3Skin;

internal sealed class GameplayHud
{
    private readonly SkinAssets _assets;
    private readonly HudTuning _tuning;
    private Canvas? _canvas;
    private RectTransform? _screenRoot;
    private HighwayHud? _highway;
    private RockMeterHud? _rock;
    private ScorePanelHud? _score;
    private HudViewport? _screenViewport;
    private HudViewport? _highwayViewport;

    public GameplayHud(SkinAssets assets, HudTuning tuning)
    {
        _assets = assets;
        _tuning = tuning;
    }

    public void Reset()
    {
        if (_canvas != null) UnityEngine.Object.Destroy(_canvas.gameObject);
        _canvas = null;
        _screenRoot = null;
        _highway = null;
        _rock = null;
        _score = null;
        _screenViewport = null;
        _highwayViewport = null;
    }

    public void EnsureCreated()
    {
        if (_canvas != null) return;
        _canvas = new GameObject("GH3 Gameplay HUD").AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.overrideSorting = true;
        _canvas.sortingOrder = 500;
        _highway = new HighwayHud(_canvas.transform, _assets);
        _screenRoot = HudVisual.Group("Screen HUD", _canvas.transform);
        _rock = new RockMeterHud(_screenRoot, _tuning, _assets.Textures);
        HudVisual.Position(_rock.Root, new Vector2(1326, 752.4f));
        _score = new ScorePanelHud(_screenRoot, _tuning, _assets.Textures);
        HudVisual.Position(_score.Root, new Vector2(322, 714));
    }

    public void Update(GameplayState state, Camera camera)
    {
        var screen = HudViewport.For(Screen.width, Screen.height);
        if (_screenViewport != screen)
        {
            HudVisual.Viewport(_screenRoot!, screen, screen.BottomOffsetY);
            _screenViewport = screen;
        }
        var rect = camera.pixelRect;
        var highway = HudViewport.ForHighway(Screen.height, rect.x, rect.y, rect.width, rect.height, camera.fieldOfView);
        if (_highwayViewport != highway)
        {
            HudVisual.Viewport(_highway!.Root, highway, highway.CenterOffsetY);
            _highwayViewport = highway;
        }
        var time = Time.time;
        _highway!.Update(state, time);
        _rock!.Update(state, time, Time.deltaTime);
        _score!.Update(state, time);
    }
}
