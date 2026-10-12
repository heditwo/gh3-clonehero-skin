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
    // The scalars of the GH3 `walking` struct of the set (min_turn); empty for a set without its own walk.
    public Dictionary<string, float> Walking { get; set; } = new();

    public bool HasValidWalking => Walking.Values.All(float.IsFinite);

    private const string WalkMinTurn = "min_turn";
    private const float HalfTurn = 180;

    // The cycle clip group of the walk (`walking.forward` or `walking.backward`; pipeline.library.direction.walk_groups).
    public static string WalkCycle(bool backward) => backward ? "walk/backward/cycle_anim/med" : "walk/forward/cycle_anim/med";

    // GH3 walk_to_pos (guitar_walking.qb): the performer keeps its facing and walks backward to a target within min_turn
    // degrees of its back (IsFacing is then false); it turns to any other target and walks forward. `front` and `toward`
    // are horizontal directions (x, z). A set without min_turn or a backward walk walks forward.
    public bool WalksBackward(float frontX, float frontZ, float towardX, float towardZ)
    {
        var lengths = MathF.Sqrt((frontX * frontX + frontZ * frontZ) * (towardX * towardX + towardZ * towardZ));
        if (!Walking.TryGetValue(WalkMinTurn, out var minTurn) || !Groups.ContainsKey(WalkCycle(true)) || lengths <= 0) return false;
        var angle = MathF.Acos(Math.Clamp((frontX * towardX + frontZ * towardZ) / lengths, -1, 1)) * HalfTurn / MathF.PI;
        return angle >= HalfTurn - minTurn;
    }
}

// The band library's performance bank (band/performance.json): members, clip groups and layers, and GH3's constants.
internal sealed class StagePerformance
{
    private const int MaximumMembers = 4;
    private const int MaximumBones = 255;
    public const int MaximumClips = 5000;
    public int Version { get; set; }
    public Dictionary<string, PerformanceMember> Members { get; set; } = new();
    public Dictionary<string, float> Constants { get; set; } = new();
    public Dictionary<string, float[]> Waypoints { get; set; } = new();

    public static StagePerformance? Load(string path)
    {
        if (!File.Exists(path)) return null;
        var bank = JsonSerializer.Deserialize<StagePerformance>(File.ReadAllText(path), Packages.PackageJson.CaseInsensitive);
        if (bank?.Version != 1 || bank.Members.Count is < 1 or > MaximumMembers)
            throw new InvalidDataException("Invalid original performance library");
        foreach (var member in bank.Members.Values)
        {
            if (member.Bones.Length is < 1 or > MaximumBones || member.Clips.Count > MaximumClips ||
                member.Clips.Any(c => c.Key.Length != 8 || c.Key.Any(x => !Uri.IsHexDigit(x)) ||
                    !float.IsFinite(c.Value.Duration) || c.Value.Duration is <= 0 or > PerformanceClip.MaximumSeconds || c.Value.Frames is < 2 or > PerformanceClip.MaximumFrames) ||
                member.Groups.Values.Concat(member.Layers.Values).SelectMany(c => c).Any(c => !member.Clips.ContainsKey(c)) || !member.HasValidWalking)
                throw new InvalidDataException("Invalid original performance clips");
        }
        return bank;
    }
}

