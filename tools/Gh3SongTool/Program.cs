using System.Security.Cryptography;
using System.Text.Json;
using Gh3Skin;

try
{
    if (args.Length > 0 && args[0] == "audit") return EventAudit.Run(args);
    if (args.Length == 3 && args[0] == "script-tokens")
    {
        var qb = new Gh3Qb(File.ReadAllBytes(args[1]));
        var script = (args[2].StartsWith("0x") ? qb.Sections.GetValueOrDefault(Convert.ToUInt32(args[2], 16)) : qb.Section(args[2]))
            ?? throw new InvalidDataException("Script was not found");
        Console.WriteLine(JsonSerializer.Serialize(new Gh3ScriptCode(script).Tokens.Select(token => new {
            token.Offset, op = token.Op.ToString("x2"), value = token.Value == null ? null : Gh3QbText.Encode(token.Value), token.Targets, token.Weights })));
        return 0;
    }
    if (args.Length >= 2 && args[0] == "script-check")
    {
        var results = args.Skip(1).SelectMany(path => new Gh3Qb(File.ReadAllBytes(path)).Sections.Where(pair => pair.Value.Type == 7).Select(pair =>
        {
            try { var code = new Gh3ScriptCode(pair.Value); return new { file = path, key = pair.Key.ToString("x8"), tokens = code.Tokens.Length, error = "" }; }
            catch (Exception error) { return new { file = path, key = pair.Key.ToString("x8"), tokens = 0, error = error.Message }; }
        })).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(results));
        return results.Any(result => result.error.Length > 0) ? 1 : 0;
    }
    if (args.Length >= 2 && args[0] is "qb-data" or "qb-references")
    {
        var data = new Dictionary<uint, Gh3QbValue>();
        foreach (var path in args.Skip(1))
            foreach (var pair in new Gh3Qb(File.ReadAllBytes(path)).Sections) data[pair.Key] = pair.Value;
        IEnumerable<uint> References(Gh3QbValue value)
        {
            if (value.Type is 13 or 26 or 28) yield return value.Integer;
            if (value.Type == 7)
                foreach (var token in new Gh3ScriptCode(value).Tokens.Where(token => token.Value != null))
                    foreach (var reference in References(token.Value!)) yield return reference;
            if (value.Type == 10)
                foreach (var field in value.Fields)
                    foreach (var reference in References(field.Value)) yield return reference;
            if (value.Type == 12)
                foreach (var item in value.Items)
                    foreach (var reference in References(item)) yield return reference;
        }
        if (args[0] == "qb-references")
            Console.WriteLine(JsonSerializer.Serialize(data.ToDictionary(pair => pair.Key.ToString("x8"), pair => References(pair.Value).Distinct().Select(key => key.ToString("x8")).ToArray())));
        else Console.WriteLine(JsonSerializer.Serialize(data.ToDictionary(pair => pair.Key.ToString("x8"),
            pair => JsonDocument.Parse(Gh3QbText.Encode(pair.Value)).RootElement.Clone())));
        return 0;
    }
    if (args.Length == 2 && args[0] is "events" or "events-stdin")
    {
        Gh3Song song;
        if (args[0] == "events-stdin")
        {
            using var input = Console.OpenStandardInput();
            using var bytes = new MemoryStream();
            var buffer = new byte[65536];
            int count;
            while ((count = input.Read(buffer)) > 0)
            {
                if (bytes.Length + count > 256 * 1024 * 1024) throw new InvalidDataException("The song PAK exceeds 256 MiB.");
                bytes.Write(buffer, 0, count);
            }
            song = new(bytes.ToArray(), args[1]);
        }
        else song = Gh3Song.Load(args[1]);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            version = 1, offsetSeconds = 0, sourceSong = song.Id, sourceSha256 = Hash(song.Pak.Copy()),
            events = Gh3SongEvents.Read(song),
            playback = song.ScriptEvents().Select(call => new
            {
                track = call.Track, time = call.Time, command = Gh3QbText.Key(call.Command), result = Gh3CommandDispatch.Resolve(call)
            })
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return 0;
    }
    if (args.Length is 3 or 4 && args[0] == "chart")
    {
        var song = Gh3Song.Load(args[1]);
        var difficulty = args.Length == 4 ? args[3] : "expert";
        File.WriteAllText(args[2], Gh3ChartExport.Write(song, difficulty));
        Console.WriteLine($"Wrote {args[2]} from {song.Id} ({difficulty})");
        return 0;
    }
    if (args.Length != 2 || args[0] != "inspect")
        throw new ArgumentException("Usage: inspect <song_song.pak.xen> | events <song_song.pak.xen> | chart <song_song.pak.xen> <notes.chart> [difficulty] | audit <DATA> <new output directory> [checksum files...]");
    var source = Gh3Song.Load(args[1]);
    var report = new
    {
        song = source.Id, sha256 = Hash(source.Pak.Copy()), entries = source.Pak.Entries.Count,
        tracks = Gh3Song.Tracks.ToDictionary(name => name, name => new { notes = source.Notes(name).Length, scripts = source.Array(name).Count }),
        cameras = source.Notes("cameras").Select((note, index) => new { index, note.Time, note.Note, note.Length, note.Velocity })
    };
    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or FormatException or OverflowException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
