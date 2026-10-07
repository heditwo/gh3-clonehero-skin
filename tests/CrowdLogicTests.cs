using Gh3Skin;

internal static class CrowdLogicTests
{
    public static void Run()
    {
        var timeline = Timeline(Call(1, "Crowd_StartLighters"), Call(5, "Crowd_StopLighters"));
        var state = new StageCrowdState(timeline);
        Check(state.VisibleAt(0) == null && state.HandAt(0, true, 0) == "clap", "Initial crowd geometry keeps its stage-authored visibility.");
        Check(state.HandAt(0, true, 1) == "lighter" && state.HandAt(0, false, 1) == "clap", "The monitor changes only the right hands.");
        Check(state.Animations(0)[0].Time == 1 + StageCrowdState.GameFrame && state.Animations(7)[0].Time == 1 + 8 * StageCrowdState.GameFrame,
            "AllPlayAnim retains the source's one-frame stagger across eight profiles.");
        Check(state.VisibleAt(1.1) == null && state.VisibleAt(1.14) == true, "Venue lighters turn on after all eight animation calls.");
        state.Update(2, 0.2f);
        Check(state.HandAt(0, true, 2) == "clap" && state.VisibleAt(2.14) == false, "Poor performance stops the lighter gesture and venue geometry.");
        state.Update(3, 0.8f);
        Check(state.HandAt(7, true, 3) == "lighter" && state.VisibleAt(3.14) == true, "The live monitor restores lighters when performance recovers.");
        Check(state.HandAt(0, true, 5) == "clap" && state.VisibleAt(5.14) == false, "Stop kills the monitor, resets hands, and completes its delayed geometry change.");
        var revision = state.Revision;
        state.Update(3, 0.8f);
        Check(state.Revision == revision, "Pause does not rebuild or restart crowd commands.");
        state.Update(1.2, 0.5f);
        Check(state.VisibleAt(1.2) == true && state.Animations(0).Count == 2, "Backward seek removes future performance samples and restores the cue state.");

        var interrupted = new StageCrowdState(Timeline(Call(1, "Crowd_StartLighters"), Call(1.02, "Crowd_StopLighters")));
        Check(interrupted.Animations(0).Any(request => request.Animation == "special") && !interrupted.Animations(1).Any(request => request.Animation == "special"),
            "Stopping a suspended monitor cancels its remaining per-person animation calls.");
        Check(interrupted.VisibleAt(1.14) == null && interrupted.VisibleAt(1.16) == false, "A cancelled monitor cannot turn the geometry on later.");
        var sameTime = new StageCrowdState(Timeline(Call(1, "Crowd_StartLighters"), Call(1, "Crowd_StopLighters")));
        Check(sameTime.HandAt(0, true, 1) == "clap" && sameTime.VisibleAt(1.2) == false, "Same-time calls retain their source order.");
        var sameTimeHand = new StageCrowdState(Timeline(Call(1, "Crowd_StartLighters"), Call(1, "Crowd_SetHand",
            Field("hand", Key("right")), Field("Type", Key("fist")))));
        Check(sameTimeHand.HandAt(0, true, 1) == "fist" && sameTimeHand.HandAt(1, true, 1) == "lighter",
            "SpawnScriptNow runs to its first wait before the next same-time song command.");
        var polling = new StageCrowdState(timeline);
        polling.Update(2.001, 0.1f);
        Check(polling.HandAt(0, true, 2.01) == "lighter" && polling.HandAt(0, true, 2.017) == "clap",
            "The monitor waits for its next game frame when health changes between polls.");
        var namedToggle = new StageCrowdState(Timeline(Call(0, "Crowd_ToggleLighters", Field("on", new(1, -1, 0u)), new Gh3QbField(0, Key("off"))),
            Call(1, "Crowd_ToggleLighters")));
        Check(namedToggle.VisibleAt(2) == true, "Toggle uses parameter presence, gives ON priority, and keeps a no-parameter call as a no-op.");
        var setup = new StageCrowdState(new StageTimeline(new[] { Call(0, "Crowd_StartLighters") with { Time = -2 },
            Call(0, "Crowd_StopLighters") with { Time = -1 } }, "negative setup"));
        Check(setup.VisibleAt(-1.5) == true && setup.VisibleAt(0) == false,
            "Negative setup cues finish their waits before song time zero.");
        var badStart = new StageCrowdState(Timeline(Call(0, "Crowd_ToggleLighters", new Gh3QbField(0, Key("on"))), Call(1, "Crowd_StartLighters")));
        badStart.Update(0, 0.1f);
        Check(badStart.VisibleAt(2) == true && badStart.Animations(0).Count == 0,
            "Starting with bad skill does not invent the OFF call that the original monitor omits.");
        Check(StageCrowdState.Skill(0.32f) == "bad" && StageCrowdState.Skill(0.33f) == "normal" && StageCrowdState.Skill(0.67f) == "good",
            "The player-health adapter uses get_skill_level's source thresholds.");
        var hands = new StageCrowdState(Timeline(Call(1, "Crowd_SetHand", Field("name", Key("crowd2")), Field("hand", Key("left")), Field("Type", Key("fist"))),
            Call(2, "Crowd_PlayAnim", Field("name", Key("crowd2")), Field("anim", Key("starpower")))));
        Check(hands.HandAt(1, false, 1) == "fist" && hands.HandAt(0, false, 1) == "clap" && hands.Animations(1).Single().Animation == "starpower",
            "Explicit crowd commands select their original member, hand, and animation.");
        foreach (var fps in new[] { 30, 60, 144 })
        {
            var sampled = new StageCrowdState(timeline);
            for (var frame = 0; frame <= fps * 6; frame++)
            {
                var time = frame / (double)fps;
                sampled.Update(time, time >= 2 && time < 3 ? 0.2f : 0.5f);
            }
            Check(sampled.VisibleAt(2.14) == false && sampled.VisibleAt(3.14) == true && sampled.VisibleAt(5.14) == false,
                "Monitor waits and cancellation are independent of render frame rate.");
        }
        if (Environment.GetEnvironmentVariable("GH3_CROWD_ASSETS") is { } directory) CheckAssets(directory, timeline);
        Console.WriteLine("Crowd monitor, delayed calls, cancellation, hand selection, health transitions, pause, seek, and frame-rate checks passed.");
    }