// Plays a band member's GH3 performance on the song clock: stance and idle segments, the arm, strum, and fret blend
// tracks, facial clips, and lip sync, into one pose per frame.
internal sealed class PerformancePlayer
{
    // GH3: the body DegenerateBlend has BlendDuration 0.2 (hero_play_adjusting_random_anims; see StageBlendTrack).
    private const double BodyBlend = 0.2;
    // Tuned: blend into a facial clip, a strum clip, and the rest pose after a strum.
    private const double FaceBlend = 0.1;
    private const double StrumBlend = 0.1;
    private const double RestBlend = 0.2;
    // Tuned: tempo bands (beats per minute) of the slow, med, and fast idle groups; GH3 source not found.
    private const double SlowTempo = 95;
    private const double MediumTempo = 130;
    // Prepare() decodes the clips of at least one minute and at most one hour of the song.
    private const double PrepareMinimumSeconds = 60;
    private const double PrepareMaximumSeconds = 3600;
    private const double MinimumSegment = 0.001;
    // Reads the arm state just before a change at the same time.
    private const double JustBefore = 1e-4;
    // Charts give normal notes no length; GH3 note arrays give them their MIDI length, usually a sixteenth note.
    private const double SixteenthNoteBeats = 0.25;
    internal sealed record Segment(double Time, double End, string Clip, double Rate, string Stance, string Action, int DisableArms);
    private readonly string _role;
    private readonly PerformanceMember _member;
    private readonly BandPose[] _bind;
    private readonly BandPose[] _previous;
    private readonly StageTimeline _timeline;
    private readonly PerformanceClipCache _clips;
    private readonly PerformanceClipCache _faceClips;
    private readonly PerformanceMember _faceMember;
    private readonly List<Segment> _segments = new();
    private readonly StageCue[] _commands;
    private readonly StageDrummer? _drummer;
    private readonly StageBlendTrack[] _armTracks = Array.Empty<StageBlendTrack>();
    private readonly StageGuitarRig? _rig;
    private readonly StageLipSync? _lipSync;
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
    // The arm IK strength (strum arm, fret arm) of the last Sample; a retargeted character uses it for its own arm IK.
    public (float Right, float Left) ArmGain { get; private set; }

    // `motion`: the set that plays (CharacterMotion) with the IK chains and constraints of its rig. For a character library
    // profile its animation set replaces `member`, and the song facial clips come from the band library member of the role.
    public PerformancePlayer(string role, string directory, PerformanceMember member, BandPose[] bind, StageTimeline timeline,
        int[]? parents = null, IReadOnlyDictionary<string, float>? constants = null, CharacterMotion? motion = null)
    {
        _role = role;
        _member = motion?.Member ?? member;
        member = _member;
        _bind = bind;
        _clips = new PerformanceClipCache(motion?.ClipDirectory ?? Path.Combine(directory, role), bind.Length, role, motion?.Offsets);
        _faceMember = motion?.Face ?? member;
        _faceClips = motion?.Face != null ? new PerformanceClipCache(motion.FaceDirectory, bind.Length, role + " face", motion.FaceOffsets) : _clips;
        _timeline = timeline;
        Pose = new BandPose[bind.Length];
        _previous = new BandPose[bind.Length];
        if (member.Bones.Length != bind.Length) throw new InvalidDataException("Performance skeleton mismatch");
        _seed = Convert.ToUInt32(Gh3Checksum.Of(role), 16);
        _commands = timeline.PlaybackCues.Where(c => c.Role == role && c.Type is "stance" or "perform" or "clip").ToArray();
        _armDelta = new BandPose[bind.Length];
        _armTouched = new bool[bind.Length];
        // The IK and the constraints of guitarist_static_tree and drummer_static_tree, with the rig data of the set. The mod
        // does not run the IK of vocalist_static_tree.
        if (role is "guitarist" or "bassist" or "drummer" && parents != null && motion != null)
            _rig = new StageGuitarRig(parents, bind, member.Bones, motion.Ik, motion.Constraints);
        if (role is "guitarist" or "bassist" && _rig != null) _armTracks = new[] { StrumTrack(constants), FretTrack(constants), FingerTrack(constants) };
        if (role == "drummer") _drummer = new StageDrummer(member, timeline, constants ?? new Dictionary<string, float>());
        _lipSync = StageLipSync.Create(timeline, role, member.Bones);
    }

