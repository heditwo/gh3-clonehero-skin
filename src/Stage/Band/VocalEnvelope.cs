using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Gh3Skin;

// The loudness of the singer from the vocal stem of the chart folder (vocals.ogg, vocals_1.opus, ...: the Clone Hero
// stem names). A worker thread decodes the stems before the song and keeps the envelope in the mod cache; the lip sync
// opens the mouth with it. Without a stem the lip sync uses the notes only.
internal sealed class VocalEnvelope
{
    public const int FramesPerSecond = 100;
    private static readonly string[] StemNames = { "vocals", "vocals_1", "vocals_2" };
    private static readonly string[] StemExtensions = { ".ogg", ".opus", ".mp3", ".wav" };
    private const string CacheMagic = "GH3VOC01";
    private const int CacheHeader = 16;
    // Tuned: 20 ms RMS windows; the level rises with a 15 ms and falls with a 90 ms time constant (a syllable onset
    // shows at once, the mouth does not snap shut in a short dip).
    private const double WindowSeconds = 0.02;
    private const double AttackSeconds = 0.015;
    private const double ReleaseSeconds = 0.09;
    // Tuned: the level 1 is the 95th percentile of the frames above 10 % of the loudest frame (the sung frames); the
    // levels below the gate (bleed and reverb of other stems in the vocal track) give 0.
    private const double LoudPercentile = 0.95;
    private const float SungGate = 0.1f;
    private const float NoiseGate = 0.08f;
    private const float MaximumLevel = 2;
    private const int StoredSteps = 255;
    private const int MaximumFrames = StageLimits.MaximumSeconds * FramesPerSecond;
    private readonly byte[] _levels;

    public string Source { get; }
    public double Seconds => _levels.Length / (double)FramesPerSecond;

    private VocalEnvelope(byte[] levels, string source)
    {
        _levels = levels;
        Source = source;
    }

    // The loudness at the song time: 0 silent, 1 the normal loud singing of the song, up to 2.
    public float At(double time)
    {
        var position = time * FramesPerSecond;
        if (!(position >= 0) || position >= _levels.Length - 1) return 0;
        var index = (int)position;
        var fraction = (float)(position - index);
        return (_levels[index] + (_levels[index + 1] - _levels[index]) * fraction) * MaximumLevel / StoredSteps;
    }

    // The envelope of the song that loads now (GameplayStage starts it after the chart is read); null without a stem.
    public static Task<VocalEnvelope?>? Current { get; set; }

    public static string[] Stems(string folder) => !Directory.Exists(folder) ? Array.Empty<string>() :
        StemNames.SelectMany(name => StemExtensions.Select(extension => Path.Combine(folder, name + extension))).Where(File.Exists)
            .GroupBy(path => Path.GetFileNameWithoutExtension(path)).Select(group => group.First()).ToArray();

