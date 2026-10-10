using UnityEngine;
using UnityEngine.UI;

namespace Gh3Skin;

// The six Star Power bulbs of the rock meter. Bulbs 4-6 slide out of the meter (StarPowerBulbAnimation).
internal sealed class StarPowerBulbsHud
{
    // GH3: the sliding bulbs move along 0, 21, and 42 degrees from vertical.
    private const int SlideAngleStep = 21;
    // Tuned: the hidden distance of a retracted bulb on this canvas (GH3: 170 HUD units).
    private const float HiddenDistance = 178.5f;
    // Tuned: bulb layouts (x, y, width, height) and tilts in degrees, from GH3 frames.
    private static readonly (Rect Layout, float Angle)[] Bulbs =
    {
        (new(0, 0, 48, 84), 40), (new(26, -22, 46, 84), 27), (new(58, -28, 50, 100), 14),
        (new(95, -31, 62, 130), -3), (new(139, -23, 62, 128), -20), (new(175, 0, 62, 130), -40)
    };
    private readonly StarPowerBulbAnimation _animation = new();
    private readonly RectTransform[] _bulbs = new RectTransform[6];
    private readonly RawImage[] _glass = new RawImage[6];
    private readonly RawImage[] _glow = new RawImage[6];
    private readonly Texture2D _normalGlow;
    private readonly Texture2D _blueGlow;
    private (float Power, bool Active)? _previous;

    public RectTransform Root { get; }
    public RockMeterPose Pose => _animation.Pose;

    public StarPowerBulbsHud(Transform parent, IReadOnlyDictionary<string, Texture2D> textures)
    {
        Root = HudVisual.Group("Star Power bulbs", parent);
        _normalGlow = textures["HUD_rock_tube_glow_full"];
        _blueGlow = textures["HUD_rock_tube_glow_full_b"];
        for (var bulb = 0; bulb < Bulbs.Length; bulb++)
        {
            var (layout, angle) = Bulbs[bulb];
            var root = _bulbs[bulb] = HudVisual.Group($"Star Power bulb {bulb}", Root);
            HudVisual.Position(root, layout.position);
            root.localEulerAngles = new Vector3(0, 0, angle);
            var face = new Rect(0, 0, layout.width, layout.height);
            _glass[bulb] = HudVisual.Image(root, $"bulb{bulb}", textures["HUD_rock_tube"], face, new Vector2(0.5f, 0));
            _glow[bulb] = HudVisual.Image(root, $"bulbGlow{bulb}", _normalGlow, face, new Vector2(0.5f, 0));
        }
    }

    public void Update(float power, bool active, float time)
    {
        var moving = _animation.Update(power, active, time);
        if (_previous == (power, active) && !moving) return;
        _previous = (power, active);
        for (var bulb = 0; bulb < Bulbs.Length; bulb++)
        {
            var extension = _animation.Extension(bulb);
            var fill = HudState.StarPowerBulbFill(power, bulb);
            _glass[bulb].enabled = extension > 0 && fill < 1;
            _glow[bulb].enabled = extension > 0 && fill > 0;
            _glow[bulb].color = new Color(1, 1, 1, fill);
            _glow[bulb].texture = active || power >= 0.5f ? _blueGlow : _normalGlow;
            if (bulb < 3) continue;
            var position = Bulbs[bulb].Layout.position;
            var angle = (bulb - 3) * SlideAngleStep * Mathf.Deg2Rad;
            var hiddenDistance = (1 - extension) * HiddenDistance;
            position += new Vector2(-Mathf.Sin(angle), Mathf.Cos(angle)) * hiddenDistance;
            HudVisual.Position(_bulbs[bulb], position);
        }
    }
}
