using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Gh3Skin;

internal static class LipSyncLogicTests
{
    private const int Resolution = 480;

    public static void Run()
    {
        CheckMidiVocals();
        CheckChartLyrics();
        CheckCurve();
        CheckApply();
        Console.WriteLine("Lip sync (vocal notes, lyrics, mouth curve, blinks, GH3 facial clip priority) checks passed.");
    }

    // 120 BPM, 480 ticks per beat: 480 ticks = 0.5 s.
    private static void CheckMidiVocals()
    {
        var midi = Midi(
            ("PART VOCALS", new[]
            {
                Text(0, "[idle]"), Note(0, 105, 1920), Lyric(480, "Drunk"), Note(480, 52, 240), Lyric(720, "los-"), Note(720, 50, 120),
                Lyric(840, "ing"), Note(840, 50, 480), Lyric(1320, "+"), Note(1320, 52, 240), Note(1600, 96, 60), Note(1700, 48, 100)
            }),
            ("HARM1", new[] { Lyric(3000, "harmony"), Note(3000, 60, 240) }));
        var sung = StageChartReader.ReadMidi(midi).Track("sing", "singer");
        Check(sung.Length == 5, $"PART VOCALS gives one cue per pitched note (got {sung.Length})");
        Check(sung[0].Value == "Drunk" && Near(sung[0].Time, 0.5) && Near(sung[0].Duration, 0.25), "vocal note time, length, and lyric");
        Check(sung[3].Value == "+" && sung[4].Value == "+" && Near(sung[4].Time, 1700 / 960.0), "a note without a lyric continues the syllable");
        Check(sung.All(c => c.Value != "harmony" && c.Value != "[idle]"), "PART VOCALS wins over HARM1; text events are not lyrics");
        var harmony = StageChartReader.ReadMidi(Midi(("HARM1", new[] { Lyric(0, "la"), Note(0, 60, 480) }))).Track("sing", "singer");
        Check(harmony.Length == 1 && harmony[0].Value == "la" && Near(harmony[0].Duration, 0.5), "HARM1 when PART VOCALS is missing");
        var unpitched = StageChartReader.ReadMidi(Midi(("PART VOCALS", new[] { Note(0, 105, 960), Lyric(0, "Hey"), Lyric(240, "you") })))
            .Track("sing", "singer");
        Check(unpitched.Length == 2 && Near(unpitched[0].Duration, 0.25) && Near(unpitched[1].Duration, 0.75), "lyrics without notes last to the phrase end");
        Check(StageChartReader.ReadMidi(Midi(("PART GUITAR", new[] { Note(0, 96, 60) }))).Track("sing", "singer").Length == 0, "no vocals, no cues");
    }

    private static void CheckChartLyrics()
    {
        var chart = StageChartReader.ReadChart("[Song]\n{\nResolution = 192\n}\n[SyncTrack]\n{\n0 = B 120000\n}\n[Events]\n{\n" +
            "0 = E \"phrase_start\"\n192 = E \"lyric Hel-\"\n288 = E \"lyric lo\"\n480 = E \"phrase_end\"\n960 = E \"phrase_start\"\n" +
            "960 = E \"lyric world\"\n2880 = E \"phrase_end\"\n3000 = E \"section Verse\"\n}\n");
        var sung = chart.Track("sing", "singer");
        Check(sung.Length == 3 && sung[0].Value == "Hel-" && Near(sung[0].Time, 0.5) && Near(sung[0].Duration, 0.25), ".chart lyric lasts to the next lyric");
        Check(Near(sung[1].Duration, 0.5), ".chart lyric lasts to the phrase end");
        Check(Near(sung[2].Duration, 1), ".chart lyric lasts at most two beats");
    }

    private static void CheckCurve()
    {
        var cues = new[]
        {
            new StageCue(1, "sing", "Drunk", 0.4, "singer"), new StageCue(1.4, "sing", "and", 0.2, "singer"),
            new StageCue(1.6, "sing", "go", 1.5, "singer"), new StageCue(3.1, "sing", "+", 0.5, "singer"), new StageCue(5, "sing", "see#", 0.3, "singer")
        };
        var syllables = StageLipSync.Build(cues);
        Check(syllables.Length == 4, "a slide (+) extends the previous syllable");
        Check(Near(syllables[2].Off, 3.6), "the slid syllable holds to the end of the slide");
        Check(syllables.All(s => s.Jaw is > 0 and <= 30), "jaw stays inside the GH3 facial clip range (p95 29.1 degrees)");
        Check(syllables[3].Jaw < syllables[0].Jaw, "spoken notes open less");
        Check(syllables[2].Round > 0.5f && syllables[3].Round < -0.3f, "o rounds the mouth; e widens it");
        Check(StageLipSync.Vowel("bad").Jaw > StageLipSync.Vowel("me").Jaw && StageLipSync.Vowel("you").Round > StageLipSync.Vowel("bad").Round &&
            StageLipSync.Vowel("me").Round < 0 && StageLipSync.Vowel("hmm").Jaw < StageLipSync.Vowel("me").Jaw, "vowel classes");
        var sync = Sync(cues);
        Check(sync.Mouth(0.9).Jaw == 0, "closed before the first syllable");
        Check(sync.Mouth(0.98).Jaw > 0, "opens a little before the note (anticipation)");
        var peak = sync.Mouth(1.2).Jaw;
        Check(peak > 0.9 * syllables[0].Jaw, "open on the note");
        Check(sync.Mouth(1.4 - 0.05).Jaw < 0.85f * peak, "consonant between consecutive syllables");
        Check(sync.Mouth(3.4).Jaw < 0.8f * sync.Mouth(1.8).Jaw && sync.Mouth(3.4).Jaw > 0.65f * syllables[2].Jaw, "a sustained vowel relaxes and stays open");
        Check(sync.Mouth(3.6 + 0.13).Jaw == 0 && sync.Mouth(4.5).Jaw == 0, "closes between phrases");
        Check(Sync(cues).Mouth(2.345) == sync.Mouth(2.345), "deterministic");
        var blinks = Enumerable.Range(0, 1000).Select(i => sync.Blink(i * 0.01)).ToArray();
        Check(blinks.Max() == 1 && blinks.Count(b => b > 0) is > 10 and < 100, "blinks while the singer sings");
        Check(sync.Blink(-5) == 0 && sync.Blink(100) == 0, "no blinks outside the vocal part");
    }

