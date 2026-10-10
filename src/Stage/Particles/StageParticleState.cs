using System.Numerics;

namespace Gh3Skin;

internal sealed class StageParticleEmitter
{
    public uint Id { get; init; }
    public uint Parent { get; init; }
    public uint Bone { get; init; }
    public uint Group { get; init; }
    public uint LightGroup { get; set; }
    public StageParticleDefinition Definition { get; init; } = null!;
    public StageParticleLod Lod { get; init; } = null!;
    public StageParticleBounds Bounds { get; } = new();
    public StageParticleVisibility Visibility { get; } = new();
    public Vector3 Wind { get; set; }
    public StageWindField? LocalWind { get; set; }
    public int Seed { get; set; }
    public StageObjectPose Placement { get; set; }
    public Vector2 Speed { get; set; }
    public Vector2 Life { get; set; }
    public Vector2 Width { get; set; }
    public Vector2 Height { get; set; }
    public Vector2 Depth { get; set; }
    public Vector3 Force { get; set; }
    public Vector3 Range { get; set; }
    public float SizeScale { get; set; } = 1;
    public StageParticleEmission Emission { get; init; } = null!;
    public StageParticleShape Shape { get; init; } = null!;
    public float[] RotationSpeeds { get; init; } = Array.Empty<float>();
    public int TextureIndex { get; set; }
    public int Frame { get; set; }
    public long? UpdateScript { get; set; }
    public long? BounceScript { get; set; }
    public double Start { get; init; }
    public float DelayRemaining { get; set; }
    public bool PrewarmPending { get; set; }
    public double Rate { get; set; }
    public int Emitted { get; set; }
    public bool BurstPending { get; set; }
    public bool EmissionStopped { get; set; }
    public bool DestroyWhenEmpty { get; set; }
    public bool Independent { get; set; }
    public bool ParentRemoved { get; set; }
    public StageObjectPose LastPose { get; set; }
    public Vector3 PreviousPosition { get; set; }
    public Vector3 PathPosition { get; set; }
    public int HistoryIndex { get; set; } = 1;
    public Vector4[][] Histories { get; init; } = Array.Empty<Vector4[]>();
    public List<StageParticle> Live { get; } = new();
}

internal sealed record StageParticle(StageParticleEmitter Emitter, double Birth, float Life, Vector3 Origin, Vector3 Velocity,
    Vector3 Force, Vector3 Anchor, float Rotation, float RotationSpeed, int Frame)
{
    public float ColorMix { get; init; }
    public int RandomIndex { get; init; }
    public StageParticleMotion Motion { get; set; } = new(Origin, Velocity);
    public float Age { get; set; }
    public StageParticleColor Color { get; set; }
    public float CurrentRotation { get; set; } = Rotation;
    public Vector4[] History { get; init; } = Array.Empty<Vector4>();
}
internal readonly record struct StageParticleSample(uint Emitter, StageParticleDefinition Definition, Vector3 Position, Vector2 Size, Vector4 Color, float Angle, int Frame, Vector3 Velocity, Vector4[] History, int HistoryIndex,
    float Depth = 0, int RandomIndex = 0, float ColorMix = 0);

internal sealed class StageParticleState
{
    public bool SystemsEnabled { get; private set; } = true;
    public bool LodEnabled { get; private set; }
    public bool JobQueueEnabled { get; private set; }
    public Func<Vector3, StageParticleVisibility, double, (float? Distance, bool Visible)>? View { get; set; }
    private readonly Gh3ScriptRuntime _runtime;
    private readonly Func<int, int> _random;
    private readonly IReadOnlyDictionary<uint, Gh3QbValue> _values;
    private readonly Func<uint, bool> _objectExists;
    private readonly Func<uint, uint, double, StageObjectPose?> _pose;
    private readonly Action<string> _gap;
    private readonly Dictionary<uint, Gh3QbValue> _nodes = new();
    private readonly Dictionary<uint, StageParticleEmitter> _emitters = new();
    private readonly List<StageParticleEmitter> _order = new();
    private int _liveCount;
    private int _unique;
    public IReadOnlyDictionary<uint, StageParticleEmitter> Emitters => _emitters;
    public IReadOnlyList<StageParticle> Particles => _order.SelectMany(emitter => emitter.Live).ToList();
    public int Count => _liveCount;
    public StageWindState Wind { get; }

