using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Gh3Skin;

// Spike: decode time of band clip encodings. Usage: ClipBench <direction dir> <spike out dir> [song id] [passes]
var direction = args[0];
var spike = args[1];
var song = args.Length > 2 ? args[2] : "mycurse";
var passes = args.Length > 3 ? int.Parse(args[3]) : 3;
var performance = StagePerformance.Load(Path.Combine(direction, "performance.json"))!;
var infos = JsonSerializer.Deserialize<JsonElement[]>(File.ReadAllText(Path.Combine(spike, "clips.json")))!
    .Select(e => (Role: e.GetProperty("role").GetString()!, Ref: e.GetProperty("ref").GetString()!, Song: e.GetProperty("song").GetString()!))
    .OrderBy(e => e.Role).ThenBy(e => e.Ref).ToArray();
var roles = new[] { "guitarist", "bassist", "drummer", "singer" };
var songSet = infos.Where(i => i.Song == "" || i.Song == song).ToArray();
Console.WriteLine($"runtime {Environment.Version}, {infos.Length} clips, song set '{song}': {songSet.Length} clips " +
    $"({songSet.Count(i => i.Song == song)} facial)");

double Median(List<double> v) { v.Sort(); return v[v.Count / 2]; }
double Pct(IEnumerable<double> v, double p) { var s = v.OrderBy(x => x).ToArray(); return s[Math.Min(s.Length - 1, (int)(p * s.Length))]; }

var current = new Dictionary<(string, string), PerformanceClip>();
PerformanceClip LoadCurrent((string Role, string Ref, string Song) i)
{
    using var file = File.OpenRead(Path.Combine(direction, i.Role, i.Ref + ".bin.gz"));
    return new PerformanceClip(file, performance.Members[i.Role].Bones.Length);
}

var report = new List<string>();
void Measure(string name, Func<(string Role, string Ref, string Song), object> load)
{
    var first = new Dictionary<(string, string), double>();
    var times = infos.ToDictionary(i => (i.Role, i.Ref), _ => new List<double>());
    for (var pass = 0; pass <= passes; pass++)
        foreach (var i in infos)
        {
            var watch = Stopwatch.StartNew();
            var clip = load(i);
            watch.Stop();
            if (pass == 0) first[(i.Role, i.Ref)] = watch.Elapsed.TotalMilliseconds;
            else times[(i.Role, i.Ref)].Add(watch.Elapsed.TotalMilliseconds);
            if (name == "current" && pass == 0) current[(i.Role, i.Ref)] = (PerformanceClip)clip;
        }
    var median = times.ToDictionary(p => p.Key, p => Median(p.Value));
    foreach (var role in roles.Append("all"))
    {
        var set = infos.Where(i => role == "all" || i.Role == role).Select(i => median[(i.Role, i.Ref)]).ToArray();
        if (set.Length == 0) continue;
        report.Add($"| {name} | {role} | {set.Length} | {set.Sum():F0} | {Pct(set, 0.5):F2} | {Pct(set, 0.95):F2} | {set.Max():F1} |");
    }
    var song = songSet.Select(i => median[(i.Role, i.Ref)]).ToArray();
    var facial = songSet.Where(i => i.Song != "").Select(i => median[(i.Role, i.Ref)]).DefaultIfEmpty(0).ToArray();
    report.Add($"| {name} | song set | {song.Length} | {song.Sum():F0} | {Pct(song, 0.5):F2} | facial max {facial.Max():F1} | first pass sum {songSet.Sum(i => first[(i.Role, i.Ref)]):F0} |");
    Console.WriteLine($"{name}: done");
}

Measure("current", i => LoadCurrent(i));

var poses = typeof(PerformanceClip).GetField("_poses", BindingFlags.NonPublic | BindingFlags.Instance)!;
var bones = typeof(PerformanceClip).GetField("_bones", BindingFlags.NonPublic | BindingFlags.Instance)!;
var masks = typeof(PerformanceClip).GetField("_masks", BindingFlags.NonPublic | BindingFlags.Instance)!;