    private static void CheckApply()
    {
        var names = new[] { "Bone_Root", "Bone_Jaw", "Bone_Lip_Lower_Mid", "Bone_Eyelid_Upper_L", "Bone_Brow_L" }.Select(Gh3Checksum.Of).ToArray();
        var cues = new[] { new StageCue(1, "sing", "Drunk", 0.5, "singer"), new StageCue(0, "beat") };
        var timeline = new StageTimeline(cues, "test");
        var sync = StageLipSync.Create(timeline, "singer", names)!;
        Check(sync != null, "lip sync on a chart with sung notes");
        Check(StageLipSync.Create(timeline, "guitarist", names) == null, "lip sync only for the singer");
        Check(StageLipSync.Create(new StageTimeline(cues.Append(new StageCue(30, "face", "e0ab6bcf", Role: "singer")), "native"), "singer", names) == null,
            "GH3 facial clips keep priority over lip sync");
        Check(StageLipSync.Create(new StageTimeline(new[] { new StageCue(0, "beat") }, "test"), "singer", names) == null, "no sung notes, no lip sync");
        StageLipSync.Enabled = false;
        Check(StageLipSync.Create(timeline, "singer", names) == null, "GH3_SKIN_LIPSYNC=off");
        StageLipSync.Enabled = true;
        var bind = Enumerable.Range(0, names.Length).Select(i => new BandPose(new(i, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f))).ToArray();
        var pose = (BandPose[])bind.Clone();
        sync!.Apply(pose, 0.5);
        Check(pose.SequenceEqual(bind), "no change while closed");
        sync.Apply(pose, 1.25);
        var jaw = sync.Mouth(1.25).Jaw;
        var delta = Quaternion.Inverse(bind[1].Rotation) * pose[1].Rotation;
        var angle = 2 * MathF.Acos(Math.Clamp(delta.W, -1, 1)) * 180 / MathF.PI;
        Check(Math.Abs(angle - jaw) < 0.5f && delta.Z > 0, $"jaw turns about its own Z axis by the curve value ({angle:0.0} for {jaw:0.0})");
        Check(pose[2] != bind[2] && pose[0] == bind[0] && pose[4] == bind[4], "the lips move; root and brows do not");
    }

    private static StageLipSync Sync(StageCue[] cues) =>
        StageLipSync.Create(new StageTimeline(cues, "test"), "singer", new[] { Gh3Checksum.Of("Bone_Jaw") })!;

    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-3;

    private static (long Tick, byte[] Data)[] Note(long tick, int note, long length) =>
        new[] { (tick, new byte[] { 0x90, (byte)note, 96 }), (tick + length, new byte[] { 0x80, (byte)note, 0 }) };
    private static (long Tick, byte[] Data)[] Lyric(long tick, string text) => new[] { Meta(tick, 0x05, text) };
    private static (long Tick, byte[] Data)[] Text(long tick, string text) => new[] { Meta(tick, 0x01, text) };
    private static (long Tick, byte[] Data) Meta(long tick, byte type, string text) =>
        (tick, new byte[] { 0xFF, type, (byte)Encoding.UTF8.GetByteCount(text) }.Concat(Encoding.UTF8.GetBytes(text)).ToArray());

    private static byte[] Midi(params (string Name, (long Tick, byte[] Data)[][] Events)[] tracks)
    {
        using var output = new MemoryStream();
        output.Write(Encoding.ASCII.GetBytes("MThd"));
        output.Write(new byte[] { 0, 0, 0, 6, 0, 1, 0, (byte)(tracks.Length + 1), Resolution >> 8, Resolution & 255 });
        Track(output, "TEMPO", new[] { (0L, new byte[] { 0xFF, 0x51, 3, 0x07, 0xA1, 0x20 }) });
        foreach (var (name, events) in tracks)
            Track(output, name, events.SelectMany(e => e).OrderBy(e => e.Tick).ThenBy(e => e.Data[0] == 0x80 ? 0 : 1).ToArray());
        return output.ToArray();
    }

    private static void Track(Stream output, string name, (long Tick, byte[] Data)[] events)
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
