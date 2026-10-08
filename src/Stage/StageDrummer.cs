namespace Gh3Skin;

// GH3 drives the drummer from the song's drum notes (guitar_hand_events.qb Drum_iterator and Drum_cymbal_iterator,
// GH3.exe 0x42d730). Each arm, the kick foot, and each cymbal is a DegenerateBlend slot of drummer_static_tree:
// a new clip blends in linearly over the old output and plays once, then holds its last frame.
internal sealed class StageDrummer
{
    private readonly record struct Branch(double Start, string Clip, double Blend);
    private readonly record struct Hit(double Start, string Clip, double Blend, int Note);

    // play_drummer_*_arm_anim blends to the sticks-down clip over 0.6 s. GH3.exe passes BlendDuration only to the
    // left arm script, so right-arm hits and the count-off taps use the hero_play_anim default of 0.2 s.
    private const double StickdownBlend = 0.6;
    private const double DefaultBlend = 0.2;
    private const double TwistFrame = 1.0 / 60;

    private readonly Branch[][] _slots;
    private readonly string _twistClip;
    private readonly float[] _twist;

    public StageDrummer(PerformanceMember member, StageTimeline timeline, IReadOnlyDictionary<string, float> constants)
    {
        string? Layer(string name) => member.Layers.TryGetValue(name, out var refs) && refs.Length > 0 ? refs[0] : null;
        double Constant(string name, double fallback) => constants.TryGetValue(name, out var value) && float.IsFinite(value) ? value : fallback;
        var drumBlend = Constant("drum_blend_time", 0.1);
        var lead = Constant("drum_anim_lead_time", 150) / 1000;
        var blendOut = Constant("drummer_arm_blend_out_delay", 0.25);
        var clearTwist = Constant("drummer_clear_arm_twist_value_delay", 0);
        var hihatCountoff = timeline.Countoff.StartsWith("hihat", StringComparison.OrdinalIgnoreCase);
        var arms = new[] { new List<Hit>(), new List<Hit>() };
        var twistNotes = new List<(double Time, int Arm, int Note)>();
        var foot = new List<Branch>();
        var cymbals = Enumerable.Range(0, 4).Select(_ => new List<Branch>()).ToArray();
        foreach (var cue in timeline.Track("drum"))
        {
            if (!int.TryParse(cue.Value, out var note)) continue;
            var start = cue.Time - lead;
            if (note is >= 37 and <= 45 && Layer($"drum/{note}") is { } left) arms[0].Add(new(start, left, drumBlend, note));
            else if (note is >= 49 and <= 57 && Layer($"drum/{note}") is { } right) arms[1].Add(new(start, right, DefaultBlend, note));
            else if (note == 48 && Layer("drum/48") is { } kick) foot.Add(new(start, kick, drumBlend));
            else if (note == 70)
            {
                twistNotes.Add((start, 0, 0));
                twistNotes.Add((start, 1, 0));
                if (hihatCountoff)
                {
                    if (Layer("drum/53") is { } hat) arms[1].Add(new(start, hat, DefaultBlend, 0));
                }
                else
                {
                    if (Layer("sticktap_l") is { } tapLeft) arms[0].Add(new(start, tapLeft, DefaultBlend, 0));
                    if (Layer("sticktap_r") is { } tapRight) arms[1].Add(new(start, tapRight, DefaultBlend, 0));
                }
            }
            var cymbal = note switch { 44 or 56 => 0, 45 or 57 => 1, 43 or 55 => 2, 41 or 42 or 53 or 54 => 3, _ => -1 };
            if (cymbal >= 0 && Layer($"cymbal/{cymbal}") is { } swing) cymbals[cymbal].Add(new(cue.Time, swing, drumBlend));
        }
        var armSlots = new List<Branch>[2];
        for (var arm = 0; arm < 2; arm++)
        {
            var hits = arms[arm].OrderBy(h => h.Start).ToList();
            var down = Layer(arm == 0 ? "stickdown_l" : "stickdown_r");
            armSlots[arm] = new List<Branch>();
            for (var i = 0; i < hits.Count; i++)
            {
                var hit = hits[i];
                armSlots[arm].Add(new(hit.Start, hit.Clip, hit.Blend));
                if (hit.Note != 0) twistNotes.Add((hit.Start, arm, hit.Note));
                var next = i + 1 < hits.Count ? hits[i + 1].Start : double.PositiveInfinity;
                var lowered = hit.Start + member.Clips[hit.Clip].Duration + blendOut;
                if (down != null && lowered < next) armSlots[arm].Add(new(lowered, down, StickdownBlend));
                if (lowered + clearTwist < next) twistNotes.Add((lowered + clearTwist, arm, 0));
            }
        }
        _slots = new[] { armSlots[0], armSlots[1], foot }.Concat(cymbals).Select(slot => slot.OrderBy(b => b.Start).ToArray()).ToArray();
        _twistClip = Layer("bodytwist") ?? "";
        _twist = _twistClip.Length == 0 ? Array.Empty<float>() : Twist(twistNotes, constants, Constant("drummer_twist_rate", 0.05));
    }

    public void Apply(BandPose[] pose, BandPose[] bind, double time, Func<string, PerformanceClip> clip)
    {
        foreach (var branches in _slots)
        {
            var last = StageTimeline.UpperBound(branches, time, b => b.Start) - 1;
            if (last < 0) continue;
            var first = last;
            while (first > 0 && last - first < 16 && Weight(branches[first], time) < 1) first--;
            for (var i = first; i <= last; i++)
                clip(branches[i].Clip).Apply(pose, bind, time - branches[i].Start, weight: Weight(branches[i], time));
        }
        if (_twist.Length == 0) return;
        var strength = _twist[Math.Clamp((int)(time / TwistFrame), 0, _twist.Length - 1)];
        if (strength > 0) clip(_twistClip).Apply(pose, bind, 0, true, strength);
    }

    private static float Weight(Branch branch, double time) =>
        branch.Blend <= 0 ? 1 : (float)Math.Clamp((time - branch.Start) / branch.Blend, 0, 1);

    // drummer_autotwist (guitar_character.qb) moves the twist strength toward the mean of both arms' factors
    // by drummer_twist_rate each game frame.
    private static float[] Twist(List<(double Time, int Arm, int Note)> notes, IReadOnlyDictionary<string, float> constants, double rate)
    {
        float Factor(int arm, int note) => note == 0 ? 0 :
            constants.GetValueOrDefault($"drummer_{(arm == 0 ? "left" : "right")}_arm_twist_factors/{note}");
        var events = notes.OrderBy(n => n.Time).ToArray();
        var end = events.Length == 0 ? 0 : Math.Min(3600, events[^1].Time + 5);
        var twist = new float[(int)(Math.Max(0, end) / TwistFrame) + 2];
        var current = new int[2];
        var value = 0.0;
        var index = 0;
        for (var frame = 0; frame < twist.Length; frame++)
        {
            while (index < events.Length && events[index].Time <= frame * TwistFrame)
            {
                current[events[index].Arm] = events[index].Note;
                index++;
            }
            var desired = (Factor(0, current[0]) + Factor(1, current[1])) * 0.5;
            value = Math.Abs(desired - value) < rate ? desired : value + Math.Sign(desired - value) * rate;
            twist[frame] = (float)value;
        }
        return twist;
    }
}
