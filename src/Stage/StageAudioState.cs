namespace Gh3Skin;

internal sealed class StageAudioState
{
    private sealed record GainChange(double Time, double From, double To, double Duration)
    {
        public double At(double time) => From + (To - From) * Math.Clamp(Duration == 0 ? 1 : (time - Time) / Duration, 0, 1);
    }
    private sealed record HealthChange(double Time, float Rock);
    private readonly StageSoundBank _bank;
    private readonly StageCue[] _commands;
    private readonly string _venue;
    private readonly string _countoff;
    private readonly List<HealthChange> _health = new() { new(double.NegativeInfinity, .5f) };
    private readonly Dictionary<uint, GainChange> _gains = new();
    private readonly HashSet<uint> _lockedBusses = new();
    private readonly PriorityQueue<Action<double>, (double Time, int Phase, long Order)> _queue = new();
    private readonly HashSet<string> _diagnostics = new();
    private StageSoundEvents _events;
    private Gh3ScriptRuntime? _runtime;
    private long _order;
    private double _lastTime = double.NaN;
    private float _rock = .5f;
    private string _skill = "";
    public bool Training { get; set; }
    public int Revision { get; private set; }
    public IReadOnlyCollection<string> Diagnostics => _diagnostics;
    public IReadOnlyList<StageSoundVoice> Voices => _events.Voices;

    public StageAudioState(StageSoundBank bank, StageTimeline timeline, string venue, string countoff)
    {
        _bank = bank;
        _commands = timeline.PlaybackCues.Where(cue => cue.Type == "gh3-audio").ToArray();
        _venue = venue;
        _countoff = countoff;
        _events = new(bank);
        Reset();
    }

    private void Schedule(double time, Action<double> action, int phase = 1) => _queue.Enqueue(action, (time, phase, _order++));

    private void Reset()
    {
        _runtime?.Dispose();
        _runtime = new(_bank.RuntimeValues, Native, time: -86400) { Failed = (_, error) => _diagnostics.Add(error.Message) };
        _runtime.Set(Gh3Checksum.Value("CrowdLevelForSurges"), Gh3ScriptRuntime.Number(_bank.Constants.GetValueOrDefault("CrowdLevelForSurges", 1.66)));
        _events = new(_bank);
        _queue.Clear();
        _gains.Clear();
        _lockedBusses.Clear();
        _order = 0;
        _skill = "";
        _rock = .5f;
        _runtime.Set(Gh3Checksum.Value("current_crowd"), Gh3ScriptRuntime.Number(1));
        _runtime.Set(Gh3Checksum.Value("game_mode"), Gh3ScriptRuntime.Key(Training ? "training" : "quickplay"));
        _runtime.Set(Gh3Checksum.Value("BackgroundSFXDebugPrintToScreen"), Gh3ScriptRuntime.Number(0));
        _runtime.Set(Gh3Checksum.Value("boss_battle"), Gh3ScriptRuntime.Number(0));
        if (_bank.Value("Default_BussSet") is { } settings)
            foreach (var field in settings.Fields.Where(field => field.Value.Type == 10))
                _gains[field.Name] = new(double.NegativeInfinity, 0, StageSoundBank.Number(field.Value.Field("vol"), 0), 0);
        foreach (var health in _health)
            Schedule(health.Time, time => { _rock = health.Rock; if (time >= 0) Bed(time); }, 0);
        Schedule(0, Bed);
        foreach (var cue in _commands)
            Schedule(cue.Time, time =>
            {
                try { Execute(Gh3ScriptEvent.Decode(cue.Value), time); }
                catch (InvalidDataException error) { _diagnostics.Add(error.Message); }
            });
        Revision++;
    }

    public void Update(double time, float rock)
    {
        if (!double.IsFinite(time) || Math.Abs(time) > 86400) throw new ArgumentOutOfRangeException(nameof(time));
        rock = Math.Clamp(float.IsFinite(rock) ? rock : .5f, 0, 1);
        var rebuild = time < _lastTime;
        if (rebuild) _health.RemoveAll(change => change.Time > time);
        if (RockState(_health[^1].Rock) != RockState(rock))
        {
            if (_health[^1].Time == time) { _health.RemoveAt(_health.Count - 1); rebuild = true; }
            _health.Add(new(time, rock));
            if (!rebuild) Schedule(time, at => { _rock = rock; Bed(at); }, 0);
        }
        if (rebuild) Reset();
        var work = 0;
        while (_queue.TryPeek(out _, out var next) && next.Time <= time)
        {
            if (++work > 1000000) throw new InvalidDataException("Stage audio work exceeds its limit");
            var action = _queue.Dequeue();
            if (double.IsFinite(next.Time)) _runtime!.Advance(next.Time);
            action(next.Time);
        }
        _runtime!.Advance(time);
        _events.Prune(time);
        _lastTime = time;
    }

    private (string Skill, bool Surge) RockState(float rock) => (Skill(rock), rock * 2 > _bank.Constants.GetValueOrDefault("CrowdLevelForSurges", 1.66));
    private string Skill(float rock) => rock * 2 < _bank.Constants.GetValueOrDefault("health_poor_medium", .66) ? "Bad" :
        rock * 2 > _bank.Constants.GetValueOrDefault("health_medium_good", 1.33) ? "Good" : "Neutral";

