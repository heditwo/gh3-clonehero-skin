using System.Numerics;
using System.Text.Json;
using Gh3Skin;

internal static class TeslaRenderTests
{
    public static void Run()
    {
        if (Environment.GetEnvironmentVariable("GH3_NATIVE_TESLA_RENDER_ORACLE") is not { } path) return;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var draws = 0;
        foreach (var row in document.RootElement.GetProperty("rows").EnumerateArray())
        {
            using var runtime = new Gh3ScriptRuntime(new Dictionary<uint, Gh3QbValue>(), (_, _, _) => null);
            var tesla = new StageTeslaState(runtime, _ => { });
            var command = Gh3Checksum.Value("CreateTeslaEffectObject");
            var parameters = Gh3QbText.Decode(row.GetProperty("parameters").GetRawText());
            tesla.Execute(new(command, 0, parameters.Fields), command, parameters);
            var effect = tesla.Effects[0]!;
            var forward = row.GetProperty("forward");
            var tapered = row.GetProperty("function").GetString() == "0x624ae0";
            var actual = StageTeslaRender.Build(effect, new(forward[0].GetSingle(), forward[1].GetSingle(), -forward[2].GetSingle()),
                row.GetProperty("milliseconds").GetUInt32(), tapered);
            void Check(float actual, float expected, string field)
            {
                if (Math.Abs(actual - expected) > .00001f) throw new Exception($"Tesla draw {draws} {field}: {actual} != {expected}");
            }
            var vertices = row.GetProperty("vertices");
            if (vertices.GetArrayLength() != actual.Positions.Length) throw new Exception("Tesla vertex count differs");
            for (var index = 0; index < actual.Positions.Length; index++)
            {
                var position = actual.Positions[index];
                var coordinate = actual.Coordinates[index];
                var components = new[] { position.X, position.Y, -position.Z, 1, coordinate.X, coordinate.Y, coordinate.Z };
                for (var field = 0; field < components.Length; field++) Check(components[field], vertices[index][field].GetSingle(), $"vertex {index}/{field}");
            }
            foreach (var (name, vector) in new[] { ("pixel0", actual.Animation), ("vertex0", actual.Animation), ("pixel1", actual.Shape), ("vertex1", actual.Shape), ("pixel2", actual.Color) })
            {
                var expected = row.GetProperty("constants").GetProperty(name);
                for (var field = 0; field < 4; field++) Check(vector[field], expected[field].GetSingle(), $"constant {name}/{field}");
            }
            if (new StageParticleMaterial(parameters).PackedBlend != row.GetProperty("blend").GetUInt32()) throw new Exception("Tesla blend differs");
            draws++;
        }
        Console.WriteLine($"Native Tesla geometry, shader constants, and blend checks passed: {draws} draws.");
    }
}
