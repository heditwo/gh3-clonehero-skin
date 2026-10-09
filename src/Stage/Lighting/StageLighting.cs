using System.Numerics;
using System.Text.Json;

namespace Gh3Skin;

internal sealed class StageLightDefinition
{
    public string Id { get; set; } = "";
    public string Object { get; set; } = "";
    public string Group { get; set; } = "";
    public string Type { get; set; } = "";
    public float[] Position { get; set; } = Array.Empty<float>();
    public float[] Rotation { get; set; } = Array.Empty<float>();
    public bool OverrideColor { get; set; }
    public float Hotspot { get; set; }
    public float Field { get; set; }
    public Vector3 Location => StageLightingBank.Vector(Position);
    public Quaternion Orientation => Quaternion.Normalize(new(Rotation[0], Rotation[1], Rotation[2], Rotation[3]));
    private Vector3? _forward;
    public Vector3 Forward => _forward ??= Vector3.Transform(Vector3.UnitZ, Orientation);
}

internal sealed class StageLightSetting
{
    public bool Inherit { get; set; }
    public float[] Color { get; set; } = Array.Empty<float>();
    public float Intensity { get; set; }
    public float Specular { get; set; }
    public float Near { get; set; }
    public float Far { get; set; }
}

internal sealed class StageBeamSetting
{
    public bool Inherit { get; set; }
    public float[] Position { get; set; } = Array.Empty<float>();
    public float[] Rotation { get; set; } = Array.Empty<float>();
    public float[] Color { get; set; } = Array.Empty<float>();
    public float Density { get; set; }
    public float StartRadius { get; set; }
    public float EndRadius { get; set; }
    public float Range { get; set; }
}

internal sealed class StageLightingSnapshot
{
    public StageLightSetting[] Lights { get; set; } = Array.Empty<StageLightSetting>();
    public Dictionary<string, StageBeamSetting> Beams { get; set; } = new();
}

internal sealed class StageLightingBank
{
    public int Version { get; set; }
    public StageLightDefinition[] Lights { get; set; } = Array.Empty<StageLightDefinition>();
    public Dictionary<string, StageLightingSnapshot> Snapshots { get; set; } = new();
    public Dictionary<string, string[]> Moods { get; set; } = new();
    public Dictionary<string, float[]> Colors { get; set; } = new();

    public static StageLightingBank? Load(string directory)
    {
        var path = Path.Combine(directory, "lighting.json");
        if (!File.Exists(path)) return null;
        var bank = JsonSerializer.Deserialize<StageLightingBank>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (bank?.Version != 1 || bank.Lights.Length is < 1 or > 128 || bank.Snapshots.Count is < 1 or > 256 ||
            !bank.Moods.ContainsKey("intro") || !bank.Moods.ContainsKey("blackout") ||
            bank.Moods.Values.Any(m => m.Length == 0 || m.Any(s => !bank.Snapshots.ContainsKey(s))))
            throw new InvalidDataException("Invalid stage lighting library");
        foreach (var light in bank.Lights)
            if (!ValidVector(light.Position) || !ValidRotation(light.Rotation) || light.Type is not ("Ambient" or "Dir" or "Point" or "Spot"))
                throw new InvalidDataException("Invalid stage light");
        var beamIds = bank.Snapshots.First().Value.Beams.Keys.OrderBy(k => k).ToArray();
        foreach (var snapshot in bank.Snapshots.Values)
        {
            if (snapshot.Lights.Length != bank.Lights.Length || snapshot.Beams.Count > 256 ||
                !snapshot.Beams.Keys.OrderBy(k => k).SequenceEqual(beamIds)) throw new InvalidDataException("Incomplete lighting snapshot");
            foreach (var light in snapshot.Lights)
                if (!ValidVector(light.Color) || !float.IsFinite(light.Intensity) || light.Intensity is < -100 or > 100 ||
                    !float.IsFinite(light.Near) || !float.IsFinite(light.Far) || light.Near < 0 || light.Far < light.Near)
                    throw new InvalidDataException("Invalid snapshot light");
            foreach (var beam in snapshot.Beams.Values)
                if (!ValidVector(beam.Position) || !ValidRotation(beam.Rotation) || !ValidVector(beam.Color) ||
                    !new[] { beam.Density, beam.StartRadius, beam.EndRadius, beam.Range }.All(v => float.IsFinite(v) && v >= 0))
                    throw new InvalidDataException("Invalid snapshot beam");
        }
        return bank;
    }

