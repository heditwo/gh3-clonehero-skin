using UnityEngine;

namespace Gh3Skin;

internal sealed class RockMeterHud
{
    private readonly RectTransform _motion;
    private readonly StarPowerBulbsHud _bulbs;
    private readonly RockGaugeHud _gauge;
    private RockMeterPose? _pose;

    public RectTransform Root { get; }

    public RockMeterHud(Transform parent, HudTuning tuning, IReadOnlyDictionary<string, Texture2D> textures)
    {
        Root = HudVisual.Group("Rock meter", parent);
        _motion = HudVisual.Group("Rock meter animation", Root);
        _bulbs = new StarPowerBulbsHud(_motion, textures);
        HudVisual.Position(_bulbs.Root, new Vector2(38, 45.6f));
        _gauge = new RockGaugeHud(_motion, tuning, textures);
        HudVisual.Position(_gauge.Root, new Vector2(4 + tuning.RockOffsetX, 0.6f + tuning.RockOffsetY));
    }

    public void Update(GameplayState state, float time, float deltaTime)
    {
        _bulbs.Update(state.StarPower, state.StarPowerActive, time);
        _gauge.Update(state.Rock, deltaTime);
        var pose = _bulbs.Pose;
        if (_pose == pose) return;
        _pose = pose;
        HudVisual.Position(_motion, new Vector2(pose.X, pose.Y) * 1.2f);
        _motion.localScale = new Vector3(pose.Scale, pose.Scale, 1);
        _motion.localEulerAngles = new Vector3(0, 0, -pose.Angle);
    }
}
