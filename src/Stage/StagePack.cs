using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gh3Skin;

internal class StagePlacement
{    private const double UnitTolerance = 0.001;


    public float[] Position { get; set; } = Array.Empty<float>();
    public float[] Rotation { get; set; } = Array.Empty<float>();

    public bool Valid => Position.Length == 3 && Rotation.Length == 4 && Position.Concat(Rotation).All(float.IsFinite) &&
        Math.Abs(Rotation.Sum(v => v * v) - 1) < UnitTolerance;
}

internal sealed class StageCrowdMember
{
    public string Role { get; set; } = "";
    public string Model { get; set; } = "";
    public float Yaw { get; set; }
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
    public StageCrowdMember[] Crowd { get; set; } = Array.Empty<StageCrowdMember>();

    public static bool ValidId(string id) => Regex.IsMatch(id, @"\A[a-z0-9][a-z0-9.-]{0,95}\z");

    public static StagePack Load(string directory)
    {
        var pack = JsonSerializer.Deserialize<StagePack>(File.ReadAllText(Path.Combine(directory, "pack.json")),
            Packages.PackageJson.CaseInsensitive);
        if (pack?.Version != 1 || !ValidId(pack.Id) || pack.Library != "gh3-band-v1" || pack.Band.Count is < 1 or > 4 ||
            pack.Band.Keys.Any(r => r is not ("guitarist" or "bassist" or "singer" or "drummer")) ||
            pack.Band.Values.Concat(pack.Waypoints.Values).Any(p => !p.Valid) ||
            pack.Effects.Any(e => e.Key is not ("pyro" or "sparks" or "fireworks" or "smoke") || e.Value.Length > 64 || e.Value.Any(p => !p.Valid)))
            throw new InvalidDataException("Invalid compiled stage pack");
        if (pack.Crowd.Length > 8 || pack.Crowd.Select(member => member.Role).Distinct().Count() != pack.Crowd.Length ||
            pack.Crowd.Any(member => !Regex.IsMatch(member.Role, @"\Acrowd[1-8]\z") || !Regex.IsMatch(member.Model, @"\A[a-zA-Z0-9_]{1,64}\z") || !float.IsFinite(member.Yaw) || Math.Abs(member.Yaw) > 360))
            throw new InvalidDataException("Invalid stage crowd members.");
        return pack;
    }
}
