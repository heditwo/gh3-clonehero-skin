using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Gh3Skin;

internal static class VenueTrackLogicTests
{
    private const int Resolution = 480;

    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "opencode", "gh3-venue-track-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            CheckRb1(directory);
            CheckRb3(directory);
            CheckAuthoredPatterns(directory);
            CheckPriority(directory);
            CheckGenerated(directory);
            CheckPortableCameras();
        }
        finally
        {
            StageChartReader.UseVenueTrack = true;
            StageChartReader.UseGeneratedDirection = true;
            Directory.Delete(directory, true);
        }
        Console.WriteLine("Rock Band VENUE (RB1/RB2 notes, RB3 text), source priority, and generated direction checks passed.");
    }

    private static void CheckRb1(string directory)
    {
        var song = Song(directory, "rb1");
        WriteMidi(Path.Combine(song, "notes.mid"), Venue(
            Note(0, 60), Note(0, 71), Text(0, "[lighting ()]"), Note(960, 48), Note(960, 98), Note(960, 39),
            Note(1920, 63), Text(1920, "[lighting (manual_cool)]"),
            Note(3840, 64), Note(3840, 72), Text(3840, "[lighting (strobe_fast)]"),
            Text(4800, "[lighting (blackout_slow)]"), Text(4800, "[bonusfx]"), Text(4800, "[FogOn]"),
            Note(5760, 62), Text(5760, "[verse]"), Text(5760, "[do_directed_cut directed_drums_lt]"),
            Note(30000, 61)));
        var timeline = StageChartReader.Load(song, Path.Combine(song, "notes.mid"));
        var cameras = timeline.Cameras.Select(c => (c.Time, c.Value, c.Duration)).ToArray();
        Check(cameras.SequenceEqual(new[] { (0.0, "87", 2.0), (2.0, "84", 2.0), (4.0, "104", 2.0), (6.0, "92", 0.0), (31.25, "97", 0.0) }),
            "RB1 camera notes map to GH3 camera notes; directed cuts win; holds over 8 s use GH3 cuts: " + string.Join(" ", cameras));
        var lighting = timeline.Cues.Where(c => StageSongDirection.Channel(c) == "lighting").Select(c => $"{c.Time:0.###}:{c.Type}:{c.Value}").ToArray();
        Check(lighting.Take(7).SequenceEqual(new[] { "0:lighting:verse", "1:gh3-note:58", "2:color:blue", "2:lighting:verse", "2:gh3-note:58",
                "4:color:off", "4:lighting:strobe" }),
            "RB1 lighting and keyframe notes map to GH3 moods, colors, and snapshot steps; a color in the same mood steps the snapshot: " + string.Join(" ", lighting));
        Check(lighting.Count(c => c.EndsWith("gh3-note:60", StringComparison.Ordinal)) == 15 &&
            lighting.Where(c => c.EndsWith("gh3-note:60", StringComparison.Ordinal)).All(c => double.Parse(c.Split(':')[0]) is > 4 and < 5),
            "strobe_fast toggles the GH3 strobe on each 32nd note until the next lighting cue");
        Check(lighting.Skip(7 + 15).SequenceEqual(new[] { "5:blend:2", "5:lighting:blackout", "6:blend:0.15", "6:lighting:verse" }),
            "blackout_slow uses a 2 s blend, and the next cue restores the default blend: " + string.Join(" ", lighting.Skip(22)));
        Check(timeline.Effects.Select(c => c.Type).OrderBy(t => t).SequenceEqual(new[] { "pyro", "smoke" }), "bonusfx and FogOn map to pyro and smoke");
        Check(timeline.Source.Contains("notes.mid VENUE (camera, lighting, pyro, smoke)", StringComparison.Ordinal), "the timeline names the VENUE source: " + timeline.Source);
        Check(timeline.Diagnostics.Any(d => d.Contains("5 camera cuts, 5 lighting cues, 2 effects mapped", StringComparison.Ordinal) &&
            d.Contains("1 post-processing", StringComparison.Ordinal) && d.Contains("1 spotlight", StringComparison.Ordinal)),
            "the VENUE diagnostic counts mapped and ignored cues: " + string.Join(" | ", timeline.Diagnostics));
    }

    private static void CheckRb3(string directory)
    {
        var song = Song(directory, "rb3");
        WriteMidi(Path.Combine(song, "notes.mid"), Venue(
            Text(0, "[coop_all_far]"), Text(0, "[lighting (loop_warm)]"), Text(0, "[film_16mm.pp]"),
            Text(960, "[first]"),
            Text(1920, "[coop_k_near]"), Text(1920, "[coop_g_near]"), Text(1920, "[lighting (stomp)]"), Text(2400, "[next]"), Text(2880, "[prev]"),
            Text(3840, "[coop_v_near]"), Text(3840, "[directed_stagedive]"), Text(3840, "[lighting (chorus)]"), Text(4320, "[next]"),
            Text(5760, "[coop_gv_near]"), Text(7680, "[coop_b_behind]"), Text(7680, "[coop_gk_near]"), Text(9600, "[coop_bd_near]"),
            Text(9600, "[lighting (dischord)]"), Text(11520, "[mystery_cut]"), Note(11520, 87)));
        var timeline = StageChartReader.Load(song, Path.Combine(song, "notes.mid"));
        var cameras = timeline.Cameras.Select(c => $"{c.Time:0.###}:{c.Value}").ToArray();
        Check(cameras.SequenceEqual(new[] { "0:87", "2:84", "4:117", "6:114", "8:102", "10:85" }),
            "RB3 cuts map by subject and shot; directed cuts and cuts without keys win: " + string.Join(" ", cameras));
        var lighting = timeline.Cues.Where(c => StageSongDirection.Channel(c) == "lighting").Select(c => $"{c.Time:0.###}:{c.Type}:{c.Value}").ToArray();
        Check(lighting.SequenceEqual(new[] { "0:color:orange", "0:lighting:verse", "1:gh3-note:58", "2:color:off", "2:lighting:strobe",
                "2.5:gh3-note:60", "4:lighting:chorus", "4.5:gh3-note:58", "10:color:red", "10:lighting:chorus", "10:gh3-note:58" }),
            "loop_warm steps every two beats, stomp toggles on next only, chorus steps on next: " + string.Join(" ", lighting));
        Check(timeline.Diagnostics.Any(d => d.Contains("1 post-processing", StringComparison.Ordinal) && d.Contains("1 sing-along", StringComparison.Ordinal) &&
            d.Contains("keyframe in automatic lighting", StringComparison.Ordinal) && d.Contains("unknown mystery_cut", StringComparison.Ordinal)),
            "RB3 diagnostics list ignored and unknown events: " + string.Join(" | ", timeline.Diagnostics));
    }

    // Patterns seen in real charts (Magma/Onyx RB3 customs and RBN charts, see docs/CUSTOM-CONTENT.md): stacked
    // alternative cuts in name order, and two lighting cues on one tick.
    private static void CheckAuthoredPatterns(string directory)
    {
        var song = Song(directory, "authored");
        WriteMidi(Path.Combine(song, "notes.mid"), Venue(
            Text(0, "[coop_all_behind]"), Text(0, "[coop_all_far]"), Text(0, "[coop_all_near]"),
            Text(0, "[lighting (intro)]"), Text(0, "[lighting (silhouettes_spot)]"),
            Text(1920, "[coop_d_behind]"), Text(1920, "[coop_d_closeup_hand]"), Text(1920, "[coop_d_closeup_head]"), Text(1920, "[coop_d_near]"),
            Text(1920, "[lighting (loop_cool)]"), Text(1920, "[lighting (blackout_fast)]"),
            Text(3840, "[coop_g_behind]"), Text(3840, "[lighting ()]"), Text(3840, "[verse]"),
            Text(5760, "[coop_g_behind]"), Text(5760, "[coop_g_closeup_hand]"), Text(5760, "[coop_g_closeup_head]"), Text(5760, "[coop_g_near]"),
            Text(5760, "[coop_kv_near]"), Text(5760, "[lighting (intro)]")));
        var timeline = StageChartReader.Load(song, Path.Combine(song, "notes.mid"));
        var cameras = timeline.Cameras.Select(c => $"{c.Time:0.###}:{c.Value}").ToArray();
        Check(cameras.SequenceEqual(new[] { "0:87", "2:89", "4:102", "6:84" }),
            "stacked cuts rotate over the alternatives that show the subject; a behind cut plays only alone: " + string.Join(" ", cameras));
        var lighting = timeline.Cues.Where(c => StageSongDirection.Channel(c) == "lighting" && c.Type != "gh3-note")
            .Select(c => $"{c.Time:0.###}:{c.Type}:{c.Value}").ToArray();
        Check(lighting.SequenceEqual(new[] { "0:lighting:solo", "2:blend:0.2", "2:lighting:blackout", "4:blend:0.15", "4:lighting:verse", "6:lighting:intro" }),
            "one lighting cue per tick: the last one, and intro yields to a real cue: " + string.Join(" ", lighting));
        Check(timeline.Diagnostics.Any(d => d.Contains("4 camera cuts, 4 lighting cues", StringComparison.Ordinal)),
            "the diagnostic counts the cues that play: " + string.Join(" | ", timeline.Diagnostics));

        // A repeated cue starts a blend into the next different cue (RBN2 authoring docs). The flash pattern is from an Onyx chart.
        WriteMidi(Path.Combine(song, "notes.mid"), Venue(
            Text(0, "[lighting (flare_fast)]"), Text(240, "[lighting (flare_fast)]"), Text(1920, "[lighting (silhouettes_spot)]"),
            Text(3840, "[lighting (harmony)]"), Text(5760, "[lighting (harmony)]"), Text(6720, "[lighting (loop_warm)]"),
            Text(9600, "[lighting (loop_warm)]")));
        timeline = StageChartReader.Load(song, Path.Combine(song, "notes.mid"));
        lighting = timeline.Cues.Where(c => StageSongDirection.Channel(c) == "lighting").Select(c => $"{c.Time:0.###}:{c.Type}:{c.Value}").ToArray();
        Check(lighting.SequenceEqual(new[] { "0:lighting:flare", "0.06:blend:1.94", "0.06:lighting:solo", "2:blend:0.15",
                "4:lighting:verse", "5:gh3-note:58", "6:color:orange", "6:blend:1", "6:lighting:verse", "6:gh3-note:58", "7:blend:0.15",
                "8:gh3-note:58", "9:gh3-note:58" }),
            "a flare is a pulse: the next cue starts at the 40% peak and blends to its own time; steps restart after the span: " + string.Join(" ", lighting));
        Check(timeline.Diagnostics.Any(d => d.Contains("4 lighting cues", StringComparison.Ordinal) &&
            d.Contains("2 repeated lighting cues blend into the next cue", StringComparison.Ordinal)),
            "the diagnostic counts the repeat blends: " + string.Join(" | ", timeline.Diagnostics));

        // A flare far from the next cue returns to the cue before it; flares closer than 0.34 s are dropped (WCAG 2.3.1).
        WriteMidi(Path.Combine(song, "notes.mid"), Venue(
            Text(0, "[lighting (verse)]"), Text(960, "[lighting (flare_fast)]"), Text(9600, "[lighting (chorus)]"),
            Text(10560, "[lighting (flare_slow)]"), Text(10680, "[lighting (verse)]"), Text(10800, "[lighting (flare_fast)]"),
            Text(10920, "[lighting (verse)]"), Text(11040, "[lighting (flare_fast)]")));
        timeline = StageChartReader.Load(song, Path.Combine(song, "notes.mid"));
        lighting = timeline.Cues.Where(c => StageSongDirection.Channel(c) == "lighting").Select(c => $"{c.Time:0.###}:{c.Type}:{c.Value}").ToArray();
        Check(lighting.SequenceEqual(new[] { "0:lighting:verse", "1:lighting:flare", "1.06:blend:0.3", "1.06:lighting:verse", "10:blend:0.15",
                "10:lighting:chorus", "11:lighting:flare", "11.06:blend:0.065", "11.06:lighting:verse", "11.125:blend:0.15", "11.375:lighting:verse",
                "11.5:lighting:flare", "11.56:blend:0.3", "11.56:lighting:verse" }),
            "a lone flare returns to the cue before it, and a flare 0.25 s after another one is dropped: " + string.Join(" ", lighting));
        Check(timeline.Diagnostics.Any(d => d.Contains("3 flares play as 40% pulses, 1 dropped to keep flares 0.34 s apart", StringComparison.Ordinal)),
            "the diagnostic counts the pulses and the dropped flares: " + string.Join(" | ", timeline.Diagnostics));

        StageRockBandVenue.ReduceFlashing = true;
        try
        {
            WriteMidi(Path.Combine(song, "notes.mid"), Venue(
                Text(0, "[lighting (flare_fast)]"), Text(240, "[lighting (verse)]"), Text(480, "[lighting (flare_fast)]"),
                Text(960, "[lighting (strobe_fast)]"), Text(1920, "[lighting (verse)]")));
            timeline = StageChartReader.Load(song, Path.Combine(song, "notes.mid"));
            var flares = timeline.Cues.Where(c => c.Type == "lighting" && c.Value == "flare").Select(c => c.Time).ToArray();
            var toggles = timeline.Cues.Where(c => c.Type == "strobe-toggle" || c.Type == "gh3-note" && c.Value == "60").Select(c => c.Time).ToArray();
            Check(flares.SequenceEqual(new[] { 0.0 }) && toggles.Length == 4 && toggles.Zip(toggles.Skip(1), (a, b) => b - a).All(gap => gap >= .2 - 1e-9),
                $"reduce flashing keeps one flare per second and strobe toggles at least 0.2 s apart: {string.Join(" ", flares)} / {string.Join(" ", toggles)}");
            Check(timeline.Cues.Any(c => c.Type == "blend" && c.Time == .03),
                "reduce flashing turns back at a 20% peak");
        }
        finally
        {
            StageRockBandVenue.ReduceFlashing = false;
        }
    }

    private static void CheckPriority(string directory)
    {
        var song = Song(directory, "priority");
        var midi = Path.Combine(song, "notes.mid");
        WriteMidi(midi, Venue(Text(0, "[coop_all_far]"), Text(0, "[lighting (verse)]"), Text(1920, "[coop_d_near]"), Text(1920, "[bonusfx]")),
            Track("CAMERAS", Note(0, 89)));
        var timeline = StageChartReader.Load(song, midi);
        Check(timeline.Cameras.Single().Value == "89" && timeline.Track("lighting").Single().Value == "verse" && timeline.Effects.Length == 1,
            "a GH CAMERAS track keeps the camera channel; VENUE fills lighting and pyro");

        File.WriteAllText(Path.Combine(song, "stage-events.json"), """
            {"version":2,"library":"gh3-band-v1","clearChannels":["pyro"],"events":[{"time":0,"type":"lighting","value":"solo"}]}
            """);
        var file = StageChartReader.Load(song, midi);
        Check(file.Track("lighting").Single().Value == "solo" && file.Cameras.Single().Value == "89" && file.Effects.Length == 0 &&
            file.ChannelSources["lighting"] == "stage-events.json" && file.ChannelSources["camera"] == "notes.mid",
            "version 2 files and clearChannels come before GH tracks and VENUE");
        WriteMidi(midi, Venue(Text(0, "[coop_all_far]"), Text(1920, "[coop_d_near]")));
        file = StageChartReader.Load(song, midi);
        Check(file.Cameras.Select(c => c.Value).SequenceEqual(new[] { "87", "89" }) && file.ChannelSources["camera"] == "notes.mid VENUE",
            "version 2 files inherit VENUE cameras when no GH track supplies them");
        StageChartReader.UseVenueTrack = false;
        file = StageChartReader.Load(song, midi);
        Check(file.Cameras.Length == 0 && !file.ChannelSources.ContainsKey("camera"), "the player option ignores VENUE tracks");
        File.Delete(Path.Combine(song, "stage-events.json"));
        Check(StageChartReader.Load(song, midi).Cameras.Length == 0, "the player option also applies without a song file");
        StageChartReader.UseVenueTrack = true;
    }

    private static void CheckGenerated(string directory)
    {
        var song = Song(directory, "generated");
        var chart = Path.Combine(song, "notes.chart");
        File.WriteAllText(chart, """
            [Song]
            {
              Resolution = 192
            }
            [SyncTrack]
            {
              0 = B 120000
            }
            [Events]
            {
              0 = E "section Intro"
              768 = E "section Pre-Chorus"
              1536 = E "section Chorus 1"
              2304 = E "section Guitar Solo"
              2688 = E "lighting ()"
            }
            [ExpertSingle]
            {
              0 = N 0 0
              3840 = N 1 0
            }
            """);
        var timeline = StageChartReader.Load(song, chart);
        var lighting = timeline.Track("lighting").Select(c => $"{c.Time:0.###}:{c.Value}").ToArray();
        Check(lighting.SequenceEqual(new[] { "7:verse" }), "a chart lighting event keeps the lighting channel; lighting () is verse: " + string.Join(" ", lighting));
        Check(timeline.Cameras.Select(c => $"{c.Time:0.###}:{c.Value}").SequenceEqual(new[] { "0:79", "2:79", "4:79", "6:81" }),
            "generated camera cuts start at each section, with the GH3 solo group for solos");
        File.WriteAllText(chart, File.ReadAllText(chart).Replace("  2688 = E \"lighting ()\"\n", ""));
        timeline = StageChartReader.Load(song, chart);
        lighting = timeline.Cues.Where(c => StageSongDirection.Channel(c) == "lighting").Select(c => $"{c.Time:0.###}:{c.Type}:{c.Value}").ToArray();
        Check(lighting.SequenceEqual(new[] { "0:lighting:intro", "2:lighting:verse", "4:lighting:chorus", "6:lighting:solo", "8:gh3-note:58" }),
            "generated lighting follows the section names and steps on each bar: " + string.Join(" ", lighting));
        Check(timeline.Source == "notes.chart + generated (camera, lighting)", "the generated source is named: " + timeline.Source);

        File.WriteAllText(chart, "[Song]\n{\n  Resolution = 192\n}\n[SyncTrack]\n{\n  0 = B 120000\n}\n[ExpertSingle]\n{\n  0 = N 0 0\n  1600 = N 0 0\n}\n");
        timeline = StageChartReader.Load(song, chart);
        Check(timeline.Cameras.Length == 0 && timeline.Cues.Where(c => StageSongDirection.Channel(c) == "lighting").Select(c => $"{c.Time:0.###}:{c.Value}")
            .SequenceEqual(new[] { "0:verse", "2:58", "4:58" }), "a chart without sections gets the verse mood and bar steps; GH3 cuts the camera");
        StageChartReader.UseGeneratedDirection = false;
        Check(StageChartReader.Load(song, chart).Cues.All(c => StageSongDirection.Channel(c) is "timing" or "strum/guitarist"),
            "the generated direction can be turned off");
        StageChartReader.UseGeneratedDirection = true;
    }

    private static void CheckPortableCameras()
    {
        OriginalCameraShot Shot(string name, float x) => new() { Name = name, Type = "stage", Control = "TwoCam", Points = new[] {
            new OriginalCameraPoint { Position = new[] { x, 1, -5 }, Rotation = new float[] { 0, 0, 0, 1 }, FieldOfView = 60 } } };
        var bank = new StageCameraBank { Version = 1, Arrays = new() {
            ["cameras"] = new[] { Shot("default", 0) }, ["cameras_bassist"] = new[] { Shot("bass", 1) }, ["cameras_wide"] = new[] { Shot("wide", 2) } } };
        var timeline = new StageTimeline(new[] { new StageCue(0, "gh3-camera", "97", 2), new StageCue(2, "gh3-camera", "87", 2), new StageCue(4, "gh3-camera", "105", 2) }, "test");
        var path = new StageCameraPath(new(Vector3.Zero, Vector3.UnitZ, 55, "fixed"), new Dictionary<string, Vector3>(), bank: bank);
        Check(path.Sample(timeline, 1).Shot == "bass" && path.Sample(timeline, 3).Shot == "wide" && path.Sample(timeline, 5).Shot == "default",
            "a bank without a GH3 note table maps GH3 camera notes to its portable groups");
    }

    private static string Song(string directory, string name)
    {
        var song = Path.Combine(directory, name);
        Directory.CreateDirectory(song);
        return song;
    }

    private static (long Tick, byte[] Data) Note(long tick, int note) => (tick, new byte[] { 0x90, (byte)note, 100 });
    private static (long Tick, byte[] Data) Text(long tick, string text) =>
        (tick, new byte[] { 0xFF, 0x01, (byte)text.Length }.Concat(Encoding.ASCII.GetBytes(text)).ToArray());

    private static (string Name, (long Tick, byte[] Data)[] Events) Venue(params (long Tick, byte[] Data)[] events) => Track("VENUE", events);
    private static (string Name, (long Tick, byte[] Data)[] Events) Track(string name, params (long Tick, byte[] Data)[] events) => (name, events);

    // Format 1, 480 ticks per beat, 120 BPM. Each note lasts 60 ticks.
    private static void WriteMidi(string path, params (string Name, (long Tick, byte[] Data)[] Events)[] tracks)
    {
        using var output = File.Create(path);
        output.Write(Encoding.ASCII.GetBytes("MThd"));
        output.Write(new byte[] { 0, 0, 0, 6, 0, 1, 0, (byte)(tracks.Length + 1), Resolution >> 8, Resolution & 255 });
        WriteTrack(output, "TEMPO", new[] { (0L, new byte[] { 0xFF, 0x51, 3, 0x07, 0xA1, 0x20 }) });
        foreach (var (name, events) in tracks)
        {
            var all = events.Concat(events.Where(e => e.Data[0] == 0x90).Select(e => (e.Tick + 60, new byte[] { 0x80, e.Data[1], 0 })));
            WriteTrack(output, name, all.OrderBy(e => e.Item1).ToArray());
        }
    }

    private static void WriteTrack(Stream output, string name, (long Tick, byte[] Data)[] events)
    {
        using var data = new MemoryStream();
        data.Write(new byte[] { 0, 0xFF, 3, (byte)name.Length });
        data.Write(Encoding.ASCII.GetBytes(name));
        long last = 0;
        foreach (var (tick, bytes) in events)
        {
            var delta = tick - last;
            last = tick;
            var stack = new List<byte> { (byte)(delta & 127) };
            for (delta >>= 7; delta > 0; delta >>= 7) stack.Insert(0, (byte)(128 | (delta & 127)));
            data.Write(stack.ToArray());
            data.Write(bytes);
        }
        data.Write(new byte[] { 0, 0xFF, 0x2F, 0 });
        output.Write(Encoding.ASCII.GetBytes("MTrk"));
        var size = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(size, (int)data.Length);
        output.Write(size);
        data.WriteTo(output);
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new Exception(message);
    }
}