var zstd = new Dictionary<string, byte[]>();
var validation = new List<string>();
foreach (var variant in new[] { "q48-gzip9", "q48-brotli11", "q48-zstd19", "q32-gzip9", "q32-brotli11", "keys-gzip9", "keys-brotli11", "keys-zstd19", "q48kr-brotli11" })
{
    var parts = variant.Split('-');
    if (parts[1] == "zstd19")
    {
        using var compressor = new ZstdSharp.Compressor(19);
        foreach (var i in infos)
            zstd[$"{parts[0]}/{i.Role}/{i.Ref}"] = compressor.Wrap(Decompress(File.ReadAllBytes(Path.Combine(spike, "variants", parts[0] + "-brotli11", i.Role, i.Ref + ".bin")), "brotli11")).ToArray();
    }
    DenseClip Load((string Role, string Ref, string Song) i)
    {
        var data = parts[1] == "zstd19" ? zstd[$"{parts[0]}/{i.Role}/{i.Ref}"]
            : File.ReadAllBytes(Path.Combine(spike, "variants", variant, i.Role, i.Ref + ".bin"));
        return ClipCodec.Decode(Decompress(data, parts[1]));
    }
    Measure(variant, i => Load(i));
    double rot = 0, pos = 0;
    foreach (var i in infos)
    {
        var a = current[(i.Role, i.Ref)];
        var b = Load(i);
        var ap = (BandPose[])poses.GetValue(a)!;
        var am = (int[])masks.GetValue(a)!;
        if (ap.Length != b.Poses.Length || !((int[])bones.GetValue(a)!).SequenceEqual(b.Bones)) throw new InvalidDataException($"{variant} {i.Ref}: layout differs");
        for (var k = 0; k < ap.Length; k++)
        {
            var m = am[k % am.Length];
            if ((m & 2) != 0)
            {
                // Chord form, because acos of a float dot product near 1 is too coarse for errors below 0.05 degrees.
                var q = Quaternion.Dot(ap[k].Rotation, b.Poses[k].Rotation) < 0 ? Quaternion.Negate(b.Poses[k].Rotation) : b.Poses[k].Rotation;
                var chord = (ap[k].Rotation - q).Length();
                rot = Math.Max(rot, 4 * Math.Asin(Math.Min(1, chord / 2)) * 180 / Math.PI);
            }
            if ((m & 1) != 0) pos = Math.Max(pos, Vector3.Distance(ap[k].Position, b.Poses[k].Position) * 100);
        }
    }
    validation.Add($"| {variant} | {rot:F4} | {pos:F4} |");
}

// Worker-thread prefetch of the song set with the recommended encoding.
foreach (var variant in new[] { "current", "q48-brotli11", "q48-gzip9" })
{
    var parts = variant.Split('-');
    object Load((string Role, string Ref, string Song) i) => variant == "current" ? LoadCurrent(i)
        : ClipCodec.Decode(Decompress(File.ReadAllBytes(Path.Combine(spike, "variants", variant, i.Role, i.Ref + ".bin")), parts[1]));
    foreach (var threads in new[] { 1, 4 })
    {
        var results = new List<double>();
        for (var pass = 0; pass < passes; pass++)
        {
            var watch = Stopwatch.StartNew();
            Task.Run(() => Parallel.ForEach(songSet, new ParallelOptions { MaxDegreeOfParallelism = threads }, i => Load(i))).Wait();
            results.Add(watch.Elapsed.TotalMilliseconds);
        }
        report.Add($"| prefetch {variant} | {threads} worker(s) | {songSet.Length} | {Median(results):F0} | | | |");
    }
}

Console.WriteLine("\n| Encoding | Role | Clips | Sum ms | p50 ms | p95 ms | Max ms |\n|---|---|---:|---:|---:|---:|---:|");
foreach (var line in report) Console.WriteLine(line);
Console.WriteLine("\nC# decoder against the current clips (max over all clips):\n\n| Variant | Rotation deg | Position cm |\n|---|---:|---:|");
foreach (var line in validation) Console.WriteLine(line);

static byte[] Decompress(byte[] data, string method)
{
    if (method == "zstd19")
    {
        using var decompressor = new ZstdSharp.Decompressor();
        return decompressor.Unwrap(data).ToArray();
    }
    using var input = new MemoryStream(data);
    using Stream stream = method == "brotli11" ? new BrotliStream(input, CompressionMode.Decompress) : new GZipStream(input, CompressionMode.Decompress);
    using var output = new MemoryStream(data.Length * 4);
    stream.CopyTo(output);
    return output.ToArray();
}

internal sealed record DenseClip(float Duration, int Frames, int[] Bones, int[] Masks, BandPose[] Poses);

internal static class ClipCodec
{
    private const float Sqrt1_2 = 0.70710678f;

