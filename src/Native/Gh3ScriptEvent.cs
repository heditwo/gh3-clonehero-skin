using System.Text.Json;

namespace Gh3Skin;

// One script call of a song direction track (time, scr, params), and its JSON form for charts ("gh3 " text events).
internal sealed record Gh3ScriptEvent(string Track, Gh3QbValue Row)
{
    private const int MaximumTextLength = 131072;
    private const int MaximumDepth = 64;
    private const int MaximumTrackName = 128;
    public static readonly string[] Tracks = { "scripts", "anim", "triggers", "cameras", "lightshow", "crowd", "drums", "performance" };
    public uint Command => Required("scr").Integer;
    public uint Time => Required("time").Integer;
    public Gh3QbValue? Parameters => Row.Field("params");

    public string Encode(Func<uint, string>? name = null) => JsonSerializer.Serialize(new
    {
        version = 1, track = Track, row = JsonSerializer.Deserialize<JsonElement>(Gh3QbText.Encode(Row, name))
    });

    public static Gh3ScriptEvent Decode(string text)
    {
        if (text.Length > MaximumTextLength) throw new InvalidDataException("The GH3 event is too large.");
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = MaximumDepth });
            var root = document.RootElement;
            if (root.GetProperty("version").GetInt32() != 1) throw new InvalidDataException("Unsupported GH3 event version.");
            var track = root.GetProperty("track").GetString();
            if (track == null || track.Length is < 1 or > MaximumTrackName) throw new InvalidDataException("Invalid GH3 event track.");
            var result = new Gh3ScriptEvent(track, Gh3QbText.Decode(root.GetProperty("row").GetRawText()));
            if (result.Required("time").Type != Gh3QbType.Integer || result.Time > Gh3Limits.MaximumMilliseconds) throw new InvalidDataException("Invalid GH3 event time.");
            if (result.Required("scr").Type != Gh3QbType.Checksum) throw new InvalidDataException("Invalid GH3 script reference.");
            _ = result.Command;
            if (result.Parameters is { } parameters) _ = parameters.Fields;
            return result;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new InvalidDataException("Invalid GH3 event.", error);
        }
    }

    public static IEnumerable<Gh3ScriptEvent> FromSong(Gh3Song song)
    {
        foreach (var track in Tracks)
            foreach (var row in song.Array(track)) yield return new(track, row);
    }

    private Gh3QbValue Required(string name) => Row.Field(name) ?? throw new InvalidDataException($"GH3 event has no {name} field.");
}
