using System.Numerics;

namespace Gh3Skin;

internal sealed class StageScriptState : IDisposable
{
    private readonly StageScriptBank _bank;
    private readonly IReadOnlyDictionary<string, StageObjectPlacement> _placements;
    private readonly StageCue[] _commands;
    private readonly string _venue;
    private readonly Func<uint, double, StageBeamValue?>? _beams;
    private readonly StageTimeline _timeline;
    private readonly uint _pyroScript;
    private readonly StageLighting? _lighting;
    private readonly Func<StageMaterialTarget, Vector4?>? _materialDefaults;
    private readonly bool _sourceLighting;
    private StageLightShowState? _lightShow;
    private readonly IReadOnlyDictionary<uint, StageScriptActorDefinition> _actorDefinitions;
    private readonly Dictionary<uint, StageScriptActorState> _actors = new();
    private readonly List<(double Time, float Rock)> _health = new() { (double.NegativeInfinity, .5f) };
    private sealed record InputEvent(double Time, uint Type, uint? Target, Gh3QbValue Parameters);
    private readonly List<InputEvent> _events = new();
    private int _nextEvent;
    private int _nextHealth;
    private float _rock = .5f;
    private uint _performance;
    private readonly HashSet<string> _diagnostics = new();
    private readonly Dictionary<uint, StageObjectState> _objects = new();
    private sealed record Pulse(uint Object, uint[] Events, Vector3 Scale, float Amount, double Duration);
    private readonly List<Pulse> _pulses = new();
    private sealed record Attachment(uint Parent, Vector3 Offset, Vector3 Position, Quaternion Rotation, bool Rotate);
    private readonly Dictionary<uint, Attachment> _attachments = new();
    private readonly HashSet<uint> _nodeFlags = new();
    private readonly HashSet<uint> _knownNodeFlags = new();
    private readonly Dictionary<uint, Gh3QbValue> _beamParameters = new();
    private Gh3ScriptRuntime _runtime = null!;
    private int _next;
    private double _time = double.NaN;
    public IReadOnlyDictionary<uint, StageObjectState> Objects => _objects;
    public IReadOnlyDictionary<uint, StageScriptActorState> Actors => _actors;
    public IReadOnlyCollection<string> Diagnostics => _diagnostics;
    public StageParticleState Particles { get; private set; } = null!;
    public Func<Vector3, StageParticleVisibility, double, (float? Distance, bool Visible)>? ParticleView { get; set; }
    public StageTeslaState Tesla { get; private set; } = null!;
    public StageMaterialState Materials { get; private set; } = null!;
    public StageMovieState Movies { get; private set; } = null!;
    public StageRenderControlState RenderControls { get; private set; } = null!;
    public StageLightScene LightScene { get; private set; } = null!;
    public bool SourcePyro => _pyroScript != 0;

    public StageScriptState(StageScriptBank bank, IReadOnlyDictionary<string, StageObjectPlacement> placements, StageTimeline timeline, string venue,
        Func<uint, double, StageBeamValue?>? beams = null, IReadOnlyDictionary<uint, StageScriptActorDefinition>? actors = null, StageLighting? lighting = null,
        Func<StageMaterialTarget, Vector4?>? materialDefaults = null)
    {
        _bank = bank;
        _placements = placements;
        _venue = venue;
        _beams = beams;
        _timeline = timeline;
        _lighting = lighting;
        _materialDefaults = materialDefaults;
        _actorDefinitions = actors ?? new Dictionary<uint, StageScriptActorDefinition>();
        var zone = venue.StartsWith("gh3.") ? "z_" + venue.Substring(4) : venue;
        _sourceLighting = lighting != null && bank.Values.ContainsKey(Gh3Checksum.Value(zone + "_lightshow_mapping"));
        _pyroScript = bank.Values.GetValueOrDefault(Gh3Checksum.Value(zone + "_lightshow_mapping"))?.Field("Pyro_Script")?.Integer ?? 0;
        bool ScriptCue(StageCue cue)
        {
            var type = cue.Type == "gh3-script" ? Gh3CommandDispatch.Resolve(Gh3ScriptEvent.Decode(cue.Value)).Type :
                cue.Type == "gh3-note" && int.TryParse(cue.Value, out var note) ? StageTimeline.FromGh3Note(cue.Time, note)?.Type : cue.Type;
            return cue.Type == "gh3-script" && type == "" || SourcePyro && type == "pyro" ||
                _sourceLighting && type is "lighting" or "color" or "blend" or "snapshot" or "strobe-toggle";
        }
        _commands = timeline.Cues.Where(ScriptCue).ToArray();
        Reset();
    }

