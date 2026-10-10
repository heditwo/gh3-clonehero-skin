namespace Gh3Skin.Packages;

// An imported character in the cache (CharacterImporter): its folder, joint names, named instrument, and bind poses
// (row-major 4 × 4).
internal sealed class CharacterModel
{
    public string Folder { get; private init; } = "";
    public string Id { get; private init; } = "";
    public string Name { get; private init; } = "";
    public string Role { get; private init; } = "";
    // character.json attachments.instrument; "" for the default (also in caches that are older than the field).
    public string Instrument { get; private init; } = "";
    // The character library that has the profile, and the animation set that character.json names ("" for the
    // profile's set). "" for a profile of the band library (also in caches that are older than the fields).
    public string Library { get; private init; } = "";
    public string Profile { get; private init; } = "";
    public string Animations { get; private init; } = "";
    // The cache folder of `Library`; the stage sets it before the band loads.
    public string LibraryFolder { get; set; } = "";
    // The highway image of the character (cache highway.png), or "" when the package has none.
    public string Highway { get; private init; } = "";
    public string[] Joints { get; private init; } = Array.Empty<string>();
    // character.json bones, colliders, and chains (PACKAGE-FORMAT.md section 9.5), bone names as checksums; empty in
    // caches that are older than the fields.
    public AddedBone[] AddedBones { get; private init; } = Array.Empty<AddedBone>();
    public ChainCollider[] Colliders { get; private init; } = Array.Empty<ChainCollider>();
    public ChainJoint[] ChainJoints { get; private init; } = Array.Empty<ChainJoint>();
    public float[][] Bindposes { get; private init; } = Array.Empty<float[]>();

    public static CharacterModel Load(string folder)
    {
        var info = PackageJson.Object(File.ReadAllBytes(Path.Combine(folder, "character.json")));
        var joints = (info["joints"] as System.Text.Json.Nodes.JsonArray ?? throw new PackageException("character.json has no joints"))
            .Select(j => PackageJson.Text(j) ?? throw new PackageException("invalid character joint")).ToArray();
        using var reader = new BinaryReader(File.OpenRead(Path.Combine(folder, "bindposes.bin")));
        if (System.Text.Encoding.ASCII.GetString(reader.ReadBytes(8)) != "GH3BPS01" || reader.ReadInt32() != joints.Length)
            throw new PackageException("invalid character bind poses");
        var bindposes = joints.Select(_ => Enumerable.Range(0, 16).Select(_ => reader.ReadSingle()).ToArray()).ToArray();
        if (bindposes.Any(m => m.Any(v => !float.IsFinite(v)))) throw new PackageException("invalid character bind poses");
        return new CharacterModel
        {
            Folder = folder, Id = PackageJson.Text(info["id"]) ?? "", Name = PackageJson.Text(info["name"]) ?? "", Role = PackageJson.Text(info["role"]) ?? "",
            Instrument = PackageJson.Text(info["instrument"]) ?? "", Library = PackageJson.Text(info["library"]) ?? "",
            Profile = PackageJson.Text(info["profile"]) ?? "", Animations = PackageJson.Text(info["animations"]) ?? "",
            Joints = joints, Bindposes = bindposes,
            AddedBones = Rows(info["bones"], n => new AddedBone(PackageJson.Text(n["name"]) ?? "", PackageJson.Text(n["parent"]) ?? "",
                PackageJson.Vector(n["bind"], 7, "bone bind").Select(v => (float)v).ToArray())),
            Colliders = Rows(info["colliders"], n => new ChainCollider(PackageJson.Text(n["bone"]) ?? "", (float)PackageJson.Number(n["radius"], "radius"),
                Floats(n["a"]), Floats(n["b"]))),
            ChainJoints = Rows(info["chains"], n => new ChainJoint(PackageJson.Text(n["bone"]) ?? "", PackageJson.Text(n["parent"]) ?? "",
                (long)PackageJson.Number(n["colliderMask"], "colliderMask"), Joint(n))),
            Highway = PackageJson.Text(info["highway"]) is { Length: > 0 } highway ? Path.Combine(folder, highway) : "",
        };
    }

    private static readonly System.Text.Json.JsonSerializerOptions JointOptions = new() { PropertyNameCaseInsensitive = true };

    private static T[] Rows<T>(System.Text.Json.Nodes.JsonNode? node, Func<System.Text.Json.Nodes.JsonNode, T> read) =>
        node is System.Text.Json.Nodes.JsonArray rows ? rows.Select(n => read(n ?? throw new PackageException("invalid character chain row"))).ToArray() : Array.Empty<T>();

    private static HairJoint Joint(System.Text.Json.Nodes.JsonNode row)
    {
        var physics = (System.Text.Json.Nodes.JsonObject)PackageJson.Clone(row)!;
        physics.Remove("bone"); physics.Remove("parent"); physics.Remove("colliderMask");
        return System.Text.Json.JsonSerializer.Deserialize<HairJoint>(physics.ToJsonString(), JointOptions) ?? throw new PackageException("invalid chain joint");
    }

    private static float[] Floats(System.Text.Json.Nodes.JsonNode? node) => PackageJson.Vector(node, 3, "vector").Select(v => (float)v).ToArray();

    // For each character joint, the index of the bone with the same name in a band role's rig. A joint that the rig does
    // not have gives null: the character cannot play that role.
    public int[]? Map(IReadOnlyList<string> rigBones)
    {
        var result = new int[Joints.Length];
        for (var i = 0; i < Joints.Length; i++)
        {
            result[i] = rigBones is string[] array ? Array.IndexOf(array, Joints[i]) : rigBones.ToList().IndexOf(Joints[i]);
            if (result[i] < 0) return null;
        }
        return result;
    }
}

internal sealed record AddedBone(string Name, string Parent, float[] Bind);
internal sealed record ChainCollider(string Bone, float Radius, float[] A, float[] B);
// A joint of a custom chain; Joint holds the physics fields (its Bone and Parent indices are set on the rig that plays it).
internal sealed record ChainJoint(string Bone, string Parent, long ColliderMask, HairJoint Joint);
