using System.Numerics;
using System.Text.Json;
using Gh3Skin;

internal static class ParticleRenderOrderTests
{
    public static void Run()
    {
        if (Environment.GetEnvironmentVariable("GH3_NATIVE_PARTICLE_ORDER_ORACLE") is not { } path) return;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        foreach (var row in root.GetProperty("scales").EnumerateArray())
            if (StageParticleRenderOrder.DistanceScale(row.GetProperty("near").GetSingle(), row.GetProperty("far").GetSingle()) != row.GetProperty("scale").GetSingle())
                throw new Exception("Native particle camera sort scale differs");
        var count = 0;
        foreach (var row in root.GetProperty("rows").EnumerateArray())
        {
            static Vector3 Vector(JsonElement value) => new(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle());
            var actual = StageParticleRenderOrder.Key(row.GetProperty("group").GetByte(), row.GetProperty("order").GetSingle(),
                row.GetProperty("blend").GetInt32() != 0 || row.GetProperty("forced").GetBoolean(), row.GetProperty("material").GetByte(),
                Vector(row.GetProperty("position")), Vector(row.GetProperty("camera")), row.GetProperty("scale").GetSingle());
            if (actual != row.GetProperty("key").GetUInt32()) throw new Exception($"Native particle sort key differs: {count}, {actual:x8}, {row.GetProperty("key").GetUInt32():x8}");
            count++;
        }
        var sorts = 0;
        foreach (var row in root.GetProperty("sorts").EnumerateArray())
        {
            var actual = StageParticleRenderOrder.Sort(row.GetProperty("keys").EnumerateArray().Select(value => value.GetUInt32()).ToArray());
            if (!actual.SequenceEqual(row.GetProperty("indices").EnumerateArray().Select(value => value.GetInt32())))
                throw new Exception("Native particle command list order differs: " + sorts);
            sorts++;
        }
        Console.WriteLine($"Native particle render keys and command list ordering passed: {count} keys, {sorts} sorts.");
    }
}
