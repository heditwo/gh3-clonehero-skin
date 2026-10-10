namespace Gh3Skin;

internal sealed record CrowdAnimationRequest(double Time, string Animation);

internal sealed class StageCrowdState
{
    public const double GameFrame = 1.0 / 60;
    private sealed record SkillChange(double Time, string Skill);
    private sealed record VisibilityChange(double Time, bool Visible);
    private sealed record HandChange(double Time, string Hand);
    private readonly StageCue[] _commands;
    private readonly List<SkillChange> _skills = new() { new(double.NegativeInfinity, "normal") };
    private readonly List<VisibilityChange> _visibility = new();
    private readonly List<CrowdAnimationRequest>[] _animations = Enumerable.Range(0, 8).Select(_ => new List<CrowdAnimationRequest>()).ToArray();
    private readonly List<HandChange>[] _hands = Enumerable.Range(0, 16).Select(_ => new List<HandChange>()).ToArray();
    private double _lastTime = double.NaN;
    public int Revision { get; private set; }

    public StageCrowdState(StageTimeline timeline)
    {
        _commands = timeline.PlaybackCues.Where(cue => cue.Type is "crowd-lighters" or "crowd-animation" or "crowd-hand").ToArray();
        foreach (var cue in _commands)
        {
            if (cue.Role.Length > 0) _ = Member(cue.Role);
            if (cue.Type == "crowd-lighters" && (cue.Role.Length > 0 || cue.Value is not ("start" or "stop" or "on" or "off")) ||
                cue.Type == "crowd-animation" && cue.Value is not ("idle" or "special" or "starpower") ||
                cue.Type == "crowd-hand" && !ValidHand(cue.Value))
                throw new InvalidDataException("Invalid crowd cue.");
        }
        Rebuild();
    }

    public static string Skill(float rock)
    {
        var health = Math.Clamp(float.IsFinite(rock) ? rock : 0.5f, 0, 1) * 2;
        return health < 0.66f ? "bad" : health > 1.3299999f ? "good" : "normal";
    }

    public void Update(double time, float rock)
    {
        if (!double.IsFinite(time) || Math.Abs(time) > StageLimits.MaximumSeconds) throw new ArgumentOutOfRangeException(nameof(time));
        var changed = false;
        if (time < _lastTime) changed = _skills.RemoveAll(sample => sample.Time > time) > 0;
        var skill = Skill(rock);
        if (_skills[^1].Skill != skill)
        {
            if (_skills[^1].Time == time) _skills.RemoveAt(_skills.Count - 1);
            _skills.Add(new(time, skill));
            changed = true;
        }
        _lastTime = time;
        if (changed) Rebuild();
    }

    public string SkillAt(double time) => Last(_skills, time, sample => sample.Time)?.Skill ?? "normal";
    public bool? VisibleAt(double time) => Last(_visibility, time, sample => sample.Time)?.Visible;
    public string HandAt(int member, bool right, double time) => Last(_hands[member * 2 + (right ? 1 : 0)], time, sample => sample.Time)?.Hand ?? "clap";
    public IReadOnlyList<CrowdAnimationRequest> Animations(int member) => _animations[member];

    private static T? Last<T>(IReadOnlyList<T> track, double time, Func<T, double> getTime) where T : class
    {
        var index = StageTimeline.UpperBound(track, time, getTime) - 1;
        return index < 0 ? null : track[index];
    }

    private void Rebuild()
    {
        _visibility.Clear();
        foreach (var track in _animations) track.Clear();
        foreach (var track in _hands) track.Clear();
        var queue = new PriorityQueue<Action<double>, (double Time, long Order)>();
        long order = 0;
        var generation = 0;
        var poll = 0;
        var active = false;
        var busy = false;
        var monitorOn = false;
        var nextPoll = 0.0;
        var skill = "normal";
        void Schedule(double time, Action<double> action) => queue.Enqueue(action, (time, order++));
        void Hands(double time, string hand, bool right, int member = -1)
        {
            for (var i = 0; i < 8; i++)
                if (member < 0 || member == i) _hands[i * 2 + (right ? 1 : 0)].Add(new(time, hand));
        }
        void Animate(double time, string animation, int? monitor, Action<double>? done = null)
        {
            for (var member = 0; member < 8; member++)
            {
                var index = member;
                Schedule(time + (index + 1) * GameFrame, at =>
                {
                    if (monitor.HasValue && monitor != generation) return;
                    _animations[index].Add(new(at, animation));
                    if (index == 7) done?.Invoke(at);
                });
            }
        }
        void Wake(double time, bool immediate = false)
        {
            if (!active || busy) return;
            var token = ++poll;
            var monitor = generation;
            var wakeAt = nextPoll + Math.Max(0, Math.Ceiling((time - nextPoll) / GameFrame - 1e-9)) * GameFrame;
            void Poll(double at)
            {
                if (token != poll || monitor != generation || !active || busy) return;
                nextPoll = at + GameFrame;
                var wanted = skill != "bad";
                if (wanted == monitorOn) return;
                busy = true;
                Hands(at, wanted ? "lighter" : "clap", true);
                Animate(at, wanted ? "special" : "idle", monitor, finished =>
                {
                    monitorOn = wanted;
                    _visibility.Add(new(finished, wanted));
                    busy = false;
                    nextPoll = finished + GameFrame;
                    Wake(nextPoll);
                });
            }
            if (immediate) Poll(time);
            else Schedule(wakeAt, Poll);
        }
        foreach (var sample in _skills)
            Schedule(sample.Time, at => { skill = sample.Skill; Wake(at); });
        foreach (var cue in _commands)
            Schedule(cue.Time, at =>
            {
                if (cue.Type == "crowd-animation")
                {
                    if (cue.Role.Length == 0) Animate(at, cue.Value, null);
                    else _animations[Member(cue.Role)].Add(new(at, cue.Value));
                    return;
                }
                if (cue.Type == "crowd-hand")
                {
                    var parts = cue.Value.Split('/');
                    Hands(at, parts[1], parts[0] == "right", cue.Role.Length == 0 ? -1 : Member(cue.Role));
                    return;
                }
                switch (cue.Value)
                {
                    case "start":
                        generation++;
                        active = true;
                        busy = false;
                        monitorOn = false;
                        nextPoll = at;
                        Wake(at, immediate: true);
                        break;
                    case "stop":
                        generation++;
                        active = false;
                        busy = false;
                        Hands(at, "clap", true);
                        Animate(at, "idle", null, finished => _visibility.Add(new(finished, false)));
                        break;
                    case "on": _visibility.Add(new(at, true)); break;
                    case "off": _visibility.Add(new(at, false)); break;
                }
            });
        while (queue.TryDequeue(out var action, out var priority)) action(priority.Time);
        Revision++;
    }

    public static int Member(string role) => role.Length == 6 && role.StartsWith("crowd", StringComparison.Ordinal) && role[5] is >= '1' and <= '8'
        ? role[5] - '1' : throw new InvalidDataException("Invalid crowd member.");

    public static bool ValidHand(string value) => value.Split('/') is { Length: 2 } parts &&
        parts[0] is "left" or "right" && parts[1] is "clap" or "lighter" or "rock" or "fist";
}
