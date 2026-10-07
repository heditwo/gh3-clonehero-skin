using System.IO.Compression;
using System.Numerics;
using System.Text.Json;

namespace Gh3Skin;

internal sealed class PerformanceClipInfo
{
    public float Duration { get; set; }
    public int Frames { get; set; }
    public float RootDistance { get; set; }
}

internal sealed class PerformanceMember
{
    public string[] Bones { get; set; } = Array.Empty<string>();
    public Dictionary<string, string[]> Groups { get; set; } = new();
    public Dictionary<string, string[]> Layers { get; set; } = new();
    public Dictionary<string, PerformanceClipInfo> Clips { get; set; } = new();
}

internal sealed class StagePerformance
{
    public int Version { get; set; }
    public Dictionary<string, PerformanceMember> Members { get; set; } = new();
    public Dictionary<string, float> Constants { get; set; } = new();
    public Dictionary<string, float[]> Waypoints { get; set; } = new();

    public static StagePerformance? Load(string path)
    {
        if (!File.Exists(path)) return null;
        var bank = JsonSerializer.Deserialize<StagePerformance>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (bank?.Version != 1 || bank.Members.Count is < 1 or > 4)
            throw new InvalidDataException("Invalid original performance library");
        foreach (var member in bank.Members.Values)
        {
            if (member.Bones.Length is < 1 or > 255 || member.Clips.Count > 5000 ||
                member.Clips.Any(c => c.Key.Length != 8 || c.Key.Any(x => !Uri.IsHexDigit(x)) ||
                    !float.IsFinite(c.Value.Duration) || c.Value.Duration is <= 0 or > 600 || c.Value.Frames is < 2 or > 18001) ||
                member.Groups.Values.Concat(member.Layers.Values).SelectMany(c => c).Any(c => !member.Clips.ContainsKey(c)))
                throw new InvalidDataException("Invalid original performance clips");
        }
        return bank;
    }
}

internal sealed class PerformanceClip
{
    public float Duration { get; }
    private readonly int _frames;
    private readonly int[] _bones;
    private readonly int[] _masks;
    private readonly BandPose[] _poses;

    public PerformanceClip(Stream source, int bones)
    {
        using var zip = new GZipStream(source, CompressionMode.Decompress);
        using var reader = new BinaryReader(zip);
        if (System.Text.Encoding.ASCII.GetString(reader.ReadBytes(8)) != "GH3CLP01" || reader.ReadInt32() != bones)
            throw new InvalidDataException("Invalid performance clip header");
        _frames = reader.ReadInt32();
        var count = reader.ReadInt32();
        Duration = reader.ReadSingle();
        if (_frames is < 2 or > 18001 || count < 1 || count > bones || !float.IsFinite(Duration) || Duration is <= 0 or > 600)
            throw new InvalidDataException("Invalid performance clip size");
        _bones = new int[count];
        _masks = new int[count];
        for (var i = 0; i < count; i++)
        {
            _bones[i] = reader.ReadInt32();
            _masks[i] = reader.ReadInt32();
            if (_bones[i] < 0 || _bones[i] >= bones || _masks[i] is < 1 or > 3)
                throw new InvalidDataException("Invalid performance bone mask");
        }
        if (_bones.Distinct().Count() != count) throw new InvalidDataException("Duplicate performance bones");
        _poses = new BandPose[_frames * count];
        for (var i = 0; i < _poses.Length; i++)
        {
            var p = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var q = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            if (!float.IsFinite(p.LengthSquared()) || !float.IsFinite(q.LengthSquared()) || Math.Abs(q.LengthSquared() - 1) > 0.02)
                throw new InvalidDataException("Invalid performance pose");
            _poses[i] = new(p, Quaternion.Normalize(q));
        }
        if (reader.BaseStream.ReadByte() != -1) throw new InvalidDataException("Extra performance clip data");
    }

