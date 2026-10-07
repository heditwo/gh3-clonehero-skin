namespace Gh3Skin;

internal static class Gh3Checksum
{
    public static string Of(string text) => Value(text).ToString("x8");

    public static uint Value(string text) => Append(uint.MaxValue, text);

    public static uint Append(uint crc, string text)
    {
        foreach (var value in System.Text.Encoding.UTF8.GetBytes(text.ToLowerInvariant()))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = crc >> 1 ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        return crc;
    }
}