    public StageParticleState(StageScriptBank bank, Gh3ScriptRuntime runtime, Func<uint, bool> objectExists,
        Func<uint, uint, double, StageObjectPose?> pose, Action<string> gap)
    {
        _runtime = runtime;
        _random = runtime.NextRandom;
        _values = bank.Values;
        _objectExists = objectExists;
        _pose = pose;
        _gap = gap;
        Wind = new(runtime.Time, runtime.FrameDuration);
        foreach (var array in bank.Nodes)
            foreach (var row in bank.Values[Gh3QbText.Key(array)].Items.Where(row => row.Field("Class")?.Integer == Gh3Checksum.Value("ParticleObject")))
            {
                var id = row.Field("Name")?.Integer ?? throw new InvalidDataException("Particle node has no name");
                _nodes.Add(id, Resolve(row));
                if (row.Flag("CreatedAtStart")) CreateNode(id);
            }
    }

    public bool Exists(uint id) => _emitters.ContainsKey(id);
    public Vector3 Position(uint id, double time) => Pose(_emitters[id], time).Position;

    private Gh3QbValue Resolve(Gh3QbValue value, int depth = 0)
    {
        if (depth > 64) throw new InvalidDataException("Particle references contain a cycle");
        if (value.Type is Gh3QbType.Checksum or Gh3QbType.GlobalReference) return Resolve(_values.GetValueOrDefault(value.Integer) ?? throw new InvalidDataException("Particle definition is missing"), depth + 1);
        var fields = new List<Gh3QbField>();
        if (value.Field("ScriptReference") is { } reference) fields.AddRange(Resolve(reference, depth + 1).Fields);
        foreach (var field in value.Fields)
        {
            if (field.Name == 0 && field.Value.Type == Gh3QbType.GlobalReference) fields.AddRange(Resolve(field.Value, depth + 1).Fields);
            else fields.Add(field);
        }
        var last = fields.Select((field, index) => (field.Name, index)).Where(field => field.Name != 0).GroupBy(field => field.Name).ToDictionary(group => group.Key, group => group.Last().index);
        return Gh3ScriptRuntime.Structure(fields.Where((field, index) => field.Name == 0 || last[field.Name] == index));
    }

    private void CreateNode(uint id)
    {
        var data = _nodes[id];
        Create(id, data, data.Field("AttachObject")?.Integer ?? 0, data.Field("Bone")?.Integer ?? 0, 0, null);
    }

    private bool Create(uint id, Gh3QbValue source, uint parent, uint bone, uint group, Vector3? position)
    {
        if (Exists(id) || _objectExists(id)) return false;
        if (_emitters.Count >= StageParticleConstants.MaximumEmitters) throw new InvalidDataException("Too many particle emitters");
        var defaultLightGroup = _values.GetValueOrDefault(Gh3Checksum.Value("default_light_group"))?.Integer ?? 0;
        var definition = new StageParticleDefinition(Resolve(source), defaultLightGroup == 0 ? StageParticleConstants.DefaultLightGroup : defaultLightGroup);
        var textureIndex = _runtime.NextRandom(StageParticleConstants.TextureIndexCount);
        var lodFrame = _runtime.NextRandom(2);
        var seed = _runtime.NextRandom(StageParticleConstants.SeedRange);
        var rotationSpeeds = definition.RotationSpeed == Vector2.Zero ? Array.Empty<float>() : Enumerable.Range(0, definition.Maximum)
            .Select(_ => (_runtime.NextRandom(StageParticleConstants.RandomSteps) * (definition.RotationSpeed.Y - definition.RotationSpeed.X) * StageParticleConstants.RandomScale + definition.RotationSpeed.X) * (2 * MathF.PI)).ToArray();
        var emitter = new StageParticleEmitter { Id = id, Parent = parent, Bone = bone, Group = group, Definition = definition,
            Placement = new(position ?? definition.Position, definition.Rotation, true), Start = _runtime.Time,
            DelayRemaining = (float)definition.Delay, PrewarmPending = definition.TimeSeed > 0,
            Speed = definition.Speed, Life = definition.Life, Width = definition.Width, Height = definition.Height, Depth = definition.Depth,
            Force = definition.Force, Range = definition.Range, SizeScale = definition.SizeScale,
            Rate = definition.Rate, BurstPending = definition.EmitCount > 0, Emission = new(definition.EmitFunction, definition.EmitPeriod),
            Shape = definition.CreateShape(), RotationSpeeds = rotationSpeeds, TextureIndex = textureIndex, Histories = new Vector4[definition.Maximum][], Independent = definition.EmitterIndependent,
            Lod = new(definition.Source, lodFrame), Seed = seed, LightGroup = definition.LightGroup };
        _emitters.Add(id, emitter);
        _order.Add(emitter);
        if (definition.UpdateScript != 0) emitter.UpdateScript = _runtime.CreateComponent(definition.UpdateScript, definition.ScriptParameters, id);
        if (definition.BounceCallback != 0) emitter.BounceScript = _runtime.CreateComponent(definition.BounceCallback, null, id);
        emitter.PreviousPosition = Pose(emitter, _runtime.Time).Position;
        _gap("Particle visibility, paths, wind scheduling, component wait modes, and native material rendering remain partial");
        Schedule(emitter);
        return true;
    }