    // Decodes the stems on a worker thread (or reads the cache). `decode` opens a file, asks for a sample sink with the
    // sample rate, and gives it the mono samples in blocks.
    public static Task<VocalEnvelope?>? Start(string folder, string cacheFolder, Action<string, Func<int, Action<float[], int>>> decode)
    {
        var stems = Stems(folder);
        if (stems.Length == 0) return null;
        return Task.Run(() =>
        {
            var key = CacheKey(stems);
            var cache = Path.Combine(cacheFolder, key + ".bin");
            var cached = ReadCache(cache);
            if (cached != null) return new VocalEnvelope(cached, "cache");
            float[]? combined = null;
            foreach (var stem in stems)
            {
                Meter? meter = null;
                decode(stem, rate => (meter = new Meter(rate)).Add);
                if (meter == null) continue;
                var raw = Smooth(meter.Finish());
                combined = combined == null ? raw : Max(combined, raw);
            }
            if (combined == null) return null;
            var stored = Normalize(combined);
            try
            {
                Directory.CreateDirectory(cacheFolder);
                WriteCache(cache, stored);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return (VocalEnvelope?)new VocalEnvelope(stored, string.Join(", ", stems.Select(Path.GetFileName)));
        });
    }

    public static VocalEnvelope FromLevels(float[] levels) => new(Normalize(levels), "levels");

    // The envelope of mono samples (checks and tools; the game decodes the stems in Start).
    public static VocalEnvelope FromSamples(float[] samples, int rate)
    {
        var meter = new Meter(rate);
        meter.Add(samples, samples.Length);
        return new VocalEnvelope(Normalize(Smooth(meter.Finish())), "samples");
    }

    // The RMS of each window of WindowSeconds at FramesPerSecond hops, from the mono samples in blocks. It keeps only
    // the sum of squares of each hop (a 4-minute stem at 48 kHz has 11.5 million samples).
    private sealed class Meter
    {
        private readonly List<double> _hops = new();
        private readonly int _hop;
        private double _sum;
        private int _count;

        public Meter(int rate) => _hop = Math.Max(1, rate / FramesPerSecond);

        public void Add(float[] samples, int count)
        {
            for (var i = 0; i < count; i++)
            {
                _sum += samples[i] * (double)samples[i];
                if (++_count < _hop) continue;
                if (_hops.Count < MaximumFrames) _hops.Add(_sum);
                _sum = 0;
                _count = 0;
            }
        }

        public float[] Finish()
        {
            var span = Math.Max(1, (int)Math.Round(WindowSeconds * FramesPerSecond));
            var result = new float[_hops.Count];
            for (var frame = 0; frame < result.Length; frame++)
            {
                var start = Math.Max(0, frame - span / 2);
                var end = Math.Min(_hops.Count, start + span);
                double sum = 0;
                for (var i = start; i < end; i++) sum += _hops[i];
                result[frame] = (float)Math.Sqrt(sum / ((end - start) * _hop));
            }
            return result;
        }
    }

    public static float[] Smooth(float[] levels)
    {
        var attack = (float)(1 - Math.Exp(-1 / (AttackSeconds * FramesPerSecond)));
        var release = (float)(1 - Math.Exp(-1 / (ReleaseSeconds * FramesPerSecond)));
        var result = new float[levels.Length];
        float level = 0;
        for (var i = 0; i < levels.Length; i++)
        {
            level += (levels[i] - level) * (levels[i] > level ? attack : release);
            result[i] = level;
        }
        return result;
    }

    private static float[] Max(float[] a, float[] b)
    {
        var result = new float[Math.Max(a.Length, b.Length)];
        for (var i = 0; i < result.Length; i++) result[i] = Math.Max(i < a.Length ? a[i] : 0, i < b.Length ? b[i] : 0);
        return result;
    }

    public static byte[] Normalize(float[] levels)
    {
        var loudest = levels.Length == 0 ? 0 : levels.Max();
        var sung = levels.Where(level => level > loudest * SungGate).OrderBy(level => level).ToArray();
        var reference = sung.Length == 0 ? 1 : Math.Max(sung[(int)((sung.Length - 1) * LoudPercentile)], float.Epsilon);
        var result = new byte[levels.Length];
        for (var i = 0; i < levels.Length; i++)
        {
            var level = Math.Max(0, (levels[i] / reference - NoiseGate) / (1 - NoiseGate));
            result[i] = (byte)Math.Round(Math.Min(MaximumLevel, level) / MaximumLevel * StoredSteps);
        }
        return result;
    }

    // The stem files with their sizes and times: a changed stem gives a new key.
    private static string CacheKey(string[] stems)
    {
        var text = string.Join("\n", stems.Select(path =>
        {
            var info = new FileInfo(path);
            return $"{Path.GetFullPath(path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CacheMagic + text)))[..32].ToLowerInvariant();
    }

    private static byte[]? ReadCache(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var data = File.ReadAllBytes(path);
            if (data.Length < CacheHeader || Encoding.ASCII.GetString(data, 0, CacheMagic.Length) != CacheMagic) return null;
            var count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(CacheMagic.Length));
            if (count < 0 || count > MaximumFrames || data.Length != CacheHeader + count) return null;
            return data[CacheHeader..];
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void WriteCache(string path, byte[] levels)
    {
        var data = new byte[CacheHeader + levels.Length];
        Encoding.ASCII.GetBytes(CacheMagic, data);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(CacheMagic.Length), levels.Length);
        levels.CopyTo(data, CacheHeader);
        File.WriteAllBytes(path + ".tmp", data);
        File.Move(path + ".tmp", path, true);
    }
}
