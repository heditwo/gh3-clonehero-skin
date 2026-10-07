using System.Numerics;

namespace Gh3Skin;

internal sealed class StageParticleDefinition
{
    public Gh3QbValue Source { get; }
    public StageParticleMaterial Material { get; }
    public uint Texture { get; }
    public uint Blend { get; }
    public int AtlasColumns { get; }
    public int AtlasRows { get; }
    public double Fps { get; }
    public Vector2 Life { get; }
    public Vector2 Speed { get; }
    public Vector2 Width { get; }
    public Vector2 Height { get; }
    public Vector2 Depth { get; }
    public float SizeScale { get; }
    public Vector2 RotationSpeed { get; }
    public float RotationTimeScale { get; }
    public Vector3 Range { get; }
    public Vector3 Force { get; }
    public float WindCoefficient { get; }
    public float LocalWindCoefficient { get; }
    public bool PerParticleWind { get; }
    public bool Bounce { get; }
    public float BounceHeight { get; }
    public float BounceCoefficient { get; }
    public uint BounceCallback { get; }
    public float BounceCallbackRate { get; }
    public Vector3 Position { get; }
    public Quaternion Rotation { get; }
    public double Rate { get; }
    public uint EmitFunction { get; }
    public float EmitPeriod { get; }
    public double Lifetime { get; }
    public double Delay { get; }
    public float TimeSeed { get; }
    public int EmitCount { get; }
    public int Maximum { get; }
    public float Follow { get; }
    public bool FillPath { get; }
    public float EmitterVelocityWeight { get; }
    public bool EmitterIndependent { get; }
    public bool AlignWithPath { get; }
    public bool Rotate3d { get; }
    public Vector3 AlignTo { get; }
    public float PathFollowTime { get; }
    public uint Type { get; }
    public uint LightGroup { get; }
    public int History { get; }
    public uint UpdateScript { get; }
    public Gh3QbValue? ScriptParameters { get; }
    public Vector4 Color { get; }
    public Vector4[] Colors { get; }
    public float[] ColorTimes { get; }

