using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Gh3Skin.Packages;

internal enum PackageKind { Stage, Character, Library }

internal readonly record struct PackageFile(long Size, string Sha256);
internal readonly record struct PackageRequirement(string Id, SemanticVersion Version);

// manifest.json of package format v1 (docs/PACKAGE-FORMAT.md section 3). The importer checks the fields that it uses;
// the Python validator (pipeline/package/validate.py) checks the full schema.
internal sealed class PackageManifest
{
    public const int FormatMajor = 1;
    // The highest minor of each kind that this mod reads: format 1.1 adds the character library and its profiles.
    public static int FormatMinor(PackageKind kind) => kind == PackageKind.Stage ? 0 : 1;
    public PackageKind Kind { get; private init; }
    public string Format { get; private init; } = "";
    public string Id { get; private init; } = "";
    public SemanticVersion Version { get; private init; }
    public string Name { get; private init; } = "";
    public IReadOnlyDictionary<string, string> Entry { get; private init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, PackageFile> Files { get; private init; } = new Dictionary<string, PackageFile>();
    public PackageRequirement[] Requires { get; private init; } = Array.Empty<PackageRequirement>();
    public SemanticVersion? MinModVersion { get; private init; }
    public string Preview { get; private init; } = "";

    public static string Extension(PackageKind kind) => kind switch
    {
        PackageKind.Stage => ".chstage", PackageKind.Character => ".chchar", _ => ".chlib",
    };

    public static PackageKind? KindOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".chstage" => PackageKind.Stage, ".chchar" => PackageKind.Character, ".chlib" => PackageKind.Library, _ => null,
    };

    public static bool ValidId(string id) => Regex.IsMatch(id, @"\A[a-z0-9](?:[a-z0-9.-]{0,95})\z") && id.Length <= 96;

