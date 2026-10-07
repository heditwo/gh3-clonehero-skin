using System.Numerics;
using System.Text.Json;
using Gh3Skin;

internal static class LightSceneTests
{
    public static void Run()
    {
        foreach (var name in new[] { "LIGHT_SCENES", "LIGHT_TRANSFORMS", "PARTICLE_SUBMISSION", "WORLD_LIGHTS" })
            if (Environment.GetEnvironmentVariable("GH3_NATIVE_" + name + "_ORACLE") is { } path) CheckScene(path);
    }

    private static void CheckScene(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var count = 0;
        var commands = 0;
        var submissions = 0;
        foreach (var row in document.RootElement.GetProperty("scenes").EnumerateArray())
        {
            var scene = new StageLightScene(row.GetProperty("sources").EnumerateArray().Select(value => Gh3QbText.Decode(value.GetRawText())));
            foreach (var group in row.GetProperty("groups").EnumerateArray())
            {
                var actual = scene.Groups[group.GetProperty("name").GetUInt32()];
                if (actual.Flags != group.GetProperty("flags").GetUInt32() || actual.Weight != group.GetProperty("weight").GetSingle())
                    throw new Exception("Native scene light group differs: " + row.GetProperty("venue"));
            }
            void Query(JsonElement query)
            {
                var position = Vector(query.GetProperty("position"));
                var sample = scene.Sample(new(position.X, position.Y, position.Z), query.GetProperty("group").GetUInt32());
                var actual = sample.Lighting;
                void Check(bool value, string field)
                {
                    if (!value) throw new Exception($"Native scene light selection differs: {row.GetProperty("venue")}, query {count}, {field}");
                }
                Check(sample.Candidates.SequenceEqual(query.GetProperty("candidates").EnumerateArray().Select(value => value.GetInt32())), "candidates");
                Check(actual.Flags == query.GetProperty("flags").GetUInt32(), "flags");
                Check(actual.Indices.SequenceEqual(query.GetProperty("indices").EnumerateArray().Select(value => value.GetInt32())), "indices");
                Check(actual.Weights.Zip(query.GetProperty("weights").EnumerateArray(), (value, expected) => Math.Abs(value-expected.GetSingle()) < .00001f).All(value => value), "weights");
                Check(Vector4.Distance(actual.Ambient, Vector(query.GetProperty("ambient"))) < .0001f, "ambient");
                Check(Vector4.Distance(actual.Direction, Vector(query.GetProperty("direction"))) < .0001f, "direction");
                Check(Vector4.Distance(actual.Color, Vector(query.GetProperty("color"))) < .0001f, "color");
                for (var i = 0; i < 4; i++) Check(Vector4.Distance(actual.Harmonics[i], Vector(query.GetProperty("harmonics")[i])) < .0001f, "harmonics " + i);
                count++;
            }
            foreach (var query in row.GetProperty("queries").EnumerateArray()) Query(query);
            if (row.TryGetProperty("submitted", out var submitted))
            {
                StageParticleLightSet? bound = null;
                foreach (var item in submitted.EnumerateArray())
                {
                    var position = Vector(item.GetProperty("position"));
                    var group = item.GetProperty("group").GetUInt32();
                    var lit = item.GetProperty("lit").GetBoolean();
                    void Check(bool valid, string field)
                    {
                        if (!valid) throw new Exception($"Native particle submission differs: {row.GetProperty("venue")}, {submissions}, {field}");
                    }
                    Check(item.GetProperty("packetType").GetUInt32() == 0x20060 && item.GetProperty("objectPointer").GetBoolean() &&
                        item.GetProperty("context").GetUInt32() == 0x11223344 && item.GetProperty("lightingPointer").GetBoolean() == lit, "packet");
                    if (lit)
                    {
                        var request = item.GetProperty("request");
                        var radius = item.GetProperty("radius").GetSingle();
                        Check(Vector(request.GetProperty("position")) == position && Vector(request.GetProperty("extent")) == new Vector4(radius, radius, radius, 0) &&
                            request.GetProperty("group").GetUInt32() == group, "query arguments");
                        bound = scene.Sample(new(position.X, position.Y, position.Z), group).Lighting;
                    }
                    else Check(item.GetProperty("request").ValueKind == JsonValueKind.Null, "unlit query");
                    Check(bound != null && Vector4.Distance(bound.Direction, Vector(item.GetProperty("direction"))) < .0001f &&
                        Vector4.Distance(bound.Color, Vector(item.GetProperty("color"))) < .0001f, "shared directional binding");
                    for (var i = 0; i < 4; i++) Check(Vector4.Distance(bound!.Harmonics[i], Vector(item.GetProperty("harmonics")[i])) < .0001f, "shared harmonics " + i);
                    submissions++;
                }
            }
            foreach (var action in row.GetProperty("actions").EnumerateArray())
            {
                var command = action.GetProperty("command").GetString()!;
                var result = scene.Execute(Gh3Checksum.Value(command), Gh3QbText.Decode(action.GetProperty("parameters").GetRawText()));
                void Check(bool valid, string field)
                {
                    if (!valid) throw new Exception($"Native light command differs: {row.GetProperty("venue")}, {commands} {command}, {field}");
                }
                Check(result?.Success == action.GetProperty("result").GetBoolean(), "result");
                foreach (var field in action.GetProperty("returned").EnumerateObject())
                {
                    var value = result?.Values?.FirstOrDefault(value => value.Name == Convert.ToUInt32(field.Name, 16))?.Value;
                    Check(value != null && (field.Value.ValueKind == JsonValueKind.Array ? value.Data is float[] vector &&
                        vector.Zip(field.Value.EnumerateArray(), (a, b) => Math.Abs(a-b.GetSingle()) < .00001f).All(match => match) :
                        Math.Abs(Gh3ScriptRuntime.Numeric(value)-field.Value.GetSingle()) < .00001f), "returned " + field.Name);
                }
                foreach (var group in action.GetProperty("groups").EnumerateArray())
                {
                    var actual = scene.Groups[group.GetProperty("name").GetUInt32()];
                    Check(actual.Flags == group.GetProperty("flags").GetUInt32() && actual.Weight == group.GetProperty("weight").GetSingle(), "group");
                }
                foreach (var light in action.GetProperty("lights").EnumerateArray())
                {
                    var id = light.GetProperty("id").GetUInt32();
                    Check(scene.Flags(id) == light.GetProperty("flags").GetUInt32(), "light flags");
                    Check(scene.Group(id) == light.GetProperty("group").GetUInt32(), "light group");
                    if (light.TryGetProperty("position", out var position))
                    {
                        var transform = scene.Transform(id);
                        Check(Vector4.Distance(transform.Position, Vector(position)) < .00001f, "position");
                        Check(Vector4.Distance(transform.Direction, Vector(light.GetProperty("direction"))) < .00001f, "direction");
                        var bounds = light.GetProperty("bounds");
                        Check(Vector3.Distance(transform.BoundsPosition, new(bounds[0].GetSingle(), bounds[1].GetSingle(), bounds[2].GetSingle())) < .0001f, "bounds");
                    }
                }
                foreach (var query in action.GetProperty("queries").EnumerateArray()) Query(query);
                commands++;
            }
        }
        Console.WriteLine($"Native scene light registration, group state, spatial selection, and shader bindings passed: {count} queries, {commands} commands, {submissions} submissions ({Path.GetFileName(path)}).");
    }

    private static Vector4 Vector(JsonElement value) => new(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle(), value[3].GetSingle());
}
