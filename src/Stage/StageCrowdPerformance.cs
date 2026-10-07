using System.Text.Json;

namespace Gh3Skin;

internal sealed class CrowdMember
{
    public string Model { get; set; } = "";
    public Dictionary<string, string[]> Groups { get; set; } = new();
}

internal sealed class CrowdDefinition
{
    public int Version { get; set; }
    public string[] Models { get; set; } = Array.Empty<string>();
    public string[] Bones { get; set; } = Array.Empty<string>();
    public string[] Hands { get; set; } = Array.Empty<string>();
    public Dictionary<string, CrowdMember> Members { get; set; } = new();
    public Dictionary<string, PerformanceClipInfo> PerformanceClips { get; set; } = new();
    public Dictionary<string, float> Constants { get; set; } = new();

    public static CrowdDefinition Load(string path)
    {
        var bank = JsonSerializer.Deserialize<CrowdDefinition>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (bank == null || bank.Version is not (1 or 2) || bank.Models.Length is < 1 or > 64 || bank.Version == 1 && bank.Models.Length > 8 || bank.Models.Distinct().Count() != bank.Models.Length ||
            bank.Models.Any(name => name.Length is < 1 or > 64 || name.Any(c => !char.IsLetterOrDigit(c) && c != '_')))
            throw new InvalidDataException("Invalid crowd definition.");
        if (bank.Version == 1) return bank;
        var hands = new[] { "left", "right" }.SelectMany(side => new[] { "clap", "lighter", "rock", "fist" }.Select(kind => side + "-" + kind)).ToArray();
        if (bank.Bones.Length is < 1 or > 255 || bank.Members.Count != 8 || bank.Hands.Length != 8 || hands.Except(bank.Hands).Any() ||
            bank.PerformanceClips.Count is < 1 or > 4096 || bank.PerformanceClips.Any(clip => clip.Key.Length != 8 || !clip.Key.All(Uri.IsHexDigit) ||
                !float.IsFinite(clip.Value.Duration) || clip.Value.Duration <= 0 || clip.Value.Duration > 120))
            throw new InvalidDataException("Invalid crowd performance library.");
        foreach (var (role, member) in bank.Members)
        {
            _ = StageCrowdState.Member(role);
            if (!bank.Models.Contains(member.Model) || member.Groups.Count > 1024 || member.Groups.Values.Any(refs => refs.Length == 0 || refs.Any(id => !bank.PerformanceClips.ContainsKey(id))))
                throw new InvalidDataException("Invalid crowd animation group.");
            foreach (var action in new[] { "idle", "special", "starpower" })
                foreach (var skill in new[] { "bad", "normal", "good" })
                    foreach (var speed in new[] { "slow", "med", "fast" })
                        if (!member.Groups.ContainsKey($"{action}/{skill}/{speed}")) throw new InvalidDataException("Missing crowd animation group.");
        }
        foreach (var name in new[] { "Crowd_BlendTime_TempoChange", "Crowd_BlendTime_Bad", "Crowd_BlendTime_Special", "Crowd_BlendTime_Slow", "Crowd_BlendTime_Med", "Crowd_BlendTime_Fast", "slow_tempo", "med_tempo", "fast_tempo" })
            if (!bank.Constants.TryGetValue(name, out var value) || !float.IsFinite(value) || value < 0 || value > 400)
                throw new InvalidDataException("Missing or invalid crowd timing constant.");
        return bank;
    }
}

internal sealed class CrowdAnimationPlayer
{
    internal sealed record Segment(double Time, double End, string Clip, double Blend, string Speed);
    private readonly CrowdDefinition _bank;
    private readonly CrowdMember _member;
    private readonly StageTimeline _timeline;
    private readonly StageCrowdState _state;
    private readonly int _memberIndex;
    private readonly uint _seed;
    private readonly List<Segment> _segments = new();
    private int _revision = -1;

    public CrowdAnimationPlayer(CrowdDefinition bank, string role, int variation, StageTimeline timeline, StageCrowdState state)
    {
        _bank = bank;
        _member = bank.Members[role];
        _memberIndex = StageCrowdState.Member(role);
        _seed = Gh3Checksum.Value(role + "/" + variation);
        _timeline = timeline;
        _state = state;
    }

    public Segment At(double time)
    {
        if (!double.IsFinite(time) || time > 86400) throw new ArgumentOutOfRangeException(nameof(time));
        time = Math.Max(0, time);
        if (_revision != _state.Revision) { _segments.Clear(); _revision = _state.Revision; }
        while (_segments.Count == 0 || _segments[^1].End <= time)
        {
            if (_segments.Count >= 100000) throw new InvalidDataException("Too many crowd animation segments.");
            Add();
        }
        return _segments[Math.Max(0, StageTimeline.UpperBound(_segments, time, segment => segment.Time) - 1)];
    }

    public void Sample(double time, BandPose[] pose, BandPose[] previous, BandPose[] bind, Func<string, PerformanceClip> clip)
    {
        time = Math.Max(0, time);
        var segment = At(time);
        Array.Copy(bind, pose, bind.Length);
        clip(segment.Clip).Apply(pose, bind, time - segment.Time);
        var index = StageTimeline.UpperBound(_segments, time, item => item.Time) - 1;
        var weight = segment.Blend <= 0 ? 1 : (float)Math.Clamp((time - segment.Time) / segment.Blend, 0, 1);
        if (index <= 0 || weight >= 1) return;
        var before = _segments[index - 1];
        Array.Copy(bind, previous, bind.Length);
        clip(before.Clip).Apply(previous, bind, time - before.Time);
        for (var bone = 0; bone < bind.Length; bone++) pose[bone] = BandPose.Mix(previous[bone], pose[bone], weight);
    }

    private void Add()
    {
        var requests = _state.Animations(_memberIndex);
        var time = _segments.Count == 0 ? Math.Min(0, requests.FirstOrDefault()?.Time ?? 0) : _segments[^1].End;
        var request = StageTimeline.UpperBound(requests, time, item => item.Time) - 1;
        var action = request < 0 ? "idle" : requests[request].Animation;
        var skill = _state.SkillAt(time);
        var bpm = _timeline.TempoAt(time);
        var speed = bpm < (_bank.Constants["slow_tempo"] + _bank.Constants["med_tempo"]) / 2 ? "slow" :
            bpm < (_bank.Constants["med_tempo"] + _bank.Constants["fast_tempo"]) / 2 ? "med" : "fast";
        if (!_member.Groups.TryGetValue($"{action}/{skill}/{speed}", out var choices))
            throw new InvalidDataException($"Unresolved crowd animation {action}/{skill}/{speed}.");
        var hash = unchecked(_seed + (uint)_segments.Count * 0x9e3779b9u);
        hash ^= hash << 13; hash ^= hash >> 17; hash ^= hash << 5;
        var selected = choices[hash % choices.Length];
        var previousSpeed = request >= 0 && requests[request].Time == time ? null : _segments.LastOrDefault()?.Speed;
        var blend = _bank.Constants["Crowd_BlendTime_" + (previousSpeed != speed ? "TempoChange" : skill == "bad" ? "Bad" : action == "special" ? "Special" :
            speed == "slow" ? "Slow" : speed == "med" ? "Med" : "Fast")];
        var end = time + _bank.PerformanceClips[selected].Duration;
        if (request + 1 < requests.Count) end = Math.Min(end, requests[request + 1].Time);
        _segments.Add(new(time, Math.Max(time + 0.000001, end), selected, blend, speed));
    }
}
