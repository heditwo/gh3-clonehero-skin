using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Gh3Skin;

internal static class Gh3EventLogicTests
{
    public static void Run()
    {
        var fields = new List<Gh3QbField>
        {
            Field("time", new(1, -1, 1000u)), Field("scr", Key("Band_PlayAnim")),
            Field("params", Struct(
                Field("name", Key("guitarist")), Field("anim", Key("jump")),
                new(0, Key("no_wait")), new(0, Key("cycle")), new(0, Key("cycle")),
                Field("unknown", new(3, -1, "Keep Case, spaces, [brackets], \\\"quotes\\\" and \\ paths")),
                Field("unknown", new(2, -1, -0.125f)),
                Field("nested", new(12, -1, new Gh3QbValue[] {
                    new(1, -1, uint.MaxValue), new(4, -1, "文字"), new(6, -1, new float[] { 1.5f, -4, 0 }),
                    new(26, -1, 0xdeadbeefu), new(28, -1, 0xaabbccddu), new(7, -1, new ReadOnlyMemory<byte>(new byte[] { 0, 1, 255 }))
                }))))
        };
        var call = new Gh3ScriptEvent("performance", new(10, -1, fields.AsReadOnly()));
        var encoded = call.Encode();
        Check(Gh3ScriptEvent.Decode(encoded).Encode() == encoded, "Typed values, duplicate fields, repeated flags, references, and case survive.");
        var projection = Gh3CommandDispatch.Resolve(call);
        Check(projection is { Status: "partial", Value: "jump", Loop: true, Immediate: true } && projection.Gaps.Any(gap => gap.Contains("Repeated parameter")),
            "Partial playback reports unhandled and repeated parameters.");
        var unknown = new Gh3ScriptEvent("scripts", Struct(Field("time", new(1, -1, 1000u)), Field("scr", Key("Custom_Unimplemented_Command"))));
        var cues = new[] { new StageCue(1, "gh3-script", encoded), new StageCue(1, "gh3-script", unknown.Encode()), new StageCue(1, "gh3-script", encoded) };
        var timeline = new StageTimeline(cues, "test");
        Check(timeline.Cues.SequenceEqual(cues) && timeline.Track("perform", "guitarist").Length == 2, "Duplicate commands remain in source and playback order.");
        Check(timeline.Diagnostics.Any(message => message.Contains("unimplemented")), "Unknown commands have a playback diagnostic.");
        var again = new StageTimeline(timeline.Cues, "merged");
        Check(again.PlaybackCues.SequenceEqual(timeline.PlaybackCues), "Channel merging does not run a script twice.");

        var text = "[Song]\nResolution = 1000\n[SyncTrack]\n0 = B 60000\n[PERFORMANCE]\n1000 = E gh3 " + encoded;
        var chart = StageChartReader.ReadChart(text);
        var midi = StageChartReader.ReadMidi(Midi(new[] { (1000, "gh3 " + encoded), (1000, "gh3 " + unknown.Encode()), (1000, "gh3 " + encoded) }));
        Check(chart.Track("perform", "guitarist").Single() == timeline.PlaybackCues[0], "Chart text uses the native command dispatcher.");
        Check(midi.Cues.Where(cue => cue.Type == "gh3-script").Select(cue => cue.Value).SequenceEqual(cues.Select(cue => cue.Value)),
            "MIDI text retains exact typed commands, duplicates, and same-time order.");
        Check(midi.PlaybackCues.Where(cue => cue.Type == "perform").SequenceEqual(timeline.PlaybackCues), "MIDI and native calls have the same playback result.");
        var shifted = StageChartReader.ReadMidi(Midi(new[] { (2500, "gh3 " + encoded) }));
        Check(shifted.Track("perform", "guitarist").Single().Time == 2.5 && Gh3ScriptEvent.Decode(shifted.Cues.Single(cue => cue.Type == "gh3-script").Value).Time == 1000,
            "Editing an event's MIDI tick changes playback time without rewriting its source row.");
        Reject(() => Gh3ScriptEvent.Decode("{}"));
        Reject(() => Gh3QbText.Decode("[6,[1,2]]"));
        Reject(() => Gh3QbText.Decode("[13,null]"));
        Reject(() => Gh3QbText.Decode("[255,0]"));
        Reject(() => Gh3QbText.Decode("[1,-1]"));
        CheckStringArray();
        CheckLights();
        CheckFile(cues);
        if (Environment.GetEnvironmentVariable("GH3_NATIVE_DATA") is { } root) CheckOriginal(root);
        Console.WriteLine("Typed GH3 commands, source preservation, common dispatch, MIDI ordering, and unsupported-cue checks passed.");
    }