    private bool Current(StageParticleEmitter emitter) => _emitters.TryGetValue(emitter.Id, out var active) && ReferenceEquals(active, emitter);

    private void Schedule(StageParticleEmitter emitter)
    {
        var next = emitter.Start + ++emitter.Frame * _runtime.FrameDuration;
        _runtime.Schedule(next, () =>
        {
            if (!Current(emitter)) return;
            var position = Pose(emitter, _runtime.Time).Position;
            var view = View?.Invoke(position, emitter.Visibility, _runtime.Time) ?? (null, true);
            emitter.Visibility.Pending |= view.Item2;
            if (!SystemsEnabled) { Schedule(emitter); return; }
            if (emitter.DelayRemaining > 0)
            {
                emitter.DelayRemaining -= (float)_runtime.FrameDuration;
                Schedule(emitter);
                return;
            }
            var elapsed = emitter.Lod.Advance((float)_runtime.FrameDuration, view.Item1, emitter.Visibility.Consume(), LodEnabled, emitter.Emission.Time,
                emitter.Life.Y, (float)emitter.Definition.Lifetime, emitter.EmissionStopped, emitter.BurstPending, emitter.Parent != 0);
            if (emitter.Definition.Lifetime > 0 && emitter.Emission.Time > (float)emitter.Definition.Lifetime && emitter.Rate != 0)
            {
                emitter.Rate = 0;
                emitter.EmissionStopped = true;
                emitter.DestroyWhenEmpty = true;
            }
            if (elapsed == null) { Schedule(emitter); return; }
            var duration = elapsed.Value;
            var displacement = (position - emitter.PreviousPosition) * Math.Max(0, emitter.Definition.Follow);
            emitter.PreviousPosition = position;
            if (emitter.PrewarmPending) { Prewarm(emitter); emitter.PrewarmPending = false; }
            Clean(emitter);
            if (!Current(emitter)) return;
            if (emitter.Parent != 0 && !_objectExists(emitter.Parent))
            {
                if (emitter.UpdateScript is { } task) _runtime.CancelComponent(task);
                emitter.UpdateScript = null;
                emitter.ParentRemoved = true;
                emitter.Rate = 0;
                emitter.EmissionStopped = true;
                emitter.DestroyWhenEmpty = true;
            }
            if (emitter.UpdateScript is { } update) _runtime.UpdateComponent(update);
            if (!Current(emitter)) return;
            if (emitter.Emission.Time != 0 && emitter.Live.Count > 0)
            {
                emitter.Bounds.Update(emitter.Width, emitter.Height);
                emitter.Visibility.Register(emitter.Bounds.Center, emitter.Bounds.Radius);
            }
            else emitter.Bounds.Clear();
            Wind.Advance(_runtime.Time);
            if (JobQueueEnabled)
            {
                emitter.Seed = _runtime.NextRandom(StageParticleConstants.SeedRange);
                emitter.Wind = Wind.Target;
                var selected = emitter.Live.Count > 0 ?
                    Wind.SelectSphere(Reflect(emitter.Bounds.Center), emitter.Bounds.Radius) : Wind.Select(Reflect(position));
                emitter.LocalWind = selected is { Id: not 0 } ? selected with { } : null;
            }
            var wind = emitter.LocalWind;
            var force = emitter.Force + Reflect(emitter.Wind) * emitter.Definition.WindCoefficient;
            if (!emitter.Definition.PerParticleWind && wind != null) force += Reflect(wind.Sample(Reflect(position))) * emitter.Definition.LocalWindCoefficient;
            var count = emitter.Emission.Step(duration, emitter.BurstPending || emitter.EmissionStopped ? 0 : (float)emitter.Rate, emitter.Lod.Scale);
            if (emitter.BurstPending) count = emitter.Definition.EmitCount;
            for (var index = 0; index < count; index++) Emit(emitter);
            if (emitter.BurstPending)
            {
                emitter.BurstPending = false;
                emitter.EmissionStopped = true;
                emitter.DestroyWhenEmpty = true;
                emitter.Rate = 0;
            }
            if (emitter.Live.Count > 0 && emitter.Live[0] is var firstParticle)
            {
                AdvanceHistory(emitter);
                emitter.Bounds.Begin(firstParticle.Motion.Position);
            }
            for (var index = 0; index < emitter.Live.Count; index++)
            {
                var particle = emitter.Live[index];
                if (particle.Age > particle.Life) { RemoveParticle(emitter, index--); continue; }
                var definition = emitter.Definition;
                var particleForce = force;
                if (definition.PerParticleWind && wind != null) particleForce += Reflect(wind.Sample(Reflect(particle.Motion.Position))) * definition.LocalWindCoefficient;
                particle.Color = particle.Color.Step(definition, duration, particle.Life, particle.ColorMix);
                RecordHistory(particle, (particle.Motion.Velocity + particleForce * duration) * duration +
                    (particle.Age == 0 ? Vector3.Zero : displacement));
                particle.Motion = particle.Motion.Step(duration, particleForce, definition.Bounce, definition.BounceHeight, definition.BounceCoefficient, out var collision,
                    particle.Age == 0 ? Vector3.Zero : displacement);
                if (StageParticleMotion.Callback(collision, definition.BounceCallbackRate, emitter.BounceScript != null, _random))
                {
                    _runtime.UpdateComponent(emitter.BounceScript!.Value);
                    if (!Current(emitter)) return;
                }
                if (particle.RotationSpeed != 0 && particle.Life > 0)
                    particle.CurrentRotation += ((definition.RotationTimeScale * particle.RotationSpeed - particle.RotationSpeed) * (particle.Age / particle.Life) + particle.RotationSpeed) * duration;
                particle.Age += duration;
                emitter.Bounds.Include(particle.Motion.Position);
            }
            if (duration > 0) emitter.PathPosition = position;
            if (Current(emitter)) Schedule(emitter);
        }, emitter.Id);
    }

