using System.Numerics;
using System.Text.Json;
using Gh3Skin;

internal static class ParticlePrewarmTests
{
    public static void Run(StageScriptBank bank)
    {
        foreach (var name in new[] { "GH3_NATIVE_PARTICLE_PREWARM_ORACLE", "GH3_NATIVE_PARTICLE_SCHEDULE_ORACLE" })
            if (Environment.GetEnvironmentVariable(name) is { } path) Compare(bank, path);
    }

    private static void Compare(StageScriptBank bank, string path)
    {
        using var oracle = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var row in oracle.RootElement.GetProperty("rows").EnumerateArray())
        {
            StageParticleState? state = null;
            var frame = 0;
            var parentDeath = row.TryGetProperty("parentDeath", out var death) ? death.GetInt32() : -1;
            var parent = parentDeath >= 0 ? 1u : 0u;
            var updates = new List<(int Frame, int Particles)>();
            using var runtime = new Gh3ScriptRuntime(bank.Values, (_, command, _) =>
            {
                if (command != Gh3Checksum.Value("mark_update")) return null;
                updates.Add((frame, state!.Particles.Count));
                return new(true);
            });
            state = new(bank, runtime, target => target == parent && frame < parentDeath, (_, _, _) => new StageObjectPose(Vector3.Zero, Quaternion.Identity, true), _ => { });
            var id = Gh3Checksum.Value("prewarm");
            var schedule = row.TryGetProperty("delay", out var delay);
            var capacity = schedule ? 16 : row.GetProperty("capacity").GetInt32();
            var rate = schedule ? 120 : row.GetProperty("rate").GetSingle();
            Gh3ScriptCallResult Invoke(string name, Gh3QbValue parameters) => state.Execute(new(Gh3Checksum.Value(name), id, parameters.Fields), Gh3Checksum.Value(name), parameters)!;
            var definition = Params(("QuickMaterial", Params(("DiffuseTexture", Gh3ScriptRuntime.Key("texture")))),
                ("Max", Number(capacity)), ("Emit_Rate", Number(rate)),
                ("History", Number(row.TryGetProperty("history", out var historyLength) ? historyLength.GetInt32() : 1)),
                ("EmitDelayStart", Number(schedule ? delay.GetSingle() : 0)), ("Lifetime", Number(schedule ? row.GetProperty("lifetime").GetSingle() : 0)),
                ("TimeSeed", Number(row.GetProperty("timeSeed").GetSingle())), ("LifeRange", schedule ? Pair(.05f, .13f) : Pair(.2f, 1.1f)), ("SpeedRange", Pair(2, 5)),
                ("FollowEmitter", Number(0)), ("Pos", Vector(1, 2, 3)), ("Force", Vector(1, -2, 3)), ("EmitRangeDims", Vector(2, 3, 4)));
            if (row.TryGetProperty("updates", out var expectedUpdates))
            {
                runtime.Set(Gh3Checksum.Value("update_particle"), ScriptLogicTests.Script((byte)0x20, (byte)1, "mark_update", (byte)1,
                    "Wait", 1, "frame", (byte)1, (byte)0x21, (byte)1, (byte)0x24));
                definition = Gh3ScriptRuntime.Structure(definition.Fields.Append(new Gh3QbField(Gh3Checksum.Value("UpdateScript"), Gh3ScriptRuntime.Key("update_particle"))));
            }
            Invoke("CreateParticleSystem", Params(("name", new(13, -1, unchecked(id - parent))), ("ObjID", new(13, -1, parent)), ("params_script", definition)));
            Invoke("SetAngleSpread", Params(("elevation", Pair(-.125f, .25f)), ("sweep", Pair(0, 2))));
            runtime.Start(Gh3Checksum.Value("SetRandomSeeds"), Gh3ScriptRuntime.Structure(new[] { 12345, 0x12b9b0a1, 0x0aa2fb3f, 12345, 0x12b9b0a1, 0x0aa2fb3f }
                .Select((value, index) => new Gh3QbField(Gh3Checksum.Value("seed" + (index + 1)), Number(value)))));
            foreach (var sample in row.GetProperty("samples").EnumerateArray())
            {
                frame = sample.GetProperty("frame").GetInt32();
                runtime.Advance((frame + 1) / 60d);
                var expected = sample.GetProperty("particles").EnumerateArray().ToArray();
                var context = $"frame={frame}, seed={row.GetProperty("timeSeed")}, rate={rate}, capacity={capacity}, delay={delay}";
                if (schedule)
                {
                    Check(state.Exists(id) == sample.GetProperty("alive").GetBoolean(), "Particle cleanup time differs: " + context);
                    if (state.Exists(id))
                    {
                        var emitter = state.Emitters[id];
                        Check(emitter.DelayRemaining == sample.GetProperty("delay").GetSingle() && emitter.Emission.Time == sample.GetProperty("time").GetSingle(), "Particle component clock differs: " + context);
                        Check(emitter.Rate == sample.GetProperty("rate").GetSingle(), "Particle lifetime cutoff differs: " + context);
                    }
                }
                Check(state.Particles.Count == expected.Length, "Prewarmed particle count differs: " + context);
                if (sample.TryGetProperty("head", out var head)) Check(state.Emitters[id].HistoryIndex == head.GetInt32(), "Prewarmed history cursor differs: " + context);
                for (var index = 0; index < expected.Length; index++)
                {
                    var particle = state.Particles[index];
                    var native = expected[index];
                    Check(Vector3.Distance(particle.Motion.Position, Reflect(ReadVector(native, "position"))) < .00001f, "Prewarmed position differs: " + context);
                    Check(Vector3.Distance(particle.Motion.Velocity, Reflect(ReadVector(native, "velocity"))) < .00001f, "Prewarmed velocity differs: " + context);
                    Check(particle.Age == native.GetProperty("age").GetSingle() && particle.Life == native.GetProperty("life").GetSingle(), "Prewarmed age or life differs: " + context);
                    if (native.TryGetProperty("history", out var history))
                    {
                        var entry = 0;
                        foreach (var point in history.EnumerateArray())
                        {
                            var values = point.EnumerateArray().Select(value => value.GetSingle()).ToArray();
                            Check(Vector4.Distance(particle.History[entry++], new(values[0], values[1], -values[2], values[3])) < .00001f, "Prewarmed history differs: " + context);
                        }
                    }
                }
            }
            if (expectedUpdates.ValueKind == JsonValueKind.Array)
                Check(updates.SequenceEqual(expectedUpdates.EnumerateArray().Select(value => (value.GetProperty("frame").GetInt32(), value.GetProperty("particles").GetInt32()))),
                    "Particle update-script clock, prewarm order, or empty-system cancellation differs");
            var random = new Gh3CrtRandom();
            if (row.TryGetProperty("crtDraws", out var crtDraws))
            {
                foreach (var draw in crtDraws.EnumerateArray()) Check(random.Next() == draw.GetInt32(), "CRT random draw differs");
                Check(random.State == row.GetProperty("crtState").GetUInt32(), "CRT random state differs");
            }
            else random.State = row.GetProperty("crtState").GetUInt32();
            Check(runtime.NextCrtRandom() == random.Next(), "Prewarm consumed different CRT random draws");
            var seeds = row.GetProperty("seeds").EnumerateArray().Select(value => unchecked((int)value.GetUInt32())).ToArray();
            var expectedRandom = new Gh3Random(seeds[0]) { Seed2 = seeds[1], Seed3 = seeds[2] };
            Check(runtime.NextRandom(10000) == expectedRandom.Next(10000), "Prewarm main random state differs");
        }
        Console.WriteLine("Native particle TimeSeed, CRT random, delay, lifetime, and component-clock checks passed: " + Path.GetFileName(path));
    }

    private static Gh3QbValue Number(double value) => Gh3ScriptRuntime.Number(value);
    private static Gh3QbValue Pair(float a, float b) => new(5, -1, new[] { a, b });
    private static Gh3QbValue Vector(float x, float y, float z) => new(6, -1, new[] { x, y, z });
    private static Gh3QbValue Params(params (string Name, Gh3QbValue Value)[] fields) => Gh3ScriptRuntime.Structure(fields.Select(field => new Gh3QbField(Gh3Checksum.Value(field.Name), field.Value)));
    private static Vector3 ReadVector(JsonElement row, string name) { var values = row.GetProperty(name).EnumerateArray().Select(value => value.GetSingle()).ToArray(); return new(values[0], values[1], values[2]); }
    private static Vector3 Reflect(Vector3 value) => new(value.X, value.Y, -value.Z);
    private static void Check(bool success, string message) { if (!success) throw new Exception(message); }
}
