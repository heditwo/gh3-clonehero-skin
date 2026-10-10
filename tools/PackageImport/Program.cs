using System.Diagnostics;
using Gh3Skin.Packages;

// Runs the mod's package importer outside the game, for tests and for the C# against Python cache comparison
// (pipeline/package/cachecheck.py).
// Usage: dotnet run --project tools/PackageImport -c Release -- PACKAGES_FOLDER CACHE_FOLDER [--rgba] [--threads N] [--workers N] [--custom FOLDER]...
var arguments = args.ToList();
if (arguments.Count < 2)
{
    Console.Error.WriteLine("usage: PackageImport PACKAGES_FOLDER CACHE_FOLDER [--rgba] [--threads N] [--workers N] [--custom FOLDER]...");
    return 2;
}
var custom = new List<string>();
for (var i = arguments.IndexOf("--custom"); i >= 0; i = arguments.IndexOf("--custom"))
{
    custom.Add(Path.GetFullPath(arguments[i + 1]));
    arguments.RemoveRange(i, 2);
}
var threads = arguments.IndexOf("--threads") is var t and >= 0 ? int.Parse(arguments[t + 1]) : Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
var workers = arguments.IndexOf("--workers") is var w and >= 0 ? int.Parse(arguments[w + 1]) : Math.Clamp(Environment.ProcessorCount / 4, 1, 2);
SemanticVersion.TryParse(Gh3Skin.ModInfo.Version, out var version);
var settings = new PackageSettings(Path.GetFullPath(arguments[0]), custom, Path.GetFullPath(arguments[1]), !arguments.Contains("--rgba"),
    "Tool", "None", version, "", threads, workers);
var manager = new PackageManager(settings);
var clock = Stopwatch.StartNew();
manager.RunNow();
while (manager.TryDequeueLog(out var line)) Console.WriteLine((line.Warning ? "WARNING " : "") + line.Text);
Console.WriteLine($"peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1e6:0} MB, {clock.Elapsed.TotalSeconds:0.0} s");
return manager.Packages.Any(p => p.State != PackageState.Ready) ? 1 : 0;
