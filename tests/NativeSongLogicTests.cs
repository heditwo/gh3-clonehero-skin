using System.Buffers.Binary;
using System.Text;
using Gh3Skin;

internal static class NativeSongLogicTests
{
    public static void Run()
    {
        var bytes = Fixture();
        var song = new Gh3Song(bytes, "native_test");
        Check(song.Notes("cameras")[1] is { Time: 12453, Note: 84, Length: 8268, Velocity: 91 }, "Native timing and velocity survive reading.");
        Check(song.ChangeCameraNote(1, 84, 84).SequenceEqual(bytes), "A no-op export is byte-identical.");
        var edited = song.ChangeCameraNote(1, 84, 89);
        Check(Enumerable.Range(0, bytes.Length).Where(i => bytes[i] != edited[i]).SequenceEqual(new[] { song.Notes("cameras")[1].NoteOffset + 3 }),
            "One camera edit changes exactly one byte and preserves unknown content.");
        Check(new Gh3Song(edited, "native_test").ChangeCameraNote(1, 89, 84).SequenceEqual(bytes), "Undo restores the exact original archive.");
        Reject(() => song.ChangeCameraNote(1, 85, 89));
        Reject(() => new Gh3Song(bytes, "wrong_song"));
        Reject(() => new Gh3Song(bytes[..^40], "native_test"));
        var corrupt = bytes.ToArray();
        Write(corrupt, 256 + 28 + 12, uint.MaxValue);
        Reject(() => new Gh3Song(corrupt, "native_test"));
        var cycle = bytes.ToArray();
        Write(cycle, 256 + 60, 48);
        Reject(() => new Gh3Song(cycle, "native_test"));

        var directory = Path.Combine(Path.GetTempPath(), "opencode", "gh3-native-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "native_test_song.pak.xen");
            File.WriteAllBytes(path, edited);
            File.WriteAllText(Path.Combine(directory, "stage-events.json"), "invalid legacy data");
            var timeline = StageChartReader.Load(directory, "");
            Check(timeline.NativeSong != null && timeline.Cameras[1].Value == "89", "Playback loads the native package directly.");
            Check(timeline.Cameras[1].Time == 12.453 && timeline.Cameras[1].Duration == 8.268, "Playback preserves native camera timing.");
            Check(timeline.NativeSong!.Notes("drums")[0].Velocity == 50, "The native document retains drum velocity.");
            Check(timeline.Track("drum")[0].Velocity == 50, "The native runtime cue retains drum velocity.");
            File.WriteAllBytes(Path.Combine(directory, "other_song.pak.xen"), edited);
            Reject(() => StageChartReader.Load(directory, ""));
        }
        finally { Directory.Delete(directory, true); }

        if (Environment.GetEnvironmentVariable("GH3_NATIVE_DATA") is { } root) CheckOriginal(root);
        Console.WriteLine("Native GH3 PAK preservation, camera edit, bounds, and direct playback checks passed.");
    }

    private static void CheckOriginal(string root)
    {
        var file = Path.Combine(root, "SONGS", "mycurse_song.pak.xen");
        var original = File.ReadAllBytes(file);
        var song = Gh3Song.Load(file);
        Check(song.Pak.Entries.Count == 9 && song.Notes("cameras").Length == 53, "Read the original My Curse archive.");
        Check(song.Notes("drums").Length == 2916 && song.Notes("drums")[0].Velocity == 75, "Read original drum direction.");
        var output = song.ChangeCameraNote(1, 84, 89);
        Check(Enumerable.Range(0, original.Length).Where(i => original[i] != output[i]).SequenceEqual(new[] { song.Notes("cameras")[1].NoteOffset + 3 }),
            "Original My Curse export differs by exactly one camera byte.");
        Check(new Gh3Song(output, "mycurse").ChangeCameraNote(1, 89, 84).SequenceEqual(original), "Original My Curse round trip is lossless.");
        var timeline = StageNativeSong.Load(file);
        Check(timeline.Cameras.Length == 53 && timeline.Track("face", "singer").Length == 5 && timeline.Track("drum").Length == 2916 && timeline.Effects.Length == 18,
            "Original My Curse plays its native direction without an event export.");
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "SONGS"), "*_song.pak.xen"))
        {
            var native = Gh3Song.Load(path);
            foreach (var section in new[] { "scripts", "anim", "triggers", "cameras", "lightshow", "crowd", "drums", "performance" })
                _ = native.Notes(section);
            count++;
        }
        Console.WriteLine($"Read native direction from {count} installed GH3 song archives.");
    }

    private static byte[] Fixture()
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[28]);
        void Word(uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            stream.Write(bytes);
        }
        void Section(string name, uint[][] rows)
        {
            var start = (uint)stream.Position;
            foreach (var word in new[] { 0x00200c00u, Gh3Checksum.Value("native_test_" + name), 0x12345678u, start + 20, 0u }) Word(word);
            Word(0x00010c00); Word((uint)rows.Length);
            if (rows.Length > 1) Word(start + 32);
            var data = start + 28 + (rows.Length > 1 ? 4u : 0u) + (uint)rows.Length * 4;
            foreach (var row in rows) { Word(data); data += 12 + (uint)row.Length * 4; }
            foreach (var row in rows)
            {
                Word(0x00010100); Word((uint)row.Length); Word((uint)stream.Position + 4);
                foreach (var value in row) Word(value);
            }
        }
        Section("cameras_notes", new[] { new uint[] { 0, 85, 12453 }, new uint[] { 12453, 84, 8268, 91 } });
        Section("drums_notes", new[] { new uint[] { 1043, 70, 43, 50 } });
        var offset = (uint)stream.Position;
        foreach (var word in new[] { 0x00200c00u, Gh3Checksum.Value("native_test_fretbars"), 0x12345678u, offset + 20, 0u,
            0x00010100u, 2u, offset + 32, 0u, 500u }) Word(word);
        var qb = stream.ToArray();
        Write(qb, 4, (uint)qb.Length); Write(qb, 8, 0x1c080204);
        var unknown = Encoding.ASCII.GetBytes("unknown entry and padding must survive without a decoder");
        var pak = Enumerable.Repeat((byte)0xa5, 256 + qb.Length + unknown.Length + 16).ToArray();
        System.Array.Clear(pak, 0, 96);
        Write(pak, 0, Gh3Checksum.Value(".qb")); Write(pak, 4, 256); Write(pak, 8, (uint)qb.Length); Write(pak, 16, 0x12345678);
        Write(pak, 32, Gh3Checksum.Value(".unknown")); Write(pak, 36, (uint)(256 + qb.Length - 32)); Write(pak, 40, (uint)unknown.Length);
        Write(pak, 64, Gh3Checksum.Value(".last"));
        qb.CopyTo(pak, 256); unknown.CopyTo(pak, 256 + qb.Length);
        return pak;
    }

    private static void Write(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at, 4), value);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("Invalid native data was accepted.");
    }
}
