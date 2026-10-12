using System.Text.Json;
using Gh3Skin;

internal static class AudioLogicTests
{
    private static Gh3QbValue Key(string text) => new(13, -1, Gh3Checksum.Value(text));
    private static Gh3QbValue Number(int value) => new(1, -1, unchecked((uint)value));
    private static Gh3QbValue Struct(params (string Name, Gh3QbValue Value)[] fields) => new(10, -1,
        fields.Select(field => new Gh3QbField(field.Name.Length == 0 ? 0 : Gh3Checksum.Value(field.Name), field.Value)).ToArray());
    private static StageCue Call(double time, string command, params (string Name, Gh3QbValue Value)[] parameters) => new(time, "gh3-script",
        new Gh3ScriptEvent("performance", Struct(("time", Number((int)(time * 1000))), ("scr", Key(command)), ("params", Struct(parameters)))).Encode());

    public static void Run()
    {
        Check(Gh3Checksum.Append(Gh3Checksum.Value("Mixed_Name"), "_container") == Gh3Checksum.Value("Mixed_Name_container"), "Sound event suffixes retain native checksum semantics.");
        var directory = Path.Combine(Path.GetTempPath(), "opencode", "gh3-sound-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var definitions = new Dictionary<string, JsonElement>();
            void Define(string name, Gh3QbValue value) => definitions[Gh3Checksum.Of(name)] = JsonDocument.Parse(Gh3QbText.Encode(value)).RootElement.Clone();
            Define("sample_event_struct", Struct(("InstanceLimit", Number(1)), ("InstanceManagement", Key("stop_oldest")), ("NoRepeatFor", new(2, -1, .1f))));
            Define("sample_event_container", Struct(("Command", Key("PlaySound")), ("Randomness", Key("None")), ("Sounds", Struct(("Sound1", Struct(("", Key("sample"))))))));
            Define("DoNothing_OneShot_container", Struct(("Command", Key("PlaySound"))));
            var bankFile = Path.Combine(directory, "audio.json");
            File.WriteAllText(bankFile, JsonSerializer.Serialize(new StageSoundBank { Version = 1, Definitions = definitions, Samples = new() {
                [Gh3Checksum.Of("sample")] = new() { Rate = 8000, Channels = 1, Frames = 16000, LoopEnd = 16000 } } }));
            var bank = StageSoundBank.Load(bankFile);
            var events = new StageSoundEvents(bank);
            var sampleEvent = Gh3Checksum.Value("sample_event");
            events.Play(sampleEvent, 1);
            events.Play(sampleEvent, 1.05);
            Check(events.Voices.Count == 1, "NoRepeatFor prevents duplicate sound triggers.");
            events.Play(sampleEvent, 1.2);
            Check(events.Voices.Count == 2 && events.Voices[0].End == 1.2, "Instance limits stop the oldest playing event.");
            events.Stop(sampleEvent, 1.5);
            Check(events.Voices[1].End == 1.5, "StopSoundEvent stops the event's remaining voices.");
            events.Play(Gh3Checksum.Value("DoNothing_OneShot"), 2);
            var source = new[] { Call(1, "SoundEvent", ("event", Key("sample_event"))), Call(2, "StopSoundEvent", ("", Key("sample_event"))) };
            var state = new StageAudioState(bank, new StageTimeline(source, "audio"), "example.studio", "sticks_normal");
            state.Update(-2, .5f);
            Check(state.Voices.Count == 0, "Song lead-in time does not start future sounds.");
            state.Update(1.25, .5f);
            Check(state.Voices.Count == 1 && state.Voices[0].Start == 1, "Authored sound starts at its event time.");
            state.Update(1.25, .5f);
            Check(state.Voices.Count == 1, "Paused time does not duplicate sound events.");
            state.Update(2.25, .5f);
            Check(state.Voices.Count == 0, "Stopped sounds are removed after their stop time.");
            state.Update(1.25, .5f);
            Check(state.Voices.Count == 1 && state.Voices[0].Start == 1, "Backward seek restores sounds with their original start time.");
            var locking = new StageAudioState(bank, new StageTimeline(new[] {
                Call(0, "SoundEvent", ("event", Key("sample_event"))),
                Call(.1, "SoundBussLock", ("", Key("Default"))),
                Call(.2, "SetSoundBussParams", ("Default", Struct(("vol", Number(-20))))),
                Call(.3, "SoundBussUnlock", ("", Key("Default"))),
                Call(.4, "SetSoundBussParams", ("Default", Struct(("vol", Number(-20)))), ("time", Number(1))),
                Call(.5, "SoundBussLock", ("", Key("Default"))) }, "locks"), "example.studio", "sticks_normal");
            locking.Update(.25, .5f);
            Check(locking.Gain(locking.Voices.Single(), .25) == 1, "Locked sound buses ignore parameter changes.");
            locking.Update(.9, .5f);
            Check(Math.Abs(locking.Gain(locking.Voices.Single(), .9) - Math.Pow(10, -.5)) < .00001,
                "A new lock retains the fade already scheduled by an unlocked command.");
            locking.Update(.25, .5f);
            Check(locking.Gain(locking.Voices.Single(), .25) == 1 && locking.Diagnostics.Count == 0, "Backward seek rebuilds sound bus locks and fades.");
        }
        finally { Directory.Delete(directory, true); }
        var mapping = new StageTimeline(new[] { new StageCue(1, "drum", "70", .3, Velocity: 74), new StageCue(2, "crowd", "78") }, "notes");
        var calls = mapping.Track("gh3-audio").Select(cue => Gh3ScriptEvent.Decode(cue.Value)).ToArray();
        Check(calls.Length == 2 && calls[0].Parameters!.Field("velocity")!.Integer == 74 && calls[0].Parameters!.Field("length")!.Integer == 300,
            "Count-off mappings pass the original note velocity and length.");
        Check(calls[1].Parameters!.Field("override_state")!.Integer == 1 && mapping.Cues.Length == 2,
            "Crowd note overrides retain original source notes.");
        if (Environment.GetEnvironmentVariable("GH3_AUDIO_ASSETS") is { } assets) Original(assets);
        Console.WriteLine("Sound event instance limits, count-off velocity, crowd overrides, shared dispatch, pause, and seek checks passed.");
    }