    public void Apply(BandPose[] output, BandPose[] bind, double time, bool additive = false, float weight = 1)
    {
        var frame = Math.Clamp(time / Duration * (_frames - 1), 0, _frames - 1);
        var a = (int)frame;
        var b = Math.Min(a + 1, _frames - 1);
        for (var i = 0; i < _bones.Length; i++)
        {
            var bone = _bones[i];
            var pose = BandPose.Mix(_poses[a * _bones.Length + i], _poses[b * _bones.Length + i], (float)(frame - a));
            var current = output[bone];
            var position = (_masks[i] & 1) == 0 ? current.Position : additive ? current.Position + pose.Position - bind[bone].Position : pose.Position;
            var rotation = (_masks[i] & 2) == 0 ? current.Rotation : additive ? Quaternion.Normalize(pose.Rotation * current.Rotation) : pose.Rotation;
            output[bone] = BandPose.Mix(current, new(position, rotation), weight);
        }
    }
}

internal sealed class PerformancePlayer
{
    internal sealed record Segment(double Time, double End, string Clip, double Rate, string Stance, string Action, int DisableArms);
    private readonly string _role;
    private readonly string _directory;
    private readonly PerformanceMember _member;
    private readonly BandPose[] _bind;
    private readonly BandPose[] _previous;
    private readonly StageTimeline _timeline;
    private readonly Dictionary<string, PerformanceClip> _clips = new();
    private readonly List<Segment> _segments = new();
    private readonly StageCue[] _commands;
    private readonly StageCue[] _frets;
    private readonly StageCue[][] _drumTracks;
    private int _commandIndex;
    private string _stance = "stance_a";
    private string _action = "idle";
    private bool _loop = true;
    private int _repeat = 1;
    private int _disableArms;
    private uint _seed;
    public BandPose[] Pose { get; }
    public string CurrentClip { get; private set; } = "";
    public string CurrentStance { get; private set; } = "stance_a";
    public bool ArmsEnabled { get; private set; } = true;
    public bool StrumEnabled { get; private set; } = true;

    public PerformancePlayer(string role, string directory, PerformanceMember member, BandPose[] bind, StageTimeline timeline)
    {
        _role = role;
        _directory = directory;
        _member = member;
        _bind = bind;
        _timeline = timeline;
        Pose = new BandPose[bind.Length];
        _previous = new BandPose[bind.Length];
        if (member.Bones.Length != bind.Length) throw new InvalidDataException("Performance skeleton mismatch");
        _seed = Convert.ToUInt32(Gh3Checksum.Of(role), 16);
        _commands = timeline.PlaybackCues.Where(c => c.Role == role && c.Type is "stance" or "perform" or "clip").ToArray();
        _frets = timeline.Track("fret").Where(c => int.TryParse(c.Value, out var note) &&
            (role == "guitarist" ? note is >= 118 and <= 127 : note is >= 101 and <= 110)).ToArray();
        var drums = timeline.Track("drum");
        _drumTracks = new[] { new[] { 36, 48 }, Enumerable.Range(37, 9).ToArray(), Enumerable.Range(49, 9).ToArray(),
            new[] { 44, 56 }, new[] { 45, 57 }, new[] { 43, 55 }, new[] { 41, 42, 53, 54 } }
            .Select(pitches => drums.Where(c => int.TryParse(c.Value, out var note) && pitches.Contains(note)).ToArray()).ToArray();
    }

    public Segment At(double time)
    {
        if (!double.IsFinite(time) || time > 86400) throw new ArgumentOutOfRangeException(nameof(time));
        time = Math.Max(0, time);
        while ((_segments.Count == 0 || _segments[^1].End <= time + 3) && _segments.Count < 100000) AddSegment();
        return _segments[Math.Max(0, StageTimeline.UpperBound(_segments, time, s => s.Time) - 1)];
    }

