using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Gh3Skin.Spike;

// Packaging spike (docs/spike/textures.md). Runs only with GH3_SKIN_SPIKE=textures and
// GH3_SKIN_SPIKE_TEXTURES_DIR=<folder with source.png and *.gh3tex from scripts/spike/encode_textures.py>.
internal static class TextureSpike
{
    private const string Prefix = "[spike-textures] ";
    private const int Runs = 7;
    private const int StartFrame = 30;
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("GH3_SKIN_SPIKE") == "textures";
    private static readonly string Directory = Environment.GetEnvironmentVariable("GH3_SKIN_SPIKE_TEXTURES_DIR") ?? "";
    private static readonly string[] CompressedFormats = { "bc1", "bc3", "bc5", "bc7" };
    private static int _frames;
    private static bool _done;

    public static void Reset() => _frames = 0;

    public static void Update()
    {
        if (!Enabled || _done || ++_frames < StartFrame) return;
        _done = true;
        try
        {
            LogSystem();
            LogContentPaths();
            var shown = Benchmark();
            Show(shown);
        }
        catch (Exception error)
        {
            MelonLogger.Error(Prefix + error);
        }
    }

    private static void Log(string message) => MelonLogger.Msg(Prefix + message);

    private static void LogSystem()
    {
        Log($"graphicsDeviceType={SystemInfo.graphicsDeviceType} version='{SystemInfo.graphicsDeviceVersion}' " +
            $"name='{SystemInfo.graphicsDeviceName}' multiThreaded={SystemInfo.graphicsMultiThreaded} " +
            $"threading={SystemInfo.renderingThreadingMode} maxTextureSize={SystemInfo.maxTextureSize} " +
            $"colorSpace={QualitySettings.activeColorSpace}");
        foreach (var format in new[] { TextureFormat.RGBA32, TextureFormat.DXT1, TextureFormat.DXT5, TextureFormat.BC4,
                     TextureFormat.BC5, TextureFormat.BC6H, TextureFormat.BC7, TextureFormat.ETC2_RGBA8, TextureFormat.ASTC_4x4 })
            Try($"SupportsTextureFormat({format})", () => SystemInfo.SupportsTextureFormat(format).ToString());
        foreach (var format in new[] { GraphicsFormat.RGBA_DXT1_SRGB, GraphicsFormat.RGBA_DXT5_SRGB, GraphicsFormat.RG_BC5_UNorm,
                     GraphicsFormat.RGBA_BC7_SRGB, GraphicsFormat.RGBA_BC7_UNorm })
            Try($"IsFormatSupported({format}, Sample)", () => SystemInfo.IsFormatSupported(format, FormatUsage.Sample).ToString());
    }

    private static void Try(string label, Func<string> value)
    {
        try { Log($"{label}={value()}"); }
        catch (Exception error) { Log($"{label} failed: {error.GetType().Name}: {error.Message}"); }
    }

