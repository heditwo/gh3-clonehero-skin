using System.Text.Json;

namespace Gh3Skin;

internal sealed class StageScriptBank
{
    public int Version { get; set; }
    public Dictionary<string, JsonElement> Definitions { get; set; } = new();
    public string[] Startup { get; set; } = Array.Empty<string>();
    public string[] Nodes { get; set; } = Array.Empty<string>();
    public string PerformanceChanged { get; set; } = "";
    public Dictionary<string, string[]> Prefixes { get; set; } = new();
    public Dictionary<string, string> LightVolumes { get; set; } = new();
    public IReadOnlyDictionary<uint, Gh3QbValue> NodeValues { get; private set; } = new Dictionary<uint, Gh3QbValue>();
    public IReadOnlyDictionary<uint, uint[]> PrefixValues { get; private set; } = new Dictionary<uint, uint[]>();
    public Dictionary<string, string> Textures { get; set; } = new();
    public Dictionary<string, string> Movies { get; set; } = new();
    public IReadOnlyDictionary<string, string> MoviePaths { get; private set; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<uint, string> TexturePaths { get; private set; } = new Dictionary<uint, string>();
    public IReadOnlyDictionary<uint, Gh3QbValue> Values { get; private set; } = new Dictionary<uint, Gh3QbValue>();

    public static StageScriptBank Load(string path, string? shared = null)
    {
        var bank = JsonSerializer.Deserialize<StageScriptBank>(File.ReadAllText(path), Packages.PackageJson.CaseInsensitive);
        if (bank?.Version != 1 || bank.Definitions.Count > 50000 || bank.Startup.Length > 256 ||
            bank.Definitions.Keys.Any(key => key.Length != 8 || !key.All(Uri.IsHexDigit)))
            throw new InvalidDataException("Invalid stage script library");
        if (bank.Nodes.Length > 256 || bank.Textures.Count > 2048) throw new InvalidDataException("Too many stage script resources");
        var library = shared != null && File.Exists(shared) ? Load(shared) : null;
        var values = library != null ? new Dictionary<uint, Gh3QbValue>(library.Values) : new();
        var textures = library != null ? new Dictionary<uint, string>(library.TexturePaths) : new();
        var directory = Path.GetFullPath(Path.GetDirectoryName(path)!) + Path.DirectorySeparatorChar;
        foreach (var (name, file) in bank.Textures)
        {
            var resolved = Path.GetFullPath(Path.Combine(directory, file));
            if (Path.IsPathRooted(file) || !resolved.StartsWith(directory, StringComparison.Ordinal) || !File.Exists(resolved))
                throw new InvalidDataException("Stage particle texture is outside its bank or missing");
            textures[Gh3QbText.Key(name)] = resolved;
        }
        bank.TexturePaths = textures;
        var movies = library != null ? new Dictionary<string, string>(library.MoviePaths, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
        if (bank.Movies.Count > 256) throw new InvalidDataException("Too many stage movies");
        foreach (var (name, file) in bank.Movies)
        {
            var resolved = Path.GetFullPath(Path.Combine(directory, file));
            if (name.Length is < 1 or > 256 || Path.IsPathRooted(file) || !resolved.StartsWith(directory, StringComparison.Ordinal) || !File.Exists(resolved))
                throw new InvalidDataException("Stage movie is outside its bank or missing");
            movies[name] = resolved;
        }
        bank.MoviePaths = movies;
        foreach (var pair in bank.Definitions) values[Convert.ToUInt32(pair.Key, 16)] = Gh3QbText.Decode(pair.Value.GetRawText());
        bank.Values = values;
        foreach (var name in bank.Nodes)
            if (!values.TryGetValue(Gh3QbText.Key(name), out var nodes) || nodes.Type != 12 || nodes.Items.Count > 10000)
                throw new InvalidDataException("Stage node array is missing or invalid: " + name);
        bank.NodeValues = bank.Nodes.SelectMany(name => values[Gh3QbText.Key(name)].Items).ToDictionary(row => row.Field("Name")?.Integer ?? throw new InvalidDataException("Stage node name is missing"));
        if (bank.Prefixes.Count > 100000 || bank.Prefixes.Values.Sum(members => (long)members.Length) > 1000000) throw new InvalidDataException("Too many node prefix entries");
        bank.PrefixValues = bank.Prefixes.ToDictionary(pair => Gh3QbText.Key(pair.Key), pair => pair.Value.Select(Gh3QbText.Key).ToArray());
        if (bank.PrefixValues.Values.SelectMany(members => members).Any(id => !bank.NodeValues.ContainsKey(id))) throw new InvalidDataException("Node prefix member is missing");
        if (bank.LightVolumes.Count > 1024 || bank.LightVolumes.Keys.Any(id => !bank.NodeValues.ContainsKey(Gh3QbText.Key(id))))
            throw new InvalidDataException("Invalid script light volume bindings");
        foreach (var value in bank.Values.Values.Where(value => value.Type == 7)) _ = new Gh3ScriptCode(value);
        foreach (var script in bank.Startup)
            if (!bank.Values.TryGetValue(Gh3QbText.Key(script), out var value) || value.Type != 7)
                throw new InvalidDataException("Stage startup script is missing: " + script);
        if (bank.PerformanceChanged.Length > 0 && (!bank.Values.TryGetValue(Gh3QbText.Key(bank.PerformanceChanged), out var performance) || performance.Type != 7))
            throw new InvalidDataException("Stage performance script is missing");
        return bank;
    }
}