    private StageObjectPose Pose(StageParticleEmitter emitter, double time)
    {
        if (emitter.Parent == 0) return emitter.Placement;
        if (emitter.ParentRemoved || !_objectExists(emitter.Parent)) return emitter.LastPose;
        if (_pose(emitter.Parent, emitter.Bone, time) is not { } parent)
        {
            _gap("Particle attachment is missing: " + Gh3QbText.Key(emitter.Parent) + "/" + Gh3QbText.Key(emitter.Bone));
            return emitter.LastPose;
        }
        var local = emitter.Placement;
        return emitter.LastPose = new(parent.Position + Vector3.Transform(local.Position * parent.Scale, parent.Rotation),
            Quaternion.Normalize(parent.Rotation * local.Rotation), parent.Visible) { Scale = parent.Scale, Velocity = parent.Velocity };
    }

    private static Vector3 Reflect(Vector3 value) => new(value.X, value.Y, -value.Z);

    private void Prewarm(StageParticleEmitter emitter)
    {
        var duration = emitter.Definition.TimeSeed;
        var count = (int)((float)emitter.Rate * duration * (emitter.Emission.Accumulator + duration));
        if (count != 0)
        {
            for (var index = 0; index < Math.Min(count, emitter.Definition.Maximum); index++) Emit(emitter);
            foreach (var particle in emitter.Live)
            {
                if (particle.Age == 0)
                    // GH3: TimeSeed draws CRT rand() / 32767 (3.0518509e-05 as float32).
                    particle.Age = particle.Life * Math.Min(duration / particle.Life, 1) * (_runtime.NextCrtRandom() * 3.0518509447574615e-05f);
                else
                {
                    var phase = (particle.Age + duration) / particle.Life;
                    particle.Age = phase - (int)phase;
                }
            }
        }
        AdvanceHistory(emitter);
        foreach (var particle in emitter.Live)
        {
            particle.Motion = particle.Motion.Step(particle.Age, emitter.Force, false, 0, 0);
            RecordHistory(particle, particle.Motion.Velocity * particle.Age);
        }
    }

