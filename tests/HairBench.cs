using System.Numerics;
using Gh3Skin;

// Hair motion bench for the comparison with the original GH3 (VERIFICATION.md, "Hair motion").
// GH3_HAIR_BENCH=<profile> GH3_HAIR_BENCH_OUT=<file> GH3_HAIR_BENCH_FPS=<render rate> runs the hair of a character
// library profile and writes, for each rendered frame, the time and the world pose (x, y, z, qx, qy, qz, qw) of the head
// and of every hair joint as float32 values.
// With GH3_HAIR_BENCH_HEAD=<file> (rows of float32 t, x, y, z, qx, qy, qz, qw: the head bone in runtime coordinates,
// for example from a GH3 memory recording) the head follows that path and all other bones keep the bind pose. With
// GH3_HAIR_BENCH_BODIES=<file> (tools/research/hair-motion/body_path.py) all recorded body bones follow their paths.
// GH3_HAIR_BENCH_BODY=<file> (body_colliders.py) adds the GH3 ragdoll body capsules as colliders (experiment).
// Without it, the profile plays My Curse as the guitarist (GH3_CHARACTER_HAIR_ASSETS=<assets>), as in the game.
// GH3_HAIR_BENCH=band:<role> runs a band library member (band/hair.json) through My Curse the same way.
// GH3_HAIR_BENCH=character:<.chchar> imports a character package on a character library profile and runs its profile
// hair and custom chains (character.json chains) through My Curse; it writes the chain joints and prints the speed, the
// depth of the joints in the colliders, and the solver time.
internal static class HairBench
{
    public static void Run(string id)
    {
        var assets = Environment.GetEnvironmentVariable("GH3_CHARACTER_HAIR_ASSETS") ?? "assets";
        var output = Environment.GetEnvironmentVariable("GH3_HAIR_BENCH_OUT") ?? throw new InvalidOperationException("GH3_HAIR_BENCH_OUT is not set");
        var fps = double.Parse(Environment.GetEnvironmentVariable("GH3_HAIR_BENCH_FPS") ?? "60", System.Globalization.CultureInfo.InvariantCulture);
        var seconds = double.Parse(Environment.GetEnvironmentVariable("GH3_HAIR_BENCH_SECONDS") ?? "240", System.Globalization.CultureInfo.InvariantCulture);
        if (id.StartsWith("band:")) { Band(assets, id[5..], output, fps, seconds); return; }
        if (id.StartsWith("character:")) { Character(assets, id[10..], output, fps, seconds); return; }
        var folder = Path.Combine(assets, "libraries/gh3-characters-v1");
        var library = Gh3Skin.Packages.CharacterLibrary.Load(folder);
        var bank = CharacterMotionBank.Load(folder);
        var profile = library.Profiles[id];
        var definition = bank.Hair[profile.Hair];
        if (Environment.GetEnvironmentVariable("GH3_HAIR_BENCH_BODY") is { } bodyFile) definition = WithBody(definition, bodyFile);
        var animation = BandAnimation.FromRig(profile.Parents, profile.Bind);
        var parents = animation.Parents;
        StageHair.PoseSampler sample;
        BandPose[] Animated() => (BandPose[])animation.BindPose.Clone();
        var animated = Animated();
        if (Environment.GetEnvironmentVariable("GH3_HAIR_BENCH_BODIES") is { } bodiesFile)
        {
            // All recorded body bones follow their recorded world paths (tools/research/hair-motion/body_path.py); the
            // other bones keep the bind pose relative to their parents.
            var raw = File.ReadAllBytes(bodiesFile);
            var count = BitConverter.ToInt32(raw, 0);
            var recorded = Enumerable.Range(0, count).Select(i => BitConverter.ToInt32(raw, 4 + 4 * i)).ToArray();
            var rows = new float[(raw.Length - 4 - 4 * count) / 4];
            Buffer.BlockCopy(raw, 4 + 4 * count, rows, 0, rows.Length * 4);
            var stride = 1 + 7 * count;
            var frames = rows.Length / stride;
            seconds = Math.Min(seconds, rows[(frames - 1) * stride] - rows[0]);
            var start = rows[0];
            var slot = Enumerable.Repeat(-1, parents.Length).ToArray();
            for (var k = 0; k < count; k++) slot[recorded[k]] = k;
            var world = new BandPose[parents.Length];
            sample = (time, pose) =>
            {
                var t = (float)(start + time);
                var at = 0;
                while (at < frames - 2 && rows[(at + 1) * stride] <= t) at++;
                var a = at * stride; var b = (at + 1) * stride;
                var f = Math.Clamp((t - rows[a]) / Math.Max(rows[b] - rows[a], 1e-6f), 0, 1);
                for (var i = 0; i < parents.Length; i++)
                {
                    var parent = parents[i] < 0 ? new BandPose(Vector3.Zero, Quaternion.Identity) : world[parents[i]];
                    if (slot[i] < 0)
                    {
                        pose[i] = animation.BindPose[i];
                        world[i] = new(parent.Position + Vector3.Transform(pose[i].Position, parent.Rotation), Quaternion.Normalize(parent.Rotation * pose[i].Rotation));
                        continue;
                    }
                    int o = 1 + 7 * slot[i], oa = a + o, ob = b + o;
                    world[i] = BandPose.Mix(new(new(rows[oa], rows[oa + 1], rows[oa + 2]), new(rows[oa + 3], rows[oa + 4], rows[oa + 5], rows[oa + 6])),
                        new(new(rows[ob], rows[ob + 1], rows[ob + 2]), new(rows[ob + 3], rows[ob + 4], rows[ob + 5], rows[ob + 6])), f);
                    var inverse = Quaternion.Inverse(parent.Rotation);
                    pose[i] = new(Vector3.Transform(world[i].Position - parent.Position, inverse), Quaternion.Normalize(inverse * world[i].Rotation));
                }
                return new(Vector3.Zero, Quaternion.Identity);
            };
        }
        else if (Environment.GetEnvironmentVariable("GH3_HAIR_BENCH_HEAD") is { } headFile)
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
                Offsets = CharacterMotion.RestOffsets(profile.Bones, profile.Bind, library.SetClipRigs[profile.Animations].Bones, library.SetClipRigs[profile.Animations].Bind),
            };
            var player = new PerformancePlayer(profile.Role, "", motion.Member, animation.BindPose, timeline, parents, performance.Constants, motion);
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

