using Gh3Skin;
using System.Text.Json;

internal static class NativeRandomLogicTests
{
    public static void Run()
    {
        var random = new Gh3Random(1);
        var limits = new[] { 5, 32761, 1000, 1, 32767, 10 };
        var expected = new[] { 3, 2660, 307, 0, 2063, 0, 2, 2374, 520, 0, 21139, 3, 4, 27737, 314, 0, 17203, 0 };
        for (var i = 0; i < expected.Length; i++) Check(random.Next(limits[i % limits.Length]) == expected[i], "Native integer random sequence differs");
        Check((random.Seed1, random.Seed2, random.Seed3) == (1399720522, 921299422, -165105005), "Native random seed state differs");
        foreach (var (a, b, type, value) in new[] { (1f, 5f, 1u, 4f), (5f, 1f, 1u, 4f), (1.000001f, 5.000001f, 1u, 4f),
            (1.0001f, 5.0001f, 2u, 3.685692310333252f), (-5f, -1f, 1u, -2f), (.5f, 1.2f, 2u, .9699786901473999f), (-.25f, .25f, 2u, .08569905161857605f) })
        {
            var result = new Gh3Random(1).Range(a, b);
            Check(result.Type == type && Gh3ScriptRuntime.Numeric(result) == value, "Native range type or value differs");
        }
        var failures = new List<Exception>();
        using var runtime = new Gh3ScriptRuntime(new Dictionary<uint, Gh3QbValue>(), (_, _, _) => null) { Failed = (_, error) => failures.Add(error) };
        var seeds = new[] { 1, 0x12b9b0a1, 0x0aa2fb3f, 2, 0x12b9b0a1, 0x0aa2fb3f };
        runtime.Start(Gh3Checksum.Value("SetRandomSeeds"), Gh3ScriptRuntime.Structure(seeds.Select((seed, i) => new Gh3QbField(Gh3Checksum.Value("seed" + (i + 1)), Gh3ScriptRuntime.Number(seed)))));
        Check(failures.Count == 0, "Native random seed binding failed");
        if (Environment.GetEnvironmentVariable("GH3_NATIVE_RANDOM_ORACLE") is { } path)
        {
            using var oracle = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var row in oracle.RootElement.GetProperty("records").EnumerateArray())
            {
                random = new(unchecked((int)row.GetProperty("seed").GetUInt32()));
                foreach (var item in row.GetProperty("values").EnumerateArray())
                    Check(random.Next(item.GetProperty("limit").GetInt32()) == item.GetProperty("value").GetInt32(), "Emulated native random output differs");
                Check(row.GetProperty("seeds").EnumerateArray().Select(seed => seed.GetInt32()).SequenceEqual(new[] { random.Seed1, random.Seed2, random.Seed3 }), "Emulated native seeds differ");
            }
        }
        if (Environment.GetEnvironmentVariable("GH3_NATIVE_CHOICES_ORACLE") is { } choicesPath)
        {
            using var oracle = JsonDocument.Parse(File.ReadAllText(choicesPath));
            foreach (var row in oracle.RootElement.GetProperty("records").EnumerateArray()) CheckChoices(row);
        }
        Console.WriteLine("Native executable random sequence, range types, endpoint ordering, and seed checks passed.");
    }

    private static void CheckChoices(JsonElement row)
    {
        var script = new Gh3QbValue(7, -1, (ReadOnlyMemory<byte>)Convert.FromBase64String(row.GetProperty("script").GetString()!));
        var values = Enumerable.Range(0, 102).ToDictionary(index => (uint)index + 1, _ => script);
        var readSeeds = Gh3Checksum.Value("ReadSeeds");
        values[readSeeds] = ScriptLogicTests.Script("GetRandomSeeds", (byte)1, "CaptureSeeds", (byte)0x2c, (byte)1, (byte)0x24);
        var results = new Dictionary<uint, int>();
        int[]? finalSeeds = null;
        using var runtime = new Gh3ScriptRuntime(values, (context, command, parameters) =>
        {
            if (command == Gh3Checksum.Value("CaptureChoice")) { results[context.Object] = (int)Gh3ScriptRuntime.Numeric(parameters.Field("choice")!); return new(true); }
            if (command == Gh3Checksum.Value("ChoiceGate")) return new(true, Events: new[] { context.Object });
            if (command != Gh3Checksum.Value("CaptureSeeds")) return null;
            finalSeeds = Enumerable.Range(1, 6).Select(index => (int)Gh3ScriptRuntime.Numeric(parameters.Field("seed" + index)!)).ToArray();
            return new(true);
        });
        var seed = unchecked((int)row.GetProperty("seed").GetUInt32());
        var seeds = new[] { seed, 0x12b9b0a1, 0x0aa2fb3f, seed, 0x12b9b0a1, 0x0aa2fb3f };
        runtime.Start(Gh3Checksum.Value("SetRandomSeeds"), Gh3ScriptRuntime.Structure(seeds.Select((value, index) =>
            new Gh3QbField(Gh3Checksum.Value("seed" + (index + 1)), Gh3ScriptRuntime.Number(value)))));
        var step = 0;
        foreach (var action in row.GetProperty("actions").EnumerateArray())
        {
            var owner = action.GetProperty("owner").GetUInt32();
            var kind = action.GetProperty("kind").GetString();
            if (kind == "release") runtime.Cancel(target: owner);
            else
            {
                results.Remove(owner);
                if (kind == "start") runtime.Start(action.GetProperty("script").GetUInt32() + 1, target: owner);
                else
                {
                    runtime.Start(Gh3Checksum.Value("LaunchEvent"), Gh3ScriptRuntime.Structure(new[] {
                        new Gh3QbField(Gh3Checksum.Value("Type"), new Gh3QbValue(13, -1, owner)) }));
                    runtime.Advance(runtime.Time);
                }
                Check(results.TryGetValue(owner, out var choice) && choice == action.GetProperty("choice").GetInt32(),
                    $"Native choice differs: opcode {row.GetProperty("opcode").GetInt32():x2}, seed {seed}, step {step}, owner {owner}, expected {action.GetProperty("choice")}, actual {choice}");
            }
            step++;
        }
        runtime.Start(readSeeds);
        var expected = row.GetProperty("seeds").EnumerateArray().Concat(row.GetProperty("seeds2").EnumerateArray()).Select(value => value.GetInt32());
        Check(finalSeeds != null && expected.SequenceEqual(finalSeeds), $"Native random-choice seed state differs for opcode {row.GetProperty("opcode").GetInt32():x2}, seed {seed}");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
