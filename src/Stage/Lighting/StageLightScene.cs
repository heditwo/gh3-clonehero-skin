using System.Numerics;

namespace Gh3Skin;

internal sealed record StageLightGroup(uint Flags, float Weight);
internal sealed record StageLightSceneSample(int[] Candidates, StageParticleLightSet Lighting);
internal readonly record struct StageLightTransform(Vector4 Position, Vector4 Direction, Vector3 BoundsPosition);

internal sealed class StageLightScene
{
    private readonly StageLightSpatial _spatial = new();
    private readonly Dictionary<uint, StageLightGroup> _groups = new() { [0x8aebf027] = new(3, 1) };
    private readonly Dictionary<uint, int> _indices;
    private readonly uint[] _flags;
    private readonly uint[] _lightGroups;
    private readonly StageLightTransition[] _transitions;
    private readonly bool[] _changed;
    private readonly StageLightTransform[] _transforms;
    private readonly bool[] _transformChanged;
    private readonly Gh3ScriptRuntime? _runtime;
    private bool _scheduled;
    private int _version;
    public IReadOnlyList<StageLightNative> Lights { get; }
    public IReadOnlyDictionary<uint, StageLightGroup> Groups => _groups;
    // GH3 lights exist only as LevelLight nodes, and every light command addresses one of them or its group.
    // Without such a node the scene can light nothing, so the stage's own lights (lighting.json) apply.
    public bool DrivesLighting => Lights.Count > 0;