    // Body collision experiment: the GH3 ragdoll body capsules (tools/research/hair-motion/body_colliders.py) as colliders
    // of every hair joint, except the body of its constraint parent.
    // GH3_HAIR_BENCH_MOTION: rows of float32 t, dx, dy, dz, qx, qy, qz, qw, the move and the world turn of the head from
    // its bind pose (tools/research/hair-motion/naturalness.py writes the test motions).
    private sealed class HeadMotion
    {
        private readonly float[] _rows;
        private readonly int _count;
        public HeadMotion(float[] rows) { _rows = rows; _count = rows.Length / 8; }
        public double Duration => _rows[(_count - 1) * 8] - _rows[0];

        public (Vector3 Offset, Quaternion Turn) At(double time)
        {
            var t = (float)(_rows[0] + time);
            var at = 0;
            while (at < _count - 2 && _rows[(at + 1) * 8] <= t) at++;
            int a = at * 8, b = (at + 1) * 8;
            var f = Math.Clamp((t - _rows[a]) / Math.Max(_rows[b] - _rows[a], 1e-6f), 0, 1);
            var offset = Vector3.Lerp(new(_rows[a + 1], _rows[a + 2], _rows[a + 3]), new(_rows[b + 1], _rows[b + 2], _rows[b + 3]), f);
            var turn = Quaternion.Slerp(new(_rows[a + 4], _rows[a + 5], _rows[a + 6], _rows[a + 7]), new(_rows[b + 4], _rows[b + 5], _rows[b + 6], _rows[b + 7]), f);
            return (offset, Quaternion.Normalize(turn));
        }
    }

