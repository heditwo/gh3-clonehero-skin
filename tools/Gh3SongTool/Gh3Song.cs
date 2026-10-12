namespace Gh3Skin;

internal sealed record Gh3SongNote(uint Time, uint Note, uint Length, uint? Velocity);

// A GH3 song PAK (<song>_song.pak.xen) of the GH3 DATA folder: its chart QB and the direction note tracks
// (PARITY-NOTES.md#native-song-data). Only the pipeline reads it; the mod does not read GH3 files.
internal sealed class Gh3Song
{
    private const int LastAscii = 127;
    private const long MaximumPakBytes = 256 * 1024 * 1024;
    public static readonly string[] Tracks = { "scripts", "anim", "triggers", "cameras", "lightshow", "crowd", "drums", "performance" };
    public Gh3Pak Pak { get; }
    public Gh3Qb Chart { get; }
    public string Id { get; }

    public Gh3Song(byte[] bytes, string id)
    {
        if (id.Length == 0 || id.Any(c => c > LastAscii || !char.IsLetterOrDigit(c) && c != '_'))
            throw new InvalidDataException("Invalid GH3 song ID.");
        Id = id.ToLowerInvariant();
        Pak = new(bytes);
        var charts = Pak.Entries.Where(entry => entry.Type == Gh3Checksum.Value(".qb"))
            .Select(entry => new Gh3Qb(Pak.Data(entry)))
            .Where(qb => qb.Section(Id + "_fretbars") != null).ToArray();
        if (charts.Length != 1) throw new InvalidDataException($"Expected one chart for '{Id}'; found {charts.Length}.");
        Chart = charts[0];
    }

    public static Gh3Song Load(string path)
    {
        const string suffix = "_song.pak.xen";
        var filename = Path.GetFileName(path);
        if (!filename.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Keep the GH3 filename: <song>_song.pak.xen.");
        if (new FileInfo(path).Length > MaximumPakBytes) throw new InvalidDataException("The song PAK exceeds 256 MiB.");
        return new(File.ReadAllBytes(path), filename[..^suffix.Length]);
    }

    public IReadOnlyList<Gh3QbValue> Array(string section) => Chart.Section(Id + "_" + section)?.Items ?? System.Array.Empty<Gh3QbValue>();

    public Gh3SongNote[] Notes(string section) => Array(section + "_notes").Select(row =>
    {
        var values = row.Items;
        if (values.Count is not (3 or 4) || values.Any(value => value.Type != Gh3QbType.Integer) ||
            values[0].Integer > Gh3Limits.MaximumMilliseconds || values[1].Integer > Gh3Limits.MaximumMidiNote || values[2].Integer > Gh3Limits.MaximumMilliseconds ||
            values.Count == 4 && values[3].Integer > Gh3Limits.MaximumMidiNote)
            throw new InvalidDataException($"Invalid {section} note.");
        return new Gh3SongNote(values[0].Integer, values[1].Integer, values[2].Integer, values.Count == 4 ? values[3].Integer : null);
    }).ToArray();

    public IEnumerable<Gh3ScriptEvent> ScriptEvents()
    {
        foreach (var track in Tracks)
            foreach (var row in Array(track)) yield return new(track, row);
    }
}
