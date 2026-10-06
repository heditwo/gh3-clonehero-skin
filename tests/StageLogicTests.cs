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
