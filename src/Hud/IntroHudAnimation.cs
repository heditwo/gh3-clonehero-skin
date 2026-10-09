using System.Numerics;

namespace Gh3Skin;

// GH3's highway and HUD entrance before the song (intro_sequence_props in guitar_intro.qb). Offsets are GH3 screen
// units (1280 x 720); rotations are degrees, clockwise on screen.
internal static class IntroHudAnimation
{
    // In the GH3 recording the highway follows move_highway_2d's curve, but it starts 0.82 s before
    // intro_sequence_props.highway_start_time and its clock runs at 0.64 of song time. The cause is not identified;
    // both values are a least-squares fit to nine board-top positions between -1.55 s and -0.65 s (residual about 5 units).
    private const double RiseStart = -2.82;
    private const double RiseRate = 0.64;
    public const double HudStart = -0.4;
    private const double HudMove = 0.2;
    private const float Frame = 1f / 60;
    private static readonly float[] HighwayCurve = BuildHighwayCurve();
    // guitar_hud_2d_career.qb: offscreen and resting positions of the rock meter and the score containers.
    private static readonly Vector2 RockOffscreen = new Vector2(2000, 610) - new Vector2(1260, 692);
    private static readonly Vector2 ScoreOffscreen = new Vector2(-500, 560) - new Vector2(300, 650);

    // move_highway_2d: the highway container starts 720 units down and rises with a logarithmic slow-down.
    public static float HighwayOffset(double time)
    {
        if (time < RiseStart) return 720;
        var frame = (int)((time - RiseStart) * RiseRate / Frame);
        return frame < HighwayCurve.Length ? HighwayCurve[frame] : 0;
    }

    private static float[] BuildHighwayCurve()
    {
        var values = new List<float>();
        var y = 720f;
        var elapsed = 0f;
        while (y > 0 && values.Count < 600)
        {
            values.Add(y);
            elapsed += Frame;
            var scaled = Math.Min(elapsed / 1.3f, 1);
            var speed = Math.Max(0.05f, MathF.Log(1.005f - scaled) * 0.25f + 1);
            y -= 720 * Frame * speed;
        }
        return values.ToArray();
    }

    // move_2d_elements_to_default and morph_2d_hud_elements: a 200 ms slide in from off screen, then a 50 unit
    // overshoot with a 5 degree tilt of the rock meter, a 25 unit swing back, and a settle.
    public static (Vector2 Rock, float RockRotation, Vector2 Score) HudOffsets(double time)
    {
        var age = (float)(time - HudStart);
        if (age <= 0) return (RockOffscreen, 0, ScoreOffscreen);
        if (age < HudMove)
        {
            var t = age / (float)HudMove;
            return (RockOffscreen * (1 - t), 0, ScoreOffscreen * (1 - t));
        }
        (float Time, float Swing, float Rotation)[] keys = { (0.2f, 0, 0), (0.3f, 50, -5), (0.425f, -25, 5), (0.525f, 0, 0) };
        for (var i = 1; i < keys.Length; i++)
        {
            if (age > keys[i].Time) continue;
            var t = (age - keys[i - 1].Time) / (keys[i].Time - keys[i - 1].Time);
            var swing = keys[i - 1].Swing + (keys[i].Swing - keys[i - 1].Swing) * t;
            var rotation = keys[i - 1].Rotation + (keys[i].Rotation - keys[i - 1].Rotation) * t;
            return (new(-swing, 0), rotation, new(swing, 0));
        }
        return (Vector2.Zero, 0, Vector2.Zero);
    }
}