    private static void AdvanceHistory(StageParticleEmitter emitter)
    {
        var length = emitter.Definition.History;
        emitter.HistoryIndex = length == 0 ? 0 : (emitter.HistoryIndex + length - 1) % length;
    }

    private static void RecordHistory(StageParticle particle, Vector3 displacement)
    {
        if (particle.History.Length > 0) particle.History[particle.Emitter.HistoryIndex] = StageParticleGeometry.History(displacement);
    }

    private void Emit(StageParticleEmitter emitter)
    {
        var d = emitter.Definition;
        if (emitter.EmissionStopped) return;
        var slot = emitter.Live.Count;
        if (slot >= d.Maximum) return;
        if (_liveCount >= StageLimits.MaximumCount) throw new InvalidDataException("Too many live particles");
        var pose = Pose(emitter, _runtime.Time);
        var path = emitter.PathPosition - pose.Position;
        var pathLength = path.Y * path.Y + path.Z * path.Z + path.X * path.X;
        var birth = emitter.Shape.Emit(emitter.Range, emitter.Speed, emitter.Life, _random, Reflect(pose.Velocity), d.EmitterVelocityWeight, d.FillPath && pathLength != 0);
        var history = emitter.Histories[slot] ??= new Vector4[d.History];
        Array.Clear(history, 0, history.Length);
        var particle = new StageParticle(emitter, _runtime.Time, birth.Life, pose.Position + (Vector3.Transform(birth.Position * pose.Scale, pose.Rotation) + path * birth.PathFraction),
            Vector3.Transform(birth.Velocity, pose.Rotation), emitter.Force, pose.Position, birth.Rotation,
            emitter.RotationSpeeds.Length == 0 ? 0 : emitter.RotationSpeeds[slot], emitter.Emitted) {
                ColorMix = birth.ColorMix, RandomIndex = emitter.TextureIndex, History = history
            };
        emitter.TextureIndex = (emitter.TextureIndex + 1) & (StageParticleConstants.TextureIndexCount - 1);
        particle.Color = new(0, 0, d.ColorAt(0, particle.ColorMix));
        emitter.Live.Add(particle);
        _liveCount++;
        emitter.Emitted++;
    }

    private void Clean(StageParticleEmitter emitter)
    {
        if (emitter.DestroyWhenEmpty && emitter.Rate == 0 && !emitter.BurstPending && emitter.Live.Count == 0) Remove(emitter);
    }

    private void Remove(StageParticleEmitter emitter)
    {
        _emitters.Remove(emitter.Id);
        _order.Remove(emitter);
        Empty(emitter);
        _runtime.RemoveObject(emitter.Id);
    }

    private void Empty(StageParticleEmitter emitter)
    {
        _liveCount -= emitter.Live.Count;
        emitter.Live.Clear();
    }

