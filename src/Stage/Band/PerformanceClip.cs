using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Numerics;

namespace Gh3Skin;

// One band clip, sampled at 30 fps: GH3CLP01 (gzip, float poses) or GH3CLQ01 q48 (PACKAGE-FORMAT.md section 11.1).
internal sealed class PerformanceClip
{
    // Ten minutes at 30 fps, plus the end frame.
    public const int MaximumFrames = 18001;
    public const float MaximumSeconds = 600;
    // GH3CLQ01: magic (8), bits (1), bones (1), frames (2), tracks (2), duration (4); then 3 bytes per track: bone, mask,
    // flags. Mask and flag bit 0 is the position, bit 1 the rotation; a set flag stores one constant value.
    private const int Q48HeaderSize = 18;
    private const int Q48BitsOffset = 8, Q48BonesOffset = 9, Q48FramesOffset = 10, Q48TracksOffset = 12, Q48DurationOffset = 14;
    private const int Q48TrackSize = 3;
    private const int PositionBit = 1, RotationBit = 2;
    private const int MinimumBits = 2, MaximumBits = 16;
    // Quaternion components other than the largest lie in -1/sqrt(2)..1/sqrt(2); 0-3 selects the largest component.
    private const int LargestComponentLast = 3;
    // Tolerance of a stored quaternion's squared length.
    private const double UnitTolerance = 0.02;
    private const int Vector3Bytes = 12;
    public float Duration { get; }
    private readonly int _frames;
    private readonly int[] _bones;
    private readonly int[] _masks;
    private readonly BandPose[] _poses;

    public PerformanceClip(Stream source, int bones)
    {
        using var zip = new GZipStream(source, CompressionMode.Decompress);
        using var reader = new BinaryReader(zip);
        if (System.Text.Encoding.ASCII.GetString(reader.ReadBytes(8)) != "GH3CLP01" || reader.ReadInt32() != bones)
            throw new InvalidDataException("Invalid performance clip header");
        _frames = reader.ReadInt32();
        var count = reader.ReadInt32();
        Duration = reader.ReadSingle();
        if (_frames is < 2 or > MaximumFrames || count < 1 || count > bones || !float.IsFinite(Duration) || Duration is <= 0 or > MaximumSeconds)
            throw new InvalidDataException("Invalid performance clip size");
        _bones = new int[count];
        _masks = new int[count];
        for (var i = 0; i < count; i++)
        {
            _bones[i] = reader.ReadInt32();
            _masks[i] = reader.ReadInt32();
        }
        CheckTracks(bones);
        _poses = new BandPose[_frames * count];
        for (var i = 0; i < _poses.Length; i++)
        {
            var p = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var q = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            if (!float.IsFinite(p.LengthSquared()) || !float.IsFinite(q.LengthSquared()) || Math.Abs(q.LengthSquared() - 1) > UnitTolerance)
                throw new InvalidDataException("Invalid performance pose");
            _poses[i] = new(p, Quaternion.Normalize(q));
        }
        if (reader.BaseStream.ReadByte() != -1) throw new InvalidDataException("Extra performance clip data");
    }

    private void CheckTracks(int bones)
    {
        for (var i = 0; i < _bones.Length; i++)
            if (_bones[i] < 0 || _bones[i] >= bones || _masks[i] is < 1 or > 3)
                throw new InvalidDataException("Invalid performance bone mask");
        if (_bones.Distinct().Count() != _bones.Length) throw new InvalidDataException("Duplicate performance bones");
    }

    // A clip file of the cache: q48 + Brotli (`<clip>.q48.br`, package format v1 section 11) or GH3CLP01 + gzip (`<clip>.bin.gz`).
    public static PerformanceClip Load(string pathWithoutExtension, int bones)
    {
        var q48 = pathWithoutExtension + ".q48.br";
        if (File.Exists(q48))
        {
            using var file = File.OpenRead(q48);
            using var brotli = new BrotliStream(file, CompressionMode.Decompress);
            using var data = new MemoryStream();
            brotli.CopyTo(data);
            return new PerformanceClip(data.GetBuffer().AsSpan(0, (int)data.Length), bones);
        }
        using var stream = File.OpenRead(pathWithoutExtension + ".bin.gz");
        return new PerformanceClip(stream, bones);
    }