    private static bool ValidVector(float[] value) => value.Length == 3 && value.All(float.IsFinite);
    private static bool ValidRotation(float[] value) => value.Length == 4 && value.All(float.IsFinite) && value.Sum(v => v * v) > 0.9;
    public static Vector3 Vector(float[] value) => new(value[0], value[1], value[2]);
}

internal readonly record struct StageLightValue(Vector3 Color, float Intensity, float Specular, float SpecularPower, float Near, float Far)
{
    public Vector3 Energy => Color * Intensity;
    public StageLightValue(Vector3 energy, float near, float far) : this(energy, 1, 1, 1, near, far) { }
    public static StageLightValue Mix(StageLightValue a, StageLightValue b, float t) => new(
        a.Color + (b.Color - a.Color) * t, a.Intensity + (b.Intensity - a.Intensity) * t,
        a.Specular + (b.Specular - a.Specular) * t, a.SpecularPower + (b.SpecularPower - a.SpecularPower) * t,
        a.Near + (b.Near - a.Near) * t, a.Far + (b.Far - a.Far) * t);
}

internal readonly record struct StageLightPose(Vector3 Position, Vector3 Forward);

internal readonly struct StageLightSample
{
    public readonly Vector3 Energy;
    private readonly Vector3 _position;
    private readonly Vector3 _forward;
    private readonly float _far;
    private readonly float _rangeScale;
    private readonly float _coneOuter;
    private readonly float _coneScale;
    private readonly bool _ambient;
    private readonly bool _directional;
    private readonly bool _spot;

    public StageLightSample(StageLightDefinition light, StageLightValue value, StageLightPose? pose = null)
    {
        Energy = value.Energy;
        _position = pose?.Position ?? light.Location;
        _forward = pose?.Forward ?? light.Forward;
        _far = value.Far;
        _rangeScale = 1 / Math.Max(0.00001f, value.Far - value.Near);
        _ambient = light.Type == "Ambient";
        _directional = light.Type == "Dir";
        _spot = light.Type == "Spot";
        _coneOuter = _spot ? MathF.Cos(light.Field * MathF.PI / 180) : 0;
        _coneScale = _spot ? 1 / Math.Max(0.0001f, MathF.Cos(light.Hotspot * MathF.PI / 180) - _coneOuter) : 0;
    }

    public float Weight(Vector3 position, Vector3 normal)
    {
        if (_ambient) return 1;
        var delta = _position - position;
        var distance = delta.Length();
        var attenuation = Math.Clamp((_far - distance) * _rangeScale, 0, 1);
        var toward = _directional ? -_forward : delta / Math.Max(distance, 0.0001f);
        if (_spot) attenuation *= Math.Clamp((Vector3.Dot(-toward, _forward) - _coneOuter) * _coneScale, 0, 1);
        return Math.Max(0, Vector3.Dot(normal, toward)) * attenuation;
    }
}

internal readonly record struct StageBeamValue(Vector3 Position, Quaternion Rotation, Vector3 Color, float Density,
    float StartRadius, float EndRadius, float Range)
{
    public static StageBeamValue Mix(StageBeamValue a, StageBeamValue b, float t) => new(
        Vector3.Lerp(a.Position, b.Position, t), Quaternion.Slerp(a.Rotation, b.Rotation, t), Vector3.Lerp(a.Color, b.Color, t),
        a.Density + (b.Density - a.Density) * t, a.StartRadius + (b.StartRadius - a.StartRadius) * t,
        a.EndRadius + (b.EndRadius - a.EndRadius) * t, a.Range + (b.Range - a.Range) * t);
}