    private static void LogContentPaths()
    {
        Log($"persistentDataPath='{Application.persistentDataPath}' dataPath='{Application.dataPath}' cwd='{Environment.CurrentDirectory}'");
        var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("Il2Cpp.GlobalVariables"))
            .FirstOrDefault(found => found != null);
        if (type == null)
        {
            Log("GlobalVariables type not found");
            return;
        }
        var flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance;
        var instance = type.GetProperty("instance", flags)?.GetValue(null);
        foreach (var name in new[] { "isPortable", "defaultSongPath", "defaultCustomContentPath", "uniqueCustomContentPaths",
                     "uniqueSongPaths", "backgroundImagePaths", "backgroundVideoPaths", "Highways", "screenshotPath", "replayPath" })
            Try("GlobalVariables." + name, () =>
            {
                var property = type.GetProperty(name, flags) ?? throw new MissingMemberException(name);
                var target = property.GetMethod!.IsStatic ? null : instance ?? throw new NullReferenceException("instance");
                return Describe(property.GetValue(target));
            });
    }

    private static string Describe(object? value)
    {
        if (value == null) return "null";
        if (value is string text) return $"'{text}'";
        var type = value.GetType();
        var count = type.GetProperty("Count")?.GetValue(value) ?? type.GetProperty("Length")?.GetValue(value);
        if (count is not int items) return $"'{value}'";
        var indexer = type.GetMethod("get_Item", new[] { typeof(int) });
        var shown = new List<string>();
        for (var i = 0; i < Math.Min(items, 12) && indexer != null; i++)
            shown.Add(DescribeItem(indexer.Invoke(value, new object[] { i })));
        return $"count={items} [{string.Join(", ", shown)}]";
    }

    private static string DescribeItem(object? item)
    {
        if (item == null) return "null";
        if (item is string text) return $"'{text}'";
        var fields = item.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(string) && property.GetIndexParameters().Length == 0)
            .Select(property => $"{property.Name}='{property.GetValue(item)}'");
        return $"{item.GetType().Name}{{{string.Join(" ", fields)}}}";
    }

    private sealed record Shown(string Label, Texture2D Texture, Texture2D Biased);

    private static List<Shown> Benchmark()
    {
        var png = File.ReadAllBytes(Path.Combine(Directory, "source.png"));
        Log($"source.png bytes={png.Length}");
        var shown = new List<Shown>();

        var loadImage = new List<double>();
        var loadImageSync = new List<double>();
        var marshal = new List<double>();
        for (var run = 0; run < Runs + 1; run++)
        {
            var watch = Stopwatch.StartNew();
            var array = new Il2CppStructArray<byte>(png);
            var marshalled = watch.Elapsed.TotalMilliseconds;
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true);
            if (!texture.LoadImage(array, true)) throw new InvalidDataException("LoadImage failed");
            var main = watch.Elapsed.TotalMilliseconds;
            texture.GetNativeTexturePtr();
            var synced = watch.Elapsed.TotalMilliseconds;
            if (run == 0)
                Log($"LoadImage warm-up main={main:0.0} ms sync={synced:0.0} ms size={texture.width}x{texture.height} format={texture.format} mips={texture.mipmapCount}");
            else
            {
                marshal.Add(marshalled);
                loadImage.Add(main);
                loadImageSync.Add(synced);
            }
            Keep(shown, "png-loadimage", texture, run, () => Reload(png));
        }
        Report("B1 LoadImage(png) main thread incl. Il2Cpp array copy", loadImage);
        Report("B1 LoadImage(png) main thread + render-thread sync", loadImageSync);
        Report("B1 Il2Cpp array copy of PNG bytes (part of main)", marshal);

        var worker = new List<double>();
        var workerDecode = new List<double>();
        var uploadMain = new List<double>();
        var uploadSync = new List<double>();
        var uploadMarshal = new List<double>();
        for (var run = 0; run < Runs + 1; run++)
        {
            double decodeMs = 0, totalMs = 0;
            int width = 0, height = 0, mipCount = 0;
            var chain = Task.Run(() =>
            {
                var watch = Stopwatch.StartNew();
                var rgba = PngDecoder.DecodeRgba(png, out width, out height);
                decodeMs = watch.Elapsed.TotalMilliseconds;
                var result = PngDecoder.WithMipChain(rgba, width, height, out mipCount);
                totalMs = watch.Elapsed.TotalMilliseconds;
                return result;
            }).GetAwaiter().GetResult();
            var main = Stopwatch.StartNew();
            var array = new Il2CppStructArray<byte>(chain);
            var marshalled = main.Elapsed.TotalMilliseconds;
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, true);
            texture.LoadRawTextureData(array);
            texture.Apply(false, true);
            var mainMs = main.Elapsed.TotalMilliseconds;
            texture.GetNativeTexturePtr();
            var syncMs = main.Elapsed.TotalMilliseconds;
            if (run == 0)
                Log($"Worker warm-up decode={decodeMs:0.0} total={totalMs:0.0} main={mainMs:0.0} mips={mipCount}/{texture.mipmapCount} bytes={chain.Length}");
            else
            {
                workerDecode.Add(decodeMs);
                worker.Add(totalMs);
                uploadMarshal.Add(marshalled);
                uploadMain.Add(mainMs);
                uploadSync.Add(syncMs);
            }
            Keep(shown, "png-worker-rgba32", texture, run, () => UploadRgba(chain, width, height));
        }
        Report("B2 worker PNG decode only", workerDecode);
        Report("B2 worker PNG decode + box mip chain", worker);
        Report("B2 main LoadRawTextureData(RGBA32 chain) + Apply(false) incl. array copy", uploadMain);
        Report("B2 main + render-thread sync", uploadSync);
        Report("B2 Il2Cpp array copy of 22 MB chain (part of main)", uploadMarshal);

        TrySetPixelDataVariant(png);

        foreach (var format in CompressedFormats)
            try { BenchmarkCompressed(format, shown); }
            catch (Exception error) { Log($"{format} failed: {error.GetType().Name}: {error.Message}"); }

        CompareReadback(shown);
        return shown;
    }

    private static void BenchmarkCompressed(string format, List<Shown> shown)
    {
        var file = Path.Combine(Directory, format + ".gh3tex");
        var read = new List<double>();
        var main = new List<double>();
        var sync = new List<double>();
        for (var run = 0; run < Runs + 1; run++)
        {
            var watch = Stopwatch.StartNew();
            var data = File.ReadAllBytes(file);
            var readMs = watch.Elapsed.TotalMilliseconds;
            var texture = UploadCompressed(data, out var header);
            var mainMs = watch.Elapsed.TotalMilliseconds;
            texture.GetNativeTexturePtr();
            var syncMs = watch.Elapsed.TotalMilliseconds;
            if (run == 0)
                Log($"{format} warm-up {header} read={readMs:0.0} main={mainMs:0.0} format={texture.format} mips={texture.mipmapCount}");
            else
            {
                read.Add(readMs);
                main.Add(mainMs);
                sync.Add(syncMs);
            }
            Keep(shown, format, texture, run, () => UploadCompressed(File.ReadAllBytes(file), out _));
        }
        Report($"B3 {format} file read (part of main)", read);
        Report($"B3 {format} read + array copy + LoadRawTextureData + Apply(false) main thread", main);
        Report($"B3 {format} main + render-thread sync", sync);
    }

    private static void TrySetPixelDataVariant(byte[] png)
    {
        try
        {
            var times = new List<double>();
            for (var run = 0; run < Runs + 1; run++)
            {
                var rgba = PngDecoder.DecodeRgba(png, out var width, out var height);
                var watch = Stopwatch.StartNew();
                var array = new Il2CppStructArray<byte>(rgba);
                var texture = new Texture2D(width, height, TextureFormat.RGBA32, true);
                texture.SetPixelData(array, 0, 0);
                texture.Apply(true, true);
                texture.GetNativeTexturePtr();
                if (run > 0) times.Add(watch.Elapsed.TotalMilliseconds);
                Object.Destroy(texture);
            }
            Report("B2b main SetPixelData(level 0) + Apply(updateMipmaps=true) + sync (Unity builds mips)", times);
        }
        catch (Exception error)
        {
            Log($"B2b SetPixelData variant failed: {error.GetType().Name}: {error.Message}");
        }
    }

    private static Texture2D Reload(byte[] png)
    {
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true);
        texture.LoadImage(new Il2CppStructArray<byte>(png), true);
        return texture;
    }

    private static Texture2D UploadRgba(byte[] chain, int width, int height)
    {
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, true);
        texture.LoadRawTextureData(new Il2CppStructArray<byte>(chain));
        texture.Apply(false, true);
        return texture;
    }

    private static Texture2D UploadCompressed(byte[] data, out string header)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x54334847) throw new InvalidDataException("Not a gh3tex file");
        var format = (TextureFormat)BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8));
        var width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12));
        var height = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16));
        var mips = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(20));
        header = $"{format} {width}x{height} mips={mips} payload={data.Length - 24}";
        var texture = new Texture2D(width, height, format, mips, false);
        texture.LoadRawTextureData(new Il2CppStructArray<byte>(data[24..]));
        texture.Apply(false, true);
        return texture;
    }

    // Run 1 is kept for display; a second copy with a strong mip bias proves the uploaded small mip levels.
    private static void Keep(List<Shown> shown, string label, Texture2D texture, int run, Func<Texture2D> copy)
    {
        if (run != 1)
        {
            Object.Destroy(texture);
            return;
        }
        var biased = copy();
        foreach (var kept in new[] { texture, biased })
        {
            kept.hideFlags = HideFlags.DontUnloadUnusedAsset;
            kept.filterMode = FilterMode.Trilinear;
            kept.wrapMode = TextureWrapMode.Clamp;
        }
        biased.mipMapBias = 4;
        shown.Add(new Shown(label, texture, biased));
    }

    private static void Report(string label, List<double> values)
    {
        var sorted = values.OrderBy(value => value).ToList();
        var median = sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        Log($"{label}: n={sorted.Count} median={median:0.0} ms max={sorted[^1]:0.0} ms min={sorted[0]:0.0} ms all=" +
            string.Join(",", values.Select(value => value.ToString("0.0", CultureInfo.InvariantCulture))));
    }

    // GPU readback: mean absolute difference against the LoadImage texture at mip 0 (2048 x 2048) and mip 5 (64 x 64).
    private static void CompareReadback(List<Shown> shown)
    {
        var reference = shown.First(item => item.Label == "png-loadimage");
        var referenceTop = ReadBack(reference.Texture, 2048);
        var referenceSmall = ReadBack(reference.Texture, 64);
        foreach (var item in shown)
        {
            var top = ReadBack(item.Texture, 2048);
            var small = ReadBack(item.Texture, 64);
            var channels = item.Label == "bc5" ? 2 : 3;
            Log($"readback {item.Label}: mip0 meanAbsDiff={Difference(top, referenceTop, channels):0.00} " +
                $"mip5 meanAbsDiff={Difference(small, referenceSmall, channels):0.00} mip5 meanRGB={Mean(small)} (0-255; bc5 compares R,G only)");
        }
    }

    private static byte[] ReadBack(Texture2D texture, int size)
    {
        var target = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(texture, target);
        var previous = RenderTexture.active;
        RenderTexture.active = target;
        var output = new Texture2D(size, size, TextureFormat.RGBA32, false);
        output.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);
        output.Apply(false, false);
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(target);
        var pixels = output.GetPixels32();
        var bytes = new byte[pixels.Length * 4];
        for (var i = 0; i < pixels.Length; i++)
        {
            var pixel = pixels[i];
            bytes[i * 4] = pixel.r;
            bytes[i * 4 + 1] = pixel.g;
            bytes[i * 4 + 2] = pixel.b;
            bytes[i * 4 + 3] = pixel.a;
        }
        Object.Destroy(output);
        return bytes;
    }

    private static double Difference(byte[] a, byte[] b, int channels)
    {
        double sum = 0;
        long count = 0;
        for (var i = 0; i < a.Length; i += 4)
            for (var c = 0; c < channels; c++, count++)
                sum += Math.Abs(a[i + c] - b[i + c]);
        return sum / count;
    }

    private static string Mean(byte[] data)
    {
        double r = 0, g = 0, b = 0;
        for (var i = 0; i < data.Length; i += 4)
        {
            r += data[i];
            g += data[i + 1];
            b += data[i + 2];
        }
        var pixels = data.Length / 4.0;
        return $"{r / pixels:0.0}/{g / pixels:0.0}/{b / pixels:0.0}";
    }

    // Top row: each texture at 250 px (minified, trilinear). Bottom row: the same data with mipMapBias 4.
    private static void Show(List<Shown> shown)
    {
        var canvas = new GameObject("GH3 spike textures").AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.overrideSorting = true;
        canvas.sortingOrder = 1000;
        Object.DontDestroyOnLoad(canvas.gameObject);
        var background = new GameObject("background").AddComponent<RawImage>();
        background.transform.SetParent(canvas.transform, false);
        background.color = Color.black;
        Place(background.rectTransform, 0, 0, Screen.width, 560);
        for (var i = 0; i < shown.Count; i++)
        {
            foreach (var (texture, y) in new[] { (shown[i].Texture, 290f), (shown[i].Biased, 20f) })
            {
                var image = new GameObject(shown[i].Label).AddComponent<RawImage>();
                image.transform.SetParent(canvas.transform, false);
                image.texture = texture;
                Place(image.rectTransform, 10 + i * 262, y, 250, 250);
            }
            Log($"tile {i}: {shown[i].Label} (x={10 + i * 262})");
        }
    }

    private static void Place(RectTransform rect, float x, float y, float width, float height)
    {
        rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.zero;
        rect.anchoredPosition = new Vector2(x, y);
        rect.sizeDelta = new Vector2(width, height);
    }
}
