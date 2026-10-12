using System.Numerics;
using System.Text.Json;
using Gh3Skin;

internal static class StagePackLogicTests
{
    public static void Run()
    {
        Check(!StagePack.ValidId("../outside") && !StagePack.ValidId("pack\n") && StagePack.ValidId("example.stage"), "pack IDs stay within the stage directory");
        CheckInheritedLighting();
        CheckContinuousOrbit();
        CheckCameraKeyframes();
        CheckLookAtScreenOffset();
        if (Environment.GetEnvironmentVariable("GH3_PACK_ASSETS") is not { } assets) return;
        var timeline = StageChartReader.LoadEventFile(Path.Combine(Path.Combine(assets, "stages", "events"), "mycurse.json"));
        var paths = Directory.GetFiles(Path.Combine(assets, "stages"), "pack.json", SearchOption.AllDirectories);
        Check(paths.Length >= 10, "all ten performance stages are imported");
        foreach (var file in paths)
        {
            var directory = Path.GetDirectoryName(file)!;
            var pack = StagePack.Load(directory);
            var library = Path.Combine(assets, "libraries", pack.Library);
            var crowd = CrowdDefinition.Load(Path.Combine(library, "crowd", "crowd.json"));
            foreach (var member in pack.Crowd)
                Check(crowd.Version == 2 && crowd.Models.Contains(member.Model) && crowd.Members.ContainsKey(member.Role),
                    $"{pack.Id}: crowd model {member.Model} and profile {member.Role} exist in the shared library");
            var performance = StagePerformance.Load(Path.Combine(library, "direction", "performance.json"))!;
            Check(performance.Waypoints.Count == 0, "the animation library has no venue placements");
            var cameras = StageCameraBank.Load(Path.Combine(directory, "cameras.json"))!;
            var lights = StageLightingBank.Load(directory)!;
            var lighting = new StageLighting(lights, timeline);
            var positions = pack.Band.ToDictionary(p => p.Key, p => new Vector3(p.Value.Position[0], p.Value.Position[1], p.Value.Position[2]));
            var path = new StageCameraPath(new(Vector3.Zero, Vector3.UnitZ, 55, "fixed"), positions, bank: cameras);
            foreach (var shot in cameras.Arrays.Values.SelectMany(s => s))
            {
                Check(shot.Nodes.All(pack.Waypoints.ContainsKey), "camera waypoints are stage-local");
                foreach (var point in shot.Points)
                    foreach (var (role, bone) in new[] { (point.LockTo, point.LockBone), (point.LookAt, point.LookBone) })
                        if (role is not ("" or "world"))
                            Check(pack.Band.ContainsKey(role) && (bone == "00000000" || performance.Members[role].Bones.Contains(bone)), $"{pack.Id}: valid camera anchor {role}/{bone}");
            }
            for (double time = 0; time < 260; time += 0.5)
            {
                var camera = path.Sample(timeline, time);
                Check(float.IsFinite(camera.Position.LengthSquared()) && camera.FieldOfView is > 0 and < 170, "portable camera pose stays valid");
                var values = lighting.At(time).Sample(time);
                Check(values.Beams.All(b => float.IsFinite(b.Position.LengthSquared()) && Math.Abs(b.Rotation.LengthSquared()-1) < 0.001), "all stage beam tracks stay valid");
            }
            var held = path.Sample(timeline, 17.25);
            _ = path.Sample(timeline, 210);
            Check(path.Sample(timeline, 17.25) == held, "portable cameras retain their seek behavior");
            var actorFile = Path.Combine(directory, "actors.json");
            if (File.Exists(actorFile))
            {
                using var actorData = JsonDocument.Parse(File.ReadAllText(actorFile));
                foreach (var actor in actorData.RootElement.GetProperty("actors").EnumerateArray())
                {
                    using var reader = new BinaryReader(File.OpenRead(Path.Combine(directory, "actors", actor.GetProperty("id").GetString()!, "animation.bin")));
                    var animation = new BandAnimation(reader);
                    var clip = animation.Clips[actor.GetProperty("clip").GetInt32()];
                    for (var bone = 0; bone < animation.Parents.Length; bone++)
                        Check(float.IsFinite(animation.SampleClip(clip, clip.Duration/2, bone).Position.LengthSquared()), "actor clips are readable by the shared runtime");
                }
            }
            Console.WriteLine($"Stage pack checked: {pack.Id}; {cameras.Arrays.Count} camera groups; {lights.Lights.Length} lights");
        }
    }