    public StageLightScene(IEnumerable<Gh3QbValue> nodes, Gh3ScriptRuntime? runtime = null, StageLighting? lighting = null, Gh3QbValue? snapshots = null)
    {
        _runtime = runtime;
        Lights = nodes.Where(node => node.Field("Class")?.Integer == Gh3Checksum.Value("LevelLight")).Select(node => new StageLightNative(node)).ToArray();
        _indices = Lights.Select((light, index) => (light.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index);
        _flags = Lights.Select(light => light.Flags).ToArray();
        _lightGroups = Lights.Select(light => light.Group).ToArray();
        _transitions = Lights.Select(light => new StageLightTransition(new(light.Color, light.Intensity, light.Specular, light.SpecularPower))).ToArray();
        _changed = new bool[Lights.Count];
        _transforms = Lights.Select(light => new StageLightTransform(light.Position, light.Direction, light.BoundsPosition)).ToArray();
        _transformChanged = new bool[Lights.Count];
        for (var i = 0; i < Lights.Count; i++)
        {
            var light = Lights[i];
            _groups.TryAdd(light.Group, new(light.Flags, 1));
            Register(i);
        }
        if (lighting != null && runtime != null)
        {
            var map = lighting.Bank.Lights.Select(light => light.Object.Length > 0 && _indices.TryGetValue(Gh3QbText.Key(light.Object), out var index) ? index : -1).ToArray();
            var initial = lighting.At(runtime.Time).Sample(runtime.Time);
            for (var i = 0; i < map.Length; i++)
                if (map[i] >= 0) _transitions[map[i]] = new(Channels(initial.Lights[i], Lights[map[i]].Color.W));
            lighting.SnapshotPlayed = (name, time, blend, values, changed, appended) => {
                var snapshotId = uint.TryParse(name, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var key) ? key : Gh3QbText.Key(name);
                var rows = snapshots?.Field(snapshotId)?.Items;
                for (var i = 0; i < map.Length; i++)
                {
                    var index = map[i];
                    if (index < 0 || !changed[i]) continue;
                    var previous = _transitions[index].Value;
                    var target = Channels(values[i], previous.Color.W) with { Shadow = previous.Shadow };
                    var source = rows?.FirstOrDefault(row => row.Field("LightChecksum")?.Integer == Lights[index].Id);
                    if (source != null && source.Field("SpecularIntensity") == null && appended.Field("SpecularIntensity") == null)
                        target = target with { Specular = previous.Specular };
                    if ((appended.Field("ShadowIntensity") ?? source?.Field("ShadowIntensity")) is { Type: 1 or 2 } shadow)
                        target = target with { Shadow = (float)Gh3ScriptRuntime.Numeric(shadow) };
                    _changed[index] = true;
                    _transitions[index].Begin(target, (float)blend, Clock(time));
                }
                Schedule();
            };
        }
    }

    private static StageLightChannels Channels(StageLightValue value, float alpha) => new(new(value.Color, alpha), value.Intensity, value.Specular, value.SpecularPower);
    private static uint Clock(double time) => unchecked((uint)(long)Math.Truncate(time * 1000));

    private void Schedule()
    {
        if (_runtime == null || _scheduled || !_transitions.Where((_, i) => _changed[i]).Any(value => !value.Complete)) return;
        _scheduled = true;
        _runtime.Schedule(_runtime.Time + _runtime.FrameDuration, () => {
            _scheduled = false;
            Update(Clock(_runtime.Time));
            Schedule();
        }, id: Gh3Checksum.Value("gh3_light_components"));
    }

    public void Update(uint milliseconds)
    {
        for (var i = 0; i < _transitions.Length; i++)
            if (_changed[i] && !_transitions[i].Complete) _transitions[i].Update(milliseconds);
    }

    public StageLightChannels Channels(uint id) => _transitions[_indices[id]].Value;
    public StageLightTransform Transform(uint id) => _transforms[_indices[id]];

    public StageLightPose? RenderPose(uint id)
    {
        if (!_indices.TryGetValue(id, out var index) || !_transformChanged[index]) return null;
        var transform = _transforms[index];
        return new(new(transform.Position.X, transform.Position.Y, -transform.Position.Z), new(transform.Direction.X, transform.Direction.Y, -transform.Direction.Z));
    }

    private void Register(int index)
    {
        var light = Lights[index];
        if ((_flags[index] & 4) != 0) _spatial.AddUnbounded(index);
        else _spatial.Add(index, _transforms[index].BoundsPosition - light.BoundsExtent, _transforms[index].BoundsPosition + light.BoundsExtent);
    }

    public uint Flags(uint id) => _indices.TryGetValue(id, out var index) ? _flags[index] : 0;
    public uint Group(uint id) => _indices.TryGetValue(id, out var index) ? _lightGroups[index] : 0;
    public bool Contains(uint id) => _indices.ContainsKey(id);

    public void Enable(uint id, bool enabled)
    {
        if (_indices.TryGetValue(id, out var index)) _flags[index] = enabled ? _flags[index] | 2 : _flags[index] & ~2u;
    }

    public StageLightValue Apply(uint id, StageLightValue value)
    {
        if (!_indices.TryGetValue(id, out var index)) return value;
        var group = _groups[_lightGroups[index]];
        var weight = (_flags[index] & group.Flags & 2) != 0 ? group.Weight : 0;
        if (_changed[index])
        {
            var channels = _transitions[index].Value;
            value = value with { Color = new(channels.Color.X, channels.Color.Y, channels.Color.Z), Intensity = channels.Intensity,
                Specular = channels.Specular, SpecularPower = channels.Shadow };
        }
        return value with { Intensity = value.Intensity * weight, Specular = value.Specular * weight };
    }

    public long Signature()
    {
        var hash = new HashCode();
        hash.Add(_version);
        for (var i = 0; i < _transitions.Length; i++)
        {
            hash.Add(_transitions[i].Value);
            hash.Add(_transforms[i]);
            hash.Add(_flags[i]);
            hash.Add(_lightGroups[i]);
        }
        foreach (var (id, group) in _groups) { hash.Add(id); hash.Add(group.Flags); hash.Add(group.Weight); }
        return hash.ToHashCode();
    }

    public Gh3ScriptCallResult? Execute(uint command, Gh3QbValue parameters, uint milliseconds = 0)
    {
        var result = ExecuteCommand(command, parameters, milliseconds);
        if (result != null) _version++;
        return result;
    }

    private Gh3ScriptCallResult? ExecuteCommand(uint command, Gh3QbValue parameters, uint milliseconds)
    {
        bool Is(string name) => command == Gh3Checksum.Value(name);
        if (Is("CompactIntervals")) { _spatial.Compact(); return new(true); }
        if (Is("PushWorldLights") || Is("PopWorldLights"))
        {
            foreach (var group in _groups.Keys.ToArray())
                _groups[group] = _groups[group] with { Flags = Is("PopWorldLights") ? _groups[group].Flags | 2 : _groups[group].Flags & ~2u };
            return new(true);
        }
        var id = parameters.Field("name")?.Integer ?? 0;
        if (Is("MoveLight") || Is("SetLightDirection") || Is("GetLightDirection"))
        {
            if (!_indices.TryGetValue(id, out var index)) return new(false);
            var transform = _transforms[index];
            if (Is("GetLightDirection")) return new(true, new[] { new Gh3QbField(0xc1b52e4c,
                new Gh3QbValue(Gh3QbType.Vector, -1, new[] { transform.Direction.X, transform.Direction.Y, transform.Direction.Z })) });
            if (Is("MoveLight"))
            {
                if (parameters.Field("pos")?.Data is not float[] { Length: 3 } position) throw new InvalidDataException("MoveLight requires a pos vector");
                var location = new Vector3(position[0], position[1], position[2]);
                var offset = transform.BoundsPosition - new Vector3(transform.Position.X, transform.Position.Y, transform.Position.Z);
                transform = transform with { Position = new(location, 1), BoundsPosition = location + offset };
            }
            else
            {
                float Angle(string name) => parameters.Field(name) is { Type: 1 or 2 } value ? (float)Gh3ScriptRuntime.Numeric(value) * .01745329238474369f : 0;
                var heading = Angle("heading");
                var pitch = Angle("pitch");
                transform = transform with { Direction = new(-(float)Math.Sin(heading) * (float)Math.Cos(pitch), -(float)Math.Sin(pitch),
                    -(float)Math.Cos(heading) * (float)Math.Cos(pitch), 0) };
            }
            _transforms[index] = transform;
            _transformChanged[index] = true;
            return new(true);
        }
        if (Is("SetLightColor") || Is("SetLightIntensity") || Is("SetLightSpecIntensity") || Is("SetLightShadowIntensity"))
        {
            if (!_indices.TryGetValue(id, out var index)) return new(false);
            _changed[index] = true;
            _transitions[index].Begin(parameters, _runtime == null ? milliseconds : Clock(_runtime.Time));
            Schedule();
            return new(true);
        }
        if (Is("GetLightColor") || Is("GetLightIntensity") || Is("GetLightSpecIntensity") || Is("GetLightShadowIntensity"))
        {
            if (!_indices.TryGetValue(id, out var index)) return new(false);
            var value = _transitions[index].Value;
            if (!Is("GetLightColor")) return new(true, new[] { new Gh3QbField(Gh3Checksum.Value("i"), Gh3ScriptRuntime.Number(
                Is("GetLightIntensity") ? value.Intensity : Is("GetLightSpecIntensity") ? value.Specular : value.Shadow)) });
            return new(true, new[] { ("r", value.Color.X), ("g", value.Color.Y), ("b", value.Color.Z) }.Select(channel =>
                new Gh3QbField(Gh3Checksum.Value(channel.Item1), Gh3ScriptRuntime.Number(unchecked((byte)(int)(channel.Item2 * 255))))).ToArray());
        }
        if (Is("SetDynamicLightModulationFactor")) return new(true);
        if (Is("GetDynamicLightModulationFactor")) return new(true, new[] {
            new Gh3QbField(0xf2bb5523, Gh3ScriptRuntime.Number(0)), new Gh3QbField(0x7fb43512, Gh3ScriptRuntime.Number(0)) });
        if (Is("CreateLightGroup"))
        {
            if (id != 0 && _groups.ContainsKey(id)) return new(false);
            var weight = parameters.Field("Brightness") is { Type: 1 or 2 } value ? (float)Gh3ScriptRuntime.Numeric(value) : 100;
            var flags = _groups.TryGetValue(id, out var previous) ? previous.Flags : 3u;
            if (!parameters.Flag("CreatedAtStart")) flags &= ~2u;
            if (!parameters.Flag("Global")) flags &= ~1u;
            _groups[id] = new(flags, weight * .01f);
            return new(true);
        }
        if (Is("EnableLightGroup") || Is("DisableLightGroup") || Is("SetLightGroupIntensity") || Is("GetLightGroupIntensity"))
        {
            if (id == 0 || !_groups.TryGetValue(id, out var group)) return new(false);
            if (Is("GetLightGroupIntensity")) return new(true, new[] { new Gh3QbField(Gh3Checksum.Value("i"), Gh3ScriptRuntime.Number(group.Weight)) });
            if (Is("SetLightGroupIntensity"))
                _groups[id] = group with { Weight = parameters.Field("i") is { Type: 1 or 2 } value ? (float)Gh3ScriptRuntime.Numeric(value) : 1 };
            else _groups[id] = group with { Flags = Is("EnableLightGroup") ? group.Flags | 2 : group.Flags & ~2u };
            return new(true);
        }
        if (Is("EnableLight") || Is("DisableLight") || Is("SetLightFlag") || Is("ClearLightFlag") || Is("AddLightToGroup"))
        {
            if (!_indices.TryGetValue(id, out var index)) return new(false);
            if (Is("AddLightToGroup"))
            {
                var group = parameters.Field("LightGroup")?.Integer ?? 0;
                if (group == 0 || !_groups.ContainsKey(group)) return new(false);
                _lightGroups[index] = group;
                return new(true);
            }
            if (Is("EnableLight") || Is("DisableLight")) { Enable(id, Is("EnableLight")); return new(true); }
            var mask = parameters.Field("flag")?.Integer switch { 0x17fa747c => 1u, 0xaf06447b => 2u, 0xb454f54b => 4u, 0x8a897dd2 => 8u, 0x366727cb => 0x1000000u, _ => 0u };
            var flags = Is("SetLightFlag") ? _flags[index] | mask : _flags[index] & ~mask;
            var moved = ((_flags[index] ^ flags) & 4) != 0;
            _flags[index] = flags;
            if (moved) { _spatial.Remove(index); Register(index); }
            return new(true);
        }
        return null;
    }

    private readonly List<int> _query = new();
    private StageParticleLight?[] _records = Array.Empty<StageParticleLight?>();

    private StageParticleLight Record(int index)
    {
        if (_records.Length != Lights.Count) _records = new StageParticleLight?[Lights.Count];
        var light = Lights[index];
        var value = _transitions[index].Value;
        var transform = _transforms[index];
        var weight = _groups[_lightGroups[index]].Weight;
        var cached = _records[index];
        if (cached != null && cached.Color == value.Color && cached.Position == transform.Position && cached.Direction == transform.Direction &&
            cached.Intensity == value.Intensity && cached.Weight == weight && cached.SpecularPower == value.Shadow && cached.Flags == _flags[index])
            return cached;
        return _records[index] = new StageParticleLight(light.Type, value.Color, transform.Position, transform.Direction, value.Intensity,
            weight, value.Shadow, light.Attenuation.X, light.Attenuation.Y, _flags[index], light.Harmonics);
    }

    public StageLightSceneSample Sample(Vector3 position, uint group)
    {
        _spatial.QueryInto(position, _query);
        var count = 0;
        foreach (var index in _query)
            if (StageParticleLights.Accepts(group, _lightGroups[index], _groups[_lightGroups[index]].Flags, _flags[index]) && ++count == 32) break;
        var candidates = new int[count];
        var lights = new StageParticleLight[count];
        count = 0;
        foreach (var index in _query)
        {
            if (count == candidates.Length) break;
            if (!StageParticleLights.Accepts(group, _lightGroups[index], _groups[_lightGroups[index]].Flags, _flags[index])) continue;
            candidates[count] = index;
            lights[count++] = Record(index);
        }
        var selected = StageParticleLights.Select(lights, new(position, 1));
        if ((selected.Flags & 7) != 0)
        {
            var indices = new int[selected.Indices.Length];
            for (var i = 0; i < indices.Length; i++) indices[i] = candidates[selected.Indices[i]];
            selected = selected with { Indices = indices };
        }
        return new(candidates, selected);
    }
}