    public StageParticleDefinition(Gh3QbValue source, uint defaultLightGroup = 0xe3714dc1)
    {
        Source = source;
        Material = new(source.Field("QuickMaterial"));
        Texture = Material.Texture;
        Blend = Material.Blend;
        AtlasColumns = Material.AtlasColumns;
        AtlasRows = Material.AtlasRows;
        Fps = Material.Fps;
        Life = Pair(source, "LifeRange", Vector2.Zero, 0, 600);
        Speed = Pair(source, "SpeedRange", Vector2.Zero, -10000, 10000);
        RotationSpeed = Pair(source, "RotVel", Vector2.Zero, -10000, 10000);
        RotationTimeScale = (float)Number(source, "RotVelTimeScale", 1, -100000, 100000);
        var size = source.Field("SizeRange")?.Items;
        var start = Size(size?.ElementAtOrDefault(0));
        var end = Size(size?.ElementAtOrDefault(1));
        Width = new(start.X, end.X);
        Height = new(start.Y, end.Y);
        Depth = Pair(source, "DepthRange", Vector2.Zero, 0, 1000);
        SizeScale = (float)Number(source, "SizeScale", 1, -100000, 100000);
        Range = Vector(source, "EmitRangeDims", Vector3.Zero, false);
        Force = Vector(source, "Force", Vector3.Zero);
        WindCoefficient = (float)Number(source, "WindCoeff", 0, -100000, 100000);
        LocalWindCoefficient = (float)Number(source, "LocalWindCoeff", 0, -100000, 100000);
        PerParticleWind = source.Flag("PerParticleWind");
        Bounce = source.Field("BounceHeight") != null;
        BounceHeight = (float)Number(source, "BounceHeight", 0, -100000, 100000);
        BounceCoefficient = (float)Number(source, "BounceCoeff", .5, -100000, 100000);
        BounceCallback = source.Field("BounceCallback")?.Integer ?? 0;
        BounceCallbackRate = (float)Number(source, "BounceCallbackRate", 0, -100000, 100000);
        Position = Vector(source, "Pos", Vector3.Zero);
        var angles = Vector(source, "Angles", Vector3.Zero, false);
        Rotation = Quaternion.CreateFromYawPitchRoll(-angles.Y, -angles.X, angles.Z);
        Rate = Number(source, "Emit_Rate", 0, 0, 100000);
        EmitFunction = source.Field("EmitFunction")?.Integer ?? Gh3Checksum.Value("constant");
        EmitPeriod = (float)Number(source, "EmitPeriod", 1, .000001, 86400);
        Lifetime = Number(source, "Lifetime", 0, 0, 86400);
        Delay = Number(source, "EmitDelayStart", 0, 0, 86400);
        TimeSeed = (float)Number(source, "TimeSeed", 0, 0, 86400);
        EmitCount = (int)Number(source, "EmitNum", 0, 0, 1000000);
        Maximum = (int)Number(source, "Max", 1, 0, 100000);
        if (Maximum == 0) Maximum = EmitCount > 0 ? EmitCount : (int)((float)Rate * Math.Max(Life.X, Life.Y) + .5f);
        if (Maximum > 100000) throw new InvalidDataException("Too many particles in one emitter");
        Follow = 1;
        foreach (var field in source.Fields)
        {
            if (field.Name == Gh3Checksum.Value("FollowEmitter")) Follow = (float)Gh3ScriptRuntime.Numeric(field.Value);
            else if (field.Name == Gh3Checksum.Value("EmitterIndependent") || field.Name == 0 && field.Value.Type == 13 && field.Value.Integer == Gh3Checksum.Value("EmitterIndependent"))
            {
                var independent = field.Name == 0 ? 1 : Gh3ScriptRuntime.Numeric(field.Value);
                EmitterIndependent = independent > 0;
                if (independent is 0 or 1) Follow = independent == 1 ? 0 : 1;
            }
        }
        if (!float.IsFinite(Follow) || Math.Abs(Follow) > 100000) throw new InvalidDataException("Invalid particle emitter follow value");
        EmitterVelocityWeight = (float)Number(source, "EmitterVelocityWeight", 0, -100000, 100000);
        FillPath = source.Flag("FillPath");
        AlignWithPath = source.Flag("AlignWithPath");
        Rotate3d = source.Flag("Rotate3D");
        AlignTo = Vector(source, "align_to", Vector3.Zero, false);
        PathFollowTime = (float)Number(source, "PathFollowTime", 0, -100000, 100000);
        Type = source.Field("Type")?.Integer ?? Gh3Checksum.Value("flat");
        LightGroup = source.Field("LightGroup")?.Integer ?? defaultLightGroup;
        UpdateScript = source.Field("UpdateScript")?.Integer ?? 0;
        ScriptParameters = source.Field("params");
        History = (int)Number(source, "History", 1, 0, 1024);
        if ((long)Maximum * History > 2000000) throw new InvalidDataException("Too many particle history entries");
        Color = DecodeColor(source.Field("Color")?.Integer ?? uint.MaxValue);
        Colors = source.Field("ParticleColor")?.Items.Select(value => DecodeColor(value.Integer)).ToArray() ?? new[] { Vector4.One, Vector4.One };
        if (Colors.Length is < 2 or > 20 || Colors.Length % 2 != 0) throw new InvalidDataException("Invalid particle color ranges");
        ColorTimes = source.Field("VertexWeight")?.Items.Select(value => (float)Gh3ScriptRuntime.Numeric(value)).ToArray() ??
            Enumerable.Range(1, Colors.Length / 2 - 1).Select(index => index / (float)(Colors.Length / 2 - 1)).ToArray();
        if (ColorTimes.Length != Colors.Length / 2 - 1 || ColorTimes.Where((value, index) => !float.IsFinite(value) || value <= (index == 0 ? 0 : ColorTimes[index - 1]) || value > 1).Any())
            throw new InvalidDataException("Invalid particle color times");
    }

    public StageParticleShape CreateShape()
    {
        var shape = new StageParticleShape { Radiate = Source.Flag("Radiate"), Circular = Source.Flag("DoCircularEmit"), RandomAngle = Source.Flag("UseRandomAngle"),
            TargetPosition = Source.Field("TargetPosition") == null ? null : (float)Number(Source, "TargetPosition", 0, -100000, 100000) };
        shape.SetElevation(0, 1);
        shape.SetSweep(0, 1);
        foreach (var field in Source.Fields)
        {
            if (field.Name == Gh3Checksum.Value("AngleSpread")) shape.SourceSpread((float)Gh3ScriptRuntime.Numeric(field.Value));
            else if (field.Name == Gh3Checksum.Value("ElevSpread") || field.Name == Gh3Checksum.Value("SweepSpread"))
            {
                var pair = Pair(field.Value, Vector2.Zero, -100000, 100000) * .0055555556900799274f - Vector2.One;
                if (field.Name == Gh3Checksum.Value("ElevSpread")) shape.SetElevation(pair.X, pair.Y);
                else shape.SetSweep(pair.X, pair.Y);
            }
        }
        shape.SetTarget(Vector(Source, "Emit_Target", Vector3.UnitY, false));
        shape.SetAngle(Vector(Source, "EmitAngle", Vector3.Zero, false));
        return shape;
    }

