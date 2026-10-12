using System.Buffers.Binary;
using System.Text;

namespace Gh3Skin;

internal sealed record Gh3ScriptToken(int Offset, byte Op, Gh3QbValue? Value = null, int[]? Targets = null, ushort[]? Weights = null);

// A compiled QB script: the LZSS expansion and the token list with resolved jump targets.
internal sealed class Gh3ScriptCode
{
    // Reader limits against corrupt data; GH3's scripts are far below them.
    private const int MaximumTokens = 1000000;
    private const int MaximumRandomBranches = 10000;
    private const int MaximumScriptLength = 16000000;

    public byte[] Bytes { get; }
    public Gh3ScriptToken[] Tokens { get; }
    public IReadOnlyDictionary<int, int> Offsets { get; }

    public Gh3ScriptCode(Gh3QbValue value)
    {
        if (value.Type != Gh3QbType.Script || value.Data is not ReadOnlyMemory<byte> data) throw new InvalidDataException("Expected compiled QB script data");
        Bytes = Expand(data.Span);
        var tokens = new List<Gh3ScriptToken>();
        var at = 0;
        uint Word() { Need(4); var word = BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(at, 4)); at += 4; return word; }
        ushort Short() { Need(2); var word = BinaryPrimitives.ReadUInt16LittleEndian(Bytes.AsSpan(at, 2)); at += 2; return word; }
        void Need(int count) { if (count < 0 || at > Bytes.Length - count) throw new InvalidDataException("Truncated QB script token"); }
        float Float() { var number = BitConverter.Int32BitsToSingle(unchecked((int)Word())); return float.IsFinite(number) ? number : throw new InvalidDataException("Non-finite QB script number"); }
        while (at < Bytes.Length)
        {
            if (tokens.Count >= MaximumTokens) throw new InvalidDataException("QB script token count exceeds its limit");
            var offset = at;
            var op = Bytes[at++];
            Gh3QbValue? literal = null;
            int[]? targets = null;
            ushort[]? weights = null;
            switch (op)
            {
                case Gh3ScriptOp.EndOfLineNumber: Word(); op = Gh3ScriptOp.EndOfLine; break;
                case Gh3ScriptOp.Name: literal = new(Gh3QbType.Checksum, offset, Word()); break;
                case Gh3ScriptOp.Integer: case Gh3ScriptOp.HexInteger: literal = new(Gh3QbType.Integer, offset, Word()); break;
                case Gh3ScriptOp.ByteInteger: Need(1); literal = new(Gh3QbType.Integer, offset, unchecked((uint)(sbyte)Bytes[at++])); break;
                case Gh3ScriptOp.Float: literal = new(Gh3QbType.Float, offset, Float()); break;
                case Gh3ScriptOp.String: case Gh3ScriptOp.LocalString: case Gh3ScriptOp.WideString:
                    var length = checked((int)Word());
                    Need(length);
                    var width = op == Gh3ScriptOp.WideString ? 2 : 1;
                    if (length < width || length % width != 0 || Bytes[at + length - 1] != 0 || width == 2 && Bytes[at + length - 2] != 0)
                        throw new InvalidDataException("Invalid QB script string");
                    literal = new(width == 1 ? Gh3QbType.String : Gh3QbType.WideString, offset, (width == 1 ? Encoding.Latin1 : Encoding.BigEndianUnicode).GetString(Bytes, at, length - width));
                    at += length;
                    break;
                case Gh3ScriptOp.Vector: literal = new(Gh3QbType.Vector, offset, new[] { Float(), Float(), Float() }); break;
                case Gh3ScriptOp.Pair: literal = new(Gh3QbType.Pair, offset, new[] { Float(), Float() }); break;
                case Gh3ScriptOp.Jump:
                    var jump = unchecked((int)Word());
                    targets = new[] { checked(at + jump) };
                    break;
                case Gh3ScriptOp.Random: case Gh3ScriptOp.Random2: case Gh3ScriptOp.RandomNoRepeat: case Gh3ScriptOp.RandomPermute:
                    var count = checked((int)Word());
                    if (count is < 1 or > MaximumRandomBranches) throw new InvalidDataException("Invalid QB random branch count");
                    weights = Enumerable.Range(0, count).Select(_ => Short()).ToArray();
                    targets = Enumerable.Range(0, count).Select(_ => { var delta = Word(); return checked(at + (int)delta); }).ToArray();
                    break;
                case Gh3ScriptOp.FastIf: case Gh3ScriptOp.FastElse: case Gh3ScriptOp.ShortBreak:
                    var origin = at;
                    targets = new[] { checked(origin + Short()) };
                    break;
                case Gh3ScriptOp.ElseIf:
                    var first = at;
                    var falseTarget = checked(first + Short());
                    var second = at;
                    targets = new[] { falseTarget, checked(second + Short()) };
                    break;
                case Gh3ScriptOp.InlineStructure:
                    var size = Short();
                    at = (at + 3) & ~3;
                    Need(size);
                    literal = Gh3Qb.InlineStructure(Bytes.AsMemory(at, size));
                    at += size;
                    at = (at + 3) & ~3;
                    break;
                case Gh3ScriptOp.StringChecksum: literal = new(Gh3QbType.StringChecksum, offset, Word()); break;
                // Tokens without a payload.
                case <= Gh3ScriptOp.GreaterThanEqual: case Gh3ScriptOp.Begin: case Gh3ScriptOp.Repeat: case Gh3ScriptOp.Break:
                case Gh3ScriptOp.Script: case Gh3ScriptOp.EndScript: case Gh3ScriptOp.If: case Gh3ScriptOp.Else: case Gh3ScriptOp.EndIf:
                case Gh3ScriptOp.Return: case Gh3ScriptOp.Undefined: case Gh3ScriptOp.ChecksumName: case Gh3ScriptOp.AllArguments:
                case Gh3ScriptOp.Argument: case Gh3ScriptOp.RandomRange: case Gh3ScriptOp.At: case Gh3ScriptOp.Or: case Gh3ScriptOp.And:
                case Gh3ScriptOp.Xor: case Gh3ScriptOp.ShiftLeft: case Gh3ScriptOp.ShiftRight: case Gh3ScriptOp.RandomRange2:
                case Gh3ScriptOp.Not: case Gh3ScriptOp.KeywordAnd: case Gh3ScriptOp.KeywordOr: case Gh3ScriptOp.Switch:
                case Gh3ScriptOp.EndSwitch: case Gh3ScriptOp.Case: case Gh3ScriptOp.Default: case Gh3ScriptOp.Colon:
                case Gh3ScriptOp.RuntimeFunction: case Gh3ScriptOp.RuntimeMemberFunction: case Gh3ScriptOp.Reserved45:
                case Gh3ScriptOp.Reserved46: case Gh3ScriptOp.GlobalReference: case Gh3ScriptOp.NotEqual: case Gh3ScriptOp.RandomFloat:
                case Gh3ScriptOp.RandomInteger: case Gh3ScriptOp.Reserved51: case Gh3ScriptOp.Reserved52: case Gh3ScriptOp.Reserved53:
                case Gh3ScriptOp.Reserved54: case Gh3ScriptOp.Reserved55: case Gh3ScriptOp.Reserved59: case Gh3ScriptOp.Reserved5A:
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
        if (data.Length < Gh3QbLayout.ScriptHeaderSize) throw new InvalidDataException("Truncated QB script header");
        var length = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
        var compressed = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8, 4));
        if (length > MaximumScriptLength || compressed != data.Length - Gh3QbLayout.ScriptHeaderSize) throw new InvalidDataException("Invalid QB script length");
        data = data.Slice(Gh3QbLayout.ScriptHeaderSize);
        if (compressed == length) return data.ToArray();
        var output = new byte[length];
        var ring = new byte[Gh3QbCompression.RingSize];
        Array.Fill(ring, (byte)' ', 0, Gh3QbCompression.RingStart);
        var write = Gh3QbCompression.RingStart;
        var at = 0;
        var count = 0;
        void Append(byte value)
        {
            if (count == output.Length) throw new InvalidDataException("QB script expands past its declared length");
            output[count++] = value;
            ring[write] = value;
            write = (write + 1) & Gh3QbCompression.RingMask;
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
                    var read = lo | (hi & Gh3QbCompression.MatchOffsetHighMask) << 4;
                    for (var i = 0; i < (hi & Gh3QbCompression.MatchLengthMask) + Gh3QbCompression.MinimumMatch; i++) Append(ring[(read + i) & Gh3QbCompression.RingMask]);
                }
        }
        if (count != output.Length) throw new InvalidDataException("QB script expansion length differs");
        return output;
    }
}
