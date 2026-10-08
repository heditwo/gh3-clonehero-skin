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

    public void SampleDelta(BandPose[] output, bool[] touched, BandPose[] bind, double time)
    {
        var frame = Math.Clamp(time / Duration * (_frames - 1), 0, _frames - 1);
        var a = (int)frame;
        var b = Math.Min(a + 1, _frames - 1);
        for (var i = 0; i < _bones.Length; i++)
        {
            var bone = _bones[i];
            var pose = BandPose.Mix(_poses[a * _bones.Length + i], _poses[b * _bones.Length + i], (float)(frame - a));
            output[bone] = new((_masks[i] & 1) == 0 ? Vector3.Zero : pose.Position - bind[bone].Position,
                (_masks[i] & 2) == 0 ? Quaternion.Identity : pose.Rotation);
            touched[bone] = true;
        }
    }

    // additive is GH3's applydifference (GH3.exe 0x4a3550): current * difference, so the difference turns about the bone's own axes.
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
            var rotation = (_masks[i] & 2) == 0 ? current.Rotation : additive ? Quaternion.Normalize(current.Rotation * pose.Rotation) : pose.Rotation;
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
    private readonly StageDrummer? _drummer;
    private readonly StageBlendTrack[] _armTracks = Array.Empty<StageBlendTrack>();
    private readonly StageGuitarRig? _rig;
    private readonly BandPose[] _armDelta;
    private readonly bool[] _armTouched;
    private const double ArmsBlendTime = 0.25;
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

    public PerformancePlayer(string role, string directory, PerformanceMember member, BandPose[] bind, StageTimeline timeline,
        int[]? parents = null, IReadOnlyDictionary<string, float>? constants = null)
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
        _armDelta = new BandPose[bind.Length];
        _armTouched = new bool[bind.Length];
        if (role == "drummer" && parents != null) _rig = new StageGuitarRig(parents, bind, member.Bones, "Bone_IK_Hand_Slave");
        if (role is "guitarist" or "bassist" && parents != null)
        {
            _rig = new StageGuitarRig(parents, bind, member.Bones);
            _armTracks = new[] { StrumTrack(constants), FretTrack(constants), FingerTrack(constants) };
        }
        if (role == "drummer") _drummer = new StageDrummer(member, timeline, constants ?? new Dictionary<string, float>());
    }

    public Segment At(double time)
    {
        if (!double.IsFinite(time) || time > 86400) throw new ArgumentOutOfRangeException(nameof(time));
        time = Math.Max(_timeline.Start, time);
        while ((_segments.Count == 0 || _segments[^1].End <= time + 3) && _segments.Count < 100000) AddSegment();
        return _segments[Math.Max(0, StageTimeline.UpperBound(_segments, time, s => s.Time) - 1)];
    }

    public void Prepare()
    {
        var end = Math.Min(3600, Math.Max(60, _timeline.Cues.LastOrDefault()?.Time ?? 0));
        _ = At(end);
        var needed = _segments.Select(s => s.Clip)
            .Concat(_member.Layers.Where(pair => !pair.Key.StartsWith("strum/")).SelectMany(pair => pair.Value)).Concat(_armTracks.SelectMany(t => t.Clips))
            .Concat(_timeline.Track("face", _role).Select(c => c.Value)).Where(_member.Clips.ContainsKey).Distinct();
        foreach (var clip in needed) _ = Clip(clip);
    }

    public void Sample(double time, double walkAge = -1, float walkWeight = 1)
    {
        time = Math.Max(_timeline.Start, time);
        var segment = At(time);
        CurrentClip = segment.Clip;
        CurrentStance = segment.Stance;
        Array.Copy(_bind, Pose, Pose.Length);
        Clip(segment.Clip).Apply(Pose, _bind, (time - segment.Time) * segment.Rate);
        var index = StageTimeline.UpperBound(_segments, time, s => s.Time) - 1;
        // Body DegenerateBlend with BlendDuration 0.2 (hero_play_adjusting_random_anims); see StageBlendTrack.
        var weight = (float)(1 / (2 - Math.Clamp((time - segment.Time) / 0.2, 0, 1)));
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
        if (_drummer != null)
        {
            _drummer.Apply(Pose, _bind, time, Clip);
            _rig?.Apply(Pose, 1, 1);
        }
        else if (_rig != null) Arms(time);
        var face = Last(_timeline.Track("face", _role), time);
        if (face != null && _member.Clips.TryGetValue(face.Value, out var info) && time < face.Time + info.Duration)
            Clip(face.Value).Apply(Pose, _bind, time - face.Time, weight: (float)Math.Clamp((time - face.Time) / 0.1, 0, 1));
        var next = _segments[Math.Min(index + 1, _segments.Count - 1)];
        _ = Clip(next.Clip);
    }

    private void AddSegment()
    {
        var start = _segments.Count == 0 ? _timeline.Start : _segments[^1].End;
        var previousStance = _stance;
        while (_commandIndex < _commands.Length && _commands[_commandIndex].Time <= start)
        {
            var command = _commands[_commandIndex++];
            if (command.Type == "stance") _stance = command.Value;
            else { _action = command.Value; _loop = command.Loop; _repeat = command.Count; _disableArms = command.DisableArms; }
        }
        var bpm = _timeline.TempoAt(start);
        var speed = bpm < 95 ? "slow" : bpm < 130 ? "med" : "fast";
        // play_special_anim switches to the win and lose stances without a transition animation.
        var special = _stance.StartsWith("win") || _stance.StartsWith("lose");
        var options = previousStance != _stance && !special ? Group($"{previousStance}/transitions/{_stance}") : Array.Empty<string>();
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
        // hero_play_tempo_anim_cfunc (GH3.exe 0x420d50) only picks the slow, med, or fast clip; hero_play_anim plays it at Speed 1.
        const double rate = 1;
        var end = start + _member.Clips[selected].Duration / rate;
        var immediate = _commands.Skip(_commandIndex).FirstOrDefault(c => c.Immediate && c.Time < end);
        if (immediate != null) end = Math.Max(start + 0.001, immediate.Time);
        _segments.Add(new(start, Math.Max(start + 0.001, end), selected, rate, _stance, _action, _disableArms));
        if (!transition && !_loop && --_repeat <= 0) { _action = "idle"; _loop = true; _disableArms = 0; }
    }

    // GH3 hero_arm_branch: strum, fret and finger difference animations applied to the body, then the IK and twist bones.
    // Hand events fire strum_anim_lead_time before each note (Strum_iterator, FretPos_iterator, FretFingers_iterator).
    private void Arms(double time)
    {
        var (right, left) = ArmStrength(time);
        if (right > 0 || left > 0)
        {
            Array.Fill(_armDelta, new BandPose(Vector3.Zero, Quaternion.Identity));
            Array.Clear(_armTouched, 0, _armTouched.Length);
            for (var i = 0; i < _armTracks.Length; i++)
            {
                var strength = i == 0 ? right : left;
                if (strength <= 0) continue;
                _armTracks[i].Evaluate(time, Clip, _bind, _armDelta, _armTouched);
                for (var bone = 0; bone < Pose.Length; bone++)
                {
                    if (!_armTouched[bone]) continue;
                    var delta = _armDelta[bone];
                    var current = Pose[bone];
                    Pose[bone] = new(current.Position + delta.Position * strength,
                        Quaternion.Normalize(Quaternion.Slerp(current.Rotation, delta.Rotation * current.Rotation, strength)));
                    _armDelta[bone] = new(Vector3.Zero, Quaternion.Identity);
                    _armTouched[bone] = false;
                }
            }
        }
        _rig!.Apply(Pose, right, left);
    }

    // hero_toggle_arms: the strum arm needs two arms, the fret arm one; each change blends over 0.25 seconds.
    private (float Right, float Left) ArmStrength(double time)
    {
        var now = ArmCount(time);
        var segment = At(time);
        var arms = Last(_timeline.Track("arms", _role), time);
        var change = Math.Max(segment.Time, arms?.Time ?? double.NegativeInfinity);
        var before = ArmCount(change - 1e-4);
        if (before == now && arms != null && arms.Time < segment.Time)
        {
            change = segment.Time;
            before = ArmCount(change - 1e-4);
        }
        var weight = (float)Math.Clamp((time - change) / ArmsBlendTime, 0, 1);
        float Side(int count, int needed) => count >= needed ? 1 : 0;
        return (Side(before, 2) + (Side(now, 2) - Side(before, 2)) * weight, Side(before, 1) + (Side(now, 1) - Side(before, 1)) * weight);
    }

    private int ArmCount(double time)
    {
        var segment = At(Math.Max(0, time));
        // GH3 holds the arms in the intro stances, and play_special_anim disables them for the win and lose animations.
        if (segment.Stance.StartsWith("intro") || segment.Stance.StartsWith("win") || segment.Stance.StartsWith("lose")) return 0;
        var arms = Last(_timeline.Track("arms", _role), time);
        var disabled = arms != null && arms.Time >= segment.Time ? int.Parse(arms.Value) : segment.DisableArms;
        return Math.Clamp(2 - disabled, 0, 2);
    }

    private static double Constant(IReadOnlyDictionary<string, float>? constants, string name, double fallback) =>
        constants != null && constants.TryGetValue(name, out var value) ? value : fallback;

    private double LeadTime(IReadOnlyDictionary<string, float>? constants) => Constant(constants, "strum_anim_lead_time", 250) / 1000;

    // Charts give normal notes no length; GH3 note arrays give them their MIDI length, usually a sixteenth note.
    private double NoteLength(StageCue note) => note.Duration > 0 ? note.Duration : 15 / _timeline.TempoAt(note.Time);

    private string[] Layer(string name) => _member.Layers.GetValueOrDefault(name, Array.Empty<string>());

    // hero_strum_guitar: a random strum for the note length class with a 0.1 s blend, then no_strum_anim with 0.2 s.
    private StageBlendTrack StrumTrack(IReadOnlyDictionary<string, float>? constants)
    {
        var lead = LeadTime(constants);
        var shortest = Constant(constants, "short_strum_max_gem_length", 150) / 1000;
        var medium = Constant(constants, "med_strum_max_gem_length", 600) / 1000;
        var styles = _timeline.Track("strum-style", _role);
        string Style(double time) => Last(styles, time)?.Value ?? "default";
        var branches = new List<StageBlendTrack.Branch>();
        var idle = Layer("strum/default/no_strum_anim");
        if (idle.Length > 0) branches.Add(new(double.NegativeInfinity, idle[0], 0));
        var notes = _timeline.Track("strum", _role);
        var random = Convert.ToUInt32(Gh3Checksum.Of(_role + "strum"), 16);
        for (var i = 0; i < notes.Length; i++)
        {
            var start = notes[i].Time - lead;
            var style = Style(start);
            var length = NoteLength(notes[i]);
            var options = Layer($"strum/{style}/{(length < shortest ? "short" : length < medium ? "med" : "long")}");
            if (options.Length == 0) continue;
            random ^= random << 13;
            random ^= random >> 17;
            random ^= random << 5;
            var clip = options[random % options.Length];
            branches.Add(new(start, clip, 0.1));
            var end = start + _member.Clips[clip].Duration;
            var rest = Layer($"strum/{style}/no_strum_anim");
            if (rest.Length > 0 && (i + 1 >= notes.Length || notes[i + 1].Time - lead > end)) branches.Add(new(end, rest[0], 0.2));
        }
        return new(branches, _bind.Length);
    }

    // handle_fret_event: the fret hand moves along the neck on animation notes with fret_blend_time.
    private StageBlendTrack FretTrack(IReadOnlyDictionary<string, float>? constants)
    {
        var lead = LeadTime(constants);
        var blend = Constant(constants, "fret_blend_time", 0.1);
        var branches = new List<StageBlendTrack.Branch>();
        var start = Layer("fret/123");
        if (start.Length > 0) branches.Add(new(double.NegativeInfinity, start[0], 0));
        foreach (var cue in _timeline.Track("fret"))
        {
            if (!int.TryParse(cue.Value, out var note) || !(_role == "guitarist" ? note is >= 118 and <= 127 : note is >= 101 and <= 110)) continue;
            var clip = Layer("fret/" + note);
            if (clip.Length > 0) branches.Add(new(cue.Time - lead, clip[0], blend));
        }
        return new(branches, _bind.Length);
    }

    // FretFingers_iterator: press each chord with finger_press_blend_time and release it after the note length plus
    // finger_note_padding with finger_release_blend_time, unless the next chord is pressed first.
    private StageBlendTrack FingerTrack(IReadOnlyDictionary<string, float>? constants)
    {
        var lead = LeadTime(constants);
        var press = Constant(constants, "finger_press_blend_time", 0.07);
        var release = Constant(constants, "finger_release_blend_time", 0.2);
        var padding = Constant(constants, "finger_note_padding", 0.01);
        var branches = new List<StageBlendTrack.Branch>();
        var open = Layer("chord/0");
        if (open.Length > 0) branches.Add(new(double.NegativeInfinity, open[0], 0));
        var notes = _timeline.Track("strum", _role);
        for (var i = 0; i < notes.Length; i++)
        {
            var start = notes[i].Time - lead;
            var chord = Layer("chord/" + notes[i].Value);
            if (chord.Length == 0) continue;
            branches.Add(new(start, chord[0], notes[i].Value == "0" ? release : press));
            var end = start + NoteLength(notes[i]) + padding;
            if (open.Length > 0 && (i + 1 >= notes.Length || notes[i + 1].Time - lead > end)) branches.Add(new(end, open[0], release));
        }
        return new(branches, _bind.Length);
    }

    private PerformanceClip Clip(string name)
    {
        if (_clips.TryGetValue(name, out var clip)) return clip;
        using var file = File.OpenRead(Path.Combine(_directory, _role, name + ".bin.gz"));
        clip = new PerformanceClip(file, _bind.Length);
        if (_clips.Count >= 512) _clips.Remove(_clips.Keys.First());
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
