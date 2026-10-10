using System.Text.Json;

namespace Gh3Skin;

// A skinned venue actor: its definition and its clip state on the song clock (no Unity).

internal sealed class StageActorDefinition
{
    public string Id { get; set; } = "";
    public int Clip { get; set; }
    public Dictionary<string, int> Clips { get; set; } = new();
    public Dictionary<string, int[]> Actions { get; set; } = new();
    public string Role { get; set; } = "";
    public bool InitiallyVisible { get; set; } = true;
    public string Playback { get; set; } = "loop";
    public StagePlacement Transform { get; set; } = new();
    public string LightGroup { get; set; } = "";
    public string Object { get; set; } = "";
    public string Script { get; set; } = "";
    public bool Created { get; set; } = true;
    public float[] Scale { get; set; } = new[] { 1f, 1, 1 };
    public string AnimationEvents { get; set; } = "";
}

internal sealed class StageActorBank
{
    public int Version { get; set; }
    public StageActorDefinition[] Actors { get; set; } = Array.Empty<StageActorDefinition>();

    public static StageActorBank Load(string path)
    {
        var bank = JsonSerializer.Deserialize<StageActorBank>(File.ReadAllText(path), Packages.PackageJson.CaseInsensitive);
        if (bank?.Version != 1 || bank.Actors == null || bank.Actors.Length > 128 ||
            bank.Actors.Select(actor => actor.Id).Distinct().Count() != bank.Actors.Length)
            throw new InvalidDataException("Invalid stage actors");
        foreach (var actor in bank.Actors)
            if (actor.Id.Length != 8 || !actor.Id.All(Uri.IsHexDigit) || !actor.Transform.Valid || actor.Playback is not ("loop" or "hold") ||
                actor.Clips == null || actor.Actions == null || actor.Clips.Count > 256 || actor.Actions.Count > 256 ||
                actor.Actions.Any(pair => pair.Value == null || pair.Value.Length is < 1 or > 256) || actor.Role.Length > 128 ||
                actor.Object.Length > 256 || actor.Script.Length > 256 || actor.Script.Length > 0 && actor.Object.Length == 0)
                throw new InvalidDataException("Invalid stage actor");
        if (bank.Actors.Any(actor => actor.Scale.Length != 3 || actor.Scale.Any(value => !float.IsFinite(value) || value <= 0)))
            throw new InvalidDataException("Invalid actor scale");
        return bank;
    }
}

internal readonly record struct StageActorSample(int Clip, double Time, bool Visible);

internal sealed class StageActorState
{
    private readonly StageActorDefinition _definition;
    private readonly double[] _durations;
    private readonly StageCue[] _commands;
    private int _index;
    private int _clip;
    private uint _seed;
    private bool _visible;
    private bool _loop;
    private bool _hold;
    private double _start;
    private double _hideAt;
    private double _lastTime = double.NaN;

    public StageActorState(StageActorDefinition definition, IEnumerable<double> durations, StageTimeline timeline)
    {
        _definition = definition;
        _durations = durations.ToArray();
        if (_durations.Length is < 1 or > 256 || _durations.Any(duration => !double.IsFinite(duration) || duration <= 0) ||
            definition.Clip < 0 || definition.Clip >= _durations.Length ||
            definition.Clips.Values.Concat(definition.Actions.Values.SelectMany(value => value)).Any(clip => clip < 0 || clip >= _durations.Length))
            throw new InvalidDataException("Invalid stage actor clip reference");
        _commands = timeline.PlaybackCues.Where(cue => cue.Type == "stage-diver" && definition.Role == "stage-diver/" + cue.Role ||
            cue.Type is "actor-play" or "actor-hide" or "actor-show" && (cue.Role == definition.Id || cue.Role == definition.Role)).ToArray();
        Reset();
    }

    private void Reset()
    {
        _index = 0;
        _clip = _definition.Clip;
        _visible = _definition.InitiallyVisible;
        _loop = _definition.Playback == "loop";
        _hold = _definition.Playback == "hold";
        _start = 0;
        _hideAt = double.PositiveInfinity;
        _seed = Gh3Checksum.Value(_definition.Id);
    }

    public StageActorSample Sample(double time)
    {
        if (!double.IsFinite(time)) throw new ArgumentOutOfRangeException(nameof(time));
        if (time < _lastTime) Reset();
        while (_index < _commands.Length && _commands[_index].Time <= time)
        {
            var cue = _commands[_index++];
            Finish(cue.Time);
            if (cue.Type == "actor-hide" || cue.Type == "stage-diver" && cue.Value == "hide") _visible = false;
            else if (cue.Type == "actor-show") _visible = true;
            else
            {
                var action = cue.Type == "stage-diver" ? "jump" : cue.Value;
                if (_definition.Actions.TryGetValue(action, out var choices))
                {
                    XorShift32.Next(ref _seed);
                    _clip = choices[_seed % choices.Length];
                }
                else if (_definition.Clips.TryGetValue(action, out var clip)) _clip = clip;
                else continue;
                _start = cue.Time;
                _hold = false;
                _loop = cue.Loop;
                _hideAt = cue.Type == "stage-diver" ? cue.Time + _durations[_clip] : double.PositiveInfinity;
                if (cue.Type == "stage-diver") _visible = true;
            }
        }
        Finish(time);
        _lastTime = time;
        var age = Math.Max(0, time - _start);
        return new(_clip, _hold ? 0 : _loop ? age % _durations[_clip] : Math.Min(age, _durations[_clip]), _visible);
    }

    private void Finish(double time)
    {
        if (time < _hideAt) return;
        _visible = false;
        _hideAt = double.PositiveInfinity;
    }
}
