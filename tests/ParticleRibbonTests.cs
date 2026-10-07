using System.Numerics;
using System.Text.Json;
using Gh3Skin;

internal static class ParticleRibbonTests
{
    public static void Run(StageScriptBank bank)
    {
        if (Environment.GetEnvironmentVariable("GH3_NATIVE_PARTICLE_RIBBON_ORACLE") is not { } path) return;
        using var oracle = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var row in oracle.RootElement.GetProperty("rows").EnumerateArray())
        {
            var layout = row.GetProperty("layout").GetString();
            var columns = layout is "Layout2x2" or "Layout1x2" ? 2 : 1;
            var rows = layout is "Layout2x2" or "Layout2x1" ? 2 : 1;
            var age = row.GetProperty("age").GetSingle();
            var start = row.GetProperty("startWidth").GetSingle();
            var width = start + (row.GetProperty("endWidth").GetSingle() - start) * (age / row.GetProperty("life").GetSingle());
            var history = row.GetProperty("history").EnumerateArray().Select(Vector4).ToArray();
            var flat = row.GetProperty("type").GetString() == "FlatRibbonTrail";
            var alignTo = Vector3(row, "alignTo");
            var normal = alignTo == System.Numerics.Vector3.Zero ? System.Numerics.Vector3.Cross(Vector3(row, "right"), Vector3(row, "up")) : StageParticleGeometry.Alignment(alignTo).Depth;
            var quads = (flat ? StageParticleGeometry.FlatRibbon(Vector3(row, "position"), width, history, row.GetProperty("head").GetInt32(), Vector3(row, "right"), Vector3(row, "up"), alignTo) :
                StageParticleGeometry.Ribbon(Vector3(row, "position"), width, history, row.GetProperty("head").GetInt32(), Vector3(row, "right"), Vector3(row, "up"),
                columns, rows, row.GetProperty("frame").GetInt32() + (int)(age * row.GetProperty("fps").GetSingle()), alignTo)).ToArray();
            var vertices = row.GetProperty("vertices").EnumerateArray().ToArray();
            Check(quads.Length * 6 == vertices.Length, "Native ribbon vertex count differs");
            for (var index = 0; index < quads.Length; index++)
            {
                var quad = quads[index];
                var positions = new[] { quad.A, quad.B, quad.C, quad.A, quad.C, quad.D };
                var uv = new[] { new Vector2(quad.Uv.Z, quad.Uv.Y), new Vector2(quad.Uv.X, quad.Uv.Y), new Vector2(quad.Uv.X, quad.Uv.W),
                    new Vector2(quad.Uv.Z, quad.Uv.Y), new Vector2(quad.Uv.X, quad.Uv.W), new Vector2(quad.Uv.Z, quad.Uv.W) };
                for (var vertex = 0; vertex < 6; vertex++)
                {
                    var expected = vertices[index * 6 + vertex];
                    if (expected.TryGetProperty("tangentFrame", out var tangent))
                    {
                        var frame = tangent.EnumerateArray().Select(value => value.GetSingle()).ToArray();
                        Check(System.Numerics.Vector3.Distance(normal, new(frame[8], frame[9], frame[10])) < .00001f, "Native ribbon normal differs");
                        Check(System.Numerics.Vector3.Distance(quad.Lighting.Right, new(frame[0], frame[1], frame[2])) < .00001f &&
                            System.Numerics.Vector3.Distance(quad.Lighting.Up, new(frame[4], frame[5], frame[6])) < .00001f, "Native ribbon tangent frame differs");
                    }
                    Check(Math.Abs(width - expected.GetProperty("softEdgeWidth").GetSingle()) < .000001f, "Native ribbon soft-edge width differs");
                    Check(System.Numerics.Vector3.Distance(positions[vertex], Vector3(expected, "position")) < .00001f,
                        $"Native ribbon position differs: layout={layout}, head={row.GetProperty("head")}, segment={index}, vertex={vertex}");
                    var values = expected.GetProperty("uv").EnumerateArray().Select(value => value.GetSingle()).ToArray();
                    Check(System.Numerics.Vector2.Distance(uv[vertex], new(values[0], values[1])) < .000001f, "Native ribbon texture coordinates differ");
                    var weight = vertex is 0 or 1 or 3 ? quad.StartAlpha : quad.EndAlpha;
                    var alpha = StageParticleGeometry.TrailAlpha((row.GetProperty("color").GetUInt32() >> 24) / 255f, weight);
                    Check(alpha == (expected.GetProperty("color").GetUInt32() >> 24) / 255f, "Native ribbon alpha differs");
                }
            }
        }
        foreach (var row in oracle.RootElement.GetProperty("lifecycles").EnumerateArray())
        {
            using var runtime = new Gh3ScriptRuntime(bank.Values, (_, _, _) => null);
            var state = new StageParticleState(bank, runtime, _ => false, (_, _, _) => null, _ => { });
            var id = Gh3Checksum.Value("ribbon");
            Gh3ScriptCallResult Invoke(string name, Gh3QbValue parameters) => state.Execute(new(Gh3Checksum.Value(name), id, parameters.Fields), Gh3Checksum.Value(name), parameters)!;
            var definition = Params(("QuickMaterial", Params(("DiffuseTexture", Gh3ScriptRuntime.Key("texture")))),
                ("Max", Number(4)), ("Emit_Rate", Number(120)), ("LifeRange", Pair(.05f, .13f)), ("SpeedRange", Pair(2, 5)), ("History", Number(row.GetProperty("length").GetInt32())),
                ("Pos", new(6, -1, new[] { 1f, 2f, 3f })), ("Force", new(6, -1, new[] { 1f, -2f, 3f })));
            Invoke("CreateParticleSystem", Params(("name", Gh3ScriptRuntime.Key("ribbon")), ("params_script", definition)));
            Invoke("SetAngleSpread", Params(("elevation", Pair(-.125f, .25f)), ("sweep", Pair(0, 2))));
            runtime.Start(Gh3Checksum.Value("SetRandomSeeds"), Gh3ScriptRuntime.Structure(new[] { 12345, 0x12b9b0a1, 0x0aa2fb3f, 12345, 0x12b9b0a1, 0x0aa2fb3f }
                .Select((value, index) => new Gh3QbField(Gh3Checksum.Value("seed" + (index + 1)), Number(value)))));
            var tick = 0;
            foreach (var sample in row.GetProperty("samples").EnumerateArray())
            {
                var frame = sample.GetProperty("frame").GetInt32();
                if (frame == 25) Invoke("KillOldParticles", Params(("time", Number(.02))));
                if (sample.GetProperty("dt").GetSingle() > 0) tick++;
                runtime.Advance(tick / 60d);
                var expected = sample.GetProperty("history").EnumerateArray().ToArray();
                Check(state.Particles.Count == expected.Length && state.Emitters[id].HistoryIndex == sample.GetProperty("head").GetInt32(), "Native particle history cursor differs");
                for (var particle = 0; particle < expected.Length; particle++)
                {
                    var entries = expected[particle].EnumerateArray().Select(Vector4).ToArray();
                    for (var entry = 0; entry < entries.Length; entry++)
                    {
                        var value = entries[entry];
                        value.Z = -value.Z;
                        Check(System.Numerics.Vector4.Distance(state.Particles[particle].History[entry], value) < .00001f,
                            $"Native particle history differs: length={entries.Length}, frame={frame}, particle={particle}, entry={entry}: {state.Particles[particle].History[entry]}; expected {value}");
                    }
                }
            }
        }
        Console.WriteLine("Native ribbon geometry, texture coordinates, history rotation, and slot reuse checks passed.");
    }

    private static Gh3QbValue Number(double value) => Gh3ScriptRuntime.Number(value);
    private static Gh3QbValue Pair(float a, float b) => new(5, -1, new[] { a, b });
    private static Gh3QbValue Params(params (string Name, Gh3QbValue Value)[] fields) => Gh3ScriptRuntime.Structure(fields.Select(field => new Gh3QbField(Gh3Checksum.Value(field.Name), field.Value)));
    private static Vector3 Vector3(JsonElement row, string field) { var values = row.GetProperty(field).EnumerateArray().Select(value => value.GetSingle()).ToArray(); return new(values[0], values[1], values[2]); }
    private static Vector4 Vector4(JsonElement row) { var values = row.EnumerateArray().Select(value => value.GetSingle()).ToArray(); return new(values[0], values[1], values[2], values[3]); }
    private static void Check(bool success, string message) { if (!success) throw new Exception(message); }
}
