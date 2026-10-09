using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Gh3Skin.Packages;

internal enum PackageState { Pending, Importing, Ready, Failed }

internal sealed class InstalledPackage
{
    public string Location { get; }
    public PackageManifest Manifest { get; }
    public int Order { get; }
    public volatile PackageState State = PackageState.Pending;
    public string CacheDirectory { get; set; } = "";
    public string Error { get; set; } = "";
    public bool SongPackage { get; init; }

    public InstalledPackage(string location, PackageManifest manifest, int order)
    {
        Location = location;
        Manifest = manifest;
        Order = order;
    }

    public string Label => $"{Manifest.Id} {Manifest.Version} ({Location})";
}

internal sealed record PackageSettings(
    string ModPackages,
    IReadOnlyList<string> CustomContent,
    string CacheRoot,
    bool CompressedTextures,
    string Platform,
    string GraphicsApi,
    SemanticVersion ModVersion,
    string PreferredStage,
    int TextureThreads);

// Finds packages, checks their manifests and requirements, and imports one package at a time on a background thread
// into the per-machine cache (PACKAGE-FORMAT.md sections 13 and 14). Bad packages are skipped with one log line each.
internal sealed class PackageManager
{
    public const int ImporterVersion = 1;
    public const int MaxGroupDepth = 4;
    private readonly PackageSettings _settings;
    private readonly ConcurrentQueue<(bool Warning, string Text)> _log = new();
    private readonly object _gate = new();
    private readonly List<InstalledPackage> _packages = new();
    private readonly List<string> _priority = new();
    private Thread? _thread;
    private bool _stopping;
    // Cache folder renames, index.json writes, and the clean-up do not overlap.
    private readonly object _cacheGate = new();
    public volatile bool Scanned;
    public volatile bool Finished;
    public volatile string Activity = "starting";

    public PackageManager(PackageSettings settings) => _settings = settings;

    public string TextureFormat => _settings.CompressedTextures ? "bc1-bc3" : "rgba32";

    // The scan and the cache check run on the calling thread (about 0.1 s). A worker thread starts only when a package
    // needs an import, so a warm start runs no package code in the background.
    public void Start()
    {
        var clock = Stopwatch.StartNew();
        Scan();
        foreach (var package in Packages.OrderBy(p => p.Manifest.Kind == PackageKind.Library ? 0 : 1))
            if (TryCacheHit(package)) package.State = PackageState.Ready;
        if (Packages.All(p => p.State != PackageState.Pending))
        {
            Activity = "finished";
            Finished = true;
            Log($"all packages ready from the cache in {clock.ElapsedMilliseconds} ms");
            // The clean-up deletes files; it does not need the main thread.
            Task.Run(CleanCache);
            return;
        }
        StartWorker();
    }

