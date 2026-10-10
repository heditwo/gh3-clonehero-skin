using System.Numerics;
using System.Text.Json;

namespace Gh3Skin;

internal sealed class HandFireDefinition
{
    public int Version { get; set; }
    public string[] Bones { get; set; } = Array.Empty<string>();
    public HandFireEmitter[] Emitters { get; set; } = Array.Empty<HandFireEmitter>();

    public static HandFireDefinition? Load(string path)
    {
        if (!File.Exists(path)) return null;
        var value = JsonSerializer.Deserialize<HandFireDefinition>(File.ReadAllText(path), Packages.PackageJson.CaseInsensitive);
        if (value?.Version != 1 || value.Bones.Length != 2 || value.Emitters.Length != 2)
            throw new InvalidDataException("Invalid hand-fire definition");
        foreach (var emitter in value.Emitters) emitter.Validate();
        return value;
    }
}

internal sealed class HandFireEmitter
{
    public string Texture { get; set; } = "";
    public float Rate { get; set; }
    public float[] Life { get; set; } = Array.Empty<float>();
    public float[] Speed { get; set; } = Array.Empty<float>();
    public float[][] Size { get; set; } = Array.Empty<float[]>();
    public float Spread { get; set; }
    public float[] Range { get; set; } = Array.Empty<float>();
    public float[] Force { get; set; } = Array.Empty<float>();
    public float[] Direction { get; set; } = Array.Empty<float>();
    public float Follow { get; set; }
    public float[] Color { get; set; } = Array.Empty<float>();
    public float[][] Colors { get; set; } = Array.Empty<float[]>();
    public int Atlas { get; set; }
    public int Fps { get; set; }

    public void Validate()
    {
        static bool Array(float[] values, int size) => values.Length == size && values.All(float.IsFinite);
        if (Path.GetFileName(Texture) != Texture || !Texture.EndsWith(".png", StringComparison.Ordinal) ||
            !float.IsFinite(Rate) || Rate is <= 0 or > 64 || !Array(Life, 2) || Life[0] <= 0 || Life[1] < Life[0] || Life[1] > 1 ||
            !Array(Speed, 2) || Speed[0] < 0 || Speed[1] < Speed[0] ||
            Size.Length != 2 || Size.Any(s => !Array(s, 2) || s.Any(v => v < 0)) ||
            !float.IsFinite(Spread) || Spread is < 0 or > 90 || !Array(Range, 3) || !Array(Force, 3) ||
            !Direction.SequenceEqual(new[] { 0f, 1f, 0f }) || Follow != 0 || !Array(Color, 4) ||
            Colors.Length != 4 || Colors.Any(c => !Array(c, 4)) || Atlas is not (1 or 2) || Fps is < 0 or > 60)
            throw new InvalidDataException("Invalid hand-fire emitter");
    }
}

internal readonly record struct HandFireParticle(int Emitter, int Hand, double Birth, float Life, Vector3 Origin, Vector3 Velocity);
internal readonly record struct HandFireSample(Vector3 Position, Vector2 Size, Vector4 Color, int Frame);

internal sealed class StageHandFireState
{
    public const int StreakThreshold = 100;
    private readonly HandFireEmitter[] _emitters;
    private readonly double[] _starts;
    private readonly int[] _emitted;
    private double _time = double.NaN;
    private BandPose _left;
    private BandPose _right;
    public bool Active { get; private set; }
    public List<HandFireParticle> Particles { get; } = new(260);

    public StageHandFireState(HandFireDefinition definition)
    {
        _emitters = definition.Emitters;
        _starts = new double[_emitters.Length];
        _emitted = new int[_emitters.Length];
    }