    private void Reset()
    {
        _runtime?.Dispose();
        Movies?.Dispose();
        _objects.Clear();
        _actors.Clear();
        foreach (var (id, definition) in _actorDefinitions) _actors.Add(id, new(definition));
        _pulses.Clear();
        _attachments.Clear();
        _nodeFlags.Clear();
        _knownNodeFlags.Clear();
        _beamParameters.Clear();
        foreach (var (id, placement) in _placements) _objects.Add(Convert.ToUInt32(id, 16), new(placement));
        _runtime = new(_bank.Values, Native, time: Math.Min(_timeline.Start, _commands.FirstOrDefault()?.Time ?? 0)) { Failed = (_, error) => _diagnostics.Add(error.Message) };
        _runtime.Tempo = _timeline.TempoAt;
        var zone = _venue.StartsWith("gh3.") ? "z_" + _venue.Substring(4) : _venue;
        _lightShow = _sourceLighting ? new(_bank, _runtime, _lighting!, zone, message => _diagnostics.Add(message)) { NodeFlagChanged = ChangeNodeFlag } : null;
        Materials = new(_bank, _runtime, message => _diagnostics.Add(message), _materialDefaults);
        LightScene = new(_bank.NodeValues.Values, _runtime, _lighting, _bank.Values.GetValueOrDefault(Gh3Checksum.Value(zone + "_snapshots")));
        RenderControls = new(_lighting, id => _objects.TryGetValue(id, out var obj) && obj.Exists, message => _diagnostics.Add(message));
        Movies = new(_bank, message => _diagnostics.Add(message));
        Tesla = new(_runtime, message => _diagnostics.Add(message));
        Particles = new(_bank, _runtime, id => _objects.TryGetValue(id, out var obj) && obj.Exists || Tesla.Exists(id), AttachmentPose, message => _diagnostics.Add(message)) {
            View = (position, visibility, time) => ParticleView?.Invoke(position, visibility, time) ?? (null, true)
        };
        _nextHealth = 0;
        _nextEvent = 0;
        _rock = .5f;
        _performance = 0;
        _runtime.EventRaised = (type, target, _) =>
        {
            foreach (var pulse in _pulses.Where(pulse => pulse.Events.Contains(type) && (target == null || pulse.Object == target)))
                if (_objects.TryGetValue(pulse.Object, out var obj) && obj.Exists) obj.Pulse(pulse.Scale, pulse.Amount, pulse.Duration, _runtime.Time);
        };
        _next = 0;
        if (_runtime.HasScript(Gh3Checksum.Value("LightShow_AddNodeFlags")))
        {
            _runtime.Start(Gh3Checksum.Value("LightShow_AddNodeFlags"));
            // LightShow_InitEventMappings always enables the guitarist follow spot and enables the bassist spot only with two players.
            var players = _bank.Values.TryGetValue(Gh3Checksum.Value("current_num_players"), out var count) ? Gh3ScriptRuntime.Numeric(count) : 1;
            ChangeNodeFlag(GuitaristSpotFlag, true);
            ChangeNodeFlag(BassistSpotFlag, players != 1);
            // start_gem_scroller sets the progression flags (LS_ALWAYS, LS_3_5_*, LS_ENCORE_*) from the game mode.
            if (_runtime.HasScript(Gh3Checksum.Value("Progression_SetProgressionNodeFlags"))) _runtime.Start(Gh3Checksum.Value("Progression_SetProgressionNodeFlags"));
        }
        foreach (var (id, placement) in _placements.Where(pair => pair.Value.Created && pair.Value.Script.Length > 0))
            _runtime.Start(Gh3QbText.Key(placement.Script), target: Convert.ToUInt32(id, 16));
        foreach (var script in _bank.Startup) _runtime.Start(Gh3QbText.Key(script));
    }

