using System.Numerics;

namespace Gh3Skin;

// GH3's wind fields that move the particles.

internal sealed record StageWindField
{
    public uint Id { get; init; } = Gh3Checksum.Value("localwind");
    public Vector3 Position { get; init; }
    public Vector3 Velocity { get; init; }
    public float Age { get; set; }
    public float Duration { get; init; } = 1;
    public float Speed { get; init; } = 1;
    public float Tangent { get; init; } = 1;
    public float Range { get; init; } = 1;
    public uint Type { get; init; } = Gh3Checksum.Value("direction");
    public uint Attenuation { get; init; } = Gh3Checksum.Value("none");

    public StageWindField Apply(Gh3QbValue parameters)
    {
        float Number(string name, float previous) => (float)StageParticleDefinition.Number(parameters, name, previous, -StageLimits.MaximumMagnitude, StageLimits.MaximumMagnitude);
        var field = this with {
            Position = StageParticleDefinition.Vector(parameters, parameters.Field("pos") != null ? "pos" : "position", Position, false),
            Velocity = StageParticleDefinition.Vector(parameters, parameters.Field("velocity") != null ? "velocity" : "vel", Velocity, false),
            Duration = Number("time", Duration), Speed = Number("speed", Speed), Tangent = Number("tangentweight", Tangent), Range = Number("range", Range),
            Type = parameters.Field("type")?.Integer ?? Type, Attenuation = parameters.Field("attenuation")?.Integer ?? Attenuation
        };
        return field.Type == Gh3Checksum.Value("direction") || field.Type == Gh3Checksum.Value("directional") ? field with { Speed = field.Velocity.Length() } : field;
    }

    public Vector3 Sample(Vector3 position)
    {
        var delta = Position - position;
        var distanceSquared = delta.Y * delta.Y + delta.Z * delta.Z + delta.X * delta.X;
        if (distanceSquared > Range * Range) return Vector3.Zero;
        var distance = MathF.Sqrt(distanceSquared);
        var attenuation = Attenuation == Gh3Checksum.Value("inv_distance") ? 1 - distance / Range : 1;
        if (Type == Gh3Checksum.Value("point")) return delta * (Speed / distance * attenuation);
        var clockwise = Type == Gh3Checksum.Value("twist_clockwise");
        if (!clockwise && Type != Gh3Checksum.Value("twist") && Type != Gh3Checksum.Value("twist_counterclockwise")) return Velocity * attenuation;
        delta.Y = 0;
        var length = delta.Length();
        if (length > 0) delta *= 1 / length;
        var direction = clockwise ? new Vector3(delta.X * (1 - Tangent) - delta.Z * Tangent, 0, delta.Z * (1 - Tangent) + delta.X * Tangent) :
            new Vector3(delta.X * (1 - Tangent) + delta.Z * Tangent, 0, delta.Z * (1 - Tangent) - delta.X * Tangent);
        length = direction.Length();
        if (length > 0) direction *= 1 / length;
        direction.Y = Velocity.Y;
        return direction * (Speed * attenuation);
    }
}

internal sealed class StageWindState
{
    private const int MaximumFields = 50;
    private readonly List<StageWindField> _fields = new();
    private readonly double _start;
    private readonly double _frameDuration;
    private int _frame;
    public IReadOnlyList<StageWindField> Fields => _fields;
    public Vector3 Target { get; private set; }
    public Vector3 Previous { get; private set; }
    public Vector3 Current { get; private set; }
    public float BlendTime { get; private set; } = -1;
    public float BlendDuration { get; set; } = 2;

    public StageWindState(double start = 0, double frameDuration = 1d / 60)
    {
        _start = start;
        _frameDuration = frameDuration;
    }

    public void Advance(double time)
    {
        var frame = (int)Math.Floor((time - _start) / _frameDuration + .000001);
        while (_frame < frame && (_fields.Count > 0 || BlendTime >= 0)) { Step((float)_frameDuration); _frame++; }
        _frame = frame;
    }

    public bool Execute(Gh3QbValue parameters)
    {
        if (parameters.Field("wind") != null)
        {
            Previous = Target;
            Target = StageParticleDefinition.Vector(parameters, "wind", Vector3.Zero, false);
            BlendTime = 0;
            return true;
        }
        if (parameters.Field("create") is { Type: Gh3QbType.Structure } create)
        {
            var field = new StageWindField { Id = create.Field("name")?.Integer ?? Gh3Checksum.Value("localwind") }.Apply(create);
            var index = _fields.FindIndex(value => value.Id == field.Id);
            if (index >= 0) _fields[index] = field;
            else if (_fields.Count <= MaximumFields) _fields.Add(field);
            return true;
        }
        if (parameters.Field("modify") is { Type: Gh3QbType.Structure } modify)
        {
            if (modify.Field("name") is { } name)
            {
                var index = _fields.FindIndex(value => value.Id == name.Integer);
                if (index >= 0) _fields[index] = _fields[index].Apply(modify);
            }
            return true;
        }
        if (parameters.Field("remove") is { } remove)
        {
            _fields.RemoveAll(field => field.Id == remove.Integer);
            return true;
        }
        if (parameters.Flag("removeall")) { _fields.Clear(); return true; }
        return parameters.Flag("print");
    }

    public void Step(float duration)
    {
        if (BlendTime >= 0)
        {
            if (BlendTime < BlendDuration)
            {
                Current = Previous + (Target - Previous) * (BlendTime / BlendDuration);
                BlendTime += duration;
            }
            else BlendTime = -1;
        }
        foreach (var field in _fields) field.Age += duration;
        _fields.RemoveAll(field => field.Duration < 0 || field.Duration > 0 && field.Age > field.Duration);
    }

    public StageWindField? Select(Vector3 position, float radius = 0)
    {
        foreach (var field in _fields)
            if (field.Speed > 0 && (radius == 0 ? Vector3.DistanceSquared(field.Position, position) < field.Range * field.Range :
                Vector3.Distance(field.Position, position) < field.Range + radius)) return field;
        return null;
    }

    public StageWindField? SelectSphere(Vector3 position, float radius) => _fields.FirstOrDefault(field =>
        field.Speed > 0 && Vector3.Distance(field.Position, position) < field.Range + radius);
}
