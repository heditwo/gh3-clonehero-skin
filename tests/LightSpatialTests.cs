using System.Numerics;
using System.Text.Json;
using Gh3Skin;

internal static class LightSpatialTests
{
    public static void Run()
    {
        if (Environment.GetEnvironmentVariable("GH3_NATIVE_LIGHT_SPATIAL_ORACLE") is not { } path) return;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var scenes = 0;
        var queries = 0;
        foreach (var scene in document.RootElement.GetProperty("scenes").EnumerateArray())
        {
            var spatial = new StageLightSpatial();
            var actions = scene.GetProperty("actions").EnumerateArray().ToArray();
            void Apply(JsonElement action)
            {
                var index = action.GetProperty("index").GetInt32();
                if (action.GetProperty("action").GetString() == "remove") spatial.Remove(index);
                else if (action.GetProperty("action").GetString() == "compact") spatial.Compact();
                else
                {
                    var bounds = scene.GetProperty("bounds")[index];
                    spatial.Add(index, Vector(bounds.GetProperty("min")), Vector(bounds.GetProperty("max")));
                }
                for (var axis = 0; axis < 3; axis++)
                {
                    var expected = action.GetProperty("segments")[axis].EnumerateArray().ToArray();
                    var actual = spatial.Axis(axis);
                    if (actual.Count != expected.Length) throw new Exception($"Native light spatial segment count differs: scene {scenes}, {action.GetProperty("action")} {index}, axis {axis}: {actual.Count}/{expected.Length}");
                    for (var i = 0; i < actual.Count; i++)
                        if (actual[i].Start != expected[i].GetProperty("start").GetSingle() || actual[i].End != expected[i].GetProperty("end").GetSingle() ||
                            !actual[i].Members.SequenceEqual(expected[i].GetProperty("members").EnumerateArray().Select(value => value.GetInt32())))
                            throw new Exception($"Native light spatial segment differs: scene {scenes}, {action.GetProperty("action")} {index}, axis {axis}, interval {i}");
                }
                if (action.TryGetProperty("queries", out var samples))
                    foreach (var query in samples.EnumerateArray()) Query(query);
            }
            void Query(JsonElement query)
            {
                var actual = spatial.Query(Vector(query.GetProperty("position")));
                var expected = query.GetProperty("indices").EnumerateArray().Select(value => value.GetInt32());
                if (!actual.SequenceEqual(expected)) throw new Exception($"Native light spatial query differs: scene {scenes}, point {query.GetProperty("position")}");
                queries++;
            }
            foreach (var action in actions.Where(action => action.GetProperty("action").GetString() == "add")) Apply(action);
            foreach (var query in scene.GetProperty("queries").EnumerateArray()) Query(query);
            foreach (var action in actions.Where(action => action.GetProperty("action").GetString() != "add")) Apply(action);
            scenes++;
        }
        Console.WriteLine($"Native light spatial intervals, ordering, boundaries, removal, and compaction passed: {scenes} scenes, {queries} queries.");
    }

    private static Vector3 Vector(JsonElement value) => new(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle());
}