internal sealed record StageLightingValues(StageLightValue[] Lights, StageBeamValue[] Beams);
internal sealed record StageLightFade(double Time, double Blend, StageLightValue From, StageLightValue To)
{
    public StageLightValue Sample(double time) => StageLightValue.Mix(From, To, Blend <= 0 ? 1 : (float)Math.Clamp((time - Time) / Blend, 0, 1));
}
internal sealed record StageBeamFade(double Time, double Blend, StageBeamValue From, StageBeamValue To)
{
    public StageBeamValue Sample(double time) => StageBeamValue.Mix(From, To, Blend <= 0 ? 1 : (float)Math.Clamp((time - Time) / Blend, 0, 1));
}
internal sealed record StageLightingFrame(double Time, double Blend, StageLightingValues From, StageLightingValues To, string Snapshot,
    StageBeamMotion[] Motion)
{
    public StageLightFade[]? LightFades { get; init; }
    public StageBeamFade[]? BeamFades { get; init; }
    public float Amount(double time) => Blend <= 0 ? 1 : (float)Math.Clamp((time - Time) / Blend, 0, 1);
    public StageBeamValue SampleBeam(int index, double time)
    {
        var pose = Motion[index].Sample(time);
        var value = BeamFades == null ? StageBeamValue.Mix(From.Beams[index], To.Beams[index], Amount(time)) : BeamFades[index].Sample(time);
        return value with { Position = pose.Position, Rotation = pose.Rotation };
    }

    public StageLightingValues Sample(double time)
    {
        var t = Amount(time);
        return new(LightFades == null ? From.Lights.Zip(To.Lights, (a, b) => StageLightValue.Mix(a, b, t)).ToArray() : LightFades.Select(fade => fade.Sample(time)).ToArray(),
            Enumerable.Range(0, To.Beams.Length).Select(i => SampleBeam(i, time)).ToArray());
    }
}

internal sealed class StageLighting
{
    private readonly StageLightingFrame[] _frames;
    private readonly List<StageLightingFrame> _scriptFrames = new();
    private bool _scriptControl;
    public StageLightingBank Bank { get; }
    public string[] BeamIds { get; }
    public Action<string, double, double, StageLightValue[], bool[], Gh3QbValue>? SnapshotPlayed { get; set; }

    public StageLighting(StageLightingBank bank, StageTimeline timeline)
    {
        Bank = bank;
        BeamIds = bank.Snapshots.First().Value.Beams.Keys.OrderBy(k => k).ToArray();
        var indices = new Dictionary<string, int>();
        var mood = "intro";
        var snapshot = bank.Moods[mood][0];
        var previous = snapshot;
        string? color = null;
        var blend = Gh3CommandDispatch.DefaultLightBlend;
        var strobeOn = false;
        var initial = Values(snapshot, color);
        var motion = initial.Beams.Select(b => new StageBeamMotion(b)).ToArray();
        var frames = new List<StageLightingFrame> { new(double.NegativeInfinity, 0, initial, initial, snapshot, motion) };
        string NextSnapshot(string current)
        {
            var choices = bank.Moods[current];
            var index = indices.GetValueOrDefault(current);
            indices[current] = index + 1;
            return choices[index % choices.Length];
        }
        foreach (var original in timeline.PlaybackCues)
        {
            var cue = original.Type == "gh3-note" && int.TryParse(original.Value, out var note) ? StageTimeline.FromGh3Note(original.Time, note) : original;
            if (cue == null) continue;
            switch (cue.Type)
            {
                case "blend":
                    blend = double.Parse(cue.Value, System.Globalization.CultureInfo.InvariantCulture);
                    continue;
                case "lighting":
                    if (!bank.Moods.ContainsKey(cue.Value))
                    {
                        color = cue.Value;
                        mood = "intro";
                        strobeOn = false;
                        snapshot = NextSnapshot(mood);
                        previous = snapshot;
                        break;
                    }
                    mood = cue.Value;
                    strobeOn = false;
                    snapshot = NextSnapshot(mood);
                    if (mood is not ("blackout" or "flare" or "strobe")) previous = snapshot;
                    break;
                case "color": color = cue.Value == "off" ? null : cue.Value; break;
                case "snapshot":
                    if (mood is "blackout" or "flare" or "strobe") continue;
                    snapshot = NextSnapshot(mood);
                    previous = snapshot;
                    break;
                case "strobe-toggle":
                    if (mood != "strobe") continue;
                    strobeOn = !strobeOn;
                    snapshot = strobeOn ? previous : bank.Moods["blackout"][0];
                    break;
                default: continue;
            }
            var from = frames[^1].Sample(cue.Time);
            var target = Values(snapshot, color, mood == "strobe" && strobeOn, from);
            if (mood == "strobe" || original.Type == "gh3-note" && original.Value == "57")
                for (var i = 0; i < target.Beams.Length; i++)
                    target.Beams[i] = target.Beams[i] with { Position = from.Beams[i].Position, Rotation = from.Beams[i].Rotation };
            var duration = mood == "strobe" ? 0 : cue.Type == "color" ? 0.4 : blend;
            if (mood != "strobe" && cue.Type != "color" && !(original.Type == "gh3-note" && original.Value == "57"))
                motion = motion.Select((m, i) => m.Retarget(cue.Time, duration, target.Beams[i])).ToArray();
            frames.Add(new(cue.Time, duration, from, target, snapshot, motion));
        }
        _frames = frames.ToArray();
    }