    private void StartWorker()
    {
        lock (_gate)
        {
            if (_thread is { IsAlive: true } && !_stopping) return;
            Finished = false;
            _stopping = false;
            _thread = new Thread(Run) { IsBackground = true, Name = "GH3 package importer", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }
    }

    // A venue package in a song folder (StageSelection.SongPackage) is found when the song starts, not at the scan.
    // Returns the package (already known or added now, then imported in the background) or null with a warning.
    public InstalledPackage? AddSongPackage(string path)
    {
        lock (_gate)
            if (_packages.FirstOrDefault(p => p.Location == path) is { } known) return known;
        try
        {
            using var source = PackageSource.At(path);
            if (!source.Contains("manifest.json")) throw new PackageException("manifest.json is missing");
            var manifest = PackageManifest.Parse(source.ReadAll("manifest.json"), PackageKind.Stage, _settings.ModVersion);
            VerifiedPackage.CheckFileList(source, manifest);
            var missing = manifest.Requires.FirstOrDefault(requirement =>
                !Packages.Any(p => p.Manifest.Id == requirement.Id && p.Manifest.Version.Satisfies(requirement.Version)));
            if (missing.Id != null) throw new PackageException($"it requires {missing.Id} {missing.Version} (same major version or later), which is not installed");
            var package = new InstalledPackage(path, manifest, int.MaxValue) { SongPackage = true };
            if (TryCacheHit(package)) package.State = PackageState.Ready;
            lock (_gate) _packages.Add(package);
            Log($"song venue {package.Label} {(package.State == PackageState.Ready ? "ready from the cache" : "imports now")}");
            if (package.State == PackageState.Pending) StartWorker();
            return package;
        }
        catch (Exception error) when (error is PackageException or InvalidDataException or IOException or System.Text.Json.JsonException or UnauthorizedAccessException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            Warn($"skipped the song venue {path}: {error.Message}");
            return null;
        }
    }

    // Scan and import on the calling thread (tools and tests).
    public void RunNow()
    {
        Scan();
        Run();
    }

    private void Scan()
    {
        var clock = Stopwatch.StartNew();
        Activity = "scanning the package folders";
        try { Discover(); }
        catch (Exception error) { Warn($"the package scan stopped: {error.Message}"); }
        Scanned = true;
        Log($"found {_packages.Count} usable package(s) in {clock.ElapsedMilliseconds} ms");
    }

    private bool TryCacheHit(InstalledPackage package)
    {
        try
        {
            if (KnownKey(package.Location) is not { } key) return false;
            var folder = Path.Combine(_settings.CacheRoot, $"{key}-i{ImporterVersion}");
            if (!CacheMatches(folder)) return false;
            package.CacheDirectory = folder;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    // Discovery only (tests): finds and checks the packages without an import.
    public void DiscoverOnly()
    {
        Discover();
        Scanned = true;
    }

    public bool TryDequeueLog(out (bool Warning, string Text) line) => _log.TryDequeue(out line);
    private void Log(string text) => _log.Enqueue((false, "GH3 packages: " + text));
    private void Warn(string text) => _log.Enqueue((true, "GH3 packages: " + text));

    public InstalledPackage? Find(PackageKind kind, string id)
    {
        lock (_gate) return _packages.FirstOrDefault(p => p.Manifest.Kind == kind && p.Manifest.Id == id && !p.SongPackage);
    }

    public IReadOnlyList<InstalledPackage> Packages
    {
        get { lock (_gate) return _packages.ToList(); }
    }

    // The stage that a song waits for imports next.
    public void Prioritize(InstalledPackage package)
    {
        lock (_gate) if (!_priority.Contains(package.Location)) _priority.Insert(0, package.Location);
    }

    private void Run()
    {
        try
        {
            var clock = Stopwatch.StartNew();
            CleanTemporaryFolders();
            while (true)
            {
                InstalledPackage? next;
                lock (_gate)
                {
                    next = _packages.Where(p => p.State == PackageState.Pending)
                        .OrderBy(p => p.Manifest.Kind == PackageKind.Library ? 0 : 1)
                        .ThenBy(p => _priority.IndexOf(p.Location) is var index and >= 0 ? index : int.MaxValue)
                        .ThenBy(p => p.Manifest.Id == _settings.PreferredStage ? 0 : 1)
                        .ThenBy(p => p.Manifest.Kind == PackageKind.Character ? 0 : 1)
                        .ThenBy(p => p.Manifest.Id, StringComparer.Ordinal).FirstOrDefault();
                    if (next != null) next.State = PackageState.Importing;
                    else _stopping = true;
                }
                if (next == null) break;
                Prepare(next);
            }
            Activity = "finished";
            Log($"all packages ready; the imports took {clock.Elapsed.TotalSeconds:0.0} s");
            CleanCache();
        }
        catch (Exception error)
        {
            Warn($"the package importer stopped: {error}");
            lock (_gate)
                foreach (var package in _packages.Where(p => p.State is PackageState.Pending or PackageState.Importing))
                {
                    package.State = PackageState.Failed;
                    package.Error = "the importer stopped";
                }
        }
        finally
        {
            lock (_gate) if (_thread == Thread.CurrentThread) Finished = true;
        }
    }

    // Package files and folders: Custom/Stages and Custom/Characters of each Clone Hero content folder (group subfolders
    // to depth 4), then the packages that ship with the mod.
    public static IEnumerable<string> Candidates(PackageSettings settings)
    {
        var roots = settings.CustomContent.SelectMany(custom => new[] { Path.Combine(custom, "Stages"), Path.Combine(custom, "Characters") })
            .Append(settings.ModPackages);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root) || !seen.Add(Path.GetFullPath(root))) continue;
            foreach (var path in Scan(root, 0)) yield return path;
        }
    }

    private static IEnumerable<string> Scan(string folder, int depth)
    {
        var entries = new List<string>();
        try { entries.AddRange(Directory.EnumerateFileSystemEntries(folder)); }
        catch (Exception) { yield break; }
        entries.Sort(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (name.StartsWith('.')) continue;
            if (PackageManifest.KindOf(name) != null) yield return entry;
            else if (Directory.Exists(entry) && depth < MaxGroupDepth)
                foreach (var nested in Scan(entry, depth + 1)) yield return nested;
        }
    }

