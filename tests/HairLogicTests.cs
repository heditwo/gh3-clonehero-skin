using System.Numerics;
using Gh3Skin;

internal static class HairLogicTests
{
    public static void Run()
    {
        CheckTransparency();
        var down = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2);
        var definition = new HairDefinition { Head = 0, Capsule = new() { A = new float[] { 0, 0, 0 }, B = new float[] { 0, 0.1f, 0 }, Radius = 0.1f },
            Joints = new[] { new HairJoint { Bone = 1, Parent = 0, Position = new float[] { 0.2f, 0, 0 }, Rotation = new[] { down.X, down.Y, down.Z, down.W },
                Length = 0.2f, Mass = 5, Friction = 0.3f, AngularDamping = 0.05f, Limits = new() { ["coneLimit"] = new float[] { -100, 0.5f } } } } };
        BandPose Sample(double t, BandPose[] pose)
        {
            pose[0] = new(new((float)Math.Sin(t * 6) * 0.2f, 2, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)Math.Sin(t * 4) * 0.4f));
            pose[1] = new(new(0.2f, 0, 0), down);
            return new(Vector3.Zero, Quaternion.Identity);
        }
        var pose = new BandPose[2];
        var reference = new StageHair(definition, new[] { -1, 0 }, Sample);
        reference.Sample(2.321); reference.Apply(pose);
        var expected = pose[1];
        Check(Math.Abs(Quaternion.Dot(expected.Rotation, down)) < 0.9999f, "moving head produces relative hair motion");
        Check(Vector3.Distance(expected.Position, new(0.2f, 0, 0)) < 0.00001f, "hair root stays attached");
        for (var i = 0; i < 10; i++) { reference.Sample(2.321); reference.Apply(pose); Check(pose[1] == expected, "paused hair does not advance"); }
        reference.Sample(12); reference.Sample(2.321); reference.Apply(pose);
        Check(pose[1] == expected, "backward seek restores hair state");
        foreach (var fps in new[] { 30, 60, 144 })
        {
            var hair = new StageHair(definition, new[] { -1, 0 }, Sample);
            for (var i = 0; i / (double)fps < 2.321; i++)
            {
                hair.Sample(i / (double)fps); hair.Apply(pose);
                var angle = 2 * MathF.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(pose[1].Rotation, down)), 0, 1));
                Check(float.IsFinite(angle) && angle <= 0.5001f, "hair respects the source cone limit");
            }
            hair.Sample(2.321); hair.Apply(pose);
            Check(pose[1] == expected, "hair is independent of render frame rate");
        }
        if (Environment.GetEnvironmentVariable("GH3_HAIR_ASSETS") is { } assets) CheckAssets(assets);
        Console.WriteLine("Hair inertia, attachment, limits, frame rates, pause, and seek checks passed.");
    }

    private static void CheckTransparency()
    {
        var triangles = new[] { 0, 1, 2, 3, 4, 5 };
        var order = new StageTriangleOrder(triangles);
        var vertices = new[] { new Vector3(0, 0, 1), new Vector3(1, 0, 1), new Vector3(0, 1, 1),
            new Vector3(0, 0, 3), new Vector3(1, 0, 3), new Vector3(0, 1, 3) };
        var output = new int[6];
        order.Sort(vertices, Vector3.UnitZ, output);
        Check(output.SequenceEqual(new[] { 3, 4, 5, 0, 1, 2 }), "rear hair triangles draw first");
        order.Sort(vertices, -Vector3.UnitZ, output);
        Check(output.SequenceEqual(triangles), "hair order follows the camera angle");
        for (var i = 0; i < 3; i++) vertices[i].Z = 5;
        order.Sort(vertices, Vector3.UnitZ, output);
        Check(output.SequenceEqual(triangles), "hair order follows the deformed vertices");
        order.Sort(vertices, Vector3.UnitX, output);
        Check(output.SequenceEqual(triangles), "coplanar hair has a stable order");
    }

    private static void CheckAssets(string assets)
    {
        var band = Path.Combine(assets, "budokan/band");
        var directory = Path.Combine(assets, "budokan/direction");
        var bank = HairBank.Load(Path.Combine(band, "hair.json"))!;
        var performance = StagePerformance.Load(Path.Combine(directory, "performance.json"))!;
        var timeline = StageChartReader.Load("", "", Path.Combine(assets, "events"), "My Curse", "Killswitch Engage");
        foreach (var (role, definition) in bank.Members)
        {
            using var reader = new BinaryReader(File.OpenRead(Path.Combine(band, role, "animation.bin")));
            var animation = new BandAnimation(reader);
            var player = new PerformancePlayer(role, directory, performance.Members[role], animation.BindPose, timeline);
            player.Prepare();
            var hair = new StageHair(definition, animation.Parents, (time, output) => {
                player.Sample(time); Array.Copy(player.Pose, output, output.Length);
                return new(Vector3.Zero, Quaternion.Identity);
            });
            var pose = new BandPose[animation.Parents.Length];
            for (double time = 0; time <= 60; time += 0.1)
            {
                hair.Sample(time); Array.Copy(player.Pose, pose, pose.Length); hair.Apply(pose);
                Check(pose.All(p => float.IsFinite(p.Position.LengthSquared()) && Math.Abs(p.Rotation.LengthSquared() - 1) < 0.001), "original hair remains finite and normalized");
            }
            hair.Sample(18.234); hair.Apply(pose);
            var saved = definition.Joints.Select(j => pose[j.Bone]).ToArray();
            hair.Sample(52.7); hair.Sample(18.234); hair.Apply(pose);
            Check(saved.SequenceEqual(definition.Joints.Select(j => pose[j.Bone])), "original hair survives a backward seek");
        }
        Console.WriteLine($"Validated {bank.Members.Values.Sum(m => m.Joints.Length)} original hair joints during My Curse.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
