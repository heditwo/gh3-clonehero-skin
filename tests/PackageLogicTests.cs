using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Gh3Skin;
using Gh3Skin.Packages;

internal static class PackageLogicTests
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
    }

    public static void Run()
    {
        CheckVersions();
        CheckManifest();
        CheckPythonRepr();
        CheckPng();
        CheckBlockCompression();
        CheckDiscovery();
        // GH3_PACKAGE_ASSETS=assets: import the library package and compare every decoded q48 clip with the GH3CLP01 clip
        // of the older install (the q48 error bound of PACKAGE-FORMAT.md section 11.4).
        if (Environment.GetEnvironmentVariable("GH3_PACKAGE_ASSETS") is { } assets) CheckClips(assets);
    }

    private static void CheckVersions()
    {
        SemanticVersion.TryParse("1.2.3-beta.2", out var beta);
        SemanticVersion.TryParse("1.2.3", out var release);
        Check(beta.CompareTo(release) < 0, "a prerelease is lower than its release");
        Check(SemanticVersion.TryParse("1.4.0", out var newer) && newer.Satisfies(release) && !release.Satisfies(newer), "requires is satisfied by the same major, not lower");
        Check(SemanticVersion.TryParse("2.0.0", out var major) && !major.Satisfies(release), "another major version does not satisfy requires");
        Check(!SemanticVersion.TryParse("1.02.0", out _) && !SemanticVersion.TryParse("1.2", out _), "semantic versions are strict");
        Check(PackageManifest.ValidPath("band/guitarist/model.glb") && !PackageManifest.ValidPath("../x") && !PackageManifest.ValidPath("a/.hidden") &&
              !PackageManifest.ValidPath("con.json") && !PackageManifest.ValidPath("a\\b"), "package paths are portable and stay inside the package");
    }

    private static byte[] Manifest(string format, string extra = "") => Encoding.UTF8.GetBytes(
        $"{{\"format\":\"{format}\",\"id\":\"test.stage\",\"version\":\"1.0.0\",\"name\":\"Test\",\"authors\":[{{\"name\":\"T\"}}],\"license\":\"CC0-1.0\"," +
        "\"permissions\":{\"redistribute\":true,\"modify\":true,\"commercial\":true},\"entry\":{\"model\":\"stage.glb\",\"placements\":\"placements.json\",\"cameras\":\"cameras.json\"}," +
        $"\"files\":{{}}{extra}}}");

    private static string Rejection(byte[] manifest, PackageKind kind = PackageKind.Stage)
    {
        SemanticVersion.TryParse("0.9.8", out var mod);
        try { PackageManifest.Parse(manifest, kind, mod); return ""; }
        catch (PackageException error) { return error.Message; }
    }

    private static void CheckManifest()
    {
        Check(Rejection(Manifest("chstage/1.0")) == "", "a v1.0 venue manifest is read");
        Check(Rejection(Manifest("chstage/1.1")).Contains("needs a newer mod"), "a newer minor format asks for a newer mod");
        Check(Rejection(Manifest("chstage/2.0")).Contains("not supported"), "an unknown major format is rejected");
        Check(Rejection(Manifest("chstage/1.0"), PackageKind.Library).Contains("does not agree"), "the extension selects the kind");
        Check(Rejection(Manifest("chstage/1.0", ",\"minModVersion\":\"9.0.0\"")).Contains("needs mod version 9.0.0"), "minModVersion is checked");
        var character = Encoding.UTF8.GetString(Manifest("chchar/1.0")).Replace("\"model\":\"stage.glb\",\"placements\":\"placements.json\",\"cameras\":\"cameras.json\"",
            "\"model\":\"character.glb\",\"character\":\"character.json\"");
        Check(Rejection(Encoding.UTF8.GetBytes(character), PackageKind.Character) == "", "a character manifest is read");
        Check(Rejection(Encoding.UTF8.GetBytes(character.Replace("chchar/1.0", "chchar/1.1")), PackageKind.Character).Contains("needs a newer mod"), "chchar/1.1 asks for a newer mod");
        var library = Encoding.UTF8.GetString(Manifest("chlib/1.0")).Replace("\"model\":\"stage.glb\",\"placements\":\"placements.json\",\"cameras\":\"cameras.json\"",
            "\"characters\":\"characters.json\"");
        Check(Rejection(Encoding.UTF8.GetBytes(library), PackageKind.Library) == "", "a character library manifest is read");
        Check(Rejection(Encoding.UTF8.GetBytes(library.Replace("characters.json", "x.json").Replace("\"characters\"", "\"other\"")), PackageKind.Library).Contains("needs 'catalog'"),
            "a band library needs the band library entries");
        Check(Rejection(Encoding.UTF8.GetBytes(library.Replace("chlib/1.0", "chlib/1.1")), PackageKind.Library).Contains("needs a newer mod"), "chlib/1.1 asks for a newer mod");
    }

    private static void CheckPythonRepr()
    {
        Check(PackageJson.PythonList(new[] { 1.0, -2.5, 1e-5, 0.0001, 123456789.0 }) == "[1.0, -2.5, 1e-05, 0.0001, 123456789.0]", "float lists print as Python prints them");
        Check(PackageJson.PythonRepr(0.1 + 0.2) == "0.30000000000000004", "Python repr uses the shortest round-trip digits");
    }

    internal static byte[] PngFile(int width, int height, int colorType, int depth, byte[] rows, byte[]? palette = null, byte[]? transparency = null)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        void Chunk(string type, byte[] data)
        {
            var header = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, data.Length);
            output.Write(header);
            output.Write(Encoding.ASCII.GetBytes(type));
            output.Write(data);
            output.Write(new byte[4]);
        }
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = (byte)depth;
        ihdr[9] = (byte)colorType;
        Chunk("IHDR", ihdr);
        if (palette != null) Chunk("PLTE", palette);
        if (transparency != null) Chunk("tRNS", transparency);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, true)) zlib.Write(rows);
        Chunk("IDAT", compressed.ToArray());
        Chunk("IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    private static void CheckPng()
    {
        // 2 × 2 palette image, 4 bits per pixel, filter 0; index 1 is transparent.
        var palette = new byte[] { 255, 0, 0, 0, 255, 0 };
        var png = PngFile(2, 2, 3, 4, new byte[] { 0, 0x01, 0, 0x10 }, palette, new byte[] { 255, 0 });
        var rgba = Png.DecodeRgba(png, out var width, out var height);
        // Rows are bottom first: PNG row 1 (index 1, 0) is the first stored row.
        Check(width == 2 && height == 2 && rgba[0..4].SequenceEqual(new byte[] { 0, 255, 0, 0 }) && rgba[4..8].SequenceEqual(new byte[] { 255, 0, 0, 255 }) &&
              rgba[8..12].SequenceEqual(new byte[] { 255, 0, 0, 255 }) && rgba[12..16].SequenceEqual(new byte[] { 0, 255, 0, 0 }),
            "palette PNG with tRNS decodes to RGBA, bottom row first");
        var gray = Png.DecodeRgba(PngFile(1, 1, 0, 8, new byte[] { 0, 77 }), out _, out _);
        Check(gray.SequenceEqual(new byte[] { 77, 77, 77, 255 }), "grey PNG expands to RGB");
    }

    private static void CheckBlockCompression()
    {
        var rgba = new byte[16 * 16 * 4];
        for (var y = 0; y < 16; y++)
            for (var x = 0; x < 16; x++)
            {
                var i = (y * 16 + x) * 4;
                (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = ((byte)(x * 15 + y), (byte)(x * 12 + y), (byte)(200 - x * 8), (byte)(x < 8 ? 255 : 64 + y * 8));
            }
        var bc3 = BlockCompression.Encode(rgba, 16, 16, alpha: true);
        var error = BlockCompression.MeanSquaredError(rgba, 16, 16, bc3, alpha: true);
        Check(bc3.Length == 16 * 16 && 10 * Math.Log10(255.0 * 255 / error) > 40, $"BC3 keeps a smooth gradient (PSNR {10 * Math.Log10(255.0 * 255 / error):0.0} dB)");
        var noise = new byte[32 * 16 * 4];
        new Random(7).NextBytes(noise);
        foreach (var withAlpha in new[] { false, true })
        {
            var blocks = BlockCompression.Encode(noise, 32, 16, withAlpha, out var squared);
            Check(Math.Abs(squared / (32.0 * 16 * (withAlpha ? 4 : 3)) - BlockCompression.MeanSquaredError(noise, 32, 16, blocks, withAlpha)) < 1e-9,
                "the encoder's block error equals the decoded mean squared error");
        }
        var solid = Enumerable.Range(0, 64).SelectMany(_ => new byte[] { 200, 101, 37, 255 }).ToArray();
        var bc1 = BlockCompression.Encode(solid, 4, 4, alpha: false);
        Check(BlockCompression.MeanSquaredError(solid, 4, 4, bc1, alpha: false) < 1, "BC1 reproduces a single colour within one step");
        var texture = TextureCodec.Encode(PngFile(1, 1, 6, 8, new byte[] { 0, 1, 2, 3, 4 }), compressed: true);
        var header = TextureCodec.ReadHeader(texture);
        Check(header == (TextureCodec.Rgba32, 1, 1, 1), "sizes that are not a multiple of 4 stay RGBA32");
        var rows = new byte[8 * (1 + 8 * 4)];
        for (var y = 0; y < 8; y++) for (var x = 0; x < 32; x++) rows[y * 33 + 1 + x] = (byte)(x % 4 == 3 ? 255 : y * 30);
        var opaque = TextureCodec.ReadHeader(TextureCodec.Encode(PngFile(8, 8, 6, 8, rows), compressed: true));
        Check(opaque == (TextureCodec.Dxt1, 8, 8, 4), "opaque textures use BC1 with the full mip chain");
    }

    private static string Folder(string root, string name, Dictionary<string, byte[]> files, string format, string id, string requires = "", bool hashes = true)
    {
        var folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        var listed = new JsonObject();
        foreach (var (path, data) in files)
        {
            File.WriteAllBytes(Path.Combine(folder, path), data);
            if (hashes) listed[path] = new JsonObject { ["sha256"] = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(), ["size"] = data.Length };
        }
        var entry = format.StartsWith("chlib") ? "{\"catalog\":\"catalog.json\",\"band\":\"band.json\",\"performance\":\"performance.json\"}" :
            "{\"model\":\"stage.glb\",\"placements\":\"placements.json\",\"cameras\":\"cameras.json\"}";
        File.WriteAllText(Path.Combine(folder, "manifest.json"),
            $"{{\"format\":\"{format}\",\"id\":\"{id}\",\"version\":\"1.0.0\",\"name\":\"{id}\",\"authors\":[{{\"name\":\"T\"}}],\"license\":\"CC0-1.0\"," +
            $"\"permissions\":{{\"redistribute\":true,\"modify\":true,\"commercial\":true}},{requires}\"entry\":{entry},\"files\":{listed.ToJsonString()}}}");
        return folder;
    }

    private static void CheckDiscovery()
    {
        var root = Path.Combine(Path.GetTempPath(), "gh3-package-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var custom = Path.Combine(root, "Custom");
            var mod = Path.Combine(root, "Packages");
            var stageFiles = new Dictionary<string, byte[]> { ["stage.glb"] = new byte[] { 1 }, ["placements.json"] = new byte[] { 2 }, ["cameras.json"] = new byte[] { 3 } };
            var requires = "\"requires\":[{\"id\":\"lib.test\",\"version\":\"1.0.0\"}],";
            Folder(mod, "lib.chlib", new Dictionary<string, byte[]> { ["catalog.json"] = new byte[] { 4 }, ["band.json"] = new byte[] { 5 }, ["performance.json"] = new byte[] { 6 } }, "chlib/1.0", "lib.test");
            Folder(Path.Combine(custom, "Stages", "Group A", "Deeper"), "good.chstage", stageFiles, "chstage/1.0", "good.stage", requires);
            Folder(Path.Combine(custom, "Stages"), "newer.chstage", stageFiles, "chstage/1.4", "newer.stage", requires);
            Folder(Path.Combine(custom, "Stages"), "orphan.chstage", stageFiles, "chstage/1.0", "orphan.stage", "\"requires\":[{\"id\":\"lib.missing\",\"version\":\"1.0.0\"}],");
            var damaged = Folder(Path.Combine(custom, "Stages"), "damaged.chstage", stageFiles, "chstage/1.0", "damaged.stage", requires);
            File.WriteAllBytes(Path.Combine(damaged, "cameras.json"), new byte[] { 9, 9 });
            Folder(Path.Combine(custom, "Stages", "a", "b", "c", "d", "e"), "deep.chstage", stageFiles, "chstage/1.0", "deep.stage", requires);
            Folder(Path.Combine(custom, "Characters"), "hero.chchar", new Dictionary<string, byte[]> { ["model.glb"] = new byte[] { 1 }, ["character.json"] = new byte[] { 2 } },
                "chchar/1.0", "hero.char", requires);
            File.WriteAllText(Path.Combine(custom, "Characters", "hero.chchar", "manifest.json"),
                File.ReadAllText(Path.Combine(custom, "Characters", "hero.chchar", "manifest.json")).Replace("\"model\":\"stage.glb\",\"placements\":\"placements.json\",\"cameras\":\"cameras.json\"", "\"model\":\"model.glb\",\"character\":\"character.json\""));
            SemanticVersion.TryParse("0.9.8", out var version);
            var manager = new PackageManager(new PackageSettings(mod, new[] { custom }, Path.Combine(root, "Cache"), true, "Test", "None", version, "", 1));
            manager.DiscoverOnly();
            var log = new List<string>();
            while (manager.TryDequeueLog(out var line)) log.Add(line.Text);
            var ids = manager.Packages.Select(p => p.Manifest.Id).OrderBy(i => i).ToArray();
            Check(ids.SequenceEqual(new[] { "good.stage", "hero.char", "lib.test" }), $"discovery keeps the valid packages ({string.Join(", ", ids)}; {string.Join(" | ", log)})");
            Check(log.Any(l => l.Contains("newer.chstage") && l.Contains("needs a newer mod")), "a newer format is skipped with a clear message");
            Check(log.Any(l => l.Contains("orphan.stage") && l.Contains("requires lib.missing")), "a missing library is skipped with a clear message");
            Check(log.Any(l => l.Contains("damaged.chstage") && l.Contains("wrong size")), "a damaged package is skipped with a clear message");
            Check(!log.Any(l => l.Contains("deep.chstage")), "group folders deeper than four levels are not scanned");
            Check(log.Any(l => l.Contains("found character hero.char")), "characters are found and logged");
            // The hash check runs at import: a file with the right size and the wrong content.
            File.WriteAllBytes(Path.Combine(custom, "Stages", "Group A", "Deeper", "good.chstage", "cameras.json"), new byte[] { 7 });
            var source = new FolderPackageSource(Path.Combine(custom, "Stages", "Group A", "Deeper", "good.chstage"));
            using var verified = new VerifiedPackage(source, manager.Find(PackageKind.Stage, "good.stage")!.Manifest);
            try
            {
                verified.Read("cameras.json");
                Check(false, "a wrong hash stops the import");
            }
            catch (PackageException error) { Check(error.Message.Contains("wrong SHA-256"), "a wrong hash stops the import with a clear message"); }
        }
        finally { Directory.Delete(root, true); }
    }

    private static void CheckClips(string assets)
    {
        var cache = Path.Combine(Path.GetTempPath(), "gh3-package-clips-" + Guid.NewGuid().ToString("N"));
        var packages = Path.Combine(cache, "packages");
        Directory.CreateDirectory(packages);
        try
        {
            File.CreateSymbolicLink(Path.Combine(packages, "gh3-band-v1.chlib"), Path.GetFullPath(Path.Combine(assets, "packages", "gh3-band-v1.chlib")));
            SemanticVersion.TryParse("0.9.8", out var version);
            var manager = new PackageManager(new PackageSettings(packages, Array.Empty<string>(), Path.Combine(cache, "cache"), false, "Test", "None", version, "", 2));
            manager.RunNow();
            var log = new List<string>();
            while (manager.TryDequeueLog(out var line)) log.Add(line.Text);
            var library = manager.Find(PackageKind.Library, "gh3-band-v1");
            Check(library?.State == PackageState.Ready, "the library package imports: " + string.Join(" | ", log));
            var old = Path.Combine(assets, "libraries", "gh3-band-v1");
            var performance = StagePerformance.Load(Path.Combine(library!.CacheDirectory, "direction", "performance.json"))!;
            var crowd = CrowdDefinition.Load(Path.Combine(library.CacheDirectory, "crowd", "crowd.json"));
            var jobs = performance.Members.SelectMany(m => m.Value.Clips.Keys.Select(c => (Folder: Path.Combine("direction", m.Key), Clip: c, Bones: m.Value.Bones.Length)))
                .Concat(crowd.PerformanceClips.Keys.Select(c => (Folder: Path.Combine("crowd", "clips"), Clip: c, Bones: crowd.Bones.Length))).ToList();
            var maxRotation = 0.0;
            var maxPosition = 0.0;
            foreach (var (folder, clip, bones) in jobs)
            {
                var decoded = PerformanceClip.Load(Path.Combine(library.CacheDirectory, folder, clip), bones);
                PerformanceClip original;
                using (var stream = File.OpenRead(Path.Combine(old, folder, clip + ".bin.gz"))) original = new PerformanceClip(stream, bones);
                Check(Math.Abs(decoded.Duration - original.Duration) < 1e-6, "q48 clip durations match");
                var a = new BandPose[bones];
                var b = new BandPose[bones];
                var bind = Enumerable.Repeat(new BandPose(Vector3.Zero, Quaternion.Identity), bones).ToArray();
                for (var step = 0; step <= 20; step++)
                {
                    var time = decoded.Duration * step / 20;
                    Array.Copy(bind, a, bones);
                    Array.Copy(bind, b, bones);
                    decoded.Apply(a, bind, time);
                    original.Apply(b, bind, time);
                    for (var i = 0; i < bones; i++)
                    {
                        maxPosition = Math.Max(maxPosition, (a[i].Position - b[i].Position).Length());
                        // Chord form 4 asin(|a - b| / 2), as roundtrip.py: exact for small angles.
                        var sign = Quaternion.Dot(a[i].Rotation, b[i].Rotation) < 0 ? -1.0 : 1.0;
                        double dx = a[i].Rotation.X - sign * b[i].Rotation.X, dy = a[i].Rotation.Y - sign * b[i].Rotation.Y,
                            dz = a[i].Rotation.Z - sign * b[i].Rotation.Z, dw = a[i].Rotation.W - sign * b[i].Rotation.W;
                        maxRotation = Math.Max(maxRotation, 4 * Math.Asin(Math.Min(1, Math.Sqrt(dx * dx + dy * dy + dz * dz + dw * dw) / 2)) * 180 / Math.PI);
                    }
                }
            }
            Console.WriteLine($"q48 clips against GH3CLP01: {jobs.Count} clips, rotation {maxRotation:0.0000}°, position {maxPosition * 1000:0.000} mm");
            // The format bound is 0.0075° (pipeline/package/roundtrip.py); the interpolated samples also hold float32 rounding.
            Check(jobs.Count == 1240 && maxRotation < 0.0075 && maxPosition < 0.0005, "q48 clips decode within the format error bound");
        }
        finally { Directory.Delete(cache, true); }
    }
}
