using System.Buffers.Binary;
using Gh3Skin;

internal static class ScriptLogicTests
{
    internal static Gh3QbValue Script(params object[] tokens)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (var token in tokens)
        {
            if (token is byte op) writer.Write(op);
            else if (token is string key) { writer.Write((byte)0x16); writer.Write(Gh3Checksum.Value(key)); }
            else if (token is int number) { writer.Write((byte)0x17); writer.Write(number); }
            else if (token is float single) { writer.Write((byte)0x1a); writer.Write(single); }
            else if (token is float[] vector && vector.Length == 3) { writer.Write((byte)0x1e); foreach (var value in vector) writer.Write(value); }
            else throw new Exception("Invalid test script token");
        }
        var data = stream.ToArray();
        var bytes = new byte[12 + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), data.Length);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), data.Length);
        data.CopyTo(bytes, 12);
        return new(7, -1, (ReadOnlyMemory<byte>)bytes);
    }

    public static void Run()
    {
        foreach (var (key, name) in Gh3Keys.Names)
            if (Gh3Checksum.Value(name) != key) throw new Exception($"Gh3Keys: {name} is {Gh3Checksum.Value(name):x8}, not {key:x8}");
        if (Gh3Checksum.Of("Bone_Head") != Gh3Bones.Head) throw new Exception("Gh3Bones.Head");
        var calls = new List<(double Time, uint Target, string Value)>();
        var values = new Dictionary<uint, Gh3QbValue> {
            [Gh3Checksum.Value("main")] = Script("value", (byte)7, 2, (byte)1,
                "mark", "value", (byte)7, (byte)0x2d, "value", (byte)1,
                "Wait", 2, "Seconds", (byte)1,
                "mark", "value", (byte)7, (byte)0x0e, (byte)0x2d, "value", (byte)0x0b, 3, (byte)0x0f, (byte)1, (byte)0x24),
            [Gh3Checksum.Value("loop")] = Script((byte)0x20, (byte)1, "mark", "value", (byte)7, 9, (byte)1,
                "Wait", 1, "gameframe", (byte)1, (byte)0x21, 3, (byte)1, (byte)0x24),
            [Gh3Checksum.Value("blocked")] = Script("Block", "anytypes", (byte)7, (byte)5, "signal", (byte)6, (byte)1,
                "mark", "value", (byte)7, 11, (byte)1, (byte)0x24)
        };
        Gh3ScriptRuntime? runtime = null;
        runtime = new(values, (context, command, parameters) =>
        {
            if (command != Gh3Checksum.Value("mark")) return null;
            calls.Add((runtime!.Time, context.Object, Gh3QbText.Encode(parameters.Field("value")!)));
            return new(true);
        });
        using (runtime)
        {
            runtime.Start(Gh3Checksum.Value("main"), target: 42);
            Check(calls.Count == 1 && calls[0] == (0, 42, "[1,2]"), "Script locals and immediate work run before yielding.");
            runtime.Advance(1.99);
            Check(calls.Count == 1, "A source wait retains its deadline.");
            runtime.Advance(2);
            Check(calls[1] == (2, 42, "[1,5]") && runtime.Pending == 0, "Resumed scripts retain local variables and object targets.");
            runtime.Start(Gh3Checksum.Value("loop"), id: 12, immediate: false);
            runtime.Advance(2 + 1.0 / 60);
            Check(calls.Count == 3, "SpawnScriptLater starts on the next game frame.");
            runtime.Cancel(id: 12);
            runtime.Advance(3);
            Check(calls.Count == 3 && runtime.Pending == 0, "Cancelling an ID removes pending loop work.");
            runtime.Start(Gh3Checksum.Value("loop"));
            runtime.Advance(4);
            Check(calls.Count == 6 && runtime.Pending == 0, "Finite repeat loops run their source repeat count.");
            runtime.Start(Gh3Checksum.Value("blocked"), target: 42);
            runtime.Advance(5);
            Check(calls.Count == 6 && runtime.Pending == 1, "Blocked scripts wait for an event rather than polling time.");
            Gh3QbValue Event(uint target) => Gh3ScriptRuntime.Structure(new[] {
                new Gh3QbField(Gh3Checksum.Value("Type"), Gh3ScriptRuntime.Key("signal")),
                new Gh3QbField(Gh3Checksum.Value("target"), new(13, -1, target)) });
            runtime.Start(Gh3Checksum.Value("LaunchEvent"), Event(43));
            runtime.Advance(5);
            Check(calls.Count == 6, "An object-specific event does not wake another object.");
            runtime.Start(Gh3Checksum.Value("LaunchEvent"), Event(42));
            runtime.Advance(5);
            Check(calls.Count == 7 && calls[^1].Time == 5 && runtime.Pending == 0, "Matching events wake blocked work at the event time.");
            runtime.Start(Gh3Checksum.Value("blocked"), target: 42, id: 12);
            runtime.Cancel(id: 12);
            runtime.Start(Gh3Checksum.Value("LaunchEvent"), Event(42));
            runtime.Advance(6);
            Check(calls.Count == 7 && runtime.Pending == 0, "Cancellation removes blocked event listeners.");
        }
        var inherited = Gh3ScriptRuntime.Structure(new[] { new Gh3QbField(Gh3Checksum.Value("value"), Gh3ScriptRuntime.Number(1)) });
        var parameters = new Dictionary<uint, Gh3QbValue> {
            [Gh3Checksum.Value("defaults")] = inherited,
            [Gh3Checksum.Value("override")] = Script("mark", (byte)3, (byte)0x4b, "defaults", "value", (byte)7, 2, "optional", (byte)7, (byte)0x2d, "absent", (byte)4, (byte)1, (byte)0x24)
        };
        Gh3QbValue? evaluated = null;
        using (var overrides = new Gh3ScriptRuntime(parameters, (_, command, args) =>
        {
            if (command != Gh3Checksum.Value("mark")) return null;
            evaluated = args;
            return new(true);
        })) overrides.Start(Gh3Checksum.Value("override"));
        Check(evaluated?.Field("value")?.Integer == 2 && evaluated.Field("optional") == null && inherited.Field("value")!.Integer == 1,
            "Evaluated structures must replace inherited fields, omit missing optional locals, and retain the source definition.");
        using (var optional = new Gh3ScriptRuntime(new Dictionary<uint, Gh3QbValue>(), (_, _, _) => null))
        {
            optional.Start(Gh3Checksum.Value("absent_transition"), optional: true);
            Check(optional.Pending == 0, "A missing optional engine transition must return without scheduling a task.");
            var failed = false;
            try { optional.Start(Gh3Checksum.Value("absent_transition")); }
            catch (InvalidDataException) { failed = true; }
            Check(failed, "A missing explicit authored command must still produce a diagnostic.");
        }
        var invalid = new byte[12];
        BinaryPrimitives.WriteInt32BigEndian(invalid.AsSpan(4), 100);
        try { Gh3ScriptCode.Expand(invalid); throw new Exception("Accepted truncated compressed script"); }
        catch (InvalidDataException) { }
        using (var components = new Gh3ScriptRuntime(values, (context, command, args) =>
        {
            if (command != Gh3Checksum.Value("mark")) return null;
            calls.Add((0, context.Object, Gh3QbText.Encode(args.Field("value")!)));
            return new(true);
        }))
        {
            calls.Clear();
            var first = components.CreateComponent(Gh3Checksum.Value("main"), null, 41);
            var second = components.CreateComponent(Gh3Checksum.Value("loop"), null, 42);
            components.Cancel();
            components.Advance(1);
            Check(calls.Count == 0, "Component scripts must wait for an engine update.");
            components.UpdateComponent(first);
            components.UpdateComponent(second);
            components.UpdateComponent(second);
            Check(calls.Count == 3 && calls[0].Target == 41 && calls[1].Target == 42 && calls[2].Target == 42, "Component frame waits must count engine updates, including updates at the same time.");
            components.Advance(3.5);
            Check(calls.Count == 3, "A suspended component must not resume on the global script clock.");
            components.UpdateComponent(first);
            components.UpdateComponent(first);
            Check(calls.Count == 3, "Component second waits must count engine update durations.");
            for (var update = 0; update < 130; update++) components.UpdateComponent(first);
            Check(calls.Count == 4 && calls[^1].Value == "[1,5]", "A component must retain locals and finish once.");
            components.RemoveObject(42);
            components.UpdateComponent(second);
            Check(calls.Count == 4 && components.Pending == 0, "Object removal must cancel component scripts.");
        }
        if (Environment.GetEnvironmentVariable("GH3_SCRIPT_SOURCE") is { } directory) Original(directory);
        Console.WriteLine("QB script locals, arithmetic, frame and second waits, object context, repeat loops, and cancellation checks passed.");
    }

    private static void Original(string directory)
    {
        var particles = new Gh3Qb(File.ReadAllBytes(Path.Combine(directory, "particle_scripts.qb"))).Sections.ToDictionary(pair => pair.Key, pair => pair.Value);
        particles[Gh3Checksum.Value("vector_array_roundtrip")] = Script("JOW_VectorToRGBA", "rgb", (byte)7, new float[] { 94.75f, 128.5f, 255 }, "a", (byte)7, 17, (byte)1,
            "mark", "value", (byte)7, (byte)0x2d, "rgba", (byte)1, (byte)0x24);
        Gh3QbValue? rgba = null;
        using (var vectors = new Gh3ScriptRuntime(particles, (_, command, parameters) =>
        {
            if (command != Gh3Checksum.Value("mark")) return null;
            rgba = parameters.Field("value");
            return new(true);
        })) vectors.Start(Gh3Checksum.Value("vector_array_roundtrip"));
        Check(rgba?.Type == 12 && rgba.Items.Select(Gh3ScriptRuntime.Numeric).SequenceEqual(new double[] { 94, 128, 255, 17 }),
            "The original color conversion must compute vector dot products and replace each local array element.");
        var sound = new Gh3Qb(File.ReadAllBytes(Path.Combine(directory, "global_sound_logic.qb"))).Sections.ToDictionary(pair => pair.Key, pair => pair.Value);
        sound[Gh3Checksum.Value("current_crowd")] = Gh3ScriptRuntime.Number(.2);
        sound[Gh3Checksum.Value("CrowdLevelForSurges")] = Gh3ScriptRuntime.Number(1.66);
        sound[Gh3Checksum.Value("game_mode")] = Gh3ScriptRuntime.Key("quickplay");
        var audioCalls = new List<uint>();
        using (var audio = new Gh3ScriptRuntime(sound, (context, command, parameters) =>
        {
            audioCalls.Add(command);
            if (command == Gh3Checksum.Value("GetPakManCurrent")) return new(true, new[] { new Gh3QbField(Gh3Checksum.Value("pak"), Gh3ScriptRuntime.Key("z_party")) });
            if (command == Gh3Checksum.Value("SoundEvent")) return new(true);
            return null;
        }))
        {
            audio.Start(Gh3Checksum.Value("GH3_Crowd_Event_Listener"), Gh3ScriptRuntime.Structure(new[] {
                new Gh3QbField(Gh3Checksum.Value("event_type"), Gh3ScriptRuntime.Key("surge_fast")),
                new Gh3QbField(Gh3Checksum.Value("override_state"), Gh3ScriptRuntime.Number(1)) }));
            Check(audioCalls.Contains(Gh3Checksum.Value("SoundEvent")), "The original crowd listener follows the override branch.");
        }
        var data = new Gh3Qb(File.ReadAllBytes(Path.Combine(directory, "z_budokan_scripts.qb"))).Sections;
        var rotations = new List<(double Time, float Z)>();
        Gh3ScriptRuntime? runtime = null;
        runtime = new(data, (context, command, parameters) =>
        {
            if (command == Gh3Checksum.Value("Obj_Rotate"))
            {
                rotations.Add((runtime!.Time, ((float[])parameters.Field("absolute")!.Data)[2]));
                return new(true);
            }
            if (command == Gh3Checksum.Value("Obj_WaitRotate")) return new(true, Wait: 1.5);
            return null;
        });
        using (runtime)
        {
            runtime.Start(Gh3Checksum.Value("Z_Budokan_MonsterWiggle"));
            runtime.Advance(3);
            Check(rotations.SequenceEqual(new[] { (0.0, 10f), (1.5, -10f), (3.0, 10f) }), "The original monster script runs its default values, vector math, waits, and loop.");
        }
        var crowd = new Gh3Qb(File.ReadAllBytes(Path.Combine(directory, "guitar_crowd.qb"))).Sections;
        var commands = new List<(double Time, uint Name)>();
        runtime = new(crowd, (context, command, parameters) =>
        {
            if (command != Gh3Checksum.Value("Crowd_PlayAnim")) return null;
            commands.Add((runtime!.Time, parameters.Field("name")!.Integer));
            return new(true);
        });
        using (runtime)
        {
            runtime.Start(Gh3Checksum.Value("Crowd_AllPlayAnim"), Gh3ScriptRuntime.Structure(new[] { new Gh3QbField(Gh3Checksum.Value("anim"), Gh3ScriptRuntime.Key("special")) }));
            runtime.Advance(1);
            Check(commands.Count == 8 && commands.Select(command => command.Name).SequenceEqual(Enumerable.Range(1, 8).Select(index => Gh3Checksum.Value("crowd" + index))),
                "The original crowd script dispatches all eight named performers.");
            Check(commands.Select((command, index) => Math.Abs(command.Time - (index + 1) / 60.0) < .000001).All(value => value),
                "The original crowd script preserves its one-frame stagger.");
        }
        var visible = new List<uint>();
        runtime = new(crowd, (context, command, parameters) =>
        {
            if (command == Gh3Checksum.Value("GetPakManCurrentName")) return new(true, new[] { new Gh3QbField(Gh3Checksum.Value("pakname"), new(3, -1, "z_video")) });
            if (command == Gh3Checksum.Value("CompositeObjectExists")) return new(true);
            if (command == Gh3Checksum.Value("Unhide")) { visible.Add(context.Object); return new(true); }
            if (command == Gh3Checksum.Value("Hide")) return new(true);
            return null;
        });
        using (runtime)
        {
            runtime.Start(Gh3Checksum.Value("Crowd_ToggleLighters"), Gh3ScriptRuntime.Structure(new[] { new Gh3QbField(0, Gh3ScriptRuntime.Key("ON")) }));
            runtime.Advance(1);
            Check(visible.Count == 15 && visible[0] == Gh3Checksum.Value("z_video_LIGHTER_geo00") && visible[^1] == Gh3Checksum.Value("z_video_LIGHTER_geo14"),
                "The original lighter loop evaluates conditions, formats object names, and calls each object.");
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
