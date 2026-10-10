namespace Gh3Skin;

internal sealed class StageLightShowState
{
    private readonly StageScriptBank _bank;
    private readonly Gh3ScriptRuntime _runtime;
    private readonly StageLighting _lighting;
    private readonly Gh3QbValue _mapping;
    private readonly Dictionary<uint, Gh3QbValue> _processors;
    private readonly Dictionary<int, Gh3QbValue> _notes;
    private readonly Action<string> _gap;
    private readonly uint _taskId = Gh3Checksum.Value("gh3_lightshow_processor");
    private Gh3QbValue _parameters = Gh3ScriptRuntime.Structure(Array.Empty<Gh3QbField>());
    private Gh3QbValue _append = Gh3ScriptRuntime.Structure(Array.Empty<Gh3QbField>());
    private uint _processor;
    private uint _previous;
    private uint _last;
    private uint _lastPosition;
    private int _index;
    private string? _color;
    private double _blend = Gh3CommandDispatch.DefaultLightBlend;
    private bool _blendEnabled = true;
    private bool _active = true;
    private uint _transitionPerformance;
    private uint _transitionMood;
    private bool _performanceSet;
    // GH3.exe reads this unnamed table after LightShow_SetParams: node flags per performance, and per mood unless the performance is poor.
    private const uint NodeFlagTable = 0x1a22a5e6;
    public Action<uint, bool>? NodeFlagChanged { get; set; }
    public uint Mood { get; private set; } = Gh3Checksum.Value("intro");
    public uint Performance { get; private set; } = Gh3Checksum.Value("medium");
    private static uint Key(string name) => Gh3Checksum.Value(name);
    private static Gh3QbValue Params(params (string Name, Gh3QbValue Value)[] fields) => Gh3ScriptRuntime.Structure(fields.Select(field => new Gh3QbField(Key(field.Name), field.Value)));
    private static bool Enabled(Gh3QbValue? value) => value?.Type == Gh3QbType.Checksum && value.Integer == Key("true");
    private static uint Event(uint key) => Gh3Checksum.Append(Key("gh3_lightshow_event_"), key.ToString("x8"));

    public StageLightShowState(StageScriptBank bank, Gh3ScriptRuntime runtime, StageLighting lighting, string zone, Action<string> gap)
    {
        _bank = bank; _runtime = runtime; _lighting = lighting; _gap = gap;
        _mapping = bank.Values[Key(zone + "_lightshow_mapping")];
        _processors = new();
        foreach (var reference in new[] { "LightShow_SharedProcessors", zone + "_lightshow_processors" })
            if (bank.Values.TryGetValue(Key(reference), out var rows))
                foreach (var row in rows.Items) _processors[row.Field("name")!.Integer] = row;
        _notes = bank.Values.TryGetValue(Key("LightShow_NoteMapping"), out var notes) ?
            notes.Items.ToDictionary(row => checked((int)Gh3ScriptRuntime.Numeric(row.Field("midinote")!))) : new();
        lighting.BeginScriptControl();
    }

    private Gh3QbValue? Resolve(Gh3QbValue? value)
    {
        for (var i = 0; value?.Type == Gh3QbType.GlobalReference; i++)
        {
            if (i >= 64) throw new InvalidDataException("Recursive lightshow reference");
            value = _bank.Values.GetValueOrDefault(value.Integer);
        }
        return value;
    }

    private Gh3QbValue? Settings()
    {
        var mood = _mapping.Field(Mood);
        return mood?.Field(Performance) ?? mood?.Field("default");
    }

    public void SetPerformance(string performance) => SetParameters(Params(("performance", Gh3ScriptRuntime.Key(performance))));

    private void SetParameters(Gh3QbValue parameters)
    {
        _parameters = Gh3ScriptRuntime.Structure(_parameters.Fields.Where(field => parameters.Field(field.Name) == null).Concat(parameters.Fields));
        var changed = false;
        if (parameters.Field("performance") is { } performance)
        {
            changed |= !_performanceSet || Performance != performance.Integer;
            _performanceSet = true;
            Performance = performance.Integer;
            Transition("performance", Performance, ref _transitionPerformance);
        }
        if (parameters.Field("mood") is { } mood)
        {
            changed |= Mood != mood.Integer;
            Mood = mood.Integer;
            Transition("mood", Mood, ref _transitionMood);
        }
        if (changed && _performanceSet) ApplyNodeFlags();
        if (!_active) return;
        var next = Settings()?.Field("processor")?.Integer ?? 0;
        if (next != _processor)
        {
            _runtime.Cancel(id: _taskId);
            RunProcessor(_processor, "screxit");
            _processor = next;
            RunProcessor(_processor, "screnter");
            RunProcessor(_processor, "screvent");
        }
    }

