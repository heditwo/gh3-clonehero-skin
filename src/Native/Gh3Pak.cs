using System.Buffers.Binary;

namespace Gh3Skin;

internal sealed record Gh3PakEntry(uint Type, uint Name, int Offset, int Size);

// Reader of a standalone GH3 PC PAK (a song PAK): the entry headers up to the ".last" marker.
internal sealed class Gh3Pak
{
    private const int EntryHeaderSize = 32;
    // Entry flag 0x20: a 160-byte file name follows the header.
    private const uint NamedEntryFlag = 0x20;
    private const int NamedEntryHeaderSize = 192;
    private const int FlagsField = 28;
    private const int MaximumEntries = 100000;
    private readonly byte[] _bytes;
    public IReadOnlyList<Gh3PakEntry> Entries { get; }

    public Gh3Pak(byte[] bytes)
    {
        _bytes = bytes.ToArray();
        var entries = new List<Gh3PakEntry>();
        var cursor = 0;
        while (true)
        {
            if (cursor > bytes.Length - EntryHeaderSize || entries.Count > MaximumEntries)
                throw new InvalidDataException("The GH3 PAK has no valid end marker.");
            uint Word(int field) => BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(cursor + field, 4));
            if (Word(0) == Gh3Checksum.Value(".last")) break;
            var start = cursor + (long)Word(4);
            var size = Word(8);
            if (start < cursor + EntryHeaderSize || start + size > bytes.Length)
                throw new InvalidDataException("The GH3 PAK entry is outside the file. This reader requires a standalone PC song PAK.");
            entries.Add(new(Word(0), Word(16), (int)start, (int)size));
            cursor += (Word(FlagsField) & NamedEntryFlag) != 0 ? NamedEntryHeaderSize : EntryHeaderSize;
        }
        var previousEnd = cursor + EntryHeaderSize;
        foreach (var entry in entries.OrderBy(entry => entry.Offset))
        {
            if (entry.Offset < previousEnd) throw new InvalidDataException("The GH3 PAK entries overlap.");
            previousEnd = entry.Offset + entry.Size;
        }
        Entries = entries.AsReadOnly();
    }

    public ReadOnlyMemory<byte> Data(Gh3PakEntry entry) => _bytes.AsMemory(entry.Offset, entry.Size);
    public byte[] Copy() => _bytes.ToArray();
}
