using Gh3Skin;

internal static class ActorLogicTests
{
    public static void Run()
    {
        StageCue Call(double time, string name, uint? index = null)
        {
            var fields = index == null ? Array.Empty<Gh3QbField>() : new[] { new Gh3QbField(Gh3Checksum.Value("index"), new(1, -1, index.Value)) };
            return new(time, "gh3-script", new Gh3ScriptEvent("performance", new(10, -1, new[] {
                new Gh3QbField(Gh3Checksum.Value("time"), new(1, -1, (uint)Math.Max(0, time * 1000))),
                new Gh3QbField(Gh3Checksum.Value("scr"), new(13, -1, Gh3Checksum.Value(name))),
                new Gh3QbField(Gh3Checksum.Value("params"), new(10, -1, fields)) })).Encode());
        }
        var definition = new StageActorDefinition { Id = "01234567", Role = "stage-diver/1", Playback = "hold", InitiallyVisible = false,
            Actions = new() { ["jump"] = new[] { 0 } } };
        var source = new[] { Call(1, "Crowd_StageDiver_Jump"), Call(2, "Crowd_StageDiver_Hide"), Call(3, "Crowd_StageDiver_Jump"),
            Call(4, "Crowd_StageDiver_Jump", 2), Call(7, "Crowd_StageDiver_Jump"), Call(7, "Crowd_StageDiver_Hide") };
        var timeline = new StageTimeline(source, "actors");
        var actor = new StageActorState(definition, new[] { 2.0 }, timeline);
        Check(!actor.Sample(0).Visible, "Source diver starts hidden.");
        Check(actor.Sample(1.5) == new StageActorSample(0, .5, true), "Jump starts the original clip at its event time.");
        Check(actor.Sample(2.5) == new StageActorSample(0, 1.5, false), "Hide preserves the active animation clock.");
        Check(actor.Sample(4.5) == new StageActorSample(0, 1.5, true), "A missing diver index leaves existing actors unchanged.");
        Check(!actor.Sample(5).Visible, "Diver hides when the clip finishes.");
        Check(!actor.Sample(7).Visible, "Same-time hide follows jump.");
        var rewind = actor.Sample(1.5);
        Check(rewind == new StageActorSample(0, .5, true) && actor.Sample(1.5) == rewind, "Backward seek rebuilds actor state and pause holds it.");
        var native = new StageActorState(definition, new[] { 2.0 }, new StageTimeline(new[] { source[0] }, "native"));
        var authored = new StageActorState(definition, new[] { 2.0 }, StageChartReader.ReadChart("[Song]\nResolution = 1000\n[SyncTrack]\n0 = B 60000\n[Events]\n1000 = E gh3 " + source[0].Value));
        Check(native.Sample(1.25) == authored.Sample(1.25), "Imported and authored commands use the same actor playback.");
        var negative = new StageActorState(definition, new[] { 2.0 }, new StageTimeline(new[] { Call(-1, "Crowd_StageDiver_Jump") }, "negative"));
        Check(negative.Sample(0) == new StageActorSample(0, 1, true), "Negative-time setup advances the source animation.");
        var restarted = new StageActorState(definition, new[] { 2.0 }, new StageTimeline(new[] { source[0], Call(2, "Crowd_StageDiver_Jump") }, "restart"));
        Check(restarted.Sample(3.5).Visible && !restarted.Sample(4).Visible, "A restarted clip does not inherit the old completion deadline.");
        Check(timeline.Cues.SequenceEqual(source), "Actor playback retains every source row.");
        if (Environment.GetEnvironmentVariable("GH3_PACK_ASSETS") is { } assets)
        {
            foreach (var directory in Directory.EnumerateDirectories(Path.Combine(assets, "stages")))
            {
                var path = Path.Combine(directory, "actors.json");
                if (!File.Exists(path)) continue;
                foreach (var entry in StageActorBank.Load(path).Actors)
                {
                    using var reader = new BinaryReader(File.OpenRead(Path.Combine(directory, "actors", entry.Id, "animation.bin")));
                    var animation = new BandAnimation(reader);
                    var player = new StageActorState(entry, animation.Clips.Select(clip => (double)clip.Duration), timeline);
                    var frame = player.Sample(1.2);
                    Check(frame.Clip < animation.Clips.Length, "Prepared actor clip references resolve.");
                }
            }
        }
        Console.WriteLine("Actor source dispatch, completion, visibility, same-time order, pause, seek, and restart checks passed.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
