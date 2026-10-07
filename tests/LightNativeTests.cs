using System.Numerics;
using System.Text.Json;
using Gh3Skin;

internal static class LightNativeTests
{
    public static void Run()
    {
        if (Environment.GetEnvironmentVariable("GH3_NATIVE_LIGHT_NODES_ORACLE") is not { } path) return;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var count = 0;
        foreach (var row in document.RootElement.GetProperty("rows").EnumerateArray())
        {
            var light = new StageLightNative(Gh3QbText.Decode(row.GetProperty("source").GetRawText()));
            void Check(bool value, string field)
            {
                if (!value) throw new Exception($"Native light node {count} ({light.Id:x8}) differs: {field}");
            }
            void Compare(Vector4 actual, string field, float tolerance = .00001f)
            {
                var expected = Vector(row.GetProperty(field));
                Check(Vector4.Distance(actual, expected) < tolerance, $"{field}: {actual}/{expected}");
            }
            Check(light.Type == row.GetProperty("type").GetInt32(), "type");
            Check(light.Flags == row.GetProperty("flags").GetUInt32(), "flags");
            Compare(light.Color, "color");
            Compare(light.Position, "position");
            Compare(light.Direction, "direction");
            Compare(light.Attenuation, "attenuation");
            Compare(light.Cone, "cone", .001f);
            Check(light.Radius == row.GetProperty("radius").GetSingle(), "radius");
            Check(light.Intensity == row.GetProperty("intensity").GetSingle(), "intensity");
            Check(light.Specular == row.GetProperty("specular")[0].GetSingle() && light.SpecularPower == row.GetProperty("specular")[1].GetSingle(), "specular");
            var bounds = row.GetProperty("bounds");
            Check(Vector3.Distance(light.BoundsPosition, new(bounds[0].GetSingle(), bounds[1].GetSingle(), bounds[2].GetSingle())) < .00005f, "bounds position");
            Check(Vector3.Distance(light.BoundsExtent, new(bounds[4].GetSingle(), bounds[5].GetSingle(), bounds[6].GetSingle())) < .00001f, "bounds extent");
            for (var i = 0; i < 9; i++) Check(Vector4.Distance(light.Harmonics[i], Vector(row.GetProperty("harmonics")[i])) < .00001f, "harmonic " + i);
            count++;
        }
        var blends = 0;
        foreach (var row in document.RootElement.GetProperty("blends").EnumerateArray())
        {
            static StageLightValue Value(JsonElement value) => new(new(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle()),
                value[4].GetSingle(), value[5].GetSingle(), value[6].GetSingle(), 0, 10);
            var actual = StageLightValue.Mix(Value(row.GetProperty("before")), Value(row.GetProperty("after")), row.GetProperty("amount").GetSingle());
            var expected = Vector(row.GetProperty("color"));
            if (Vector3.Distance(actual.Color, new(expected.X, expected.Y, expected.Z)) > .000001f ||
                Math.Abs(actual.Intensity-row.GetProperty("intensity").GetSingle()) > .000001f ||
                Math.Abs(actual.Specular-row.GetProperty("specular")[0].GetSingle()) > .000001f ||
                Math.Abs(actual.SpecularPower-row.GetProperty("specular")[1].GetSingle()) > .000001f)
                throw new Exception("Native light color/intensity blend differs: " + blends);
            blends++;
        }
        Console.WriteLine($"Native light node defaults, bounds, direction, attenuation, flags, and harmonics passed: {count} source nodes, {blends} color/intensity blends.");
    }

    private static Vector4 Vector(JsonElement value) => new(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle(), value[3].GetSingle());
}
