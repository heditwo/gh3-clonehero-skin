using System.Buffers.Binary;
using System.Text;
using Gh3Skin;

internal static class StageLogicTests
{
    public static void Run()
    {
        var chart = StageChartReader.ReadChart("""
            [Song]
            {
              Resolution = 192
              Offset = 0.125
            }
            [SyncTrack]
            {
              0 = B 120000
              192 = B 60000
            }
            [Events]
            {
              384 = E "stage pyro"
              192 = E "stage blackout"
              0 = E "section This is not an effect"
              576 = E "[bonusfx]"
            }
            [ExpertSingle]
            {
              0 = E "stage pyro"
            }
            """);
        Check(chart.Effects.Length == 2, "only global chart events create effects");
        Check(chart.Effects[0].Time == 1.625 && chart.Effects[1].Time == 2.625, "tempo changes and chart offset map to song seconds");
        Check(chart.Lighting(0.624) != new StageColor(0, 0, 0), "blackout does not start early");
        Check(chart.Lighting(0.625) == new StageColor(0, 0, 0), "blackout starts at the exact tick");
        Check(chart.FirstEffectAfter(2) == 1, "practice jump skips old bursts");
        Check(chart.FirstEffectAfter(1.625) == 0, "effect at seek boundary is included");
        var before = chart.Lighting(0.5);
        _ = chart.Lighting(100);
        Check(chart.Lighting(0.5) == before, "backward seek reconstructs earlier lights");
        Check(chart.Lighting(0.5) == chart.Lighting(0.5), "paused clock keeps lights fixed");

        var fade = new StageTimeline(new[] {
            new StageCue(1, "blend", "2"), new StageCue(1, "lighting", "red"),
            new StageCue(2, "lighting", "green"), new StageCue(6, "gh3-note", "72"),
            new StageCue(7, "gh3-note", "74"), new StageCue(8, "gh3-note", "56")
        }, "test");
        var middle = StageColor.Mix(StageColor.Named("intro"), StageColor.Named("red"), 0.5);
        Check(fade.Lighting(2) == middle, "interrupted blend starts from the current color");
        Check(fade.Lighting(3) == StageColor.Mix(middle, StageColor.Named("green"), 0.5), "interrupted blend remains continuous");
        Check(fade.Lighting(6) == new StageColor(0, 0, 0), "GH3 note 72 is blackout");
        Check(fade.Effects.Length == 1 && fade.Effects[0].Time == 8, "GH3 note 56 creates pyro");

        var midi = BuildMidi();
        var midiTimeline = StageChartReader.ReadMidi(midi);
        Check(midiTimeline.Effects.Length == 2, "only GH3 venue note-ons trigger pyro, not guitar notes or velocity-zero notes");
        Check(midiTimeline.Effects[0].Time == 0.5 && midiTimeline.Effects[1].Time == 1.5, "MIDI tempo track and running status are applied");
        Reject(() => StageChartReader.ReadMidi(midi[..^1]), "truncated MIDI is rejected");
        var smpte = (byte[])midi.Clone();
        smpte[12] = 0xE7;
        Reject(() => StageChartReader.ReadMidi(smpte), "SMPTE does not silently use PPQN timing");
        Reject(() => StageChartReader.ReadChart("[Song]\nResolution = 0"), "zero chart resolution is rejected");
        Reject(() => new StageTimeline(new[] { new StageCue(double.NaN, "pyro") }, "test"), "non-finite JSON event time is rejected");
        CheckDirectionTracks();

        var directory = Path.Combine(Path.GetTempPath(), "opencode", "gh3-stage-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "stage-events.json"), """
                {"version":1,"offsetSeconds":-1,"events":[
                  {"time":0,"type":"lighting","value":"red"},
                  {"time":2,"type":"pyro"}]}
                """);
            var sidecar = StageChartReader.Load(directory, "missing.mid");
            Check(sidecar.Effects[0].Time == 1, "sidecar offset aligns the original song to an audio edit");
            Check(sidecar.Lighting(0) == StageColor.Named("red"), "negative offset preserves earlier lighting state");
            File.WriteAllText(Path.Combine(directory, "stage-events.json"), "{\"version\":3}");
            Reject(() => StageChartReader.Load(directory, "missing.mid"), "unsupported sidecar version is rejected");
            File.Delete(Path.Combine(directory, "stage-events.json"));
            var library = Path.Combine(directory, "library");
            Directory.CreateDirectory(library);
            File.WriteAllText(Path.Combine(library, "index.json"), """
                {"version":1,"songs":[{"title":"My Curse","artist":"Killswitch Engage","file":"mycurse.json"}]}
                """);
            File.WriteAllText(Path.Combine(library, "mycurse.json"), """
                {"version":1,"events":[{"time":45.274,"type":"gh3-note","value":"56"}]}
                """);
            var matched = StageChartReader.Load(directory, "missing.mid", library, "MY CURSE!", "Killswitch Engage");
            Check(matched.Effects.Single().Time == 45.274, "original GH3 library matches title and artist");
            File.WriteAllText(Path.Combine(library, "mycurse.json"), """
                {"version":1,"events":[{"time":0,"type":"gh3-camera","value":"88"},{"time":45.274,"type":"pyro"}]}
                """);
            var overrideChart = Path.Combine(directory, "override.chart");
            File.WriteAllText(overrideChart, "[Events]\n0 = E \"stage camera drummer\"");
            var overlay = StageChartReader.Load(directory, overrideChart, library, "My Curse", "Killswitch Engage");
            Check(overlay.Cameras.Single().Value == "drummer" && overlay.Effects.Single().Time == 45.274,
                "custom camera channel replaces GH3 camera notes and keeps the original effects");
            Check(StageChartReader.Load(directory, "missing.mid", library, "My Curse", "Other Artist").CueCount == 0,
                "same song title with another artist does not use GH3 cues");
            File.WriteAllText(Path.Combine(directory, "stage-events.json"), "{\"version\":1,\"events\":[]}");
            Check(StageChartReader.Load(directory, "missing.mid", library, "My Curse", "Killswitch Engage").CueCount == 0,
                "an explicit empty sidecar takes priority over the library");
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("Stage timing, MIDI, cue mapping, pause, and seek tests passed.");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new Exception(message);
    }

    private static void CheckDirectionTracks()
    {
        const string source = """
            [Song]
            {
              Resolution = 480
              Offset = 0.125
            }
            [SyncTrack]
            {
              0 = B 120000
              480 = B 60000
            }
            [CAMERAS]
            {
              240 = N 89 480
              480 = N 85 240
            }
            [LIGHTSHOW]
            {
              240 = N 56 480
              240 = N 120 0
            }
            [ANIMS]
            {
              0 = N 125 240
            }
            [DRUMS]
            {
              0 = N 70 480
              240 = N 70 480
            }
            [CROWD]
            {
              0 = N 72 480
            }
            [ExpertDrums]
            {
              0 = N 1 0
            }
            """;
        var chart = StageChartReader.ReadChart(source);
        using var output = new MemoryStream();
        output.Write(Encoding.ASCII.GetBytes("MThd"));
        output.Write(new byte[] { 0, 0, 0, 6, 0, 1, 0, 7, 1, 224 });
        void Track(string name, byte[] events)
        {
            using var data = new MemoryStream();
            data.Write(new byte[] { 0, 255, 3, (byte)name.Length });
            data.Write(Encoding.ASCII.GetBytes(name));
            data.Write(events);
            data.Write(new byte[] { 0, 255, 47, 0 });
            WriteTrack(output, data.ToArray());
        }
        Track("TEMPO", new byte[] { 0, 255, 81, 3, 7, 161, 32, 131, 96, 255, 81, 3, 15, 66, 64 });
        Track("CAMERAS", new byte[] { 129, 112, 144, 89, 100, 129, 112, 85, 100, 129, 112, 128, 89, 0, 0, 85, 0 });
        Track("LIGHTSHOW", new byte[] { 129, 112, 144, 56, 92, 0, 120, 63, 0, 128, 120, 0, 131, 96, 56, 0 });
        Track("ANIMS", new byte[] { 0, 144, 125, 80, 129, 112, 128, 125, 0 });
        Track("DRUMS", new byte[] { 0, 144, 70, 75, 129, 112, 145, 70, 42, 129, 112, 128, 70, 0, 129, 112, 129, 70, 0 });
        Track("CROWD", new byte[] { 0, 144, 72, 90, 131, 96, 128, 72, 0 });
        Track("PART DRUMS", new byte[] { 0, 144, 96, 100, 0, 128, 96, 0 });
        var midi = StageChartReader.ReadMidi(output.ToArray());
        Check(chart.Cues.SequenceEqual(midi.Cues.Select(c => c with { Time = c.Time + 0.125, Velocity = null })),
            "Standard direction tracks use the same note IDs and tempo conversion in chart and MIDI files");
        Check(chart.Cameras[0] == new StageCue(0.375, "gh3-camera", "89", 0.75),
            "Camera sustain crosses a tempo change and uses the chart offset");
        Check(midi.Track("drum").Select(c => (c.Time, c.Duration, c.Velocity)).SequenceEqual(new[] { (0.0, 0.5, (int?)75), (0.25, 0.75, (int?)42) }),
            "Overlapping drum notes on separate MIDI channels retain their own lengths and velocities");
        Check(chart.Track("gh3-note").Any(c => c.Value == "120") && midi.Track("gh3-note").Any(c => c.Value == "120" && c.Velocity == 63),
            "An unmapped light note remains available in the runtime timeline");
        Check(midi.Track("gh3-note").Single(c => c.Value == "56") is { Duration: 0.75, Velocity: 92 },
            "Lightshow source cues retain duration and velocity before playback mapping");
        Check(chart.Track("fret").Single().Value == "125" && chart.Track("drum").Length == 2,
            "ANIMS uses the existing GH tool track name and playable drums are separate");
        Check(StageChartReader.ReadChart("[LIGHTSHOW]\n192 = E bonusfx").Effects.Single().Time == 0.5,
            "Track-local events accept Moonscraper's unquoted chart syntax");
        Reject(() => StageChartReader.ReadChart("[CAMERAS]\n0 = N 128 12"), "Out-of-range camera note is rejected");
        Reject(() => StageChartReader.ReadChart("[DRUMS]\n0 = N 70 12 75"), "Extra chart note fields cannot silently discard velocity");
    }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception(message);
    }

    private static byte[] BuildMidi()
    {
        using var output = new MemoryStream();
        output.Write(Encoding.ASCII.GetBytes("MThd"));
        output.Write(new byte[] { 0, 0, 0, 6, 0, 1, 0, 3, 1, 224 });
        WriteTrack(output, new byte[] {
            0, 255, 81, 3, 7, 161, 32,
            131, 96, 255, 81, 3, 15, 66, 64,
            0, 255, 47, 0
        });
        using var venue = new MemoryStream();
        venue.Write(new byte[] { 0, 255, 3, 9 });
        venue.Write(Encoding.ASCII.GetBytes("LIGHTSHOW"));
        venue.Write(new byte[] { 131, 96, 144, 56, 100, 131, 96, 56, 100, 0, 56, 0, 0, 255, 47, 0 });
        WriteTrack(output, venue.ToArray());
        using var guitar = new MemoryStream();
        guitar.Write(new byte[] { 0, 255, 3, 11 });
        guitar.Write(Encoding.ASCII.GetBytes("PART GUITAR"));
        guitar.Write(new byte[] { 0, 144, 56, 100, 0, 255, 47, 0 });
        WriteTrack(output, guitar.ToArray());
        return output.ToArray();
    }

    private static void WriteTrack(Stream output, byte[] data)
    {
        output.Write(Encoding.ASCII.GetBytes("MTrk"));
        var size = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(size, data.Length);
        output.Write(size);
        output.Write(data);
    }
}