    public void Update(double time, float rock = .5f)
    {
        if (!double.IsFinite(time) || Math.Abs(time) > StageLimits.MaximumSeconds) throw new ArgumentOutOfRangeException(nameof(time));
        rock = Math.Clamp(float.IsFinite(rock) ? rock : .5f, 0, 1);
        var rebuild = time < _time;
        if (rebuild)
        {
            _health.RemoveAll(change => change.Time > time);
            _events.RemoveAll(change => change.Time > time);
        }
        if (_health[^1].Rock != rock)
        {
            if (_health[^1].Time == time) { _health.RemoveAt(_health.Count - 1); rebuild = true; }
            _health.Add((time, rock));
        }
        if (rebuild) Reset();
        if (time == _time && !rebuild && _nextHealth == _health.Count) return;
        if (time < _runtime.Time) { _time = time; return; }
        while (true)
        {
            var commandTime = _next < _commands.Length ? _commands[_next].Time : double.PositiveInfinity;
            var healthTime = _nextHealth < _health.Count ? _health[_nextHealth].Time : double.PositiveInfinity;
            var eventTime = _nextEvent < _events.Count ? _events[_nextEvent].Time : double.PositiveInfinity;
            if (Math.Min(eventTime, Math.Min(commandTime, healthTime)) > time) break;
            if (healthTime <= commandTime && healthTime <= eventTime)
            {
                if (healthTime >= _runtime.Time) _runtime.Advance(healthTime);
                _rock = _health[_nextHealth++].Rock;
                _runtime.Set(Gh3Checksum.Value("current_crowd"), Gh3ScriptRuntime.Number(_rock * 2));
                var performance = Gh3Checksum.Value(Performance());
                if (_lightShow != null) _lightShow.SetPerformance(Performance());
                else if (performance != _performance && _bank.PerformanceChanged.Length > 0)
                    _runtime.Start(Gh3QbText.Key(_bank.PerformanceChanged), Gh3ScriptRuntime.Structure(new[] { new Gh3QbField(Gh3Checksum.Value("performance"), new(Gh3QbType.Checksum, -1, performance)) }));
                _performance = performance;
                continue;
            }
            if (eventTime < commandTime)
            {
                _runtime.Advance(eventTime);
                Dispatch(_events[_nextEvent++]);
                continue;
            }
            var cue = _commands[_next++];
            _runtime.Advance(cue.Time);
            if (cue.Type == "gh3-script")
            {
                var call = Gh3ScriptEvent.Decode(cue.Value);
                _runtime.Start(call.Command, call.Parameters);
            }
            else if (_lightShow?.Cue(cue) != true && SourcePyro) _runtime.Start(Gh3Checksum.Value("LightShow_PyroEvent"));
        }
        _runtime.Advance(time);
        _time = time;
    }

    public void Raise(uint type, uint? target = null, Gh3QbValue? parameters = null)
    {
        if (!double.IsFinite(_time) || _time < _runtime.Time) return;
        if (_events.Count >= 1000000) throw new InvalidDataException("Too many recorded gameplay events");
        var input = new InputEvent(_time, type, target, parameters ?? Gh3ScriptRuntime.Structure(Array.Empty<Gh3QbField>()));
        _events.Add(input);
        _nextEvent = _events.Count;
        Dispatch(input);
        _runtime.Advance(_time);
    }

    private void Dispatch(InputEvent input)
    {
        var fields = new List<Gh3QbField> { new(Gh3Checksum.Value("type"), new(Gh3QbType.Checksum, -1, input.Type)), new(Gh3Checksum.Value("Data"), input.Parameters) };
        if (input.Target is { } target) fields.Add(new(Gh3Checksum.Value("target"), new(Gh3QbType.Checksum, -1, target)));
        _runtime.Start(Gh3Checksum.Value("LaunchEvent"), Gh3ScriptRuntime.Structure(fields));
    }

    public StageObjectPose Sample(uint id, double time, int depth = 0)
    {
        if (depth > 64) throw new InvalidDataException("Stage object attachment depth exceeds its limit");
        var pose = _objects[id].Sample(time);
        if (!_attachments.TryGetValue(id, out var attachment)) return pose;
        var parent = Sample(attachment.Parent, time, depth + 1);
        return pose with {
            Position = parent.Position + Vector3.Transform(parent.Scale * (attachment.Offset + Vector3.Transform(pose.Position - attachment.Position, attachment.Rotation)), parent.Rotation),
            Velocity = parent.Velocity + Vector3.Transform(parent.Scale * Vector3.Transform(pose.Velocity, attachment.Rotation), parent.Rotation),
            Rotation = attachment.Rotate ? Quaternion.Normalize(parent.Rotation * attachment.Rotation * pose.Rotation) : pose.Rotation
        };
    }

