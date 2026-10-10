using UnityEngine;

namespace Gh3Skin;

// The rock meter: the gauge, the Star Power bulbs, and the meter pulse.

internal sealed class RockMeterHud
{
    // Tuned: placement of the bulbs and the gauge in the meter, from GH3 frames.
    private const float BulbsX = 38, BulbsY = 45.6f;
    private const float GaugeX = 4, GaugeY = 0.6f;
    // Tuned: the measured pulse offsets (StarPowerBulbAnimation) are 720p pixels; this canvas has 1.2 per pixel.
    private const float PoseScale = 1.2f;
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
        HudVisual.Position(_bulbs.Root, new Vector2(BulbsX, BulbsY));
        _gauge = new RockGaugeHud(_motion, tuning, textures);
        HudVisual.Position(_gauge.Root, new Vector2(GaugeX + tuning.RockOffsetX, GaugeY + tuning.RockOffsetY));
    }

    public void Update(GameplayState state, float time)
    {
        _bulbs.Update(state.StarPower, state.StarPowerActive, time);
        _gauge.Update(state.Rock);
        var pose = _bulbs.Pose;
        if (_pose == pose) return;
        _pose = pose;
        HudVisual.Position(_motion, new Vector2(pose.X, pose.Y) * PoseScale);
        _motion.localScale = new Vector3(pose.Scale, pose.Scale, 1);
        _motion.localEulerAngles = new Vector3(0, 0, -pose.Angle);
    }
}
