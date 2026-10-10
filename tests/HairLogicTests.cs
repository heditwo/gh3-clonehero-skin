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
                // The limits are soft, as in Havok (each step corrects about half of a violation).
                Check(float.IsFinite(angle) && angle <= 0.6f, "hair respects the source cone limit");
            }
            hair.Sample(2.321); hair.Apply(pose);
            Check(pose[1] == expected, "hair is independent of render frame rate");
        }
        CheckHavokLimits();
        if (Environment.GetEnvironmentVariable("GH3_HAIR_ASSETS") is { } assets) CheckAssets(assets);
        if (Environment.GetEnvironmentVariable("GH3_CHARACTER_HAIR_ASSETS") is { } characters) CheckCharacterAssets(characters);
        Console.WriteLine("Hair inertia, attachment, limits, Havok limit frames, speed limit, chains, frame rates, pause, and seek checks passed.");
    }

    // A still head: the joints hang from their limits under gravity (no spring to the bind pose), the cone is centred on
    // the parent's cone axis, the planes hold, the speed limit holds, and a chain stays connected.
    private static void CheckHavokLimits()
    {
        HairJoint Joint(int bone, int parent, float x, float cone, float mass = 5) => new()
        {
            Bone = bone, Parent = parent, Position = new[] { x, 0f, 0f }, Rotation = new[] { 0f, 0f, 0f, 1f }, Length = 0.2f, Mass = mass,
            Friction = 0.3f, AngularDamping = 0.05f, Limits = new() { ["coneLimit"] = new[] { -100f, cone }, ["planesLimit"] = new[] { -1.5f, 1.5f } },
        };
        BandPose Still(double t, BandPose[] pose)
        {
            pose[0] = new(new(0, 2, 0), Quaternion.Identity);
            for (var i = 1; i < pose.Length; i++) pose[i] = new(new(i == 1 ? 0.1f : 0.2f, 0, 0), Quaternion.Identity);
            return new(Vector3.Zero, Quaternion.Identity);
        }
        float Angle(Vector3 a, Vector3 b) => MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1, 1));
        Vector3 Direction(HairDefinition definition, int[] parents, int bone, double time)
        {
            var hair = new StageHair(definition, parents, Still);
            var pose = new BandPose[parents.Length];
            hair.Sample(time); Still(time, pose); hair.Apply(pose);
            var rotation = Quaternion.Identity;
            for (var b = bone; b >= 0; b = parents[b]) rotation = pose[b].Rotation * rotation;
            return Vector3.Transform(Vector3.UnitX, rotation);
        }
        var capsule = new HairCapsule { A = new float[] { 0, 0, 0 }, B = new float[] { 0, 0.01f, 0 }, Radius = 0.01f };
        var level = new HairDefinition { Head = 0, Capsule = capsule, Joints = new[] { Joint(1, 0, 0.1f, 1.2f) } };
        var hanging = Direction(level, new[] { -1, 0 }, 1, 4);
        Check(Math.Abs(Angle(hanging, Vector3.UnitX) - 1.2f) < 0.03f && hanging.Y < 0, "a level joint hangs at its cone limit, not near the bind pose");
        level.Joints[0].TwistAxis = new[] { 1f, 0, 0 };
        level.Joints[0].ConeAxis = new[] { MathF.Cos(0.3f), -MathF.Sin(0.3f), 0 };
        level.Joints[0].PlaneAxis = new[] { 0f, 0, 1 };
        level.Joints[0].Limits["coneLimit"] = new[] { -100f, 0.2f };
        hanging = Direction(level, new[] { -1, 0 }, 1, 4);
        Check(Math.Abs(Angle(hanging, new Vector3(MathF.Cos(0.3f), -MathF.Sin(0.3f), 0)) - 0.2f) < 0.02f && Angle(hanging, Vector3.UnitX) > 0.45f,
            "the cone is centred on the cone axis of the parent");
        level.Joints[0].ConeAxis = new[] { 1f, 0, 0 };
        level.Joints[0].PlaneAxis = new[] { 0f, 1, 0 };
        level.Joints[0].Limits["coneLimit"] = new[] { -100f, 1f };
        level.Joints[0].Limits["planesLimit"] = new[] { -0.1f, 0.3f };
        hanging = Direction(level, new[] { -1, 0 }, 1, 4);
        Check(Math.Abs(MathF.Asin(hanging.Y) + 0.1f) < 0.02f, "the planes limit the joint");
        level.Joints[0].Limits["planesLimit"] = new[] { -1.5f, 1.5f };
        level.Joints[0].MaxAngularVelocity = 2;
        level.Joints[0].Friction = 0;
        var parents = new[] { -1, 0 };
        var free = new StageHair(level, parents, Still);
        var frame = new BandPose[2];
        var last = Vector3.UnitX;
        var fastest = 0f;
        for (var tick = 1; tick <= 60; tick++)
        {
            free.Sample(tick / 120.0); Still(tick / 120.0, frame); free.Apply(frame);
            var now = Vector3.Transform(Vector3.UnitX, frame[1].Rotation);
            fastest = Math.Max(fastest, Angle(last, now) * 120);
            last = now;
        }
        Check(fastest <= 2.01f && fastest > 1.9f, "the body speed limit holds");
        var chain = new HairDefinition { Head = 0, Capsule = capsule, Joints = new[] { Joint(1, 0, 0.1f, 0.5f), Joint(2, 1, 0.2f, 0.5f, 20) } };
        var chainParents = new[] { -1, 0, 1 };
        var chained = new StageHair(chain, chainParents, Still);
        var chainPose = new BandPose[3];
        for (var tick = 1; tick <= 240; tick++)
        {
            chained.Sample(tick / 120.0); Still(tick / 120.0, chainPose); chained.Apply(chainPose);
            Check(Vector3.Distance(chainPose[2].Position, new(0.2f, 0, 0)) < 1e-5f, "a chain stays connected");
        }
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

    // Every profile of the character library plays My Curse as the guitarist with its own animation set; its hair must
    // stay below StageHair.WarningSpeed (the runtime warning of StageBand).
    private static void CheckCharacterAssets(string assets)
    {
        var folder = Path.Combine(assets, "libraries/gh3-characters-v1");
        var library = Gh3Skin.Packages.CharacterLibrary.Load(folder);
        var bank = CharacterMotionBank.Load(folder);
        var performance = StagePerformance.Load(Path.Combine(assets, "libraries/gh3-band-v1/direction/performance.json"))!;
        var timeline = StageChartReader.Load("", "", Path.Combine(assets, "stages/events"), "My Curse", "Killswitch Engage");
        foreach (var (id, profile) in library.Profiles.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (profile.Hair.Length == 0) continue;
            var set = bank.Animations[profile.Animations];
            var motion = new CharacterMotion
            {
                Profile = profile, SetName = profile.Animations, Hair = bank.Hair[profile.Hair],
                Member = new PerformanceMember { Bones = profile.Bones, Groups = set.Groups, Layers = set.Layers, Clips = set.Clips },
                ClipDirectory = Path.Combine(folder, "clips"),
                Offsets = CharacterMotion.RestOffsets(profile.Bones, profile.Bind, library.ClipRig.Bones, library.ClipRig.Bind),
            };
            var animation = BandAnimation.FromRig(profile.Parents, profile.Bind);
            var player = new PerformancePlayer("guitarist", "", motion.Member, animation.BindPose, timeline, animation.Parents, performance.Constants, motion);
            player.Prepare();
            var hair = new StageHair(motion.Hair, animation.Parents, (time, output) =>
            {
                player.Sample(time); Array.Copy(player.Pose, output, output.Length);
                return new(Vector3.Zero, Quaternion.Identity);
            });
            var pose = new BandPose[animation.Parents.Length];
            var world = new BandPose[pose.Length];
            var swing = new double[motion.Hair.Joints.Length];
            var samples = 0;
            var measured = 0;
            for (double time = 0; time <= 240; time += 1 / 60.0)
            {
                hair.Sample(time);
                if (samples++ % 6 != 0) continue;
                measured++;
                Array.Copy(player.Pose, pose, pose.Length); hair.Apply(pose);
                for (var i = 0; i < pose.Length; i++)
                {
                    var parent = animation.Parents[i] < 0 ? new BandPose(Vector3.Zero, Quaternion.Identity) : world[animation.Parents[i]];
                    world[i] = new(parent.Position + Vector3.Transform(pose[i].Position, parent.Rotation), Quaternion.Normalize(parent.Rotation * pose[i].Rotation));
                }
                for (var j = 0; j < swing.Length; j++)
                {
                    var joint = motion.Hair.Joints[j];
                    var rest = world[joint.Parent].Rotation * new Quaternion(joint.Rotation[0], joint.Rotation[1], joint.Rotation[2], joint.Rotation[3]);
                    swing[j] += 2 * Math.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(Quaternion.Normalize(rest), world[joint.Bone].Rotation)), 0, 1));
                }
            }
            var worst = Enumerable.Range(0, swing.Length).MaxBy(j => swing[j]);
            Console.WriteLine(FormattableString.Invariant($"{id}: hair peak {hair.PeakSpeed:F2} m/s at {hair.PeakBone}, ") +
                FormattableString.Invariant($"largest mean turn from the rest pose {swing[worst] / measured * 180 / Math.PI:F1} degrees at {motion.Hair.Joints[worst].Bone}"));
            Check(hair.PeakSpeed < StageHair.WarningSpeed, $"{id}: hair moves faster than {StageHair.WarningSpeed} m/s");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
