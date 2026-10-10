using System.Diagnostics;

namespace Gh3Skin.Packages;

// Collects the pre-encoded textures of one GLB and writes them to the cache (TextureCodec.ForCache: a copy, or a decode to
// RGBA32 for a GPU without BC1/BC3); the importer flushes before it reads the next GLB, so only one GLB stays in memory.
internal sealed class TextureJobs
{
    private readonly List<(string Path, Func<byte[]> Data)> _jobs = new();
    private readonly bool _compressed;
    private readonly int _threads;
    public int Written { get; private set; }
    public TimeSpan Time { get; private set; }

    public TextureJobs(bool compressed, int threads)
    {
        _compressed = compressed;
        _threads = Math.Max(1, threads);
    }

    public void Add(string pathWithoutExtension, Func<byte[]> data) => _jobs.Add((pathWithoutExtension, data));

    public void Flush()
    {
        if (_jobs.Count == 0) return;
        var clock = Stopwatch.StartNew();
        void Write((string Path, Func<byte[]> Data) job)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(job.Path)!);
            File.WriteAllBytes(job.Path + ".tex", TextureCodec.ForCache(job.Data(), _compressed, System.IO.Path.GetFileName(job.Path)));
        }
        if (_compressed) foreach (var job in _jobs) Write(job);
        else
            try { Parallel.ForEach(_jobs, new ParallelOptions { MaxDegreeOfParallelism = _threads }, Write); }
            catch (AggregateException error) when (error.InnerExceptions.Count > 0) { throw error.InnerExceptions[0]; }
        Written += _jobs.Count;
        _jobs.Clear();
        Time += clock.Elapsed;
    }
}
