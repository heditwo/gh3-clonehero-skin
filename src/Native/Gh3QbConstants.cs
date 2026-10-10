namespace Gh3Skin;

// GH3 PC QB format constants: value types, file and node headers, and the script token opcodes. Source: the QB files
// in DATA/PAK/qb.pab.xen and the QB readers of the community tools (QueenBee item types, NXTools script tokens); the
// names follow those tools where they agree with the GH3 behaviour that the runtime reproduces.

// The type byte of a QB value (Gh3QbValue.Type).
internal static class Gh3QbType
{
    // Not a GH3 type: the runtime's missing or deleted value.
    public const uint None = 0;
    public const uint Integer = 1;
    public const uint Float = 2;
    public const uint String = 3;
    // UTF-16 big-endian text (QueenBee "StringW", the localized strings).
    public const uint WideString = 4;
    public const uint Pair = 5;
    public const uint Vector = 6;
    public const uint Script = 7;
    public const uint Structure = 10;
    public const uint Array = 12;
    // A checksum name (QueenBee "QbKey").
    public const uint Checksum = 13;
    // A checksum that names a global value; the runtime resolves it (QueenBee "QbKeyString").
    public const uint GlobalReference = 26;
    // A checksum of a string (QueenBee "QbKeyStringQs"); script token StringChecksum.
    public const uint StringChecksum = 28;
}

// Binary layout of a GH3 PC QB file (big-endian words).
internal static class Gh3QbLayout
{
    public const int FileHeaderSize = 28;
    public const uint FileMagic = 0x1c080204;
    public const int SectionHeaderSize = 20;
    // Section word: 0x0020TT00, TT is the value type.
    public const uint SectionKindMask = 0xffff00ff;
    public const uint SectionKind = 0x00200000;
    // Array word: 0x0001TT00, TT is the item type; 0x00010000 alone is an empty array.
    public const uint ArrayKindMask = 0xffff00ff;
    public const uint ArrayKind = 0x00010000;
    public const uint EmptyArray = 0x10000;
    public const int ArrayHeaderSize = 12;
    public const uint StructureHeader = 0x100;
    public const int StructureHeaderSize = 8;
    // Structure field word: 0x00TT0000 with bit 0x00800000 set; the low seven bits of TT are the value type.
    public const uint FieldKindMask = 0xff00ffff;
    public const uint FieldFlag = 0x00800000;
    public const uint FieldTypeMask = 0x7f;
    public const int FieldSize = 16;
    // Script value: 4-byte checksum, uncompressed length, compressed length (big-endian), then LZSS data.
    public const int ScriptHeaderSize = 12;
}

// LZSS of compiled QB scripts: a 4096-byte ring buffer that starts filled with spaces, written from offset 4078
// (the classic LZSS of Okumura with N = 4096, F = 18, THRESHOLD = 2).
internal static class Gh3QbCompression
{
    public const int RingSize = 4096;
    public const int RingMask = RingSize - 1;
    public const int RingStart = 4078;
    public const int MinimumMatch = 3;
    public const int MatchLengthMask = 0x0f;
    public const int MatchOffsetHighMask = 0xf0;
}

// Opcodes of the compiled QB script tokens.
internal static class Gh3ScriptOp
{
    public const byte EndOfFile = 0x00;
    public const byte EndOfLine = 0x01;
    // End of line with a 4-byte line number; the reader turns it into EndOfLine.
    public const byte EndOfLineNumber = 0x02;
    public const byte StartStructure = 0x03;
    public const byte EndStructure = 0x04;
    public const byte StartArray = 0x05;
    public const byte EndArray = 0x06;
    public const byte EqualSign = 0x07;
    public const byte Dot = 0x08;
    public const byte Comma = 0x09;
    public const byte Minus = 0x0a;
    public const byte Add = 0x0b;
    public const byte Divide = 0x0c;
    public const byte Multiply = 0x0d;
    public const byte OpenParenthesis = 0x0e;
    public const byte CloseParenthesis = 0x0f;
    public const byte LessThan = 0x12;
    public const byte LessThanEqual = 0x13;
    public const byte GreaterThan = 0x14;
    public const byte GreaterThanEqual = 0x15;
    public const byte Name = 0x16;
    public const byte Integer = 0x17;
    public const byte HexInteger = 0x18;
    // An integer in one signed byte.
    public const byte ByteInteger = 0x19;
    public const byte Float = 0x1a;
    public const byte String = 0x1b;
    public const byte LocalString = 0x1c;
    public const byte Vector = 0x1e;
    public const byte Pair = 0x1f;
    public const byte Begin = 0x20;
    public const byte Repeat = 0x21;
    public const byte Break = 0x22;
    public const byte Script = 0x23;
    public const byte EndScript = 0x24;
    public const byte If = 0x25;
    public const byte Else = 0x26;
    public const byte ElseIf = 0x27;
    public const byte EndIf = 0x28;
    public const byte Return = 0x29;
    public const byte Undefined = 0x2a;
    public const byte ChecksumName = 0x2b;
    public const byte AllArguments = 0x2c;
    public const byte Argument = 0x2d;
    public const byte Jump = 0x2e;
    public const byte Random = 0x2f;
    public const byte RandomRange = 0x30;
    public const byte At = 0x31;
    public const byte Or = 0x32;
    public const byte And = 0x33;
    public const byte Xor = 0x34;
    public const byte ShiftLeft = 0x35;
    public const byte ShiftRight = 0x36;
    public const byte Random2 = 0x37;
    public const byte RandomRange2 = 0x38;
    public const byte Not = 0x39;
    // Second encodings of "and" and "or" (the keyword forms).
    public const byte KeywordAnd = 0x3a;
    public const byte KeywordOr = 0x3b;
    public const byte Switch = 0x3c;
    public const byte EndSwitch = 0x3d;
    public const byte Case = 0x3e;
    public const byte Default = 0x3f;
    public const byte RandomNoRepeat = 0x40;
    public const byte RandomPermute = 0x41;
    public const byte Colon = 0x42;
    public const byte RuntimeFunction = 0x43;
    public const byte RuntimeMemberFunction = 0x44;
    // Tokens without a payload and without a name in the community tools. The venue scripts do not use them.
    public const byte Reserved45 = 0x45;
    public const byte Reserved46 = 0x46;
    public const byte FastIf = 0x47;
    public const byte FastElse = 0x48;
    public const byte ShortBreak = 0x49;
    public const byte InlineStructure = 0x4a;
    public const byte GlobalReference = 0x4b;
    public const byte WideString = 0x4c;
    public const byte NotEqual = 0x4d;
    public const byte StringChecksum = 0x4e;
    public const byte RandomFloat = 0x4f;
    public const byte RandomInteger = 0x50;
    public const byte Reserved51 = 0x51;
    public const byte Reserved52 = 0x52;
    public const byte Reserved53 = 0x53;
    public const byte Reserved54 = 0x54;
    public const byte Reserved55 = 0x55;
    public const byte Reserved59 = 0x59;
    public const byte Reserved5A = 0x5a;
}

internal static class Gh3Time
{
    public const int MillisecondsPerSecond = 1000;
    public const float SecondsPerMillisecond = .001f;
}

// Limits of the native readers against corrupt data. GH3's own data stays far below them.
internal static class Gh3Limits
{
    // One day; song and event times are milliseconds.
    public const uint MaximumMilliseconds = 86400000;
    public const uint MaximumMidiNote = 127;
}