    private void RemoveParticle(StageParticleEmitter emitter, int index, bool copyHistory = true)
    {
        var live = emitter.Live;
        var last = live.Count - 1;
        if (emitter.RotationSpeeds.Length > 0) emitter.RotationSpeeds[index] = live[last].RotationSpeed;
        var history = live[index].History;
        if (copyHistory) live[last].History.CopyTo(history, 0);
        live[index] = live[last] with { History = history };
        live.RemoveAt(last);
        _liveCount--;
    }

    public IEnumerable<StageParticleSample> Sample(double time) => _order.SelectMany(emitter => emitter.Live).Select(Sample);

    public static StageParticleSample Sample(StageParticle particle)
    {
        var age = particle.Age;
        var emitter = particle.Emitter;
        var d = emitter.Definition;
        var phase = particle.Life > 0 ? age / particle.Life : 1;
        return new(emitter.Id, d, particle.Motion.Position, new Vector2(emitter.Width.X + (emitter.Width.Y - emitter.Width.X) * phase,
            emitter.Height.X + (emitter.Height.Y - emitter.Height.X) * phase),
            particle.Color.Value, emitter.RotationSpeeds.Length == 0 ? 0 : particle.CurrentRotation, particle.Frame + (int)(age * (float)d.Fps), particle.Motion.Velocity,
            particle.History, emitter.HistoryIndex, emitter.Depth.X + (emitter.Depth.Y - emitter.Depth.X) * phase, particle.RandomIndex, particle.ColorMix);
    }

