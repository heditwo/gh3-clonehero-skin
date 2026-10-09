using System.Diagnostics;
using BCnEncoder.Decoder;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;

var data = File.ReadAllBytes(args[0]);
int width = BitConverter.ToInt32(data, 12), height = BitConverter.ToInt32(data, 16);
var top = data.AsMemory(24, width * height * 4).ToArray();
var runs = int.Parse(args[1]);
foreach (var parallel in args.Length > 2 ? new[] { true } : new[] { true, false })
foreach (var quality in new[] { CompressionQuality.Fast, CompressionQuality.Balanced })
foreach (var format in new[] { CompressionFormat.Bc1, CompressionFormat.Bc3, CompressionFormat.Bc7 })
{
    if (!parallel && format == CompressionFormat.Bc7 && quality == CompressionQuality.Balanced) continue;
    var times = new List<double>();
    long bytes = 0;
    double psnr = 0;
    for (var run = 0; run < runs; run++)
    {
        var encoder = new BcEncoder();
        encoder.OutputOptions.GenerateMipMaps = true;
        encoder.OutputOptions.Quality = quality;
        encoder.OutputOptions.Format = format;
        encoder.Options.IsParallel = parallel;
        var watch = Stopwatch.StartNew();
        var mips = encoder.EncodeToRawBytes(top, width, height, PixelFormat.Rgba32);
        watch.Stop();
        times.Add(watch.Elapsed.TotalMilliseconds);
        bytes = mips.Sum(mip => (long)mip.Length);
        if (run == 0)
        {
            var decoded = new BcDecoder().DecodeRaw(mips[0], width, height, format);
            double error = 0;
            for (var i = 0; i < decoded.Length; i++)
            {
                error += Math.Pow(decoded[i].r - top[i * 4], 2) + Math.Pow(decoded[i].g - top[i * 4 + 1], 2) + Math.Pow(decoded[i].b - top[i * 4 + 2], 2);
            }
            psnr = 10 * Math.Log10(255.0 * 255 / (error / (decoded.Length * 3.0)));
        }
    }
    times.Sort();
    Console.WriteLine($"{format} {quality} parallel={parallel} bytes={bytes} runs={string.Join(",", times.Select(t => t.ToString("0")))} median={times[times.Count / 2]:0} max={times[^1]:0} ms psnrRGB={psnr:0.00}");
}
