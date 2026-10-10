namespace Gh3Skin;

internal sealed record StageScriptActorDefinition(StageActorDefinition Actor, double[] Durations, BandAnimation? Animation = null, IReadOnlyDictionary<uint, int>? Bones = null);
internal readonly record struct StageScriptActorSample(int Clip, double Time, int PreviousClip, double PreviousTime, float Blend);

internal sealed class StageScriptActorState
{    private const int MaximumBlendSeconds = 600;

    private sealed record Playback(int Clip, double Start, double Offset, double End, double Rate, bool Loop);
    private readonly StageScriptActorDefinition _definition;
    private readonly Dictionary<uint, int> _clips;
    private Playback _current;
    private Playback _previous;
    private double _blend;
    public bool Active { get; private set; }
    public uint ClipKey { get; private set; }
    public double Start => _current.Start;
    public double Offset => _current.Offset;
    public double Rate => _current.Rate;
    public bool Loop => _current.Loop;
    public double Duration => _definition.Durations[_current.Clip];

    public StageScriptActorState(StageScriptActorDefinition definition)
    {
        if (definition.Durations.Length is < 1 or > 256 || definition.Durations.Any(value => !double.IsFinite(value) || value <= 0) ||
            definition.Actor.Clip < 0 || definition.Actor.Clip >= definition.Durations.Length ||
            definition.Actor.Clips.Values.Any(clip => clip < 0 || clip >= definition.Durations.Length))
            throw new InvalidDataException("Invalid scripted actor clips");
        _definition = definition;
        _clips = new();
        foreach (var (name, index) in definition.Actor.Clips)
        {
            var key = name.Length == 8 && name.All(Uri.IsHexDigit) ? Convert.ToUInt32(name, 16) : Gh3QbText.Key(name);
            if (!_clips.TryAdd(key, index) && _clips[key] != index) throw new InvalidDataException("Actor clip names have the same checksum");
        }
        _current = new(definition.Actor.Clip, 0, 0, definition.Actor.Playback == "hold" ? 0 : definition.Durations[definition.Actor.Clip],
            definition.Actor.Playback == "hold" ? 0 : 1, definition.Actor.Playback == "loop");
        _previous = _current;
        Active = definition.Actor.Script.Length > 0;
    }

    public void Activate() => Active = true;

    private double ClipTime(Playback playback, double time)
    {
        var age = playback.Offset + Math.Max(0, time - playback.Start) * playback.Rate;
        var duration = _definition.Durations[playback.Clip];
        return playback.Loop ? age % duration : Math.Min(age, duration);
    }

    public StageScriptActorSample Sample(double time) => new(_current.Clip, ClipTime(_current, time), _previous.Clip, ClipTime(_previous, time),
        _blend == 0 ? 1 : (float)Math.Clamp((time - _current.Start) / _blend, 0, 1));

    public Gh3ScriptCallResult? Execute(uint command, Gh3QbValue parameters, double time)
    {
        bool Is(string name) => command == Gh3Checksum.Value(name);
        if (Is("GameObj_PlayAnim") || Is("PlayAnim"))
        {
            var reference = parameters.Field("anim") ?? parameters.Fields.FirstOrDefault(field => field.Name == 0 && field.Value.Type == Gh3QbType.Checksum)?.Value;
            var key = reference?.Data is string name ? Gh3Checksum.Value(name) : reference?.Integer ?? 0;
            if (!_clips.TryGetValue(key, out var clip)) throw new InvalidDataException("Actor animation is missing: " + Gh3QbText.Key(key));
            double Number(string field, double fallback) => parameters.Field(field) is { Type: Gh3QbType.Integer or Gh3QbType.Float } value ? Gh3ScriptRuntime.Numeric(value) : fallback;
            var rate = Number("speed", 1);
            var offset = Number("starttime", 0);
            // GH3: the PlayAnim BlendDuration default.
            var blend = Number("BlendDuration", .1);
            if (!double.IsFinite(rate) || rate <= 0 || !double.IsFinite(offset) || offset < 0 || offset > _definition.Durations[clip] ||
                !double.IsFinite(blend) || blend is < 0 or > MaximumBlendSeconds) throw new InvalidDataException("Invalid actor animation timing");
            _previous = _current;
            ClipKey = key;
            _current = new(clip, time, offset, time + (_definition.Durations[clip] - offset) / rate, rate,
                parameters.Flag("cycle") || parameters.Flag("loop") || parameters.Field("cycle") != null);
            _blend = blend;
            Active = true;
            return new(true);
        }
        if (Is("GameObj_WaitAnimFinished") || Is("WaitAnimFinished")) return new(true, Wait: Math.Max(0, _current.End - time));
        if (Is("GameObj_IsAnimFinished") || Is("AnimFinished")) return new(!_current.Loop && time >= _current.End);
        return null;
    }
}
