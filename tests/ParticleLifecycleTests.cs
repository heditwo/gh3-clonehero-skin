using System.Numerics;
using System.Text.Json;
using Gh3Skin;

internal static class ParticleLifecycleTests
{
    public static void Run(StageScriptBank bank)
    {
        if (Environment.GetEnvironmentVariable("GH3_NATIVE_PARTICLE_BIRTH_ORACLE") is not { } path) return;
        using var oracle = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var row in oracle.RootElement.GetProperty("lifecycles").EnumerateArray())
        {
            using var runtime = new Gh3ScriptRuntime(bank.Values, (_, _, _) => null);
            var state = new StageParticleState(bank, runtime, _ => false, (_, _, _) => null, _ => { });
            var id = Gh3Checksum.Value("lifecycle");
            Gh3ScriptCallResult Invoke(string name, Gh3QbValue parameters) => state.Execute(new(Gh3Checksum.Value(name), id, parameters.Fields), Gh3Checksum.Value(name), parameters)!;
            var definition = Params(("QuickMaterial", Params(("DiffuseTexture", Gh3ScriptRuntime.Key("texture")))),
                ("Max", Number(4)), ("Emit_Rate", Number(120)), ("LifeRange", Pair(.05f, .13f)), ("SpeedRange", Pair(2, 5)),
                ("RotVel", Pair(-1, 2)), ("RotVelTimeScale", Number(row.GetProperty("rotationScale").GetSingle())),
                ("FollowEmitter", Number(row.GetProperty("follow").GetSingle())), ("Pos", Vector(new(1, 2, 3))),
                ("Force", Vector(new(1, -2, 3))), ("EmitRangeDims", Vector(new(2, 3, 4))));
            if (row.GetProperty("fillPath").GetBoolean()) definition = Gh3ScriptRuntime.Structure(definition.Fields.Append(new Gh3QbField(0, Gh3ScriptRuntime.Key("FillPath"))));
            Invoke("CreateParticleSystem", Params(("name", Gh3ScriptRuntime.Key("lifecycle")), ("params_script", definition)));
            Invoke("SetAngleSpread", Params(("elevation", Pair(-.125f, .25f)), ("sweep", Pair(0, 2))));
            var speeds = row.GetProperty("rotationSpeeds").EnumerateArray().Select(value => value.GetSingle()).ToArray();
            speeds.CopyTo(state.Emitters[id].RotationSpeeds, 0);
            runtime.Start(Gh3Checksum.Value("SetRandomSeeds"), Gh3ScriptRuntime.Structure(new[] { 12345, 0x12b9b0a1, 0x0aa2fb3f, 12345, 0x12b9b0a1, 0x0aa2fb3f }
                .Select((value, index) => new Gh3QbField(Gh3Checksum.Value("seed" + (index + 1)), Number(value)))));
            var tick = 0;
            var expectedRandom = new Gh3Random(12345);
            foreach (var sample in row.GetProperty("samples").EnumerateArray())
            {
                var frame = sample.GetProperty("frame").GetInt32();
                Invoke("SetPos", Params(("pos", Vector(ReadVector(sample, "position")))));
                if (frame == 25) Invoke("KillOldParticles", Params(("time", Number(.02))));
                if (frame == 40) Invoke("EmptySystem", Params());
                Invoke("EmitRate", Params(("rate", Number(frame >= 50 && frame < 60 ? 0 : 120))));
                if (sample.GetProperty("dt").GetSingle() > 0) tick++;
                runtime.Advance(tick / 60d);
                var expected = sample.GetProperty("particles").EnumerateArray().ToArray();
                Check(state.Particles.Count == expected.Length, $"Particle count differs at frame {frame}: {state.Particles.Count} != {expected.Length}");
                for (var index = 0; index < expected.Length; index++)
                {
                    var particle = state.Particles[index];
                    var native = expected[index];
                    var context = $"frame {frame}, particle {index}, follow {row.GetProperty("follow")}";
                    Check(Vector3.Distance(particle.Motion.Position, Reflect(ReadVector(native, "position"))) < .00001f, $"Particle position differs at {context}: {particle.Motion.Position}; {native.GetProperty("position")}");
                    Check(Vector3.Distance(particle.Motion.Velocity, Reflect(ReadVector(native, "velocity"))) < .00001f, "Particle velocity differs at " + context);
                    Check(particle.Age == native.GetProperty("age").GetSingle() && particle.Life == native.GetProperty("life").GetSingle(), "Particle age or life differs at " + context);
                    Check(particle.RotationSpeed == native.GetProperty("rotationSpeed").GetSingle(), "Reused particle rotation speed differs at " + context);
                    Check(Math.Abs(particle.CurrentRotation - native.GetProperty("rotation").GetSingle()) < .000001f, "Particle rotation time scale differs at " + context);
                }
                var seeds = sample.GetProperty("seeds").EnumerateArray().Select(value => unchecked((int)value.GetUInt32())).ToArray();
                expectedRandom = new(seeds[0]) { Seed2 = seeds[1], Seed3 = seeds[2] };
            }
            Check(runtime.NextRandom(10000) == expectedRandom.Next(10000), "Particle lifecycle random state differs");
        }
        Console.WriteLine("Native particle lifecycle, capacity, removal order, rotation, and moving-emitter checks passed.");
    }

    private static Gh3QbValue Number(double value) => Gh3ScriptRuntime.Number(value);
    private static Gh3QbValue Pair(float a, float b) => new(5, -1, new[] { a, b });
    private static Gh3QbValue Vector(Vector3 value) => new(6, -1, new[] { value.X, value.Y, value.Z });
    private static Gh3QbValue Params(params (string Name, Gh3QbValue Value)[] fields) => Gh3ScriptRuntime.Structure(fields.Select(field => new Gh3QbField(Gh3Checksum.Value(field.Name), field.Value)));
    private static Vector3 ReadVector(JsonElement row, string name) { var values = row.GetProperty(name).EnumerateArray().Select(value => value.GetSingle()).ToArray(); return new(values[0], values[1], values[2]); }
    private static Vector3 Reflect(Vector3 value) => new(value.X, value.Y, -value.Z);
    private static void Check(bool success, string message) { if (!success) throw new Exception(message); }
}