    // Decoded q48 layout (GH3CLQ01, PACKAGE-FORMAT.md section 11.1). Channels outside the track mask stay zero or identity.
    public PerformanceClip(ReadOnlySpan<byte> data, int bones)
    {
        if (data.Length < Q48HeaderSize || System.Text.Encoding.ASCII.GetString(data[..8]) != "GH3CLQ01" || data[Q48BonesOffset] != bones)
            throw new InvalidDataException("Invalid q48 clip header");
        int bits = data[Q48BitsOffset];
        _frames = BinaryPrimitives.ReadUInt16LittleEndian(data[Q48FramesOffset..]);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(data[Q48TracksOffset..]);
        Duration = BinaryPrimitives.ReadSingleLittleEndian(data[Q48DurationOffset..]);
        if (bits is < MinimumBits or > MaximumBits || _frames is < 2 or > MaximumFrames || count < 1 || count > bones || !float.IsFinite(Duration) ||
            Duration is <= 0 or > MaximumSeconds || data.Length < Q48HeaderSize + Q48TrackSize * count)
            throw new InvalidDataException("Invalid q48 clip size");
        _bones = new int[count];
        _masks = new int[count];
        var flags = new int[count];
        for (var i = 0; i < count; i++)
        {
            _bones[i] = data[Q48HeaderSize + Q48TrackSize * i];
            _masks[i] = data[Q48HeaderSize + 1 + Q48TrackSize * i];
            flags[i] = data[Q48HeaderSize + 2 + Q48TrackSize * i];
            if ((flags[i] & ~_masks[i]) != 0) throw new InvalidDataException("Invalid q48 track flags");
        }
        CheckTracks(bones);
        var at = Q48HeaderSize + Q48TrackSize * count;
        var positions = new Vector3[_frames * count];
        var rotations = new Quaternion[_frames * count];
        Array.Fill(rotations, Quaternion.Identity);
        for (var t = 0; t < count; t++)
        {
            if ((_masks[t] & 1) == 0) continue;
            if ((flags[t] & PositionBit) != 0)
            {
                var block = Take(data, ref at, Vector3Bytes);
                var value = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(block), BinaryPrimitives.ReadSingleLittleEndian(block[4..]), BinaryPrimitives.ReadSingleLittleEndian(block[8..]));
                for (var f = 0; f < _frames; f++) positions[f * count + t] = value;
                continue;
            }
            // Three float lows, then three float scales.
            var range = Take(data, ref at, 2 * Vector3Bytes);
            for (var c = 0; c < 3; c++)
            {
                var low = BinaryPrimitives.ReadSingleLittleEndian(range[(4 * c)..]);
                var scale = BinaryPrimitives.ReadSingleLittleEndian(range[(Vector3Bytes + 4 * c)..]);
                var plane = Take(data, ref at, 2 * _frames);
                var value = 0;
                for (var f = 0; f < _frames; f++)
                {
                    value = (value + BinaryPrimitives.ReadUInt16LittleEndian(plane[(2 * f)..])) & 0xFFFF;
                    // float64 as the reference decoder (pipeline/package/q48.py), then float32 as GH3CLP01 stores it.
                    var x = (float)(low + (double)value * scale);
                    ref var p = ref positions[f * count + t];
                    if (c == 0) p.X = x; else if (c == 1) p.Y = x; else p.Z = x;
                }
            }
        }
        var top = (1 << bits) - 1;
        Span<double> q = stackalloc double[4];
        for (var t = 0; t < count; t++)
        {
            if ((_masks[t] & RotationBit) == 0) continue;
            if ((flags[t] & RotationBit) != 0)
            {
                var block = Take(data, ref at, 16);
                var value = Quaternion.Normalize(new Quaternion(BinaryPrimitives.ReadSingleLittleEndian(block), BinaryPrimitives.ReadSingleLittleEndian(block[4..]),
                    BinaryPrimitives.ReadSingleLittleEndian(block[8..]), BinaryPrimitives.ReadSingleLittleEndian(block[12..])));
                if (!float.IsFinite(value.LengthSquared())) throw new InvalidDataException("Invalid q48 constant rotation");
                for (var f = 0; f < _frames; f++) rotations[f * count + t] = value;
                continue;
            }
            var index = Take(data, ref at, _frames);
            var planes = Take(data, ref at, 6 * _frames);
            int a = 0, b = 0, d = 0;
            for (var f = 0; f < _frames; f++)
            {
                a = (a + BinaryPrimitives.ReadUInt16LittleEndian(planes[(2 * f)..])) & 0xFFFF;
                b = (b + BinaryPrimitives.ReadUInt16LittleEndian(planes[(2 * (_frames + f))..])) & 0xFFFF;
                d = (d + BinaryPrimitives.ReadUInt16LittleEndian(planes[(2 * (2 * _frames + f))..])) & 0xFFFF;
                if (a > top || b > top || d > top || index[f] > LargestComponentLast) throw new InvalidDataException("Invalid q48 rotation value");
                double x = ((double)a / top * 2 - 1) * Sqrt1_2, y = ((double)b / top * 2 - 1) * Sqrt1_2, z = ((double)d / top * 2 - 1) * Sqrt1_2;
                var largest = Math.Sqrt(Math.Clamp(1 - (x * x + y * y + z * z), 0, 1));
                for (int k = 0, slot = 0; k < 4; k++) q[k] = k == index[f] ? largest : slot++ switch { 0 => x, 1 => y, _ => z };
                var norm = Math.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
                rotations[f * count + t] = new Quaternion((float)(q[0] / norm), (float)(q[1] / norm), (float)(q[2] / norm), (float)(q[3] / norm));
            }
        }
        if (at != data.Length) throw new InvalidDataException("Extra q48 clip data");
        _poses = new BandPose[_frames * count];
        for (var i = 0; i < _poses.Length; i++)
        {
            if (!float.IsFinite(positions[i].LengthSquared())) throw new InvalidDataException("Invalid q48 position");
            _poses[i] = new(positions[i], Quaternion.Normalize(rotations[i]));
        }
    }

    private const double Sqrt1_2 = 0.70710678118654752440;

    // Adds an offset for each bone to the position tracks (a clip of the character library clip rig on a profile rig;
    // CharacterMotion). A profile on the clip rig itself has only zero offsets: the clip stays as it is, so that its poses
    // are bit for bit the poses of the band library member on the same rig (a sum with zero changes -0 to +0).
    public PerformanceClip Move(Vector3[]? offsets)
    {
        if (offsets == null || Array.TrueForAll(offsets, offset => offset == Vector3.Zero)) return this;
        for (var i = 0; i < _bones.Length; i++)
        {
            if ((_masks[i] & 1) == 0 || _bones[i] >= offsets.Length) continue;
            var offset = offsets[_bones[i]];
            for (var f = 0; f < _frames; f++)
            {
                ref var pose = ref _poses[f * _bones.Length + i];
                pose = pose with { Position = pose.Position + offset };
            }
        }
        return this;
    }

    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> data, ref int cursor, int size)
    {
        if (cursor + size > data.Length) throw new InvalidDataException("Truncated q48 clip");
        cursor += size;
        return data.Slice(cursor - size, size);
    }

    public void SampleDelta(BandPose[] output, bool[] touched, BandPose[] bind, double time)
    {
        var frame = Math.Clamp(time / Duration * (_frames - 1), 0, _frames - 1);
        var a = (int)frame;
        var b = Math.Min(a + 1, _frames - 1);
        for (var i = 0; i < _bones.Length; i++)
        {
            var bone = _bones[i];
            var pose = BandPose.Mix(_poses[a * _bones.Length + i], _poses[b * _bones.Length + i], (float)(frame - a));
            output[bone] = new((_masks[i] & 1) == 0 ? Vector3.Zero : pose.Position - bind[bone].Position,
                (_masks[i] & 2) == 0 ? Quaternion.Identity : pose.Rotation);
            touched[bone] = true;
        }
    }

    // additive is GH3's applydifference (GH3.exe 0x4a3550): current * difference, so the difference turns about the bone's own axes.
    public void Apply(BandPose[] output, BandPose[] bind, double time, bool additive = false, float weight = 1)
    {
        var frame = Math.Clamp(time / Duration * (_frames - 1), 0, _frames - 1);
        var a = (int)frame;
        var b = Math.Min(a + 1, _frames - 1);
        for (var i = 0; i < _bones.Length; i++)
        {
            var bone = _bones[i];
            var pose = BandPose.Mix(_poses[a * _bones.Length + i], _poses[b * _bones.Length + i], (float)(frame - a));
            var current = output[bone];
            var position = (_masks[i] & 1) == 0 ? current.Position : additive ? current.Position + pose.Position - bind[bone].Position : pose.Position;
            var rotation = (_masks[i] & 2) == 0 ? current.Rotation : additive ? Quaternion.Normalize(current.Rotation * pose.Rotation) : pose.Rotation;
            output[bone] = BandPose.Mix(current, new(position, rotation), weight);
        }
    }
}