    public Segment At(double time)
    {
        if (!double.IsFinite(time) || time > StageLimits.MaximumSeconds) throw new ArgumentOutOfRangeException(nameof(time));
        time = Math.Max(_timeline.Start, time);
        while ((_segments.Count == 0 || _segments[^1].End <= time + 3) && _segments.Count < StageLimits.MaximumCount) AddSegment();
        return _segments[Math.Max(0, StageTimeline.UpperBound(_segments, time, s => s.Time) - 1)];
    }

    public void Prepare()
    {
        var end = Math.Min(PrepareMaximumSeconds, Math.Max(PrepareMinimumSeconds, _timeline.Cues.LastOrDefault()?.Time ?? 0));
        _ = At(end);
        var needed = _segments.Select(s => s.Clip)
            .Concat(_member.Layers.Where(pair => !pair.Key.StartsWith("strum/")).SelectMany(pair => pair.Value)).Concat(_armTracks.SelectMany(t => t.Clips));
        var faces = _timeline.Track("face", _role).Select(c => c.Value).Where(_faceMember.Clips.ContainsKey);
        if (_faceClips == _clips) _clips.Prefetch(needed.Concat(faces).Where(_member.Clips.ContainsKey).Distinct());
        else
        {
            _clips.Prefetch(needed.Where(_member.Clips.ContainsKey).Distinct());
            _faceClips.Prefetch(faces.Distinct());
        }
    }

    public PerformanceClipCache Clips => _clips;
    // The facial clip cache when it is not `Clips` (a character library profile), else null.
    public PerformanceClipCache? FaceClips => _faceClips == _clips ? null : _faceClips;
    public StageLipSync? LipSync => _lipSync;

    public void Sample(double time, double walkAge = -1, float walkWeight = 1, bool walkBackward = false)
    {
        time = Math.Max(_timeline.Start, time);
        var segment = At(time);
        CurrentClip = segment.Clip;
        CurrentStance = segment.Stance;
        Array.Copy(_bind, Pose, Pose.Length);
        Clip(segment.Clip).Apply(Pose, _bind, (time - segment.Time) * segment.Rate);
        var index = StageTimeline.UpperBound(_segments, time, s => s.Time) - 1;
        var weight = (float)Math.Clamp((time - segment.Time) / BodyBlend, 0, 1);
        if (index > 0 && weight < 1)
        {
            var previous = _segments[index - 1];
            Array.Copy(_bind, _previous, Pose.Length);
            Clip(previous.Clip).Apply(_previous, _bind, (time - previous.Time) * previous.Rate);
            for (var bone = 0; bone < Pose.Length; bone++) Pose[bone] = BandPose.Mix(_previous[bone], Pose[bone], weight);
        }
        if (walkAge >= 0)
        {
            var walk = Group(PerformanceMember.WalkCycle(walkBackward));
            if (walk.Length > 0)
            {
                Array.Copy(_bind, _previous, Pose.Length);
                var clip = Clip(walk[0]);
                clip.Apply(_previous, _bind, walkAge % clip.Duration);
                _previous[0] = _previous[0] with { Position = new(_bind[0].Position.X, _previous[0].Position.Y, _bind[0].Position.Z) };
                for (var bone = 0; bone < Pose.Length; bone++) Pose[bone] = BandPose.Mix(Pose[bone], _previous[bone], walkWeight);
            }
        }
        ArmGain = (0, 0);
        if (_drummer != null)
        {
            _drummer.Apply(Pose, _bind, time, Clip);
            _rig?.Apply(Pose, 1, 1);
            if (_rig != null) ArmGain = (1, 1);
        }
        else if (_rig != null) Arms(time);
        var face = Last(_timeline.Track("face", _role), time);
        if (face != null && _faceMember.Clips.TryGetValue(face.Value, out var info) && time < face.Time + info.Duration)
            _faceClips.Get(face.Value).Apply(Pose, _bind, time - face.Time, weight: (float)Math.Clamp((time - face.Time) / FaceBlend, 0, 1));
        _lipSync?.Apply(Pose, time);
        var next = _segments[Math.Min(index + 1, _segments.Count - 1)];
        _ = Clip(next.Clip);
    }

