using System.Numerics;
using Gh3Skin;

internal static class DrumHandLogicTests
{
    public static void Run()
    {
        var branches = new[] { new StageBlendTrack.Branch(0, "a", 0.2), new StageBlendTrack.Branch(1, "b", 0.2), new StageBlendTrack.Branch(1.1, "c", 0.1) };
        Span<float> factors = stackalloc float[StageBlendTrack.MaxDepth + 1];
        var oldest = StageBlendTrack.Factors(branches, 1, 1.1, factors);
        Check(oldest == 0 && factors[0] == 1 && Math.Abs(factors[1] - 1 / 1.5f) < 1e-5f, "two GH3 DegenerateBlend branches give the new branch 1 / (2 - u)");
        oldest = StageBlendTrack.Factors(branches, 2, 1.12, factors);
        Check(oldest == 0 && Math.Abs(factors[1] - 0.8f / 1.1f) < 1e-5f && Math.Abs(factors[2] - 1 / 2.1f) < 1e-5f,
            "older GH3 branches decay at the newest blend rate and mix by running weight");
        oldest = StageBlendTrack.Factors(branches, 2, 1.25, factors);
        Check(oldest == 2 && factors[0] == 1, "branches leave the blend when their weight reaches zero");

        if (Environment.GetEnvironmentVariable("GH3_DIRECTION_ASSETS") is { } assets)
        {
            var directory = Path.Combine(assets, "budokan", "direction");
            var bank = StagePerformance.Load(Path.Combine(directory, "performance.json"))!;
            var member = bank.Members["drummer"];
            using var reader = new BinaryReader(File.OpenRead(Path.Combine(assets, "budokan", "band", "drummer", "animation.bin")));
            var animation = new BandAnimation(reader);
            var parents = animation.Parents;
            Check(member.Bones[72] == Gh3Checksum.Of("Bone_IK_Hand_Slave_L") && member.Bones[73] == Gh3Checksum.Of("Bone_IK_Hand_Slave_R"),
                "drummer hand targets match drummer_IK_params");
            var timeline = StageChartReader.Load("", "", Path.Combine(assets, "events"), "My Curse", "Killswitch Engage");
            var player = new PerformancePlayer("drummer", directory, member, animation.BindPose, timeline, parents, bank.Constants);
            player.Prepare();
            var reached = 0;
            var frames = 0;
            for (var frame = 0; frame <= 253 * 30; frame += 3)
            {
                player.Sample(frame / 30.0);
                foreach (var (upper, lower, hand, target) in new[] { (11, 12, 13, 72), (15, 16, 17, 73) })
                {
                    var a = World(parents, player.Pose, upper).Position;
                    var b = World(parents, player.Pose, lower).Position;
                    var c = World(parents, player.Pose, hand);
                    var goal = World(parents, player.Pose, target);
                    var cosine = Vector3.Dot(Vector3.Normalize(a - b), Vector3.Normalize(c.Position - b));
                    Check(cosine >= -0.9701f && cosine <= 0.9701f, "drummer elbows stay within the drummer_IK_params hinge limits");
                    Check(Math.Min((c.Rotation - goal.Rotation).Length(), (c.Rotation + goal.Rotation).Length()) < 1e-3f, "drummer palms take the target rotation");
                    frames++;
                    if (Vector3.Distance(c.Position, goal.Position) < 0.005f) reached++;
                }
            }
            Check(reached > frames * 0.97, "drummer hands reach their animated targets");
            player.Sample(173.3);
            var held = player.Pose.ToArray();
            player.Sample(37.2);
            player.Sample(173.3);
            Check(player.Pose.SequenceEqual(held), "held clocks and backward seeks repeat the drummer pose");
            Console.WriteLine("Validated drummer IK targets, hinge limits, and seeks through My Curse.");
        }
        Console.WriteLine("GH3 DegenerateBlend weight checks passed.");
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
