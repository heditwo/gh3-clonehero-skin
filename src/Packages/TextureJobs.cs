using System.Diagnostics;

namespace Gh3Skin.Packages;

// Collects the textures of one GLB and encodes them in parallel; the importer flushes before it reads the next GLB, so
// only one GLB stays in memory.
internal sealed class TextureJobs
{
    private readonly List<(string Path, Func<byte[]> Png)> _jobs = new();
    private readonly bool _compressed;
    private readonly int _threads;
    public int Written { get; private set; }
    public TimeSpan Time { get; private set; }

    public TextureJobs(bool compressed, int threads)
    {
        _compressed = compressed;
        _threads = Math.Max(1, threads);
    }

    public void Add(string pathWithoutExtension, Func<byte[]> png) => _jobs.Add((pathWithoutExtension, png));

    public void Flush()
    {
        if (_jobs.Count == 0) return;
        var clock = Stopwatch.StartNew();
        Parallel.ForEach(_jobs, new ParallelOptions { MaxDegreeOfParallelism = _threads }, job =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(job.Path)!);
            File.WriteAllBytes(job.Path + ".tex", TextureCodec.Encode(job.Png(), _compressed));
        });
        Written += _jobs.Count;
        _jobs.Clear();
        Time += clock.Elapsed;
    }
}
