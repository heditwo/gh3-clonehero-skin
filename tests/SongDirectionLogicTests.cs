using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Gh3Skin;

internal static class SongDirectionLogicTests
{
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "opencode", "gh3-song-direction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            CheckFiles(directory);
            if (Environment.GetEnvironmentVariable("GH3_PACK_ASSETS") is { } assets) CheckOriginal(directory, assets);
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("Version 2 song channel priority, clearing, timing, MIDI, validation, and legacy checks passed.");
    }

    private static void CheckFiles(string directory)
    {
        var library = Path.Combine(directory, "library");
        Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(library, "index.json"), """
            {"version":1,"songs":[{"title":"My Curse","artist":"Killswitch Engage","file":"mycurse.json"}]}
            """);
        var original = new[] {
            new StageCue(0, "gh3-camera", "85", 8), new StageCue(40, "camera", "drummer"),
            new StageCue(0, "gh3-note", "39"), new StageCue(1, "gh3-note", "75"), new StageCue(5, "gh3-note", "58"),
            new StageCue(0, "stance", "stance_a", Role: "guitarist"), new StageCue(20, "perform", "jump", Role: "guitarist"),
            new StageCue(25, "clip", "00000001", Role: "guitarist"), new StageCue(0, "stance", "stance_a", Role: "bassist"),
            new StageCue(10, "face", "00000001", Role: "singer"), new StageCue(11, "face", "00000001", Role: "guitarist"),
            new StageCue(0, "arms", "1", Role: "guitarist"), new StageCue(0, "strum-style", "normal", Role: "guitarist"),
            new StageCue(0, "walk", "guitarist_walk01", Role: "guitarist"),
            new StageCue(0, "fret", "125"), new StageCue(0, "fret", "108"), new StageCue(1, "drum", "36"),
            new StageCue(4, "strum", "31", Role: "guitarist"), new StageCue(4, "strum", "31", Role: "bassist"),
            new StageCue(6, "gh3-note", "56"), new StageCue(7, "pyro"), new StageCue(8, "smoke"),
            new StageCue(0, "tempo", "80"), new StageCue(0, "beat"), new StageCue(0.75, "beat")
        };
        File.WriteAllText(Path.Combine(library, "mycurse.json"), JsonSerializer.Serialize(new { version = 1, events = original }));
        var chartPath = Path.Combine(directory, "notes.chart");
        const string chart = """
            [Song]
            Resolution = 192
            Offset = 0.125
            [SyncTrack]
            0 = B 120000
            192 = B 60000
            [ExpertSingle]
            0 = N 0 384
            0 = N 2 384
            [ExpertDoubleBass]
            192 = N 1 0
            """;
        File.WriteAllText(chartPath, chart);
        var sidecar = Path.Combine(directory, "stage-events.json");
        void Write(string fields) => File.WriteAllText(sidecar, "{\"version\":2,\"library\":\"gh3-band-v1\"," + fields + "}");
        StageTimeline Load(StagePerformance? performance = null) => StageChartReader.Load(directory, chartPath, library,
            "MY CURSE!", "Killswitch Engage", performance);

        Write(""" "offsetSeconds":-1,"events":[{"time":12,"type":"camera","value":"singer","duration":4}] """);
        var cameras = Load();
        Check(cameras.Cameras.Single() == new StageCue(11, "camera", "singer", 4), "file camera cues replace the entire original camera channel");
        Check(cameras.Cues.Where(c => StageSongDirection.Channel(c) == "lighting").SequenceEqual(original.Where(c => StageSongDirection.Channel(c) == "lighting")),
            "a camera-only file retains original numeric lighting with unshifted times");
        Check(cameras.Track("perform", "guitarist").Single().Time == 20 && cameras.Track("face", "singer").Single().Time == 10,
            "a camera-only file retains body and face direction");
        Check(cameras.Track("strum", "guitarist").Single() == new StageCue(0.125, "strum", "5", 1.5, "guitarist"),
            "playable chord masks, note times, and sustains replace original strums");
        Check(cameras.Track("strum", "bassist").Single().Value == "2", "bass notes resolve separately");
        Check(cameras.Beats.Length == 0 && cameras.TempoAt(0.2) == 120 && cameras.TempoAt(0.7) == 60,
            "the chart tempo map replaces both original tempo and beat cues");
        Check(cameras.ChannelSources["camera"] == "stage-events.json" && cameras.ChannelSources["lighting"] == "mycurse.json",
            "the loader reports the selected channel sources");
        var light = cameras.Lighting(6);
        _ = cameras.Lighting(100);
        Check(cameras.Lighting(6) == light && cameras.CameraAt(11)?.Value == "singer", "resolved direction retains clock-based seek behavior");

        File.WriteAllText(chartPath, chart + "\n[Events]\n0 = E \"stage camera drummer\"\n192 = E \"stage pyro\"\n384 = E \"stage strum guitarist 9\"");
        var chartPriority = Load();
        Check(chartPriority.Cameras.Single().Value == "singer" && chartPriority.Effects.Count(c => c.Type == "pyro") == 1 &&
            chartPriority.Effects.Single(c => c.Type == "pyro").Time == 0.625, "file overrides chart cameras while chart pyro replaces both original pyro forms");
        Check(chartPriority.Track("strum", "guitarist").Single().Value == "9" && chartPriority.Track("strum", "bassist").Single().Value == "2",
            "explicit chart strums replace note-derived strums for only their role");
        Write(""" "events":[{"time":9,"type":"strum","role":"guitarist","value":"3"}] """);
        Check(Load().Track("strum", "guitarist").Single().Time == 9, "file strums take priority over explicit chart strums");

        Write("""
            "events":[
            {"time":2,"type":"perform","role":"guitarist","value":"jump"},
            {"time":0,"type":"blend","value":"0"},
            {"time":0,"type":"lighting","value":"red"},
            {"time":0,"type":"color","value":"blue"}]
            """);
        var grouped = Load();
        Check(grouped.Track("stance", "guitarist").Length == 0 && grouped.Track("clip", "guitarist").Length == 0 &&
            grouped.Track("perform", "guitarist").Single().Time == 2, "all guitarist body command types form one channel");
        Check(grouped.Track("stance", "bassist").Length == 1 && grouped.Track("walk", "guitarist").Length == 1 &&
            grouped.Track("arms", "guitarist").Length == 1, "body replacement keeps other roles, walking, and arm control");
        Check(!grouped.Cues.Any(c => c.Type == "gh3-note" && c.Value != "56") && grouped.Lighting(0) == StageColor.Named("blue"),
            "lighting replacement removes all inherited light state and preserves equal-time command order");

        Write(""" "clearChannels":["camera","lighting","body/guitarist","face/singer","strum/guitarist","fret/bassist","drum","pyro"],"events":[] """);
        var cleared = Load();
        Check(cleared.Cameras.Length == 0 && !cleared.Cues.Any(c => StageSongDirection.Channel(c) is "lighting" or "body/guitarist" or "face/singer" or "strum/guitarist" or "fret/bassist" or "drum" or "pyro"),
            "clearChannels suppresses file, chart, generated notes, and original events in each cleared channel");
        Check(cleared.Track("face", "guitarist").Length == 1 && cleared.Track("strum", "bassist").Length == 1 &&
            cleared.Effects.Single().Type == "smoke" && cleared.TempoAt(0.7) == 60, "clearing channels retains independent channels and chart timing");
        Write(""" "events":[{"time":3,"type":"fret","value":"120"}] """);
        Check(Load().Track("fret").Select(c => c.Value).SequenceEqual(new[] { "108", "120" }), "numeric frets resolve to separate instrument channels");

        Write(""" "events":[] """);
        Check(Load().Cameras.Single().Value == "drummer", "an empty version 2 file inherits chart direction");
        File.WriteAllText(chartPath, chart);
        Check(Load().Cameras.Length == 2, "omitted camera direction inherits the original library");
        Write(""" "events":[{"time":0,"type":"tempo","value":"200"}] """);
        Check(Load().TempoAt(0.2) == 120, "file timing cannot override the playable tempo map");
        File.WriteAllText(chartPath, "[SyncTrack]\n192 = B 60000");
        Check(Load().TempoAt(0) == 120 && Load().TempoAt(0.5) == 60, "chart timing retains the implicit 120 BPM before a later first tempo event");
        File.WriteAllText(chartPath, "[ExpertSingle]\n0 = N 0 0");
        Check(Load().TempoAt(0) == 200, "file timing is used when the chart has no tempo map");
        Write(""" "events":[] """);
        Check(Load().TempoAt(0) == 80, "original timing supplies the next fallback");
        Write(""" "originalSongLookup":"off","events":[] """);
        File.WriteAllText(Path.Combine(library, "index.json"), "not JSON");
        var disabled = Load();
        Check(disabled.Cameras.Length == 0 && disabled.Track("strum", "guitarist").Single().Value == "1" && disabled.TempoAt(0) == 115,
            "lookup off bypasses the library and retains gameplay notes and default tempo");

        var member = new PerformanceMember { Groups = new() { ["stance_a/idle/normal/med"] = new[] { "00000001" },
            ["stance_a/jump/normal/med"] = new[] { "00000001" }, ["walk/cycle_anim/med"] = new[] { "00000001" } }, Clips = new() { ["00000001"] = new() },
            Layers = new() { ["strum/normal/short"] = new[] { "00000001" } } };
        var performance = new StagePerformance { Members = new() { ["guitarist"] = member,
            ["drummer"] = new() { Groups = new() { ["stance_a/idle/normal/med"] = new[] { "00000001" } } } } };
        Write(""" "originalSongLookup":"off","events":[{"time":0,"type":"perform","role":"guitarist","value":"jump"}] """);
        Check(Load(performance).Track("perform", "guitarist").Length == 1, "authored actions resolve through the role catalog");
        Write(""" "originalSongLookup":"off","events":[{"time":0,"type":"perform","role":"drummer","value":"jump"}] """);
        Reject(() => Load(performance), "unresolved perform", "an action must exist for its target role");
        Write(""" "originalSongLookup":"off","events":[{"time":0,"type":"face","role":"guitarist","value":"deadbeef"}] """);
        Reject(() => Load(performance), "unresolved face", "unknown facial clips are reported");
        Write(""" "originalSongLookup":"off","events":[{"time":0,"type":"stance","role":"guitarist","value":"walk"}] """);
        Reject(() => Load(performance), "unresolved stance", "walking groups cannot be used as body stances");

        foreach (var fields in new[] {
            "\"events\":[],\"originalSongLookup\":\"yes\"", "\"events\":[],\"clearChannels\":[\"timing\"]",
            "\"events\":[],\"clearChannels\":[\"body/other\"]", "\"events\":[],\"clearChannels\":[\"strum/singer\"]",
            "\"events\":[],\"clearChannels\":[\"camera\",\"camera\"]", "\"events\":[],\"clearChannels\":null",
            "\"events\":[{\"time\":0,\"type\":\"camera\",\"value\":\"wide\"}],\"clearChannels\":[\"camera\"]",
            "\"events\":[{\"time\":0,\"type\":\"gh3-note\",\"value\":\"56\"}],\"clearChannels\":[\"pyro\"]",
            "\"events\":[{\"time\":0,\"type\":\"gh3-note\",\"value\":\"58\"}],\"clearChannels\":[\"lighting\"]",
            "\"events\":[{\"time\":0,\"type\":\"perform\",\"role\":\"guitarist\",\"value\":\"jump\"}],\"clearChannels\":[\"body/guitarist\"]",
            "\"events\":[{\"time\":0,\"type\":\"fret\",\"value\":\"125\"}],\"clearChannels\":[\"fret/guitarist\"]",
            "\"events\":[{\"type\":\"pyro\"}]", "\"events\":[{\"time\":0}]", "\"events\":[null]", "\"events\":null",
            "\"events\":[{\"time\":0,\"type\":\"perform\",\"value\":\"jump\"}]",
            "\"events\":[{\"time\":0,\"type\":\"strum\",\"role\":\"singer\",\"value\":\"1\"}]",
            "\"events\":[{\"time\":0,\"type\":\"strum\",\"role\":\"guitarist\",\"value\":\"32\"}]",
            "\"events\":[{\"time\":0,\"type\":\"fret\",\"value\":\"96\"}]",
            "\"events\":[{\"time\":0,\"type\":\"drum\",\"value\":\"96\"}]",
            "\"events\":[{\"time\":0,\"type\":\"gh3-note\",\"value\":\"1\"}]",
            "\"events\":[{\"time\":0,\"type\":\"crowd\",\"value\":\"1\"}]",
            "\"events\":[{\"time\":0,\"type\":\"tempo\",\"value\":\"NaN\"}]",
            "\"events\":[{\"time\":0,\"type\":\"pyro\",\"immediate\":1}]"
        })
        {
            Write(fields);
            Reject(() => Load(), "stage-events.json", "invalid version 2 fields fail with a file diagnostic: " + fields);
        }
        foreach (var json in new[] { "{\"version\":2,\"events\":[]}", "{\"version\":2,\"library\":\"other\",\"events\":[]}",
            "{\"version\":2,\"library\":\"gh3-band-v1\"}", "{" })
        {
            File.WriteAllText(sidecar, json);
            Reject(() => Load(), "stage-events.json", "required version 2 fields and JSON syntax are checked");
        }
        Write(""" "originalSongLookup":"off","offsetSeconds":2,"events":[{"time":86400,"type":"pyro"}] """);
        Reject(() => Load(), "Invalid stage cue", "the time limit applies after the file offset");

        File.WriteAllText(chartPath, "[Song]\nResolution = 0");
        File.WriteAllText(sidecar, "{\"version\":1,\"events\":[]}");
        Check(Load().CueCount == 0, "version 1 still replaces the entire timeline without loading chart or library");
        CheckMidi(directory);
    }

    private static void CheckMidi(string directory)
    {
        var path = Path.Combine(directory, "notes.mid");
        using (var output = File.Create(path))
        {
            output.Write(Encoding.ASCII.GetBytes("MThd"));
            output.Write(new byte[] { 0, 0, 0, 6, 0, 1, 0, 4, 1, 224 });
            Track(output, "TEMPO", new byte[] { 0, 255, 81, 3, 7, 161, 32 });
            Track(output, "PART GUITAR", new byte[] { 0, 144, 96, 100, 0, 98, 100, 131, 96, 128, 96, 0, 0, 98, 0 });
            Track(output, "PART DRUMS", new byte[] { 0, 144, 96, 100, 131, 96, 128, 96, 0 });
            Track(output, "GH3 DRUMS", new byte[] { 0, 144, 36, 100, 131, 96, 128, 36, 0 });
        }
        File.WriteAllText(Path.Combine(directory, "stage-events.json"), """
            {"version":2,"library":"gh3-band-v1","originalSongLookup":"off","events":[{"time":0,"type":"camera","value":"wide"}]}
            """);
        var timeline = StageChartReader.Load(directory, path);
        Check(timeline.Track("strum", "guitarist").Single() == new StageCue(0, "strum", "5", 0.5, "guitarist") && timeline.TempoAt(1) == 120,
            "camera-only files retain MIDI chords, note-off durations, and tempo maps");
        Check(timeline.Track("drum").Single().Value == "36", "GH3 drum-animation notes remain distinct from playable PART DRUMS notes");
    }

    private static void Track(Stream output, string name, byte[] events)
    {
        using var data = new MemoryStream();
        data.Write(new byte[] { 0, 255, 3, (byte)name.Length });
        data.Write(Encoding.ASCII.GetBytes(name));
        data.Write(events);
        data.Write(new byte[] { 0, 255, 47, 0 });
        output.Write(Encoding.ASCII.GetBytes("MTrk"));
        var size = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(size, (int)data.Length);
        output.Write(size);
        data.WriteTo(output);
    }

    private static void CheckOriginal(string directory, string assets)
    {
        var library = Path.Combine(assets, "stages", "events");
        var performance = StagePerformance.Load(Path.Combine(assets, "libraries", "gh3-band-v1", "direction", "performance.json"))!;
        var original = StageChartReader.Load("", "", library, "My Curse", "Killswitch Engage");
        File.WriteAllText(Path.Combine(directory, "stage-events.json"), """
            {"version":2,"library":"gh3-band-v1","events":[{"time":0,"type":"camera","value":"wide"},{"time":12,"type":"camera","value":"singer"}]}
            """);
        var resolved = StageChartReader.Load(directory, "", library, "My Curse", "Killswitch Engage", performance);
        var expected = original.Cues.Where(c => StageSongDirection.Channel(c) is not (null or "camera"));
        Check(resolved.Cues.Where(c => StageSongDirection.Channel(c) != "camera").SequenceEqual(expected),
            "a real My Curse camera override retains every supported original non-camera cue");
        Check(resolved.Cameras.Length == 2 && resolved.Track("drum").Length == 2916 && resolved.Track("face", "singer").Length == 5 && resolved.Effects.Length == 18,
            "the original My Curse drum, face, and effect channels remain complete");
        File.Copy(Path.Combine(assets, "..", "docs", "examples", "custom-content", "stage-events.json"), Path.Combine(directory, "stage-events.json"), true);
        var example = StageChartReader.Load(directory, "", library, "My Curse", "Killswitch Engage", performance);
        Check(example.Track("perform", "guitarist").Single().Value == "jump", "the documented example resolves against the installed action catalog");
        Console.WriteLine("Version 2 camera-only and documented full examples checked against the original My Curse library.");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new Exception(message);
    }

    private static void Reject(Action action, string expected, string message)
    {
        try { action(); }
        catch (InvalidDataException error) when (error.Message.Contains(expected, StringComparison.Ordinal)) { return; }
        throw new Exception(message);
    }
}