    public StageLightingFrame At(double time) => _scriptControl ?
        _scriptFrames[Math.Max(0, StageTimeline.UpperBound(_scriptFrames, time, f => f.Time) - 1)] :
        _frames[Math.Max(0, StageTimeline.UpperBound(_frames, time, f => f.Time) - 1)];

    public void BeginScriptControl()
    {
        _scriptControl = true;
        _scriptFrames.Clear();
        _scriptFrames.Add(_frames[0]);
    }

    public void PlaySnapshot(string name, double time, double blend, string? color, Gh3QbValue parameters, bool positions)
    {
        if (!_scriptControl || !Bank.Snapshots.ContainsKey(name)) throw new InvalidDataException("Script lighting snapshot is missing: " + name);
        if (_scriptFrames.Count >= 100000) throw new InvalidDataException("Too many script lighting changes");
        var frame = At(time);
        var from = frame.Sample(time);
        var snapshot = Bank.Snapshots[name];
        var target = Values(name, color, previous: frame.To);
        if (parameters.Field("intensity") is { Type: 1 or 2 } intensity)
            for (var i = 0; i < target.Lights.Length; i++)
            {
                var setting = snapshot.Lights[i];
                if (setting.Inherit) continue;
                var rgb = color != null && Bank.Lights[i].OverrideColor ? StageLightingBank.Vector(Bank.Colors[color]) : StageLightingBank.Vector(setting.Color);
                target.Lights[i] = target.Lights[i] with { Color = rgb, Intensity = (float)Gh3ScriptRuntime.Numeric(intensity) };
            }
        if (parameters.Field("specularintensity") is { Type: 1 or 2 } specular)
            for (var i = 0; i < target.Lights.Length; i++)
                if (!snapshot.Lights[i].Inherit) target.Lights[i] = target.Lights[i] with { Specular = (float)Gh3ScriptRuntime.Numeric(specular) };
        var motion = positions ? frame.Motion.Select((item, i) => snapshot.Beams[BeamIds[i]].Inherit ? item : item.Retarget(time, blend, target.Beams[i])).ToArray() : frame.Motion;
        if (!positions)
            for (var i = 0; i < target.Beams.Length; i++)
                target.Beams[i] = target.Beams[i] with { Position = from.Beams[i].Position, Rotation = from.Beams[i].Rotation };
        _scriptFrames.Add(new(time, blend, from, target, name, motion) {
            LightFades = target.Lights.Select((value, i) => snapshot.Lights[i].Inherit ?
                frame.LightFades?[i] ?? new(frame.Time, frame.Blend, frame.From.Lights[i], frame.To.Lights[i]) : new(time, blend, from.Lights[i], value)).ToArray(),
            BeamFades = target.Beams.Select((value, i) => snapshot.Beams[BeamIds[i]].Inherit ?
                frame.BeamFades?[i] ?? new(frame.Time, frame.Blend, frame.From.Beams[i], frame.To.Beams[i]) : new(time, blend, from.Beams[i], value)).ToArray()
        });
        SnapshotPlayed?.Invoke(name, time, blend, target.Lights, snapshot.Lights.Select(light => !light.Inherit).ToArray(), parameters);
    }