    public void Prepare()
    {
        var end = Math.Min(3600, Math.Max(60, _timeline.Cues.LastOrDefault()?.Time ?? 0));
        _ = At(end);
        var needed = _segments.Select(s => s.Clip).Concat(_member.Layers.Values.SelectMany(c => c))
            .Concat(_timeline.Track("face", _role).Select(c => c.Value)).Where(_member.Clips.ContainsKey).Distinct();
        foreach (var clip in needed) _ = Clip(clip);
    }

    public void Sample(double time, double walkAge = -1, float walkWeight = 1)
    {
        time = Math.Max(0, time);
        var segment = At(time);
        CurrentClip = segment.Clip;
        CurrentStance = segment.Stance;
        Array.Copy(_bind, Pose, Pose.Length);
        Clip(segment.Clip).Apply(Pose, _bind, (time - segment.Time) * segment.Rate);
        var index = StageTimeline.UpperBound(_segments, time, s => s.Time) - 1;
        var weight = (float)Math.Clamp((time - segment.Time) / 0.2, 0, 1);
        if (index > 0 && weight < 1)
        {
            var previous = _segments[index - 1];
            Array.Copy(_bind, _previous, Pose.Length);
            Clip(previous.Clip).Apply(_previous, _bind, (time - previous.Time) * previous.Rate);
            for (var bone = 0; bone < Pose.Length; bone++) Pose[bone] = BandPose.Mix(_previous[bone], Pose[bone], weight);
        }
        if (walkAge >= 0)
        {
            var walk = Group("walk/cycle_anim/med");
            if (walk.Length > 0)
            {
                Array.Copy(_bind, _previous, Pose.Length);
                var clip = Clip(walk[0]);
                clip.Apply(_previous, _bind, walkAge % clip.Duration);
                _previous[0] = _previous[0] with { Position = new(_bind[0].Position.X, _previous[0].Position.Y, _bind[0].Position.Z) };
                for (var bone = 0; bone < Pose.Length; bone++) Pose[bone] = BandPose.Mix(Pose[bone], _previous[bone], walkWeight);
            }
        }
        var arms = Last(_timeline.Track("arms", _role), time);
        var disabled = arms != null && arms.Time >= segment.Time ? int.Parse(arms.Value) : segment.DisableArms;
        ArmsEnabled = disabled < 2 && !segment.Stance.StartsWith("intro");
        StrumEnabled = disabled == 0;
        if (_role is "guitarist" or "bassist" && ArmsEnabled) Hands(time);
        if (_role == "drummer") Drums(time);
        var face = Last(_timeline.Track("face", _role), time);
        if (face != null && _member.Clips.TryGetValue(face.Value, out var info) && time < face.Time + info.Duration)
            Clip(face.Value).Apply(Pose, _bind, time - face.Time, weight: (float)Math.Clamp((time - face.Time) / 0.1, 0, 1));
        var next = _segments[Math.Min(index + 1, _segments.Count - 1)];
        _ = Clip(next.Clip);
    }

    private void AddSegment()
    {
        var start = _segments.Count == 0 ? Math.Min(0, _timeline.Cues.FirstOrDefault()?.Time ?? 0) : _segments[^1].End;
        var previousStance = _stance;
        while (_commandIndex < _commands.Length && _commands[_commandIndex].Time <= start)
        {
            var command = _commands[_commandIndex++];
            if (command.Type == "stance") _stance = command.Value;
            else { _action = command.Value; _loop = command.Loop; _repeat = command.Count; _disableArms = command.DisableArms; }
        }
        var bpm = _timeline.TempoAt(start);
        var speed = bpm < 95 ? "slow" : bpm < 130 ? "med" : "fast";
        var options = previousStance != _stance ? Group($"{previousStance}/transitions/{_stance}") : Array.Empty<string>();
        var transition = options.Length > 0;
        if (!transition && _member.Clips.ContainsKey(_action)) options = new[] { _action };
        if (options.Length == 0) options = Group($"{_stance}/{_action}/normal/{speed}");
        if (options.Length == 0) options = Group($"{_stance}/idle/normal/{speed}");
        if (options.Length == 0) options = Group($"stance_a/idle/normal/{speed}");
        if (options.Length == 0) throw new InvalidDataException($"No original idle animation for {_role}");
        _seed ^= _seed << 13;
        _seed ^= _seed >> 17;
        _seed ^= _seed << 5;
        var selected = options[_seed % options.Length];
        var rate = transition ? 1 : bpm / (speed == "slow" ? 75 : speed == "med" ? 115 : 145);
        rate = Math.Clamp(rate, 0.5, 2);
        var end = start + _member.Clips[selected].Duration / rate;
        var immediate = _commands.Skip(_commandIndex).FirstOrDefault(c => c.Immediate && c.Time < end);
        if (immediate != null) end = Math.Max(start + 0.001, immediate.Time);
        _segments.Add(new(start, Math.Max(start + 0.001, end), selected, rate, _stance, _action, _disableArms));
        if (!transition && !_loop && --_repeat <= 0) { _action = "idle"; _loop = true; _disableArms = 0; }
    }