    public bool Attached(uint id) => _attachments.ContainsKey(id);

    public StageBeamValue Beam(uint id, StageBeamValue value)
    {
        if (!_beamParameters.TryGetValue(id, out var parameters)) return value;
        return ApplyBeam(parameters, value);
    }

    private static StageBeamValue ApplyBeam(Gh3QbValue parameters, StageBeamValue value)
    {
        float Number(string name, float fallback) => (float)StageParticleDefinition.Number(parameters, name, fallback, 0, StageLimits.MaximumMagnitude);
        var color = parameters.Field("VolumeColor")?.Items;
        return value with { Density = Number("VolumeDensity", value.Density), Range = Number("Range", value.Range),
            StartRadius = Number("StartRadius", value.StartRadius), EndRadius = Number("EndRadius", value.EndRadius),
            Color = color == null ? value.Color : new((float)Gh3ScriptRuntime.Numeric(color[0]) / 255, (float)Gh3ScriptRuntime.Numeric(color[1]) / 255, (float)Gh3ScriptRuntime.Numeric(color[2]) / 255) };
    }

    public StageObjectPose? AttachmentPose(uint id, uint bone, double time)
    {
        if (!_objects.TryGetValue(id, out var obj) || !obj.Exists) return null;
        var world = Sample(id, time);
        if (bone == 0) return world;
        if (!_actorDefinitions.TryGetValue(id, out var definition) || definition.Animation is not { } animation ||
            definition.Bones == null || !definition.Bones.TryGetValue(bone, out var index)) return null;
        var sample = _actors[id].Sample(time);
        var pose = new BandPose(Vector3.Zero, Quaternion.Identity);
        for (var current = index; current >= 0; current = animation.Parents[current])
        {
            var local = animation.SampleClip(animation.Clips[sample.Clip], sample.Time, current);
            if (sample.Blend < 1) local = BandPose.Mix(animation.SampleClip(animation.Clips[sample.PreviousClip], sample.PreviousTime, current), local, sample.Blend);
            pose = new(local.Position + Vector3.Transform(pose.Position, local.Rotation), Quaternion.Normalize(local.Rotation * pose.Rotation));
        }
        return world with { Position = world.Position + Vector3.Transform(pose.Position * world.Scale, world.Rotation), Rotation = Quaternion.Normalize(world.Rotation * pose.Rotation) };
    }

    private void ScheduleAnimationEvents(uint id, StageScriptActorState actor, Gh3QbValue parameters)
    {
        var taskId = Gh3Checksum.Value("gh3_actor_animation_events");
        _runtime.CancelScheduled(id: taskId, target: id);
        if (parameters.Field("AnimEvents") is not { } enabled || !Gh3ScriptRuntime.Truth(enabled) || enabled.Data is uint key && key == Gh3Checksum.Value("Off")) return;
        var tableName = _actorDefinitions[id].Actor.AnimationEvents;
        if (tableName.Length == 0) return;
        if (!_bank.Values.TryGetValue(Gh3QbText.Key(tableName), out var table) || table.Type != Gh3QbType.Structure)
            throw new InvalidDataException("Actor animation event table is missing: " + tableName);
        if (table.Field(actor.ClipKey) is not { } track) return;
        var events = track.Items.Select(row => (Time: Gh3ScriptRuntime.Numeric(row.Field("time") ?? throw new InvalidDataException("Animation event time is missing")),
            Script: row.Field("scr")?.Integer ?? throw new InvalidDataException("Animation event script is missing"), Params: row.Field("params"))).OrderBy(row => row.Time).ToArray();
        if (events.Length > 10000 || events.Any(row => !double.IsFinite(row.Time) || row.Time < 0 || row.Time > actor.Duration))
            throw new InvalidDataException("Animation event exceeds its clip");
        void Cycle(double start, double offset)
        {
            foreach (var row in events.Where(row => row.Time >= offset))
                _runtime.Schedule(start + (row.Time - offset) / actor.Rate,
                    () => _runtime.Start(row.Script, row.Params, id), id, taskId);
            if (actor.Loop && events.Length > 0)
            {
                var end = start + (actor.Duration - offset) / actor.Rate;
                _runtime.Schedule(end, () => Cycle(end, 0), id, taskId);
            }
        }
        Cycle(actor.Start, actor.Offset);
    }

