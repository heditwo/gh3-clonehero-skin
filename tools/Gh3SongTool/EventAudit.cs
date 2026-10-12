using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Gh3Skin;

internal static class EventAudit
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private sealed record SourceFile(string Path, string Sha256, long Bytes);
    private sealed record Failure(string Source, string Error);
    private sealed record Definition(string Archive, string File, string Key, string Name, uint Type, string Sha256);
    private sealed record Mapping(string Archive, string File, string Key, string Name, JsonElement Data);
    private sealed class CommandUse
    {
        public string Key { get; init; } = "";
        public string Name { get; init; } = "";
        public int Calls { get; set; }
        public SortedSet<string> Songs { get; } = new();
        public SortedSet<string> Tracks { get; } = new();
        public SortedSet<string> Parameters { get; } = new();
        public SortedSet<string> Flags { get; } = new();
        public SortedDictionary<string, int> Playback { get; } = new();
        public SortedSet<string> Gaps { get; } = new();
    }

    public static int Run(string[] args)
    {
        if (args.Length < 3) throw new ArgumentException("audit <GH3 DATA> <new output directory> [checksum files...]");
        var root = Path.GetFullPath(args[1]);
        var output = Path.GetFullPath(args[2]);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new ArgumentException("Use an empty audit output directory.");
        Directory.CreateDirectory(Path.Combine(output, "songs"));
        var names = new Dictionary<uint, string>();
        foreach (var file in args.Skip(3))
            foreach (var line in File.ReadLines(file))
            {
                var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[0].StartsWith("0x") &&
                    uint.TryParse(parts[0].AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var key) &&
                    Gh3Checksum.Value(parts[1].Trim()) == key) names.TryAdd(key, parts[1].Trim());
            }
        string Name(uint key) => names.GetValueOrDefault(key, Gh3QbText.Key(key));
        var sources = new List<SourceFile>();
        var failures = new List<Failure>();
        var commands = new Dictionary<uint, CommandUse>();
        var notes = new SortedDictionary<string, SortedDictionary<uint, int>>();
        var definitions = new List<Definition>();
        var mappings = new List<Mapping>();
        var songs = 0;
        var sections = 0;
        var roundTrips = 0;
        byte[] Read(string path)
        {
            var bytes = File.ReadAllBytes(path);
            sources.Add(new(Path.GetRelativePath(root, path), Hash(bytes), bytes.Length));
            return bytes;
        }
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "SONGS"), "*_song.pak.xen").OrderBy(path => path, StringComparer.Ordinal))
        {
            try
            {
                var song = new Gh3Song(Read(path), Path.GetFileName(path)[..^13]);
                var data = new List<object>();
                foreach (var (key, value) in song.Chart.Sections)
                {
                    var text = Gh3QbText.Encode(value);
                    if (Gh3QbText.Encode(Gh3QbText.Decode(text)) != text)
                        throw new InvalidDataException($"Typed round trip changed section {Gh3QbText.Key(key)}.");
                    roundTrips++;
                    data.Add(new { key = Gh3QbText.Key(key), name = Name(key), value = JsonSerializer.Deserialize<JsonElement>(text) });
                }
                File.WriteAllText(Path.Combine(output, "songs", song.Id + ".json"), JsonSerializer.Serialize(new { song = song.Id, sections = data }, Json));
                foreach (var track in Gh3Song.Tracks)
                {
                    if (!notes.TryGetValue(track, out var pitches)) notes.Add(track, pitches = new());
                    foreach (var note in song.Notes(track)) pitches[note.Note] = pitches.GetValueOrDefault(note.Note) + 1;
                }
                foreach (var call in song.ScriptEvents())
                {
                    var result = Gh3CommandDispatch.Resolve(call);
                    if (!commands.TryGetValue(call.Command, out var use))
                        commands.Add(call.Command, use = new() { Key = Gh3QbText.Key(call.Command), Name = Name(call.Command) });
                    use.Calls++;
                    use.Songs.Add(song.Id);
                    use.Tracks.Add(call.Track);
                    use.Playback[result.Status] = use.Playback.GetValueOrDefault(result.Status) + 1;
                    foreach (var gap in result.Gaps) use.Gaps.Add(gap);
                    foreach (var field in call.Parameters?.Fields ?? Array.Empty<Gh3QbField>())
                        if (field.Name == 0) use.Flags.Add(Gh3QbText.Encode(field.Value, Name));
                        else use.Parameters.Add(Name(field.Name) + ":" + field.Value.Type);
                }
                sections += song.Chart.Sections.Count;
                songs++;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException)
            {
                failures.Add(new(Path.GetRelativePath(root, path), error.Message));
            }
        }
        var archives = new[] { "PAK/qb.pak.xen" }.Concat(new[] { "party", "dive", "video", "artdeco", "prison", "wikker", "budokan", "hell", "soundcheck", "credits" }
            .Select(venue => $"ZONES/z_{venue}.pak.xen"));
        foreach (var relative in archives)
        {
            try
            {
                var path = Path.Combine(root, relative);
                var bytes = Read(path);
                var pab = path.Replace(".pak.", ".pab.", StringComparison.Ordinal);
                if (File.Exists(pab)) bytes = bytes.Concat(Read(pab)).ToArray();
                var archive = new Gh3Pak(bytes);
                foreach (var entry in archive.Entries.Where(entry => entry.Type == Gh3Checksum.Value(".qb")))
                {
                    try
                    {
                        var qb = new Gh3Qb(archive.Data(entry));
                        foreach (var (key, value) in qb.Sections)
                        {
                            var name = Name(key);
                            if (value.Type == 7)
                                definitions.Add(new(relative, Name(entry.Name), Gh3QbText.Key(key), name, value.Type, Hash(((ReadOnlyMemory<byte>)value.Data).Span)));
                            else if (name.Contains("notemapping", StringComparison.OrdinalIgnoreCase) || ContainsNoteMap(value))
                                mappings.Add(new(relative, Name(entry.Name), Gh3QbText.Key(key), name,
                                    JsonSerializer.Deserialize<JsonElement>(Gh3QbText.Encode(value, Name))));
                        }
                    }
                    catch (InvalidDataException error) { failures.Add(new(relative + ":" + Name(entry.Name), error.Message)); }
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException)
            {
                failures.Add(new(relative, error.Message));
            }
        }
        var summary = new
        {
            songs, sections, typedRoundTrips = roundTrips, commands = commands.Count, calls = commands.Values.Sum(use => use.Calls),
            scriptDefinitions = definitions.Count, noteMaps = mappings.Count, readFailures = failures.Count,
            fullPlaybackCoverage = false,
            scope = "Installed song arrays, global QB script definitions, note-map data, and ten venue archives. Script bodies and native engine functions still require behavior tests."
        };
        File.WriteAllText(Path.Combine(output, "inventory.json"), JsonSerializer.Serialize(new
        {
            version = 1, summary, sources, failures, commands = commands.Values.OrderBy(use => use.Key, StringComparer.Ordinal), notes,
            mappings, definitions
        }, Json));
        Console.WriteLine(JsonSerializer.Serialize(summary, Json));
        return failures.Count == 0 ? 0 : 1;
    }

    private static bool ContainsNoteMap(Gh3QbValue value)
    {
        if (value.Data is IReadOnlyList<Gh3QbValue> items) return items.Any(ContainsNoteMap);
        if (value.Data is IReadOnlyList<Gh3QbField> fields)
            return fields.Any(field => field.Name == Gh3Checksum.Value("MidiNote")) && fields.Any(field => field.Name == Gh3Checksum.Value("Scr")) ||
                fields.Any(field => ContainsNoteMap(field.Value));
        return false;
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
