using System.Numerics;
using System.Text.Json;
using Gh3Skin;

internal static class WindLogicTests
{
    public static void Run()
    {
        if (Environment.GetEnvironmentVariable("GH3_NATIVE_WIND_ORACLE") is not { } path) return;
        using var oracle = JsonDocument.Parse(File.ReadAllText(path));
        var state = new StageWindState();
        foreach (var row in oracle.RootElement.GetProperty("commands").EnumerateArray())
        {
            var parameters = Gh3ScriptRuntime.Structure(Value(row.GetProperty("parameters")).Fields.Concat(row.GetProperty("flags").EnumerateArray()
                .Select(value => new Gh3QbField(0, Gh3ScriptRuntime.Key(value.GetString()!)))));
            Check(state.Execute(parameters) == row.GetProperty("success").GetBoolean(), "Wind command return value differs");
            CheckVector(state.Target, row, "target");
            CheckVector(state.Previous, row, "previous");
            CheckVector(state.Current, row, "current");
            CheckFields(state, row);
        }
        state = new();
        foreach (var setup in oracle.RootElement.GetProperty("setups").EnumerateArray()) state.Execute(Params(("create", Value(setup))));
        foreach (var row in oracle.RootElement.GetProperty("lifecycle").EnumerateArray())
        {
            state.Step(1f / 60);
            CheckFields(state, row);
            foreach (var query in row.GetProperty("selections").EnumerateArray())
            {
                var actual = state.Select(Vector(query, "position"), query.GetProperty("radius").GetSingle());
                var expected = query.GetProperty("selected");
                Check(actual?.Id == (expected.ValueKind == JsonValueKind.Null ? null : expected.GetUInt32()), "Wind field selection differs");
            }
        }
        foreach (var row in oracle.RootElement.GetProperty("blends").EnumerateArray())
        {
            state = new() { BlendDuration = row.GetProperty("duration").GetSingle() };
            state.Execute(Gh3ScriptRuntime.Structure(new[] { new Gh3QbField(Gh3Checksum.Value("wind"), new(6, -1, new float[] { -1, -2, 0 })) }));
            state.Execute(Gh3ScriptRuntime.Structure(new[] { new Gh3QbField(Gh3Checksum.Value("wind"), new(6, -1, new float[] { 2, 6, 4 })) }));
            foreach (var sample in row.GetProperty("samples").EnumerateArray())
            {
                state.Step(sample.GetProperty("dt").GetSingle());
                CheckVector(state.Current, sample, "current");
                Check(state.BlendTime == sample.GetProperty("time").GetSingle(), "Wind blend clock differs");
            }
        }
        foreach (var row in oracle.RootElement.GetProperty("samples").EnumerateArray())
        {
            var field = new StageWindField { Position = new(1, 2, 3), Velocity = new(2, 3, 4), Range = 8, Speed = 5,
                Type = Gh3Checksum.Value(row.GetProperty("type").GetString()!), Attenuation = Gh3Checksum.Value(row.GetProperty("attenuation").GetString()!),
                Tangent = row.GetProperty("tangent").GetSingle() };
            CheckVector(field.Sample(Vector(row, "position")), row, "value");
        }
        foreach (var row in oracle.RootElement.GetProperty("motion").EnumerateArray())
        {
            var bank = new StageScriptBank();
            using var runtime = new Gh3ScriptRuntime(bank.Values, (_, _, _) => null);
            var particles = new StageParticleState(bank, runtime, _ => false, (_, _, _) => null, _ => { });
            var id = Gh3Checksum.Value("wind_particles");
            Gh3ScriptCallResult Invoke(string name, Gh3QbValue parameters) => particles.Execute(new(Gh3Checksum.Value(name), id, parameters.Fields), Gh3Checksum.Value(name), parameters)!;
            var definition = Params(("QuickMaterial", Params(("DiffuseTexture", Gh3ScriptRuntime.Key("texture")))), ("Max", Number(16)),
                ("LifeRange", new(5, -1, new float[] { 10, 10 })), ("FollowEmitter", Number(0)), ("Pos", Vector(0, .5f, 0)), ("Force", Vector(1, -2, 3)),
                ("WindCoeff", Number(row.GetProperty("globalCoefficient").GetSingle())), ("LocalWindCoeff", Number(row.GetProperty("localCoefficient").GetSingle())));
            if (row.GetProperty("perParticle").GetBoolean()) definition = Gh3ScriptRuntime.Structure(definition.Fields.Append(new Gh3QbField(0, Gh3ScriptRuntime.Key("PerParticleWind"))));
            Invoke("CreateParticleSystem", Params(("name", new(13, -1, id)), ("params_script", definition)));
            Invoke("EngineConfig", Params(("JobQParticle", Number(1))));
            Invoke("Emit", Params(("num", Number(2))));
            var starts = row.GetProperty("starts").EnumerateArray().ToArray();
            for (var index = 0; index < starts.Length; index++) particles.Particles[index].Motion = new(Reflect(Vector(starts[index], "position")), Reflect(Vector(starts[index], "velocity")));
            Invoke("EngineWind", Params(("wind", Vector(2, -1, 3))));
            Invoke("EngineWind", Params(("create", Params(("name", Gh3ScriptRuntime.Key("field")), ("pos", Vector(1, 2, 3)), ("velocity", Vector(2, 3, 4)),
                ("range", Number(30)), ("speed", Number(5)), ("tangentweight", Number(.25)), ("attenuation", Gh3ScriptRuntime.Key("inv_distance")),
                ("type", Gh3ScriptRuntime.Key(row.GetProperty("type").GetString()!)), ("time", Number(0))))));
            var frame = 0;
            foreach (var sample in row.GetProperty("frames").EnumerateArray())
            {
                runtime.Advance(++frame / 60d);
                var index = 0;
                foreach (var expected in sample.EnumerateArray())
                {
                    var actual = particles.Particles[index++].Motion;
                    CheckVector(Reflect(actual.Position), expected, "position");
                    CheckVector(Reflect(actual.Velocity), expected, "velocity");
                }
            }
        }
        if (Environment.GetEnvironmentVariable("GH3_NATIVE_PARTICLE_WIND_ORACLE") is { } cachePath)
        {
            using var cache = JsonDocument.Parse(File.ReadAllText(cachePath));
            foreach (var row in cache.RootElement.GetProperty("systems").EnumerateArray()) CompareCache(row);
            Console.WriteLine("Native queued particle wind selection, cached fields, bounds, motion, and random states passed.");
        }
        Console.WriteLine("Native wind commands, field samples, blend clocks, and particle motion checks passed.");
    }