    // Verification (GH3_SKIN_TRACE): the body clip at `time` without blends, layers, or IK, as GH3 samples it.
    public (Segment Segment, double ClipTime) SampleClip(double time, BandPose[] output)
    {
        time = Math.Max(_timeline.Start, time);
        var segment = At(time);
        Array.Copy(_bind, output, output.Length);
        var clipTime = (time - segment.Time) * segment.Rate;
        Clip(segment.Clip).Apply(output, _bind, clipTime);
        return (segment, clipTime);
    }

    // Verification (GH3_SKIN_TRACE): the song facial clip at `time` without the blend in, or null when none plays.
    public (string Clip, double ClipTime)? SampleFace(double time, BandPose[] output)
    {
        var face = Last(_timeline.Track("face", _role), time);
        if (face == null || !_faceMember.Clips.TryGetValue(face.Value, out var info) || time >= face.Time + info.Duration) return null;
        Array.Copy(_bind, output, output.Length);
        _faceClips.Get(face.Value).Apply(output, _bind, time - face.Time);
        return (face.Value, time - face.Time);
    }

    // Verification (GH3_SKIN_TRACE): the walk cycle clip `walkAge` seconds into a walk, without the blend and the root
    // lock, or null when the member has no walk cycle.
    public (string Clip, double ClipTime)? SampleWalk(double walkAge, bool backward, BandPose[] output)
    {
        var walk = Group(PerformanceMember.WalkCycle(backward));
        if (walk.Length == 0) return null;
        var clip = Clip(walk[0]);
        var clipTime = walkAge % clip.Duration;
        Array.Copy(_bind, output, output.Length);
        clip.Apply(output, _bind, clipTime);
        return (walk[0], clipTime);
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
        var speed = bpm < SlowTempo ? "slow" : bpm < MediumTempo ? "med" : "fast";
        // play_special_anim switches to the win and lose stances without a transition animation.
        var special = _stance.StartsWith("win") || _stance.StartsWith("lose");
        var options = previousStance != _stance && !special ? Group($"{previousStance}/transitions/{_stance}") : Array.Empty<string>();
        var transition = options.Length > 0;
        if (!transition && _member.Clips.ContainsKey(_action)) options = new[] { _action };
        if (options.Length == 0) options = Group($"{_stance}/{_action}/normal/{speed}");
        if (options.Length == 0) options = Group($"{_stance}/idle/normal/{speed}");
        if (options.Length == 0) options = Group($"stance_a/idle/normal/{speed}");
        if (options.Length == 0) throw new InvalidDataException($"No original idle animation for {_role}");
        XorShift32.Next(ref _seed);
        var selected = options[_seed % options.Length];
        // hero_play_tempo_anim_cfunc (GH3.exe 0x420d50) only picks the slow, med, or fast clip; hero_play_anim plays it at Speed 1.
        const double rate = 1;
        var end = start + _member.Clips[selected].Duration / rate;
        var immediate = _commands.Skip(_commandIndex).FirstOrDefault(c => c.Immediate && c.Time < end);
        if (immediate != null) end = Math.Max(start + MinimumSegment, immediate.Time);
        _segments.Add(new(start, Math.Max(start + MinimumSegment, end), selected, rate, _stance, _action, _disableArms));
        if (!transition && !_loop && --_repeat <= 0) { _action = "idle"; _loop = true; _disableArms = 0; }
    }