    private void ApplyNodeFlags()
    {
        if (NodeFlagChanged == null || !_bank.Values.TryGetValue(NodeFlagTable, out var table)) return;
        static uint Flag(Gh3QbValue row) => row.Fields.First(field => field.Value.Type == Gh3QbType.Checksum).Value.Integer;
        var enabled = new Dictionary<uint, bool>();
        foreach (var section in table.Fields)
            foreach (var state in section.Value.Fields)
                foreach (var row in state.Value.Items) enabled.TryAdd(Flag(row), false);
        void Enable(string section, uint state)
        {
            foreach (var row in table.Field(section)?.Field(state)?.Items ?? Array.Empty<Gh3QbValue>())
                enabled[Flag(row)] = row.Fields.FirstOrDefault(field => field.Value.Type == Gh3QbType.Integer)?.Value is not { } value || Gh3ScriptRuntime.Truth(value);
        }
        Enable("performance", Performance);
        if (Performance != Key("poor")) Enable("mood", Mood);
        foreach (var (flag, value) in enabled) NodeFlagChanged(flag, value);
    }

    private void Transition(string name, uint value, ref uint previous)
    {
        if (previous == value) return;
        var parameters = Params((name, new(Gh3QbType.Checksum, -1, value)), ("previous" + name, new(Gh3QbType.Checksum, -1, previous)));
        foreach (var script in _mapping.Field("transitions")?.Field(name)?.Items ?? Array.Empty<Gh3QbValue>())
            _runtime.Start(script.Integer, parameters, optional: true);
        previous = value;
    }

    private Gh3QbValue GetParameters() => Gh3ScriptRuntime.Structure(_parameters.Fields.Where(field => !new[] { Key("mood"), Key("performance"), Key("previous_snapshot"), Key("bpm") }.Contains(field.Name)).Concat(
        Params(("mood", new(Gh3QbType.Checksum, -1, Mood)), ("performance", new(Gh3QbType.Checksum, -1, Performance)), ("bpm", Gh3ScriptRuntime.Number(_runtime.Tempo(_runtime.Time)))).Fields).Concat(
        _previous == 0 ? Array.Empty<Gh3QbField>() : new[] { new Gh3QbField(Key("previous_snapshot"), new(Gh3QbType.Checksum, -1, _previous)) }));

    private void RunProcessor(uint processor, string phase)
    {
        if (processor == 0) return;
        if (!_processors.TryGetValue(processor, out var row)) throw new InvalidDataException("Lightshow processor is missing: " + Gh3QbText.Key(processor));
        if (row.Field(phase) is { } script) _runtime.Start(script.Integer, GetParameters(), id: _taskId);
    }

    public bool Note(int note)
    {
        if (!_notes.TryGetValue(note, out var row)) return false;
        var parameters = Resolve(row.Field("params")) ?? Gh3ScriptRuntime.Structure(Array.Empty<Gh3QbField>());
        if (row.Field("scr") is { } script) _runtime.Start(script.Integer, parameters);
        else if (row.Field("event") is { } type) PassEvent(type.Integer, parameters);
        return true;
    }

    private void PassEvent(uint type, Gh3QbValue parameters)
    {
        _runtime.Start(Key("LaunchEvent"), Params(("Type", new(Gh3QbType.Checksum, -1, Event(type))), ("Data", parameters)));
        _runtime.Advance(_runtime.Time);
    }

    public bool Cue(StageCue cue)
    {
        if (cue.Type == "gh3-note" && int.TryParse(cue.Value, out var note)) return Note(note);
        if (cue.Type == "lighting")
        {
            if (_lighting.Bank.Moods.ContainsKey(cue.Value) || _mapping.Field(Key(cue.Value)) != null)
                SetParameters(Params(("mood", Gh3ScriptRuntime.Key(cue.Value))));
            else
            {
                Execute(Key("LightShow_OverrideColor"), Params(("color", Gh3ScriptRuntime.Key(cue.Value))));
                SetParameters(Params(("mood", Gh3ScriptRuntime.Key("intro"))));
                Execute(Key("LightShow_CycleNextSnapshot"), Params(("usesnapshotpositions", Gh3ScriptRuntime.Key("true")), ("save", Gh3ScriptRuntime.Key("true"))));
            }
        }
        else if (cue.Type == "blend") _blend = double.Parse(cue.Value, System.Globalization.CultureInfo.InvariantCulture);
        else if (cue.Type == "color") Execute(Key("LightShow_OverrideColor"), cue.Value == "off" ? Gh3ScriptRuntime.Structure(new[] { new Gh3QbField(0, Gh3ScriptRuntime.Key("off")) }) : Params(("color", Gh3ScriptRuntime.Key(cue.Value))));
        else if (cue.Type is "snapshot" or "strobe-toggle") PassEvent(Key(cue.Type == "snapshot" ? "snapshotchange" : "strobetoggle"), Params(("usesnapshotpositions", Gh3ScriptRuntime.Key("true"))));
        else return false;
        return true;
    }