    private void Hands(double time)
    {
        var notes = _timeline.Track("strum", _role);
        var index = StageTimeline.UpperBound(notes, time, c => c.Time) - 1;
        var note = index < 0 ? null : notes[index];
        var style = Last(_timeline.Track("strum-style", _role), time)?.Value ?? "normal";
        var mask = note != null && time <= note.Time + Math.Max(0.1, note.Duration) ? note.Value : "0";
        Layer("chord/" + mask, 0, true);
        var fret = Last(_frets, time);
        if (fret != null) Layer("fret/" + fret.Value, 0, true);
        else Layer("fret/" + (_role == "guitarist" ? "125" : "108"), 0, true);
        if (!StrumEnabled) return;
        var length = note == null ? "no_strum_anim" : note.Duration < 0.15 ? "short" : note.Duration < 0.6 ? "med" : "long";
        var options = _member.Layers.GetValueOrDefault($"strum/{style}/{length}", Array.Empty<string>());
        if (note != null && options.Length > 0)
        {
            var clip = Clip(options[index % options.Length]);
            var age = time - note.Time;
            if (age < clip.Duration)
            {
                clip.Apply(Pose, _bind, age, true);
                return;
            }
        }
        Layer("strum/normal/no_strum_anim", 0, true);
    }

    private void Drums(double time)
    {
        for (var group = 0; group < _drumTracks.Length; group++)
        {
            var note = Last(_drumTracks[group], time);
            if (note == null) continue;
            var layer = group < 3 ? "drum/" + note.Value : "cymbal/" + (group - 3);
            var refs = _member.Layers.GetValueOrDefault(layer, Array.Empty<string>());
            if (refs.Length == 0) continue;
            var clip = Clip(refs[0]);
            var age = time - note.Time;
            if (age < clip.Duration + 0.25)
                clip.Apply(Pose, _bind, age, weight: (float)Math.Clamp((clip.Duration + 0.25 - age) / 0.25, 0, 1));
        }
    }

    private void Layer(string name, double time, bool additive)
    {
        var refs = _member.Layers.GetValueOrDefault(name, Array.Empty<string>());
        if (refs.Length > 0) Clip(refs[0]).Apply(Pose, _bind, time, additive);
    }

    private PerformanceClip Clip(string name)
    {
        if (_clips.TryGetValue(name, out var clip)) return clip;
        using var file = File.OpenRead(Path.Combine(_directory, _role, name + ".bin.gz"));
        clip = new PerformanceClip(file, _bind.Length);
        if (_clips.Count >= 128) _clips.Remove(_clips.Keys.First());
        _clips.Add(name, clip);
        return clip;
    }

    private string[] Group(string name) => _member.Groups.GetValueOrDefault(name, Array.Empty<string>());
    private static StageCue? Last(StageCue[] cues, double time)
    {
        var index = StageTimeline.UpperBound(cues, time, c => c.Time) - 1;
        return index < 0 ? null : cues[index];
    }
}