    private static HeadMotion Motion(string file)
    {
        var raw = File.ReadAllBytes(file);
        var rows = new float[raw.Length / 4];
        Buffer.BlockCopy(raw, 0, rows, 0, rows.Length * 4);
        return new HeadMotion(rows);
    }

    // The custom joint at `bone` or nearest above it in the rig, or -1.
    private static int Above(HairJoint[] joints, int[] parents, int bone)
    {
        for (; bone >= 0; bone = parents[bone])
            if (Array.FindIndex(joints, j => j.Bone == bone) is var k and >= 0) return k;
        return -1;
    }

    private static HairDefinition WithBody(HairDefinition definition, string file)
    {
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
        var root = json.RootElement;
        float[] Floats(System.Text.Json.JsonElement e) => e.EnumerateArray().Select(v => v.GetSingle()).ToArray();
        var colliders = root.GetProperty("colliders").EnumerateArray().Select(c => new HairCollider
        {
            Bone = c.GetProperty("bone").GetInt32(), A = Floats(c.GetProperty("a")), B = Floats(c.GetProperty("b")), Radius = c.GetProperty("radius").GetSingle(),
        }).ToArray();
        var radius = root.GetProperty("radius");
        var joints = definition.Joints.Select(j =>
        {
            long mask = 0;
            for (var c = 0; c < colliders.Length; c++) if (colliders[c].Bone != j.Parent) mask |= 1L << c;
            return new HairJoint
            {
                Bone = j.Bone, Parent = j.Parent, Position = j.Position, Rotation = j.Rotation, Length = j.Length, Mass = j.Mass, Friction = j.Friction,
                AngularDamping = j.AngularDamping, LinearDamping = j.LinearDamping, Limits = j.Limits, Type = j.Type, Axis = j.Axis, Range = j.Range,
                TwistAxis = j.TwistAxis, ConeAxis = j.ConeAxis, PlaneAxis = j.PlaneAxis, MaxAngularVelocity = j.MaxAngularVelocity, ColliderMask = mask,
                Radius = radius.TryGetProperty(j.Bone.ToString(), out var r) ? r.GetSingle() : 0,
            };
        }).ToArray();
        return new HairDefinition { Head = definition.Head, Capsule = definition.Capsule, Joints = joints, Colliders = colliders };
    }

