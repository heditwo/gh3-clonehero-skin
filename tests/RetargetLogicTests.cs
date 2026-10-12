using System.Numerics;
using Gh3Skin;
using Gh3Skin.Packages;

// Retarget of GH3 clips to a character skeleton (PACKAGE-FORMAT.md section 9.6): bone directions follow the GH3 bones
// for any rest roll and for a T-pose against the GH3 A-pose, the identity case, the twist bones, followers, root motion,
// and the IK hand goal.
internal static class RetargetLogicTests
{
    private static readonly (string Role, string Gh3, string Parent, Vector3 Position)[] Body =
    {
        ("", "control_root", "", new(0, 0, 0)),
        ("hips", "bone_pelvis", "control_root", new(0, 1.0f, 0)),
        ("spine", "bone_stomach_lower", "bone_pelvis", new(0, 1.08f, 0)),
        ("chest", "bone_stomach_upper", "bone_stomach_lower", new(0, 1.18f, 0.02f)),
        ("upperChest", "bone_chest", "bone_stomach_upper", new(0, 1.28f, 0.05f)),
        ("neck", "bone_neck", "bone_chest", new(0, 1.45f, 0.05f)),
        ("head", "bone_head", "bone_neck", new(0, 1.52f, 0.02f)),
        ("leftShoulder", "bone_collar_l", "bone_chest", new(0.02f, 1.37f, 0.01f)),
        ("leftUpperArm", "bone_bicep_l", "bone_collar_l", new(0.15f, 1.37f, 0.03f)),
        ("leftLowerArm", "bone_forearm_l", "bone_bicep_l", new(0.31f, 1.2f, 0.03f)),
        ("leftHand", "bone_palm_l", "bone_forearm_l", new(0.47f, 1.04f, 0.02f)),
        ("leftIndexProximal", "bone-7f8c4cd6", "bone_palm_l", new(0.53f, 0.99f, -0.01f)),
        ("leftMiddleProximal", "bone-68ec1dda", "bone_palm_l", new(0.535f, 0.99f, 0.013f)),
        ("leftMiddleIntermediate", "bone-f55a971a", "bone-68ec1dda", new(0.563f, 0.96f, 0.012f)),
        ("leftLittleProximal", "bone-0fd44a48", "bone_palm_l", new(0.52f, 0.99f, 0.047f)),
        ("rightShoulder", "bone_collar_r", "bone_chest", new(-0.02f, 1.37f, 0.01f)),
        ("rightUpperArm", "bone_bicep_r", "bone_collar_r", new(-0.15f, 1.37f, 0.03f)),
        ("rightLowerArm", "bone_forearm_r", "bone_bicep_r", new(-0.31f, 1.2f, 0.03f)),
        ("rightHand", "bone_palm_r", "bone_forearm_r", new(-0.47f, 1.04f, 0.02f)),
        ("rightIndexProximal", "bone-858371b5", "bone_palm_r", new(-0.53f, 0.99f, -0.01f)),
        ("rightMiddleProximal", "bone-92e320b9", "bone_palm_r", new(-0.535f, 0.99f, 0.013f)),
        ("rightLittleProximal", "bone-f5db772b", "bone_palm_r", new(-0.52f, 0.99f, 0.047f)),
        ("leftUpperLeg", "bone_thigh_l", "bone_pelvis", new(0.09f, 0.95f, 0.01f)),
        ("leftLowerLeg", "bone_knee_l", "bone_thigh_l", new(0.11f, 0.61f, 0.02f)),
        ("leftFoot", "bone_ankle_l", "bone_knee_l", new(0.15f, 0.17f, 0.02f)),
        ("leftToes", "bone_toe_l", "bone_ankle_l", new(0.15f, 0.09f, -0.07f)),
        ("rightUpperLeg", "bone_thigh_r", "bone_pelvis", new(-0.09f, 0.95f, 0.01f)),
        ("rightLowerLeg", "bone_knee_r", "bone_thigh_r", new(-0.11f, 0.61f, 0.02f)),
        ("rightFoot", "bone_ankle_r", "bone_knee_r", new(-0.15f, 0.17f, 0.02f)),
        ("rightToes", "bone_toe_r", "bone_ankle_r", new(-0.15f, 0.09f, -0.07f)),
    };

