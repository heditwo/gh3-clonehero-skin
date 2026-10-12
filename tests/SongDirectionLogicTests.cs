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
        Console.WriteLine("Version 2 song channel priority, clearing, timing, suggestions, MIDI, validation, and version 1 checks passed.");
    }

    private static void CheckFiles(string directory)
    {
        // The GH3 direction of a song as a chart pack sidecar has it (pipeline.song_direction), here without strums and timing.
        var direction = new[] {
            new StageCue(0, "gh3-camera", "85", 8), new StageCue(40, "camera", "drummer"),
            new StageCue(0, "gh3-note", "39"), new StageCue(1, "gh3-note", "75"), new StageCue(5, "gh3-note", "58"),
            new StageCue(0, "stance", "stance_a", Role: "guitarist"), new StageCue(20, "perform", "jump", Role: "guitarist"),
            new StageCue(25, "clip", "00000001", Role: "guitarist"), new StageCue(0, "stance", "stance_a", Role: "bassist"),
            new StageCue(10, "face", "00000001", Role: "singer"), new StageCue(11, "face", "00000001", Role: "guitarist"),
            new StageCue(0, "arms", "1", Role: "guitarist"), new StageCue(0, "strum-style", "normal", Role: "guitarist"),
            new StageCue(0, "walk", "guitarist_walk01", Role: "guitarist"),
            new StageCue(0, "fret", "125"), new StageCue(0, "fret", "108"), new StageCue(1, "drum", "36"),
            new StageCue(6, "gh3-note", "56"), new StageCue(7, "pyro"), new StageCue(8, "smoke")
        };
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
        void WriteDirection(double offset) => File.WriteAllText(sidecar, JsonSerializer.Serialize(new
        {
            version = 2, library = "gh3-band-v1", sourceSong = "mycurse", countoff = "HiHat01", offsetSeconds = offset, stage = "gh3.dive",
            characters = new Dictionary<string, string> { ["singer"] = "gh3.singer-female" }, events = direction
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        StageTimeline Load(StagePerformance? performance = null) => StageChartReader.Load(directory, chartPath, performance);

        WriteDirection(-1);
        var full = Load();
        Check(full.Cues.Where(c => StageSongDirection.Channel(c) is not (null or "timing" or "strum/guitarist" or "strum/bassist"))
                .SequenceEqual(direction.Select(c => c with { Time = c.Time - 1 }).OrderBy(c => c.Time)),
            "the file supplies every direction channel, shifted by its offset");
        Check(full.Cameras.Length == 2 && full.Cameras[0].Time == -1 && full.SourceSong == "mycurse" && full.Countoff == "HiHat01",
            "the file keeps its source song and count-off");
        Check(full.Track("strum", "guitarist").Single() == new StageCue(0.125, "strum", "5", 1.5, "guitarist"),
            "playable chord masks, note times, and sustains supply the strums");
        Check(full.Track("strum", "bassist").Single().Value == "2", "bass notes resolve separately");
        Check(full.Beats.Length == 0 && full.TempoAt(0.2) == 120 && full.TempoAt(0.7) == 60, "the chart tempo map supplies the timing");
        Check(full.ChannelSources["camera"] == "stage-events.json" && full.ChannelSources["strum/guitarist"] == "notes.chart Expert notes",
            "the loader reports the selected channel sources");
        var light = full.Lighting(5);
        _ = full.Lighting(100);
        Check(full.Lighting(5) == light && full.CameraAt(39)?.Value == "drummer", "resolved direction retains clock-based seek behavior");

        File.WriteAllText(chartPath, chart + "\n[Events]\n0 = E \"stage camera singer\"\n192 = E \"stage sparks\"\n384 = E \"stage strum guitarist 9\"");
        var chartPriority = Load();
        Check(chartPriority.Cameras.Length == 2 && chartPriority.Cameras.All(c => c.Value != "singer") &&
            chartPriority.Effects.Single(c => c.Type == "sparks").Time == 0.625, "the file overrides chart cameras; chart sparks fill a free channel");
        Check(chartPriority.Track("strum", "guitarist").Single().Value == "9" && chartPriority.Track("strum", "bassist").Single().Value == "2",
            "explicit chart strums replace note-derived strums for only their role");
        Write(""" "events":[{"time":9,"type":"strum","role":"guitarist","value":"3"}] """);
        Check(Load().Track("strum", "guitarist").Single().Time == 9, "file strums take priority over explicit chart strums");
        Write(""" "events":[] """);
        Check(Load().Cameras.Single().Value == "singer", "an empty version 2 file inherits chart direction");

        Write("""
            "events":[
            {"time":2,"type":"perform","role":"guitarist","value":"jump"},
            {"time":0,"type":"blend","value":"0"},
            {"time":0,"type":"lighting","value":"red"},
            {"time":0,"type":"color","value":"blue"}]
            """);
        var grouped = Load();
        Check(grouped.Track("perform", "guitarist").Single().Time == 2 && grouped.Lighting(0) == StageColor.Named("blue"),
            "body and lighting commands keep their equal-time order");

        Write(""" "clearChannels":["camera","strum/guitarist","sparks"],"events":[] """);
        var cleared = Load();
        Check(cleared.Cameras.Length == 0 && cleared.Track("strum", "guitarist").Length == 0 && !cleared.Effects.Any(),
            "clearChannels suppresses chart and generated cues in each cleared channel");
        Check(cleared.Track("strum", "bassist").Length == 1 && cleared.TempoAt(0.7) == 60, "clearing channels retains independent channels and chart timing");
        File.WriteAllText(chartPath, chart);
        Write(""" "events":[{"time":3,"type":"fret","value":"120"},{"time":3,"type":"fret","value":"108"}] """);
        Check(Load().Track("fret").Select(c => c.Value).SequenceEqual(new[] { "120", "108" }), "numeric frets resolve to separate instrument channels");

        Write(""" "events":[{"time":0,"type":"tempo","value":"200"}] """);
        Check(Load().TempoAt(0.2) == 120, "file timing cannot override the playable tempo map");
        File.WriteAllText(chartPath, "[SyncTrack]\n192 = B 60000");
        Check(Load().TempoAt(0) == 120 && Load().TempoAt(0.5) == 60, "chart timing retains the implicit 120 BPM before a later first tempo event");
        File.WriteAllText(chartPath, "[ExpertSingle]\n0 = N 0 0");
        Check(Load().TempoAt(0) == 200, "file timing is used when the chart has no tempo map");
        Write(""" "events":[] """);
        Check(Load().TempoAt(0) == 115 && Load().Track("strum", "guitarist").Single().Value == "1", "without file timing the default tempo applies");

        var member = new PerformanceMember { Groups = new() { ["stance_a/idle/normal/med"] = new[] { "00000001" },
            ["stance_a/jump/normal/med"] = new[] { "00000001" }, ["walk/cycle_anim/med"] = new[] { "00000001" } }, Clips = new() { ["00000001"] = new() },
            Layers = new() { ["strum/normal/short"] = new[] { "00000001" } } };
        var performance = new StagePerformance { Members = new() { ["guitarist"] = member,
            ["drummer"] = new() { Groups = new() { ["stance_a/idle/normal/med"] = new[] { "00000001" } } } } };
        Write(""" "events":[{"time":0,"type":"perform","role":"guitarist","value":"jump"}] """);
        Check(Load(performance).Track("perform", "guitarist").Length == 1, "authored actions resolve through the role catalog");
        Write(""" "events":[{"time":0,"type":"perform","role":"drummer","value":"jump"}] """);
        Reject(() => Load(performance), "unresolved perform", "an action must exist for its target role");
        Write(""" "events":[{"time":0,"type":"face","role":"guitarist","value":"deadbeef"}] """);
        Reject(() => Load(performance), "unresolved face", "unknown facial clips are reported");
        Write(""" "events":[{"time":0,"type":"stance","role":"guitarist","value":"walk"}] """);
        Reject(() => Load(performance), "unresolved stance", "walking groups cannot be used as body stances");

        foreach (var fields in new[] {
            "\"events\":[],\"stage\":\"Not An Id\"", "\"events\":[],\"characters\":{\"keyboard\":\"gh3.judy\"}",
            "\"events\":[],\"characters\":{\"singer\":\"\"}", "\"events\":[],\"clearChannels\":[\"timing\"]",
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
            "\"events\":[{\"time\":0,\"type\":\"fret\",\"value\":\"128\"}]",
            "\"events\":[{\"time\":0,\"type\":\"drum\",\"value\":\"-1\"}]",
            "\"events\":[{\"time\":0,\"type\":\"gh3-note\",\"value\":\"128\"}]",
            "\"events\":[{\"time\":0,\"type\":\"crowd\",\"value\":\"no-note\"}]",
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
        Write(""" "offsetSeconds":2,"events":[{"time":86400,"type":"pyro"}] """);
        Reject(() => Load(), "Invalid stage cue", "the time limit applies after the file offset");

        File.WriteAllText(chartPath, "[Song]\nResolution = 0");
        File.WriteAllText(sidecar, "{\"version\":1,\"events\":[]}");
        Check(Load().CueCount == 0, "version 1 still replaces the entire timeline without loading the chart");
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
            {"version":2,"library":"gh3-band-v1","events":[{"time":0,"type":"camera","value":"wide"}]}
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

    // The GH3 direction of My Curse as the chart pack sidecar (pipeline.song_direction) against the GH3 export of the song.
    private static void CheckOriginal(string directory, string assets)
    {
        var performance = StagePerformance.Load(Path.Combine(assets, "libraries", "gh3-band-v1", "direction", "performance.json"))!;
        var original = StageChartReader.LoadEventFile(Path.Combine(assets, "stages", "events", "mycurse.json"));
        var pack = Path.Combine(assets, "song-direction", "gh3-legends-of-rock", "Killswitch Engage - My Curse", "stage-events.json");
        if (File.Exists(pack))
        {
            File.Copy(pack, Path.Combine(directory, "stage-events.json"), true);
            var sidecar = StageChartReader.Load(directory, "", performance);
            Check(sidecar.Cues.SequenceEqual(original.Cues.Where(c => c.Type != "beat")),
                "the pack sidecar of My Curse has every direction cue and strum of the GH3 song at the same time");
        }
        File.WriteAllText(Path.Combine(directory, "stage-events.json"), JsonSerializer.Serialize(new
        {
            version = 2, library = "gh3-band-v1", events = original.Cues.Where(c => c.Type is not ("strum" or "beat" or "camera" or "gh3-camera"))
                .Append(new StageCue(0, "camera", "wide")).Append(new StageCue(12, "camera", "singer"))
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var resolved = StageChartReader.Load(directory, "", performance);
        Check(resolved.Cameras.Length == 2 && resolved.Track("drum").Length == 2916 && resolved.Track("face", "singer").Length == 5 && resolved.Effects.Length == 18,
            "the My Curse drum, face, and effect channels stay complete beside authored cameras");
        File.Copy(Path.Combine(assets, "..", "docs", "examples", "custom-content", "stage-events.json"), Path.Combine(directory, "stage-events.json"), true);
        var example = StageChartReader.Load(directory, "", performance);
        Check(example.Track("perform", "guitarist").Single().Value == "jump", "the documented example resolves against the installed action catalog");
        Console.WriteLine("Version 2 camera-only and documented full examples checked against the My Curse direction.");
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
