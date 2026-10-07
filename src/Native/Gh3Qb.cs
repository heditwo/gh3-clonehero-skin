using System.Buffers.Binary;
using System.Text;

namespace Gh3Skin;

internal sealed record Gh3QbField(uint Name, Gh3QbValue Value);

internal sealed record Gh3QbValue(uint Type, int Offset, object Data)
{
    public uint Integer => Data is uint value ? value : throw new InvalidDataException("Expected a QB integer or checksum.");
    public double Number => Data is float value ? value : Integer;
    public IReadOnlyList<Gh3QbValue> Items => Data as IReadOnlyList<Gh3QbValue> ?? throw new InvalidDataException("Expected a QB array.");
    public IReadOnlyList<Gh3QbField> Fields => Data as IReadOnlyList<Gh3QbField> ?? throw new InvalidDataException("Expected a QB structure.");
    public Gh3QbValue? Field(string name) => Field(Gh3Checksum.Value(name));
    public Gh3QbValue? Field(uint name) => Fields.FirstOrDefault(field => field.Name == name)?.Value;
    public bool Flag(string name) => Fields.Any(field => field.Name == 0 && field.Value.Data is uint value && value == Gh3Checksum.Value(name));
}

internal sealed class Gh3Qb
{
    private readonly ReadOnlyMemory<byte> _data;
    private readonly int _minimumPointer = 28;
    private int _values;
    public IReadOnlyDictionary<uint, Gh3QbValue> Sections { get; }

    public Gh3Qb(ReadOnlyMemory<byte> data)
    {
        _data = data;
        if (data.Length < 28 || Word(4) != data.Length || Word(8) != 0x1c080204)
            throw new InvalidDataException("Expected a GH3 PC QB file.");
        var sections = new Dictionary<uint, Gh3QbValue>();
        var cursor = 28;
        while (cursor < data.Length)
        {
            Range(cursor, 20);
            var kind = Word(cursor);
            if ((kind & 0xffff00ff) != 0x00200000)
                throw new InvalidDataException($"Unsupported QB section type {kind:x8} at {cursor}.");
            var end = cursor + 20;
            var value = Value((kind >> 8) & 255, cursor + 12, ref end, 0);
            if (!sections.TryAdd(Word(cursor + 4), value)) throw new InvalidDataException("Duplicate QB section.");
            cursor = Align(end);
        }
        if (cursor != data.Length) throw new InvalidDataException("Invalid QB section padding.");
        Sections = sections;
    }

    public Gh3QbValue? Section(string name) => Sections.GetValueOrDefault(Gh3Checksum.Value(name));

    private Gh3Qb(ReadOnlyMemory<byte> data, bool inline)
    {
        _data = data;
        _minimumPointer = inline ? 0 : 28;
        Sections = new Dictionary<uint, Gh3QbValue>();
    }

    public static Gh3QbValue InlineStructure(ReadOnlyMemory<byte> data)
    {
        var reader = new Gh3Qb(data, true);
        var end = 0;
        return new(10, -1, reader.Structure(0, ref end, 0));
    }