    public Vector4 ColorAt(float phase, float random)
    {
        var segment = 0;
        while (segment < ColorTimes.Length - 1 && phase > ColorTimes[segment]) segment++;
        var previousTime = segment == 0 ? 0 : ColorTimes[segment - 1];
        var blend = ColorTimes.Length == 0 ? 0 : Math.Clamp((phase - previousTime) / (ColorTimes[segment] - previousTime), 0, 1);
        return ColorAtSegment(segment, blend, random);
    }

    public Vector4 ColorAtSegment(int segment, float blend, float random)
    {
        var start = Color * 255 * Colors[segment * 2];
        var end = Color * 255 * Colors[segment * 2 + 1];
        var color = start + (end - start) * random;
        if (ColorTimes.Length > 0)
        {
            var nextStart = Color * 255 * Colors[(segment + 1) * 2];
            var nextEnd = Color * 255 * Colors[(segment + 1) * 2 + 1];
            color += ((nextStart - start) + ((nextEnd - end) - (nextStart - start)) * random) * blend;
        }
        float Channel(float value) => (byte)Math.Clamp((int)value, 0, 255) / 255f;
        return new(Channel(color.X), Channel(color.Y), Channel(color.Z), Channel(color.W));
    }

    public static double Number(Gh3QbValue value, string field, double fallback, double min, double max)
    {
        var number = value.Field(field) is { } v ? Gh3ScriptRuntime.Numeric(v) : fallback;
        if (!double.IsFinite(number) || number < min || number > max) throw new InvalidDataException("Invalid particle " + field);
        return number;
    }

    public static Vector3 Vector(Gh3QbValue value, string field, Vector3 fallback, bool reflect = true)
    {
        if (value.Field(field) is not { } item) return fallback;
        if (item.Data is not float[] values || values.Length != 3 || values.Any(v => !float.IsFinite(v) || Math.Abs(v) > 100000))
            throw new InvalidDataException("Invalid particle vector " + field);
        return new(values[0], values[1], reflect ? -values[2] : values[2]);
    }

    private static Vector2 Pair(Gh3QbValue value, string field, Vector2 fallback, float min, float max) => Pair(value.Field(field), fallback, min, max);
    private static Vector2 Size(Gh3QbValue? value) => value?.Type is 1 or 2 ? new((float)Gh3ScriptRuntime.Numeric(value)) : Pair(value, Vector2.Zero, 0, 1000);
    private static Vector2 Pair(Gh3QbValue? item, Vector2 fallback, float min, float max)
    {
        if (item == null) return fallback;
        if (item.Data is not float[] values || values.Length != 2 || values.Any(v => !float.IsFinite(v) || v < min || v > max))
            throw new InvalidDataException("Invalid particle range");
        return new(values[0], values[1]);
    }

    private static Vector4 DecodeColor(uint value) => new((value >> 24) / 255f, ((value >> 16) & 255) / 255f, ((value >> 8) & 255) / 255f, (value & 255) / 255f);
}

internal readonly record struct StageParticleColor(int Segment, float Phase, Vector4 Value)
{
    public StageParticleColor Step(StageParticleDefinition definition, float duration, float life, float random)
    {
        if (duration <= 0) return this;
        var segment = Segment;
        var phase = Phase;
        if (phase > 1)
        {
            if (segment < definition.ColorTimes.Length - 1) { segment++; phase = 0; }
            else phase = 1;
        }
        var color = definition.ColorAtSegment(segment, phase, random);
        if (definition.ColorTimes.Length > 0 && life > 0)
        {
            var length = definition.ColorTimes[segment] - (segment == 0 ? 0 : definition.ColorTimes[segment - 1]);
            phase += duration / (length * life);
        }
        return new(segment, phase, color);
    }
}
