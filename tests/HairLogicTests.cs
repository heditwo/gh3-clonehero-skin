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
        CheckCustomChains();
        CheckSpringChains();
        if (Environment.GetEnvironmentVariable("GH3_HAIR_ASSETS") is { } assets) CheckAssets(assets);
        if (Environment.GetEnvironmentVariable("GH3_CHARACTER_HAIR_ASSETS") is { } characters) CheckCharacterAssets(characters);
        Console.WriteLine("Hair inertia, attachment, limits, Havok limit frames, speed limit, chains, custom chain axes and colliders, stiffness, drag, gravity, bend limit, offset bones, frame rates, pause, and seek checks passed.");
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

    // Custom chains (character.json): a joint that points along its bone's +Y moves as the same joint along +X, and the
    // body colliders keep the joint segments out of their capsules, also when a collider moves through the chain.
    private static void CheckCustomChains()
    {
        var toX = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2);
        var cone = new Vector3(MathF.Cos(0.3f), -MathF.Sin(0.3f), 0);
        HairJoint Joint(int bone, int parent, float x, bool aligned) => new()
        {
            Bone = bone, Parent = parent, Position = aligned && parent > 0 ? new[] { 0f, x, 0f } : new[] { x, 0f, 0f },
            Rotation = aligned && parent == 0 ? new[] { toX.X, toX.Y, toX.Z, toX.W } : new[] { 0f, 0f, 0f, 1f },
            Direction = aligned ? new[] { 0f, 1f, 0f } : Array.Empty<float>(), Length = 0.2f, Mass = 2, Friction = 0.1f, AngularDamping = 0.5f,
            Limits = new() { ["coneLimit"] = new[] { -1f, 0.6f }, ["planesLimit"] = new[] { -0.5f, 0.4f } },
            TwistAxis = Axis(Vector3.UnitX, aligned), ConeAxis = Axis(cone, aligned), PlaneAxis = Axis(Vector3.UnitZ, aligned), HeadCapsule = false,
        };
        // An axis of the +X joint frame in the frame of the +Y bone (the bone frame turned by toX).
        float[] Axis(Vector3 v, bool aligned)
        {
            var a = aligned ? Vector3.Transform(v, Quaternion.Inverse(toX)) : v;
            return new[] { a.X, a.Y, a.Z };
        }
        BandPose Moving(double t, BandPose[] pose, bool aligned)
        {
            pose[0] = new(new((float)Math.Sin(t * 5) * 0.3f, 2, (float)Math.Cos(t * 3) * 0.1f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)Math.Sin(t * 2)));
            pose[1] = new(new(0.1f, 0, 0), aligned ? toX : Quaternion.Identity);
            pose[2] = new(aligned ? new(0, 0.2f, 0) : new(0.2f, 0, 0), Quaternion.Identity);
            return new(Vector3.Zero, Quaternion.Identity);
        }
        var parents = new[] { -1, 0, 1 };
        var poses = new[] { false, true }.Select(aligned =>
        {
            var definition = new HairDefinition { Head = -1, Joints = new[] { Joint(1, 0, 0.1f, aligned), Joint(2, 1, 0.2f, aligned) } };
            var hair = new StageHair(definition, parents, (t, pose) => Moving(t, pose, aligned));
            var pose = new BandPose[3];
            var tips = new List<Vector3>();
            for (var tick = 0; tick <= 360; tick += 3)
            {
                hair.Sample(tick / 120.0); Moving(tick / 120.0, pose, aligned); hair.Apply(pose);
                var world = new BandPose(Vector3.Zero, Quaternion.Identity);
                for (var b = 0; b < 3; b++) world = new(world.Position + Vector3.Transform(pose[b].Position, world.Rotation), world.Rotation * pose[b].Rotation);
                tips.Add(world.Position + Vector3.Transform(aligned ? Vector3.UnitY : Vector3.UnitX, world.Rotation) * 0.2f);
            }
            return tips;
        }).ToArray();
        Check(poses[0].Zip(poses[1]).All(p => Vector3.Distance(p.First, p.Second) < 1e-4f), "a +Y chain moves as the same +X chain");
        Check(poses[0].Max(p => p.Y) < 1.95f || poses[0].Any(p => p.Y < 1.9f), "the custom chain moves");

        // A sphere on a moving bone (bone 3, not a joint) sweeps through a hanging chain of three joints.
        var down = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2);
        HairJoint Hanging(int bone, int parent) => new()
        {
            Bone = bone, Parent = parent, Position = parent == 0 ? new[] { 0.1f, 0f, 0f } : new[] { 0.12f, 0f, 0f },
            Rotation = parent == 0 ? new[] { down.X, down.Y, down.Z, down.W } : new[] { 0f, 0f, 0f, 1f }, Length = 0.12f, Mass = 1, Friction = 0.05f,
            AngularDamping = 0.3f, Limits = new() { ["coneLimit"] = new[] { -1f, 1.2f } }, Radius = 0.01f, ColliderMask = 1, HeadCapsule = false,
        };
        var sweep = new HairDefinition
        {
            Head = -1, Joints = new[] { Hanging(1, 0), Hanging(2, 1), Hanging(3, 2) },
            Colliders = new[] { new HairCollider { Bone = 4, A = new[] { 0f, 0, -0.1f }, B = new[] { 0f, 0, 0.1f }, Radius = 0.05f } },
        };
        BandPose Sweep(double t, BandPose[] pose)
        {
            pose[0] = new(new(0, 2, 0), Quaternion.Identity);
            pose[1] = new(new(0.1f, 0, 0), down);
            pose[2] = new(new(0.12f, 0, 0), Quaternion.Identity);
            pose[3] = new(new(0.12f, 0, 0), Quaternion.Identity);
            pose[4] = new(new(0.1f + 0.25f * (float)Math.Cos(t * 4), -0.2f, 0), Quaternion.Identity);
            return new(Vector3.Zero, Quaternion.Identity);
        }
        var sweepParents = new[] { -1, 0, 1, 2, 0 };
        foreach (var fps in new[] { 60, 144 })
        {
            var hair = new StageHair(sweep, sweepParents, Sweep);
            var frame = new BandPose[5];
            var deepest = 0f;
            for (var i = 0; i / (double)fps < 6; i++)
            {
                var time = i / (double)fps;
                hair.Sample(time); Sweep(time, frame); hair.Apply(frame);
                var world = new BandPose[5];
                for (var b = 0; b < 5; b++)
                {
                    var parent = sweepParents[b] < 0 ? new BandPose(Vector3.Zero, Quaternion.Identity) : world[sweepParents[b]];
                    world[b] = new(parent.Position + Vector3.Transform(frame[b].Position, parent.Rotation), parent.Rotation * frame[b].Rotation);
                }
                var centre = world[4].Position;
                for (var b = 1; b <= 3; b++)
                {
                    var a = world[b].Position;
                    var tip = a + Vector3.Transform(Vector3.UnitX, world[b].Rotation) * 0.12f;
                    var along = Math.Clamp(Vector3.Dot(centre - a, tip - a) / (tip - a).LengthSquared(), 0, 1);
                    var distance = Vector3.Distance(centre, a + (tip - a) * along);
                    if (Math.Abs(centre.Z - (a + (tip - a) * along).Z) < 0.1f) deepest = Math.Max(deepest, 0.06f - distance);
                }
                Check(world.All(p => float.IsFinite(p.Position.X) && float.IsFinite(p.Rotation.W)), "colliding chains stay finite");
            }
            Check(deepest < 0.005f, FormattableString.Invariant($"a moving collider does not pass through the chain at {fps} fps (depth {deepest:F3} m)"));
        }
    }

    // Custom chain physics: the stiffness holds the bind direction against gravity, the drag stops the swings after a
    // move, the gravity share, the bend limit of the chain end, a joint that hangs from an offset bone below the joint
    // above it, and the frame rate independence.
    private static void CheckSpringChains()
    {
        // A level chain of four 0.15 m joints on a head that jerks sideways at 1 s and stops.
        HairJoint Joint(int bone, int parent, float stiffness, float drag, float gravity, float bend) => new()
        {
            Bone = bone, Parent = parent, Position = new[] { parent == 0 ? 0.1f : 0.15f, 0f, 0f }, Rotation = new[] { 0f, 0f, 0f, 1f }, Length = 0.15f, Mass = 1,
            Friction = 0.02f, AngularDamping = 1, Limits = new() { ["coneLimit"] = new[] { -1f, 0.8f } }, HeadCapsule = false, Custom = true,
            Stiffness = stiffness, Drag = drag, Gravity = gravity, BendLimit = bend,
        };
        BandPose Jerk(double t, BandPose[] pose)
        {
            var x = (float)Math.Clamp((t - 1) / 0.15, 0, 1) * 0.3f;
            pose[0] = new(new(x, 2, 0), Quaternion.Identity);
            for (var i = 1; i < pose.Length; i++) pose[i] = new(new(i == 1 ? 0.1f : 0.15f, 0, 0), Quaternion.Identity);
            return new(Vector3.Zero, Quaternion.Identity);
        }
        var parents = new[] { -1, 0, 1, 2, 3 };
        HairDefinition Chain(float stiffness, float drag, float gravity, float bend = 0) =>
            new() { Head = -1, Joints = Enumerable.Range(1, 4).Select(b => Joint(b, b - 1, stiffness, drag, gravity, bend)).ToArray() };
        Vector3[] Ends(HairDefinition definition, int[] rig, StageHair.PoseSampler sample, double seconds, int fps = 120)
        {
            var hair = new StageHair(definition, rig, sample);
            var pose = new BandPose[rig.Length];
            var ends = new List<Vector3>();
            for (var i = 0; i / (double)fps <= seconds; i++)
            {
                hair.Sample(i / (double)fps); sample(i / (double)fps, pose); hair.Apply(pose);
                var world = new BandPose[rig.Length];
                for (var b = 0; b < rig.Length; b++)
                {
                    var parent = rig[b] < 0 ? new BandPose(Vector3.Zero, Quaternion.Identity) : world[rig[b]];
                    world[b] = new(parent.Position + Vector3.Transform(pose[b].Position, parent.Rotation), parent.Rotation * pose[b].Rotation);
                }
                var last = world[rig.Length - 1];
                ends.Add(last.Position + Vector3.Transform(Vector3.UnitX, last.Rotation) * 0.15f - world[0].Position);
            }
            return ends.ToArray();
        }
        float Sag(Vector3 end) => MathF.Atan2(-end.Y, end.X);
        var loose = Ends(Chain(0, 15, 1), parents, Jerk, 0.9);
        var stiff = Ends(Chain(400, 15, 1), parents, Jerk, 0.9);
        var weightless = Ends(Chain(0, 0, 0), parents, Jerk, 0.9);
        Check(Sag(stiff[^1]) < 0.5f * Sag(loose[^1]) && Sag(stiff[^1]) > 0.02f, FormattableString.Invariant($"the stiffness holds the bind shape against gravity ({Sag(stiff[^1]):F2} rad, loose {Sag(loose[^1]):F2} rad)"));
        Check(Math.Abs(Sag(weightless[^1])) < 0.001f, "a chain without gravity keeps its bind shape");
        // After the stop (1.15 s), the end moves less with the drag.
        float Motion(Vector3[] ends) => Enumerable.Range(1, ends.Length - 1).Where(i => i > 120 * 1.6).Sum(i => Vector3.Distance(ends[i], ends[i - 1]));
        var swinging = Ends(Chain(150, 0, 1), parents, Jerk, 3);
        var damped = Ends(Chain(150, 15, 1), parents, Jerk, 3);
        Check(Motion(damped) < 0.3f * Motion(swinging), FormattableString.Invariant($"the drag stops the swings after a move ({Motion(damped):F3} m, without drag {Motion(swinging):F3} m)"));
        // The bend limit: the chord from the chain start to the end stays within 0.5 rad of the bind direction (+X).
        var bent = Ends(Chain(0, 5, 1, 0.5f), parents, Jerk, 3);
        var chord = bent.Skip(240).Max(e => MathF.Atan2(-(e.Y), e.X - 0.1f));
        Check(chord < 0.56f && Sag(loose[^1]) > 0.7f, FormattableString.Invariant($"the bend limit holds the chain end ({chord:F2} rad)"));
        // Offset bones between the joints (a game rig): bones 2 and 4 are helpers at the joint ends; the chain moves as
        // the chain without them.
        var offsetParents = new[] { -1, 0, 1, 2, 3, 4 };
        BandPose Offset(double t, BandPose[] pose)
        {
            Jerk(t, pose);
            pose[2] = new(new(0.15f, 0, 0), Quaternion.Identity);
            pose[3] = new(Vector3.Zero, Quaternion.Identity);
            pose[4] = new(new(0.15f, 0, 0), Quaternion.Identity);
            pose[5] = new(Vector3.Zero, Quaternion.Identity);
            return new(Vector3.Zero, Quaternion.Identity);
        }
        var helpers = new HairDefinition { Head = -1, Joints = new[] { Joint(1, 0, 60, 10, 1, 0), Joint(3, 2, 60, 10, 1, 0), Joint(5, 4, 60, 10, 1, 0) } };
        helpers.Joints[1].Position = helpers.Joints[2].Position = new[] { 0f, 0f, 0f };
        var direct = new HairDefinition { Head = -1, Joints = new[] { Joint(1, 0, 60, 10, 1, 0), Joint(2, 1, 60, 10, 1, 0), Joint(3, 2, 60, 10, 1, 0) } };
        var withHelpers = Ends(helpers, offsetParents, Offset, 2.5);
        var withoutHelpers = Ends(direct, new[] { -1, 0, 1, 2 }, (t, pose) => { var full = new BandPose[5]; Jerk(t, full); Array.Copy(full, pose, 4); return new(Vector3.Zero, Quaternion.Identity); }, 2.5);
        Check(withHelpers.Zip(withoutHelpers).All(p => Vector3.Distance(p.First, p.Second) < 2e-3f), "a joint below an offset bone follows the joint above it");
        Check(withHelpers.Max(e => Vector3.Distance(e, withHelpers[0])) > 0.05f, "the chain with offset bones moves");
        foreach (var fps in new[] { 30, 60, 144 })
        {
            var ends = Ends(Chain(150, 15, 1, 0.5f), parents, Jerk, 2, fps);
            var reference = Ends(Chain(150, 15, 1, 0.5f), parents, Jerk, 2, 120);
            Check(Vector3.Distance(ends[^1], reference[^1]) < 1e-5f, $"the custom chain physics is independent of the render frame rate ({fps} fps)");
        }
        Check(Throws(() => new StageHair(Chain(6000, 0, 1), parents, Jerk)) && Throws(() => new StageHair(new HairDefinition { Head = -1,
            Joints = new[] { new HairJoint { Bone = 1, Parent = 0, Position = new[] { 0f, 0, 0 }, Rotation = new[] { 0f, 0, 0, 1 }, Length = 0.1f, Mass = 1,
                Limits = new() { ["coneLimit"] = new[] { -1f, 0.5f } }, Stiffness = 10 } } }, new[] { -1, 0 }, Jerk)),
            "the solver refuses a stiffness out of range and the custom fields on GH3 joints");
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (InvalidDataException) { return true; }
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
        var timeline = StageChartReader.LoadEventFile(Path.Combine(Path.Combine(assets, "events"), "mycurse.json"));
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

    // Every profile of the character library plays My Curse in the role of its profile with its own animation set; its hair must
    // stay below StageHair.WarningSpeed (the runtime warning of StageBand).
    private static void CheckCharacterAssets(string assets)
    {
        var folder = Path.Combine(assets, "libraries/gh3-characters-v1");
        var library = Gh3Skin.Packages.CharacterLibrary.Load(folder);
        var bank = CharacterMotionBank.Load(folder);
        var performance = StagePerformance.Load(Path.Combine(assets, "libraries/gh3-band-v1/direction/performance.json"))!;
        var timeline = StageChartReader.LoadEventFile(Path.Combine(Path.Combine(assets, "stages/events"), "mycurse.json"));
        foreach (var (id, profile) in library.Profiles.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (profile.Hair.Length == 0) continue;
            var set = bank.Animations[profile.Animations];
            var motion = new CharacterMotion
            {
                Profile = profile, SetName = profile.Animations, Hair = bank.Hair[profile.Hair],
                Member = new PerformanceMember { Bones = profile.Bones, Groups = set.Groups, Layers = set.Layers, Clips = set.Clips },
                ClipDirectory = Path.Combine(folder, "clips"),
                Offsets = CharacterMotion.RestOffsets(profile.Bones, profile.Bind, library.SetClipRigs[profile.Animations].Bones, library.SetClipRigs[profile.Animations].Bind),
            };
            var animation = BandAnimation.FromRig(profile.Parents, profile.Bind);
            var player = new PerformancePlayer(profile.Role, "", motion.Member, animation.BindPose, timeline, animation.Parents, performance.Constants, motion);
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