    public static DenseClip Decode(byte[] data)
    {
        var magic = System.Text.Encoding.ASCII.GetString(data, 0, 8);
        return magic switch
        {
            "GH3CLQ01" => DecodeDense(data),
            "GH3CLK01" or "GH3CLR01" => DecodeKeys(data, magic == "GH3CLR01"),
            _ => throw new InvalidDataException("Unknown clip encoding"),
        };
    }

    private static DenseClip DecodeDense(byte[] data)
    {
        var span = data.AsSpan();
        int bits = span[8], frames = BinaryPrimitives.ReadUInt16LittleEndian(span[10..]), count = BinaryPrimitives.ReadUInt16LittleEndian(span[12..]);
        var duration = BinaryPrimitives.ReadSingleLittleEndian(span[14..]);
        var at = 18;
        var bones = new int[count];
        var masks = new int[count];
        var flags = new int[count];
        for (var t = 0; t < count; t++, at += 3) (bones[t], masks[t], flags[t]) = (span[at], span[at + 1], span[at + 2]);
        var poses = new BandPose[frames * count];
        var positions = new Vector3[frames];
        for (var t = 0; t < count; t++)
        {
            if ((masks[t] & 1) == 0) continue;
            at = ReadPositions(span, at, frames, (flags[t] & 1) != 0, positions);
            for (var f = 0; f < frames; f++) poses[f * count + t] = new(positions[f], Quaternion.Identity);
        }
        var rotations = new Quaternion[frames];
        for (var t = 0; t < count; t++)
        {
            if ((masks[t] & 2) == 0)
            {
                for (var f = 0; f < frames; f++) poses[f * count + t] = new(poses[f * count + t].Position, Quaternion.Identity);
                continue;
            }
            at = ReadRotations(span, at, frames, (flags[t] & 2) != 0, bits, rotations);
            for (var f = 0; f < frames; f++) poses[f * count + t] = new(poses[f * count + t].Position, rotations[f]);
        }
        if (at != data.Length) throw new InvalidDataException("Extra clip data");
        return new DenseClip(duration, frames, bones, masks, poses);
    }

    private static DenseClip DecodeKeys(byte[] data, bool frameTimes)
    {
        var span = data.AsSpan();
        int count = BinaryPrimitives.ReadUInt16LittleEndian(span[9..]), frames = BinaryPrimitives.ReadUInt16LittleEndian(span[15..]);
        var duration = BinaryPrimitives.ReadSingleLittleEndian(span[11..]);
        var at = 17;
        var bones = new int[count];
        var masks = new int[count];
        var posKeys = new int[count];
        var rotKeys = new int[count];
        for (var t = 0; t < count; t++, at += 6)
            (bones[t], masks[t], posKeys[t], rotKeys[t]) = (span[at], span[at + 1], BinaryPrimitives.ReadUInt16LittleEndian(span[(at + 2)..]), BinaryPrimitives.ReadUInt16LittleEndian(span[(at + 4)..]));
        var poses = new BandPose[frames * count];
        for (var i = 0; i < poses.Length; i++) poses[i] = new(Vector3.Zero, Quaternion.Identity);
        var step = duration / (frames - 1);
        var tickTime = frameTimes ? step / 2 : 1f / 60;
        var times = new float[ushort.MaxValue + 1];
        var positions = new Vector3[ushort.MaxValue + 1];
        var rotations = new Quaternion[ushort.MaxValue + 1];
        for (var t = 0; t < count; t++)
        {
            if ((masks[t] & 1) != 0)
            {
                var n = posKeys[t];
                at = ReadTimes(span, at, n, tickTime, times);
                at = ReadPositions(span, at, n, n == 1, positions);
                for (int f = 0, k = 0; f < frames; f++)
                {
                    var time = f * step;
                    while (k < n - 1 && times[k + 1] <= time) k++;
                    var p = positions[k];
                    if (k < n - 1 && time > times[k]) p = Vector3.Lerp(p, positions[k + 1], (time - times[k]) / (times[k + 1] - times[k]));
                    poses[f * count + t] = new(p, Quaternion.Identity);
                }
            }
            if ((masks[t] & 2) != 0)
            {
                var n = rotKeys[t];
                at = ReadTimes(span, at, n, tickTime, times);
                at = ReadRotations(span, at, n, n == 1, 15, rotations);
                for (var k = 1; k < n; k++)
                    if (Quaternion.Dot(rotations[k - 1], rotations[k]) < 0) rotations[k] = Quaternion.Negate(rotations[k]);
                for (int f = 0, k = 0; f < frames; f++)
                {
                    var time = f * step;
                    while (k < n - 1 && times[k + 1] <= time) k++;
                    var q = rotations[k];
                    if (k < n - 1 && time > times[k]) q = Quaternion.Normalize(Quaternion.Lerp(q, rotations[k + 1], (time - times[k]) / (times[k + 1] - times[k])));
                    poses[f * count + t] = new(poses[f * count + t].Position, q);
                }
            }
        }
        if (at != data.Length) throw new InvalidDataException("Extra clip data");
        return new DenseClip(duration, frames, bones, masks, poses);
    }

