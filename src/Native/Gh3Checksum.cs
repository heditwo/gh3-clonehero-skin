namespace Gh3Skin;

internal static class Gh3Checksum
{
    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(index =>
    {
        var crc = (uint)index;
        for (var bit = 0; bit < 8; bit++) crc = crc >> 1 ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        return crc;
    }).ToArray();

    public static string Of(string text) => Value(text).ToString("x8");

    public static uint Value(string text) => Append(uint.MaxValue, text);

    public static uint Append(uint crc, string text)
    {
        foreach (var character in text)
            if (character >= 0x80) return AppendBytes(crc, text);
        foreach (var character in text)
        {
            var value = character is >= 'A' and <= 'Z' ? character + 32 : character;
            crc = crc >> 8 ^ Table[(crc ^ (uint)value) & 0xff];
        }
        return crc;
    }

    private static uint AppendBytes(uint crc, string text)
    {
        foreach (var value in System.Text.Encoding.UTF8.GetBytes(text.ToLowerInvariant()))
            crc = crc >> 8 ^ Table[(crc ^ value) & 0xff];
        return crc;
    }
}
