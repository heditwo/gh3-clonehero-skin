using System.Numerics;
using Gh3Skin;

internal static class DrumHandLogicTests
{
    private static readonly (int Upper, int Lower, int Hand, int Target, int Stick)[] Chains = { (11, 12, 13, 72, 83), (15, 16, 17, 73, 84) };

    public static void Run()
    {
        var parents = new int[100];
        parents[0] = -1;
        var bind = Enumerable.Repeat(new BandPose(Vector3.Zero, Quaternion.Identity), 100).ToArray();
        bind[0] = new(new(2, 3, 4), Quaternion.CreateFromYawPitchRoll(0.8f, -0.3f, 0.2f));
        bind[74] = new(new(0.2f, 0, 0.1f), Quaternion.CreateFromYawPitchRoll(-1, 0.1f, 0.3f));
        foreach (var (upper, lower, hand, target, stick) in Chains)
        {
            parents[lower] = upper;
            parents[hand] = lower;
            parents[stick] = 74;
            bind[upper] = new(new(0, 1, 0), Quaternion.Identity);
            bind[lower] = new(new(0.3f, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.6f));
            bind[hand] = new(new(0.25f, 0, 0), Quaternion.Identity);
            bind[stick] = new(new(0.2f, 1, -0.3f), Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.7f));
        }
        foreach (var destination in new[] { new Vector3(0.1f, 1.15f, -0.3f), new Vector3(2, 1, 0), new Vector3(-0.5f, 1, 0), new Vector3(0, 1, 0) })
        {
            var pose = bind.ToArray();
            foreach (var (_, _, _, target, _) in Chains) pose[target] = new(destination, Quaternion.CreateFromYawPitchRoll(0.6f, 0.2f, -0.5f));
            CheckPose(parents, pose);
        }
        if (Environment.GetEnvironmentVariable("GH3_DIRECTION_ASSETS") is { } assets)
        {
            var directory = Path.Combine(assets, "budokan", "direction");
            var member = StagePerformance.Load(Path.Combine(directory, "performance.json"))!.Members["drummer"];
            using var reader = new BinaryReader(File.OpenRead(Path.Combine(assets, "budokan", "band", "drummer", "animation.bin")));
            var animation = new BandAnimation(reader);
            Check(member.Bones[72] == Gh3Checksum.Of("Bone_IK_Hand_Slave_L") && member.Bones[73] == Gh3Checksum.Of("Bone_IK_Hand_Slave_R"),
                "drummer hand targets match the original IK definitions");
            var timeline = StageChartReader.Load("", "", Path.Combine(assets, "events"), "My Curse", "Killswitch Engage");
            var player = new PerformancePlayer("drummer", directory, member, animation.BindPose, timeline);
            player.Prepare();
            for (var frame = 0; frame <= 253 * 30; frame++)
            {
                player.Sample(frame / 30.0);
                CheckPose(animation.Parents, player.Pose);
            }
            foreach (var time in new[] { 37.2, 84.8, 37.2, 37.2 })
            {
                player.Sample(time);
                StageDrumHands.Apply(animation.Parents, player.Pose);
                var first = player.Pose.ToArray();
                player.Sample(time);
                StageDrumHands.Apply(animation.Parents, player.Pose);
                Check(player.Pose.SequenceEqual(first), "held clocks and backward seeks repeat the corrected drummer pose");
            }
            for (var frame = 0; frame <= 600; frame++)
            {
                var sample = animation.At(frame / 30.0);
                CheckPose(animation.Parents, Enumerable.Range(0, animation.Parents.Length).Select(i => animation.Sample(sample, i)).ToArray());
            }
            Console.WriteLine("Validated drummer hand targets and stick grips through My Curse and fallback animations.");
        }
        Console.WriteLine("Drummer hand reach, hinge limits, grip offsets, and unchanged body checks passed.");
    }

    private static void CheckPose(int[] parents, BandPose[] pose)
    {
        var original = pose.ToArray();
        StageDrumHands.Apply(parents, pose);
        for (var i = 0; i < pose.Length; i++)
        {
            Check(float.IsFinite(pose[i].Position.LengthSquared()) && Math.Abs(pose[i].Rotation.LengthSquared() - 1) < 0.00001f, "drummer IK remains finite and normalized");
            if (i is not (11 or 12 or 13 or 15 or 16 or 17 or 83 or 84)) Check(pose[i] == original[i], "drummer IK preserves the other body, drum, and finger channels");
        }
        foreach (var (upper, lower, hand, target, stick) in Chains)
        {
            var a = World(parents, pose, upper);
            var b = World(parents, pose, lower);
            var c = World(parents, pose, hand);
            var goal = World(parents, original, target);
            var originalStick = World(parents, original, stick);
            var correctedStick = World(parents, pose, stick);
            var first = Vector3.Distance(World(parents, original, upper).Position, World(parents, original, lower).Position);
            var second = Vector3.Distance(World(parents, original, lower).Position, World(parents, original, hand).Position);
            Check(Math.Abs(Vector3.Distance(a.Position, b.Position) - first) < 0.00001f && Math.Abs(Vector3.Distance(b.Position, c.Position) - second) < 0.00001f,
                "drummer arms keep their source bone lengths");
            var cosine = Vector3.Dot(Vector3.Normalize(b.Position - a.Position), Vector3.Normalize(c.Position - b.Position));
            Check(cosine >= -0.97001f && cosine <= 0.97001f, "drummer elbows stay within the source hinge limits");
            var distance = Vector3.Distance(a.Position, goal.Position);
            var min = MathF.Sqrt(first * first + second * second - 2 * first * second * 0.97f);
            var max = MathF.Sqrt(first * first + second * second + 2 * first * second * 0.97f);
            if (distance >= min && distance <= max) Check(Vector3.Distance(c.Position, goal.Position) < 0.00001f, "reachable drummer hands meet their authored targets");
            Check(Math.Min((c.Rotation - goal.Rotation).Length(), (c.Rotation + goal.Rotation).Length()) < 0.00001f,
                "drummer palms use their authored target orientation");
            Check(Vector3.Distance(originalStick.Position - goal.Position, correctedStick.Position - c.Position) < 0.00001f && pose[stick].Rotation == original[stick].Rotation,
                "sticks keep their animated grip offsets and rotations when reach is limited");
        }
    }

    private static BandPose World(int[] parents, BandPose[] pose, int bone)
    {
        if (bone < 0) return new(Vector3.Zero, Quaternion.Identity);
        var parent = World(parents, pose, parents[bone]);
        return new(parent.Position + Vector3.Transform(pose[bone].Position, parent.Rotation), parent.Rotation * pose[bone].Rotation);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
