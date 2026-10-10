using System.Buffers.Binary;
using System.IO.Compression;

namespace Gh3Skin.Packages;

// Package and cache texture (PACKAGE-FORMAT.md section 7): magic GH3TEX02, Unity TextureFormat, width, height, mip count,
// flags (int32 each), then the full mip chain, largest level first, rows bottom first, as Texture2D.LoadRawTextureData
// expects. Packages store it pre-encoded (pipeline/package/texture.py); the importer copies it into the cache.
internal static class TextureCodec
{
    public const int Rgba32 = 4;
    public const int Dxt1 = 10;
    public const int Dxt5 = 12;
    public const int FlagLinear = 1;
    public static readonly byte[] Magic = System.Text.Encoding.ASCII.GetBytes("GH3TEX02");
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };
    public const int HeaderSize = 28;
    public const int MaxSize = 8192;

    public static int MipCount(int width, int height)
    {
        var count = 1;
        for (var size = Math.Max(width, height); size > 1; size >>= 1) count++;
        return count;
    }

    public static int LevelSize(int format, int width, int height) =>
        format == Rgba32 ? width * height * 4 : Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * (format == Dxt1 ? 8 : 16);

    public static (int Format, int Width, int Height, int Mips, int Flags) ReadHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || !data[..8].SequenceEqual(Magic)) throw new InvalidDataException("Invalid texture header");
        var format = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
        var width = BinaryPrimitives.ReadInt32LittleEndian(data[12..]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(data[16..]);
        var mips = BinaryPrimitives.ReadInt32LittleEndian(data[20..]);
        var flags = BinaryPrimitives.ReadInt32LittleEndian(data[24..]);
        if (format is not (Rgba32 or Dxt1 or Dxt5) || width is < 1 or > MaxSize || height is < 1 or > MaxSize || mips != MipCount(width, height) ||
            (flags & ~FlagLinear) != 0 || format != Rgba32 && (width % 4 != 0 || height % 4 != 0))
            throw new InvalidDataException("Invalid texture header");
        long size = 0;
        for (int mip = 0, w = width, h = height; mip < mips; mip++, w = Math.Max(1, w / 2), h = Math.Max(1, h / 2)) size += LevelSize(format, w, h);
        if (data.Length != HeaderSize + size) throw new InvalidDataException("Invalid texture size");
        return (format, width, height, mips, flags);
    }

    // A package texture for the cache: the same bytes, or RGBA32 when the GPU has no BC1/BC3 (decoded on the worker).
    public static byte[] ForCache(byte[] data, bool compressed, string name)
    {
        if (data.AsSpan().StartsWith(PngSignature))
            throw new PackageException($"texture {name} is a PNG image; package format 1.0 stores textures pre-encoded (GH3TEX02): export the package again with the current CH Stage Tools add-on");
        (int Format, int Width, int Height, int Mips, int Flags) header;
        try { header = ReadHeader(data); }
        catch (InvalidDataException error) { throw new PackageException($"texture {name}: {error.Message}"); }
        return compressed || header.Format == Rgba32 ? data : ToRgba32(data, header);
    }

    private static byte[] ToRgba32(byte[] data, (int Format, int Width, int Height, int Mips, int Flags) header)
    {
        using var output = new MemoryStream();
        Span<byte> head = stackalloc byte[HeaderSize];
        data.AsSpan(0, HeaderSize).CopyTo(head);
        BinaryPrimitives.WriteInt32LittleEndian(head[8..], Rgba32);
        output.Write(head);
        var offset = HeaderSize;
        for (int mip = 0, w = header.Width, h = header.Height; mip < header.Mips; mip++, w = Math.Max(1, w / 2), h = Math.Max(1, h / 2))
        {
            output.Write(BlockCompression.Decode(data.AsSpan(offset, LevelSize(header.Format, w, h)), w, h, header.Format == Dxt5));
            offset += LevelSize(header.Format, w, h);
        }
        return output.ToArray();
    }
}

