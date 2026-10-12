using System.IO.Compression;
using System.Numerics;
using System.Text;
using Gh3Skin;
using Gh3Skin.Packages;

// Character library profiles (PACKAGE-FORMAT.md section 10.1): the rig and set loaders, the clip offsets, the hinge and
// prismatic hair joints, and the IK chains from data.
internal static class CharacterMotionLogicTests
{
    public static void Run()
    {
        CheckLibrary();
        CheckSingerClipRig();
        CheckClipOffsets();
        CheckHingeAndPrismatic();
        CheckIkFromData();
        CheckWalkDirection();
        Console.WriteLine("Character library profiles, singer profiles on their own clip rig, set clip offsets, hinge and prismatic hair joints, IK data, and walk direction checks passed.");
    }

    // GH3 walk_to_pos and face_audience (StageWalk): the walk direction, the cycle count, the foot, the root motion, and
    // the turns. Test clips: the root moves along -Z (forward) or +Z (backward) at a constant speed.
    private static void CheckWalkDirection()
    {
        var clips = new Dictionary<string, (float Duration, float Distance)>
        {
            ["00000001"] = (0.6f, 0.6f), ["00000002"] = (1, 1.4f), ["00000003"] = (1, 0.6f), ["00000004"] = (1, 1.2f),
            ["00000011"] = (0.6f, -0.6f), ["00000012"] = (1, -1.4f), ["00000013"] = (1, -0.6f), ["00000014"] = (1, -1.2f),
        };
        var groups = new Dictionary<string, string[]>();
        var parts = new[] { PerformanceMember.WalkStart, PerformanceMember.WalkCycle, PerformanceMember.WalkStopLeft, PerformanceMember.WalkStopRight };
        for (var i = 0; i < parts.Length; i++)
        {
            groups[PerformanceMember.WalkClip(false, parts[i])] = new[] { $"0000000{i + 1}" };
            groups[PerformanceMember.WalkClip(true, parts[i])] = new[] { $"0000001{i + 1}" };
        }
        var member = new PerformanceMember
        {
            Groups = groups, Walking = new() { ["min_turn"] = 45, ["turn_speed"] = 45, ["face_audience_speed"] = 90 },
            Clips = clips.ToDictionary(c => c.Key, c => new PerformanceClipInfo { Duration = c.Value.Duration, Frames = 31, RootDistance = Math.Abs(c.Value.Distance) }),
        };
        Vector3 Root(string clip, double time) => new(0, 0, -clips[clip].Distance * (float)(time / clips[clip].Duration));
        var data = new StageWalk.Data(member, new Dictionary<string, float> { ["min_walk_dist"] = 0.75f }, Root, 1);

        var back = StageWalk.Plan(data, 10, new Vector2(0, 0), 0, new Vector2(0, 2.6f));
        var (end, yaw) = back.Root(back.TurnEnd);
        Check(back.Backward && back.Cycles == 1 && !back.RightFoot, "a target behind is walked to backward with whole cycles and the left foot stop");
        Check(back.Branches.Select(b => b.Clip).SequenceEqual(new[] { "00000011", "00000012", "00000013" }) && Math.Abs(back.Branches[1].Time - 10.6) < 1e-6 &&
            Math.Abs(back.End - 12.6) < 1e-6, "start, cycle, and stop clips follow each other");
        Check(Vector2.Distance(end, new Vector2(0, 2.6f)) < 0.05f && Math.Abs(yaw) < 0.01f, "the root motion (with the clip blends) reaches the target and the performer still faces the audience");
        var (middle, _) = back.Root(11);
        Check(middle.Y > 0.3f && middle.Y < 1.2f && back.Active(11)?.Branch.Clip == "00000012", "the root moves during the walk");

        var side = StageWalk.Plan(data, 0, new Vector2(0, 0), 0, new Vector2(3, 0));
        var (_, turned) = side.Root(1);
        Check(!side.Backward && Math.Abs(turned + 45) < 0.5f, "a target to the side: the performer turns at turn_speed and walks forward");
        var (sideEnd, sideYaw) = side.Root(side.TurnEnd);
        Check(sideEnd.X > 2.5f && Math.Abs(sideYaw - StageWalk.Yaw(new Vector2(0, -50) - sideEnd)) < 0.01f, "after the stop the performer turns to the audience");

        var half = StageWalk.Plan(data, 0, new Vector2(0, 0), 0, new Vector2(0, -2));
        Check(!half.Backward && half.Cycles == 0.5 && half.RightFoot && half.Branches[^1].Clip == "00000004", "half a cycle stops on the right foot");

        var near = StageWalk.Plan(data, 0, new Vector2(0, 0), 90, new Vector2(0.5f, 0));
        Check(!near.Walks && Math.Abs(near.Root(near.TurnEnd).Yaw) < 0.01f && Math.Abs(near.TurnEnd - 2) < 0.02, "a target nearer than min_walk_dist: no walk, only the turn to the audience");
    }

