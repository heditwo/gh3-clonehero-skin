using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gh3Skin;

internal sealed class StagePlacement
{
    public float[] Position { get; set; } = Array.Empty<float>();
    public float[] Rotation { get; set; } = Array.Empty<float>();

    public bool Valid => Position.Length == 3 && Rotation.Length == 4 && Position.Concat(Rotation).All(float.IsFinite) &&
        Math.Abs(Rotation.Sum(v => v * v) - 1) < 0.001;
}

internal sealed class StagePack
{
    public int Version { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Library { get; set; } = "";
    public Dictionary<string, StagePlacement> Band { get; set; } = new();
    public Dictionary<string, StagePlacement> Waypoints { get; set; } = new();
    public Dictionary<string, StagePlacement[]> Effects { get; set; } = new();

    public static bool ValidId(string id) => Regex.IsMatch(id, @"\A[a-z0-9][a-z0-9.-]{0,95}\z");

    public static StagePack Load(string directory)
    {
        var pack = JsonSerializer.Deserialize<StagePack>(File.ReadAllText(Path.Combine(directory, "pack.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (pack?.Version != 1 || !ValidId(pack.Id) || pack.Library != "gh3-band-v1" || pack.Band.Count != 4 ||
            pack.Band.Keys.Any(r => r is not ("guitarist" or "bassist" or "singer" or "drummer")) ||
            pack.Band.Values.Concat(pack.Waypoints.Values).Any(p => !p.Valid) ||
            pack.Effects.Any(e => e.Key is not ("pyro" or "sparks" or "fireworks" or "smoke") || e.Value.Length > 64 || e.Value.Any(p => !p.Valid)))
            throw new InvalidDataException("Invalid compiled stage pack");
        return pack;
    }
}