// PNG reader for package images (PACKAGE-FORMAT.md section 7): all colour types, bit depths 1-16, tRNS, not interlaced.
// Output is RGBA32 with the bottom row first, as Unity's LoadImage stores it.
internal static class Png
{
    public static byte[] DecodeRgba(byte[] png, out int width, out int height)
    {
        if (png.Length < 33 || BinaryPrimitives.ReadUInt64BigEndian(png) != 0x89504E470D0A1A0AUL) throw new InvalidDataException("not a PNG file");
        width = height = 0;
        int depth = 0, colorType = -1;
        byte[]? palette = null, transparency = null;
        using var compressed = new MemoryStream();
        for (var offset = 8; offset + 12 <= png.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            if (length < 0 || offset + 12L + length > png.Length) throw new InvalidDataException("truncated PNG chunk");
            var type = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 4));
            var data = png.AsSpan(offset + 8, length);
            switch (type)
            {
                case 0x49484452:
                    width = BinaryPrimitives.ReadInt32BigEndian(data);
                    height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                    depth = data[8];
                    colorType = data[9];
                    if (data[12] != 0) throw new InvalidDataException("interlaced PNG files are not permitted");
                    break;
                case 0x504C5445: palette = data.ToArray(); break;
                case 0x74524E53: transparency = data.ToArray(); break;
                case 0x49444154: compressed.Write(data); break;
            }
            if (type == 0x49454E44) break;
            offset += length + 12;
        }
        var channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException("invalid PNG colour type") };
        if (width is < 1 or > 8192 || height is < 1 or > 8192 || depth is not (1 or 2 or 4 or 8 or 16) ||
            colorType == 3 && (depth > 8 || palette == null) || colorType is 2 or 4 or 6 && depth < 8)
            throw new InvalidDataException("unsupported PNG header");
        var bitsPerPixel = channels * depth;
        var stride = (width * bitsPerPixel + 7) / 8;
        var bpp = Math.Max(1, bitsPerPixel / 8);
        var filtered = new byte[(long)(stride + 1) * height];
        compressed.Position = 0;
        using (var inflater = new ZLibStream(compressed, CompressionMode.Decompress))
            for (var read = 0; read < filtered.Length;)
            {
                var count = inflater.Read(filtered, read, filtered.Length - read);
                if (count == 0) throw new InvalidDataException("truncated PNG data");
                read += count;
            }
        var previous = new byte[stride];
        var current = new byte[stride];
        var rgba = new byte[width * height * 4];
        var scale = depth < 8 ? 255 / ((1 << depth) - 1) : 1;
        for (var y = 0; y < height; y++)
        {
            Unfilter(filtered[y * (stride + 1)], filtered.AsSpan(y * (stride + 1) + 1, stride), current, previous, bpp);
            var target = rgba.AsSpan((height - 1 - y) * width * 4, width * 4);
            if (depth == 8 && colorType == 6) current.AsSpan(0, width * 4).CopyTo(target);
            else if (depth == 8 && colorType == 2 && transparency == null) Rgb8Row(current, target, width);
            else ConvertRow(current, target, width, channels, depth, colorType, scale, palette, transparency);
            (previous, current) = (current, previous);
        }
        return rgba;
    }

    // The common 8-bit RGB rows without per-pixel branches.
    private static void Rgb8Row(byte[] current, Span<byte> target, int width)
    {
        for (int x = 0, source = 0, at = 0; x < width; x++, source += 3, at += 4)
        {
            target[at] = current[source];
            target[at + 1] = current[source + 1];
            target[at + 2] = current[source + 2];
            target[at + 3] = 255;
        }
    }

    private static void ConvertRow(byte[] current, Span<byte> target, int width, int channels, int depth, int colorType, int scale,
        byte[]? palette, byte[]? transparency)
    {
        for (var x = 0; x < width; x++)
        {
            int Sample(int channel)
            {
                if (depth == 8) return current[x * channels + channel];
                if (depth == 16) return current[(x * channels + channel) * 2];
                var bit = (x * channels + channel) * depth;
                return (current[bit >> 3] >> (8 - depth - (bit & 7))) & ((1 << depth) - 1);
            }
            int Raw16(int channel) => depth == 16 ? current[(x * channels + channel) * 2] << 8 | current[(x * channels + channel) * 2 + 1] : Sample(channel);
            byte r, g, b, a = 255;
            switch (colorType)
            {
                case 3:
                    var index = Sample(0);
                    if (index * 3 + 2 >= palette!.Length) throw new InvalidDataException("PNG palette index out of range");
                    (r, g, b) = (palette[index * 3], palette[index * 3 + 1], palette[index * 3 + 2]);
                    if (transparency != null && index < transparency.Length) a = transparency[index];
                    break;
                case 0:
                    r = g = b = (byte)(Sample(0) * scale);
                    if (transparency is { Length: >= 2 } && Raw16(0) == (transparency[0] << 8 | transparency[1])) a = 0;
                    break;
                case 4:
                    r = g = b = (byte)Sample(0);
                    a = (byte)Sample(1);
                    break;
                case 2:
                    (r, g, b) = ((byte)Sample(0), (byte)Sample(1), (byte)Sample(2));
                    if (transparency is { Length: >= 6 } && Raw16(0) == (transparency[0] << 8 | transparency[1]) &&
                        Raw16(1) == (transparency[2] << 8 | transparency[3]) && Raw16(2) == (transparency[4] << 8 | transparency[5])) a = 0;
                    break;
                default:
                    (r, g, b, a) = ((byte)Sample(0), (byte)Sample(1), (byte)Sample(2), (byte)Sample(3));
                    break;
            }
            target[x * 4] = r;
            target[x * 4 + 1] = g;
            target[x * 4 + 2] = b;
            target[x * 4 + 3] = a;
        }
    }

    // The first bpp bytes of a row have no left neighbour; the loops handle them first so the inner loops have no branch.
    private static void Unfilter(byte filter, ReadOnlySpan<byte> row, byte[] current, byte[] previous, int bpp)
    {
        var head = Math.Min(bpp, row.Length);
        switch (filter)
        {
            case 0: row.CopyTo(current); break;
            case 1:
                row[..head].CopyTo(current);
                for (var i = head; i < row.Length; i++) current[i] = (byte)(row[i] + current[i - bpp]);
                break;
            case 2: for (var i = 0; i < row.Length; i++) current[i] = (byte)(row[i] + previous[i]); break;
            case 3:
                for (var i = 0; i < head; i++) current[i] = (byte)(row[i] + (previous[i] >> 1));
                for (var i = head; i < row.Length; i++) current[i] = (byte)(row[i] + ((current[i - bpp] + previous[i]) >> 1));
                break;
            case 4:
                // Paeth with a = c = 0 selects b.
                for (var i = 0; i < head; i++) current[i] = (byte)(row[i] + previous[i]);
                for (var i = head; i < row.Length; i++)
                {
                    int a = current[i - bpp], b = previous[i], c = previous[i - bpp];
                    int pa = Math.Abs(b - c), pb = Math.Abs(a - c), pc = Math.Abs(a + b - 2 * c);
                    current[i] = (byte)(row[i] + (pa <= pb && pa <= pc ? a : pb <= pc ? b : c));
                }
                break;
            default: throw new InvalidDataException($"unknown PNG filter {filter}");
        }
    }
}
