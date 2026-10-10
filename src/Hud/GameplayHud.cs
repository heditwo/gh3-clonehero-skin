using UnityEngine;

namespace Gh3Skin;

// Builds the GH3 HUD tree (highway, rock meter, score panel, messages, hit effects) and updates it each frame.

internal sealed class GameplayHud
{
    // Above Clone Hero's own canvases.
    private const int CanvasSortingOrder = 500;
    private readonly SkinAssets _assets;
    private readonly HudTuning _tuning;
    private readonly bool _hitEffects;
    private Canvas? _canvas;
    private RectTransform? _screenRoot;
    private HighwayHud? _highway;
    private HitEffectsHud? _hits;
    private RockMeterHud? _rock;
    private ScorePanelHud? _score;
    private MessageHud? _message;
    private RectTransform? _overlayRoot;
    private YouRockHud? _youRock;
    private HudViewport? _screenViewport;
    private HudViewport? _highwayViewport;
    private float _highwayRise = float.NaN;
    private static readonly Vector2 RockPosition = new(1326, 752.4f);
    private static readonly Vector2 ScorePosition = new(322, 714);

    public GameplayHud(SkinAssets assets, HudTuning tuning, bool hitEffects)
    {
        _assets = assets;
        _tuning = tuning;
        _hitEffects = hitEffects;
    }

    public void Reset()
    {
        if (_canvas != null) UnityEngine.Object.Destroy(_canvas.gameObject);
        _canvas = null;
        _screenRoot = null;
        _highway = null;
        _hits = null;
        _rock = null;
        _score = null;
        _message = null;
        _overlayRoot = null;
        _youRock = null;
        _screenViewport = null;
        _highwayViewport = null;
        _highwayRise = float.NaN;
    }

    public void EnsureCreated()
    {
        if (_canvas != null) return;
        _canvas = new GameObject("GH3 Gameplay HUD").AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.overrideSorting = true;
        _canvas.sortingOrder = CanvasSortingOrder;
        _highway = new HighwayHud(_canvas.transform, _assets);
        if (_hitEffects) _hits = new HitEffectsHud(_highway.Root, _assets);
        _screenRoot = HudVisual.Group("Screen HUD", _canvas.transform);
        _rock = new RockMeterHud(_screenRoot, _tuning, _assets.Textures);
        HudVisual.Position(_rock.Root, RockPosition);
        _score = new ScorePanelHud(_screenRoot, _tuning, _assets.Textures, _assets.AmpMaterial,
            new HudFont(_assets.Fonts, "score-font"), new HudFont(_assets.Fonts, "counter-font"));
        HudVisual.Position(_score.Root, ScorePosition);
        if (File.Exists(Path.Combine(_assets.Fonts, "message-font.png"))) _message = new MessageHud(_screenRoot, _assets.Fonts);
        _overlayRoot = HudVisual.Group("Screen overlay", _canvas.transform);
        if (File.Exists(Path.Combine(_assets.Fonts, "yourock-font.png"))) _youRock = new YouRockHud(_overlayRoot, _assets.Fonts, _assets.YouRockFlames);
    }

    // With intro set, the highway and HUD enter as in GH3's intro transition. winTime is the song time of GH3's
    // song_won event; play_outro removes the HUD then, and the highway follows.
    public void Update(GameplayState state, Camera camera, double songTime, bool intro, double winTime)
    {
        var screen = HudViewport.For(Screen.width, Screen.height);
        if (_screenViewport != screen)
        {
            HudVisual.Viewport(_screenRoot!, screen, screen.BottomOffsetY);
            HudVisual.Viewport(_overlayRoot!, screen, screen.CenterOffsetY);
            _score!.Layout(screen.Scale);
            _screenViewport = screen;
        }
        var rect = camera.pixelRect;
        var highway = HudViewport.ForHighway(Screen.height, rect.x, rect.y, rect.width, rect.height, camera.fieldOfView);
        var rise = intro ? IntroHudAnimation.HighwayOffset(songTime) * Screen.height / HudCanvas.Gh3Height : 0;
        if (_highwayViewport != highway || rise != _highwayRise)
        {
            HudVisual.Viewport(_highway!.Root, highway, highway.CenterOffsetY + rise);
            _highwayViewport = highway;
            _highwayRise = rise;
        }
        if (intro && songTime < 1)
        {
            // GH3 career HUD units are 1.05 canvas units (HudVisual.Gh3Scale).
            var (rock, rotation, score) = IntroHudAnimation.HudOffsets(songTime);
            HudVisual.Position(_rock!.Root, RockPosition + new Vector2(rock.X, rock.Y) * HudVisual.Gh3Scale);
            _rock.Root.localEulerAngles = new Vector3(0, 0, -rotation);
            HudVisual.Position(_score!.Root, ScorePosition + new Vector2(score.X, score.Y) * HudVisual.Gh3Scale);
        }
        var won = songTime >= winTime;
        SetActive(_screenRoot!, !won);
        SetActive(_highway!.Root, songTime < winTime + StageShowFlow.HighwayHideDelay);
        _youRock?.Update(songTime - winTime);
        if (won) return;
        var time = Time.time;
        _highway!.Update(state, time);
        _rock!.Update(state, time);
        _score!.Update(state, time);
        _message?.Update(state, time);
    }

    public void UpdateHitEffects(Il2Cpp.BasePlayer player, GameplayState state, double songTime) =>
        _hits?.Update(player, state, songTime);

    private static void SetActive(Component component, bool active)
    {
        if (component.gameObject.activeSelf != active) component.gameObject.SetActive(active);
    }
}
