using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using static Gh3Skin.Packages.PackageJson;

namespace Gh3Skin.Packages;

// A rig profile of a character library (PACKAGE-FORMAT.md section 10.1): the GH3 skeleton of one guitarist profile and
// outfit, with the names of its animation set, hair, and IK chains in the same library.
internal sealed class CharacterProfile
{
    public string Id { get; init; } = "";
    public string Role { get; init; } = "";
    public string Animations { get; init; } = "";
    public string Hair { get; init; } = "";
    public string Ik { get; init; } = "";
    public float Scale { get; init; } = 1;
    public string[] Bones { get; init; } = System.Array.Empty<string>();
    public int[] Parents { get; init; } = System.Array.Empty<int>();
    // Local bind transforms [x, y, z, qx, qy, qz, qw] in runtime coordinates, as the rows of GH3ANM01.
    public float[][] Bind { get; init; } = System.Array.Empty<float[]>();
}

// characters.json of a character library in the cache: the rig profiles and the clip rig. The animation sets, hair, and
// IK chains are read by the stage (CharacterMotion).
internal sealed class CharacterLibrary
{
    public const string FileName = "characters.json";
    private static readonly ConcurrentDictionary<string, CharacterLibrary> Loaded = new(StringComparer.Ordinal);
    public string Folder { get; private init; } = "";
    public string Id { get; private init; } = "";
    public CharacterProfile ClipRig { get; private init; } = new();
    public IReadOnlyDictionary<string, CharacterProfile> Profiles { get; private init; } = new Dictionary<string, CharacterProfile>();
    public IReadOnlySet<string> Animations { get; private init; } = new HashSet<string>();

    public static bool Has(string folder) => File.Exists(Path.Combine(folder, FileName));

    // Cache folders do not change after the import, so one read for each folder is enough.
    public static CharacterLibrary Load(string folder) => Loaded.GetOrAdd(Path.GetFullPath(folder), Read);

    private static CharacterLibrary Read(string folder)
    {
        var root = Object(File.ReadAllBytes(Path.Combine(folder, FileName)));
        if (Number(root["version"], "version") != 1) throw new PackageException("characters.json version must be 1");
        var animations = (root["animations"] as JsonObject ?? throw new PackageException("characters.json has no animations")).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var hair = (root["hair"] as JsonObject ?? new JsonObject()).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var ik = (root["ik"] as JsonObject ?? new JsonObject()).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var clipRig = Profile("clipRig", root["clipRig"] as JsonObject ?? throw new PackageException("characters.json has no clipRig"), null, null, null, rigOnly: true);
        var profiles = new Dictionary<string, CharacterProfile>(StringComparer.Ordinal);
        foreach (var (id, node) in root["profiles"] as JsonObject ?? throw new PackageException("characters.json has no profiles"))
        {
            if (!PackageManifest.ValidId(id)) throw new PackageException($"invalid profile id {id}");
            var profile = Profile(id, node as JsonObject ?? throw new PackageException($"invalid profile {id}"), animations, hair, ik, rigOnly: false);
            if (!profile.Bones.SequenceEqual(clipRig.Bones)) throw new PackageException($"profile {id} has other bones than the clip rig");
            profiles[id] = profile;
        }
        return new CharacterLibrary
        {
            Folder = folder, Id = Text(root["library"]) ?? "", ClipRig = clipRig, Profiles = profiles, Animations = animations,
        };
    }

    private static CharacterProfile Profile(string id, JsonObject row, ISet<string>? animations, ISet<string>? hair, ISet<string>? ik, bool rigOnly)
    {
        var bones = (row["bones"] as JsonArray ?? throw new PackageException($"profile {id} has no bones"))
            .Select(b => Text(b) is { Length: 8 } name && name.All(Uri.IsHexDigit) ? name : throw new PackageException($"invalid bone in profile {id}")).ToArray();
        var parents = (row["parents"] as JsonArray ?? throw new PackageException($"profile {id} has no parents")).Select(p => (int)Number(p, "parent")).ToArray();
        var bind = (row["bind"] as JsonArray ?? throw new PackageException($"profile {id} has no bind pose"))
            .Select(r => (r as JsonArray ?? throw new PackageException($"invalid bind pose in profile {id}")).Select(v => (float)Number(v, "bind")).ToArray()).ToArray();
        if (bones.Length is < 1 or > 255 || parents.Length != bones.Length || bind.Length != bones.Length || bind.Any(r => r.Length != 7 || r.Any(v => !float.IsFinite(v))) ||
            parents.Select((p, i) => p < -1 || p >= bones.Length || p == i).Any(bad => bad) || bones.Distinct().Count() != bones.Length)
            throw new PackageException($"invalid rig in profile {id}");
        for (var i = 0; i < parents.Length; i++)
            for (int bone = i, depth = 0; bone >= 0; bone = parents[bone], depth++)
                if (depth > bones.Length) throw new PackageException($"the rig of profile {id} has a cycle");
        if (rigOnly) return new CharacterProfile { Id = id, Bones = bones, Parents = parents, Bind = bind };
        var role = Text(row["role"]) ?? "";
        var set = Text(row["animations"]) ?? "";
        var hairId = row["hair"] == null ? "" : Text(row["hair"]) ?? throw new PackageException($"invalid hair in profile {id}");
        var ikId = Text(row["ik"]) ?? "";
        var scale = Number(row["scale"], "scale");
        if (role != "guitarist") throw new PackageException($"profile {id}: role {role} is not supported");
        if (!animations!.Contains(set)) throw new PackageException($"profile {id}: no animation set {set}");
        if (hairId.Length > 0 && !hair!.Contains(hairId)) throw new PackageException($"profile {id}: no hair {hairId}");
        if (!ik!.Contains(ikId)) throw new PackageException($"profile {id}: no IK chains {ikId}");
        if (!double.IsFinite(scale) || scale is < 0.25 or > 4) throw new PackageException($"profile {id}: invalid scale");
        return new CharacterProfile
        {
            Id = id, Role = role, Animations = set, Hair = hairId, Ik = ikId, Scale = (float)scale, Bones = bones, Parents = parents, Bind = bind,
        };
    }
}
