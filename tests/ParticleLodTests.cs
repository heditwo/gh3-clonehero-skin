using System.Text.Json;
using System.Numerics;
using Gh3Skin;

internal static class ParticleLodTests
{
    public static void Run()
    {
        if (Environment.GetEnvironmentVariable("GH3_NATIVE_PARTICLE_LOD_ORACLE") is not { } path) return;
        using var oracle = JsonDocument.Parse(File.ReadAllText(path));
        var bank = new StageScriptBank();
        foreach (var row in oracle.RootElement.GetProperty("configurations").EnumerateArray())
        {
            StageParticleState? state = null;
            using var runtime = new Gh3ScriptRuntime(bank.Values, (context, command, parameters) => state!.Execute(context, command, parameters));
            state = new(bank, runtime, _ => false, (_, _, _) => null, message => throw new Exception(message));
            var initial = row.GetProperty("initial").GetBoolean() ? 1 : 0;
            runtime.Start(Key("EngineConfig"), Params(("ParticleLOD", Gh3ScriptRuntime.Number(initial)), ("ParticleSystem", Gh3ScriptRuntime.Number(initial))));
            var value = row.GetProperty("value");
            runtime.Start(Key("EngineConfiguration"), value.ValueKind == JsonValueKind.Null ? Params() : Params(("ParticleLOD", Gh3ScriptRuntime.Number(value.GetInt32())), ("ParticleSystem", Gh3ScriptRuntime.Number(value.GetInt32()))));
            Check(state.SystemsEnabled == row.GetProperty("active").GetBoolean() && state.LodEnabled == row.GetProperty("lod").GetBoolean(), "Native particle engine configuration differs");
        }
        foreach (var row in oracle.RootElement.GetProperty("source").EnumerateArray())
        {
            StageParticleState? state = null;
            using var runtime = new Gh3ScriptRuntime(bank.Values, (context, command, parameters) => state!.Execute(context, command, parameters));
            state = new(bank, runtime, _ => false, (_, _, _) => null, _ => { });
            var source = new List<Gh3QbField> { new(Key("QuickMaterial"), Params(("DiffuseTexture", Gh3ScriptRuntime.Key("texture")))) };
            foreach (var field in row.GetProperty("fields").EnumerateArray())
            {
                var name = field[0].GetString()!;
                var value = field[1];
                source.Add(value.ValueKind == JsonValueKind.Null ? new(0, Gh3ScriptRuntime.Key(name)) : new(Key(name),
                    value.ValueKind == JsonValueKind.Array ? new(5, -1, value.EnumerateArray().Select(item => item.GetSingle()).ToArray()) : Gh3ScriptRuntime.Number(value.GetInt32())));
            }
            runtime.Start(Key("CreateParticleSystem"), Params(("name", Gh3ScriptRuntime.Key("lod")), ("params_script", Gh3ScriptRuntime.Structure(source))));
            var lod = state.Emitters[Key("lod")].Lod;
            Check(lod.Distances == new Vector2(row.GetProperty("distances")[0].GetSingle(), row.GetProperty("distances")[1].GetSingle()) &&
                lod.Minimum == row.GetProperty("minimum").GetInt32(), "Native particle source LOD fields or field order differ");
        }
        foreach (var row in oracle.RootElement.GetProperty("controls").EnumerateArray())
        {
            var state = new StageParticleLod();
            state.Set(Gh3ScriptRuntime.Structure(row.GetProperty("parameters").EnumerateObject().Select(field =>
                new Gh3QbField(Gh3Checksum.Value(field.Name), Gh3ScriptRuntime.Number(field.Value.GetDouble())))));
            Check(state.Minimum == unchecked((int)row.GetProperty("minimum").GetUInt32()) && state.Distances.X == Number(row, "distance1") && state.Distances.Y == Number(row, "distance2"),
                "Native particle LOD command differs");
        }
        foreach (var row in oracle.RootElement.GetProperty("rows").EnumerateArray())
        {
            var state = new StageParticleLod();
            var delay = Number(row, "delay");
            var visibility = new StageParticleVisibility();
            var time = 0f;
            var rate = 10f;
            var stopped = false;
            var frame = 0;
            foreach (var step in row.GetProperty("steps").EnumerateArray())
            {
                var duration = Number(step, "duration");
                float? elapsed = null;
                visibility.Pending |= step.GetProperty("visible").GetBoolean();
                if (delay > 0) delay -= duration;
                else
                {
                    elapsed = state.Advance(duration, row.GetProperty("camera").GetBoolean() ? Number(step, "distance") : null, visibility.Consume(),
                        row.GetProperty("enabled").GetBoolean(), time, Number(row, "maximumLife"), Number(row, "lifetime"), stopped, false, row.GetProperty("locked").GetBoolean());
                    if (Number(row, "lifetime") > 0 && time > Number(row, "lifetime") && rate != 0) { rate = 0; stopped = true; }
                    if (elapsed is { } dt) time += dt;
                }
                var expected = step.GetProperty("updated");
                Check(visibility.Pending == step.GetProperty("pendingVisibility").GetBoolean() && visibility.Previous == step.GetProperty("previousVisibility").GetBoolean(), "Particle render visibility latch differs");
                Check((expected.ValueKind == JsonValueKind.Null ? elapsed == null : elapsed is { } actual && Math.Abs(actual - expected.GetSingle()) < .000001f) &&
                    state.Counter == step.GetProperty("counter").GetInt32() && Math.Abs(state.Scale - Number(step, "scale")) < .000001f &&
                    Math.Abs(state.Pending - Number(step, "pending")) < .000001f && Math.Abs(time - Number(step, "time")) < .000001f &&
                    Math.Abs(delay - Number(step, "delay")) < .000001f && rate == Number(step, "rate") && stopped == step.GetProperty("stopped").GetBoolean() &&
                    state.Culled == step.GetProperty("culled").GetBoolean(), $"Native particle LOD clock differs at frame {frame}: {row.GetProperty("visibility")}, camera={row.GetProperty("camera")}, enabled={row.GetProperty("enabled")}");
                frame++;
            }
        }
        foreach (var row in oracle.RootElement.GetProperty("systems").EnumerateArray()) CompareSystem(bank, row);
        if (Environment.GetEnvironmentVariable("GH3_NATIVE_PARTICLE_VISIBILITY_ORACLE") is { } visibilityPath)
        {
            using var visibilityOracle = JsonDocument.Parse(File.ReadAllText(visibilityPath));
            foreach (var row in visibilityOracle.RootElement.GetProperty("cases").EnumerateArray())
            {
                var state = new StageParticleVisibility();
                var center = row.GetProperty("center");
                state.Register(new(center[0].GetSingle(), center[1].GetSingle(), center[2].GetSingle()), Number(row, "radius"));
                var expected = row.GetProperty("registered");
                Check(state.Center == new Vector3(expected[0].GetSingle(), expected[1].GetSingle(), expected[2].GetSingle()) / 32 &&
                    state.Radius == expected[3].GetSingle() / 32, "Native particle spatial registration differs");
                var planes = row.GetProperty("planes").EnumerateArray().Select(plane => new Vector4(plane[0].GetSingle(), plane[1].GetSingle(), plane[2].GetSingle(), plane[3].GetSingle())).ToArray();
                Check(state.Test(planes) == row.GetProperty("visible").GetBoolean(), "Native particle frustum test differs: " + row);
                var camera = row.GetProperty("camera");
                var position = new Vector3(camera[0].GetSingle(), camera[1].GetSingle(), camera[2].GetSingle());
                var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, Number(row, "yaw"));
                var generated = StageParticleVisibility.Planes(new(position, position + Vector3.Transform(Vector3.UnitZ, rotation), 90, "visibility") { Rotation = rotation }, 1, .25f, 20);
                Check(generated.Zip(planes).All(pair => Vector4.Distance(pair.First, pair.Second) < .00001f), "Particle camera frustum differs from native test planes");
            }
            Console.WriteLine("Native particle sphere registration and frustum boundary checks passed.");
        }
        Console.WriteLine("Native particle LOD commands, distance bands, visibility clocks, accumulated motion, and random states passed.");
    }

    private static void CompareSystem(StageScriptBank bank, JsonElement row)
    {
        StageParticleState? state = null;
        var parent = row.GetProperty("locked").GetBoolean() ? Key("lod_parent") : 0;
        var id = unchecked(Key("lod") + parent);
        var frame = 0;
        var updates = new List<(int Frame, int Particles)>();
        using var runtime = new Gh3ScriptRuntime(bank.Values, (context, command, parameters) =>
        {
            if (command != Key("mark_update")) return state!.Execute(context, command, parameters);
            updates.Add((frame, state!.Particles.Count));
            return new(true);
        });
        state = new(bank, runtime, value => parent != 0 && value == parent, (_, _, _) => new StageObjectPose(Vector3.Zero, Quaternion.Identity, true), _ => { });
        runtime.Set(Key("update_particle"), ScriptLogicTests.Script((byte)0x20, (byte)1, "mark_update", (byte)1,
            "Wait", 1, "frame", (byte)1, (byte)0x21, (byte)1, (byte)0x24));
        runtime.Start(Key("SetRandomSeeds"), Gh3ScriptRuntime.Structure(new[] { 12345, 0x12b9b0a1, 0x0aa2fb3f, 12345, 0x12b9b0a1, 0x0aa2fb3f }
            .Select((value, index) => new Gh3QbField(Key("seed" + (index + 1)), Gh3ScriptRuntime.Number(value)))));
        var definition = Params(("QuickMaterial", Params(("DiffuseTexture", Gh3ScriptRuntime.Key("texture")))),
            ("Max", Gh3ScriptRuntime.Number(64)), ("Emit_Rate", Gh3ScriptRuntime.Number(120)),
            ("UpdateScript", Gh3ScriptRuntime.Key("update_particle")),
            ("EmitDelayStart", Gh3ScriptRuntime.Number(Number(row, "delay"))), ("Lifetime", Gh3ScriptRuntime.Number(Number(row, "lifetime"))),
            ("TimeSeed", Gh3ScriptRuntime.Number(Number(row, "timeSeed"))), ("LifeRange", new(5, -1, new[] { .05f, .13f })), ("SpeedRange", new(5, -1, new[] { 2f, 5f })),
            ("SizeRange", new(12, -1, new[] { new Gh3QbValue(5, -1, new[] { 1f, 2f }), new Gh3QbValue(5, -1, new[] { .25f, .5f }) })),
            ("FollowEmitter", Gh3ScriptRuntime.Number(0)), ("Pos", new(6, -1, new[] { 1f, 2f, 3f })), ("Force", new(6, -1, new[] { 1f, -2f, 3f })), ("EmitRangeDims", new(6, -1, new[] { 2f, 3f, 4f })));
        if (row.GetProperty("fillPath").GetBoolean()) definition = Gh3ScriptRuntime.Structure(definition.Fields.Append(new Gh3QbField(0, Gh3ScriptRuntime.Key("FillPath"))));
        runtime.Start(Key("CreateParticleSystem"), Params(("name", Gh3ScriptRuntime.Key("lod")), ("params_script", definition), ("ObjID", new(13, -1, parent))));
        runtime.Start(Key("SetAngleSpread"), Params(("elevation", new(5, -1, new[] { -.125f, .25f })), ("sweep", new(5, -1, new[] { 0f, 2f }))), id);
        foreach (var sample in row.GetProperty("samples").EnumerateArray())
        {
            frame = sample.GetProperty("frame").GetInt32();
            if (state.Exists(id))
            {
                runtime.Start(Key("SetPos"), Params(("pos", new(6, -1, sample.GetProperty("position").EnumerateArray().Select(value => value.GetSingle()).ToArray()))), id);
                runtime.Start(Key("SetLODParams"), Params(("min_particles", Gh3ScriptRuntime.Number(sample.GetProperty("minimumParticles").GetInt32())),
                    ("lod_dist1", Gh3ScriptRuntime.Number(5)), ("lod_dist2", Gh3ScriptRuntime.Number(20))), id);
            }
            if (frame == 30 && state.Exists(id)) runtime.Start(Key("SetParticleSize"), Params(("sw", Gh3ScriptRuntime.Number(.3f)), ("sh", Gh3ScriptRuntime.Number(.1f)), ("ew", Gh3ScriptRuntime.Number(3)), ("eh", Gh3ScriptRuntime.Number(2))), id);
            state.View = (_, _, _) => (Number(sample, "distance"), sample.GetProperty("visible").GetBoolean());
            runtime.Start(Key("EngineConfig"), Params(("ParticleLOD", Gh3ScriptRuntime.Number(sample.GetProperty("enabled").GetBoolean() ? 1 : 0)),
                ("ParticleSystem", Gh3ScriptRuntime.Number(sample.GetProperty("active").GetBoolean() ? 1 : 0))));
            runtime.Advance((frame + 1) / 60d);
            var context = $"frame={frame}, mode={row.GetProperty("mode")}, visibility={row.GetProperty("visibility")}, seed={row.GetProperty("timeSeed")}, lifetime={row.GetProperty("lifetime")}, fillPath={row.GetProperty("fillPath")}";
            Check(state.Exists(id) == sample.GetProperty("alive").GetBoolean(), "LOD particle cleanup differs: " + context);
            if (state.Exists(id))
            {
                var emitter = state.Emitters[id];
                Check(emitter.Visibility.Pending == sample.GetProperty("pendingVisibility").GetBoolean() && emitter.Visibility.Previous == sample.GetProperty("previousVisibility").GetBoolean(), "Particle render visibility latch differs: " + context);
                Check(emitter.Emission.Time == Number(sample, "time") && emitter.DelayRemaining == Number(sample, "delay") && emitter.Rate == Number(sample, "rate"), "LOD emission clock differs: " + context);
                Check(emitter.Lod.Counter == sample.GetProperty("counter").GetInt32() && Math.Abs(emitter.Lod.Scale - Number(sample, "scale")) < .000001f &&
                    emitter.Lod.Pending == Number(sample, "pending"), "LOD component clock differs: " + context);
                var minimum = Vector(sample, "minimum");
                var maximum = Vector(sample, "maximum");
                Check(Vector3.Distance(emitter.Bounds.Center, Vector(sample, "center")) < .00001f && Math.Abs(emitter.Bounds.Radius - Number(sample, "radius")) < .00001f &&
                    Vector3.Distance(emitter.Bounds.Minimum, new(minimum.X, minimum.Y, maximum.Z)) < .00001f &&
                    Vector3.Distance(emitter.Bounds.Maximum, new(maximum.X, maximum.Y, minimum.Z)) < .00001f, "LOD particle bounds differ: " + context);
            }
            var expected = sample.GetProperty("particles").EnumerateArray().ToArray();
            Check(state.Particles.Count == expected.Length, $"LOD particle count differs: {context}; {state.Particles.Count} != {expected.Length}");
            for (var index = 0; index < expected.Length; index++)
            {
                var particle = state.Particles[index];
                var actual = expected[index];
                Check(Vector3.Distance(particle.Motion.Position, Vector(actual, "position")) < .00001f && Vector3.Distance(particle.Motion.Velocity, Vector(actual, "velocity")) < .00001f &&
                    particle.Age == Number(actual, "age") && particle.Life == Number(actual, "life"), $"LOD particle motion differs: {context}, particle={index}");
            }
        }
        Check(updates.SequenceEqual(row.GetProperty("updates").EnumerateArray().Select(value => (value.GetProperty("frame").GetInt32(), value.GetProperty("particles").GetInt32()))), "LOD update-script scheduling differs");
        var seeds = row.GetProperty("seeds").EnumerateArray().Select(value => unchecked((int)value.GetUInt32())).ToArray();
        var random = new Gh3Random(seeds[0]) { Seed2 = seeds[1], Seed3 = seeds[2] };
        Check(runtime.NextRandom(10000) == random.Next(10000), "LOD emission consumed different random draws");
        var crt = new Gh3CrtRandom { State = row.GetProperty("crtState").GetUInt32() };
        Check(runtime.NextCrtRandom() == crt.Next(), "LOD prewarm consumed different CRT random draws");
    }

    private static Vector3 Vector(JsonElement row, string field) => new(row.GetProperty(field)[0].GetSingle(), row.GetProperty(field)[1].GetSingle(), -row.GetProperty(field)[2].GetSingle());
    private static float Number(JsonElement row, string field) => row.GetProperty(field).GetSingle();
    private static uint Key(string value) => Gh3Checksum.Value(value);
    private static Gh3QbValue Params(params (string Name, Gh3QbValue Value)[] fields) => Gh3ScriptRuntime.Structure(fields.Select(field => new Gh3QbField(Key(field.Name), field.Value)));
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