    private Gh3QbValue Value(uint type, int offset, ref int end, int depth)
    {
        if (depth > 64 || ++_values > 2000000) throw new InvalidDataException("The QB data exceeds the reader limit.");
        var raw = Word(offset);
        object data;
        switch (type)
        {
            case 1: case 13: case 26: case 28:
                data = raw;
                break;
            case 2:
                var number = BitConverter.Int32BitsToSingle(unchecked((int)raw));
                if (!float.IsFinite(number)) throw new InvalidDataException("Non-finite QB number.");
                data = number;
                break;
            case 3: case 4:
                var width = type == 4 ? 2 : 1;
                var start = Position(raw, width);
                var stop = start;
                while (true)
                {
                    Range(stop, width);
                    if (_data.Span[stop] == 0 && (width == 1 || _data.Span[stop + 1] == 0)) break;
                    stop += width;
                }
                data = (type == 4 ? Encoding.BigEndianUnicode : Encoding.Latin1).GetString(_data.Span.Slice(start, stop - start));
                end = Math.Max(end, Align(stop + width));
                break;
            case 5: case 6:
                var vector = Pointer(offset);
                var length = type == 5 ? 2 : 3;
                Range(vector, 4 + length * 4);
                data = Enumerable.Range(0, length).Select(i => BitConverter.Int32BitsToSingle(unchecked((int)Word(vector + 4 + i * 4)))).ToArray();
                end = Math.Max(end, vector + 4 + length * 4);
                break;
            case 7:
                var script = Pointer(offset);
                Range(script, 12);
                var size = Count(script + 8);
                Range(script + 12, size);
                data = _data.Slice(script, 12 + size);
                end = Math.Max(end, Align(script + 12 + size));
                break;
            case 10:
                data = Structure(Pointer(offset), ref end, depth + 1);
                break;
            case 12:
                data = Array(Pointer(offset), ref end, depth + 1);
                break;
            default:
                throw new InvalidDataException($"Unsupported QB value type {type:x2} at {offset}.");
        }
        return new(type, offset, data);
    }

    private IReadOnlyList<Gh3QbValue> Array(int offset, ref int end, int depth)
    {
        Range(offset, 12);
        var kind = Word(offset);
        if (kind == 0x10000)
        {
            end = Math.Max(end, offset + 12);
            return System.Array.Empty<Gh3QbValue>();
        }
        if ((kind & 0xffff00ff) != 0x00010000) throw new InvalidDataException("Invalid QB array type.");
        var count = Count(offset + 4);
        if (count > 1000000) throw new InvalidDataException("The QB array exceeds the reader limit.");
        var start = count > 1 ? Pointer(offset + 8) : offset + 8;
        Range(start, count * 4);
        end = Math.Max(end, Math.Max(offset + 12, start + count * 4));
        var rows = new Gh3QbValue[count];
        for (var i = 0; i < count; i++) rows[i] = Value((kind >> 8) & 255, start + i * 4, ref end, depth);
        return System.Array.AsReadOnly(rows);
    }

    private IReadOnlyList<Gh3QbField> Structure(int offset, ref int end, int depth)
    {
        Range(offset, 8);
        if (Word(offset) != 0x100) throw new InvalidDataException("Invalid QB structure.");
        end = Math.Max(end, offset + 8);
        var next = Word(offset + 4);
        var seen = new HashSet<uint>();
        var fields = new List<Gh3QbField>();
        while (next != 0)
        {
            if (!seen.Add(next) || fields.Count >= 10000) throw new InvalidDataException("Invalid QB structure links.");
            var cursor = Position(next);
            Range(cursor, 16);
            var kind = Word(cursor);
            if ((kind & 0xff00ffff) != 0x00000000 || (kind & 0x00800000) == 0)
                throw new InvalidDataException($"Unsupported QB structure field {kind:x8}.");
            end = Math.Max(end, cursor + 16);
            fields.Add(new(Word(cursor + 4), Value((kind >> 16) & 0x7f, cursor + 8, ref end, depth)));
            next = Word(cursor + 12);
        }
        return fields.AsReadOnly();
    }

    private int Count(int offset)
    {
        var value = Word(offset);
        if (value > int.MaxValue) throw new InvalidDataException("Invalid QB size.");
        return (int)value;
    }
    private int Pointer(int offset) => Position(Word(offset));
    private int Position(uint value, int alignment = 4)
    {
        if (value < _minimumPointer || value > _data.Length - alignment || value % alignment != 0)
            throw new InvalidDataException($"Invalid QB pointer {value} (alignment {alignment}, file size {_data.Length}).");
        return (int)value;
    }
    private uint Word(int offset)
    {
        Range(offset, 4);
        return BinaryPrimitives.ReadUInt32BigEndian(_data.Span.Slice(offset, 4));
    }
    private void Range(int offset, int size)
    {
        if (offset < 0 || size < 0 || offset > _data.Length - size) throw new InvalidDataException("QB data is outside the file.");
    }
    private static int Align(int value) => checked((value + 3) & ~3);
}