    private static void Original(string directory)
    {
        var bank = StageSoundBank.Load(Path.Combine(directory, "audio.json"));
        var voices = new StageSoundEvents(bank);
        foreach (var instrument in new[] { "sticks_normal", "sticks_huge", "sticks_tiny", "hihat01", "hihat02", "hihat03" })
            foreach (var strength in new[] { "Hard", "Med", "Soft" })
                voices.Play(Gh3Checksum.Value("Countoff_SFX_" + instrument + "_" + strength), 0);
        Check(voices.Voices.Count == 18, "All original count-off instruments and velocity groups resolve their samples.");
        foreach (var sample in bank.Samples)
        {
            using var stream = new System.IO.Compression.GZipStream(File.OpenRead(Path.Combine(directory, sample.Key + ".pcm.gz")), System.IO.Compression.CompressionMode.Decompress);
            long size = 0;
            var buffer = new byte[8192];
            int count;
            while ((count = stream.Read(buffer)) > 0) size += count;
            Check(size == sample.Value.Frames * sample.Value.Channels * 2, "Decoded audio length matches the source sample metadata.");
        }
        foreach (var venue in new[] { "party", "dive", "video", "artdeco", "prison", "wikker", "budokan", "hell", "soundcheck", "credits" })
        {
            var source = new[] { new StageCue(1, "crowd", "76"), new StageCue(2, "crowd", "73"),
                new StageCue(3, "crowd", "78"), new StageCue(4, "crowd", "77"), new StageCue(5, "drum", "70", Velocity: 75) };
            var low = new StageAudioState(bank, new StageTimeline(source, "original"), "z_" + venue, "sticks_normal");
            low.Update(0, .1f);
            low.Update(1, .1f);
            Check(low.Voices.Count == 1 && low.Voices[0].Loop, "Poor performance suppresses unforced crowd surges but retains the crowd bed.");
            low.Update(2, .1f);
            Check(low.Voices.Count > 1, "Override notes trigger the source venue swell during poor performance: " + venue + " " + string.Join("; ", low.Diagnostics));
            low.Update(5, .1f);
            Check(low.Diagnostics.Count == 0, "Every tested venue reaction and count-off resolves its sound definition.");
            var high = new StageAudioState(bank, new StageTimeline(new[] { source[0] }, "surge"), "z_" + venue, "sticks_normal");
            high.Update(0, .9f);
            high.Update(.9, .9f);
            var bed = high.Voices.Single(voice => voice.Loop);
            var initial = high.Gain(bed, .9);
            high.Update(2.2, .9f);
            Check(Math.Abs(high.Gain(bed, 2.2) / initial - Math.Pow(10, .5)) < .0001, "A fast surge raises the source crowd bed from -7 dB to +3 dB over 1.2 seconds.");
            high.Update(6.5, .9f);
            Check(Math.Abs(high.Gain(bed, 6.5) / initial - Math.Pow(10, .05)) < .0001, "The delayed fast-surge reset reaches -6 dB after four seconds.");
            var transition = new StageAudioState(bank, new StageTimeline(Array.Empty<StageCue>(), "crossfade"), "z_" + venue, "sticks_normal");
            transition.Update(0, .5f);
            transition.Update(.1, .9f);
            transition.Update(.2, .1f);
            transition.Update(.3, .9f);
            transition.Update(30, .9f);
            Check(transition.Diagnostics.Count == 0, "Interrupted source audio fades and one-shot loops run without missing functions: " + string.Join("; ", transition.Diagnostics));
            Check(transition.Voices.Count(voice => voice.Loop) == 1, "Repeated health changes leave one crowd background after their fades finish.");
            var expected = transition.Voices.Select(voice => (voice.Sample, voice.Start, voice.End, voice.Gain)).ToArray();
            transition.Update(.4, .9f);
            transition.Update(30, .9f);
            Check(expected.SequenceEqual(transition.Voices.Select(voice => (voice.Sample, voice.Start, voice.End, voice.Gain))), "Backward seek reproduces interrupted fades and source random one-shots.");
            var training = new StageAudioState(bank, new StageTimeline(Array.Empty<StageCue>(), "training"), "z_" + venue, "sticks_normal") { Training = true };
            training.Update(0, .9f);
            training.Update(10, .1f);
            Check(training.Voices.Count == 0 && training.Diagnostics.Count == 0, "Practice mode does not start performance crowd beds.");
        }
        Console.WriteLine($"Read all {bank.Samples.Count} original audio samples and checked count-offs and crowd reactions in ten venues.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