    private static int ReadTimes(ReadOnlySpan<byte> span, int at, int n, float scale, float[] times)
    {
        if (n == 1) { times[0] = 0; return at; }
        var value = 0;
        for (var k = 0; k < n; k++, at += 2)
        {
            value = (value + BinaryPrimitives.ReadUInt16LittleEndian(span[at..])) & 0xFFFF;
            times[k] = value * scale;
        }
        return at;
    }

    private static int ReadPositions(ReadOnlySpan<byte> span, int at, int n, bool constant, Vector3[] output)
    {
        if (constant)
        {
            var v = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(span[at..]), BinaryPrimitives.ReadSingleLittleEndian(span[(at + 4)..]), BinaryPrimitives.ReadSingleLittleEndian(span[(at + 8)..]));
            for (var f = 0; f < n; f++) output[f] = v;
            return at + 12;
        }
        Span<float> low = stackalloc float[3], scale = stackalloc float[3];
        for (var c = 0; c < 3; c++)
        {
            low[c] = BinaryPrimitives.ReadSingleLittleEndian(span[(at + 4 * c)..]);
            scale[c] = BinaryPrimitives.ReadSingleLittleEndian(span[(at + 12 + 4 * c)..]);
        }
        at += 24;
        for (var c = 0; c < 3; c++)
        {
            var value = 0;
            for (var f = 0; f < n; f++, at += 2)
            {
                value = (value + BinaryPrimitives.ReadUInt16LittleEndian(span[at..])) & 0xFFFF;
                var x = low[c] + value * scale[c];
                if (c == 0) output[f].X = x; else if (c == 1) output[f].Y = x; else output[f].Z = x;
            }
        }
        return at;
    }

    private static int ReadRotations(ReadOnlySpan<byte> span, int at, int n, bool constant, int bits, Quaternion[] output)
    {
        if (constant)
        {
            var q = Quaternion.Normalize(new Quaternion(BinaryPrimitives.ReadSingleLittleEndian(span[at..]), BinaryPrimitives.ReadSingleLittleEndian(span[(at + 4)..]),
                BinaryPrimitives.ReadSingleLittleEndian(span[(at + 8)..]), BinaryPrimitives.ReadSingleLittleEndian(span[(at + 12)..])));
            for (var f = 0; f < n; f++) output[f] = q;
            return at + 16;
        }
        var index = span.Slice(at, n);
        at += n;
        var scale = 2f * Sqrt1_2 / ((1 << bits) - 1);
        Span<float> c = stackalloc float[4];
        int a = 0, b = 0, d = 0;
        var planeA = at;
        var planeB = at + 2 * n;
        var planeC = at + 4 * n;
        for (var f = 0; f < n; f++)
        {
            a = (a + BinaryPrimitives.ReadUInt16LittleEndian(span[(planeA + 2 * f)..])) & 0xFFFF;
            b = (b + BinaryPrimitives.ReadUInt16LittleEndian(span[(planeB + 2 * f)..])) & 0xFFFF;
            d = (d + BinaryPrimitives.ReadUInt16LittleEndian(span[(planeC + 2 * f)..])) & 0xFFFF;
            float x = a * scale - Sqrt1_2, y = b * scale - Sqrt1_2, z = d * scale - Sqrt1_2;
            var largest = MathF.Sqrt(MathF.Max(0, 1 - x * x - y * y - z * z));
            int largestIndex = index[f], slot = 0;
            for (var k = 0; k < 4; k++) c[k] = k == largestIndex ? largest : slot++ switch { 0 => x, 1 => y, _ => z };
            output[f] = Quaternion.Normalize(new Quaternion(c[0], c[1], c[2], c[3]));
        }
        return at + 6 * n;
    }
}
