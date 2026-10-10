using System.Numerics;

namespace Gh3Skin;

// One Tesla arc. The defaults are GH3's for omitted parameters (PARITY-NOTES.md#particles-and-tesla).
internal sealed record StageTeslaEffect(uint Id, double Start, double End)
{
    public int Slot { get; init; }
    public float Remaining { get; init; } = -1;
    public Vector3 From { get; init; } = Vector3.UnitX;
    public Vector3 To { get; init; }
    public Vector3 Color { get; init; } = Vector3.One;
    public float Width { get; init; } = 1;
    public float Speed { get; init; } = .5f;
    public float Scale { get; init; } = 1;
    public float LightWidth { get; init; } = .3f; // GH3: default
    public float FadeOut { get; init; } = .8f; // GH3: default
    public float Amplitude { get; init; } = .5f;
    public float FalloffAlpha { get; init; } = .3f; // GH3: default
    public uint Blend { get; init; } = Gh3Checksum.Value("add");
}

internal sealed class StageTeslaState
{
    public const int Capacity = 30;
    private readonly StageTeslaEffect?[] _effects = new StageTeslaEffect?[Capacity];
    private readonly Gh3ScriptRuntime _runtime;
    private readonly Action<string> _gap;
    private bool _scheduled;
    public IReadOnlyList<StageTeslaEffect?> Effects => _effects;

    public StageTeslaState(Gh3ScriptRuntime runtime, Action<string> gap)
    {
        _runtime = runtime;
        _gap = gap;
    }

    public bool Exists(uint id) => _effects.Any(effect => effect?.Id == id);

    public Gh3ScriptCallResult? Execute(Gh3ScriptContext context, uint command, Gh3QbValue parameters)
    {
        bool Is(string name) => command == Gh3Checksum.Value(name);
        if (!Is("CreateTeslaEffectObject") && !Is("SetTeslaEffectObject") && !Is("DeleteTeslaEffectObject")) return null;
        var id = parameters.Field("Name")?.Integer ?? 0;
        var create = Is("CreateTeslaEffectObject");
        var slot = Array.FindIndex(_effects, effect => effect?.Id == id || create && effect == null);
        if (slot < 0) return new(false);
        if (Is("DeleteTeslaEffectObject"))
        {
            _effects[slot] = null;
            return new(true);
        }
        var effect = _effects[slot] ?? new(id, _runtime.Time, double.PositiveInfinity) { Slot = slot };
        float Number(string field, float fallback) => (float)StageParticleDefinition.Number(parameters, field, fallback, -StageLimits.MaximumMagnitude, StageLimits.MaximumMagnitude);
        effect = effect with {
            From = StageParticleDefinition.Vector(parameters, "start", effect.From), To = StageParticleDefinition.Vector(parameters, "end", effect.To),
            Color = StageParticleDefinition.Vector(parameters, "Color", effect.Color, false), Width = Number("width", effect.Width),
            Speed = Number("speed", effect.Speed), Scale = Number("scale", effect.Scale), LightWidth = Number("lightWidth", effect.LightWidth),
            FadeOut = Number("fadeOut", effect.FadeOut), Amplitude = Number("amplitude", effect.Amplitude), FalloffAlpha = Number("falloffAlpha", effect.FalloffAlpha),
            Blend = parameters.Field("blendMode")?.Integer ?? effect.Blend
        };
        if (parameters.Field("time") is { } duration)
        {
            var seconds = (float)Gh3ScriptRuntime.Numeric(duration);
            if (!float.IsFinite(seconds) || Math.Abs(seconds) > StageLimits.MaximumSeconds) throw new InvalidDataException("Invalid Tesla lifetime");
            effect = effect with { Remaining = seconds, End = seconds <= 0 ? double.PositiveInfinity : _runtime.Time + seconds };
        }
        _effects[slot] = effect;
        Schedule();
        _gap("Tesla shared render scheduling, texture sampling, and whole-stage composition still need original-game comparison");
        return new(true);
    }

    private void Schedule()
    {
        if (_scheduled || !_effects.Any(effect => effect?.Remaining > 0)) return;
        _scheduled = true;
        _runtime.Schedule(_runtime.Time + _runtime.FrameDuration, () => {
            _scheduled = false;
            Update((float)_runtime.FrameDuration);
            Schedule();
        });
    }

    public void Update(float elapsed)
    {
        for (var index = 0; index < _effects.Length; index++)
            if (_effects[index] is { Remaining: > 0 } effect)
            {
                var remaining = effect.Remaining - elapsed;
                _effects[index] = remaining < 0 ? null : effect with { Remaining = remaining };
            }
    }

    public IEnumerable<StageTeslaEffect> Sample(double time) => _effects.OfType<StageTeslaEffect>().Where(effect => time >= effect.Start);
}