    public static bool ValidPath(string path)
    {
        if (path.Length is 0 or > 240) return false;
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment[0] == '.' || segment[^1] == '.' || !Regex.IsMatch(segment, @"\A[A-Za-z0-9_.-]+\z")) return false;
            var stem = segment.Split('.')[0].ToLowerInvariant();
            if (stem is "con" or "prn" or "aux" or "nul" || Regex.IsMatch(stem, @"\A(com|lpt)[0-9]\z")) return false;
        }
        return true;
    }

    public static PackageManifest Parse(byte[] json, PackageKind kind, SemanticVersion modVersion)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new PackageException("manifest.json is not a JSON object");
        string Text(string name) => root[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text :
            throw new PackageException($"manifest.json needs the text field '{name}'");
        var format = Text("format");
        var match = Regex.Match(format, @"\A(chstage|chchar|chlib)/([0-9]+)\.([0-9]+)\z");
        if (!match.Success) throw new PackageException($"unknown package format '{format}'");
        var formatKind = match.Groups[1].Value switch { "chstage" => PackageKind.Stage, "chchar" => PackageKind.Character, _ => PackageKind.Library };
        if (formatKind != kind) throw new PackageException($"format '{format}' does not agree with the file extension {Extension(kind)}");
        var major = int.Parse(match.Groups[2].Value);
        var minor = int.Parse(match.Groups[3].Value);
        if (major != FormatMajor) throw new PackageException($"format '{format}' is not supported (this mod reads {match.Groups[1].Value}/{FormatMajor}.x)");
        if (minor > FormatMinor(kind)) throw new PackageException($"format '{format}' needs a newer mod (this mod reads {match.Groups[1].Value}/{FormatMajor}.{FormatMinor(kind)})");
        var id = Text("id");
        if (!ValidId(id)) throw new PackageException($"invalid package id '{id}'");
        if (!SemanticVersion.TryParse(Text("version"), out var version)) throw new PackageException("invalid package version");
        var name = Text("name");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128) throw new PackageException("invalid package name");
        SemanticVersion? minMod = null;
        if (root["minModVersion"] != null)
        {
            if (!SemanticVersion.TryParse(Text("minModVersion"), out var minimum)) throw new PackageException("invalid minModVersion");
            if (minimum.CompareTo(modVersion) > 0) throw new PackageException($"needs mod version {minimum} or later (installed: {modVersion})");
            minMod = minimum;
        }
        var entry = new Dictionary<string, string>();
        if (root["entry"] is not JsonObject entries) throw new PackageException("manifest.json needs an 'entry' object");
        foreach (var (role, value) in entries)
        {
            if (value is not JsonValue path || !path.TryGetValue<string>(out var text) || !ValidPath(text)) throw new PackageException($"invalid entry '{role}'");
            entry[role] = text;
        }
        var files = new Dictionary<string, PackageFile>(StringComparer.Ordinal);
        if (root["files"] is not JsonObject listed) throw new PackageException("manifest.json needs a 'files' object");
        var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, value) in listed)
        {
            if (!ValidPath(path) || path == "manifest.json" || !folded.Add(path)) throw new PackageException($"invalid file path '{path}'");
            if (value is not JsonObject row || row["sha256"] is not JsonValue hash || !hash.TryGetValue<string>(out var sha) ||
                !Regex.IsMatch(sha, @"\A[0-9a-f]{64}\z") || row["size"] is not JsonValue size || !size.TryGetValue<long>(out var bytes) || bytes < 0)
                throw new PackageException($"invalid file record '{path}'");
            files[path] = new PackageFile(bytes, sha);
        }
        var requires = new List<PackageRequirement>();
        if (root["requires"] is JsonArray required)
            foreach (var item in required)
            {
                if (item is not JsonObject row || row["id"]?.GetValue<string>() is not { } rid || !ValidId(rid) ||
                    row["version"]?.GetValue<string>() is not { } text || !SemanticVersion.TryParse(text, out var lowest))
                    throw new PackageException("invalid 'requires' record");
                requires.Add(new PackageRequirement(rid, lowest));
            }
        var requiredEntries = kind switch
        {
            PackageKind.Stage => new[] { "model", "placements", "cameras" },
            PackageKind.Character => new[] { "model", "character" },
            // Format 1.1 adds the character library (section 10.1); a band library keeps its three entries.
            _ when minor >= 1 && entry.ContainsKey("characters") => new[] { "characters" },
            _ => new[] { "catalog", "band", "performance" },
        };
        foreach (var role in requiredEntries)
            if (!entry.ContainsKey(role)) throw new PackageException($"manifest.json entry needs '{role}'");
        var preview = root["preview"] == null ? "" : Text("preview");
        if (preview.Length > 0 && (!ValidPath(preview) || !preview.EndsWith(".png", StringComparison.Ordinal))) throw new PackageException("invalid preview path");
        return new PackageManifest
        {
            Kind = kind, Format = format, Id = id, Version = version, Name = name, Entry = entry, Files = files,
            Requires = requires.ToArray(), MinModVersion = minMod, Preview = preview,
        };
    }
}

internal sealed class PackageException : Exception
{
    public PackageException(string message) : base(message) { }
}

internal readonly record struct SemanticVersion(int Major, int Minor, int Patch, string Prerelease) : IComparable<SemanticVersion>
{
    public static bool TryParse(string text, out SemanticVersion version)
    {
        version = default;
        var match = Regex.Match(text, @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?\z");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var major) || !int.TryParse(match.Groups[2].Value, out var minor) ||
            !int.TryParse(match.Groups[3].Value, out var patch)) return false;
        version = new SemanticVersion(major, minor, patch, match.Groups[4].Value);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        var order = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (order != 0) return order;
        if (Prerelease == other.Prerelease) return 0;
        if (Prerelease.Length == 0) return 1;
        if (other.Prerelease.Length == 0) return -1;
        var a = Prerelease.Split('.');
        var b = other.Prerelease.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var numeric = int.TryParse(a[i], out var x) & int.TryParse(b[i], out var y);
            var part = numeric ? x.CompareTo(y) : string.CompareOrdinal(a[i], b[i]);
            if (part != 0) return part;
        }
        return a.Length.CompareTo(b.Length);
    }

    public bool Satisfies(SemanticVersion lowest) => Major == lowest.Major && CompareTo(lowest) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Prerelease.Length > 0 ? "-" + Prerelease : "");
}