    private static void CheckLibrary()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gh3-character-library-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "characters.json"), """
            {"version": 1, "library": "gh3-characters-v1",
             "clipRig": {"profile": "gh3-guitarist-axel-v1", "bones": ["0000000a", "0000000b"], "parents": [-1, 0], "bind": [[0, 1, 0, 0, 0, 0, 1], [0, 0.5, 0.25, 0, 0, 0, 1]]},
             "profiles": {"gh3-guitarist-test-v1": {"role": "guitarist", "name": "Test", "outfit": 1, "skeleton": "12345678", "scale": 0.9, "animations": "test",
               "hair": null, "ik": "hero", "bones": ["0000000a", "0000000b"], "parents": [-1, 0], "bind": [[0, 0.9, 0, 0, 0, 0, 1], [0.1, 0.5, 0.25, 0, 0, 0, 1]]}},
             "animations": {"test": {"animSet": "11111111", "groups": {"stance_a/idle/normal/med": ["c1c1c1c1"], "walk/backward/cycle_anim/med": ["c1c1c1c1"]}, "layers": {},
               "clips": {"c1c1c1c1": {"duration": 1.0, "frames": 31, "rootDistance": 0.0}}, "walking": {"min_turn": 45}}},
             "hair": {}, "ik": {"hero": [{"bones": ["0000000a", "0000000b", "0000000b"], "target": "0000000a", "hinge": [0, 0, -1], "cosMax": -0.97, "cosMin": 0.97}]}}
            """);
            var library = CharacterLibrary.Load(folder);
            var profile = library.Profiles["gh3-guitarist-test-v1"];
            Check(profile.Role == "guitarist" && Math.Abs(profile.Scale - 0.9f) < 1e-6 && profile.Hair == "" && profile.Ik == "hero", "the profile fields are read");
            var model = CharacterModelFor(folder, "gh3-guitarist-test-v1");
            var motion = CharacterMotion.Create(model);
            Check(motion.Member.Bones.SequenceEqual(profile.Bones) && motion.Member.Groups.ContainsKey("stance_a/idle/normal/med"), "the set gets the bones of the profile");
            Check(motion.Member.Walking.TryGetValue("min_turn", out var minTurn) && minTurn == 45, "the set gets its walking scalars");
            Check(motion.Offsets != null && Vector3.Distance(motion.Offsets[0], new Vector3(0, -0.1f, 0)) < 1e-6 && Vector3.Distance(motion.Offsets[1], new Vector3(0.1f, 0, 0)) < 1e-6,
                "clip offsets are the profile rest positions minus the clip rig rest positions");
            Check(motion.Hair == null && motion.Ik?.Length == 1, "a profile without hair has no hair physics; the IK chains come from the library");
            var broken = Path.Combine(folder, "broken");
            Directory.CreateDirectory(broken);
            File.WriteAllText(Path.Combine(broken, "characters.json"), File.ReadAllText(Path.Combine(folder, "characters.json")).Replace("\"animations\": \"test\"", "\"animations\": \"none\""));
            try { CharacterLibrary.Load(broken); Check(false, "a profile with an unknown animation set is rejected"); }
            catch (PackageException) { }
        }
        finally { Directory.Delete(folder, true); }
    }

    // A singer profile whose set is on another clip rig (clipRigs): the offsets use the rig of the set, the profile plays
    // only the singer role, and a set on another rig than the profile is rejected.
    private static void CheckSingerClipRig()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gh3-character-library-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var json = """
            {"version": 1, "library": "gh3-characters-v1",
             "clipRig": {"profile": "gh3-guitarist-axel-v1", "bones": ["0000000a", "0000000c"], "parents": [-1, 0], "bind": [[0, 1, 0, 0, 0, 0, 1], [0, 0.5, 0.25, 0, 0, 0, 1]]},
             "clipRigs": {"singer": {"profile": "gh3-singer-male-v1", "bones": ["0000000a", "0000000b"], "parents": [-1, 0], "bind": [[0, 1, 0, 0, 0, 0, 1], [0, 0.31, 0, 0, 0, 0, 1]]}},
             "profiles": {"gh3-singer-test-v1": {"role": "singer", "name": "Test", "outfit": 1, "skeleton": "12345678", "scale": 1, "animations": "singer-test",
               "hair": null, "ik": "singer", "bones": ["0000000a", "0000000b"], "parents": [-1, 0], "bind": [[0, 1, 0, 0, 0, 0, 1], [0, 0.21, 0, 0, 0, 0, 1]]}},
             "animations": {"singer-test": {"animSet": "11111111", "clipRig": "singer", "groups": {"stance_a/idle/normal/med": ["c1c1c1c1"]}, "layers": {},
               "clips": {"c1c1c1c1": {"duration": 1.0, "frames": 31, "rootDistance": 0.0}}}},
             "hair": {}, "ik": {"singer": [{"bones": ["0000000a", "0000000b", "0000000b"], "target": "0000000a", "hinge": [0, 0, 1], "cosMax": -0.99, "cosMin": 0.97}]}}
            """;
            File.WriteAllText(Path.Combine(folder, "characters.json"), json);
            var library = CharacterLibrary.Load(folder);
            Check(library.SetClipRigs["singer-test"].Bones[1] == "0000000b" && library.Profiles["gh3-singer-test-v1"].Role == "singer", "a set names its clip rig");
            var motion = CharacterMotion.Create(CharacterModelFor(folder, "gh3-singer-test-v1"));
            Check(motion.Offsets != null && Vector3.Distance(motion.Offsets[1], new Vector3(0, -0.1f, 0)) < 1e-6, "the offsets use the clip rig of the set");
            Check(CharacterLibrary.UsesOwnSet("singer", "singer") && !CharacterLibrary.UsesOwnSet("singer", "guitarist") && CharacterLibrary.UsesOwnSet("guitarist", "bassist") &&
                !CharacterLibrary.UsesOwnSet("guitarist", "singer"), "a singer profile plays its own set as the singer; a guitarist profile as the guitarist and the bassist");
            var broken = Path.Combine(folder, "broken");
            Directory.CreateDirectory(broken);
            File.WriteAllText(Path.Combine(broken, "characters.json"), json.Replace("\"clipRig\": \"singer\", ", ""));
            try { CharacterLibrary.Load(broken); Check(false, "a profile on another rig than its set is rejected"); }
            catch (PackageException) { }
            File.WriteAllText(Path.Combine(broken, "characters.json"), json.Replace("\"clipRig\": \"singer\"", "\"clipRig\": \"none\""));
            try { CharacterLibrary.Load(broken); Check(false, "a set with an unknown clip rig is rejected"); }
            catch (PackageException) { }
        }
        finally { Directory.Delete(folder, true); }
    }

    private static CharacterModel CharacterModelFor(string libraryFolder, string profile)
    {
        var folder = Path.Combine(libraryFolder, "character");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "character.json"),
            $"{{\"version\": 1, \"id\": \"gh3.test\", \"name\": \"Test\", \"profile\": \"{profile}\", \"role\": \"guitarist\", \"instrument\": \"\", " +
            "\"joints\": [\"0000000a\", \"0000000b\"], \"library\": \"gh3-characters-v1\", \"animations\": \"\"}");
        using (var writer = new BinaryWriter(File.Create(Path.Combine(folder, "bindposes.bin"))))
        {
            writer.Write(Encoding.ASCII.GetBytes("GH3BPS01"));
            writer.Write(2);
            for (var j = 0; j < 2; j++) for (var k = 0; k < 16; k++) writer.Write(k % 5 == 0 ? 1f : 0f);
        }
        var model = CharacterModel.Load(folder);
        Check(model.Library == "gh3-characters-v1" && model.Profile == profile, "the cache records the library and the profile");
        model.LibraryFolder = libraryFolder;
        return model;
    }

    // GH3CLP01: bone 0 with positions and rotations, bone 1 with rotations only.
    private static PerformanceClip Clip()
    {
        using var data = new MemoryStream();
        using (var zip = new GZipStream(data, CompressionLevel.Fastest, true))
        using (var writer = new BinaryWriter(zip))
        {
            writer.Write(Encoding.ASCII.GetBytes("GH3CLP01"));
            writer.Write(2); writer.Write(2); writer.Write(2); writer.Write(1f);
            writer.Write(0); writer.Write(3); writer.Write(1); writer.Write(2);
            foreach (var x in new[] { 0f, 1f })
            {
                foreach (var v in new[] { x, 1f, 0f, 0f, 0f, 0f, 1f }) writer.Write(v);
                foreach (var v in new[] { 9f, 9f, 9f, 0f, 0f, 0f, 1f }) writer.Write(v);
            }
        }
        data.Position = 0;
        return new PerformanceClip(data, 2);
    }

    private static void CheckClipOffsets()
    {
        var bind = new[] { new BandPose(new(0, 1, 0), Quaternion.Identity), new BandPose(new(0, 0.5f, 0), Quaternion.Identity) };
        var pose = new BandPose[2];
        Array.Copy(bind, pose, 2);
        Clip().Move(new[] { new Vector3(0, -0.1f, 0.2f), new Vector3(5, 5, 5) }).Apply(pose, bind, 0.5);
        Check(Vector3.Distance(pose[0].Position, new Vector3(0.5f, 0.9f, 0.2f)) < 1e-5f, "the offset moves a position track");
        Check(pose[1].Position == bind[1].Position, "a bone without a position track keeps its rest position");
    }

    private static void CheckHingeAndPrismatic()
    {
        var down = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2);
        HairJoint Joint(int bone, string type, float[] axis, float[] range) => new()
        {
            Bone = bone, Parent = 0, Position = new float[] { 0.2f, 0, 0 }, Rotation = new[] { down.X, down.Y, down.Z, down.W }, Length = 0.2f, Mass = 5,
            Friction = 0.3f, AngularDamping = 0.05f, Type = type, Axis = axis, Range = range,
        };
        // The chains hang from bone 0, which is not the head (bone 2): a character library chain on the chest.
        var definition = new HairDefinition
        {
            Head = 2, Capsule = new() { A = new float[] { 0, 0, 0 }, B = new float[] { 0, 0.1f, 0 }, Radius = 0.01f },
            Joints = new[] { Joint(1, "hinge", new float[] { 0, 0, 1 }, new[] { -0.3f, 0.3f }), Joint(3, "prismatic", new float[] { 1, 0, 0 }, new[] { 0f, 0.05f }) },
        };
        BandPose Sample(double t, BandPose[] pose)
        {
            pose[0] = new(new((float)Math.Sin(t * 7) * 0.3f, 2 + (float)Math.Sin(t * 5) * 0.2f, (float)Math.Cos(t * 3) * 0.3f),
                Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1, 1, 0)), (float)Math.Sin(t * 4) * 0.6f));
            pose[1] = new(new(0.2f, 0, 0), down);
            pose[2] = new(Vector3.Zero, Quaternion.Identity);
            pose[3] = new(new(0.2f, 0, 0), down);
            return new(Vector3.Zero, Quaternion.Identity);
        }
        var hair = new StageHair(definition, new[] { -1, 0, 0, 0 }, Sample);
        var pose = new BandPose[4];
        var moved = 0f;
        var turned = 0f;
        for (var i = 0; i < 400; i++)
        {
            Sample(i / 120.0, pose);
            hair.Sample(i / 120.0); hair.Apply(pose);
            // The hinge turns only about the bind Z axis of the joint, within the range.
            var turn = Quaternion.Inverse(down) * pose[1].Rotation;
            var axis = Vector3.Normalize(new Vector3(turn.X, turn.Y, turn.Z) + new Vector3(0, 0, 1e-9f));
            var angle = 2 * MathF.Acos(Math.Clamp(Math.Abs(turn.W), 0, 1));
            Check(angle < 1e-3f || Math.Abs(Math.Abs(axis.Z) - 1) < 1e-3f, "a hinge joint turns about its axis only");
            Check(angle <= 0.3f + 1e-3f, "a hinge joint stays in its range");
            turned = Math.Max(turned, angle);
            Check(Math.Abs(Math.Abs(Quaternion.Dot(pose[3].Rotation, down)) - 1) < 1e-5f, "a prismatic joint does not turn");
            var slide = Vector3.Transform(pose[3].Position - new Vector3(0.2f, 0, 0), Quaternion.Inverse(down));
            Check(slide.X >= -1e-4f && slide.X <= 0.05f + 1e-4f && Math.Abs(slide.Y) < 1e-4f && Math.Abs(slide.Z) < 1e-4f, "a prismatic joint slides along its axis within its range");
            moved = Math.Max(moved, slide.X);
        }
        Check(moved > 0.01f, "a prismatic joint moves when its parent moves");
        Check(turned > 0.05f, "a hinge joint swings when its parent moves");
    }

    private static void CheckIkFromData()
    {
        string K(string name) => Gh3Checksum.Of(name);
        var names = new[] { "Bone_Root", "Bone_Bicep_R", "Bone_Forearm_R", "Bone_Palm_R", "Bone_IK_Hand_Guitar_R" }.Select(K).ToArray();
        var parents = new[] { -1, 0, 1, 2, 0 };
        var bind = new[]
        {
            new BandPose(Vector3.Zero, Quaternion.Identity), new BandPose(new(0.2f, 1.4f, 0), Quaternion.Identity),
            new BandPose(new(0.3f, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -0.3f)), new BandPose(new(0.25f, 0, 0), Quaternion.Identity),
            new BandPose(new(0.5f, 1.1f, 0.2f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f)),
        };
        var hero = new[] { new IkChainDefinition { Bones = new[] { K("Bone_Bicep_R"), K("Bone_Forearm_R"), K("Bone_Palm_R") }, Target = K("Bone_IK_Hand_Guitar_R"),
            Hinge = new[] { 0f, 0, -1 }, CosMax = -0.97f, CosMin = 0.97f, Hand = "right" } };
        var a = bind.ToArray();
        var b = bind.ToArray();
        var rig = new StageGuitarRig(parents, bind, names, hero, Array.Empty<RigConstraint>());
        rig.Apply(a, 1, 1);
        rig.Apply(b, 0, 1);
        Check(!a.SequenceEqual(bind), "the IK chain of the data moves the arm");
        Check(b.SequenceEqual(bind), "an arm chain follows the strength of its hand (right: the strum arm)");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
    }
}