    private void Discover()
    {
        var found = new List<InstalledPackage>();
        var order = 0;
        foreach (var path in Candidates(_settings))
        {
            var kind = PackageManifest.KindOf(path)!.Value;
            try
            {
                using var source = PackageSource.At(path);
                if (!source.Contains("manifest.json")) throw new PackageException("manifest.json is missing");
                var manifest = PackageManifest.Parse(source.ReadAll("manifest.json"), kind, _settings.ModVersion);
                VerifiedPackage.CheckFileList(source, manifest);
                var folder = Path.GetFileName(Path.GetDirectoryName(path)) ?? "";
                if (kind == PackageKind.Character && folder.Equals("Stages", StringComparison.OrdinalIgnoreCase) ||
                    kind == PackageKind.Stage && folder.Equals("Characters", StringComparison.OrdinalIgnoreCase))
                    Warn($"{path} is a {PackageManifest.Extension(kind)} file in the {folder} folder; it loads as its extension says");
                found.Add(new InstalledPackage(path, manifest, order++));
            }
            catch (Exception error) when (error is PackageException or InvalidDataException or IOException or System.Text.Json.JsonException or UnauthorizedAccessException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                Warn($"skipped {path}: {error.Message}");
            }
        }
        var chosen = new List<InstalledPackage>();
        foreach (var group in found.GroupBy(p => (p.Manifest.Kind, p.Manifest.Id)))
        {
            var best = group.OrderByDescending(p => p.Manifest.Version).ThenBy(p => p.Order).First();
            foreach (var other in group.Where(p => p != best))
                Log($"{other.Label} is not used: {best.Label} has the same id and a version that is not lower");
            chosen.Add(best);
        }
        foreach (var package in chosen.OrderBy(p => p.Order))
        {
            var missing = package.Manifest.Requires.FirstOrDefault(requirement =>
                !chosen.Any(p => p.Manifest.Id == requirement.Id && p.Manifest.Version.Satisfies(requirement.Version)));
            if (missing.Id != null)
            {
                Warn($"skipped {package.Label}: it requires {missing.Id} {missing.Version} (same major version or later), which is not installed");
                continue;
            }
            if (package.Manifest.Kind == PackageKind.Character) Log($"found character {package.Label}");
            lock (_gate) _packages.Add(package);
        }
    }