// Decoded clips of one role. Prefetch decodes the clips that a song needs on worker threads before the song starts, so no
// clip decodes on the main thread during play (PACKAGING-PLAN section 4.2). A clip outside the prefetched set still loads,
// on the calling thread, and is counted.
internal sealed class PerformanceClipCache
{
    private readonly ConcurrentDictionary<string, PerformanceClip> _clips = new();
    private readonly string _directory;
    private readonly int _bones;
    private readonly string _label;
    private readonly Vector3[]? _offsets;
    private HashSet<string> _pending = new();
    private Task? _prefetch;
    public int Prefetched { get; private set; }
    public double PrefetchMilliseconds { get; private set; }
    public static int LateLoads;
    public static double LateMilliseconds;
    public static Action<string>? Report { get; set; }

    public PerformanceClipCache(string directory, int bones, string label, Vector3[]? offsets = null)
    {
        _directory = directory;
        _bones = bones;
        _label = label;
        _offsets = offsets;
    }

    public void Prefetch(IEnumerable<string> names)
    {
        var list = names.Where(name => !_clips.ContainsKey(name)).Distinct().ToArray();
        _pending = list.ToHashSet();
        Prefetched = list.Length;
        _prefetch = Task.Run(() =>
        {
            var clock = Stopwatch.StartNew();
            Parallel.ForEach(list, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) },
                name => _clips.TryAdd(name, PerformanceClip.Load(Path.Combine(_directory, name), _bones).Move(_offsets)));
            PrefetchMilliseconds = clock.Elapsed.TotalMilliseconds;
        });
    }

    // Waits for the prefetch; returns the time that the caller waited.
    public double Wait()
    {
        if (_prefetch == null) return 0;
        var clock = Stopwatch.StartNew();
        // Wait on the handle: Task.Wait can run a task that has not started on the waiting (main) thread.
        ((IAsyncResult)_prefetch).AsyncWaitHandle.WaitOne();
        if (_prefetch.Exception is { } error) Report?.Invoke($"clip prefetch of {_label} failed: {error.InnerException?.Message}");
        _pending.Clear();
        return clock.Elapsed.TotalMilliseconds;
    }

    public PerformanceClip Get(string name)
    {
        if (_clips.TryGetValue(name, out var clip)) return clip;
        if (_pending.Contains(name))
        {
            Wait();
            if (_clips.TryGetValue(name, out clip)) return clip;
        }
        var clock = Stopwatch.StartNew();
        clip = PerformanceClip.Load(Path.Combine(_directory, name), _bones).Move(_offsets);
        _clips[name] = clip;
        LateLoads++;
        LateMilliseconds += clock.Elapsed.TotalMilliseconds;
        Report?.Invoke($"clip {_label}/{name} was not prefetched; it loaded on the main thread in {clock.Elapsed.TotalMilliseconds:0.0} ms");
        return clip;
    }
}