    public void Update(double time, int combo, BandPose left, BandPose right)
    {
        if (!double.IsFinite(time)) { Reset(); return; }
        if (!double.IsFinite(_time) || time < _time || time - _time > 0.25)
        {
            Reset();
            _time = time;
            _left = left;
            _right = right;
        }
        for (var i = Particles.Count - 1; i >= 0; i--)
            if (time - Particles[i].Birth >= Particles[i].Life) Particles.RemoveAt(i);
        var active = combo >= StreakThreshold && time >= 0;
        if (active && !Active)
            for (var i = 0; i < _emitters.Length; i++) { _starts[i] = time; _emitted[i] = 0; }
        Active = active;
        if (active)
            for (var emitter = 0; emitter < _emitters.Length; emitter++)
            {
                var definition = _emitters[emitter];
                while (_starts[emitter] + _emitted[emitter] / (double)definition.Rate <= time + 1e-9)
                {
                    var index = _emitted[emitter]++;
                    var birth = _starts[emitter] + index / (double)definition.Rate;
                    var mix = time > _time ? (float)Math.Clamp((birth - _time) / (time - _time), 0, 1) : 1;
                    Spawn(emitter, 0, index, birth, BandPose.Mix(_left, left, mix));
                    Spawn(emitter, 1, index, birth, BandPose.Mix(_right, right, mix));
                }
            }
        _time = time;
        _left = left;
        _right = right;
    }

    public HandFireSample Sample(HandFireParticle particle, double time)
    {
        var emitter = _emitters[particle.Emitter];
        var age = (float)Math.Clamp(time - particle.Birth, 0, particle.Life);
        var progress = age / particle.Life;
        var colorTime = progress * (emitter.Colors.Length - 1);
        var colorIndex = Math.Min((int)colorTime, emitter.Colors.Length - 2);
        var color = Vector4.Lerp(Color(emitter.Colors[colorIndex]), Color(emitter.Colors[colorIndex + 1]), colorTime - colorIndex) * Color(emitter.Color);
        var size = Vector2.Lerp(new(emitter.Size[0][0], emitter.Size[0][1]), new(emitter.Size[1][0], emitter.Size[1][1]), progress);
        return new(particle.Origin + particle.Velocity * age + Vector(emitter.Force) * (age * age * 0.5f), size, color,
            (int)(age * emitter.Fps) % (emitter.Atlas * emitter.Atlas));
    }

    public void Reset()
    {
        Particles.Clear();
        Active = false;
        _time = double.NaN;
    }

    private void Spawn(int emitterIndex, int hand, int index, double birth, BandPose anchor)
    {
        var emitter = _emitters[emitterIndex];
        var seed = index * 101 + hand * 7919 + emitterIndex * 997;
        var angle = Random(seed) * MathF.Tau;
        var cosine = 1 - Random(seed + 1) * (1 - MathF.Cos(emitter.Spread * MathF.PI / 180));
        var sine = MathF.Sqrt(1 - cosine * cosine);
        var direction = new Vector3(MathF.Cos(angle) * sine, cosine, MathF.Sin(angle) * sine);
        if (emitterIndex == 1 && hand == 1) direction.Y = -direction.Y;
        var speed = emitter.Speed[0] + Random(seed + 2) * (emitter.Speed[1] - emitter.Speed[0]);
        var life = emitter.Life[0] + Random(seed + 3) * (emitter.Life[1] - emitter.Life[0]);
        var offset = Vector(emitter.Range) * new Vector3(Random(seed + 4) - 0.5f, Random(seed + 5) - 0.5f, Random(seed + 6) - 0.5f);
        Particles.Add(new(emitterIndex, hand, birth, life, anchor.Position + Vector3.Transform(offset, anchor.Rotation),
            Vector3.Transform(direction * speed, anchor.Rotation)));
    }

    private static Vector3 Vector(float[] v) => new(v[0], v[1], v[2]);
    private static Vector4 Color(float[] v) => new(v[0], v[1], v[2], v[3]);
    private static float Random(int seed)
    {
        var value = unchecked((uint)seed * 747796405u + 2891336453u);
        value = ((value >> (int)((value >> 28) + 4)) ^ value) * 277803737u;
        return ((value >> 22) ^ value) / (float)uint.MaxValue;
    }
}