    private static void CheckAssets(string directory, StageTimeline timeline)
    {
        var bank = CrowdDefinition.Load(Path.Combine(directory, "crowd.json"));
        Check(bank.Version == 2 && bank.Members.Count == 8 && bank.Hands.Length == 8, "The prepared library has all crowd profiles and hand variants.");
        foreach (var info in bank.PerformanceClips)
        {
            using var stream = File.OpenRead(Path.Combine(directory, "clips", info.Key + ".bin.gz"));
            _ = new PerformanceClip(stream, bank.Bones.Length);
        }
        var clips = new Dictionary<string, PerformanceClip>();
        PerformanceClip Clip(string id)
        {
            if (clips.TryGetValue(id, out var value)) return value;
            using var stream = File.OpenRead(Path.Combine(directory, "clips", id + ".bin.gz"));
            return clips[id] = new PerformanceClip(stream, bank.Bones.Length);
        }
        var state = new StageCrowdState(timeline);
        state.Update(2, 0.2f);
        state.Update(3, 0.8f);
        foreach (var (role, member) in bank.Members)
        {
            using var reader = new BinaryReader(File.OpenRead(Path.Combine(directory, member.Model, "animation.bin")));
            var animation = new BandAnimation(reader);
            var player = new CrowdAnimationPlayer(bank, role, 0, timeline, state);
            var pose = new BandPose[animation.Parents.Length];
            var previous = new BandPose[pose.Length];
            var sampleTime = state.Animations(StageCrowdState.Member(role))[0].Time + 0.01;
            var segment = player.At(sampleTime);
            Check(member.Groups["special/normal/med"].Contains(segment.Clip), "A lighter command uses that profile's source special animation.");
            for (var frame = 0; frame <= 60 * 12; frame++)
            {
                player.Sample(frame / 60.0, pose, previous, animation.BindPose, Clip);
                Check(pose.All(bone => float.IsFinite(bone.Position.LengthSquared()) && Math.Abs(bone.Rotation.LengthSquared() - 1) < 0.001),
                    "Original crowd animation poses stay finite and normalized.");
            }
            player.Sample(3.5, pose, previous, animation.BindPose, Clip);
            var held = pose.ToArray();
            player.Sample(60, pose, previous, animation.BindPose, Clip);
            player.Sample(3.5, pose, previous, animation.BindPose, Clip);
            Check(pose.SequenceEqual(held), "Seeking restores the same crowd animation pose and selection.");
        }
        Console.WriteLine($"Checked {bank.PerformanceClips.Count} original crowd clips and all eight source profiles.");
    }

    private static StageTimeline Timeline(params StageCue[] cues) => new(cues, "crowd test");
    private static StageCue Call(double time, string name, params Gh3QbField[] parameters) => new(time, "gh3-script",
        new Gh3ScriptEvent("performance", new(10, -1, new Gh3QbField[] {
            Field("time", new(1, -1, (uint)(time * 1000))), Field("scr", Key(name)), Field("params", new(10, -1, parameters))
        })).Encode());
    private static Gh3QbValue Key(string name) => new(13, -1, Gh3Checksum.Value(name));
    private static Gh3QbField Field(string name, Gh3QbValue value) => new(Gh3Checksum.Value(name), value);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
