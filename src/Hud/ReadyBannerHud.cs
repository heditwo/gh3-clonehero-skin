using UnityEngine;
using UnityEngine.UI;

namespace Gh3Skin;

internal sealed class ReadyBannerHud
{
    private readonly ReadyAnimationTrigger _animation = new();
    private readonly IReadOnlyList<AnimationFrame> _frames;
    private readonly IReadOnlyList<AnimationFrame> _lightningFrames;
    private readonly RawImage _banner;
    private readonly RawImage _lightning;
    private int _frame = -2;

    public ReadyBannerHud(Transform parent, SkinAssets assets)
    {
        var root = HudVisual.Group("Ready effect", parent);
        _frames = assets.ReadyFrames;
        _lightningFrames = assets.ReadyLightningFrames;
        _banner = HudVisual.Image(root, "readyFrame", _frames[0].Texture, _frames[0].Layout);
        _lightning = HudVisual.Image(root, "readyLightning", _lightningFrames[0].Texture, _lightningFrames[0].Layout);
    }

    public void Update(float power, bool active, float time)
    {
        _animation.Update(power, time, active);
        var frame = _animation.Frame(time, _frames.Count);
        if (_frame == frame) return;
        _frame = frame;
        _banner.enabled = frame >= 0;
        _lightning.enabled = frame >= 0 && frame < _lightningFrames.Count;
        if (frame < 0) return;
        _banner.texture = _frames[frame].Texture;
        HudVisual.Layout(_banner, _frames[frame].Layout);
        if (!_lightning.enabled) return;
        _lightning.texture = _lightningFrames[frame].Texture;
        HudVisual.Layout(_lightning, _lightningFrames[frame].Layout);
    }
}