    private static void CheckLights()
    {
        Gh3CommandResult Call(string name, params Gh3QbField[] parameters) => Gh3CommandDispatch.Resolve(new("lightshow",
            Struct(Field("time", new(1, -1, 0u)), Field("scr", Key(name)), Field("params", Struct(parameters)))));
        Check(Call("LightShow_SetTime", new Gh3QbField(0, Key("default"))).Value == "0.15", "The original lightshow default is 0.15 seconds.");
        Check(StageTimeline.FromGh3Note(0, 39)!.Value == "0.15", "Note 39 and the native default command agree.");
        Check(Call("LightShow_SetParams", Field("mood", Key("chorus"))) is { Type: "lighting", Value: "chorus" }, "Native mood commands reach lighting playback.");
        Check(Call("LightShow_OverrideColor", Field("color", Key("purple"))) is { Type: "color", Value: "purple" }, "Native color commands reach lighting playback.");
        Check(Call("LightShow_OverrideColor", new Gh3QbField(0, Key("off"))) is { Type: "color", Value: "off" }, "Native color-off flags clear the override.");
        Check(Call("LightShow_PyroEvent") is { Type: "pyro", Status: "partial" }, "Pyro reaches emitters and reports the missing venue script.");
        Check(Call("LightShow_SetTime", Field("time", new(3, -1, "bad"))).Status == "invalid", "Wrong numeric parameter types have an explicit diagnostic.");
    }

    private static void CheckFile(StageCue[] cues)
    {
        var directory = Path.Combine(Path.GetTempPath(), "opencode", "gh3-events-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var all = cues.Concat(new[] { new StageCue(2, "drum", "96"), new StageCue(2, "crowd", "1"), new StageCue(2, "fret", "96") }).ToArray();
            File.WriteAllText(Path.Combine(directory, "stage-events.json"), JsonSerializer.Serialize(new StageEventFile
            {
                Version = 2, Library = "gh3-band-v1", OriginalSongLookup = "off", Events = all
            }));
            var loaded = StageChartReader.Load(directory, "");
            Check(loaded.Cues.SequenceEqual(all), "Version 2 channel resolution retains unknown commands and numeric cues.");
            Check(loaded.Diagnostics.Any(message => message.Contains("drum note 96")), "Unmapped numeric cues are reported.");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void CheckOriginal(string root)
    {
        var songs = 0;
        var calls = 0;
        var exports = 0;
        var exportedLibrary = Environment.GetEnvironmentVariable("GH3_EXPORTED_EVENTS");
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "SONGS"), "*_song.pak.xen"))
        {
            var song = Gh3Song.Load(path);
            var source = song.ScriptEvents().OrderBy(call => call.Time).Select(call => call.Encode()).ToArray();
            var native = new StageTimeline(Gh3SongEvents.Read(song), song.Id);
            Check(native.Cues.Where(cue => cue.Type == "gh3-script").Select(cue => cue.Value).SequenceEqual(source), "All original script rows reach the runtime: " + song.Id);
            var midi = StageChartReader.ReadMidi(Midi(source.Select(text => ((int)Gh3ScriptEvent.Decode(text).Time, "gh3 " + text)).ToArray()));
            Check(midi.Cues.Where(cue => cue.Type == "gh3-script").Select(cue => cue.Value).SequenceEqual(source), "Original script rows survive standard MIDI text: " + song.Id);
            var converted = new StageTimeline(JsonSerializer.Deserialize<StageCue[]>(JsonSerializer.Serialize(native.Cues))!, "converted");
            Check(converted.Cues.SequenceEqual(native.Cues) && converted.PlaybackCues.SequenceEqual(native.PlaybackCues), "Original and converted data share playback: " + song.Id);
            if (exportedLibrary != null)
            {
                var exported = JsonSerializer.Deserialize<StageEventFile>(File.ReadAllText(Path.Combine(exportedLibrary, song.Id + ".json")),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                var fromExporter = new StageTimeline(exported.Events, "exporter");
                Check(fromExporter.Cues.SequenceEqual(native.Cues) && fromExporter.PlaybackCues.SequenceEqual(native.PlaybackCues),
                    "The Python library exporter retains all native direction data and shared dispatch: " + song.Id);
                exports++;
            }
            if (song.Id == "mycurse")
            {
                var index = Array.FindIndex(native.Cues, cue => cue.Type == "gh3-camera" && cue.Time == 12.453);
                Check(index >= 0 && native.Cues[index].Value == "84", "Locate the original camera edit target.");
                var edit = native.Cues.ToArray();
                edit[index] = edit[index] with { Value = "89" };
                var saved = JsonSerializer.Deserialize<StageCue[]>(JsonSerializer.Serialize(edit))!;
                Check(saved[index].Duration == 8.268 && saved.Where((_, i) => i != index).SequenceEqual(native.Cues.Where((_, i) => i != index)),
                    "Change one original camera cue without changing any other direction note or script call.");
                saved[index] = saved[index] with { Value = "84" };
                Check(saved.SequenceEqual(native.Cues), "Undo restores the complete original converted direction.");
            }
            songs++;
            calls += source.Length;
        }
        Console.WriteLine($"Retained all {calls} script calls from {songs} original songs through MIDI text and JSON; matched native playback projections.");
        if (exportedLibrary != null) Console.WriteLine($"Compared {exports} exported song files with native playback.");
    }