    private const uint GuitaristSpotFlag = 0xfb1a2bba;
    private const uint BassistSpotFlag = 0x0fcd6094;

    private void ChangeNodeFlag(uint flag, bool enabled)
    {
        if (!_knownNodeFlags.Contains(flag) || enabled == _nodeFlags.Contains(flag)) return;
        if (enabled) _nodeFlags.Add(flag); else _nodeFlags.Remove(flag);
        ChangeNodeObjects(flag);
    }

    // GH3.exe creates a node when every known flag named by its CreatedFromVariable and CreatedOnProgress fields is set.
    private void ChangeNodeObjects(uint flag)
    {
        foreach (var (id, node) in _bank.NodeValues)
        {
            var flags = new[] { node.Field("CreatedFromVariable")?.Integer, node.Field("CreatedOnProgress")?.Integer };
            if (!flags.Contains(flag)) continue;
            if (!_objects.ContainsKey(id)) continue;
            var create = flags.All(item => item is not { } other || !_knownNodeFlags.Contains(other) || _nodeFlags.Contains(other));
            var args = Gh3ScriptRuntime.Structure(new[] { new Gh3QbField(Gh3Checksum.Value("nodeName"), new(Gh3QbType.Checksum, -1, id)) });
            _runtime.Start(Gh3Checksum.Value(create ? "SafeCreate" : "SafeKill"), args);
        }
    }