    private static void CompareCache(JsonElement row)
    {
        var bank = new StageScriptBank();
        StageParticleState? state = null;
        using var runtime = new Gh3ScriptRuntime(bank.Values, (context, command, parameters) => state!.Execute(context, command, parameters));
        state = new(bank, runtime, _ => false, (_, _, _) => null, _ => { });
        void Invoke(string command, Gh3QbValue parameters, uint target = 0) => runtime.Start(Gh3Checksum.Value(command), parameters, target);
        Invoke("SetRandomSeeds", Gh3ScriptRuntime.Structure(new[] { 12345, 0x12b9b0a1, 0x0aa2fb3f, 12345, 0x12b9b0a1, 0x0aa2fb3f }
            .Select((value, index) => new Gh3QbField(Gh3Checksum.Value("seed" + (index + 1)), Number(value)))));
        var id = Gh3Checksum.Value("wind_particles");
        var definition = Params(("QuickMaterial", Params(("DiffuseTexture", Gh3ScriptRuntime.Key("texture")))), ("Max", Number(64)),
            ("Emit_Rate", Number(120)), ("LifeRange", new(5, -1, new[] { .1f, .3f })), ("SpeedRange", new(5, -1, new[] { 2f, 5f })),
            ("FollowEmitter", Number(0)), ("Force", Vector(1, -2, 3)), ("EmitRangeDims", Vector(2, 3, 4)),
            ("WindCoeff", Number(.3f)), ("LocalWindCoeff", Number(.7f)),
            ("SizeRange", new(12, -1, new[] { new Gh3QbValue(5, -1, new[] { .2f, .3f }), new Gh3QbValue(5, -1, new[] { .1f, .2f }) })));
        if (row.GetProperty("perParticle").GetBoolean()) definition = Gh3ScriptRuntime.Structure(definition.Fields.Append(new Gh3QbField(0, Gh3ScriptRuntime.Key("PerParticleWind"))));
        Invoke("CreateParticleSystem", Params(("name", new(13, -1, id)), ("params_script", definition)));
        Invoke("SetAngleSpread", Params(("elevation", new(5, -1, new[] { -.125f, .25f })), ("sweep", new(5, -1, new[] { 0f, 2f }))), id);
        foreach (var sample in row.GetProperty("samples").EnumerateArray())
        {
            var frame = sample.GetProperty("frame").GetInt32();
            if (row.GetProperty("commands").TryGetProperty(frame.ToString(), out var commands))
                foreach (var command in commands.EnumerateArray()) Invoke("EngineWind", Value(command));
            Invoke("SetPos", Params(("pos", Value(sample.GetProperty("position")))), id);
            Invoke("EngineConfig", Params(("JobQParticle", Number(sample.GetProperty("queued").GetBoolean() ? 1 : 0))));
            runtime.Advance((frame + 1) / 60d);
            var emitter = state.Emitters[id];
            var context = $"frame={frame}, mode={row.GetProperty("mode")}, perParticle={row.GetProperty("perParticle")}, range={row.GetProperty("range")}, name={row.GetProperty("name")}";
            CheckVector(emitter.Wind, sample, "wind");
            Check((emitter.LocalWind?.Id ?? 0) == sample.GetProperty("local").GetUInt32(), "Particle wind selection differs: " + context);
            Check(emitter.Seed == unchecked((int)sample.GetProperty("seed").GetUInt32()), "Particle queued seed differs: " + context);
            CheckVector(Reflect(emitter.Bounds.Center), sample, "center");
            Check(Math.Abs(emitter.Bounds.Radius - sample.GetProperty("radius").GetSingle()) < .00001f, "Particle wind bounds differ: " + context);
            var expected = sample.GetProperty("particles").EnumerateArray().ToArray();
            Check(state.Particles.Count == expected.Length, "Particle wind count differs: " + context);
            for (var index = 0; index < expected.Length; index++)
            {
                var actual = state.Particles[index];
                CheckVector(Reflect(actual.Motion.Position), expected[index], "position", context);
                CheckVector(Reflect(actual.Motion.Velocity), expected[index], "velocity", context);
                Check(actual.Age == expected[index].GetProperty("age").GetSingle() && actual.Life == expected[index].GetProperty("life").GetSingle(), "Particle wind lifecycle differs: " + context);
            }
        }
        var seeds = row.GetProperty("seeds").EnumerateArray().Select(value => unchecked((int)value.GetUInt32())).ToArray();
        var random = new Gh3Random(seeds[0]) { Seed2 = seeds[1], Seed3 = seeds[2] };
        Check(runtime.NextRandom(10000) == random.Next(10000), "Queued particle RNG differs");
    }

