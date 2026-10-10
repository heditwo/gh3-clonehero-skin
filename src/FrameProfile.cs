using System.Diagnostics;
using MelonLoader;

namespace Gh3Skin;

// GH3_SKIN_PROFILE=1 logs per-subsystem milliseconds and managed allocations per frame once a second.
internal static class FrameProfile
{
    public static readonly bool Enabled = SkinEnvironment.IsOne(SkinEnvironment.Profile);
    private static readonly Dictionary<string, double> Times = new();
    private static readonly Dictionary<string, long> Allocations = new();
    private static readonly Stopwatch Clock = new();
    private static int _frames;
    private static double _songStart = double.NaN;
    private static int _collections;

    public static void Measure(string name, Action action)
    {
        if (!Enabled) { action(); return; }
        var start = Stopwatch.GetTimestamp();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        action();
        Times[name] = Times.GetValueOrDefault(name) + (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        Allocations[name] = Allocations.GetValueOrDefault(name) + GC.GetAllocatedBytesForCurrentThread() - allocated;
    }

    public static void Add(string name, double milliseconds)
    {
        if (Enabled) Times[name] = Times.GetValueOrDefault(name) + milliseconds;
    }

    public static void EndFrame(double songTime, string detail = "")
    {
        if (!Enabled) return;
        _frames++;
        if (double.IsNaN(_songStart)) { _songStart = songTime; Clock.Restart(); }
        if (Clock.Elapsed.TotalSeconds < 1) return;
        var collections = GC.CollectionCount(0);
        MelonLogger.Msg($"GH3 profile song {_songStart:0.0}s: {_frames / Clock.Elapsed.TotalSeconds:0} fps; gen0 {collections - _collections}; ms/frame " +
            string.Join(", ", Times.OrderByDescending(pair => pair.Value).Select(pair => $"{pair.Key} {pair.Value / _frames:0.00}")) +
            "; KB/frame " + string.Join(", ", Allocations.Where(pair => pair.Value > 0).OrderByDescending(pair => pair.Value)
                .Select(pair => $"{pair.Key} {pair.Value / 1024.0 / _frames:0.0}")) + detail);
        _collections = collections;
        Times.Clear();
        Allocations.Clear();
        _frames = 0;
        _songStart = double.NaN;
    }
}
