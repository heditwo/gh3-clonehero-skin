using System.Numerics;
using System.Text.Json;
using Gh3Skin;

internal static class ParticleCallbackTests
{
    public static void Run()
    {
        if (Environment.GetEnvironmentVariable("GH3_NATIVE_PARTICLE_CALLBACK_ORACLE") is { } path) Compare(path);
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "{\"version\":1,\"definitions\":{}}");
            var bank = StageScriptBank.Load(file);
            StageParticleState? state = null;
            var calls = new List<uint>();
            using var runtime = new Gh3ScriptRuntime(bank.Values, (context, command, parameters) =>
            {
                if (command != Key("mark")) return state!.Execute(context, command, parameters);
                calls.Add(context.Object);
                return new(true);
            });
            state = new(bank, runtime, _ => false, (_, _, _) => null, _ => { });
            var definition = Params(("QuickMaterial", Params(("DiffuseTexture", Gh3ScriptRuntime.Key("texture")))), ("Max", Number(2)),
                ("LifeRange", new(5, -1, new[] { 10f, 10f })), ("BounceHeight", Number(0)), ("BounceCoeff", Number(0)),
                ("Force", new(6, -1, new[] { 0f, -4f, 0f })), ("BounceCallback", Gh3ScriptRuntime.Key("bounce")), ("BounceCallbackRate", Number(1)));
            runtime.Set(Key("bounce"), ScriptLogicTests.Script((byte)0x20, (byte)1, "mark", (byte)1, "Wait", 1, "gameframe", (byte)1, (byte)0x21, (byte)1, (byte)0x24));
            Gh3ScriptCallResult Call(string command, Gh3QbValue parameters, uint target = 0) => state.Execute(new(Key(command), target, parameters.Fields), Key(command), parameters)!;
            Call("CreateParticleSystem", Params(("name", Gh3ScriptRuntime.Key("bounce_system")), ("params_script", definition)));
            if (Environment.GetEnvironmentVariable("GH3_NATIVE_PARTICLE_CALLBACK_ORACLE") is { } controlsPath)
            {
                using var controls = JsonDocument.Parse(File.ReadAllText(controlsPath));
                foreach (var row in controls.RootElement.GetProperty("independent").EnumerateArray())
                {
                    var value = row.GetProperty("value");
                    var parameters = Gh3ScriptRuntime.Structure(value.ValueKind == JsonValueKind.Null ? Array.Empty<Gh3QbField>() : new[] { new Gh3QbField(0, Number(value.GetDouble())) });
                    Call("SetEmitterIndependent", parameters, Key("bounce_system"));
                    Check(state.Emitters[Key("bounce_system")].Independent == row.GetProperty("enabled").GetBoolean(), "Native independent-emitter command differs");
                }
            }
            Call("Emit", Params(("num", Number(2))), Key("bounce_system"));
            runtime.Advance(1 / 60d);
            Check(calls.SequenceEqual(new[] { Key("bounce_system"), Key("bounce_system") }), "Bounce scripts must retain object context and resume for each collision in a frame");
            runtime.Advance(1 / 60d);
            Check(calls.Count == 2, "Paused particle callbacks must not resume");
            Call("Destroy", Params(), Key("bounce_system"));
            runtime.Advance(1);
            Check(calls.Count == 2 && runtime.Pending == 0, "Particle removal must cancel the bounce script");
            runtime.Set(Key("single_bounce"), ScriptLogicTests.Script("mark", (byte)1, "Die", (byte)1, "mark", (byte)1, (byte)0x24));
            definition = Gh3ScriptRuntime.Structure(definition.Fields.Where(field => field.Name != Key("BounceCallback"))
                .Append(new Gh3QbField(Key("BounceCallback"), Gh3ScriptRuntime.Key("single_bounce"))));
            Call("CreateParticleSystem", Params(("name", Gh3ScriptRuntime.Key("self_remove")), ("params_script", definition)));
            Call("Emit", Params(("num", Number(2))), Key("self_remove"));
            runtime.Advance(2);
            Check(calls.Count == 3 && calls[^1] == Key("self_remove") && !state.Exists(Key("self_remove")) && runtime.Pending == 0,
                "A bounce callback must safely remove its own system and stop its script");
        }
        finally { File.Delete(file); }
        Console.WriteLine("Particle callback scheduling, component clocks, object context, and removal checks passed.");
    }

    private static void Compare(string path)
    {
        using var oracle = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var row in oracle.RootElement.GetProperty("sourceIndependent").EnumerateArray())
        {
            var material = new Gh3QbField(Key("QuickMaterial"), Params(("DiffuseTexture", Gh3ScriptRuntime.Key("texture"))));
            var fields = row.GetProperty("fields").EnumerateArray().Select(field => field[1].ValueKind == JsonValueKind.Null ? new Gh3QbField(0, Gh3ScriptRuntime.Key(field[0].GetString()!)) :
                new Gh3QbField(Key(field[0].GetString()!), Number(field[1].GetDouble())));
            var definition = new StageParticleDefinition(Gh3ScriptRuntime.Structure(new[] { material }.Concat(fields)));
            Check(definition.EmitterIndependent == (row.GetProperty("independent").GetInt32() > 0) && definition.Follow == row.GetProperty("follow").GetSingle(),
                "Native independent-emitter parsing or follow-field order differs");
        }
        foreach (var row in oracle.RootElement.GetProperty("waits").EnumerateArray())
        {
            var unit = row.GetProperty("unit").GetString()!;
            var tokens = new List<object> { "Wait", row.GetProperty("amount").GetSingle() };
            if (unit.Length > 0) tokens.Add(unit);
            tokens.AddRange(new object[] { (byte)1, "mark", (byte)1, (byte)0x24 });
            var calls = 0;
            using var runtime = new Gh3ScriptRuntime(new Dictionary<uint, Gh3QbValue> { [Key("wait_test")] = ScriptLogicTests.Script(tokens.ToArray()) }, (_, command, _) =>
            {
                if (command != Key("mark")) return null;
                calls++;
                return new(true);
            }) { FrameDuration = row.GetProperty("duration").GetSingle() };
            var task = runtime.CreateComponent(Key("wait_test"), null, 0);
            runtime.UpdateComponent(task);
            var updates = 0;
            while (calls == 0 && updates < 500) { runtime.UpdateComponent(task); updates++; }
            Check(calls == 1 && updates == row.GetProperty("updates").GetInt32(), "Native component wait differs: " + row);
        }
        foreach (var row in oracle.RootElement.GetProperty("clocks").EnumerateArray())
        {
            var fields = new List<Gh3QbField> { new(0, Number(row.GetProperty("amount").GetDouble())) };
            var unit = row.GetProperty("unit").GetString()!;
            if (unit.Length > 0) fields.Add(new(0, Gh3ScriptRuntime.Key(unit)));
            fields.AddRange(row.GetProperty("flags").EnumerateArray().Select(flag => new Gh3QbField(0, Gh3ScriptRuntime.Key(flag.GetString()!))));
            var waits = Enumerable.Range(0, 2).Select(_ => new Gh3ComponentWait(Gh3ScriptRuntime.Structure(fields), row.GetProperty("start").GetUInt32())).ToArray();
            var complete = new bool[2];
            var tokens = new List<object> { "Wait", row.GetProperty("amount").GetSingle() };
            if (unit.Length > 0) tokens.Add(unit);
            tokens.AddRange(row.GetProperty("flags").EnumerateArray().Select(flag => (object)flag.GetString()!));
            tokens.AddRange(new object[] { (byte)1, "mark", (byte)1, (byte)0x24 });
            var calls = new int[2];
            using var runtime = new Gh3ScriptRuntime(new Dictionary<uint, Gh3QbValue> { [Key("clock_test")] = ScriptLogicTests.Script(tokens.ToArray()) }, (context, command, _) =>
            {
                if (command != Key("mark")) return null;
                calls[context.Object]++;
                return new(true);
            }, time: row.GetProperty("start").GetDouble() / 1000) { UnscaledFrameDuration = 1f / 60 };
            var tasks = Enumerable.Range(0, 2).Select(index => runtime.CreateComponent(Key("clock_test"), null, (uint)index)).ToArray();
            var taken = -1;
            foreach (var step in row.GetProperty("steps").EnumerateArray())
            {
                var index = step.GetProperty("index").GetInt32();
                var frame = step.GetProperty("frame").GetInt32();
                bool Take() { if (taken == frame) return false; taken = frame; return true; }
                if (!complete[index]) complete[index] = waits[index].Advance(step.GetProperty("duration").GetSingle(), 1f / 60, step.GetProperty("milliseconds").GetUInt32(), Take);
                Check(complete[index] == step.GetProperty("complete").GetBoolean(), $"Native component clock differs: {unit}, {row.GetProperty("flags")}, frame {frame}, task {index}");
                runtime.FrameDuration = step.GetProperty("duration").GetSingle();
                runtime.Advance(step.GetProperty("milliseconds").GetDouble() / 1000);
                runtime.UpdateComponent(tasks[index]);
                Check(calls[index] == (complete[index] ? 1 : 0), $"Runtime component clock differs: {unit}, {row.GetProperty("flags")}, frame {frame}, task {index}");
            }
        }
        foreach (var row in oracle.RootElement.GetProperty("bounces").EnumerateArray())
        {
            var random = new Gh3Random(12345);
            var draws = new List<int>();
            int Next(int limit) { var value = random.Next(limit); draws.Add(value); return value; }
            var particles = row.GetProperty("initial").EnumerateArray().Select(value => new StageParticleMotion(Vector(value, "position"), Vector(value, "velocity"))).ToArray();
            var resumes = row.GetProperty("resumes").EnumerateArray().ToArray();
            var resumed = 0;
            var frame = 0;
            foreach (var sample in row.GetProperty("samples").EnumerateArray())
            {
                for (var index = 0; index < particles.Length; index++)
                {
                    particles[index] = particles[index].Step(sample.GetProperty("duration").GetSingle(), new(0, -4, 0), true, .25f,
                        row.GetProperty("coefficient").GetSingle(), out var collision);
                    var actual = particles[index];
                    var expected = sample.GetProperty("particles")[index];
                    Check(Vector3.Distance(actual.Position, Vector(expected, "position")) < .00001f && Vector3.Distance(actual.Velocity, Vector(expected, "velocity")) < .00001f,
                        "Native callback particle motion differs");
                    if (!StageParticleMotion.Callback(collision, row.GetProperty("rate").GetSingle(), row.GetProperty("enabled").GetBoolean(), Next)) continue;
                    Check(resumed < resumes.Length && resumes[resumed].GetProperty("frame").GetInt32() == frame && resumes[resumed].GetProperty("particle").GetInt32() == index,
                        "Native bounce probability or callback order differs");
                    resumed++;
                }
                frame++;
            }
            Check(resumed == resumes.Length && draws.SequenceEqual(row.GetProperty("draws").EnumerateArray().Select(value => value.GetInt32())), "Native bounce random draws differ");
            Check(new[] { (uint)random.Seed1, (uint)random.Seed2, (uint)random.Seed3 }.SequenceEqual(row.GetProperty("seeds").EnumerateArray().Select(value => value.GetUInt32())), "Native bounce random state differs");
        }
    }

    private static uint Key(string name) => Gh3Checksum.Value(name);
    private static Gh3QbValue Number(double value) => Gh3ScriptRuntime.Number(value);
    private static Gh3QbValue Params(params (string Name, Gh3QbValue Value)[] fields) => Gh3ScriptRuntime.Structure(fields.Select(field => new Gh3QbField(Key(field.Name), field.Value)));
    private static Vector3 Vector(JsonElement row, string field) { var values = row.GetProperty(field).EnumerateArray().Select(value => value.GetSingle()).ToArray(); return new(values[0], values[1], values[2]); }
    private static void Check(bool success, string message) { if (!success) throw new Exception(message); }
}