    private static void CheckStringArray()
    {
        var qb = new byte[72];
        void Word(int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(qb.AsSpan(offset, 4), value);
        Word(4, 72); Word(8, 0x1c080204); Word(28, 0x00200c00); Word(32, Gh3Checksum.Value("strings")); Word(40, 48);
        Word(48, 0x00010400); Word(52, 2); Word(56, 60); Word(60, 68); Word(64, 70);
        Check(new Gh3Qb(qb).Section("strings")!.Items.Select(value => (string)value.Data).SequenceEqual(new[] { "", "" }),
            "Packed UTF-16 string-array elements can start at two-byte boundaries.");
        Word(64, 71);
        Reject(() => new Gh3Qb(qb));
    }

    internal static byte[] Midi((int Tick, string Text)[] events)
    {
        using var track = new MemoryStream();
        void Variable(int value)
        {
            var bytes = new List<byte> { (byte)(value & 127) };
            while ((value >>= 7) > 0) bytes.Insert(0, (byte)((value & 127) | 128));
            track.Write(bytes.ToArray());
        }
        void Meta(int delta, byte type, byte[] data)
        {
            Variable(delta); track.WriteByte(255); track.WriteByte(type); Variable(data.Length); track.Write(data);
        }
        Meta(0, 3, Encoding.ASCII.GetBytes("PERFORMANCE"));
        Meta(0, 0x51, new byte[] { 15, 66, 64 });
        var previous = 0;
        foreach (var (tick, text) in events)
        {
            Meta(tick - previous, 1, Encoding.UTF8.GetBytes(text));
            previous = tick;
        }
        Meta(0, 0x2f, Array.Empty<byte>());
        using var midi = new MemoryStream();
        midi.Write(new byte[] { 77, 84, 104, 100, 0, 0, 0, 6, 0, 1, 0, 1, 3, 232, 77, 84, 114, 107 });
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, (int)track.Length);
        midi.Write(length); track.WriteTo(midi);
        return midi.ToArray();
    }

    private static Gh3QbValue Key(string name) => new(13, -1, Gh3Checksum.Value(name));
    private static Gh3QbField Field(string name, Gh3QbValue value) => new(Gh3Checksum.Value(name), value);
    private static Gh3QbValue Struct(params Gh3QbField[] fields) => new(10, -1, fields);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("Malformed GH3 event data was accepted.");
    }
}
