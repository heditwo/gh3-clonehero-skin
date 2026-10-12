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
    public Gh3QbValue? Field(uint name)
    {
        var fields = Fields;
        for (var i = 0; i < fields.Count; i++)
            if (fields[i].Name == name) return fields[i].Value;
        return null;
    }

    public bool Flag(string name)
    {
        var fields = Fields;
        var key = Gh3Checksum.Value(name);
        for (var i = 0; i < fields.Count; i++)
            if (fields[i].Name == 0 && fields[i].Value.Data is uint value && value == key) return true;
        return false;
    }
}

// Reader of a GH3 PC QB file: the top-level sections and their typed values.
internal sealed class Gh3Qb
{
    // Reader limits against corrupt data; GH3's files are far below them.
    private const int MaximumDepth = 64;
    private const int MaximumValues = 2000000;
    private const int MaximumArrayItems = 1000000;
    private const int MaximumFields = 10000;

    private readonly ReadOnlyMemory<byte> _data;
    private readonly int _minimumPointer = Gh3QbLayout.FileHeaderSize;
    private int _values;
    public IReadOnlyDictionary<uint, Gh3QbValue> Sections { get; }

    public Gh3Qb(ReadOnlyMemory<byte> data)
    {
        _data = data;
        if (data.Length < Gh3QbLayout.FileHeaderSize || Word(4) != data.Length || Word(8) != Gh3QbLayout.FileMagic)
            throw new InvalidDataException("Expected a GH3 PC QB file.");
        var sections = new Dictionary<uint, Gh3QbValue>();
        var cursor = Gh3QbLayout.FileHeaderSize;
        while (cursor < data.Length)
        {
            Range(cursor, Gh3QbLayout.SectionHeaderSize);
            var kind = Word(cursor);
            if ((kind & Gh3QbLayout.SectionKindMask) != Gh3QbLayout.SectionKind)
                throw new InvalidDataException($"Unsupported QB section type {kind:x8} at {cursor}.");
            var end = cursor + Gh3QbLayout.SectionHeaderSize;
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
        _minimumPointer = inline ? 0 : Gh3QbLayout.FileHeaderSize;
        Sections = new Dictionary<uint, Gh3QbValue>();
    }

    public static Gh3QbValue InlineStructure(ReadOnlyMemory<byte> data)
    {
        var reader = new Gh3Qb(data, true);
        var end = 0;
        return new(Gh3QbType.Structure, -1, reader.Structure(0, ref end, 0));
    }

    private Gh3QbValue Value(uint type, int offset, ref int end, int depth)
    {
        if (depth > MaximumDepth || ++_values > MaximumValues) throw new InvalidDataException("The QB data exceeds the reader limit.");
        var raw = Word(offset);
        object data;
        switch (type)
        {
            case Gh3QbType.Integer: case Gh3QbType.Checksum: case Gh3QbType.GlobalReference: case Gh3QbType.StringChecksum:
                data = raw;
                break;
            case Gh3QbType.Float:
                var number = BitConverter.Int32BitsToSingle(unchecked((int)raw));
                if (!float.IsFinite(number)) throw new InvalidDataException("Non-finite QB number.");
                data = number;
                break;
            case Gh3QbType.String: case Gh3QbType.WideString:
                var width = type == Gh3QbType.WideString ? 2 : 1;
                var start = Position(raw, width);
                var stop = start;
                while (true)
                {
                    Range(stop, width);
                    if (_data.Span[stop] == 0 && (width == 1 || _data.Span[stop + 1] == 0)) break;
                    stop += width;
                }
                data = (type == Gh3QbType.WideString ? Encoding.BigEndianUnicode : Encoding.Latin1).GetString(_data.Span.Slice(start, stop - start));
                end = Math.Max(end, Align(stop + width));
                break;
            case Gh3QbType.Pair: case Gh3QbType.Vector:
                var vector = Pointer(offset);
                var length = type == Gh3QbType.Pair ? 2 : 3;
                Range(vector, 4 + length * 4);
                data = Enumerable.Range(0, length).Select(i => BitConverter.Int32BitsToSingle(unchecked((int)Word(vector + 4 + i * 4)))).ToArray();
                end = Math.Max(end, vector + 4 + length * 4);
                break;
            case Gh3QbType.Script:
                var script = Pointer(offset);
                Range(script, Gh3QbLayout.ScriptHeaderSize);
                var size = Count(script + 8);
                Range(script + Gh3QbLayout.ScriptHeaderSize, size);
                data = _data.Slice(script, Gh3QbLayout.ScriptHeaderSize + size);
                end = Math.Max(end, Align(script + Gh3QbLayout.ScriptHeaderSize + size));
                break;
            case Gh3QbType.Structure:
                data = Structure(Pointer(offset), ref end, depth + 1);
                break;
            case Gh3QbType.Array:
                data = Array(Pointer(offset), ref end, depth + 1);
                break;
            default:
                throw new InvalidDataException($"Unsupported QB value type {type:x2} at {offset}.");
        }
        return new(type, offset, data);
    }

    private IReadOnlyList<Gh3QbValue> Array(int offset, ref int end, int depth)
    {
        Range(offset, Gh3QbLayout.ArrayHeaderSize);
        var kind = Word(offset);
        if (kind == Gh3QbLayout.EmptyArray)
        {
            end = Math.Max(end, offset + Gh3QbLayout.ArrayHeaderSize);
            return System.Array.Empty<Gh3QbValue>();
        }
        if ((kind & Gh3QbLayout.ArrayKindMask) != Gh3QbLayout.ArrayKind) throw new InvalidDataException("Invalid QB array type.");
        var count = Count(offset + 4);
        if (count > MaximumArrayItems) throw new InvalidDataException("The QB array exceeds the reader limit.");
        var start = count > 1 ? Pointer(offset + 8) : offset + 8;
        Range(start, count * 4);
        end = Math.Max(end, Math.Max(offset + Gh3QbLayout.ArrayHeaderSize, start + count * 4));
        var rows = new Gh3QbValue[count];
        for (var i = 0; i < count; i++) rows[i] = Value((kind >> 8) & 255, start + i * 4, ref end, depth);
        return System.Array.AsReadOnly(rows);
    }

    private IReadOnlyList<Gh3QbField> Structure(int offset, ref int end, int depth)
    {
        Range(offset, Gh3QbLayout.StructureHeaderSize);
        if (Word(offset) != Gh3QbLayout.StructureHeader) throw new InvalidDataException("Invalid QB structure.");
        end = Math.Max(end, offset + Gh3QbLayout.StructureHeaderSize);
        var next = Word(offset + 4);
        var seen = new HashSet<uint>();
        var fields = new List<Gh3QbField>();
        while (next != 0)
        {
            if (!seen.Add(next) || fields.Count >= MaximumFields) throw new InvalidDataException("Invalid QB structure links.");
            var cursor = Position(next);
            Range(cursor, Gh3QbLayout.FieldSize);
            var kind = Word(cursor);
            if ((kind & Gh3QbLayout.FieldKindMask) != 0 || (kind & Gh3QbLayout.FieldFlag) == 0)
                throw new InvalidDataException($"Unsupported QB structure field {kind:x8}.");
            end = Math.Max(end, cursor + Gh3QbLayout.FieldSize);
            fields.Add(new(Word(cursor + 4), Value((kind >> 16) & Gh3QbLayout.FieldTypeMask, cursor + 8, ref end, depth)));
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
