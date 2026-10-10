namespace Gh3Skin.Packages;

// Managed BC1 (DXT1) and BC3 (DXT5) encoder for the package cache. Written for this project (no third-party code):
// principal-axis endpoints, least-squares refinement, exact single-colour blocks, and min/max alpha with 8 levels.
internal static class BlockCompression
{
    private static readonly byte[,] Single5 = SingleColorTable(5);
    private static readonly byte[,] Single6 = SingleColorTable(6);

    public static byte[] Encode(byte[] rgba, int width, int height, bool alpha) => Encode(rgba, width, height, alpha, out _);

    // squaredError: the sum of the squared RGB(A) errors of the blocks, as MeanSquaredError counts them when the size is a
    // multiple of 4 (no edge pixels repeat), so the importer does not decode the level again.
    public static byte[] Encode(byte[] rgba, int width, int height, bool alpha, out long squaredError)
    {
        squaredError = 0;
        int blocksX = Math.Max(1, (width + 3) / 4), blocksY = Math.Max(1, (height + 3) / 4);
        var blockSize = alpha ? 16 : 8;
        var output = new byte[blocksX * blocksY * blockSize];
        Span<byte> block = stackalloc byte[64];
        for (var by = 0; by < blocksY; by++)
            for (var bx = 0; bx < blocksX; bx++)
            {
                for (var y = 0; y < 4; y++)
                    for (var x = 0; x < 4; x++)
                    {
                        var sx = Math.Min(bx * 4 + x, width - 1);
                        var sy = Math.Min(by * 4 + y, height - 1);
                        var source = (sy * width + sx) * 4;
                        var target = (y * 4 + x) * 4;
                        block[target] = rgba[source];
                        block[target + 1] = rgba[source + 1];
                        block[target + 2] = rgba[source + 2];
                        block[target + 3] = rgba[source + 3];
                    }
                var at = (by * blocksX + bx) * blockSize;
                if (alpha)
                {
                    squaredError += EncodeAlpha(block, output.AsSpan(at, 8));
                    at += 8;
                }
                squaredError += EncodeColor(block, output.AsSpan(at, 8));
            }
        return output;
    }

