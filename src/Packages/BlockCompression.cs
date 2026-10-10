namespace Gh3Skin.Packages;

// BC1 (DXT1) and BC3 (DXT5) decoder, for a GPU without these formats (the packages store them pre-encoded). It decodes as
// the GPU does: a BC1 block with color0 <= color1 has three colours and a transparent index 3.
internal static class BlockCompression
{
    public static byte[] Decode(ReadOnlySpan<byte> blocks, int width, int height, bool alpha)
    {
        int blocksX = Math.Max(1, (width + 3) / 4);
        var blockSize = alpha ? 16 : 8;
        var rgba = new byte[width * height * 4];
        Span<int> palette = stackalloc int[16];
        Span<int> levels = stackalloc int[8];
        for (var by = 0; by * 4 < height; by++)
            for (var bx = 0; bx < blocksX; bx++)
            {
                var at = (by * blocksX + bx) * blockSize;
                ulong alphaBits = 0;
                if (alpha)
                {
                    int a0 = blocks[at], a1 = blocks[at + 1];
                    for (var i = 0; i < 6; i++) alphaBits |= (ulong)blocks[at + 2 + i] << (8 * i);
                    levels[0] = a0;
                    levels[1] = a1;
                    for (var i = 2; i < 8; i++)
                        levels[i] = a0 > a1 ? ((8 - i) * a0 + (i - 1) * a1 + 3) / 7 : i == 6 ? 0 : i == 7 ? 255 : ((6 - i) * a0 + (i - 1) * a1 + 2) / 5;
                    at += 8;
                }
                int c0 = blocks[at] | blocks[at + 1] << 8, c1 = blocks[at + 2] | blocks[at + 3] << 8;
                Palette(c0, c1, !alpha && c0 <= c1, palette);
                var bits = (uint)(blocks[at + 4] | blocks[at + 5] << 8 | blocks[at + 6] << 16 | blocks[at + 7] << 24);
                for (var pixel = 0; pixel < 16; pixel++)
                {
                    int x = bx * 4 + pixel % 4, y = by * 4 + pixel / 4;
                    if (x >= width || y >= height) continue;
                    var target = (y * width + x) * 4;
                    var choice = (int)(bits >> (2 * pixel) & 3) * 4;
                    rgba[target] = (byte)palette[choice];
                    rgba[target + 1] = (byte)palette[choice + 1];
                    rgba[target + 2] = (byte)palette[choice + 2];
                    rgba[target + 3] = alpha ? (byte)levels[(int)(alphaBits >> (3 * pixel) & 7)] : (byte)palette[choice + 3];
                }
            }
        return rgba;
    }

    private static int Expand5(int v) => v << 3 | v >> 2;
    private static int Expand6(int v) => v << 2 | v >> 4;

    private static void Palette(int c0, int c1, bool threeColors, Span<int> palette)
    {
        int r0 = Expand5(c0 >> 11), g0 = Expand6(c0 >> 5 & 63), b0 = Expand5(c0 & 31);
        int r1 = Expand5(c1 >> 11), g1 = Expand6(c1 >> 5 & 63), b1 = Expand5(c1 & 31);
        palette[0] = r0; palette[1] = g0; palette[2] = b0; palette[3] = 255;
        palette[4] = r1; palette[5] = g1; palette[6] = b1; palette[7] = 255;
        if (threeColors)
        {
            palette[8] = (r0 + r1) / 2; palette[9] = (g0 + g1) / 2; palette[10] = (b0 + b1) / 2; palette[11] = 255;
            palette[12] = palette[13] = palette[14] = palette[15] = 0;
            return;
        }
        palette[8] = (2 * r0 + r1) / 3; palette[9] = (2 * g0 + g1) / 3; palette[10] = (2 * b0 + b1) / 3; palette[11] = 255;
        palette[12] = (r0 + 2 * r1) / 3; palette[13] = (g0 + 2 * g1) / 3; palette[14] = (b0 + 2 * b1) / 3; palette[15] = 255;
    }
}
