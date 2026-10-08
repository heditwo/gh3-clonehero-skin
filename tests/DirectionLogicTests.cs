using System.IO.Compression;
using System.Numerics;
using Gh3Skin;

internal static class DirectionLogicTests
{
    public static void Run()
    {
        Check(Gh3Checksum.Of("CameraCuts_NoteMapping") == "78e0a256", "GH3 identifiers match the source CRC");
        var point1 = new OriginalCameraPoint { Position = new float[] { 0, 2, -10 }, Rotation = new float[] { 0, 0, 0, 1 }, FieldOfView = 72 };
        var point2 = new OriginalCameraPoint { Position = new float[] { 10, 2, -10 }, Rotation = new float[] { 0, 0, 0, 1 }, FieldOfView = 40 };
        var shot = new OriginalCameraShot { Name = "native", Control = "TwoCam", Points = new[] { point1, point2 } };
        var bank = new StageCameraBank { Version = 1, Arrays = new() { ["cameras"] = new[] { shot } },
            Notes = new() { [85] = new() { Array = "cameras" }, [78] = new() { Enable = false } } };
        var timeline = new StageTimeline(new[] { new StageCue(0, "gh3-camera", "85", 12.453), new StageCue(12.453, "gh3-camera", "78") }, "test");
        var path = new StageCameraPath(new(Vector3.Zero, Vector3.UnitZ, 55, "fixed"), new Dictionary<string, Vector3>(), bank: bank);
        var halfway = path.Sample(timeline, 6.2265);
        Check(Math.Abs(halfway.Position.X - 5) < 0.0001, "native camera note length controls the full morph");
        Check(path.Sample(timeline, 9).Position.X > halfway.Position.X, "native morph continues after five seconds");
        _ = path.Sample(timeline, 300);
        Check(path.Sample(timeline, 6.2265) == halfway, "native camera seeking is deterministic");
        Check(path.Sample(timeline, 100).Shot == "native", "camera disable cue holds the selected camera");

        var member = new PerformanceMember { Bones = new[] { "00000000" }, Clips = new() {
            ["00000001"] = new() { Duration = 2, Frames = 2 }, ["00000002"] = new() { Duration = 3, Frames = 2 }
        }, Groups = new() { ["stance_a/idle/normal/med"] = new[] { "00000001" }, ["stance_b/idle/normal/med"] = new[] { "00000002" } } };
        var cues = new StageTimeline(new[] { new StageCue(0.5, "stance", "stance_b", Role: "guitarist") }, "test");
        var bind = new[] { new BandPose(Vector3.Zero, Quaternion.Identity) };
        var player = new PerformancePlayer("guitarist", "", member, bind, cues);
        Check(player.At(0.6).Stance == "stance_a", "queued stance waits for the current body clip");
        var at = player.At(2.1);
        Check(at.Stance == "stance_b" && at.Clip == "00000002", "queued stance selects the original state's clip group");
        _ = player.At(90);
        Check(player.At(2.1) == at, "body schedule survives a backward seek");
        var immediate = new PerformancePlayer("guitarist", "", member, bind,
            new StageTimeline(new[] { new StageCue(0.5, "stance", "stance_b", Role: "guitarist", Immediate: true) }, "test"));
        Check(immediate.At(0.5).Stance == "stance_b", "no_wait stance interrupts at the exact cue time");

        using var bytes = new MemoryStream();
        using (var zip = new GZipStream(bytes, CompressionLevel.Fastest, true))
        using (var writer = new BinaryWriter(zip))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("GH3CLP01"));
            writer.Write(2); writer.Write(2); writer.Write(1); writer.Write(2f);
            writer.Write(1); writer.Write(1);
            foreach (var x in new[] { 2f, 4f })
                foreach (var f in new[] { x, 0, 0, 0, 0, 0, 1 }) writer.Write(f);
        }
        bytes.Position = 0;
        var clip = new PerformanceClip(bytes, 2);
        var poses = new[] { new BandPose(new(10, 0, 0), Quaternion.Identity), new BandPose(new(20, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1)) };
        var originalRotation = poses[1].Rotation;
        clip.Apply(poses, poses, 1);
        Check(poses[0].Position.X == 10 && poses[1].Position.X == 3 && poses[1].Rotation == originalRotation,
            "sparse animation masks preserve unanimated bones and channels");

        using var gripBytes = new MemoryStream();
        using (var zip = new GZipStream(gripBytes, CompressionLevel.Fastest, true))
        using (var writer = new BinaryWriter(zip))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("GH3CLP01"));
            writer.Write(2); writer.Write(2); writer.Write(1); writer.Write(1f);
            writer.Write(1); writer.Write(2);
            for (var i = 0; i < 2; i++)
                foreach (var f in new[] { 0f, 0, 0, MathF.Sqrt(0.5f), 0, 0, MathF.Sqrt(0.5f) }) writer.Write(f);
        }
        gripBytes.Position = 0;
        var grip = new PerformanceClip(gripBytes, 2);
        var gripPose = new[] { new BandPose(Vector3.One, Quaternion.Identity),
            new BandPose(new(3, 4, 5), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2)) };
        var gripBind = gripPose.ToArray();
        grip.Apply(gripPose, gripBind, 0, true);
        Check(Vector3.Distance(Vector3.Transform(Vector3.UnitX, gripPose[1].Rotation), Vector3.UnitY) < 0.00001f,
            "hand rotation deltas turn the authored grip in parent space");
        Check(gripPose[0] == gripBind[0] && gripPose[1].Position == gripBind[1].Position,
            "a hand rotation layer preserves other bones and the grip position");

        var chart = StageChartReader.ReadChart("[Song]\nResolution = 192\n[SyncTrack]\n0 = B 120000\n192 = B 60000\n[ExpertSingle]\n0 = N 0 384\n0 = N 2 384");
        var chord = chart.Track("strum", "guitarist").Single();
        Check(chord.Value == "5" && chord.Duration == 1.5, "chart chords merge and sustains span tempo changes");
        Check(chart.TempoAt(0.6) == 60, "performance tempo comes from the chart");
        CheckCameraSelection();
        if (Environment.GetEnvironmentVariable("GH3_DIRECTION_ASSETS") is { } assets) CheckConvertedAssets(assets);
        Console.WriteLine("Original camera, performance scheduling, clip masks, and instrument timing checks passed.");
    }

    private static void CheckCameraSelection()
    {
        OriginalCameraShot Shot(string name, string type) => new() { Name = name, Type = type, Control = "TwoCam", Points = new[] {
            new OriginalCameraPoint { Position = new float[] { 0, 2, -10 }, Rotation = new float[] { 0, 0, 0, 1 } },
            new OriginalCameraPoint { Position = new float[] { 10, 2, -10 }, Rotation = new float[] { 0, 0, 0, 1 } } } };
        var bank = new StageCameraBank { Version = 1,
            Arrays = new() { ["cameras"] = new[] { Shot("mid", "mid"), Shot("closeup", "closeup") },
                ["cameras_drummer"] = new[] { Shot("drummer1", "stage"), Shot("drummer2", "stage") } },
            Notes = new() { [79] = new() { Array = "cameras" }, [85] = new() { Array = "cameras", Type = "mid" },
                [87] = new() { Array = "cameras", Type = "stage" }, [89] = new() { Array = "cameras_drummer" } } };
        StageCameraPath Path() => new(new(Vector3.Zero, Vector3.UnitZ, 55, "fixed"), new Dictionary<string, Vector3>(), bank: bank);
        var timeline = new StageTimeline(new[] { new StageCue(0, "gh3-camera", "85", 2), new StageCue(2, "gh3-camera", "79", 2),
            new StageCue(4, "gh3-camera", "79", 2), new StageCue(6, "gh3-camera", "87", 2), new StageCue(8, "gh3-camera", "89", 2) }, "selection");
        var path = Path();
        Check(path.Sample(timeline, 1).Shot == "mid" && path.Sample(timeline, 3).Shot == "closeup" && path.Sample(timeline, 5).Shot == "mid",
            "an untyped camera note excludes the type of the previous shot");
        Check(path.Sample(timeline, 9).Shot == "drummer1", "GH3 uses the first array entry when the filters exclude every shot");

        var timer = new StageTimeline(new[] { new StageCue(0, "gh3-camera", "79") }, "timer");
        path = Path();
        var first = path.Sample(timer, 0).Shot;
        var cut = Enumerable.Range(1, 1000).Select(step => step / 100.0).First(time => path.Sample(timer, time).Shot != first);
        Check(cut >= 3 && cut <= 8 && Math.Abs(cut - Math.Round(cut)) < 0.0001, "the camera timer is a whole number of seconds from 3 to 8");

        var three = new StageCameraBank { Version = 1, Arrays = new() { ["cameras"] = new[] { Shot("a", "mid"), Shot("b", "stage"), Shot("c", "longshot") } },
            Notes = bank.Notes };
        var expected = new Gh3Random(77);
        var firstIndex = expected.Next(3);
        var firstLength = 3 + expected.Next(6);
        var remaining = Enumerable.Range(0, 3).Where(i => i != firstIndex).ToArray();
        var secondIndex = remaining[expected.Next(2)];
        path = new(new(Vector3.Zero, Vector3.UnitZ, 55, "fixed"), new Dictionary<string, Vector3>(), bank: three) { Seed = 77 };
        Check(path.Sample(timer, 0).Shot == "abc"[firstIndex].ToString() && path.Sample(timer, firstLength - 0.01).Shot == "abc"[firstIndex].ToString() &&
            path.Sample(timer, firstLength).Shot == "abc"[secondIndex].ToString(), "camera draws follow GH3's order: shot index, then timer, then the next shot");

        var close = new StageTimeline(new[] { new StageCue(0, "gh3-camera", "85", 1.999), new StageCue(2, "gh3-camera", "79", 2) }, "close");
        path = Path();
        Check(path.Sample(close, 1.9995).Position.X == 10 && path.Sample(close, 2).Shot == "closeup",
            "a note one millisecond after the camera timer does not add a second cut");
    }

    private static void CheckConvertedAssets(string assets)
    {
        var directory = Path.Combine(assets, "budokan", "direction");
        var bank = StagePerformance.Load(Path.Combine(directory, "performance.json"))!;
        var cameras = StageCameraBank.Load(Path.Combine(directory, "cameras.json"))!;
        Check(bank.Waypoints.Count > 0, "converted venue waypoints are present");
        var timeline = StageChartReader.Load("", "", Path.Combine(assets, "events"), "My Curse", "Killswitch Engage");
        Check(timeline.Cameras.Length == 53 && timeline.Track("face", "singer").Length == 5, "original song direction is complete");
        var path = new StageCameraPath(new(Vector3.Zero, Vector3.UnitZ, 55, "fixed"), new Dictionary<string, Vector3>(), bank: cameras);
        foreach (var (role, definition) in bank.Members)
        {
            using var reader = new BinaryReader(File.OpenRead(Path.Combine(assets, "budokan", "band", role, "animation.bin")));
            var bind = new BandAnimation(reader).BindPose;
            var player = new PerformancePlayer(role, directory, definition, bind, timeline);
            player.Prepare();
            for (double time = 0; time < timeline.Cues[^1].Time + 1; time += 0.1)
            {
                player.Sample(time);
                Check(player.Pose.All(p => float.IsFinite(p.Position.LengthSquared()) && Math.Abs(p.Rotation.LengthSquared() - 1) < 0.001), "converted animation poses remain finite and normalized");
                var camera = path.Sample(timeline, time);
                Check(float.IsFinite(camera.Position.LengthSquared()) && float.IsFinite(camera.FieldOfView), "converted camera poses remain finite");
            }
            player.Sample(45.5);
            var held = player.Pose.ToArray();
            player.Sample(180);
            player.Sample(45.5);
            Check(player.Pose.SequenceEqual(held), "converted song layers reproduce the pose after a seek");
        }
        foreach (var (role, member) in bank.Members)
            foreach (var clip in member.Clips.Keys)
            {
                using var stream = File.OpenRead(Path.Combine(directory, role, clip + ".bin.gz"));
                _ = new PerformanceClip(stream, member.Bones.Length);
            }
        Console.WriteLine($"Validated {bank.Members.Values.Sum(m => m.Clips.Count)} converted clips and the full My Curse direction track.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