    private static void CheckInheritedLighting()
    {
        StageLightingSnapshot Snapshot(float intensity, bool inherit = false) => new() { Lights = new[] {
            new StageLightSetting { Color = new float[] { 1,1,1 }, Intensity = intensity, Near = 1, Far = 1, Inherit = inherit } } };
        var light = new StageLightDefinition { Type = "Dir", Position = new float[3], Rotation = new float[] { 0,0,0,1 } };
        var bank = new StageLightingBank { Version = 1, Lights = new[] { light }, Snapshots = new() {
            ["start"] = Snapshot(0.5f), ["subtract"] = Snapshot(-0.25f), ["keep"] = Snapshot(0, true) },
            Moods = new() { ["intro"] = new[] { "start" }, ["verse"] = new[] { "subtract" }, ["solo"] = new[] { "keep" }, ["blackout"] = new[] { "subtract" } } };
        var lighting = new StageLighting(bank, new StageTimeline(new[] { new StageCue(0,"blend","0"),
            new StageCue(1,"lighting","verse"), new StageCue(2,"lighting","solo") },"partial"));
        Check(lighting.At(3).Sample(3).Lights[0].Energy.X == -0.25f, "partial snapshots preserve a preceding subtractive light");
        Check(lighting.At(0.5).Sample(0.5).Lights[0].Energy.X == 0.5f, "seeks reconstruct inherited light state");
        var sample = new StageLightSample(light, lighting.At(3).Sample(3).Lights[0]);
        Check(sample.Weight(new Vector3(0,0,0.5f),-Vector3.UnitZ) == 1 && sample.Weight(new Vector3(0,0,1.5f),-Vector3.UnitZ) == 0,
            "equal attenuation limits form a finite hard boundary");
    }

    private static void CheckContinuousOrbit()
    {
        var point = new OriginalCameraPoint { Position = new float[] { 0,0,-10 }, Rotation = new float[] { 0,0,0,1 }, LockTo = "guitarist", LookAt = "guitarist" };
        var shot = new OriginalCameraShot { Control = "OrbitContinuous", Points = new[] { point }, AngularSpeed = -90, HeightEase = new float[] { -2,4,1 } };
        var bank = new StageCameraBank { Version = 1, Arrays = new() { ["cameras"] = new[] { shot }, ["cameras_singer"] = new[] { shot } } };
        var timeline = new StageTimeline(new[] { new StageCue(1,"camera","singer") },"orbit");
        var path = new StageCameraPath(new(Vector3.Zero,Vector3.UnitZ,55,"fixed"),new Dictionary<string,Vector3> { ["guitarist"] = new(3,1,2) },bank:bank);
        Check(Vector3.Distance(path.Sample(timeline,0).Position,new(3,-1,-8)) < 0.0001, "continuous orbit starts at its authored height");
        var pose = path.Sample(timeline,1);
        Check(Vector3.Distance(pose.Position,new(13,5-6/MathF.E,2)) < 0.0001, "continuous orbit uses the song clock across cuts");
        _ = path.Sample(timeline,200);
        Check(path.Sample(timeline,1) == pose, "continuous orbit supports backward seeks");
    }

    private static void CheckCameraKeyframes()
    {
        OriginalCameraPoint Point(float x, float fov) => new() { Position = new[] { x,0,0 }, Rotation = new float[] { 0,0,0,1 }, FieldOfView = fov };
        var shot = new OriginalCameraShot { Control = "Keyframes", Points = new[] { Point(0,70), Point(0,70), Point(8,40) },
            Times = new float[] { 0,2,2.75f } };
        var bank = new StageCameraBank { Version = 1, Arrays = new() { ["cameras"] = new[] { shot } } };
        var timeline = new StageTimeline(new[] { new StageCue(0,"camera","wide",10) },"keyframes");
        var path = new StageCameraPath(new(Vector3.Zero,Vector3.UnitZ,55,"fixed"),new Dictionary<string,Vector3>(),bank:bank);
        Check(path.Sample(timeline,1).Position.X == 0, "camera keyframes retain authored holds");
        var middle = path.Sample(timeline,2.375);
        Check(Math.Abs(middle.Position.X-4) < 0.0001, "quick zoom follows its authored duration");
        Check(path.Sample(timeline,9).Position.X == 8, "camera keyframes hold the final pose");
        Check(path.Sample(timeline,2.375) == middle, "timed camera tracks support seeks");
    }

    private static void CheckLookAtScreenOffset()
    {
        var roll = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.3f);
        var point = new OriginalCameraPoint { Position = new float[] { 0,0,0 }, Rotation = new[] { roll.X,roll.Y,roll.Z,roll.W },
            FieldOfView = 90, LookAt = "guitarist", ScreenOffset = new[] { -0.5f,-0.5f } };
        var shot = new OriginalCameraShot { Control = "TwoCam", Points = new[] { point } };
        var bank = new StageCameraBank { Version = 1, Arrays = new() { ["cameras"] = new[] { shot } } };
        var timeline = new StageTimeline(new[] { new StageCue(0,"camera","wide",10) },"look");
        var path = new StageCameraPath(new(Vector3.Zero,Vector3.UnitZ,55,"fixed"),new Dictionary<string,Vector3> { ["guitarist"] = new(0,0,10) },bank:bank) { Aspect = 2 };
        var view = Quaternion.Inverse(path.Sample(timeline,1).Rotation!.Value);
        var target = Vector3.Transform(Vector3.UnitZ, view);
        var yaw = MathF.Atan2(target.X, target.Z);
        var pitch = MathF.Atan2(target.Y, new Vector2(target.X, target.Z).Length());
        Check(Math.Abs(yaw + MathF.PI / 8) < 0.0001 && Math.Abs(pitch - MathF.PI / 16) < 0.0001,
            "look-at screen offset turns the view by the half field of view, pitch first");
        Check(Vector3.Transform(Vector3.UnitY, path.Sample(timeline,1).Rotation!.Value).X < -0.2f, "look-at keeps the authored roll");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
