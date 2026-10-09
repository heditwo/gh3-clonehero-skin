using System.IO.Compression;
using System.Security.Cryptography;

namespace Gh3Skin.Packages;

// A package zip or a development folder (PACKAGE-FORMAT.md sections 2.1 and 2.4). Not thread-safe: the importer reads
// entries on its own thread only.
internal abstract class PackageSource : IDisposable
{
    public string Location { get; }
    public abstract bool IsFolder { get; }
    public abstract IReadOnlyCollection<string> Names { get; }
    public abstract long Size(string name);
    public abstract Stream Open(string name);
    public bool Contains(string name) => Names.Contains(name);

    protected PackageSource(string location) => Location = location;

    public static PackageSource At(string location) =>
        Directory.Exists(location) ? new FolderPackageSource(location) : new ZipPackageSource(location);

    public byte[] ReadAll(string name)
    {
        using var stream = Open(name);
        var size = Size(name);
        if (size > int.MaxValue - 64) throw new PackageException($"{name} is too large");
        var data = new byte[size];
        var read = 0;
        while (read < data.Length)
        {
            var count = stream.Read(data, read, data.Length - read);
            if (count == 0) throw new PackageException($"{name} is truncated");
            read += count;
        }
        if (stream.ReadByte() != -1) throw new PackageException($"{name} is longer than its size");
        return data;
    }

    public virtual void Dispose() { }
}

internal sealed class ZipPackageSource : PackageSource
{
    private readonly ZipArchive _archive;
    private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.Ordinal);
    public override bool IsFolder => false;
    public override IReadOnlyCollection<string> Names => _entries.Keys;

    public ZipPackageSource(string path) : base(path)
    {
        _archive = new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read, false);
        var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _archive.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            if (entry.FullName.Contains('\\') || !PackageManifest.ValidPath(entry.FullName) && entry.FullName != "manifest.json")
                throw new PackageException($"invalid zip entry name '{entry.FullName}'");
            if (!folded.Add(entry.FullName)) throw new PackageException($"zip entry '{entry.FullName}' occurs more than one time");
            _entries[entry.FullName] = entry;
        }
    }

    public override long Size(string name) => _entries[name].Length;
    public override Stream Open(string name) => _entries.TryGetValue(name, out var entry) ? entry.Open() : throw new PackageException($"missing file {name}");
    public override void Dispose() => _archive.Dispose();
}

internal sealed class FolderPackageSource : PackageSource
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    public override bool IsFolder => true;
    public override IReadOnlyCollection<string> Names => _files.Keys;

    public FolderPackageSource(string path) : base(path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetRelativePath(path, file).Replace(Path.DirectorySeparatorChar, '/');
            if (name.Split('/').Any(segment => segment.StartsWith('.'))) continue;
            _files[name] = file;
        }
    }

    public string FullPath(string name) => _files[name];
    public override long Size(string name) => new FileInfo(_files[name]).Length;
    public override Stream Open(string name) => _files.TryGetValue(name, out var file) ? File.OpenRead(file) : throw new PackageException($"missing file {name}");
}

// Reads package files and checks each one against its manifest record (size and SHA-256).
internal sealed class VerifiedPackage : IDisposable
{
    private readonly HashSet<string> _verified = new(StringComparer.Ordinal);
    public PackageSource Source { get; }
    public PackageManifest Manifest { get; }
    public long BytesVerified { get; private set; }
    private bool Checked => !(Source.IsFolder && Manifest.Files.Count == 0);

    public VerifiedPackage(PackageSource source, PackageManifest manifest)
    {
        Source = source;
        Manifest = manifest;
        CheckFileList(source, manifest);
    }

    // The cheap part of the check: names and sizes from the zip directory or the file system.
    public static void CheckFileList(PackageSource source, PackageManifest manifest)
    {
        if (source.IsFolder && manifest.Files.Count == 0) return;
        foreach (var name in source.Names)
            if (name != "manifest.json" && !manifest.Files.ContainsKey(name)) throw new PackageException($"{name} is not listed in manifest.json");
        foreach (var (name, file) in manifest.Files)
        {
            if (!source.Contains(name)) throw new PackageException($"{name} is missing");
            if (source.Size(name) != file.Size) throw new PackageException($"{name} has the wrong size");
        }
    }

    public bool Contains(string name) => Source.Contains(name);
    public IEnumerable<string> Names => Source.Names.Where(name => name != "manifest.json");

    public byte[] Read(string name)
    {
        if (!Source.Contains(name)) throw new PackageException($"missing file {name}");
        var data = Source.ReadAll(name);
        if (Checked)
        {
            var hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            if (hash != Manifest.Files[name].Sha256) throw new PackageException($"{name} has the wrong SHA-256 (the package is damaged)");
        }
        BytesVerified += data.Length;
        _verified.Add(name);
        return data;
    }

    // Copies a file without holding it in memory and checks its hash while it streams.
    public void CopyTo(string name, string destination)
    {
        if (!Source.Contains(name)) throw new PackageException($"missing file {name}");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1 << 20);
        long total = 0;
        try
        {
            using var input = Source.Open(name);
            using var output = File.Create(destination);
            int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, count);
                output.Write(buffer, 0, count);
                total += count;
            }
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
        if (Checked && Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != Manifest.Files[name].Sha256)
            throw new PackageException($"{name} has the wrong SHA-256 (the package is damaged)");
        BytesVerified += total;
        _verified.Add(name);
    }

    // Files that the importer does not convert (for example preview.png) are checked too, so a damaged package never imports.
    public void VerifyRemaining()
    {
        if (!Checked) return;
        foreach (var name in Manifest.Files.Keys)
        {
            if (_verified.Contains(name)) continue;
            using var stream = Source.Open(name);
            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
            if (hash != Manifest.Files[name].Sha256) throw new PackageException($"{name} has the wrong SHA-256 (the package is damaged)");
            BytesVerified += Manifest.Files[name].Size;
            _verified.Add(name);
        }
    }

    public void Dispose() => Source.Dispose();
}