    // Mean squared RGB(A) error of the encoded level against the source (decoded as the GPU does).
    public static double MeanSquaredError(byte[] rgba, int width, int height, byte[] blocks, bool alpha)
    {
        int blocksX = Math.Max(1, (width + 3) / 4);
        var blockSize = alpha ? 16 : 8;
        Span<int> palette = stackalloc int[12];
        Span<int> levels = stackalloc int[8];
        double total = 0;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var at = ((y / 4) * blocksX + x / 4) * blockSize;
                var pixel = (y % 4) * 4 + x % 4;
                var source = (y * width + x) * 4;
                if (alpha)
                {
                    int a0 = blocks[at], a1 = blocks[at + 1];
                    ulong bits = 0;
                    for (var i = 0; i < 6; i++) bits |= (ulong)blocks[at + 2 + i] << (8 * i);
                    var index = (int)(bits >> (3 * pixel) & 7);
                    var value = index == 0 ? a0 : index == 1 ? a1 : a0 > a1 ? ((8 - index) * a0 + (index - 1) * a1 + 3) / 7 :
                        index == 6 ? 0 : index == 7 ? 255 : ((6 - index) * a0 + (index - 1) * a1 + 2) / 5;
                    var d = value - rgba[source + 3];
                    total += d * d;
                    at += 8;
                }
                int c0 = blocks[at] | blocks[at + 1] << 8, c1 = blocks[at + 2] | blocks[at + 3] << 8;
                Palette(c0, c1, palette);
                var choice = (int)((uint)(blocks[at + 4] | blocks[at + 5] << 8 | blocks[at + 6] << 16 | blocks[at + 7] << 24) >> (2 * pixel) & 3);
                for (var c = 0; c < 3; c++)
                {
                    var d = palette[choice * 3 + c] - rgba[source + c];
                    total += d * d;
                }
            }
        return total / (width * height * (alpha ? 4 : 3));
    }

    private static int Expand5(int v) => v << 3 | v >> 2;
    private static int Expand6(int v) => v << 2 | v >> 4;
    private static int Pack(int r, int g, int b) => r << 11 | g << 5 | b;
    private static int Quantize(double value, int bits)
    {
        var top = (1 << bits) - 1;
        return Math.Clamp((int)Math.Round(value * top / 255.0), 0, top);
    }

    // For each 8-bit value, the endpoint pair whose 2/3 + 1/3 mix is closest (BC1 index 2).
    private static byte[,] SingleColorTable(int bits)
    {
        var table = new byte[256, 2];
        var top = (1 << bits) - 1;
        for (var value = 0; value < 256; value++)
        {
            var best = int.MaxValue;
            for (var a = 0; a <= top; a++)
                for (var b = 0; b <= top; b++)
                {
                    int ea = bits == 5 ? Expand5(a) : Expand6(a), eb = bits == 5 ? Expand5(b) : Expand6(b);
                    var error = Math.Abs((2 * ea + eb) / 3 - value) * 100 + Math.Abs(ea - eb);
                    if (error < best) { best = error; table[value, 0] = (byte)a; table[value, 1] = (byte)b; }
                }
        }
        return table;
    }

    private static void Palette(int c0, int c1, Span<int> palette)
    {
        int r0 = Expand5(c0 >> 11), g0 = Expand6(c0 >> 5 & 63), b0 = Expand5(c0 & 31);
        int r1 = Expand5(c1 >> 11), g1 = Expand6(c1 >> 5 & 63), b1 = Expand5(c1 & 31);
        palette[0] = r0; palette[1] = g0; palette[2] = b0;
        palette[3] = r1; palette[4] = g1; palette[5] = b1;
        palette[6] = (2 * r0 + r1) / 3; palette[7] = (2 * g0 + g1) / 3; palette[8] = (2 * b0 + b1) / 3;
        palette[9] = (r0 + 2 * r1) / 3; palette[10] = (g0 + 2 * g1) / 3; palette[11] = (b0 + 2 * b1) / 3;
    }

    private static int Match(ReadOnlySpan<byte> block, int c0, int c1, Span<int> indices)
    {
        Span<int> palette = stackalloc int[12];
        Palette(c0, c1, palette);
        var total = 0;
        for (var i = 0; i < 16; i++)
        {
            int r = block[i * 4], g = block[i * 4 + 1], b = block[i * 4 + 2];
            int best = int.MaxValue, choice = 0;
            for (var p = 0; p < 4; p++)
            {
                int dr = r - palette[p * 3], dg = g - palette[p * 3 + 1], db = b - palette[p * 3 + 2];
                var error = dr * dr + dg * dg + db * db;
                if (error < best) { best = error; choice = p; }
            }
            indices[i] = choice;
            total += best;
        }
        return total;
    }

    private static int EncodeColor(ReadOnlySpan<byte> block, Span<byte> output)
    {
        int error;
        Span<int> indices = stackalloc int[16];
        int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;
        double meanR = 0, meanG = 0, meanB = 0;
        for (var i = 0; i < 16; i++)
        {
            int r = block[i * 4], g = block[i * 4 + 1], b = block[i * 4 + 2];
            minR = Math.Min(minR, r); minG = Math.Min(minG, g); minB = Math.Min(minB, b);
            maxR = Math.Max(maxR, r); maxG = Math.Max(maxG, g); maxB = Math.Max(maxB, b);
            meanR += r; meanG += g; meanB += b;
        }
        int c0, c1;
        if (minR == maxR && minG == maxG && minB == maxB)
        {
            c0 = Pack(Single5[minR, 0], Single6[minG, 0], Single5[minB, 0]);
            c1 = Pack(Single5[minR, 1], Single6[minG, 1], Single5[minB, 1]);
            var exact = Pack(Quantize(minR, 5), Quantize(minG, 6), Quantize(minB, 5));
            if (Expand5(exact >> 11) == minR && Expand6(exact >> 5 & 63) == minG && Expand5(exact & 31) == minB) c0 = c1 = exact;
            error = Match(block, c0, c1, indices);
        }
        else
        {
            meanR /= 16; meanG /= 16; meanB /= 16;
            double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            for (var i = 0; i < 16; i++)
            {
                double r = block[i * 4] - meanR, g = block[i * 4 + 1] - meanG, b = block[i * 4 + 2] - meanB;
                xx += r * r; xy += r * g; xz += r * b; yy += g * g; yz += g * b; zz += b * b;
            }
            double ax = maxR - minR, ay = maxG - minG, az = maxB - minB;
            for (var iteration = 0; iteration < 6; iteration++)
            {
                double nx = xx * ax + xy * ay + xz * az, ny = xy * ax + yy * ay + yz * az, nz = xz * ax + yz * ay + zz * az;
                var length = Math.Max(Math.Abs(nx), Math.Max(Math.Abs(ny), Math.Abs(nz)));
                if (length < 1e-9) break;
                ax = nx / length; ay = ny / length; az = nz / length;
            }
            if (Math.Abs(ax) + Math.Abs(ay) + Math.Abs(az) < 1e-9) (ax, ay, az) = (0.299, 0.587, 0.114);
            double low = double.MaxValue, high = double.MinValue;
            int lowIndex = 0, highIndex = 0;
            for (var i = 0; i < 16; i++)
            {
                var d = block[i * 4] * ax + block[i * 4 + 1] * ay + block[i * 4 + 2] * az;
                if (d < low) { low = d; lowIndex = i; }
                if (d > high) { high = d; highIndex = i; }
            }
            c0 = Pack(Quantize(block[highIndex * 4], 5), Quantize(block[highIndex * 4 + 1], 6), Quantize(block[highIndex * 4 + 2], 5));
            c1 = Pack(Quantize(block[lowIndex * 4], 5), Quantize(block[lowIndex * 4 + 1], 6), Quantize(block[lowIndex * 4 + 2], 5));
            error = Match(block, c0, c1, indices);
            Span<int> trial = stackalloc int[16];
            for (var pass = 0; pass < 2 && error > 0; pass++)
            {
                if (!Refine(block, indices, out var r0, out var r1)) break;
                var trialError = Match(block, r0, r1, trial);
                if (trialError >= error) break;
                (c0, c1, error) = (r0, r1, trialError);
                trial.CopyTo(indices);
            }
        }
        if (c0 < c1)
        {
            (c0, c1) = (c1, c0);
            for (var i = 0; i < 16; i++) indices[i] = indices[i] switch { 0 => 1, 1 => 0, 2 => 3, _ => 2 };
        }
        else if (c0 == c1)
            for (var i = 0; i < 16; i++) indices[i] = 0;
        output[0] = (byte)c0; output[1] = (byte)(c0 >> 8);
        output[2] = (byte)c1; output[3] = (byte)(c1 >> 8);
        uint bits = 0;
        for (var i = 15; i >= 0; i--) bits = bits << 2 | (uint)indices[i];
        output[4] = (byte)bits; output[5] = (byte)(bits >> 8); output[6] = (byte)(bits >> 16); output[7] = (byte)(bits >> 24);
        return error;
    }

    // Least-squares endpoints for the given indices (weights 1, 0, 2/3, 1/3 of endpoint 0).
    private static bool Refine(ReadOnlySpan<byte> block, ReadOnlySpan<int> indices, out int c0, out int c1)
    {
        double aa = 0, bb = 0, ab = 0, ar = 0, ag = 0, abl = 0, br = 0, bg = 0, bbl = 0;
        for (var i = 0; i < 16; i++)
        {
            var w = indices[i] switch { 0 => 1.0, 1 => 0.0, 2 => 2.0 / 3, _ => 1.0 / 3 };
            var v = 1 - w;
            aa += w * w; bb += v * v; ab += w * v;
            ar += w * block[i * 4]; ag += w * block[i * 4 + 1]; abl += w * block[i * 4 + 2];
            br += v * block[i * 4]; bg += v * block[i * 4 + 1]; bbl += v * block[i * 4 + 2];
        }
        var det = aa * bb - ab * ab;
        c0 = c1 = 0;
        if (Math.Abs(det) < 1e-9) return false;
        var f = 1 / det;
        c0 = Pack(Quantize(Math.Clamp((ar * bb - br * ab) * f, 0, 255), 5), Quantize(Math.Clamp((ag * bb - bg * ab) * f, 0, 255), 6),
            Quantize(Math.Clamp((abl * bb - bbl * ab) * f, 0, 255), 5));
        c1 = Pack(Quantize(Math.Clamp((br * aa - ar * ab) * f, 0, 255), 5), Quantize(Math.Clamp((bg * aa - ag * ab) * f, 0, 255), 6),
            Quantize(Math.Clamp((bbl * aa - abl * ab) * f, 0, 255), 5));
        return true;
    }

    private static int EncodeAlpha(ReadOnlySpan<byte> block, Span<byte> output)
    {
        int low = 255, high = 0, squared = 0;
        for (var i = 0; i < 16; i++)
        {
            low = Math.Min(low, block[i * 4 + 3]);
            high = Math.Max(high, block[i * 4 + 3]);
        }
        output[0] = (byte)high;
        output[1] = (byte)low;
        ulong bits = 0;
        if (high != low)
        {
            Span<int> palette = stackalloc int[8];
            palette[0] = high;
            palette[1] = low;
            for (var i = 2; i < 8; i++) palette[i] = ((8 - i) * high + (i - 1) * low + 3) / 7;
            for (var i = 15; i >= 0; i--)
            {
                var a = block[i * 4 + 3];
                int best = int.MaxValue, choice = 0;
                for (var p = 0; p < 8; p++)
                {
                    var error = Math.Abs(a - palette[p]);
                    if (error < best) { best = error; choice = p; }
                }
                bits = bits << 3 | (uint)choice;
                squared += best * best;
            }
        }
        for (var i = 0; i < 6; i++) output[2 + i] = (byte)(bits >> (8 * i));
        return squared;
    }
}
