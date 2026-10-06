using System.Numerics;

namespace Gh3Skin;

internal readonly record struct BandPose(Vector3 Position, Quaternion Rotation)
{
    public static BandPose Mix(BandPose a, BandPose b, float t) =>
        new(Vector3.Lerp(a.Position, b.Position, t), Quaternion.Slerp(a.Rotation, b.Rotation, t));
}

internal sealed record BandClip(float Duration, int Frames, BandPose[] Poses);
internal readonly record struct BandSample(int Clip, double Time, float Blend);

internal sealed class BandAnimation
{
    public int[] Parents { get; }
    public BandPose[] BindPose { get; }
    public BandClip[] Clips { get; }
    private readonly double _duration;
    private const float Transition = 0.25f;

    public BandAnimation(BinaryReader reader)
    {
        if (System.Text.Encoding.ASCII.GetString(reader.ReadBytes(8)) != "GH3ANM01")
            throw new InvalidDataException("Invalid band animation header");
        var count = reader.ReadInt32();
        var clips = reader.ReadInt32();
        if (count < 1 || count > 255 || clips < 1 || clips > 16)
            throw new InvalidDataException("Invalid band animation size");
        Parents = new int[count];
        BindPose = new BandPose[count];
        for (var i = 0; i < count; i++)
        {
            Parents[i] = reader.ReadInt32();
            if (Parents[i] < -1 || Parents[i] >= count || Parents[i] == i)
                throw new InvalidDataException("Invalid band bone parent");
            BindPose[i] = ReadPose(reader);
        }
        for (var i = 0; i < count; i++)
        {
            var parent = i;
            for (var depth = 0; parent >= 0; depth++, parent = Parents[parent])
                if (depth >= count) throw new InvalidDataException("Band skeleton contains a cycle");
        }
        Clips = new BandClip[clips];
        for (var i = 0; i < clips; i++)
        {
            var duration = reader.ReadSingle();
            var frames = reader.ReadInt32();
            if (!float.IsFinite(duration) || duration < 0.5 || duration > 120 || frames < 2 || frames > 7201)
                throw new InvalidDataException("Invalid band clip size");
            var poses = new BandPose[count * frames];
            for (var j = 0; j < poses.Length; j++) poses[j] = ReadPose(reader);
            Clips[i] = new BandClip(duration, frames, poses);
            _duration += duration - Transition;
        }
        if (reader.BaseStream.Position != reader.BaseStream.Length)
            throw new InvalidDataException("Extra band animation data");
    }

    public BandSample At(double time)
    {
        if (!double.IsFinite(time)) throw new ArgumentOutOfRangeException(nameof(time));
        time = Math.Max(0, time) % _duration;
        for (var i = 0; i < Clips.Length; i++)
        {
            var length = Clips[i].Duration - Transition;
            if (time < length || i == Clips.Length - 1)
            {
                var blend = (float)Math.Clamp((time - length + Transition) / Transition, 0, 1);
                return new BandSample(i, time + Transition, blend * blend * (3 - 2 * blend));
            }
            time -= length;
        }
        throw new InvalidOperationException("Missing band clip");
    }

    public BandPose Sample(BandSample sample, int bone)
    {
        var current = SampleClip(Clips[sample.Clip], sample.Time, bone);
        if (sample.Blend == 0) return current;
        var next = Clips[(sample.Clip + 1) % Clips.Length];
        var nextTime = Math.Max(0, sample.Time - Clips[sample.Clip].Duration + Transition);
        return BandPose.Mix(current, SampleClip(next, nextTime, bone), sample.Blend);
    }

    private BandPose SampleClip(BandClip clip, double time, int bone)
    {
        var frame = Math.Clamp(time / clip.Duration * (clip.Frames - 1), 0, clip.Frames - 1);
        var a = (int)frame;
        var b = Math.Min(a + 1, clip.Frames - 1);
        return BandPose.Mix(clip.Poses[a * Parents.Length + bone], clip.Poses[b * Parents.Length + bone], (float)(frame - a));
    }

    private static BandPose ReadPose(BinaryReader reader)
    {
        var p = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        var q = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        if (!float.IsFinite(p.LengthSquared()) || p.LengthSquared() > 10000 ||
            !float.IsFinite(q.LengthSquared()) || Math.Abs(q.LengthSquared() - 1) > 0.01)
            throw new InvalidDataException("Invalid band bone pose");
        return new BandPose(p, Quaternion.Normalize(q));
    }
}
