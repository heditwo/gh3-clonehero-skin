using System.Buffers.Binary;
using System.Text.Json;
using Gh3Skin;

internal static class PcCommandTests
{
    public static void Run()
    {
        if (Environment.GetEnvironmentVariable("GH3_NATIVE_PC_COMMANDS_ORACLE") is not { } path) return;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var commands = document.RootElement.GetProperty("constants").EnumerateArray().ToArray();
        if (commands.Length != Gh3PcCommands.Constants.Count) throw new Exception("PC constant-command count differs");
        var comparisons = 0;
        foreach (var command in commands)
        {
            var name = command.GetProperty("name").GetString()!;
            var id = command.GetProperty("checksum").GetUInt32();
            var actual = Gh3PcCommands.Execute(id) ?? throw new Exception("PC command is missing: " + name);
            if (Gh3PcCommands.Constants[id].Name != name || actual.Values != null || actual.Wait != 0 || actual.Events != null || actual.Waiting != null || actual.ComponentWait != null)
                throw new Exception("PC constant command changed output fields or scheduled work: " + name);
            foreach (var row in command.GetProperty("rows").EnumerateArray())
            {
                if (actual.Success != row.GetProperty("result").GetBoolean()) throw new Exception("PC command result differs: " + name);
                comparisons++;
            }
            var observed = new List<bool>();
            var program = Conditional(name);
            using var runtime = new Gh3ScriptRuntime(new Dictionary<uint, Gh3QbValue> { [Gh3Checksum.Value("main")] = program }, (_, key, parameters) =>
            {
                if (key != Gh3Checksum.Value("capture")) return null;
                if (parameters.Field("sentinel")?.Integer != 97) throw new Exception("PC command overwrote a script local: " + name);
                observed.Add(parameters.Field("value")!.Integer == 1);
                return new(true);
            });
            runtime.Start(Gh3Checksum.Value("main"), target: 42);
            if (observed.Count != 1 || observed[0] != actual.Success || runtime.Pending != 0) throw new Exception("PC command branch differs: " + name);
            var call = new Gh3ScriptEvent("performance", Gh3ScriptRuntime.Structure(new[] {
                new Gh3QbField(Gh3Checksum.Value("time"), Gh3ScriptRuntime.Number(0)),
                new Gh3QbField(Gh3Checksum.Value("scr"), Gh3ScriptRuntime.Key(name)) }));
            if (Gh3CommandDispatch.Resolve(call).Status != "implemented") throw new Exception("PC command reports missing playback: " + name);
        }
        if (Gh3PcCommands.Execute(Gh3Checksum.Value("unrecognized_custom_command")) != null) throw new Exception("Unknown PC command was accepted");
        Console.WriteLine($"Native PC constant results, script branches, and unchanged locals passed: {commands.Length} commands, {comparisons} native calls.");
    }

    private static Gh3QbValue Conditional(string name)
    {
        byte[] Code(params object[] tokens) => Gh3ScriptCode.Expand(((ReadOnlyMemory<byte>)ScriptLogicTests.Script(tokens).Data).Span);
        var setup = Code("sentinel", (byte)7, 97, (byte)1);
        var condition = Code(name, "unrecognized_argument", (byte)7, 9, (byte)1);
        var success = Code("capture", "value", (byte)7, 1, "sentinel", (byte)7, (byte)0x2d, "sentinel", (byte)1);
        var failure = Code("capture", "value", (byte)7, 0, "sentinel", (byte)7, (byte)0x2d, "sentinel", (byte)1);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(new byte[12]);
        writer.Write(setup);
        writer.Write((byte)0x47);
        writer.Write(checked((ushort)(2 + condition.Length + success.Length + 3)));
        writer.Write(condition);
        writer.Write(success);
        writer.Write((byte)0x48);
        writer.Write(checked((ushort)(2 + failure.Length)));
        writer.Write(failure);
        writer.Write((byte)0x28);
        writer.Write((byte)0x24);
        var bytes = stream.ToArray();
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), bytes.Length - 12);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), bytes.Length - 12);
        return new(7, -1, (ReadOnlyMemory<byte>)bytes);
    }
}
