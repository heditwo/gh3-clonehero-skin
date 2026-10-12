using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Gh3Skin.Packages.PackageJson;

namespace Gh3Skin.Packages;

// The rigs of a band library in the cache (catalog.json, PACKAGE-FORMAT.md section 10.2): for each band role its fixed
// profile ID, bone checksums, rig points, and IK chains, and the twist and split constraints of the library rigs.
internal sealed class LibraryRigs
{
    // A stage load asks for several roles; catalog.json is read one time for each file version.
    private static readonly ConcurrentDictionary<string, LibraryRigs> Loaded = new(StringComparer.Ordinal);
    private Dictionary<string, LibraryRole> Roles { get; init; } = new(StringComparer.Ordinal);
    // The library ID (catalog.json library).
    public string Library { get; private init; } = "";
    public RigConstraint[] Constraints { get; private init; } = System.Array.Empty<RigConstraint>();

    public sealed record LibraryRole(string Profile, string[] Bones, RigPoints Points, IkChainDefinition[] Ik);

    public static LibraryRigs Load(string libraryCache)
    {
        var path = Path.GetFullPath(Path.Combine(libraryCache, "catalog.json"));
        return Loaded.GetOrAdd(path + "|" + File.GetLastWriteTimeUtc(path).Ticks, _ => Read(path));
    }

    public LibraryRole Role(string role) =>
        Roles.TryGetValue(role, out var found) && found.Bones.Length > 0 ? found : throw new PackageException($"the library has no {role} rig");

    // The band role of a fixed profile of the library (character.json and instrument.json `profile`), or null.
    public string? ProfileRole(string profile) => Roles.FirstOrDefault(pair => pair.Value.Profile.Length > 0 && pair.Value.Profile == profile).Key;

    // The fixed profiles of the library, by band role.
    public IEnumerable<(string Role, string Profile)> Profiles => Roles.Where(pair => pair.Value.Profile.Length > 0).Select(pair => (pair.Key, pair.Value.Profile));

    private static LibraryRigs Read(string path)
    {
        var catalog = Object(File.ReadAllBytes(path));
        var roles = new Dictionary<string, LibraryRole>(StringComparer.Ordinal);
        foreach (var (role, member) in catalog["members"] as JsonObject ?? new JsonObject())
        {
            var bones = (member?["bones"] as JsonArray ?? new JsonArray()).Select(b => Text(b) ?? throw new PackageException("invalid library bone")).ToArray();
            var ik = Deserialize<IkChainDefinition>(member?["ik"], $"{role} IK chains");
            if (!IkChainDefinition.ValidChains(ik)) throw new PackageException($"invalid {role} IK chains");
            var profile = member?["profile"] == null ? "" : Text(member["profile"]) is { } id && PackageManifest.ValidId(id) ? id : throw new PackageException($"invalid {role} profile");
            roles[role] = new LibraryRole(profile, bones, RigPoints.Read(member?["points"], bones, role), ik);
        }
        var constraints = Deserialize<RigConstraint>(catalog["constraints"], "rig constraints");
        if (!RigConstraint.ValidRows(constraints)) throw new PackageException("invalid rig constraints");
        return new LibraryRigs { Roles = roles, Library = Text(catalog["library"]) ?? "", Constraints = constraints };
    }

    public static T[] Deserialize<T>(JsonNode? node, string what)
    {
        if (node == null) return System.Array.Empty<T>();
        try
        {
            return node.Deserialize<T[]>(CaseInsensitive) ?? System.Array.Empty<T>();
        }
        catch (JsonException error)
        {
            throw new PackageException($"invalid {what}: {error.Message}");
        }
    }
}