    public static void Run()
    {
        CheckIdentity();
        CheckDirections();
        CheckTwistAndFollowers();
        CheckRootMotionAndIk();
        CheckPythonFixture("retarget-case.json");
        CheckPythonFixture("retarget-any-rig.json");
        CheckPythonFixture("retarget-singer.json");
        CheckPythonFixture("retarget-drummer.json");
        var library = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../assets/libraries/gh3-band-v1"));
        if (File.Exists(Path.Combine(library, "catalog.json"))) CheckGh3Rigs(library);
        Console.WriteLine("Retarget identity, rest-pose correction (T-pose, bone rolls, scale), twist bones, followers, root motion, IK, the four Python fixtures (with the gear), " +
            "GH3 rigs as skeletons (humanoid map, face bones, scale), and the drum kit, throne, sticks, and microphone stand fit passed.");
    }

    private static string Key(string name) => name.StartsWith("bone-") ? name[5..] : Gh3Checksum.Of(name);

    private static (string[] Bones, int[] Parents, BandPose[] Bind) Source()
    {
        var bones = Body.Select(b => Key(b.Gh3)).ToArray();
        var parents = Body.Select(b => b.Parent.Length == 0 ? -1 : Array.IndexOf(bones, Key(b.Parent))).ToArray();
        var bind = Body.Select((b, i) => new BandPose(b.Position - (parents[i] < 0 ? Vector3.Zero : Body[parents[i]].Position), Quaternion.Identity)).ToArray();
        return (bones, parents, bind);
    }

    // A skeleton with the same joints, scaled, with the arms in a T-pose, and with a random roll on every bone.
    private static RetargetSkeleton Target(float scale, bool tPose, int seed, Func<string, bool>? keep = null)
    {
        var random = new Random(seed);
        var rows = Body.Where(b => keep == null || b.Role.Length == 0 || keep(b.Role)).ToArray();
        var names = rows.Select(b => "t_" + b.Gh3).ToArray();
        var parents = rows.Select(b => b.Parent.Length == 0 ? -1 : Array.IndexOf(names, "t_" + b.Parent)).ToArray();
        var world = rows.Select(b => b.Position * scale).ToArray();
        if (tPose)
            for (var i = 0; i < rows.Length; i++)
            {
                var side = rows[i].Role.StartsWith("left") ? 1 : rows[i].Role.StartsWith("right") ? -1 : 0;
                if (side == 0 || rows[i].Role.Contains("Shoulder") || rows[i].Role.Contains("Leg") || rows[i].Role.Contains("Foot") || rows[i].Role.Contains("Toes") ||
                    rows[i].Role.EndsWith("UpperArm")) continue;
                var shoulder = world[Array.FindIndex(rows, r => r.Role == (side > 0 ? "leftUpperArm" : "rightUpperArm"))];
                var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, side * 0.8f);
                world[i] = shoulder + Vector3.Transform(world[i] - shoulder, turn);
            }
        var rotation = rows.Select(_ => Quaternion.Normalize(new Quaternion((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f,
            (float)random.NextDouble() - 0.5f, (float)random.NextDouble() + 0.2f))).ToArray();
        var bind = new BandPose[rows.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            var parent = parents[i];
            bind[i] = parent < 0 ? new BandPose(world[i], rotation[i]) : new BandPose(
                Vector3.Transform(world[i] - world[parent], Quaternion.Inverse(rotation[parent])), Quaternion.Normalize(Quaternion.Inverse(rotation[parent]) * rotation[i]));
        }
        var humanoid = rows.Select((b, i) => (b.Role, i)).Where(p => p.Role.Length > 0).ToDictionary(p => p.Role, p => p.i);
        return new RetargetSkeleton { Bones = names, Parents = parents, Bind = bind, Humanoid = humanoid };
    }