    // GH3 hero_arm_branch: strum, fret and finger difference animations applied to the body, then the IK and twist bones.
    // Hand events fire strum_anim_lead_time before each note (Strum_iterator, FretPos_iterator, FretFingers_iterator).
    private void Arms(double time)
    {
        var (right, left) = ArmStrength(time);
        ArmGain = (right, left);
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
        var before = ArmCount(change - JustBefore);
        if (before == now && arms != null && arms.Time < segment.Time)
        {
            change = segment.Time;
            before = ArmCount(change - JustBefore);
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

    // GH3: the defaults of guitar_band constants missing from the bank (milliseconds, then seconds).
    private double LeadTime(IReadOnlyDictionary<string, float>? constants) => Constant(constants, "strum_anim_lead_time", 250) / 1000;

    private double NoteLength(StageCue note) => note.Duration > 0 ? note.Duration : SixteenthNoteBeats * 60 / _timeline.TempoAt(note.Time);

    private string[] Layer(string name) => _member.Layers.GetValueOrDefault(name, Array.Empty<string>());

    // hero_strum_guitar: a random strum for the note length class with a 0.1 s blend, then no_strum_anim with 0.2 s.
    private StageBlendTrack StrumTrack(IReadOnlyDictionary<string, float>? constants)
    {
        var lead = LeadTime(constants);
        // GH3: guitar_band defaults.
        var shortest = Constant(constants, "short_strum_max_gem_length", 150) / 1000;
        // GH3: guitar_band default.
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
            XorShift32.Next(ref random);
            var clip = options[random % options.Length];
            branches.Add(new(start, clip, StrumBlend));
            var end = start + _member.Clips[clip].Duration;
            var rest = Layer($"strum/{style}/no_strum_anim");
            if (rest.Length > 0 && (i + 1 >= notes.Length || notes[i + 1].Time - lead > end)) branches.Add(new(end, rest[0], RestBlend));
        }
        return new(branches, _bind.Length);
    }

    // handle_fret_event: the fret hand moves along the neck on animation notes with fret_blend_time.
    private StageBlendTrack FretTrack(IReadOnlyDictionary<string, float>? constants)
    {
        var lead = LeadTime(constants);
        // GH3: guitar_band default.
        var blend = Constant(constants, "fret_blend_time", 0.1);
        var branches = new List<StageBlendTrack.Branch>();
        var start = Layer("fret/123");
        if (start.Length > 0) branches.Add(new(double.NegativeInfinity, start[0], 0));
        foreach (var cue in _timeline.Track("fret"))
        {
            if (!int.TryParse(cue.Value, out var note) || !(_role == "guitarist" ? note is >= Gh3TrackNote.GuitarFretFirst and <= Gh3TrackNote.GuitarFretLast : note is >= Gh3TrackNote.BassFretFirst and <= Gh3TrackNote.BassFretLast)) continue;
            var clip = Layer("fret/" + note);
            if (clip.Length > 0) branches.Add(new(cue.Time - lead, clip[0], blend));
        }
        return new(branches, _bind.Length);
    }

    // FretFingers_iterator: press each chord with finger_press_blend_time. The hand holds the chord until the next note:
    // the release event (chord none, after the note length plus finger_note_padding) adds no branch to GH3's LeftHand node.
    private StageBlendTrack FingerTrack(IReadOnlyDictionary<string, float>? constants)
    {
        var lead = LeadTime(constants);
        // GH3: guitar_band defaults.
        var press = Constant(constants, "finger_press_blend_time", 0.07);
        // GH3: guitar_band default.
        var release = Constant(constants, "finger_release_blend_time", 0.2);
        var branches = new List<StageBlendTrack.Branch>();
        var open = Layer("chord/0");
        if (open.Length > 0) branches.Add(new(double.NegativeInfinity, open[0], 0));
        foreach (var note in _timeline.Track("strum", _role))
        {
            var chord = Layer("chord/" + note.Value);
            if (chord.Length > 0) branches.Add(new(note.Time - lead, chord[0], note.Value == "0" ? release : press));
        }
        return new(branches, _bind.Length);
    }

    private PerformanceClip Clip(string name) => _clips.Get(name);

    private string[] Group(string name) => _member.Groups.GetValueOrDefault(name, Array.Empty<string>());
    private static StageCue? Last(StageCue[] cues, double time)
    {
        var index = StageTimeline.UpperBound(cues, time, c => c.Time) - 1;
        return index < 0 ? null : cues[index];
    }
}