    private static Gh3QbValue Value(JsonElement value) => value.ValueKind switch {
        JsonValueKind.Object => Gh3ScriptRuntime.Structure(value.EnumerateObject().Select(field => new Gh3QbField(Gh3Checksum.Value(field.Name), Value(field.Value)))),
        JsonValueKind.Array => new(6, -1, value.EnumerateArray().Select(item => item.GetSingle()).ToArray()),
        JsonValueKind.String => value.GetString() is "" ? new(13, -1, 0u) : Gh3ScriptRuntime.Key(value.GetString()!),
        _ => Gh3ScriptRuntime.Number(value.GetDouble())
    };
    private static void CheckFields(StageWindState state, JsonElement row)
    {
        var fields = row.GetProperty("fields").EnumerateArray().ToArray();
        Check(fields.Length == state.Fields.Count, "Wind creation or removal differs");
        for (var index = 0; index < fields.Length; index++)
        {
            var actual = state.Fields[index];
            var expected = fields[index];
            Check(actual.Id == expected.GetProperty("id").GetUInt32() && actual.Type == expected.GetProperty("type").GetUInt32() &&
                actual.Attenuation == expected.GetProperty("attenuation").GetUInt32(), "Wind identity or type differs");
            CheckVector(actual.Position, expected, "position");
            CheckVector(actual.Velocity, expected, "velocity");
            Check(actual.Age == expected.GetProperty("age").GetSingle() && actual.Duration == expected.GetProperty("time").GetSingle() &&
                actual.Speed == expected.GetProperty("speed").GetSingle() && actual.Tangent == expected.GetProperty("tangent").GetSingle() &&
                actual.Range == expected.GetProperty("range").GetSingle(), "Wind parameters differ");
        }
    }
    private static Vector3 Vector(JsonElement row, string name) { var values = row.GetProperty(name).EnumerateArray().Select(value => value.GetSingle()).ToArray(); return new(values[0], values[1], values[2]); }
    private static Gh3QbValue Vector(float x, float y, float z) => new(6, -1, new[] { x, y, z });
    private static Vector3 Reflect(Vector3 value) => new(value.X, value.Y, -value.Z);
    private static Gh3QbValue Number(double value) => Gh3ScriptRuntime.Number(value);
    private static Gh3QbValue Params(params (string Name, Gh3QbValue Value)[] fields) => Gh3ScriptRuntime.Structure(fields.Select(field => new Gh3QbField(Gh3Checksum.Value(field.Name), field.Value)));
    private static void CheckVector(Vector3 actual, JsonElement expected, string name, string context = "") => Check(Vector3.Distance(actual, Vector(expected, name)) < .00001f, $"Wind {name} differs: {actual} != {Vector(expected, name)}; {context}");
    private static void Check(bool success, string message) { if (!success) throw new Exception(message); }
}
