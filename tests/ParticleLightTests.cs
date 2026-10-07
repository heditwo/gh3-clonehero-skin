using System.Numerics;
using System.Text.Json;
using Gh3Skin;

internal static class ParticleLightTests
{
    public static void Run()
    {
        if (Environment.GetEnvironmentVariable("GH3_NATIVE_PARTICLE_LIGHTS_ORACLE") is not { } path) return;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var index = 0;
        foreach (var row in document.RootElement.GetProperty("rows").EnumerateArray())
        {
            var lights = row.GetProperty("lights").EnumerateArray().Select(value => new StageParticleLight(value.GetProperty("type").GetInt32(),
                Vector(value.GetProperty("color")), Vector(value.GetProperty("position")), Vector(value.GetProperty("direction")),
                value.GetProperty("intensity").GetSingle(), value.GetProperty("weight").GetSingle(), value.GetProperty("specularPower").GetSingle(),
                value.GetProperty("farSquared").GetSingle(), value.GetProperty("inverseRange").GetSingle(), value.GetProperty("flags").GetUInt32(),
                value.GetProperty("harmonics").EnumerateArray().Select(Vector).ToArray())).ToArray();
            var actual = StageParticleLights.Select(lights, Vector(row.GetProperty("position")), row.GetProperty("disableSH").GetBoolean());
            var expected = row.GetProperty("selection");
            void Check(bool condition, string field)
            {
                if (!condition) throw new Exception($"Native particle light set {index} differs: {field}");
            }
            Check(actual.Flags == expected.GetProperty("flags").GetUInt32(), "flags");
            Check(actual.Indices.SequenceEqual(expected.GetProperty("indices").EnumerateArray().Select(value => value.GetInt32())), "indices");
            Check(actual.Weights.SequenceEqual(expected.GetProperty("weights").EnumerateArray().Select(value => value.GetSingle())), "weights");
            Check(Vector4.Distance(actual.Ambient, Vector(expected.GetProperty("ambient"))) < .00001f, "ambient");
            Check(Vector4.Distance(actual.Direction, Vector(row.GetProperty("direction"))) < .00001f, "direction");
            Check(Vector4.Distance(actual.Color, Vector(row.GetProperty("color"))) < .00001f, "color");
            for (var h = 0; h < 4; h++)
            {
                var reference = Vector(expected.GetProperty("harmonics")[h]);
                Check(Vector4.Distance(actual.Harmonics[h], reference) < .00002f, $"harmonic {h}: {actual.Harmonics[h]} / {reference}");
                Check(Vector4.Distance(actual.Harmonics[h], Vector(row.GetProperty("harmonics")[h])) < .00002f, "bound harmonic " + h);
            }
            index++;
        }
        foreach (var row in document.RootElement.GetProperty("queries").EnumerateArray())
        {
            var group = row.GetProperty("group").GetUInt32();
            var accepted = row.GetProperty("candidates").EnumerateArray().Select((value, i) => (Value: value, Index: i)).Where(item =>
                StageParticleLights.Accepts(group, item.Value.GetProperty("group").GetUInt32(), item.Value.GetProperty("enabled").GetUInt32(), item.Value.GetProperty("flags").GetUInt32()))
                .Take(row.GetProperty("limit").GetInt32()).Select(item => item.Index);
            if (!accepted.SequenceEqual(row.GetProperty("indices").EnumerateArray().Select(value => value.GetInt32()))) throw new Exception("Native particle light-group filter differs");
        }
        static Gh3QbValue Parameters(params (string Name, Gh3QbValue Value)[] fields) =>
            Gh3ScriptRuntime.Structure(fields.Select(field => new Gh3QbField(Gh3Checksum.Value(field.Name), field.Value)));
        if (new StageParticleDefinition(Parameters()).LightGroup != document.RootElement.GetProperty("defaultGroup").GetUInt32())
            throw new Exception("Native particle light-group default differs");
        foreach (var row in document.RootElement.GetProperty("parsed").EnumerateArray())
            if (new StageParticleDefinition(Parameters(("LightGroup", new(13, -1, row.GetProperty("group").GetUInt32())))).LightGroup != row.GetProperty("value").GetUInt32())
                throw new Exception("Native particle light-group parser differs");
        var bank = new StageScriptBank();
        StageParticleState? state = null;
        using var runtime = new Gh3ScriptRuntime(bank.Values, (context, command, parameters) => state!.Execute(context, command, parameters));
        state = new(bank, runtime, _ => false, (_, _, _) => null, _ => { });
        var id = Gh3Checksum.Value("light-test");
        runtime.Start(Gh3Checksum.Value("CreateParticleSystem"), Parameters(("name", new(13, -1, id)), ("params_script", Parameters())));
        var emitter = state.Emitters[id];
        foreach (var row in document.RootElement.GetProperty("commands").EnumerateArray())
        {
            emitter.LightGroup = row.GetProperty("initial").GetUInt32();
            var group = row.GetProperty("group");
            var parameters = group.ValueKind == JsonValueKind.Null ? Parameters() : Parameters(("group", new(13, -1, group.GetUInt32())));
            var command = Gh3Checksum.Value("LightGroup");
            var result = state.Execute(new(command, id, parameters.Fields), command, parameters);
            if (result?.Success != row.GetProperty("result").GetBoolean() || emitter.LightGroup != row.GetProperty("value").GetUInt32())
                throw new Exception("Native particle light-group command differs");
        }
        Console.WriteLine($"Native particle light selection, directional binding, spherical harmonics, group filters, and commands passed: {index} sets.");
    }

    private static Vector4 Vector(JsonElement value)
    {
        var values = value.EnumerateArray().Select(item => item.GetSingle()).ToArray();
        return new(values[0], values[1], values[2], values[3]);
    }
}
