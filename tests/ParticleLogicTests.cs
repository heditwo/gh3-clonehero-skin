using System.Numerics;
using System.Text.Json;
using Gh3Skin;

internal static class ParticleLogicTests
{
    private static uint Key(string text) => Gh3Checksum.Value(text);
    private static Gh3QbValue Params(params (string Name, Gh3QbValue Value)[] fields) => Gh3ScriptRuntime.Structure(fields.Select(field => new Gh3QbField(Key(field.Name), field.Value)));
    private static Gh3QbValue Number(double value) => Gh3ScriptRuntime.Number(value);
    private static Gh3QbValue Name(string value) => Gh3ScriptRuntime.Key(value);
    private static Gh3QbValue Pair(float a, float b) => new(5, -1, new[] { a, b });
    private static Gh3QbValue Vector(float x, float y, float z) => new(6, -1, new[] { x, y, z });
    private static Gh3QbValue Definition() => Params(("QuickMaterial", Params(("DiffuseTexture", Name("test_texture")), ("BlendMode", Name("add")))),
        ("Max", Number(0)), ("AngleSpread", Number(0)), ("FollowEmitter", Number(0)),
        ("Emit_Rate", Number(10)), ("LifeRange", Pair(1, 1)), ("SpeedRange", Pair(2, 2)), ("Emit_Target", Vector(0, 1, 0)), ("Force", Vector(0, -4, 0)));