    private static void Character(string assets, string package, string output, double fps, double seconds)
    {
        var folder = Path.Combine(assets, "libraries/gh3-characters-v1");
        var cache = Path.Combine(Path.GetTempPath(), "gh3-hair-bench-" + Environment.ProcessId);
        Gh3Skin.Packages.SemanticVersion.TryParse("1.0.0", out var version);
        var source = Gh3Skin.Packages.PackageSource.At(package);
        var manifest = Gh3Skin.Packages.PackageManifest.Parse(source.ReadAll("manifest.json"), Gh3Skin.Packages.PackageKind.Character, version);
        using (var verified = new Gh3Skin.Packages.VerifiedPackage(source, manifest))
            new Gh3Skin.Packages.CharacterImporter(verified, new Gh3Skin.Packages.TextureJobs(false, 1), Path.Combine(assets, "libraries/gh3-band-v1"),
                new Dictionary<string, string> { ["gh3-characters-v1"] = folder }).Import(cache);
        var model = Gh3Skin.Packages.CharacterModel.Load(cache);
        var library = Gh3Skin.Packages.CharacterLibrary.Load(folder);
        var bank = CharacterMotionBank.Load(folder);
        var profile = library.Profiles[model.Profile];
        var animation = BandAnimation.FromRig(profile.Parents, profile.Bind);
        var skeleton = model.Skeleton;
        int[] parents, map;
        BandPose[] extraBind;
        HairJoint[] joints;
        HairCollider[] colliders;
        HairDefinition definition;
        CharacterRetarget? retarget = null;
        if (skeleton != null)
        {
            // A character on its own skeleton: the profile clips play on the profile rig and the retarget moves the
            // skeleton (StageBand.LoadRetargeted); the chains have no profile hair.
            var identity = Enumerable.Range(0, skeleton.Names.Length).ToArray();
            (_, extraBind, joints, colliders) = CharacterChainRig.Plan(model, identity, identity);
            extraBind = Array.Empty<BandPose>();
            map = identity;
            retarget = new CharacterRetarget(profile.Bones, animation.Parents, animation.BindPose, new RetargetSkeleton
            {
                Bones = skeleton.Names, Parents = skeleton.Parents,
                Bind = skeleton.Bind.Select(b => new BandPose(new(b[0], b[1], b[2]), Quaternion.Normalize(new(b[3], b[4], b[5], b[6])))).ToArray(),
                Humanoid = skeleton.Humanoid, Followers = skeleton.Followers, InstrumentBone = skeleton.InstrumentBone,
                InstrumentOffset = new(skeleton.InstrumentOffset[0], skeleton.InstrumentOffset[1], skeleton.InstrumentOffset[2]), InstrumentScale = skeleton.InstrumentScale,
            }, "guitarist");
            parents = skeleton.Parents;
            definition = HairDefinition.Merge(null, joints, colliders)!;
        }
        else
        {
            var (mapped, bind) = CharacterChainRig.MapJoints(model, profile.Bones.Length,
                names => Gh3Skin.Packages.CharacterRig.Map(names, profile.Bones, profile.Parents, profile.Bones, new HashSet<int>(), ownRole: true))!.Value;
            map = mapped;
            int[] extraParents;
            (extraParents, extraBind, joints, colliders) = CharacterChainRig.Plan(model, map, bind);
            parents = animation.Parents.Concat(extraParents).ToArray();
            definition = HairDefinition.Merge(profile.Hair.Length > 0 ? bank.Hair[profile.Hair] : null, joints, colliders)!;
        }
        var performance = StagePerformance.Load(Path.Combine(assets, "libraries/gh3-band-v1/direction/performance.json"))!;
        var timeline = StageChartReader.Load("", "", Path.Combine(assets, "stages/events"), "My Curse", "Killswitch Engage");
        var set = bank.Animations[profile.Animations];
        var motion = new CharacterMotion
        {
            Profile = profile, SetName = profile.Animations, Hair = definition,
            Member = new PerformanceMember { Bones = profile.Bones, Groups = set.Groups, Layers = set.Layers, Clips = set.Clips },
            ClipDirectory = Path.Combine(folder, "clips"),
            Offsets = CharacterMotion.RestOffsets(profile.Bones, profile.Bind, library.SetClipRigs[profile.Animations].Bones, library.SetClipRigs[profile.Animations].Bind),
        };
        var player = new PerformancePlayer(profile.Role, "", motion.Member, animation.BindPose, timeline, animation.Parents, performance.Constants, motion);
        player.Prepare();
        var profileHead = Array.IndexOf(profile.Bones, Gh3Skin.Packages.CharacterRig.Head);
        var headBone = skeleton != null ? skeleton.Humanoid["head"] : profileHead;
        var path = Environment.GetEnvironmentVariable("GH3_HAIR_BENCH_MOTION") is { } motionFile ? Motion(motionFile) : null;
        if (path != null) seconds = Math.Min(seconds, path.Duration);
        var sourcePose = new BandPose[animation.Parents.Length];
        BandPose Sample(double time, BandPose[] pose)
        {
            if (path != null)
            {
                // The head moves and turns about its bind position; the other bones keep the bind pose.
                Array.Copy(animation.BindPose, sourcePose, sourcePose.Length);
                var bindHead = World(animation.Parents, sourcePose, profileHead);
                var parentPose = World(animation.Parents, sourcePose, animation.Parents[profileHead]);
                var (offset, turn) = path.At(time);
                var target = new BandPose(bindHead.Position + offset, Quaternion.Normalize(turn * bindHead.Rotation));
                var inverse = Quaternion.Inverse(parentPose.Rotation);
                sourcePose[profileHead] = new(Vector3.Transform(target.Position - parentPose.Position, inverse), Quaternion.Normalize(inverse * target.Rotation));
            }
            else
            {
                player.Sample(time);
                Array.Copy(player.Pose, sourcePose, sourcePose.Length);
            }
            if (retarget != null)
            {
                var (right, left) = path != null ? (0f, 0f) : player.ArmGain;
                retarget.Apply(sourcePose, pose, right, left);
            }
            else
            {
                Array.Copy(sourcePose, pose, sourcePose.Length);
                Array.Copy(extraBind, 0, pose, sourcePose.Length, extraBind.Length);
            }
            return new(Vector3.Zero, Quaternion.Identity);
        }
        var hair = new StageHair(definition, parents, Sample);
        var frame = new BandPose[parents.Length];
        var world = new BandPose[parents.Length];
        var rest = new BandPose[parents.Length];
        var custom = definition.Joints.Skip(definition.Joints.Length - joints.Length).ToArray();
        var names = model.ChainJoints.Select(c => c.Bone).ToArray();
        File.WriteAllText(output + ".json", System.Text.Json.JsonSerializer.Serialize(new
        {
            joints = custom.Select((j, k) => new { name = names[k], parent = Above(custom, parents, j.Parent), length = j.Length }).ToArray(),
            row = 1 + 7 + 12 * custom.Length,
        }));
        var deepest = new float[custom.Length];
        var deepestCollider = new int[custom.Length];
        var contacts = new int[custom.Length];
        var depths = new List<float>();
        var clock = new System.Diagnostics.Stopwatch();
        var finite = true;
        using var writer = new BinaryWriter(File.Create(output));
        for (var i = 0; i / fps <= seconds; i++)
        {
            var time = i / fps;
            clock.Start();
            hair.Sample(time);
            clock.Stop();
            Sample(time, frame);
            hair.Apply(frame);
            for (var b = 0; b < frame.Length; b++)
            {
                var parent = parents[b] < 0 ? new BandPose(Vector3.Zero, Quaternion.Identity) : world[parents[b]];
                world[b] = new(parent.Position + Vector3.Transform(frame[b].Position, parent.Rotation), Quaternion.Normalize(parent.Rotation * frame[b].Rotation));
            }
            // The pose without physics: the chains in their bind shape on the animated rig.
            Sample(time, frame);
            for (var b = 0; b < frame.Length; b++)
            {
                var parent = parents[b] < 0 ? new BandPose(Vector3.Zero, Quaternion.Identity) : rest[parents[b]];
                rest[b] = new(parent.Position + Vector3.Transform(frame[b].Position, parent.Rotation), Quaternion.Normalize(parent.Rotation * frame[b].Rotation));
            }
            writer.Write((float)time);
            var head = world[headBone];
            writer.Write(head.Position.X); writer.Write(head.Position.Y); writer.Write(head.Position.Z);
            writer.Write(head.Rotation.X); writer.Write(head.Rotation.Y); writer.Write(head.Rotation.Z); writer.Write(head.Rotation.W);
            for (var j = 0; j < custom.Length; j++)
            {
                var joint = custom[j];
                var pose = world[joint.Bone];
                finite &= float.IsFinite(pose.Position.X) && float.IsFinite(pose.Rotation.W);
                var axis = joint.Direction.Length == 3 ? new Vector3(joint.Direction[0], joint.Direction[1], joint.Direction[2]) : Vector3.UnitX;
                var tail = pose.Position + Vector3.Transform(axis, pose.Rotation) * joint.Length;
                var restPose = rest[joint.Bone];
                var restTail = restPose.Position + Vector3.Transform(axis, restPose.Rotation) * joint.Length;
                writer.Write(pose.Position.X); writer.Write(pose.Position.Y); writer.Write(pose.Position.Z);
                writer.Write(tail.X); writer.Write(tail.Y); writer.Write(tail.Z);
                writer.Write(restPose.Position.X); writer.Write(restPose.Position.Y); writer.Write(restPose.Position.Z);
                writer.Write(restTail.X); writer.Write(restTail.Y); writer.Write(restTail.Z);
                for (var c = 0; c < colliders.Length; c++)
                {
                    if ((joint.ColliderMask & (1L << c)) == 0) continue;
                    var bone = world[colliders[c].Bone];
                    var a = bone.Position + Vector3.Transform(new Vector3(colliders[c].A[0], colliders[c].A[1], colliders[c].A[2]), bone.Rotation);
                    var b = bone.Position + Vector3.Transform(new Vector3(colliders[c].B[0], colliders[c].B[1], colliders[c].B[2]), bone.Rotation);
                    // The depth of the joint end (GH3_HAIR_BENCH_MIDDLE=1: also of the joint middle; a joint can start
                    // inside a collider, for example a skirt joint on the hips).
                    var depth = 0f;
                    for (var k = Environment.GetEnvironmentVariable("GH3_HAIR_BENCH_MIDDLE") == "1" ? 1 : 2; k <= 2; k++)
                    {
                        var point = pose.Position + (tail - pose.Position) * (k / 2f);
                        var ab = b - a;
                        var t = ab.LengthSquared() < 1e-10f ? 0 : Math.Clamp(Vector3.Dot(point - a, ab) / ab.LengthSquared(), 0, 1);
                        depth = Math.Max(depth, colliders[c].Radius + joint.Radius - Vector3.Distance(point, a + ab * t));
                    }
                    if (time > 1 && depth > 0)
                    {
                        if (depth > deepest[j]) (deepest[j], deepestCollider[j]) = (depth, c);
                        depths.Add(depth);
                        contacts[j]++;
                    }
                }
            }
        }
        var sorted = depths.OrderBy(d => d).ToArray();
        string Quantile(double q) => sorted.Length == 0 ? "0" : (sorted[(int)Math.Min(sorted.Length - 1, q * sorted.Length)] * 1000).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        Console.WriteLine(FormattableString.Invariant($"{model.Id} at {fps} fps, {seconds:F0} s: {definition.Joints.Length} joints ({custom.Length} custom), {colliders.Length} colliders; ") +
            FormattableString.Invariant($"finite {finite}; peak {hair.PeakSpeed:F2} m/s at bone {hair.PeakBone}; solver {clock.Elapsed.TotalMilliseconds / (seconds * fps):F3} ms per frame; ") +
            FormattableString.Invariant($"contacts {depths.Count}, depth p50/p99/max {Quantile(0.5)}/{Quantile(0.99)}/{Quantile(1)} mm; ") +
            "deepest joint " + FormattableString.Invariant($"{deepest.Max() * 1000:F1} mm at {model.Joints.ElementAtOrDefault(Array.IndexOf(map, custom[Array.IndexOf(deepest, deepest.Max())].Bone))}"));
        if (Environment.GetEnvironmentVariable("GH3_HAIR_BENCH_DETAIL") == "1")
            for (var j = 0; j < custom.Length; j++)
                Console.WriteLine(FormattableString.Invariant($"  joint {names[j]}: deepest {deepest[j] * 1000:F1} mm in collider {deepestCollider[j]}, {contacts[j]} contacts"));
        Directory.Delete(cache, true);
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
