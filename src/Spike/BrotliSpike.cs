using System.Diagnostics;
using System.IO.Compression;
using MelonLoader;

namespace Gh3Skin.Spike;

// Package format check (docs/PACKAGE-FORMAT.md): decode q48 clips with Brotli and gzip inside the game.
// Runs only with GH3_SKIN_SPIKE=brotli and GH3_SKIN_SPIKE_CLIPS=<folder with brotli11/ and gzip9/ subfolders
// that hold the same clip file names>.
internal static class BrotliSpike
{
    private const string Prefix = "[spike-brotli] ";
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("GH3_SKIN_SPIKE") == "brotli";
    private static readonly string Directory = Environment.GetEnvironmentVariable("GH3_SKIN_SPIKE_CLIPS") ?? "";
    private static readonly byte[] Magic = System.Text.Encoding.ASCII.GetBytes("GH3CLQ01");
    private static bool _started;

    public static void Start()
    {
        if (!Enabled || _started) return;
        _started = true;
        Task.Run(Run);
    }

    private static void Run()
    {
        try
        {
            Log($"thread={Environment.CurrentManagedThreadId} runtime={Environment.Version} os={Environment.OSVersion}");
            var names = System.IO.Directory.GetFiles(Path.Combine(Directory, "brotli11")).Select(Path.GetFileName).OrderBy(n => n).ToArray();
            for (var pass = 0; pass < 3; pass++)
            {
                double brotliMs = 0, gzipMs = 0, brotliMax = 0, gzipMax = 0;
                long raw = 0, brotliBytes = 0, gzipBytes = 0;
                var mismatches = 0;
                foreach (var name in names)
                {
                    var brotliFile = File.ReadAllBytes(Path.Combine(Directory, "brotli11", name!));
                    var gzipFile = File.ReadAllBytes(Path.Combine(Directory, "gzip9", name!));
                    var watch = Stopwatch.StartNew();
                    var fromBrotli = Decode(new BrotliStream(new MemoryStream(brotliFile), CompressionMode.Decompress));
                    var brotliTime = watch.Elapsed.TotalMilliseconds;
                    watch.Restart();
                    var fromGzip = Decode(new GZipStream(new MemoryStream(gzipFile), CompressionMode.Decompress));
                    var gzipTime = watch.Elapsed.TotalMilliseconds;
                    if (!fromBrotli.AsSpan().SequenceEqual(fromGzip) || !fromBrotli.AsSpan(0, 8).SequenceEqual(Magic))
                        mismatches++;
                    brotliMs += brotliTime;
                    gzipMs += gzipTime;
                    brotliMax = Math.Max(brotliMax, brotliTime);
                    gzipMax = Math.Max(gzipMax, gzipTime);
                    raw += fromBrotli.Length;
                    brotliBytes += brotliFile.Length;
                    gzipBytes += gzipFile.Length;
                }
                Log($"pass={pass} clips={names.Length} mismatches={mismatches} raw={raw} brotli={brotliBytes} gzip={gzipBytes} " +
                    $"brotliMs={brotliMs:F1} brotliMaxMs={brotliMax:F2} gzipMs={gzipMs:F1} gzipMaxMs={gzipMax:F2}");
            }
            Log("done");
        }
        catch (Exception error)
        {
            MelonLogger.Error(Prefix + error);
        }
    }

    private static byte[] Decode(Stream stream)
    {
        using (stream)
        using (var output = new MemoryStream())
        {
            stream.CopyTo(output);
            return output.ToArray();
        }
    }

    private static void Log(string message) => MelonLogger.Msg(Prefix + message);
}