    private static BandPose[] RandomPose((string[] Bones, int[] Parents, BandPose[] Bind) source, int seed, float amount)
    {
        var random = new Random(seed);
        return source.Bind.Select((b, i) => i == 0 ? b : new BandPose(b.Position, Quaternion.Normalize(b.Rotation * Quaternion.CreateFromAxisAngle(
            Vector3.Normalize(new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f)),
            ((float)random.NextDouble() - 0.5f) * amount)))).ToArray();
    }

    private static Vector3[] WorldPositions(int[] parents, BandPose[] local)
    {
        var p = new Vector3[parents.Length];
        var q = new Quaternion[parents.Length];
        for (var pass = 0; pass < 2; pass++)
            for (var i = 0; i < parents.Length; i++)
            {
                var parent = parents[i];
                p[i] = parent < 0 ? local[i].Position : p[parent] + Vector3.Transform(local[i].Position, q[parent]);
                q[i] = parent < 0 ? local[i].Rotation : q[parent] * local[i].Rotation;
            }
        return p;
    }

    private static float Angle(Vector3 a, Vector3 b) =>
        MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1, 1)) * 180 / MathF.PI;

    private static void CheckIdentity()
    {
        var source = Source();
        var target = new RetargetSkeleton
        {
            Bones = source.Bones, Parents = source.Parents, Bind = source.Bind,
            Humanoid = Body.Select((b, i) => (b.Role, i)).Where(p => p.Role.Length > 0).ToDictionary(p => p.Role, p => p.i),
        };
        var retarget = new CharacterRetarget(source.Bones, source.Parents, source.Bind, RigFixtures.Points(source.Bones), target, "singer");
        Check(Math.Abs(retarget.HipScale - 1) < 1e-5 && Math.Abs(retarget.TorsoScale - 1) < 1e-5, "the scales of the same skeleton are 1");
        var pose = RandomPose(source, 7, 1.2f);
        var result = new BandPose[source.Bones.Length];
        retarget.Apply(pose, result, 0, 0);
        // World rotations are equal; the forearms differ by the half twist that they take from the hands.
        var worst = 0f;
        for (var i = 1; i < pose.Length; i++)
        {
            if (Body[i].Role.EndsWith("LowerArm")) continue;
            var d = Quaternion.Dot(retarget.SourceWorld(i).Rotation, retarget.World(i).Rotation);
            worst = Math.Max(worst, MathF.Acos(Math.Min(1, Math.Abs(d))) * 360 / MathF.PI);
        }
        Check(worst < 0.05f, $"the same skeleton gets the same world rotations ({worst:0.###}°)");
    }

    private static void CheckDirections()
    {
        var source = Source();
        foreach (var (scale, tPose, seed) in new[] { (1f, false, 1), (0.8f, true, 2), (1.15f, true, 3) })
        {
            var target = Target(scale, tPose, seed);
            var retarget = new CharacterRetarget(source.Bones, source.Parents, source.Bind, RigFixtures.Points(source.Bones), target, "singer");
            for (var trial = 0; trial < 5; trial++)
            {
                var pose = RandomPose(source, 100 + trial, trial == 0 ? 0 : 1.0f);
                var result = new BandPose[target.Bones.Length];
                retarget.Apply(pose, result, 0, 0);
                var sp = WorldPositions(source.Parents, pose);
                var tp = WorldPositions(target.Parents, result);
                var worst = 0f;
                foreach (var (role, bone) in target.Humanoid)
                {
                    if (role.Contains("Leg") || role.Contains("Foot")) continue;
                    var next = HumanoidRoles.Next(role, target.Humanoid.ContainsKey);
                    if (next == null || !target.Humanoid.TryGetValue(next, out var child)) continue;
                    var s = Array.IndexOf(source.Bones, Key(Body.First(b => b.Role == role).Gh3));
                    var sc = Array.IndexOf(source.Bones, Key(Body.First(b => b.Role == next).Gh3));
                    worst = Math.Max(worst, Angle(tp[child] - tp[bone], sp[sc] - sp[s]));
                }
                Check(worst < 0.05f, $"scale {scale}, T-pose {tPose}, pose {trial}: the bones point as the GH3 bones ({worst:0.###}°)");
            }
        }
        // A skeleton with two spine bones takes the bottom and top GH3 spine bones.
        var two = Target(1, false, 4, role => role is not "chest");
        var spine = new CharacterRetarget(source.Bones, source.Parents, source.Bind, RigFixtures.Points(source.Bones), two, "singer");
        Check(spine.MappedSource(two.Humanoid["upperChest"]) == Array.IndexOf(source.Bones, Gh3Checksum.Of("bone_chest")) &&
              spine.MappedSource(two.Humanoid["spine"]) == Array.IndexOf(source.Bones, Gh3Checksum.Of("bone_stomach_lower")), "two spine bones map to the bottom and top GH3 spine bones");
    }

    private static void CheckTwistAndFollowers()
    {
        var source = Source();
        // A skeleton with a forearm twist bone between the forearm and the hand, and a follower at half of its turn.
        var basic = Target(1, false, 5);
        var names = basic.Bones.Concat(new[] { "t_twist_l", "t_twist_helper_l" }).ToList();
        var parents = basic.Parents.ToList();
        var bind = basic.Bind.ToList();
        var lower = basic.Humanoid["leftLowerArm"];
        var hand = basic.Humanoid["leftHand"];
        var world = WorldPositions(basic.Parents, basic.Bind);
        var lowerRotation = Quaternion.Identity;
        for (var bone = lower; bone >= 0; bone = basic.Parents[bone]) lowerRotation = basic.Bind[bone].Rotation * lowerRotation;
        var middle = (world[lower] + world[hand]) / 2;
        parents.Add(lower);
        bind.Add(new BandPose(Vector3.Transform(middle - world[lower], Quaternion.Inverse(lowerRotation)), Quaternion.Identity));
        parents.Add(lower);
        bind.Add(new BandPose(Vector3.Transform((world[lower] + middle) / 2 - world[lower], Quaternion.Inverse(lowerRotation)), Quaternion.Identity));
        var twist = names.Count - 2;
        parents[hand] = twist;
        bind[hand] = new BandPose(Vector3.Transform(world[hand] - middle, Quaternion.Inverse(lowerRotation)), basic.Bind[hand].Rotation);
        var humanoid = new Dictionary<string, int>(basic.Humanoid) { ["leftLowerArmTwist"] = twist };
        var target = new RetargetSkeleton
        {
            Bones = names.ToArray(), Parents = parents.ToArray(), Bind = bind.ToArray(), Humanoid = humanoid,
            Followers = new[] { (names.Count - 1, twist, 0.5f) },
        };
        var retarget = new CharacterRetarget(source.Bones, source.Parents, source.Bind, RigFixtures.Points(source.Bones), target, "singer");
        // Turn the GH3 hand 80° about the forearm.
        var pose = source.Bind.ToArray();
        var palm = Array.IndexOf(source.Bones, Gh3Checksum.Of("bone_palm_l"));
        var axis = Vector3.Normalize(pose[palm].Position);
        pose[palm] = pose[palm] with { Rotation = Quaternion.CreateFromAxisAngle(axis, 80 * MathF.PI / 180) };
        var result = new BandPose[target.Bones.Length];
        retarget.Apply(pose, result, 0, 0);
        float AngleOf(Quaternion q) => 2 * MathF.Acos(Math.Min(1, Math.Abs(Quaternion.Normalize(q).W))) * 180 / MathF.PI;
        var twistTurn = AngleOf(Quaternion.Inverse(target.Bind[twist].Rotation) * result[twist].Rotation);
        var handTurn = AngleOf(Quaternion.Inverse(target.Bind[hand].Rotation) * result[hand].Rotation);
        var helperTurn = AngleOf(Quaternion.Inverse(target.Bind[names.Count - 1].Rotation) * result[names.Count - 1].Rotation);
        Check(Math.Abs(twistTurn - 80) < 0.5f && handTurn < 0.5f, $"the twist bone takes the hand's turn about the forearm ({twistTurn:0.##}°, hand {handTurn:0.##}°)");
        Check(Math.Abs(helperTurn - 40) < 0.5f, $"a follower at weight 0.5 turns half ({helperTurn:0.##}°)");

        // Without a twist bone, the forearm takes half of the turn.
        var plain = Target(1, false, 5);
        var half = new CharacterRetarget(source.Bones, source.Parents, source.Bind, RigFixtures.Points(source.Bones), plain, "singer");
        var plainResult = new BandPose[plain.Bones.Length];
        half.Apply(pose, plainResult, 0, 0);
        var forearmTurn = AngleOf(Quaternion.Inverse(plain.Bind[plain.Humanoid["leftLowerArm"]].Rotation) * plainResult[plain.Humanoid["leftLowerArm"]].Rotation);
        Check(Math.Abs(forearmTurn - 40) < 0.5f, $"without a twist bone the forearm turns half ({forearmTurn:0.##}°)");
    }

    private static void CheckRootMotionAndIk()
    {
        var source = Source();
        var target = Target(0.8f, true, 6);
        var retarget = new CharacterRetarget(source.Bones, source.Parents, source.Bind, RigFixtures.Points(source.Bones), target, "singer");
        Check(Math.Abs(retarget.HipScale - 0.8f) < 1e-4, $"the hip scale is the hip height ratio ({retarget.HipScale:0.####})");
        var pose = source.Bind.ToArray();
        var pelvis = Array.IndexOf(source.Bones, Gh3Checksum.Of("bone_pelvis"));
        pose[pelvis] = pose[pelvis] with { Position = pose[pelvis].Position + new Vector3(0.1f, -0.2f, 0.05f) };
        var result = new BandPose[target.Bones.Length];
        retarget.Apply(pose, result, 1, 1);
        var tp = WorldPositions(target.Parents, result);
        var hips = target.Humanoid["hips"];
        var rest = WorldPositions(target.Parents, target.Bind);
        Check(Vector3.Distance(tp[hips] - rest[hips], new Vector3(0.08f, -0.16f, 0.04f)) < 1e-4, "the hips move as the pelvis, scaled by the hip height");
        // The feet go where the GH3 feet go, scaled (leg IK with the character's own lengths): here the knees bend.
        var sp = WorldPositions(source.Parents, source.Bind);
        var spPose = WorldPositions(source.Parents, pose);
        foreach (var foot in new[] { "leftFoot", "rightFoot" })
        {
            var gh3 = Array.IndexOf(source.Bones, Key(Body.First(b => b.Role == foot).Gh3));
            var goal = rest[target.Humanoid[foot]] + (spPose[gh3] - sp[gh3]) * 0.8f;
            Check(Vector3.Distance(tp[target.Humanoid[foot]], goal) < 1e-3, $"the {foot} follows the GH3 foot ({Vector3.Distance(tp[target.Humanoid[foot]], goal) * 1000:0.##} mm)");
        }
        // A planted GH3 foot (its ankle at rest while the pelvis goes down) keeps the character's foot planted.
        pose = source.Bind.ToArray();
        pose[pelvis] = pose[pelvis] with { Position = pose[pelvis].Position + new Vector3(0, -0.1f, 0) };
        foreach (var knee in new[] { "bone_knee_l", "bone_knee_r" })
        {
            var k = Array.IndexOf(source.Bones, Gh3Checksum.Of(knee));
            var thigh = source.Parents[k];
            // Bend the GH3 knees so that the GH3 ankles stay at rest (a squat).
            var a = sp[thigh] + new Vector3(0, -0.1f, 0);
            var ankle = sp[Array.IndexOf(source.Parents, k)];
            var upper = (sp[k] - sp[thigh]).Length();
            var lower = (ankle - sp[k]).Length();
            var d = (ankle - a).Length();
            var bendAngle = MathF.Acos(Math.Clamp((upper * upper + d * d - lower * lower) / (2 * upper * d), -1, 1));
            var thighTurn = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -bendAngle) * CharacterRetarget.FromTo(Vector3.Normalize(sp[k] - sp[thigh]), Vector3.Normalize(ankle - a));
            pose[thigh] = pose[thigh] with { Rotation = thighTurn };
            var kneeWorld = a + Vector3.Transform(sp[k] - sp[thigh], thighTurn);
            var shinNow = Vector3.Transform(ankle - sp[k], thighTurn);
            pose[k] = pose[k] with { Rotation = Quaternion.Inverse(thighTurn) * CharacterRetarget.FromTo(Vector3.Normalize(shinNow), Vector3.Normalize(ankle - kneeWorld)) * thighTurn };
        }
        spPose = WorldPositions(source.Parents, pose);
        retarget.Apply(pose, result, 1, 1);
        tp = WorldPositions(target.Parents, result);
        foreach (var foot in new[] { "leftFoot", "rightFoot" })
        {
            var gh3 = Array.IndexOf(source.Bones, Key(Body.First(b => b.Role == foot).Gh3));
            Check(Vector3.Distance(spPose[gh3], sp[gh3]) < 1e-3, "the GH3 squat keeps the GH3 ankles at rest");
            Check(Vector3.Distance(tp[target.Humanoid[foot]], rest[target.Humanoid[foot]]) < 1e-3,
                $"the {foot} stays planted in a squat ({Vector3.Distance(tp[target.Humanoid[foot]], rest[target.Humanoid[foot]]) * 1000:0.##} mm)");
        }
        Check(retarget.HandMiss < 0.03f, $"the hands reach their goals ({retarget.HandMiss * 1000:0.#} mm)");
    }

    // tests/fixtures/*.json (tests/test_skeleton.py): the Python retarget of the Blender preview gives the same poses as the
    // game for an MMD-like skeleton with twist bones, followers, a T-pose, and bone rolls (retarget-case.json), and for a
    // skeleton of another rig family: a long spine and neck with joints between the roles, hands without a middle finger,
    // two-joint thumbs, no shoulders and toes, an A-pose (retarget-any-rig.json).
    private static void CheckPythonFixture(string file)
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "tests", "fixtures", file))) directory = directory.Parent;
        Check(directory != null, $"the fixture tests/fixtures/{file} exists");
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "tests", "fixtures", file)))!;
        float[] F(System.Text.Json.Nodes.JsonNode? n) => n!.AsArray().Select(v => (float)v!.GetValue<double>()).ToArray();
        BandPose Pose(float[] r) => new(new Vector3(r[0], r[1], r[2]), Quaternion.Normalize(new Quaternion(r[3], r[4], r[5], r[6])));
        var sourceBones = root["sourceBones"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        var sourceParents = root["sourceParents"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray();
        var sourceBind = root["sourceBind"]!.AsArray().Select(n => Pose(F(n))).ToArray();
        var names = root["names"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        var target = new RetargetSkeleton
        {
            Bones = names, Parents = root["parents"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray(),
            Bind = root["bind"]!.AsArray().Select(n => Pose(F(n))).ToArray(),
            Humanoid = root["humanoid"]!.AsObject().ToDictionary(p => p.Key, p => Array.IndexOf(names, p.Value!.GetValue<string>())),
            Followers = root["followers"]!.AsArray().Select(f => (Array.IndexOf(names, f!["bone"]!.GetValue<string>()), Array.IndexOf(names, f["source"]!.GetValue<string>()),
                (float)f["weight"]!.GetValue<double>())).ToArray(),
        };
        var retarget = new CharacterRetarget(sourceBones, sourceParents, sourceBind, RigFixtures.Points(sourceBones), target, root["role"]!.GetValue<string>());
        var result = new BandPose[names.Length];
        float worstPosition = 0, worstAngle = 0;
        foreach (var frame in root["frames"]!.AsArray())
        {
            var positions = frame!["positions"]!.AsArray().Select(F).ToArray();
            var rotations = frame["rotations"]!.AsArray().Select(F).ToArray();
            var pose = positions.Select((p, i) => new BandPose(new Vector3(p[0], p[1], p[2]),
                Quaternion.Normalize(new Quaternion(rotations[i][0], rotations[i][1], rotations[i][2], rotations[i][3])))).ToArray();
            retarget.Apply(pose, result, (float)frame["right"]!.GetValue<double>(), (float)frame["left"]!.GetValue<double>());
            var expectedP = frame["targetPositions"]!.AsArray().Select(F).ToArray();
            var expectedQ = frame["targetRotations"]!.AsArray().Select(F).ToArray();
            for (var i = 0; i < names.Length; i++)
            {
                worstPosition = Math.Max(worstPosition, Vector3.Distance(result[i].Position, new Vector3(expectedP[i][0], expectedP[i][1], expectedP[i][2])));
                // Chord form: acos of a float dot product near 1 cannot resolve less than 0.04°.
                var a = result[i].Rotation;
                var b = new Quaternion(expectedQ[i][0], expectedQ[i][1], expectedQ[i][2], expectedQ[i][3]);
                if (Quaternion.Dot(a, b) < 0) b = Quaternion.Negate(b);
                var angle = (float)(4 * Math.Asin(Math.Min(1, (a - b).Length() / 2.0)) * 180 / Math.PI);
                worstAngle = Math.Max(worstAngle, angle);
            }
            // The moved and scaled gear of the GH3 rig (instrument, microphone stand, microphone, drum kit, sticks).
            var sourceOut = frame["sourceOut"]!.AsArray().Select(F).ToArray();
            for (var i = 0; i < sourceOut.Length; i++)
                worstPosition = Math.Max(worstPosition, Vector3.Distance(retarget.SourceOut[i].Position, new Vector3(sourceOut[i][0], sourceOut[i][1], sourceOut[i][2])));
        }
        Check(worstPosition < 1e-4f && worstAngle < 0.005f, $"the game and the Python preview agree on {file} ({worstPosition * 1000:0.###} mm, {worstAngle:0.###}°)");
    }

    private static RetargetSkeleton Gh3Skeleton((string[] Bones, int[] Parents, float[][] Bind) rig, float scale = 1) => new()
    {
        Bones = rig.Bones, Parents = rig.Parents, Bind = Poses(rig.Bind), Humanoid = HumanoidRoles.FromGh3(rig.Bones), Scale = scale, Gh3Face = true,
    };

    private static BandPose[] Poses(float[][] rows) =>
        rows.Select(r => new BandPose(new(r[0], r[1], r[2]), Quaternion.Normalize(new(r[3], r[4], r[5], r[6])))).ToArray();

    // The GH3 rigs of the band library (PACKAGING-PLAN decision 15): a GH3 rig maps to itself by its bone names, its face
    // bones follow the GH3 face bones by name, a profile scale scales the skeleton, and the drum kit and the microphone
    // stand fit the size of the body.
    private static void CheckGh3Rigs(string library)
    {
        var guitarist = CharacterImporter.Rig(library, "guitarist");
        var singer = CharacterImporter.Rig(library, "singer");
        var drummer = CharacterImporter.Rig(library, "drummer");
        var map = HumanoidRoles.FromGh3(guitarist.Bones);
        Check(map.Count == 52 && map["upperChest"] == Array.IndexOf(guitarist.Bones, Gh3Checksum.Of("bone_chest")) && !map.ContainsKey("jaw"),
            $"a GH3 rig maps 52 roles from its bone names, the face bones by name ({map.Count})");

        // The singer rig on the guitarist rig: the bind pose gives the guitarist's own rest pose, and the face bones turn as
        // the singer's face bones turn from their rest pose.
        var bind = Poses(singer.Bind);
        var retarget = new CharacterRetarget(singer.Bones, singer.Parents, bind, LibraryRigs.Load(library).Role("singer").Points, Gh3Skeleton(guitarist), "singer");
        var result = new BandPose[guitarist.Bones.Length];
        retarget.Apply(bind, result, 0, 0);
        var jaw = Array.IndexOf(guitarist.Bones, Gh3Checksum.Of("Bone_Jaw"));
        var rest = Poses(guitarist.Bind);
        Check(Quaternion.Dot(result[jaw].Rotation, rest[jaw].Rotation) > 0.99999f && Vector3.Distance(result[jaw].Position, rest[jaw].Position) < 1e-5f,
            "at rest the face bones keep their own rest pose");
        var sourceJaw = Array.IndexOf(singer.Bones, Gh3Checksum.Of("Bone_Jaw"));
        var open = (BandPose[])bind.Clone();
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.4f);
        open[sourceJaw] = open[sourceJaw] with { Rotation = Quaternion.Normalize(turn * bind[sourceJaw].Rotation) };
        retarget.Apply(open, result, 0, 0);
        var jawTurn = Quaternion.Normalize(result[jaw].Rotation * Quaternion.Inverse(rest[jaw].Rotation));
        Check(Math.Abs(Quaternion.Dot(jawTurn, turn)) > 0.99999f, "the jaw takes the turn of the GH3 jaw in its parent frame");
        Check(retarget.HasMicrophone && retarget.StandScale is > 0.9f and < 1.1f, $"the stand fits the head height ({retarget.StandScale:0.###})");

        // The drummer rig on itself at 0.8 of its size: the kit, the throne, and the sticks scale by the arm length
        // ratio, the character sits at the GH3 seat (no lift), and the hands and the stick tips reach the GH3 places on
        // the scaled kit.
        var drum = Poses(drummer.Bind);
        var small = new CharacterRetarget(drummer.Bones, drummer.Parents, drum, LibraryRigs.Load(library).Role("drummer").Points, Gh3Skeleton(drummer, 0.8f), "drummer");
        Check(small.HasKit && Math.Abs(small.InstrumentScale - 0.8f) < 1e-4f && Math.Abs(small.SeatLift) < 1e-4f,
            $"the kit scales with the arms ({small.InstrumentScale:0.####}), no seat lift ({small.SeatLift:0.####})");
        var output = new BandPose[drummer.Bones.Length];
        var hit = (BandPose[])drum.Clone();
        var bicep = Array.IndexOf(drummer.Bones, Gh3Checksum.Of("Bone_Bicep_L"));
        hit[bicep] = hit[bicep] with { Rotation = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f) * drum[bicep].Rotation) };
        small.Apply(hit, output, 1, 1);
        var kit = Array.IndexOf(drummer.Bones, Gh3Checksum.Of("Bone_Drum_Stick_L")) is var stick and >= 0 ? drummer.Parents[stick] : -1;
        Check(Vector3.Distance(small.SourceOut[kit].Position, drum[kit].Position * 0.8f) < 1e-5f, "the kit root moves to 0.8 of its place");
        Check(small.HandMiss < 1e-3f && small.StickTipMiss.Max() < 1e-3f, $"the hands and the stick tips reach the scaled kit ({small.HandMiss * 100:0.###} cm, " +
            $"{small.StickTipMiss.Max() * 100:0.###} cm)");
        var hips = Array.IndexOf(drummer.Bones, Gh3Checksum.Of("bone_pelvis"));
        Check(Vector3.Distance(small.World(hips).Position, small.SourceWorld(hips).Position * 0.8f) < 1e-4f, "the hips sit on the scaled seat");
        for (var bone = 0; bone < output.Length; bone++)
            if (drummer.Parents[bone] >= 0 && bone != hips && small.MappedSource(bone) < 0)
                Check(Vector3.Distance(output[bone].Position, drum[bone].Position) < 1e-5f, "the scaled skeleton keeps its bone lengths in its own units");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Retarget check failed: " + message);
    }
}
