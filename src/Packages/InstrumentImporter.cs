using System.Text.Json.Nodes;
using static Gh3Skin.Packages.PackageJson;

namespace Gh3Skin.Packages;

// Instrument package (`.chinst`, PACKAGE-FORMAT.md section 9.4) to a band model folder: the GLB on a fixed rig profile of
// the band library, converted as a character (CharacterImporter.ImportModel), plus instrument.json and the optional
// highway image. Every mesh part must follow the instrument joints (BandAttachments), not the body.
internal sealed class InstrumentImporter
{
    public static readonly IReadOnlyDictionary<string, string> Kinds = new Dictionary<string, string> { ["guitar"] = "guitarist", ["bass"] = "bassist" };
    private readonly VerifiedPackage _package;
    private readonly CharacterImporter _model;
    private readonly string _library;
    public List<string> Warnings => _model.Warnings;

    public InstrumentImporter(VerifiedPackage package, TextureJobs textures, string libraryCache)
    {
        _package = package;
        _library = libraryCache;
        _model = new CharacterImporter(package, textures, libraryCache);
    }

    public (string Kind, int Joints, int Meshes, int Vertices) Import(string folder)
    {
        var instrument = Object(_package.Read(_package.Manifest.Entry["instrument"]));
        Fields(instrument, "instrument.json", new[] { "version", "library", "profile", "kind", "highway", "brand", "model", "series", "finish" },
            "version", "library", "profile", "kind");
        if (Number(instrument["version"], "version") != 1) throw new PackageException("instrument.json version must be 1");
        if (Text(instrument["library"]) != "gh3-band-v1") throw new PackageException("instrument.json library must be gh3-band-v1");
        var kind = Text(instrument["kind"]) ?? "";
        if (!Kinds.ContainsKey(kind)) throw new PackageException($"instrument.json kind must be guitar or bass, not '{kind}'");
        var profile = Text(instrument["profile"]) ?? "";
        if (profile is not ("gh3-guitarist-axel-v1" or "gh3-bassist-v1")) throw new PackageException($"instrument.json profile must be gh3-guitarist-axel-v1 or gh3-bassist-v1, not '{profile}'");
        var labels = new JsonObject();
        foreach (var field in new[] { "brand", "model", "series", "finish" })
            if (instrument[field] != null) labels[field] = Text(instrument[field]) ?? throw new PackageException($"instrument.json {field} must be text");
        var highway = _model.Highway(instrument["highway"], folder);
        var role = CharacterImporter.Profiles[profile];
        var (bones, parents, bind) = CharacterImporter.Rig(_library, role);
        var (names, parts) = _model.ImportModel(folder, _package.Manifest.Entry["model"], profile, bones, parents, bind, new HashSet<string>());
        var body = BandAttachments.BodyJoints(bones, parents);
        var map = names.Select(name => System.Array.IndexOf(bones, name)).ToArray();
        var meshes = BandAttachments.ReadParts(Path.Combine(folder, "meshes.bin"));
        if (meshes.Any(part => !BandAttachments.IsAttachment(part, body, map)))
            throw new PackageException("a mesh part follows the body joints; weight the instrument to bone_guitar_body");
        Write(Path.Combine(folder, "instrument.json"), new JsonObject
        {
            ["version"] = 1, ["id"] = _package.Manifest.Id, ["name"] = _package.Manifest.Name, ["kind"] = kind, ["profile"] = profile,
            ["joints"] = new JsonArray(names.Select(n => (JsonNode?)n).ToArray()), ["highway"] = highway, ["labels"] = labels,
        });
        return (kind, names.Length, parts.Count, parts.Sum(p => p.Count));
    }
}

// An imported instrument in the cache (InstrumentImporter): the band role that can hold it, its joint names, and its
// highway image ("" when it has none).
internal sealed class InstrumentModel
{
    public string Folder { get; private init; } = "";
    public string Id { get; private init; } = "";
    public string Name { get; private init; } = "";
    public string Kind { get; private init; } = "";
    public string Role => InstrumentImporter.Kinds.TryGetValue(Kind, out var role) ? role : "";
    public string Highway { get; private init; } = "";
    public string[] Joints { get; private init; } = System.Array.Empty<string>();

    public static InstrumentModel Load(string folder)
    {
        var info = Object(File.ReadAllBytes(Path.Combine(folder, "instrument.json")));
        var joints = (info["joints"] as JsonArray ?? throw new PackageException("instrument.json has no joints"))
            .Select(j => Text(j) ?? throw new PackageException("invalid instrument joint")).ToArray();
        return new InstrumentModel
        {
            Folder = folder, Id = Text(info["id"]) ?? "", Name = Text(info["name"]) ?? "", Kind = Text(info["kind"]) ?? "", Joints = joints,
            Highway = Text(info["highway"]) is { Length: > 0 } highway ? Path.Combine(folder, highway) : "",
        };
    }
}