    public static void Run()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"version\":1,\"definitions\":{}}");
            var bank = StageScriptBank.Load(path);
            var gaps = new HashSet<string>();
            var parent = new StageObjectPose(new(10, 2, 3), Quaternion.Identity, true) { Scale = new(2) };
            StageParticleState? state = null;
            using var runtime = new Gh3ScriptRuntime(bank.Values, (context, command, parameters) => state!.Execute(context, command, parameters));
            state = new(bank, runtime, id => id == Key("parent"), (_, _, time) => parent with { Position = parent.Position + Vector3.UnitX * (float)time }, gap => gaps.Add(gap));
            Gh3ScriptCallResult Invoke(string command, Gh3QbValue parameters, uint target = 0) => state.Execute(new(Key(command), target, parameters.Fields), Key(command), parameters) ?? throw new Exception("Missing particle function");
            var positioned = Params(("name", Name("fire")), ("params_script", Definition()), ("pos", Vector(1, 2, 3)));
            Check(Invoke("CreateParticleSystem", positioned).Success, "A source emitter can be created.");
            Check(!Invoke("CreateParticleSystem", positioned).Success, "Duplicate particle IDs fail without replacing an emitter.");
            runtime.Cancel();
            runtime.Advance(.5);
            Check(state.Particles.Count == 5, "Emission uses the source rate independently of renderer updates.");
            var first = state.Sample(.5).First();
            Check(Vector3.Distance(first.Position, new(1, 2.4722222f, -3)) < .00001, "Particle position, velocity, and force use source coordinates and native frame integration.");
            Invoke("EmitRate", Params(("rate", Number(0))), Key("fire"));
            Invoke("Destroy", Params(("ifEmpty", Number(1))), Key("fire"));
            runtime.Advance(.9);
            Check(state.Exists(Key("fire")) && state.Particles.Count == 5, "Destroy-if-empty retains existing particles after rate zero.");
            runtime.Advance(1.6);
            Check(!state.Exists(Key("fire")) && state.Particles.Count == 0, "Destroy-if-empty removes its object at the final particle death.");
            var attached = Params(("name", Name("attached")), ("ObjID", Name("parent")), ("bone", Name("jaw")), ("params_script", Definition()));
            Invoke("CreateParticleSystem", attached);
            var attachedId = unchecked(Key("attached") + Key("parent"));
            Check(state.Exists(attachedId), "Attached particle IDs use the native checksum sum.");
            runtime.Advance(1.8);
            Check(Math.Abs(state.Particles[0].Origin.X - 11.7f) < .00001, "Attachment transforms are sampled at each source emission time.");
            var unique = Invoke("GetUniqueCompositeObjectID", Params(("preferredID", new(13, -1, attachedId)))).Values!.Single().Value.Integer;
            Check(unique != attachedId && !state.Exists(unique), "Unique particle IDs avoid live objects.");
            Invoke("Destroy", Params(), attachedId);
            Check(!state.Exists(attachedId) && state.Particles.Count == 0, "Immediate destroy removes its particles.");
            runtime.Advance(2);
            Check(state.Particles.Count == 0, "Canceled emitter callbacks cannot create more particles.");
            Check(gaps.Count == 1, "Unverified source rendering semantics stay visible in diagnostics.");
            Invoke("CreateParticleSystem", positioned);
            Invoke("EmitRate", Params(("rate", Number(0))), Key("fire"));
            Invoke("Emit", Params(), Key("fire"));
            runtime.Advance(2.25);
            var beforeForce = state.Sample(2.25).Single();
            Invoke("SetForce", Params(("force", Vector(4, 0, 0))), Key("fire"));
            Check(state.Sample(2.25).Single().Position == beforeForce.Position, "A force change must not move existing particles at the command time.");
            runtime.Advance(2.5);
            Check(Vector3.Distance(state.Sample(2.5).Single().Position, beforeForce.Position + new Vector3(.1333333f, .25f, 0)) < .00001,
                "A force change must affect existing particles and retain their current velocity.");
            Invoke("SetParticleSize", Params(("sw", Number(2)), ("sh", Number(3)), ("ew", Number(4)), ("eh", Number(5))), Key("fire"));
            Invoke("SetParticleSizeScale", Params(("scalesize", Number(.5))), Key("fire"));
            Check(Vector2.Distance(state.Sample(2.5).Single().Size, new Vector2(3, 4)) < .00001, "Flat-particle size uses the endpoints; the native vertex path does not apply SizeScale.");
            Invoke("SetLife", Params(("min", Number(3))), Key("fire"));
            Invoke("SetSpeedRange", Params(("min", Number(7))), Key("fire"));
            Invoke("SetPos", Params(("pos", Vector(8, 9, 10))), Key("fire"));
            Invoke("Emit", Params(), Key("fire"));
            Check(state.Particles[0].Life == 1 && state.Particles[1].Life == 3 && state.Particles[1].Velocity == new Vector3(0, 7, 0) && state.Particles[1].Origin == new Vector3(8, 9, -10),
                "Life, speed, and position controls must configure future births without replacing live particles.");
            Invoke("Destroy", Params(), Key("fire"));
            var paced = Gh3ScriptRuntime.Structure(Definition().Fields.Where(field => field.Name != Key("Emit_Rate") && field.Name != Key("LifeRange")).Concat(
                Params(("Emit_Rate", Number(72)), ("LifeRange", Pair(10, 10))).Fields));
            Invoke("CreateParticleSystem", Params(("name", Name("paced")), ("params_script", paced)));
            runtime.Advance(3);
            Check(state.Particles.Count == 30, "Native emission resets its accumulator after a batch; rate 72 at 60 Hz must emit 30 particles in half a second.");
            var held = state.Sample(3).ToArray();
            runtime.Advance(3);
            Check(held.SequenceEqual(state.Sample(3)), "Paused emission must retain all particle samples.");
            Invoke("EmitRate", Params(("rate", Number(0))), Key("paced"));
            runtime.Advance(3.25);
            Check(state.Particles.Count == 30, "Rate zero must stop future births.");
            Invoke("EmitRate", Params(("rate", Number(10))), Key("paced"));
            runtime.Advance(3.5);
            Check(state.Particles.Count == 32, "An emission-rate change must use the existing fixed-frame clock.");
            NativeControls(bank);
            ParticleShapeTests.Run();
            ParticleLifecycleTests.Run(bank);
            ParticleGeometryTests.Run();
            ParticlePrewarmTests.Run(bank);
            ParticleRibbonTests.Run(bank);
        }
        finally { File.Delete(path); }
        SourceEvents();
        Console.WriteLine("Particle controls, emission, colors, motion, attachment poses, and cleanup checks passed.");
    }

    private static void NativeControls(StageScriptBank bank)
    {
        if (Environment.GetEnvironmentVariable("GH3_NATIVE_PARTICLE_ORACLE") is not { } path) return;
        using var oracle = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var row in oracle.RootElement.GetProperty("bounceFields").EnumerateArray())
        {
            var definition = new StageParticleDefinition(Gh3ScriptRuntime.Structure(Definition().Fields.Concat(Params(("BounceHeight", Number(row.GetProperty("height").GetDouble()))).Fields)));
            Check(definition.Bounce == row.GetProperty("enabled").GetBoolean() && definition.BounceHeight == row.GetProperty("height").GetSingle(),
                "The presence of BounceHeight, including zero, must enable native ground collision.");
        }
        foreach (var row in oracle.RootElement.GetProperty("motion").EnumerateArray())
        {
            Vector3 Vector(JsonElement record, string name)
            {
                var values = record.GetProperty(name).EnumerateArray().Select(value => value.GetSingle()).ToArray();
                return new(values[0], values[1], -values[2]);
            }
            var motion = new StageParticleMotion(Vector(row, "position"), Vector(row, "velocity"));
            foreach (var sample in row.GetProperty("samples").EnumerateArray())
            {
                motion = motion.Step(sample.GetProperty("dt").GetSingle(), Vector(sample, "force"), row.GetProperty("bounce").GetBoolean(),
                    row.GetProperty("height").GetSingle(), row.GetProperty("coefficient").GetSingle());
                Check(Vector3.Distance(motion.Position, Vector(sample, "position")) < .00001 && Vector3.Distance(motion.Velocity, Vector(sample, "velocity")) < .00001,
                    "Particle force integration, ground clamp, or rebound differs from native.");
            }
        }
        foreach (var row in oracle.RootElement.GetProperty("emission").EnumerateArray())
        {
            var emission = new StageParticleEmission(Key(row.GetProperty("function").GetString()!), row.GetProperty("period").GetSingle());
            foreach (var sample in row.GetProperty("samples").EnumerateArray())
            {
                var count = emission.Step(sample.GetProperty("dt").GetSingle(), sample.GetProperty("rate").GetSingle());
                Check(count == sample.GetProperty("count").GetInt32() && emission.Time == sample.GetProperty("time").GetSingle() &&
                    emission.Accumulator == sample.GetProperty("accumulator").GetSingle(), "Particle emission pacing differs from native: " + row.GetProperty("function"));
            }
        }
        foreach (var row in oracle.RootElement.GetProperty("colors").EnumerateArray().Concat(oracle.RootElement.GetProperty("colorFrames").EnumerateArray()))
        {
            var source = Gh3ScriptRuntime.Structure(Definition().Fields.Concat(Params(
                ("Color", new(1, -1, row.GetProperty("color").GetUInt32())),
                ("ParticleColor", new(12, -1, row.GetProperty("ranges").EnumerateArray().Select(value => new Gh3QbValue(1, -1, value.GetUInt32())).ToArray())),
                ("VertexWeight", new(12, -1, row.GetProperty("weights").EnumerateArray().Select(value => Number(value.GetDouble())).ToArray()))).Fields));
            var definition = new StageParticleDefinition(source);
            var random = row.GetProperty("random").GetSingle();
            if (row.TryGetProperty("samples", out var samples))
            {
                var color = new StageParticleColor(0, 0, definition.ColorAt(0, random));
                foreach (var sample in samples.EnumerateArray())
                {
                    color = color.Step(definition, sample.GetProperty("dt").GetSingle(), row.GetProperty("life").GetSingle(), random);
                    Check(color.Segment == sample.GetProperty("segment").GetInt32() && color.Phase == sample.GetProperty("phase").GetSingle(),
                        "Native particle color segment timing or replacement differs.");
                    CheckColor(color.Value, sample);
                }
            }
            else CheckColor(definition.ColorAt(row.GetProperty("phase").GetSingle(), random), row);
            void CheckColor(Vector4 color, JsonElement sample)
            {
                var actual = new[] { color.X, color.Y, color.Z, color.W }.Select(value => (int)MathF.Round(value * 255));
                Check(actual.SequenceEqual(sample.GetProperty("rgba").EnumerateArray().Select(value => value.GetInt32())),
                    "Particle random color ranges, source keyframe weights, or byte conversion differ from native.");
            }
        }
        foreach (var row in oracle.RootElement.GetProperty("bursts").EnumerateArray())
        {
            using var runtime = new Gh3ScriptRuntime(bank.Values, (_, _, _) => null);
            var state = new StageParticleState(bank, runtime, _ => false, (_, _, _) => null, _ => { });
            var id = Key("burst");
            var source = Gh3ScriptRuntime.Structure(Definition().Fields.Where(field => field.Name != Key("Emit_Rate")).Concat(Params(
                ("Emit_Rate", Number(72)), ("EmitNum", Number(row.GetProperty("count").GetInt32())), ("Max", Number(row.GetProperty("maximum").GetInt32()))).Fields));
            Gh3ScriptCallResult Invoke(string name, Gh3QbValue parameters) => state.Execute(new(Key(name), id, parameters.Fields), Key(name), parameters)!;
            Invoke("CreateParticleSystem", Params(("name", Name("burst")), ("params_script", source)));
            var time = 0d;
            foreach (var sample in row.GetProperty("samples").EnumerateArray())
            {
                var count = state.Particles.Count;
                runtime.Advance(time += sample.GetProperty("dt").GetDouble());
                Check(state.Particles.Count - count == sample.GetProperty("emitted").EnumerateArray().Sum(value => value.GetInt32()), "EmitNum burst count differs from the original frame update.");
                Check(state.Emitters[id].Rate == sample.GetProperty("rate").GetSingle() && state.Emitters[id].EmissionStopped == sample.GetProperty("stopped").GetBoolean(),
                    "The original EmitNum update must stop future emissions after its first active frame.");
            }
            var emitted = state.Emitters[id].Emitted;
            Invoke("EmitRate", Params(("rate", Number(100))));
            Invoke("Emit", Params(("num", Number(3))));
            runtime.Advance(.5);
            Check(state.Emitters[id].Emitted == emitted, "A completed burst must not restart from Emit or EmitRate.");
            Invoke("EmitRate", Params(("rate", Number(0))));
            runtime.Advance(2);
            Check(!state.Exists(id), "A one-shot emitter must be removed after its particles drain.");
        }
        foreach (var row in oracle.RootElement.GetProperty("sizes").EnumerateArray())
        {
            var size = new Gh3QbValue(12, -1, row.GetProperty("source").EnumerateArray().Select(value => value.ValueKind == JsonValueKind.Array
                ? new Gh3QbValue(5, -1, value.EnumerateArray().Select(v => v.GetSingle()).ToArray()) : Number(value.GetDouble())).ToArray());
            var definition = new StageParticleDefinition(Gh3ScriptRuntime.Structure(Definition().Fields.Concat(Params(("SizeRange", size)).Fields)));
            var start = row.GetProperty("start").EnumerateArray().Select(value => value.GetSingle()).ToArray();
            var end = row.GetProperty("end").EnumerateArray().Select(value => value.GetSingle()).ToArray();
            Check(definition.Width == new Vector2(start[0], end[0]) && definition.Height == new Vector2(start[1], end[1]),
                "Particle size endpoints differ from the original QB parser.");
        }
        foreach (var row in oracle.RootElement.GetProperty("records").EnumerateArray())
        {
            var before = row.GetProperty("before");
            float[] Values(JsonElement value, string name) => value.GetProperty(name).EnumerateArray().Select(value => value.GetSingle()).ToArray();
            var ages = Values(before, "ages");
            using var runtime = new Gh3ScriptRuntime(bank.Values, (_, _, _) => null);
            var state = new StageParticleState(bank, runtime, _ => false, (_, _, _) => null, _ => { });
            var id = Key("native");
            Gh3ScriptCallResult Invoke(string name, Gh3QbValue parameters) => state.Execute(new(Key(name), id, parameters.Fields), Key(name), parameters)!;
            Invoke("CreateParticleSystem", Params(("name", Name("native")), ("params_script", Definition())));
            Invoke("EmitRate", Params(("rate", Number(0))));
            Invoke("SetLife", Params(("min", Number(10))));
            foreach (var age in ages.OrderByDescending(value => value))
            {
                runtime.Advance(1 - age);
                Invoke("Emit", Params());
            }
            runtime.Advance(1);
            for (var index = 0; index < ages.Length; index++) state.Particles[index].Age = ages[index];
            var position = Values(before, "position");
            Invoke("SetPos", Params(("pos", Vector(position[0], position[1], position[2]))));
            var force = Values(before, "force");
            Invoke("SetForce", Params(("force", Vector(force[0], force[1], force[2]))));
            var range = Values(before, "range");
            Invoke("SetEmitRange", Params(("width", Number(range[0])), ("height", Number(range[1])), ("depth", Number(range[2]))));
            foreach (var (command, name) in new[] { ("SetSpeedRange", "speed"), ("SetLife", "life") })
            {
                var values = Values(before, name);
                Invoke(command, Params(("min", Number(values[0])), ("max", Number(values[1]))));
            }
            var start = Values(before, "sizeStart");
            var end = Values(before, "sizeEnd");
            var depth = Values(before, "depth");
            Invoke("SetParticleSize", Params(("sw", Number(start[0])), ("sh", Number(start[1])), ("ew", Number(end[0])), ("eh", Number(end[1])), ("sd", Number(depth[0])), ("ed", Number(depth[1]))));
            Invoke("SetParticleSizeScale", Params(("scalesize", Number(before.GetProperty("scale").GetSingle()))));
            Invoke("EmitRate", Params(("rate", Number(before.GetProperty("rate").GetSingle()))));
            var emitted = state.Emitters[id].Emitted;
            var call = row.GetProperty("command").GetString()!;
            var parameters = Gh3ScriptRuntime.Structure(row.GetProperty("parameters").EnumerateObject().Select(field => new Gh3QbField(Key(field.Name),
                field.Value.ValueKind == JsonValueKind.Array ? new(6, -1, field.Value.EnumerateArray().Select(value => value.GetSingle()).ToArray()) : Number(field.Value.GetDouble()))));
            Check(Invoke(call, parameters).Success, "The source particle control must return true: " + call);
            var emitter = state.Emitters[id];
            var after = row.GetProperty("after");
            CheckVector(new[] { emitter.Placement.Position.X, emitter.Placement.Position.Y, -emitter.Placement.Position.Z }, Values(after, "position"));
            CheckVector(new[] { emitter.Force.X, emitter.Force.Y, -emitter.Force.Z }, Values(after, "force"));
            CheckVector(new[] { emitter.Range.X, emitter.Range.Y, emitter.Range.Z }, Values(after, "range"));
            CheckVector(new[] { emitter.Speed.X, emitter.Speed.Y }, Values(after, "speed"));
            CheckVector(new[] { emitter.Life.X, emitter.Life.Y }, Values(after, "life"));
            CheckVector(new[] { emitter.Width.X, emitter.Height.X }, Values(after, "sizeStart"));
            CheckVector(new[] { emitter.Width.Y, emitter.Height.Y }, Values(after, "sizeEnd"));
            CheckVector(new[] { emitter.Depth.X, emitter.Depth.Y }, Values(after, "depth"));
            Check(emitter.SizeScale == after.GetProperty("scale").GetSingle() && emitter.Rate == after.GetProperty("rate").GetSingle(), "Particle scale or rate differs from native: " + call);
            if (call == "Emit") Check(emitter.Emitted - emitted == row.GetProperty("emitted").EnumerateArray().Sum(value => value.GetInt32()), "Explicit particle count differs from native.");
            if (call is "KillOldParticles" or "EmptySystem") CheckVector(state.Particles.Select(particle => particle.Age).ToArray(), Values(after, "ages"));
            void CheckVector(float[] actual, float[] expected) => Check(actual.Length == expected.Length && actual.Zip(expected).All(pair => Math.Abs(pair.First - pair.Second) < .00001), "Particle control differs from native: " + call);
        }
    }

    private static void SourceEvents()
    {
        if (Environment.GetEnvironmentVariable("GH3_PACK_ASSETS") is not { } root) return;
        var directory = Path.Combine(root, "stages", "gh3.artdeco");
        var bank = StageScriptBank.Load(Path.Combine(directory, "scripts.json"), Path.Combine(root, "libraries", "gh3-band-v1", "scripts.json"));
        bank.Startup = Array.Empty<string>();
        bank.PerformanceChanged = "";
        var actor = StageActorBank.Load(Path.Combine(directory, "actors.json")).Actors.Single(actor => Gh3QbText.Key(actor.Object) == Key("Z_ArtDeco_TRG_Geo_Dragon"));
        using var reader = new BinaryReader(File.OpenRead(Path.Combine(directory, "actors", actor.Id, "animation.bin")));
        var animation = new BandAnimation(reader);
        var id = Gh3QbText.Key(actor.Object);
        var placements = new Dictionary<string, StageObjectPlacement> { [id.ToString("x8")] = new() { Position = actor.Transform.Position, Rotation = actor.Transform.Rotation } };
        var actors = new Dictionary<uint, StageScriptActorDefinition> { [id] = new(actor, animation.Clips.Select(clip => (double)clip.Duration).ToArray(), animation,
            animation.LoadBoneNames(Path.Combine(directory, "actors", actor.Id, "bones.json"))) };
        StageCue Call(double time, string name) => new(time, "gh3-script", new Gh3ScriptEvent("performance", Params(("time", Number(time * 1000)), ("scr", Name(name)))).Encode());
        using var state = new StageScriptState(bank, placements, new(new[] { Call(0, "z_artdeco_animevent_dragon_start"), Call(1, "z_artdeco_animevent_dragon_stop") }, "dragon particles"), "gh3.artdeco", actors: actors);
        state.Update(.5);
        Check(state.Particles.Emitters.Count == 3 && state.Particles.Particles.Count > 0, "The original dragon event creates its three source emitters.");
        var samples = state.Particles.Sample(.5).Select(sample => (sample.Emitter, sample.Position, sample.Color, sample.Frame)).ToArray();
        state.Update(5);
        Check(state.Particles.Emitters.Count == 0 && state.Particles.Particles.Count == 0, "The original dragon stop script stops and drains all three emitters.");
        state.Update(.5);
        Check(samples.SequenceEqual(state.Particles.Sample(.5).Select(sample => (sample.Emitter, sample.Position, sample.Color, sample.Frame))), "Backward seek rebuilds source particle emissions and bone attachments.");
        Check(state.Diagnostics.All(message => message.StartsWith("Particle visibility,")), string.Join("; ", state.Diagnostics));
        var butterfly = Key("butterfly");
        var create = new StageCue(0, "gh3-script", new Gh3ScriptEvent("performance", Params(("time", Number(0)), ("scr", Name("CreateParticleSystem")), ("params", Params(
            ("name", new(13, -1, butterfly)), ("params_script", Name("GP_StarPower_Butterflies01")), ("pos", Vector(0, 1, 0)))))).Encode());
        var destroy = new StageCue(2, "gh3-script", new Gh3ScriptEvent("performance", Params(("time", Number(2000)), ("scr", Name("Destroy")), ("params", Params(("name", new(13, -1, butterfly)))))).Encode());
        using var butterflies = new StageScriptState(bank, new Dictionary<string, StageObjectPlacement>(), new(new[] { create, destroy }, "butterfly script"), "gh3.artdeco");
        var widths = new List<Vector2>();
        for (var frame = 1; frame < 60; frame++)
        {
            butterflies.Update(frame / 60d);
            widths.Add(butterflies.Particles.Emitters[butterfly].Width);
        }
        Check(widths.Distinct().Count() > 10, "The source butterfly update script must change wing size.");
        var held = butterflies.Particles.Emitters[butterfly].Width;
        butterflies.Update(59 / 60d);
        Check(butterflies.Particles.Emitters[butterfly].Width == held, "A paused particle update script must retain its state.");
        butterflies.Update(3);
        Check(!butterflies.Particles.Exists(butterfly), "Particle removal must stop its update script.");
        butterflies.Update(59 / 60d);
        Check(butterflies.Particles.Emitters[butterfly].Width == held, "Seeking must restore the source particle script and random phase.");
        Check(butterflies.Diagnostics.All(message => message.StartsWith("Particle visibility,")), string.Join("; ", butterflies.Diagnostics));
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