    private void Bed(double time)
    {
        _runtime!.Set(Gh3Checksum.Value("current_crowd"), Gh3ScriptRuntime.Number(_rock * 2));
        if (Training) return;
        var skill = Skill(_rock);
        if (skill == _skill) return;
        _skill = skill;
        var command = Gh3Checksum.Value("Change_Crowd_Looping_SFX_" + skill);
        if (_runtime.HasScript(command)) _runtime.Start(command);
    }

    public float Gain(StageSoundVoice voice, double time)
    {
        var decibels = _bank.Busses(voice.Buss).Sum(bus => _gains.TryGetValue(bus, out var change) ? change.At(time) : 0);
        return voice.Gain * (float)Math.Pow(10, decibels / 20);
    }

    private void Execute(Gh3ScriptEvent call, double time)
    {
        var command = call.Command;
        var parameters = call.Parameters;
        _runtime!.Set(Gh3Checksum.Value("current_crowd"), Gh3ScriptRuntime.Number(_rock * 2));
        _runtime.Set(Gh3Checksum.Value("game_mode"), Gh3ScriptRuntime.Key(Training ? "training" : "quickplay"));
        _runtime.Start(command, parameters);
    }

    private Gh3ScriptCallResult? Native(Gh3ScriptContext context, uint command, Gh3QbValue parameters)
    {
        bool Is(string name) => command == Gh3Checksum.Value(name);
        var time = _runtime!.Time;
        if (Is("SoundEvent"))
        {
            var key = parameters.Field("event") ?? throw new InvalidDataException("SoundEvent requires an event reference");
            _events.Play(key.Integer, time);
            return new(true);
        }
        if (Is("PlaySound"))
        {
            var sample = parameters.Fields.FirstOrDefault(field => field.Name == 0)?.Value ?? throw new InvalidDataException("PlaySound requires a sound reference");
            if (sample.Integer == Gh3Checksum.Value("default") || sample.Integer == Gh3Checksum.Value("none")) return new(false);
            _events.Play(sample.Integer, time, buss: parameters.Field("buss")?.Integer, options: parameters);
            return new(true);
        }
        if (Is("StopSoundEvent") || Is("StopStream") || Is("StopSound"))
        {
            var key = parameters.Field("event") ?? parameters.Fields.FirstOrDefault(field => field.Name == 0)?.Value;
            if (key != null) _events.Stop(key.Integer, time);
            return new(true);
        }
        if (Is("IsStreamPlaying") || Is("IsSoundPlaying") || Is("IsSoundEventPlaying"))
        {
            var key = parameters.Field("event") ?? parameters.Fields.FirstOrDefault(field => field.Name == 0)?.Value;
            return new(key != null && _events.Playing(key.Integer, time));
        }
        if (Is("SetSoundParams"))
        {
            var key = parameters.Fields.FirstOrDefault(field => field.Name == 0)?.Value;
            if (key != null && parameters.Field("vol") is { } gain) _events.Volume(key.Integer, time, (float)StageSoundBank.Number(gain, 100) / 100);
            return new(true);
        }
        if (Is("GetPakManCurrent") || Is("GetPakManCurrentName"))
        {
            var zone = _venue.StartsWith("gh3.") ? "z_" + _venue.Substring(4) : _venue;
            return new(true, new[] { new Gh3QbField(Gh3Checksum.Value(Is("GetPakManCurrent") ? "pak" : "pakname"),
                Is("GetPakManCurrent") ? new(13, -1, _runtime.Intern(zone)) : new(3, -1, zone)) });
        }
        if (Is("get_song_struct")) return new(true, new[] { new Gh3QbField(Gh3Checksum.Value("song_struct"),
            Gh3ScriptRuntime.Structure(new[] { new Gh3QbField(Gh3Checksum.Value("countoff"), new(3, -1, _countoff)) })) });
        if (Is("SoundBussUnlock") || Is("SoundBussLock"))
        {
            var busses = parameters.Fields.Where(field => field.Name == 0 && field.Value.Type == 13).Select(field => field.Value.Integer).ToArray();
            if (busses.Length == 0) throw new InvalidDataException("A sound bus lock needs a bus reference");
            foreach (var bus in busses)
                if (Is("SoundBussLock")) _lockedBusses.Add(bus);
                else _lockedBusses.Remove(bus);
            return new(true);
        }
        if (Is("SetSoundBussParams"))
        {
            var duration = StageSoundBank.Number(parameters.Field("time"), 0);
            if (!double.IsFinite(duration) || duration is < 0 or > 86400) throw new InvalidDataException("Invalid sound bus fade duration");
            foreach (var field in parameters.Fields.Where(field => field.Value.Type == 10))
            {
                if (_lockedBusses.Contains(field.Name)) continue;
                var previous = _gains.TryGetValue(field.Name, out var value) ? value.At(time) : 0;
                var target = StageSoundBank.Number(field.Value.Field("vol"), previous);
                if (!double.IsFinite(target) || target is < -1000 or > 100) throw new InvalidDataException("Invalid sound bus volume");
                _gains[field.Name] = new(time, previous, target, duration);
            }
            return new(true);
        }
        return null;
    }

}
