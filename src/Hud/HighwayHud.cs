using UnityEngine;
using UnityEngine.UI;

namespace Gh3Skin;

internal sealed class HighwayHud
{
    // Tuned: top of the active Star Power rail images on the 1080-line canvas.
    private const float ActiveHighwayTop = 456;
    private readonly RawImage _edges;
    private readonly RawImage _active;
    private readonly Texture2D _normalEdges;
    private readonly Texture2D _blueEdges;
    private readonly Texture2D[] _activeFrames;
    private readonly StarPowerHighwayAnimation _animation = new();
    private readonly ReadyBannerHud _ready;

    public RectTransform Root { get; }

    public HighwayHud(Transform parent, SkinAssets assets)
    {
        Root = HudVisual.Group("Highway effects", parent);
        _normalEdges = assets.Textures["highway-edges-normal"];
        _blueEdges = assets.Textures["highway-edges-blue"];
        _activeFrames = Enumerable.Range(0, StarPowerHighwayAnimation.FrameCount)
            .Select(frame => assets.Textures[$"active-highway-{frame:00}"]).ToArray();
        _edges = HudVisual.Image(Root, "highwayEdges", _normalEdges, new Rect(0, 0, HudCanvas.Width, HudCanvas.Height));
        _active = HudVisual.Image(Root, "activeHighway", _activeFrames[0], new Rect(0, ActiveHighwayTop, HudCanvas.Width, HudCanvas.Height - ActiveHighwayTop));
        _ready = new ReadyBannerHud(Root, assets);
    }

    public void Update(GameplayState state, float time)
    {
        _animation.Update(state.StarPowerActive, time);
        _active.enabled = _animation.Frame >= 0;
        _edges.enabled = !_active.enabled;
        _edges.texture = state.StarPowerActive || state.StarPower >= 0.5f ? _blueEdges : _normalEdges;
        if (_active.enabled) _active.texture = _activeFrames[_animation.Frame];
        _ready.Update(state.StarPower, state.StarPowerActive, time);
    }
}
