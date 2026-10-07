using System.Buffers.Binary;
using System.Text;

namespace Gh3Skin;

internal sealed record Gh3ScriptToken(int Offset, byte Op, Gh3QbValue? Value = null, int[]? Targets = null, ushort[]? Weights = null);

internal sealed class Gh3ScriptCode
{
    public byte[] Bytes { get; }
    public Gh3ScriptToken[] Tokens { get; }
    public IReadOnlyDictionary<int, int> Offsets { get; }

    public Gh3ScriptCode(Gh3QbValue value)
    {
        if (value.Type != 7 || value.Data is not ReadOnlyMemory<byte> data) throw new InvalidDataException("Expected compiled QB script data");
        Bytes = Expand(data.Span);
        var tokens = new List<Gh3ScriptToken>();
        var at = 0;
        uint Word() { Need(4); var word = BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(at, 4)); at += 4; return word; }
        ushort Short() { Need(2); var word = BinaryPrimitives.ReadUInt16LittleEndian(Bytes.AsSpan(at, 2)); at += 2; return word; }
        void Need(int count) { if (count < 0 || at > Bytes.Length - count) throw new InvalidDataException("Truncated QB script token"); }
        float Float() { var number = BitConverter.Int32BitsToSingle(unchecked((int)Word())); return float.IsFinite(number) ? number : throw new InvalidDataException("Non-finite QB script number"); }
        while (at < Bytes.Length)
        {
            if (tokens.Count >= 1000000) throw new InvalidDataException("QB script token count exceeds its limit");
            var offset = at;
            var op = Bytes[at++];
            Gh3QbValue? literal = null;
            int[]? targets = null;
            ushort[]? weights = null;
            switch (op)
            {
                case 0x02: Word(); op = 1; break;
                case 0x16: literal = new(13, offset, Word()); break;
                case 0x17: case 0x18: literal = new(1, offset, Word()); break;
                case 0x19: Need(1); literal = new(1, offset, unchecked((uint)(sbyte)Bytes[at++])); break;
                case 0x1a: literal = new(2, offset, Float()); break;
                case 0x1b: case 0x1c: case 0x4c:
                    var length = checked((int)Word());
                    Need(length);
                    var width = op == 0x4c ? 2 : 1;
                    if (length < width || length % width != 0 || Bytes[at + length - 1] != 0 || width == 2 && Bytes[at + length - 2] != 0)
                        throw new InvalidDataException("Invalid QB script string");
                    literal = new(width == 1 ? 3u : 4u, offset, (width == 1 ? Encoding.Latin1 : Encoding.BigEndianUnicode).GetString(Bytes, at, length - width));
                    at += length;
                    break;
                case 0x1e: literal = new(6, offset, new[] { Float(), Float(), Float() }); break;
                case 0x1f: literal = new(5, offset, new[] { Float(), Float() }); break;
                case 0x2e:
                    var jump = unchecked((int)Word());
                    targets = new[] { checked(at + jump) };
                    break;
                case 0x2f: case 0x37: case 0x40: case 0x41:
                    var count = checked((int)Word());
                    if (count is < 1 or > 10000) throw new InvalidDataException("Invalid QB random branch count");
                    weights = Enumerable.Range(0, count).Select(_ => Short()).ToArray();
                    targets = Enumerable.Range(0, count).Select(_ => { var delta = Word(); return checked(at + (int)delta); }).ToArray();
                    break;
                case 0x47: case 0x48: case 0x49:
                    var origin = at;
                    targets = new[] { checked(origin + Short()) };
                    break;
                case 0x27:
                    var first = at;
                    var falseTarget = checked(first + Short());
                    var second = at;
                    targets = new[] { falseTarget, checked(second + Short()) };
                    break;
                case 0x4a:
                    var size = Short();
                    at = (at + 3) & ~3;
                    Need(size);
                    literal = Gh3Qb.InlineStructure(Bytes.AsMemory(at, size));
                    at += size;
                    at = (at + 3) & ~3;
                    break;
                case 0x4e: literal = new(28, offset, Word()); break;
                case <= 0x15: case 0x20: case 0x21: case 0x22: case 0x23: case 0x24: case 0x25: case 0x26:
                case 0x28: case 0x29: case 0x2a: case 0x2b: case 0x2c: case 0x2d: case 0x30: case 0x31: case 0x32: case 0x33: case 0x34: case 0x35:
                case 0x36: case 0x38: case 0x39: case 0x3a: case 0x3b: case 0x3c: case 0x3d: case 0x3e: case 0x3f:
                case 0x42: case 0x43: case 0x44: case 0x45: case 0x46: case 0x4b: case 0x4d: case 0x4f: case 0x50:
                case 0x51: case 0x52: case 0x53: case 0x54: case 0x55: case 0x59: case 0x5a:
                    break;
                default: throw new InvalidDataException($"Unknown QB script opcode {op:x2} at {offset}");
            }
            tokens.Add(new(offset, op, literal, targets, weights));
        }
        Tokens = tokens.ToArray();
        var offsets = Tokens.Select((token, index) => (token.Offset, index)).ToDictionary(pair => pair.Offset, pair => pair.index);
        offsets.Add(Bytes.Length, Tokens.Length);
        foreach (var token in Tokens)
            if (token.Targets?.Any(target => !offsets.ContainsKey(target)) == true)
                throw new InvalidDataException($"QB script jump at {token.Offset} is outside a token boundary");
        Offsets = offsets;
    }

    public static byte[] Expand(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12) throw new InvalidDataException("Truncated QB script header");
        var length = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
        var compressed = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8, 4));
        if (length > 16000000 || compressed != data.Length - 12) throw new InvalidDataException("Invalid QB script length");
        data = data.Slice(12);
        if (compressed == length) return data.ToArray();
        var output = new byte[length];
        var ring = new byte[4096];
        Array.Fill(ring, (byte)' ', 0, 4078);
        var write = 4078;
        var at = 0;
        var count = 0;
        void Append(byte value)
        {
            if (count == output.Length) throw new InvalidDataException("QB script expands past its declared length");
            output[count++] = value;
            ring[write] = value;
            write = (write + 1) & 4095;
        }
        while (at < data.Length)
        {
            var flags = data[at++];
            for (var bit = 0; bit < 8 && at < data.Length; bit++)
                if ((flags & (1 << bit)) != 0) Append(data[at++]);
                else
                {
                    if (at + 2 > data.Length) throw new InvalidDataException("Truncated QB script compression reference");
                    var lo = data[at++]; var hi = data[at++];
                    var read = lo | (hi & 0xf0) << 4;
                    for (var i = 0; i < (hi & 15) + 3; i++) Append(ring[(read + i) & 4095]);
                }
        }
        if (count != output.Length) throw new InvalidDataException("QB script expansion length differs");
        return output;
    }
}
