using System.Diagnostics;
using MelonLoader;
using UnityEngine;

namespace Gh3Skin;

// GH3_SKIN_TIMING=1 logs load phases with the time since the process start, the managed heap, and the resident memory,
// and logs main-thread frames longer than 50 ms (docs/VERIFICATION.md, load times).
internal static class LoadTiming
{
    public static readonly bool Enabled = SkinEnvironment.IsOne(SkinEnvironment.Timing);
    public static int Reloads = Enabled && int.TryParse(SkinEnvironment.Get(SkinEnvironment.TimingReloads), out var count) ? count : 0;
    private static readonly DateTime ProcessStart = Process.GetCurrentProcess().StartTime.ToUniversalTime();
    private static readonly Stopwatch Phase = Stopwatch.StartNew();
    private static string _scene = "";
    private static int _sceneFrames;
    private static float _lastFrame;

    public static double Seconds => (DateTime.UtcNow - ProcessStart).TotalSeconds;

    public static void Mark(string phase)
    {
        if (!Enabled) return;
        using var process = Process.GetCurrentProcess();
        MelonLogger.Msg($"[timing] {Seconds:0.000} s {phase} (+{Phase.Elapsed.TotalMilliseconds:0} ms; heap {GC.GetTotalMemory(false) / 1e6:0} MB, " +
            $"rss {process.WorkingSet64 / 1e6:0} MB, gen0/1/2 {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}, " +
            $"jit {System.Runtime.JitInfo.GetCompiledMethodCount()} methods in {System.Runtime.JitInfo.GetCompilationTime().TotalMilliseconds:0} ms)");
        Phase.Restart();
    }

    // Milliseconds of each step of a longer phase, in one line.
    public sealed class Steps
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<string> _parts = new();

        public void Step(string name)
        {
            if (!Enabled) return;
            _parts.Add($"{name} {_clock.Elapsed.TotalMilliseconds:0}");
            _clock.Restart();
        }

        public void Finish(string phase)
        {
            if (!Enabled) return;
            Mark($"{phase} [ms: {string.Join(", ", _parts)}]");
        }
    }

    private static readonly Dictionary<string, double> Sections = new();

    // Adds the time since `start` (Stopwatch.GetTimestamp) to a named section; Finish prints and clears the sections.
    public static void Add(string section, long start)
    {
        if (!Enabled) return;
        Sections[section] = Sections.GetValueOrDefault(section) + (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
    }

    public static long Now => Enabled ? Stopwatch.GetTimestamp() : 0;

    public static void FlushSections(string phase)
    {
        if (!Enabled || Sections.Count == 0) return;
        MelonLogger.Msg($"[timing] {phase} sections [ms: {string.Join(", ", Sections.OrderByDescending(pair => pair.Value).Select(pair => $"{pair.Key} {pair.Value:0}"))}]");
        Sections.Clear();
    }

    public static void SceneLoaded(string scene)
    {
        if (!Enabled) return;
        _scene = scene;
        _sceneFrames = 0;
        Mark($"scene {scene} loaded");
    }

    public static void Frame()
    {
        if (!Enabled) return;
        var now = Time.realtimeSinceStartup;
        var length = now - _lastFrame;
        _lastFrame = now;
        if (++_sceneFrames is 1 or 30) Mark($"frame {_sceneFrames} of scene {_scene}");
        else if (_sceneFrames > 2 && length > 0.05f) MelonLogger.Msg($"[timing] {Seconds:0.000} s long frame {length * 1000:0} ms in {_scene}");
    }
}