    public void SetBeam(int index, double time, StageBeamValue value)
    {
        if (!_scriptControl || index < 0 || index >= BeamIds.Length) throw new InvalidDataException("Script light volume is missing");
        if (_scriptFrames.Count >= 100000) throw new InvalidDataException("Too many script lighting changes");
        var frame = At(time);
        var from = frame.Sample(time);
        var beams = frame.To.Beams.ToArray();
        beams[index] = value;
        _scriptFrames.Add(new(time, 0, from, new(frame.To.Lights, beams), frame.Snapshot, frame.Motion) {
            LightFades = frame.LightFades ?? frame.To.Lights.Select((light, i) => new StageLightFade(frame.Time, frame.Blend, frame.From.Lights[i], light)).ToArray(),
            BeamFades = beams.Select((beam, i) => i == index ? new StageBeamFade(time, 0, beam, beam) :
                frame.BeamFades?[i] ?? new(frame.Time, frame.Blend, frame.From.Beams[i], frame.To.Beams[i])).ToArray()
        });
    }

    private StageLightingValues Values(string name, string? color, bool fullIntensity = false, StageLightingValues? previous = null)
    {
        var snapshot = Bank.Snapshots[name];
        Vector3? tint = color == null ? null : Bank.Colors.TryGetValue(color, out var rgb) ? StageLightingBank.Vector(rgb) :
            new Vector3(StageColor.Named(color).R, StageColor.Named(color).G, StageColor.Named(color).B);
        return new(snapshot.Lights.Select((s, i) => s.Inherit && previous != null ? previous.Lights[i] : new StageLightValue(
                Bank.Lights[i].OverrideColor && tint.HasValue ? tint.Value : StageLightingBank.Vector(s.Color), fullIntensity ? 1 : s.Intensity, s.Specular, 1, s.Near, s.Far)).ToArray(),
            BeamIds.Select((id, i) => { var b = snapshot.Beams[id]; return b.Inherit && previous != null ? previous.Beams[i] : new StageBeamValue(StageLightingBank.Vector(b.Position),
                Quaternion.Normalize(new(b.Rotation[0], b.Rotation[1], b.Rotation[2], b.Rotation[3])), tint ?? StageLightingBank.Vector(b.Color),
                b.Density, b.StartRadius, b.EndRadius, b.Range); }).ToArray());
    }

    public static float Weight(StageLightDefinition light, StageLightValue value, Vector3 position, Vector3 normal, StageLightPose? pose = null)
    {
        if (light.Type == "Ambient") return 1;
        var delta = (pose?.Position ?? light.Location) - position;
        var distance = delta.Length();
        var attenuation = Math.Clamp((value.Far - distance) / Math.Max(0.00001f, value.Far - value.Near), 0, 1);
        var forward = pose?.Forward ?? light.Forward;
        var toward = light.Type == "Dir" ? -forward : delta / Math.Max(distance, 0.0001f);
        if (light.Type == "Spot")
        {
            var inner = MathF.Cos(light.Hotspot * MathF.PI / 180);
            var outer = MathF.Cos(light.Field * MathF.PI / 180);
            attenuation *= Math.Clamp((Vector3.Dot(-toward, forward) - outer) / Math.Max(0.0001f, inner - outer), 0, 1);
        }
        return Math.Max(0, Vector3.Dot(normal, toward)) * attenuation;
    }
}