    private Gh3ScriptCallResult? Native(Gh3ScriptContext context, uint command, Gh3QbValue parameters)
    {
        bool Is(string name) => command == Gh3Checksum.Value(name);
        if (Is("IsPS3") || Is("IsPS2") || Is("IsXenon") || Is("IsWii")) return new(false);
        if (Is("IsPC")) return new(true);
        if (RenderControls.Execute(context, command, parameters, _runtime.Time) is { } renderResult) return renderResult;
        if (_lightShow?.Execute(command, parameters) is { } lightResult) return lightResult;
        if (Is("LightShow_GetPyroScript")) return new(SourcePyro, SourcePyro ? new[] { new Gh3QbField(Gh3Checksum.Value("pyro_script"), new(Gh3QbType.Checksum, -1, _pyroScript)) } : null);
        if ((Is("SafeCreate") || Is("SafeKill")) && _runtime.HasScript(command)) return null;
        var target = parameters.Field("Name") ?? parameters.Field("nodeName") ?? parameters.Field("id") ?? parameters.Fields.FirstOrDefault(item => item.Name == 0 && item.Value.Type == Gh3QbType.Checksum)?.Value;
        if (Is("IsInNodeArray")) return new(_bank.NodeValues.ContainsKey(target?.Integer ?? context.Object) || _objects.ContainsKey(target?.Integer ?? context.Object));
        if (Is("GetNodeFlag") || Is("SetNodeFlag") || Is("ClearNodeFlag") || Is("CreateNodeFlag") || Is("ChangeNodeFlag"))
        {
            var flag = target?.Integer ?? parameters.Field("flag")?.Integer ?? throw new InvalidDataException("Node flag is missing");
            if (Is("GetNodeFlag")) return new(_nodeFlags.Contains(flag));
            var value = parameters.Fields.FirstOrDefault(field => field.Name == 0 && field.Value.Type == Gh3QbType.Integer)?.Value;
            var enabled = Is("SetNodeFlag") || !Is("ClearNodeFlag") && value != null && Gh3ScriptRuntime.Truth(value);
            if (Is("CreateNodeFlag") && _knownNodeFlags.Add(flag))
            {
                if (enabled) _nodeFlags.Add(flag);
                return new(true);
            }
            if (Is("ChangeNodeFlag") && !_knownNodeFlags.Contains(flag) || enabled == _nodeFlags.Contains(flag)) return new(true);
            if (enabled) _nodeFlags.Add(flag); else _nodeFlags.Remove(flag);
            if (parameters.Field("queue") is { } queue && Gh3ScriptRuntime.Truth(queue)) _runtime.Schedule(_runtime.Time + _runtime.FrameDuration, () => ChangeNodeObjects(flag));
            else ChangeNodeObjects(flag);
            return new(true);
        }
        if (Is("GetNode"))
        {
            if (!_bank.NodeValues.TryGetValue(target?.Integer ?? context.Object, out var node)) return new(false);
            return new(true, new[] { new Gh3QbField(Gh3Checksum.Value("node"), node) });
        }
        if (Is("CallScriptOnNode"))
        {
            var prefix = parameters.Field("prefix")?.Integer;
            var targets = prefix is { } p ? _bank.PrefixValues.GetValueOrDefault(p, Array.Empty<uint>()) : target is { } n ? new[] { n.Integer } : throw new InvalidDataException("Node callback needs a target");
            var callback = parameters.Field("callBack_Script")?.Integer ?? throw new InvalidDataException("Node callback script is missing");
            foreach (var id in targets)
            {
                if (!_bank.NodeValues.TryGetValue(id, out var node)) throw new InvalidDataException("Callback node is missing");
                var fields = (parameters.Field("params")?.Fields ?? Array.Empty<Gh3QbField>()).Where(field => field.Name != Gh3Checksum.Value("nodeName") && field.Name != Gh3Checksum.Value("node"));
                _runtime.Start(callback, Gh3ScriptRuntime.Structure(fields.Concat(new[] { new Gh3QbField(Gh3Checksum.Value("nodeName"), new(Gh3QbType.Checksum, -1, id)), new Gh3QbField(Gh3Checksum.Value("node"), node) })), context.Object);
            }
            return new(true);
        }
        if (Is("CompositeObjectExists") || Is("IsCreated") || Is("ObjectExists"))
            return new(_objects.TryGetValue(target?.Integer ?? context.Object, out var obj) && obj.Exists || Particles.Exists(target?.Integer ?? context.Object) || Tesla.Exists(target?.Integer ?? context.Object));
        if (Tesla.Execute(context, command, parameters) is { } teslaResult) return teslaResult;
        if (Materials.Execute(command, parameters) is { } materialResult) return materialResult;
        if (Movies.Execute(command, parameters, _runtime) is { } movieResult) return movieResult;
        if (Particles.Execute(context, command, parameters) is { } particleResult) return particleResult;
        if (LightScene.Execute(command, parameters) is { } lightSceneResult)
        {
            if (lightSceneResult.Success && Is("SetLightFlag") && parameters.Field("flag")?.Integer == Gh3Checksum.Value("Shadow"))
                _diagnostics.Add("Source shadow-map rendering is incomplete");
            return lightSceneResult;
        }
        if (Is("LightShow_GetParams") || Is("hero_get_skill_level_cfunc"))
        {
            if (Is("hero_get_skill_level_cfunc")) return new(true, new[] { new Gh3QbField(Gh3Checksum.Value("skill"),
                Gh3ScriptRuntime.Key(Performance() switch { "poor" => "bad", "good" => "good", _ => "normal" })) });
            return new(true, new[] {
                new Gh3QbField(Gh3Checksum.Value("performance"), Gh3ScriptRuntime.Key(Performance())),
                new Gh3QbField(Gh3Checksum.Value("bpm"), Gh3ScriptRuntime.Number(_timeline.TempoAt(_runtime.Time))),
                new Gh3QbField(Gh3Checksum.Value("mood"), Gh3ScriptRuntime.Key(_timeline.Track("lighting").LastOrDefault(cue => cue.Time <= _runtime.Time)?.Value ?? "intro")) });
        }
        if (Is("get_anim_speed_for_tempo_cfunc"))
        {
            double Tempo(string name, double fallback) => _bank.Values.TryGetValue(Gh3Checksum.Value(name), out var value) ? Gh3ScriptRuntime.Numeric(value) : fallback;
            var bpm = _timeline.TempoAt(_runtime.Time);
            var speed = bpm < (Tempo("slow_tempo", 75) + Tempo("med_tempo", 115)) / 2 ? "slow" :
                bpm < (Tempo("med_tempo", 115) + Tempo("fast_tempo", 145)) / 2 ? "med" : "fast";
            return new(true, new[] { new Gh3QbField(Gh3Checksum.Value("anim_speed"), Gh3ScriptRuntime.Key(speed)) });
        }
        if (Is("Obj_LockToObject"))
        {
            var parent = parameters.Field("objectName")?.Integer ?? throw new InvalidDataException("Object attachment needs a parent");
            if (!_objects.ContainsKey(parent)) throw new InvalidDataException("Attachment parent is missing: " + Gh3QbText.Key(parent));
            if (!_objects.ContainsKey(context.Object)) throw new InvalidDataException("Attachment object is missing: " + Gh3QbText.Key(context.Object));
            var ancestor = parent;
            for (var depth = 0; ; depth++)
            {
                if (ancestor == context.Object || depth > 64) throw new InvalidDataException("Stage object attachments contain a cycle");
                if (!_attachments.TryGetValue(ancestor, out var link)) break;
                ancestor = link.Parent;
            }
            var vector = parameters.Field("offset") ?? parameters.Fields.FirstOrDefault(field => field.Name == 0 && field.Value.Type == Gh3QbType.Vector)?.Value;
            var xyz = vector?.Data as float[] ?? new float[3];
            if (xyz.Length != 3 || xyz.Any(value => !float.IsFinite(value))) throw new InvalidDataException("Invalid attachment offset");
            var pose = _objects[context.Object].Sample(_runtime.Time);
            _attachments[context.Object] = new(parent, new(xyz[0], xyz[1], -xyz[2]), pose.Position,
                Quaternion.Inverse(Sample(parent, _runtime.Time).Rotation), !parameters.Flag("NO_ROTATE"));
            return new(true);
        }
        if (Is("Obj_GetPosition") && _objects.ContainsKey(context.Object))
        {
            var p = Sample(context.Object, _runtime.Time).Position;
            var position = new Gh3QbValue(Gh3QbType.Vector, -1, new[] { p.X, p.Y, -p.Z });
            return new(true, new[] { new Gh3QbField(Gh3Checksum.Value("position"), position), new Gh3QbField(Gh3Checksum.Value("pos"), position) });
        }
        if (Is("LightVolume_GetParams"))
        {
            if (_beams?.Invoke(context.Object, _runtime.Time) is not { } beam) throw new InvalidDataException("Light volume object is missing");
            beam = Beam(context.Object, beam);
            return new(true, new[] {
                new Gh3QbField(Gh3Checksum.Value("VolumeDensity"), Gh3ScriptRuntime.Number(beam.Density)),
                new Gh3QbField(Gh3Checksum.Value("Range"), Gh3ScriptRuntime.Number(beam.Range)),
                new Gh3QbField(Gh3Checksum.Value("StartRadius"), Gh3ScriptRuntime.Number(beam.StartRadius)),
                new Gh3QbField(Gh3Checksum.Value("EndRadius"), Gh3ScriptRuntime.Number(beam.EndRadius)),
                new Gh3QbField(Gh3Checksum.Value("VolumeColor"), new(Gh3QbType.Array, -1, new[] { Gh3ScriptRuntime.Number(beam.Color.X * 255), Gh3ScriptRuntime.Number(beam.Color.Y * 255), Gh3ScriptRuntime.Number(beam.Color.Z * 255) })) });
        }
        if (Is("LightVolume_SetParams"))
        {
            var previous = _beamParameters.GetValueOrDefault(context.Object);
            if (parameters.Field("VolumeColor") is { } color && (color.Type != Gh3QbType.Array || color.Items.Count != 3 || color.Items.Any(item => !double.IsFinite(Gh3ScriptRuntime.Numeric(item)))))
                throw new InvalidDataException("Invalid light volume color");
            var fields = previous?.Fields.Where(field => parameters.Field(field.Name) == null) ?? Array.Empty<Gh3QbField>();
            var merged = Gh3ScriptRuntime.Structure(fields.Concat(parameters.Fields));
            _ = ApplyBeam(merged, default);
            var binding = _bank.LightVolumes.FirstOrDefault(pair => Gh3QbText.Key(pair.Key) == context.Object).Value;
            var index = binding == null || _lighting == null ? -1 : Array.IndexOf(_lighting.BeamIds, Gh3QbText.Key(binding).ToString("x8"));
            if (_sourceLighting && index >= 0)
                _lighting!.SetBeam(index, _runtime.Time, ApplyBeam(merged, _lighting.At(_runtime.Time).SampleBeam(index, _runtime.Time)));
            else _beamParameters[context.Object] = merged;
            var known = new[] { "VolumeDensity", "Range", "StartRadius", "EndRadius", "VolumeColor" }.Select(Gh3Checksum.Value).ToHashSet();
            foreach (var field in parameters.Fields.Where(field => !known.Contains(field.Name))) _diagnostics.Add("Light volume parameter is incomplete: " + Gh3QbText.Key(field.Name));
            return new(true);
        }
        if (Is("AddPulseEvent"))
        {
            if (!_objects.ContainsKey(context.Object)) throw new InvalidDataException("Pulse object is missing");
            var events = parameters.Field("events")?.Items.Select(value => value.Integer).ToArray() ?? throw new InvalidDataException("Pulse events are missing");
            var amount = (float)StageSoundBank.Number(parameters.Field("amount"), 1.12);
            var duration = StageSoundBank.Number(parameters.Field("time"), .1);
            if (!float.IsFinite(amount) || amount < 0 || !double.IsFinite(duration) || duration is < 0 or > StageLimits.MaximumSeconds) throw new InvalidDataException("Invalid object pulse");
            _pulses.Add(new(context.Object, events, _objects[context.Object].Sample(_runtime.Time).Scale, amount, duration));
            return new(true);
        }
        if (Is("SafeCreate") || Is("SafeKill") || Is("Create") || Is("Kill"))
        {
            target ??= context.Object == 0 ? null : new(Gh3QbType.Checksum, -1, context.Object);
            if (target == null || !_objects.TryGetValue(target.Integer, out var obj)) throw new InvalidDataException("Missing stage object " + Gh3QbText.Key(target?.Integer ?? 0));
            var create = Is("SafeCreate") || Is("Create");
            if (obj.Exists == create && (Is("SafeCreate") || Is("SafeKill"))) return new(true);
            if (!create) _runtime.RemoveObject(target.Integer);
            RenderControls.RemoveObject(target.Integer);
            _attachments.Remove(target.Integer);
            _pulses.RemoveAll(pulse => pulse.Object == target.Integer);
            var result = obj.Execute(Gh3Checksum.Value(create ? "Create" : "Kill"), parameters, _runtime.Time);
            LightScene.Enable(target.Integer, create);
            if (create && _actorDefinitions.TryGetValue(target.Integer, out var actorDefinition)) _actors[target.Integer] = new(actorDefinition);
            if (_actors.TryGetValue(target.Integer, out var changedActor)) changedActor.Activate();
            var placement = _placements[target.Integer.ToString("x8")];
            if (create && placement.Script.Length > 0) _runtime.Start(Gh3QbText.Key(placement.Script), target: target.Integer);
            return result;
        }
        if (Is("GetPakManCurrent") || Is("GetPakManCurrentName"))
        {
            var zone = _venue.StartsWith("gh3.") ? "z_" + _venue.Substring(4) : _venue;
            return new(true, new[] { new Gh3QbField(Gh3Checksum.Value(Is("GetPakManCurrent") ? "pak" : "pakname"),
                Is("GetPakManCurrent") ? new(Gh3QbType.Checksum, -1, _runtime.Intern(zone)) : new(Gh3QbType.String, -1, zone)) });
        }
        if (_actors.TryGetValue(context.Object, out var actor) && actor.Execute(command, parameters, _runtime.Time) is { } animation)
        {
            if (Is("GameObj_PlayAnim") || Is("PlayAnim")) ScheduleAnimationEvents(context.Object, actor, parameters);
            return animation;
        }
        if (_objects.TryGetValue(context.Object, out var item))
        {
            var result = item.Execute(command, parameters, _runtime.Time);
            if (result != null && _actors.TryGetValue(context.Object, out var changedActor)) changedActor.Activate();
            return result;
        }
        return null;
    }

    public void Dispose() { _runtime.Dispose(); Movies.Dispose(); }

    private string Performance()
    {
        var poor = _bank.Values.TryGetValue(Gh3Checksum.Value("health_poor_medium"), out var low) ? Gh3ScriptRuntime.Numeric(low) : .66;
        var good = _bank.Values.TryGetValue(Gh3Checksum.Value("health_medium_good"), out var high) ? Gh3ScriptRuntime.Numeric(high) : 1.33;
        return _rock * 2 < poor ? "poor" : _rock * 2 > good ? "good" : "medium";
    }
}
