using System.Text.Json;
using Gh3Skin;

internal static class ParticleTechniqueTests
{
    public static void Run()
    {
        if (Environment.GetEnvironmentVariable("GH3_NATIVE_PARTICLE_TECHNIQUES_ORACLE") is not { } path) return;
        var root = Environment.GetEnvironmentVariable("GH3_PACK_ASSETS") ?? "assets";
        var techniques = StageParticleTechniques.Load(Path.Combine(root, "libraries/gh3-band-v1/render/particle-materials.json"));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var source = techniques.Lit.Concat(techniques.Unlit).ToArray();
        var rows = document.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        if (rows.Length != source.Length) throw new Exception("Particle technique count differs");
        foreach (var row in rows)
        {
            var actual = source.Single(value => value.Name == row.GetProperty("name").GetString());
            if (actual.SortId != row.GetProperty("sortId").GetByte() || actual.Flags != row.GetProperty("flags").GetUInt32())
                throw new Exception("Native particle technique sort fields differ: " + actual.Name);
        }
        Console.WriteLine($"Native particle technique loading, flags, and sort identifiers passed: {rows.Length} techniques.");
    }
}
