using System.Buffers.Binary;
using System.IO.Compression;

namespace Gh3Skin.Spike;

// Spike-only managed PNG decoder: 8-bit RGB or RGBA, not interlaced. Output is RGBA32, bottom row first (Unity order).
internal static class PngDecoder
{
    public static byte[] DecodeRgba(byte[] png, out int width, out int height)
    {
        if (png.Length < 33 || BinaryPrimitives.ReadUInt64BigEndian(png) != 0x89504E470D0A1A0AUL)
            throw new InvalidDataException("Not a PNG file");
        width = height = 0;
        var channels = 0;
        using var compressed = new MemoryStream();
        for (var offset = 8; offset + 12 <= png.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            var type = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 4));
            var data = png.AsSpan(offset + 8, length);
            if (type == 0x49484452)
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                if (data[8] != 8 || data[12] != 0 || (data[9] != 6 && data[9] != 2))
                    throw new NotSupportedException("Only 8-bit RGB or RGBA non-interlaced PNG files are supported");
                channels = data[9] == 6 ? 4 : 3;
            }
            else if (type == 0x49444154)
                compressed.Write(data);
            else if (type == 0x49454E44)
                break;
            offset += length + 12;
        }
        var stride = width * channels;
        var filtered = new byte[(stride + 1) * height];
        compressed.Position = 0;
        using (var inflater = new ZLibStream(compressed, CompressionMode.Decompress))
            for (var read = 0; read < filtered.Length;)
            {
                var count = inflater.Read(filtered, read, filtered.Length - read);
                if (count == 0) throw new InvalidDataException("Truncated PNG data");
                read += count;
            }
        var previous = new byte[stride];
        var current = new byte[stride];
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var row = filtered.AsSpan(y * (stride + 1) + 1, stride);
            Unfilter(filtered[y * (stride + 1)], row, current, previous, channels);
            var target = rgba.AsSpan((height - 1 - y) * width * 4, width * 4);
            if (channels == 4)
                current.CopyTo(target);
            else
                for (var x = 0; x < width; x++)
                {
                    target[x * 4] = current[x * 3];
                    target[x * 4 + 1] = current[x * 3 + 1];
                    target[x * 4 + 2] = current[x * 3 + 2];
                    target[x * 4 + 3] = 255;
                }
            (previous, current) = (current, previous);
        }
        return rgba;
    }

    private static void Unfilter(byte filter, ReadOnlySpan<byte> row, byte[] current, byte[] previous, int bpp)
    {
        switch (filter)
        {
            case 0:
                row.CopyTo(current);
                break;
            case 1:
                for (var i = 0; i < row.Length; i++)
                    current[i] = (byte)(row[i] + (i >= bpp ? current[i - bpp] : 0));
                break;
            case 2:
                for (var i = 0; i < row.Length; i++)
                    current[i] = (byte)(row[i] + previous[i]);
                break;
            case 3:
                for (var i = 0; i < row.Length; i++)
                    current[i] = (byte)(row[i] + (((i >= bpp ? current[i - bpp] : 0) + previous[i]) >> 1));
                break;
            case 4:
                for (var i = 0; i < row.Length; i++)
                {
                    int a = i >= bpp ? current[i - bpp] : 0, b = previous[i], c = i >= bpp ? previous[i - bpp] : 0;
                    int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    current[i] = (byte)(row[i] + (pa <= pb && pa <= pc ? a : pb <= pc ? b : c));
                }
                break;
            default:
                throw new InvalidDataException($"Unknown PNG filter {filter}");
        }
    }

    // Full RGBA32 mip chain (box filter), largest level first, as LoadRawTextureData expects for a mipmapped texture.
    public static byte[] WithMipChain(byte[] rgba, int width, int height, out int mipCount)
    {
        var levels = new List<(byte[] Data, int Width, int Height)> { (rgba, width, height) };
        while (levels[^1].Width > 1 || levels[^1].Height > 1)
        {
            var (source, sourceWidth, sourceHeight) = levels[^1];
            int levelWidth = Math.Max(1, sourceWidth / 2), levelHeight = Math.Max(1, sourceHeight / 2);
            var level = new byte[levelWidth * levelHeight * 4];
            for (var y = 0; y < levelHeight; y++)
            {
                int y0 = Math.Min(y * 2, sourceHeight - 1), y1 = Math.Min(y * 2 + 1, sourceHeight - 1);
                for (var x = 0; x < levelWidth; x++)
                {
                    int x0 = Math.Min(x * 2, sourceWidth - 1), x1 = Math.Min(x * 2 + 1, sourceWidth - 1);
                    for (var c = 0; c < 4; c++)
                        level[(y * levelWidth + x) * 4 + c] = (byte)((source[(y0 * sourceWidth + x0) * 4 + c] +
                            source[(y0 * sourceWidth + x1) * 4 + c] + source[(y1 * sourceWidth + x0) * 4 + c] +
                            source[(y1 * sourceWidth + x1) * 4 + c] + 2) >> 2);
                }
            }
            levels.Add((level, levelWidth, levelHeight));
        }
        mipCount = levels.Count;
        var chain = new byte[levels.Sum(level => level.Data.Length)];
        var offset = 0;
        foreach (var level in levels)
        {
            level.Data.CopyTo(chain, offset);
            offset += level.Data.Length;
        }
        return chain;
    }
}
