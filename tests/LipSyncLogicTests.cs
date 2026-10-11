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
        CheckPhonemes();
        CheckEnvelope();
        CheckCurve();
        CheckApply();
        Console.WriteLine("Lip sync (vocal notes, lyrics, phonemes, visemes, coarticulation, stem loudness, face life, blinks, GH3 facial clip priority) checks passed.");
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
        Check(sung[0].Value == "Drunk" && Near(sung[0].Time, 0.5) && Near(sung[0].Duration, 0.25) && sung[0].Pitch == 52, "vocal note time, length, pitch, and lyric");
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

    private static void CheckPhonemes()
    {
        string Text(byte[] phonemes) => string.Join(" ", phonemes.Select(p => LyricPhonemes.Phonemes[p]));
        Check(Text(LyricPhonemes.Word("love")) == "L AH V", $"dictionary word (got {Text(LyricPhonemes.Word("love"))})");
        Check(LyricPhonemes.IsKnown("rockin'") && Text(LyricPhonemes.Word("rockin'")) == Text(LyricPhonemes.Word("rocking")), "a dropped g reads as -ing");
        var losing = LyricPhonemes.Divide(new[] { "los-", "ing" });
        Check(Text(losing[0]) == "L UW" && Text(losing[1]) == "Z IH NG", $"one consonant starts the next syllable (got {Text(losing[0])} | {Text(losing[1])})");
        var yeah = LyricPhonemes.Divide(new[] { "yeah-", "eah" });
        Check(yeah[1].Length == 1 && LyricPhonemes.IsVowel(yeah[1][0]) && yeah[1][0] == yeah[0].Last(LyricPhonemes.IsVowel), "a held vowel written as two syllables");
        var unknown = LyricPhonemes.Word("zarbleflop");
        Check(unknown.Length > 4 && unknown.Any(LyricPhonemes.IsVowel), "spelling rules for unknown words");
        Check(LyricPhonemes.Clean("Drunk#") == "drunk" && LyricPhonemes.JoinsNext("los-") && LyricPhonemes.JoinsNext("hyph=") && !LyricPhonemes.JoinsNext("go"), "lyric marks");
    }

    private static void CheckCurve()
    {
        var cues = new[]
        {
            new StageCue(1, "sing", "Drunk", 0.4, "singer"), new StageCue(1.4, "sing", "and", 0.2, "singer"),
            new StageCue(1.6, "sing", "go", 1.5, "singer"), new StageCue(3.1, "sing", "+", 0.5, "singer"), new StageCue(5, "sing", "see#", 0.3, "singer"),
            new StageCue(6, "sing", "go", 0.5, "singer"), new StageCue(6.5, "sing", "on", 0.5, "singer"), new StageCue(7, "sing", "may-", 0.4, "singer"),
            new StageCue(7.4, "sing", "be", 0.6, "singer"), new StageCue(9, "sing", "I", 1, "singer"),
        };
        VocalEnvelope.Current = null;
        var notes = StageLipSync.Notes(cues);
        Check(notes.Length == 9 && Near(notes[2].End, 3.6), "a slide (+) extends the previous note");
        var sync = Sync(cues);
        var weights = new float[16];
        Check(sync.Jaw(0.5) == 0, "closed before the first syllable");
        Check(sync.Jaw(0.99) > 0, "opens a little before the note (anticipation)");
        var peak = sync.Jaw(1.2);
        Check(peak is > 10 and < 40, $"open on the note ({peak:0.0} degrees)");
        Check(sync.Jaw(2.4) > 0.5f * peak && sync.Jaw(3.4) > 0.4f * peak, "a held vowel stays open");
        Check(sync.Jaw(4.3) < 1, "closed between phrases");
        var legato = Enumerable.Range(0, 20).Select(i => sync.Jaw(6.3 + i * 0.02)).Min();
        Check(legato > 0.4f * sync.Jaw(6.25), $"vowel to vowel legato stays open ({legato:0.0} degrees)");
        var m = 7.4 - 0.04 - 0.035;
        Check(sync.Weights(m, weights) > 0.95f && Math.Abs(sync.Jaw(m)) < 5, $"m closes the lips (jaw {sync.Jaw(m):0.0})");
        sync.Weights(9.2, weights);
        var start = weights.ToArray();
        sync.Weights(9.9, weights);
        Check(start[(int)StageLipSync.Viseme.AA] > 0.6f && weights[(int)StageLipSync.Viseme.IY] > 0.5f, "the diphthong of I glides from ah to ee");
        var shapes = new float[Gh3Skin.Packages.CharacterSkeleton.FaceSlots.Length];
        var vowelsOnly = Gh3Skin.Packages.CharacterSkeleton.FaceSlots.Select(slot => slot is "blink" or "a" or "i" or "u" or "e" or "o").ToArray();
        sync.Face(5.1, shapes, vowelsOnly);
        Check(shapes[2] == shapes[1..6].Max() && shapes[2] > 0.2f, "see shows the i shape");
        sync.Face(2.2, shapes, vowelsOnly);
        Check(shapes[5] == shapes[1..6].Max() && shapes[5] > 0.4f, "go shows the o shape");
        sync.Face(m, shapes, vowelsOnly);
        Check(shapes[1..6].Max() < 0.05f, "m closes the blend shapes");
        var visemes = Gh3Skin.Packages.CharacterSkeleton.FaceSlots.Select(slot => slot is not ("browUp" or "browDown" or "squint")).ToArray();
        sync.Face(m, shapes, visemes);
        Check(shapes[Array.IndexOf(Gh3Skin.Packages.CharacterSkeleton.FaceSlots, "pp")] > 0.95f, "m uses the pp viseme of a VRChat face");
        sync.Face(3.3, shapes, vowelsOnly);
        Check(shapes[Array.IndexOf(Gh3Skin.Packages.CharacterSkeleton.FaceSlots, "browUp")] > 0.5f, "the brow shape rises on a long note");
        Check(Sync(cues).Jaw(2.345) == sync.Jaw(2.345), "deterministic");
        var blinks = Enumerable.Range(0, 1000).Select(i => sync.Blink(i * 0.01)).ToArray();
        Check(blinks.Max() == 1 && blinks.Count(b => b > 0) is > 10 and < 100, "blinks while the singer sings");
        Check(sync.Blink(-5) == 0 && sync.Blink(100) == 0, "no blinks outside the vocal part");
        Check(sync.BrowAt(3.3) > 0.5f && Math.Abs(sync.BrowAt(5.15)) < 0.3f, "the brows rise on a long note");

        var levels = Enumerable.Range(0, 1200).Select(i => i is > 600 and < 700 ? 1f : i is > 900 and < 1000 ? 0.3f : 0.01f).ToArray();
        VocalEnvelope.Current = Task.FromResult<VocalEnvelope?>(VocalEnvelope.FromLevels(levels));
        var loud = Sync(cues);
        Check(loud.Loudness(6.5) > 0.9f && loud.Loudness(9.5) is > 0.2f and < 0.4f, "stem loudness normalized to the loud singing");
        VocalEnvelope.Current = Task.FromResult<VocalEnvelope?>(VocalEnvelope.FromLevels(levels.Select((level, i) => i is > 900 and < 1000 ? 1f : level).ToArray()));
        Check(Sync(cues).Jaw(9.4) > 1.3f * loud.Jaw(9.4), "a loud note opens more than a quiet one");
        Check(loud.SquintAt(6.5) > 0 && loud.SquintAt(9.5) == 0, "loud singing narrows the eyes");
        VocalEnvelope.Current = null;
    }

    private static void CheckEnvelope()
    {
        var smooth = VocalEnvelope.Smooth(Enumerable.Range(0, 100).Select(i => i is >= 10 and < 50 ? 1f : 0f).ToArray());
        Check(smooth[12] > 0.7f && smooth[55] is > 0.2f and < 0.8f && smooth[99] < 0.05f, "fast rise, slow fall");
        var envelope = VocalEnvelope.FromLevels(new[] { 0f, 0.5f, 1f, 1f, 0.02f });
        Check(Math.Abs(envelope.At(0.02) - 1) < 0.05f && envelope.At(0.04) == 0 && envelope.At(-1) == 0 && envelope.At(10) == 0, "normalized levels and the noise gate");
        Check(VocalEnvelope.Stems("/nonexistent").Length == 0, "no stems in a missing folder");
    }

    private static void CheckApply()
    {
        var names = new[] { "Bone_Root", "Bone_Jaw", "Bone_Lip_Lower_Mid", "Bone_Eyelid_Upper_L", "Bone_Brow_L", "Bone_Head", "Bone_Eye_L" }.Select(Gh3Checksum.Of).ToArray();
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
        sync!.Apply(pose, -20);
        Check(pose.SequenceEqual(bind), "no change long before the singing");
        pose = (BandPose[])bind.Clone();
        sync.Apply(pose, 1.25);
        var jaw = sync.Jaw(1.25);
        var delta = Quaternion.Inverse(bind[1].Rotation) * pose[1].Rotation;
        var angle = 2 * MathF.Acos(Math.Clamp(delta.W, -1, 1)) * 180 / MathF.PI;
        Check(Math.Abs(angle - jaw) < 1.5f && delta.Z > 0, $"jaw turns about its own Z axis by the viseme value ({angle:0.0} for {jaw:0.0})");
        Check(pose[2] != bind[2] && pose[0] == bind[0], "the lips move; the root does not");
        pose = (BandPose[])bind.Clone();
        sync.Apply(pose, 1 - 0.05 + 0.1);
        Check(pose[5] != bind[5], "the head nods at the phrase start");
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
