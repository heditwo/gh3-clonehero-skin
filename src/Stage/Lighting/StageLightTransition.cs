using System.Numerics;

namespace Gh3Skin;

internal readonly record struct StageLightChannels(Vector4 Color, float Intensity, float Specular, float Shadow)
{
    public static StageLightChannels Mix(StageLightChannels from, StageLightChannels to, float amount) => new(
        from.Color + (to.Color - from.Color) * amount, from.Intensity + (to.Intensity - from.Intensity) * amount,
        from.Specular + (to.Specular - from.Specular) * amount, from.Shadow + (to.Shadow - from.Shadow) * amount);
}

internal sealed class StageLightTransition
{
    private StageLightChannels _from;
    public StageLightChannels Value { get; private set; }
    public StageLightChannels Target { get; private set; }
    public int Motion { get; private set; } = 1;
    public uint Start { get; private set; }
    public uint Duration { get; private set; }
    public uint Elapsed { get; private set; }
    public bool Complete { get; private set; }

    public StageLightTransition(StageLightChannels value) => Value = Target = _from = value;

    public void Begin(Gh3QbValue parameters, uint time)
    {
        _from = Value;
        var target = Value;
        foreach (var field in parameters.Fields)
        {
            if (field.Value.Type is not (Gh3QbType.Integer or Gh3QbType.Float)) continue;
            var value = (float)Gh3ScriptRuntime.Numeric(field.Value);
            var color = target.Color;
            switch (field.Name)
            {
                case Gh3Keys.R: color.X = value * (1f / 255); target = target with { Color = color }; break;
                case Gh3Keys.G: color.Y = value * (1f / 255); target = target with { Color = color }; break;
                case Gh3Keys.B: color.Z = value * (1f / 255); target = target with { Color = color }; break;
                case Gh3Keys.Intensity: target = target with { Intensity = value }; break;
                case Gh3Keys.SpecularIntensity: target = target with { Specular = value }; break;
                case Gh3Keys.ShadowIntensity: target = target with { Shadow = value }; break;
            }
        }
        var motion = parameters.Field("motion")?.Integer switch { Gh3Keys.Linear => 0, Gh3Keys.Smooth => 1, Gh3Keys.EaseIn => 2, Gh3Keys.EaseOut => 3, _ => Motion };
        var duration = parameters.Field("time") is { Type: Gh3QbType.Integer or Gh3QbType.Float } seconds ? (float)Gh3ScriptRuntime.Numeric(seconds) : 0;
        Begin(target, duration, time, motion);
    }

    public void Begin(StageLightChannels target, float duration, uint time, int? motion = null)
    {
        _from = Value;
        Target = target;
        Motion = motion ?? Motion;
        Duration = unchecked((uint)(long)((double)duration * 1000));
        Start = time;
        Elapsed = 0;
        Complete = false;
        if (Duration == 0) Update(time);
    }

    public void Update(uint time)
    {
        Elapsed = unchecked(time - Start);
        var amount = Amount(Duration, Elapsed, Motion);
        Value = StageLightChannels.Mix(_from, Target, amount);
        Complete = Duration == 0 || Elapsed >= Duration;
    }

    public static float Amount(uint duration, uint elapsed, int motion)
    {
        var amount = duration == 0 || elapsed > duration ? 1 : (float)((double)elapsed / duration);
        // GH3: the DoMorph motion curves (smooth 3t² - 2t³, ease_in, ease_out).
        return motion switch { 1 => ((amount - 1.5f) * amount) * amount * -2, 2 => amount * amount, 3 => amount * 2 - amount * amount, _ => amount };
    }
}
