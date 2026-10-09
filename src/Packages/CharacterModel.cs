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
    // Format 1.1: the character library that has the profile, and the animation set that character.json names ("" for the
    // profile's set). "" for a profile of the band library (also in caches that are older than the fields).
    public string Library { get; private init; } = "";
    public string Profile { get; private init; } = "";
    public string Animations { get; private init; } = "";
    // The cache folder of `Library`; the stage sets it before the band loads.
    public string LibraryFolder { get; set; } = "";
    public string[] Joints { get; private init; } = Array.Empty<string>();
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
        };
    }

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
