using System.Numerics;
using Gh3Skin;

// Hair motion bench for the comparison with the original GH3 (VERIFICATION.md, "Hair motion").
// GH3_HAIR_BENCH=<profile> GH3_HAIR_BENCH_OUT=<file> GH3_HAIR_BENCH_FPS=<render rate> runs the hair of a character
// library profile and writes, for each rendered frame, the time and the world pose (x, y, z, qx, qy, qz, qw) of the head
// and of every hair joint as float32 values.
// With GH3_HAIR_BENCH_HEAD=<file> (rows of float32 t, x, y, z, qx, qy, qz, qw: the head bone in runtime coordinates,
// for example from a GH3 memory recording) the head follows that path and all other bones keep the bind pose.
// Without it, the profile plays My Curse as the guitarist (GH3_CHARACTER_HAIR_ASSETS=<assets>), as in the game.
// GH3_HAIR_BENCH=band:<role> runs a band library member (band/hair.json) through My Curse the same way.
internal static class HairBench
{
    public static void Run(string id)
    {
        var assets = Environment.GetEnvironmentVariable("GH3_CHARACTER_HAIR_ASSETS") ?? "assets";
        var output = Environment.GetEnvironmentVariable("GH3_HAIR_BENCH_OUT") ?? throw new InvalidOperationException("GH3_HAIR_BENCH_OUT is not set");
        var fps = double.Parse(Environment.GetEnvironmentVariable("GH3_HAIR_BENCH_FPS") ?? "60", System.Globalization.CultureInfo.InvariantCulture);
        var seconds = double.Parse(Environment.GetEnvironmentVariable("GH3_HAIR_BENCH_SECONDS") ?? "240", System.Globalization.CultureInfo.InvariantCulture);
        if (id.StartsWith("band:")) { Band(assets, id[5..], output, fps, seconds); return; }
        var folder = Path.Combine(assets, "libraries/gh3-characters-v1");
        var library = Gh3Skin.Packages.CharacterLibrary.Load(folder);
        var bank = CharacterMotionBank.Load(folder);
        var profile = library.Profiles[id];
        var definition = bank.Hair[profile.Hair];
        var animation = BandAnimation.FromRig(profile.Parents, profile.Bind);
        var parents = animation.Parents;
        StageHair.PoseSampler sample;
        BandPose[] Animated() => (BandPose[])animation.BindPose.Clone();
        var animated = Animated();
        if (Environment.GetEnvironmentVariable("GH3_HAIR_BENCH_HEAD") is { } headFile)
        {
            var raw = File.ReadAllBytes(headFile);
            var rows = new float[raw.Length / 4];
            Buffer.BlockCopy(raw, 0, rows, 0, rows.Length * 4);
            var count = rows.Length / 8;
            seconds = Math.Min(seconds, rows[(count - 1) * 8] - rows[0]);
            var start = rows[0];
            var head = definition.Head;
            BandPose Path(double time)
            {
                var t = (float)(start + time);
                var at = 0;
                while (at < count - 2 && rows[(at + 1) * 8] <= t) at++;
                var a = at * 8; var b = (at + 1) * 8;
                var f = Math.Clamp((t - rows[a]) / Math.Max(rows[b] - rows[a], 1e-6f), 0, 1);
                return BandPose.Mix(new(new(rows[a + 1], rows[a + 2], rows[a + 3]), new(rows[a + 4], rows[a + 5], rows[a + 6], rows[a + 7])),
                    new(new(rows[b + 1], rows[b + 2], rows[b + 3]), new(rows[b + 4], rows[b + 5], rows[b + 6], rows[b + 7])), f);
            }
            sample = (time, pose) =>
            {
                Array.Copy(animation.BindPose, pose, pose.Length);
                var parent = World(parents, pose, parents[head]);
                var target = Path(time);
                var inverse = Quaternion.Inverse(parent.Rotation);
                pose[head] = new(Vector3.Transform(target.Position - parent.Position, inverse), Quaternion.Normalize(inverse * target.Rotation));
                return new(Vector3.Zero, Quaternion.Identity);
            };
        }
        else
        {
            var performance = StagePerformance.Load(Path.Combine(assets, "libraries/gh3-band-v1/direction/performance.json"))!;
            var timeline = StageChartReader.Load("", "", Path.Combine(assets, "stages/events"), "My Curse", "Killswitch Engage");
            var set = bank.Animations[profile.Animations];
            var motion = new CharacterMotion
            {
                Profile = profile, SetName = profile.Animations, Hair = definition,
                Member = new PerformanceMember { Bones = profile.Bones, Groups = set.Groups, Layers = set.Layers, Clips = set.Clips },
                ClipDirectory = Path.Combine(folder, "clips"),
                Offsets = CharacterMotion.RestOffsets(profile.Bones, profile.Bind, library.ClipRig.Bones, library.ClipRig.Bind),
            };
            var player = new PerformancePlayer("guitarist", "", motion.Member, animation.BindPose, timeline, parents, performance.Constants, motion);
            player.Prepare();
            sample = (time, pose) => { player.Sample(time); Array.Copy(player.Pose, pose, pose.Length); return new(Vector3.Zero, Quaternion.Identity); };
        }
        Write(id, definition, parents, sample, output, fps, seconds);
    }

