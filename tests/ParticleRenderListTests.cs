using System.Text.Json;
using Gh3Skin;

internal static class ParticleRenderListTests
{
    public static void Run()
    {
        if (Environment.GetEnvironmentVariable("GH3_NATIVE_PARTICLE_RENDER_LIST_ORACLE") is not { } path) return;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var count = 0;
        var candidates = 0;
        foreach (var row in document.RootElement.GetProperty("rows").EnumerateArray())
        {
            var source = row.GetProperty("candidates").EnumerateArray().ToArray();
            var observed = new List<int>();
            var attempted = new List<int>();
            var selected = StageParticleRenderList.Select(Enumerable.Range(0, source.Length),
                index => source[index].GetProperty("present").GetBoolean() && source[index].GetProperty("enabled").GetBoolean(),
                index => { attempted.Add(index); return source[index].GetProperty("allocated").GetBoolean(); }, observed.Add).ToArray();
            foreach (var (name, actual) in new[] { ("selected", selected), ("observed", observed.ToArray()), ("attempted", attempted.ToArray()) })
                if (!actual.SequenceEqual(row.GetProperty(name).EnumerateArray().Select(value => value.GetInt32())))
                    throw new Exception($"Native particle render-list {name} differs in case {count}");
            count++;
            candidates += source.Length;
        }
        Console.WriteLine($"Native particle render-list limit, filtering, preparation, and observations passed: {count} lists, {candidates} candidates.");
    }
}
