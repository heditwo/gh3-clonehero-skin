using System.Numerics;

namespace Gh3Skin;

internal sealed record StageTeslaDraw(Vector3[] Positions, Vector3[] Coordinates, Vector4 Animation, Vector4 Shape, Vector4 Color);

internal static class StageTeslaRender
{
    public static StageTeslaDraw Build(StageTeslaEffect effect, Vector3 forward, uint milliseconds, bool tapered = true)
    {
        static Vector3 Reflect(Vector3 value) => new(value.X, value.Y, -value.Z);
        var from = Reflect(effect.From);
        var to = Reflect(effect.To);
        var delta = to - from;
        var direction = delta.LengthSquared() > 0 ? Vector3.Normalize(delta) : Vector3.Zero;
        var side = Vector3.Cross(Reflect(forward), direction) * (effect.Width * .5f);
        var positions = tapered ? new[] {
            from - side * .5f, from + side * .5f,
            from + delta * .2f - side, from + delta * .2f + side,
            from + delta * .8f - side, from + delta * .8f + side,
            to - side * .5f, to + side * .5f
        } : new[] { from - side, from + side, to + side, to - side };
        var coordinates = tapered ? new[] {
            new Vector3(0, .25f, 0), new Vector3(0, .75f, 0),
            new Vector3(.2f, 0, 1), new Vector3(.2f, 1, 1),
            new Vector3(.8f, 0, 1), new Vector3(.8f, 1, 1),
            new Vector3(1, .25f, 0), new Vector3(1, .75f, 0)
        } : new[] { new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0), new Vector3(1, 0, 0) };
        var phase = (float)(milliseconds * (double).001f) * effect.Speed;
        return new(positions.Select(Reflect).ToArray(), coordinates, new(phase * (1f / 11), phase - MathF.Truncate(phase), effect.Scale, 1),
            new(tapered ? (1 - effect.LightWidth) * .5f : effect.Amplitude, effect.LightWidth, effect.FadeOut, effect.FalloffAlpha), new(effect.Color, 1));
    }
}