    private static void Band(string assets, string role, string output, double fps, double seconds)
    {
        var library = Path.Combine(assets, "libraries/gh3-band-v1");
        var band = Path.Combine(library, "band");
        var directory = Path.Combine(library, "direction");
        var definition = HairBank.Load(Path.Combine(band, "hair.json"))!.Members[role];
        var performance = StagePerformance.Load(Path.Combine(directory, "performance.json"))!;
        var timeline = StageChartReader.Load("", "", Path.Combine(assets, "stages/events"), "My Curse", "Killswitch Engage");
        using var reader = new BinaryReader(File.OpenRead(Path.Combine(band, role, "animation.bin")));
        var animation = new BandAnimation(reader);
        var player = new PerformancePlayer(role, directory, performance.Members[role], animation.BindPose, timeline);
        player.Prepare();
        Write("band:" + role, definition, animation.Parents, (time, pose) => { player.Sample(time); Array.Copy(player.Pose, pose, pose.Length); return new(Vector3.Zero, Quaternion.Identity); },
            output, fps, seconds);
    }

    private static void Write(string id, HairDefinition definition, int[] parents, StageHair.PoseSampler sample, string output, double fps, double seconds)
    {
        var hair = new StageHair(definition, parents, sample);
        var bones = new[] { definition.Head }.Concat(definition.Joints.Select(j => j.Bone)).ToArray();
        using var writer = new BinaryWriter(File.Create(output));
        var frame = new BandPose[parents.Length];
        var world = new BandPose[parents.Length];
        for (var i = 0; i / fps <= seconds; i++)
        {
            var time = i / fps;
            hair.Sample(time);
            sample(time, frame);
            hair.Apply(frame);
            for (var b = 0; b < frame.Length; b++)
            {
                var parent = parents[b] < 0 ? new BandPose(Vector3.Zero, Quaternion.Identity) : world[parents[b]];
                world[b] = new(parent.Position + Vector3.Transform(frame[b].Position, parent.Rotation), Quaternion.Normalize(parent.Rotation * frame[b].Rotation));
            }
            writer.Write((float)time);
            foreach (var b in bones)
            {
                var p = world[b];
                writer.Write(p.Position.X); writer.Write(p.Position.Y); writer.Write(p.Position.Z);
                writer.Write(p.Rotation.X); writer.Write(p.Rotation.Y); writer.Write(p.Rotation.Z); writer.Write(p.Rotation.W);
            }
        }
        Console.WriteLine(FormattableString.Invariant($"{id}: {bones.Length - 1} hair joints, {seconds:F1} s at {fps} fps, peak {hair.PeakSpeed:F2} m/s -> {output}"));
    }

    private static BandPose World(int[] parents, BandPose[] pose, int bone)
    {
        if (bone < 0) return new(Vector3.Zero, Quaternion.Identity);
        var parent = World(parents, pose, parents[bone]);
        return new(parent.Position + Vector3.Transform(pose[bone].Position, parent.Rotation), Quaternion.Normalize(parent.Rotation * pose[bone].Rotation));
    }
}