    public Gh3ScriptCallResult? Execute(uint command, Gh3QbValue parameters)
    {
        bool Is(string name) => command == Key(name);
        if (Is("LightShow_GetParams")) return new(true, GetParameters().Fields);
        if (Is("LightShow_SetParams")) { SetParameters(parameters); return new(true); }
        if (Is("LightShow_SetActive"))
        {
            var active = Enabled(parameters.Field("active"));
            if (_active != active)
            {
                _runtime.Cancel(id: _taskId);
                RunProcessor(_processor, "screxit");
                _processor = _previous = _last = _lastPosition = 0;
                _transitionPerformance = _transitionMood = 0;
                _index = 0;
                _active = active;
            }
            return new(true);
        }
        if (Is("LightShow_AppendSnapshotParams"))
        {
            _append = parameters.Flag("clear") ? Gh3ScriptRuntime.Structure(Array.Empty<Gh3QbField>()) : parameters;
            foreach (var field in _append.Fields.Where(field => field.Name != Key("intensity") && field.Name != Key("specularintensity") && field.Name != Key("shadowintensity")))
                _gap("Lightshow snapshot parameter rendering is incomplete: " + Gh3QbText.Key(field.Name));
            return new(true);
        }
        if (Is("LightShow_SetTime"))
        {
            if (parameters.Flag("default")) _blend = Gh3CommandDispatch.DefaultLightBlend;
            if (parameters.Field("time") is { Type: Gh3QbType.Integer or Gh3QbType.Float } time) _blend = Gh3ScriptRuntime.Numeric(time);
            if (!double.IsFinite(_blend) || _blend is < 0 or > 600) throw new InvalidDataException("Invalid lightshow blend");
            if (parameters.Field("enable") is { } enabled) _blendEnabled = Enabled(enabled);
            return new(true);
        }
        if (Is("LightShow_OverrideColor"))
        {
            if (parameters.Flag("off")) _color = null;
            else if (parameters.Field("color") is { } color)
            {
                var name = _lighting.Bank.Colors.Keys.FirstOrDefault(name => Key(name) == color.Integer);
                if (name == null)
                {
                    _gap("This venue has no lightshow color " + Gh3QbText.Key(color.Integer));
                    return new(false);
                }
                _color = name;
            }
            return new(true);
        }
        if (Is("LightShow_WaitForNextEvent")) return new(true, Events: parameters.Field("events")?.Items.Select(value => Event(value.Integer)).ToArray() ?? throw new InvalidDataException("Lightshow event list is missing"));
        if (Is("LightShow_CycleNextSnapshot") || Is("LightShow_PlaySnapshot"))
        {
            if (!_active) return new(true);
            var positions = Enabled(parameters.Field("usesnapshotpositions"));
            uint snapshot;
            if (Is("LightShow_CycleNextSnapshot"))
            {
                var snapshots = Resolve(Settings()?.Field("snapshots"))?.Items;
                if (snapshots == null || snapshots.Count == 0) return new(true);
                _index %= snapshots.Count;
                if (positions && snapshots.Count > 1 && snapshots[_index].Integer == _lastPosition) _index = (_index + 1) % snapshots.Count;
                if (positions && snapshots.Count > 2 && snapshots[_index].Integer == _last) _index = (_index + 1) % snapshots.Count;
                snapshot = snapshots[_index++].Integer;
                _last = snapshot;
                if (positions) _lastPosition = snapshot;
            }
            else snapshot = parameters.Field("name")?.Integer ?? throw new InvalidDataException("Lightshow snapshot name is missing");
            if (!_lighting.Bank.Snapshots.ContainsKey(snapshot.ToString("x8"))) return new(true);
            if (Enabled(parameters.Field("save"))) _previous = snapshot;
            _lighting.PlaySnapshot(snapshot.ToString("x8"), _runtime.Time, _blendEnabled ? _blend : 0, _color, _append, positions);
            return new(true);
        }
        return null;
    }
}
