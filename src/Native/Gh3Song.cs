using System.Buffers.Binary;

namespace Gh3Skin;

internal sealed record Gh3SongNote(uint Time, uint Note, uint Length, uint? Velocity, int NoteOffset);

internal sealed class Gh3Song
{
    public Gh3Pak Pak { get; }
    public Gh3PakEntry ChartEntry { get; }
    public Gh3Qb Chart { get; }
    public string Id { get; }

    public Gh3Song(byte[] bytes, string id)
    {
        if (id.Length == 0 || id.Any(c => c > 127 || !char.IsLetterOrDigit(c) && c != '_'))
            throw new InvalidDataException("Invalid GH3 song ID.");
        Id = id.ToLowerInvariant();
        Pak = new(bytes);
        var charts = Pak.Entries.Where(entry => entry.Type == Gh3Checksum.Value(".qb"))
            .Select(entry => (entry, qb: new Gh3Qb(Pak.Data(entry))))
            .Where(item => item.qb.Section(Id + "_fretbars") != null).ToArray();
        if (charts.Length != 1) throw new InvalidDataException($"Expected one native chart for '{Id}'; found {charts.Length}.");
        ChartEntry = charts[0].entry;
        Chart = charts[0].qb;
    }

    public static Gh3Song Load(string path)
    {
        const string suffix = "_song.pak.xen";
        var filename = Path.GetFileName(path);
        if (!filename.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Keep the native filename: <song>_song.pak.xen.");
        if (new FileInfo(path).Length > 256 * 1024 * 1024) throw new InvalidDataException("The song PAK exceeds 256 MiB.");
        return new(File.ReadAllBytes(path), filename[..^suffix.Length]);
    }

    public IReadOnlyList<Gh3QbValue> Array(string section) => Chart.Section(Id + "_" + section)?.Items ?? System.Array.Empty<Gh3QbValue>();

    public Gh3SongNote[] Notes(string section) => Array(section + "_notes").Select(row =>
    {
        var values = row.Items;
        if (values.Count is not (3 or 4) || values.Any(value => value.Type != 1) ||
            values[0].Integer > 86400000 || values[1].Integer > 127 || values[2].Integer > 86400000 ||
            values.Count == 4 && values[3].Integer > 127)
            throw new InvalidDataException($"Invalid native {section} note.");
        return new Gh3SongNote(values[0].Integer, values[1].Integer, values[2].Integer,
            values.Count == 4 ? values[3].Integer : null, ChartEntry.Offset + values[1].Offset);
    }).ToArray();

    public byte[] ChangeCameraNote(int index, uint expectedNote, uint newNote)
    {
        var notes = Notes("cameras");
        if (index < 0 || index >= notes.Length) throw new ArgumentOutOfRangeException(nameof(index));
        if (newNote > 127) throw new ArgumentOutOfRangeException(nameof(newNote));
        if (notes[index].Note != expectedNote) throw new InvalidDataException("The source camera note differs from the expected value.");
        var output = Pak.Copy();
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(notes[index].NoteOffset, 4), newNote);
        var reread = new Gh3Song(output, Id).Notes("cameras");
        if (!reread.SequenceEqual(notes.Select((note, i) => i == index ? note with { Note = newNote } : note)))
            throw new InvalidDataException("The camera edit changed another camera row.");
        return output;
    }
}