    public Gh3ScriptCallResult? Execute(Gh3ScriptContext context, uint command, Gh3QbValue parameters)
    {
        bool Is(string name) => command == Gh3Checksum.Value(name);
        if (Is("EngineConfig") || Is("EngineConfiguration"))
        {
            foreach (var field in parameters.Fields)
            {
                if (field.Name == Gh3Checksum.Value("ParticleSystem")) SystemsEnabled = Gh3ScriptRuntime.Numeric(field.Value) != 0;
                else if (field.Name == Gh3Checksum.Value("ParticleLOD")) LodEnabled = Gh3ScriptRuntime.Numeric(field.Value) != 0;
                else if (field.Name == Gh3Checksum.Value("JobQParticle")) JobQueueEnabled = Gh3ScriptRuntime.Numeric(field.Value) != 0;
                else _gap("Engine configuration is not implemented: " + Gh3QbText.Key(field.Name));
            }
            return new(true);
        }
        if (Is("EngineWind"))
        {
            Wind.Advance(_runtime.Time);
            return new(Wind.Execute(parameters));
        }
        var target = parameters.Field("Name") ?? parameters.Field("nodeName") ?? parameters.Field("id") ?? parameters.Fields.FirstOrDefault(field => field.Name == 0 && field.Value.Type == Gh3QbType.Checksum)?.Value;
        if (Is("GetUniqueCompositeObjectID"))
        {
            var id = parameters.Field("preferredID")?.Integer ?? 0;
            for (var attempt = 0; id == 0 || Exists(id) || _objectExists(id); attempt++)
            {
                if (attempt >= 1000) throw new InvalidDataException("No free composite object ID");
                id = Gh3Checksum.Value("unique_composite_object_id_" + _unique.ToString("D3", System.Globalization.CultureInfo.InvariantCulture));
                _unique = (_unique + 1) % 1000;
            }
            return new(true, new[] { new Gh3QbField(Gh3Checksum.Value("uniqueID"), new(Gh3QbType.Checksum, -1, id)) });
        }
        if (Is("CreateParticleSystem") || Is("CreateParticleSystem_Fast"))
        {
            var name = parameters.Field("Name")?.Integer ?? throw new InvalidDataException("Particle creation needs a name");
            var source = parameters.Field("params_script") ?? throw new InvalidDataException("Particle creation needs parameters");
            var parent = parameters.Field("ObjID")?.Integer ?? 0;
            var id = unchecked(name + parent);
            var positioned = parameters.Field("Pos") != null;
            return new(Create(id, source, positioned ? 0 : parent, parameters.Field("Bone")?.Integer ?? 0,
                parameters.Field("groupID")?.Integer ?? 0, positioned ? StageParticleDefinition.Vector(parameters, "Pos", Vector3.Zero) : null));
        }
        var objectId = target?.Integer ?? context.Object;
        if (Is("SafeKill") && _nodes.ContainsKey(objectId) && !Exists(objectId)) return new(true);
        if ((Is("Create") || Is("SafeCreate")) && _nodes.ContainsKey(objectId))
        {
            if (!Exists(objectId)) CreateNode(objectId);
            return new(true);
        }
        if (!_emitters.TryGetValue(objectId, out var emitter)) return null;
        if (Is("SetLODParams")) { emitter.Lod.Set(parameters); return new(true); }
        if (Is("LightGroup"))
        {
            if (parameters.Field("group") is { Type: Gh3QbType.Checksum } lightGroup) emitter.LightGroup = lightGroup.Integer;
            return new(true);
        }
        if (emitter.Shape.Execute(command, parameters)) return new(true);
        float Number(string name, float fallback = 0) => (float)StageParticleDefinition.Number(parameters, name, fallback, -StageLimits.MaximumMagnitude, StageLimits.MaximumMagnitude);
        Vector3 Vector(string name) => parameters.Field(name) != null ? StageParticleDefinition.Vector(parameters, name, Vector3.Zero) : new(Number("x"), Number("y"), -Number("z"));
        if (Is("SetPos")) { emitter.Placement = emitter.Placement with { Position = Vector("pos") }; return new(true); }
        if (Is("SetEmitRange")) { emitter.Range = new(Number("width"), Number("height", Number("width")), Number("depth")); return new(true); }
        if (Is("SetSpeedRange")) { emitter.Speed = new(Number("min"), Number("max", Number("min"))); return new(true); }
        if (Is("SetLife")) { emitter.Life = new(Number("min"), Number("max", Number("min"))); return new(true); }
        if (Is("SetParticleSizeScale")) { emitter.SizeScale = Number("scalesize", 1); return new(true); }
        if (Is("SetParticleSize"))
        {
            emitter.Width = new(Number("sw", emitter.Width.X), Number("ew", emitter.Width.Y));
            emitter.Height = new(Number("sh", emitter.Height.X), Number("eh", emitter.Height.Y));
            emitter.Depth = new(Number("sd", emitter.Depth.X), Number("ed", emitter.Depth.Y));
            return new(true);
        }
        if (Is("SetForce"))
        {
            emitter.Force = Vector("force");
            return new(true);
        }
        if (Is("SetEmitterIndependent"))
        {
            emitter.Independent = parameters.Fields.FirstOrDefault(field => field.Name == 0 && field.Value.Type == Gh3QbType.Integer)?.Value.Integer == 1;
            return new(true);
        }
        if (Is("Emit"))
        {
            var count = (int)StageParticleDefinition.Number(parameters, "num", 1, 0, StageLimits.MaximumMagnitude);
            for (var index = 0; index < count; index++) Emit(emitter);
            return new(true);
        }
        if (Is("KillOldParticles"))
        {
            if (parameters.Field("time") == null) return new(true);
            var maximumAge = Number("time");
            for (var index = 0; index < emitter.Live.Count; index++)
            {
                if (emitter.Live[index].Age <= maximumAge) continue;
                // The native loop skips the last particle that it moves into this slot.
                RemoveParticle(emitter, index, false);
            }
            Clean(emitter);
            return new(true);
        }
        if (Is("EmitRate"))
        {
            emitter.Rate = StageParticleDefinition.Number(parameters, "rate", 1, 0, StageLimits.MaximumMagnitude);
            return new(true);
        }
        if (Is("Destroy") || Is("Kill") || Is("SafeKill") || Is("Die"))
        {
            if (parameters.Field("ifEmpty") is { } empty && Gh3ScriptRuntime.Truth(empty))
            {
                emitter.DestroyWhenEmpty = true;
                Clean(emitter);
            }
            else Remove(emitter);
            return new(true);
        }
        if (Is("EmptySystem"))
        {
            Empty(emitter);
            Clean(emitter);
            return new(true);
        }
        return null;
    }

}