    private void CleanTemporaryFolders()
    {
        if (!Directory.Exists(_settings.CacheRoot)) return;
        foreach (var folder in Directory.EnumerateDirectories(_settings.CacheRoot, ".import-*"))
            try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    private void Prepare(InstalledPackage package)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            foreach (var requirement in package.Manifest.Requires)
            {
                var dependency = Find(PackageKind.Library, requirement.Id) ?? Find(PackageKind.Stage, requirement.Id);
                if (dependency?.State != PackageState.Ready) throw new PackageException($"its required package {requirement.Id} did not import");
            }
            Activity = $"hashing {package.Manifest.Id}";
            var key = CacheKey(package.Location);
            Activity = $"checking the cache of {package.Manifest.Id}";
            var folder = Path.Combine(_settings.CacheRoot, $"{key}-i{ImporterVersion}");
            if (CacheMatches(folder))
            {
                package.CacheDirectory = folder;
                package.State = PackageState.Ready;
                Log($"{package.Manifest.Id} {package.Manifest.Version} ready from the cache in {clock.ElapsedMilliseconds} ms");
                return;
            }
            Import(package, key, folder, clock);
            package.CacheDirectory = folder;
            package.State = PackageState.Ready;
            // Give the large import buffers back before the next package, so the import memory stays at one package.
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
        }
        catch (Exception error)
        {
            package.Error = error.Message;
            package.State = PackageState.Failed;
            Warn($"skipped {package.Label}: {error.Message}");
        }
        finally { Activity = "idle"; }
    }

    private static void CopyPreview(VerifiedPackage package, string folder)
    {
        var preview = package.Manifest.Preview;
        if (preview.Length > 0 && package.Contains(preview)) package.CopyTo(preview, Path.Combine(folder, "preview.png"));
    }

    // Deletes the cache folders that no package uses: other importer versions, and keys that no installed package and no
    // index.json entry of an existing package file has. Only folder names of the cache layout inside the cache root are
    // deleted; links are never followed.
    public void CleanCache()
    {
        lock (_cacheGate)
        try
        {
            if (!Directory.Exists(_settings.CacheRoot)) return;
            var index = ReadIndex();
            var keep = new HashSet<string>(StringComparer.Ordinal);
            foreach (var package in Packages)
                if (package.CacheDirectory.Length > 0) keep.Add(Path.GetFileName(package.CacheDirectory));
            var stale = new List<string>();
            foreach (var (location, row) in index.ToList())
            {
                var exists = File.Exists(location) || Directory.Exists(location);
                if (exists && row is JsonObject entry && PackageJson.Text(entry["sha256"]) is { Length: 64 } key) keep.Add($"{key}-i{ImporterVersion}");
                if (!exists) stale.Add(location);
            }
            var removed = 0;
            long bytes = 0;
            foreach (var folder in Directory.EnumerateDirectories(_settings.CacheRoot))
            {
                var name = Path.GetFileName(folder);
                if (!CacheFolderName.IsMatch(name) || keep.Contains(name)) continue;
                var info = new DirectoryInfo(folder);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                try
                {
                    var size = info.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                    info.Delete(true);
                    removed++;
                    bytes += size;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Warn($"cache folder {name} could not be deleted: {error.Message}"); }
            }
            if (stale.Count > 0)
            {
                foreach (var location in stale) index.Remove(location);
                var indexPath = Path.Combine(_settings.CacheRoot, "index.json");
                PackageJson.Write(indexPath + ".tmp", index);
                File.Move(indexPath + ".tmp", indexPath, true);
            }
            if (removed > 0) Log($"deleted {removed} unused cache folder(s), {bytes / 1e6:0} MB");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Warn($"the cache clean-up stopped: {error.Message}");
        }
    }

    private static readonly System.Text.RegularExpressions.Regex CacheFolderName = new(@"\A[0-9a-f]{64}-i[0-9]{1,4}\z");

    private bool CacheMatches(string folder)
    {
        var path = Path.Combine(folder, "cache.json");
        if (!File.Exists(path)) return false;
        try
        {
            var cache = PackageJson.Object(File.ReadAllBytes(path));
            if (PackageJson.Text(cache["platform"]) == _settings.Platform && PackageJson.Text(cache["graphicsApi"]) == _settings.GraphicsApi &&
                PackageJson.Text(cache["textureFormat"]) == TextureFormat) return true;
            Log($"cache {Path.GetFileName(folder)} was made for {PackageJson.Text(cache["platform"])} {PackageJson.Text(cache["graphicsApi"])} " +
                $"{PackageJson.Text(cache["textureFormat"])}; it is made again");
        }
        catch (Exception error) when (error is IOException or PackageException or System.Text.Json.JsonException) { }
        return false;
    }

    private void Import(InstalledPackage package, string key, string folder, Stopwatch clock)
    {
        Directory.CreateDirectory(_settings.CacheRoot);
        var temporary = Path.Combine(_settings.CacheRoot, $".import-{key[..16]}-{Guid.NewGuid():N}");
        var startMemory = GC.GetTotalMemory(false);
        try
        {
            Activity = $"importing {package.Manifest.Id}";
            using var source = PackageSource.At(package.Location);
            using var verified = new VerifiedPackage(source, package.Manifest);
            var textures = new TextureJobs(_settings.CompressedTextures, _settings.TextureThreads);
            string detail;
            if (package.Manifest.Kind == PackageKind.Library)
            {
                var result = new LibraryImporter(verified, textures).Import(temporary);
                detail = $"{result.Models} models, {result.Clips} clips, {result.Samples} sounds, {result.Movies} movies";
            }
            else if (package.Manifest.Kind == PackageKind.Character)
            {
                var library = package.Manifest.Requires.Select(r => Find(PackageKind.Library, r.Id)).FirstOrDefault(p => p?.State == PackageState.Ready)
                    ?? throw new PackageException("a character needs the band library (requires gh3-band-v1)");
                var importer = new CharacterImporter(verified, textures, library.CacheDirectory);
                var result = importer.Import(temporary);
                CopyPreview(verified, temporary);
                verified.VerifyRemaining();
                foreach (var warning in importer.Warnings.Distinct().Take(8)) Warn($"{package.Manifest.Id}: {warning}");
                detail = $"{result.Role} profile, {result.Joints} joints, {result.Meshes} meshes, {result.Vertices} vertices";
            }
            else
            {
                var library = package.Manifest.Requires.Select(r => Find(PackageKind.Library, r.Id)).FirstOrDefault(p => p?.State == PackageState.Ready);
                var catalogPath = library == null ? "" : Path.Combine(library.CacheDirectory, "catalog.json");
                var catalog = File.Exists(catalogPath) ? PackageJson.Object(File.ReadAllBytes(catalogPath)) : null;
                var compiler = new StageCompiler(verified, textures, catalog);
                var result = compiler.Compile(temporary);
                CopyPreview(verified, temporary);
                verified.VerifyRemaining();
                foreach (var warning in compiler.Warnings.Distinct().Take(8)) Warn($"{package.Manifest.Id}: {warning}");
                detail = $"{result.Meshes} meshes, {result.Vertices} vertices, {result.Materials} materials, {result.Fixtures} fixtures, {result.Actors} actors";
            }
            PackageJson.Write(Path.Combine(temporary, "cache.json"), new JsonObject
            {
                ["key"] = $"{key}-i{ImporterVersion}", ["importer"] = ImporterVersion,
                ["package"] = new JsonObject
                {
                    ["id"] = package.Manifest.Id, ["version"] = package.Manifest.Version.ToString(), ["format"] = package.Manifest.Format, ["location"] = package.Location,
                },
                ["platform"] = _settings.Platform, ["graphicsApi"] = _settings.GraphicsApi, ["textureFormat"] = TextureFormat,
                ["importSeconds"] = Math.Round(clock.Elapsed.TotalSeconds, 2), ["created"] = DateTime.UtcNow.ToString("o"),
            });
            lock (_cacheGate)
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
                Directory.Move(temporary, folder);
            }
            Log($"imported {package.Manifest.Id} {package.Manifest.Version} in {clock.Elapsed.TotalSeconds:0.00} s " +
                $"({verified.BytesVerified / 1e6:0} MB checked, {textures.Written} textures as {TextureFormat} in {textures.Time.TotalSeconds:0.00} s of worker time; {detail}; " +
                $"managed heap {startMemory / 1e6:0} -> {GC.GetTotalMemory(false) / 1e6:0} MB)");
        }
        catch
        {
            try { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); } catch (IOException) { }
            throw;
        }
    }

    // PACKAGE-FORMAT.md section 14: SHA-256 of the zip file, or of the sorted "<path> <sha256>\n" lines of a folder.
    // index.json keeps the hash with the size and time of each package, so a warm start does not read the packages.
    private JsonObject ReadIndex()
    {
        var indexPath = Path.Combine(_settings.CacheRoot, "index.json");
        try { return File.Exists(indexPath) ? PackageJson.Object(File.ReadAllBytes(indexPath)) : new JsonObject(); }
        catch (Exception error) when (error is IOException or PackageException or System.Text.Json.JsonException) { return new JsonObject(); }
    }

    private string? KnownKey(string location) =>
        ReadIndex()[location] is JsonObject row && PackageJson.Text(row["signature"]) == Signature(location) && PackageJson.Text(row["sha256"]) is { Length: 64 } known
            ? known : null;

    private string CacheKey(string location)
    {
        var indexPath = Path.Combine(_settings.CacheRoot, "index.json");
        var index = ReadIndex();
        var signature = Signature(location);
        if (index[location] is JsonObject row && PackageJson.Text(row["signature"]) == signature && PackageJson.Text(row["sha256"]) is { Length: 64 } known)
            return known;
        string key;
        if (Directory.Exists(location))
        {
            var lines = new StringBuilder();
            using var source = new FolderPackageSource(location);
            foreach (var name in source.Names.OrderBy(n => n, StringComparer.Ordinal))
            {
                using var stream = source.Open(name);
                using var sha = SHA256.Create();
                lines.Append(name).Append(' ').Append(Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant()).Append('\n');
            }
            key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lines.ToString()))).ToLowerInvariant();
        }
        else
        {
            using var stream = File.OpenRead(location);
            using var sha = SHA256.Create();
            key = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }
        lock (_cacheGate)
        {
            index = ReadIndex();
            index[location] = new JsonObject { ["signature"] = signature, ["sha256"] = key };
            Directory.CreateDirectory(_settings.CacheRoot);
            var temporary = indexPath + ".tmp";
            PackageJson.Write(temporary, index);
            File.Move(temporary, indexPath, true);
        }
        return key;
    }

    private static string Signature(string location)
    {
        if (!Directory.Exists(location))
        {
            var info = new FileInfo(location);
            return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }
        var lines = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(location, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var info = new FileInfo(file);
            lines.Append(Path.GetRelativePath(location, file)).Append(':').Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lines.ToString()))).ToLowerInvariant();
    }
}
