using System.Numerics;
using Gh3Skin;

internal static class BandLogicTests
{
    public static void Run()
    {
        var bytes = Fixture();
        using var reader = new BinaryReader(new MemoryStream(bytes));
        var animation = new BandAnimation(reader);
        var held = animation.Sample(animation.At(0.5), 0);
        for (var i = 0; i < 10; i++) Check(animation.Sample(animation.At(0.5), 0) == held, "held pose is stable");
        _ = animation.Sample(animation.At(10000), 0);
        Check(animation.Sample(animation.At(0.5), 0) == held, "backward seek restores pose");
        Check(Vector3.Distance(animation.Sample(animation.At(1.749999), 0).Position,
            animation.Sample(animation.At(1.75), 0).Position) < 0.001f, "clip crossfade is continuous");
        Check(Vector3.Distance(animation.Sample(animation.At(3.499999), 0).Position,
            animation.Sample(animation.At(3.5), 0).Position) < 0.001f, "playlist wrap is continuous");
        Check(Math.Abs(held.Rotation.LengthSquared() - 1) < 0.0001f, "interpolated rotation is normalized");
        Reject(bytes[..^1], "truncated animation rejected");
        var bad = (byte[])bytes.Clone();
        BitConverter.GetBytes(0).CopyTo(bad, 16);
        Reject(bad, "self-parent rejected");
        BitConverter.GetBytes(float.NaN).CopyTo(bytes, 24);
        Reject(bytes, "non-finite pose rejected");

        var cues = new StageTimeline(new[] { new StageCue(7, "camera", "singer"), new StageCue(9, "camera", "drummer") }, "test");
        var fixedPose = new StageCameraPose(new(0, 4, -20), new(0, 4, 0), 55, "fixed");
        var positions = new Dictionary<string, Vector3> { ["singer"] = new(-1, 0, -3), ["drummer"] = new(0, 1.2f, 3) };
        OriginalCameraShot Shot(string name) => new()
        {
            Name = name, Control = "TwoCam", Points = new[] {
                new OriginalCameraPoint { Position = new float[] { 0, 4, -20 }, Rotation = new float[] { 0, 0, 0, 1 } },
                new OriginalCameraPoint { Position = new float[] { 2, 5, -18 }, Rotation = new float[] { 0, 0, 0, 1 } }
            }
        };
        var bank = new StageCameraBank { Version = 1, Arrays = new() {
            ["cameras"] = new[] { Shot("wide") }, ["cameras_singer"] = new[] { Shot("singer") },
            ["cameras_drummer"] = new[] { Shot("drummer") }
        } };
        var camera = new StageCameraPath(fixedPose, positions, bank: bank);
        Check(cues.CameraAt(6.999) == null && cues.CameraAt(7)?.Value == "singer", "camera cue boundary");
        Check(camera.Sample(cues, 9).Shot == "drummer", "camera cuts at song time");
        var pose = camera.Sample(cues, 8);
        _ = camera.Sample(cues, 1000);
        Check(camera.Sample(cues, 8) == pose, "camera seek restores framing");
        Check(camera.Sample(cues, 7.5).Position != pose.Position, "camera moves within a shot");
        Check(new StageCameraPath(fixedPose, positions, "fixed").Sample(cues, 9) == fixedPose, "fixed camera override");
        var chart = StageChartReader.ReadChart("[Events]\n192 = E \"stage camera singer\"");
        Check(chart.CameraAt(0.5)?.Value == "singer", "chart camera event uses tempo conversion");

        var directory = Path.Combine(AppContext.BaseDirectory, "../../../../assets/stages/budokan/band");
        if (Directory.Exists(directory))
            foreach (var file in Directory.GetFiles(directory, "animation.bin", SearchOption.AllDirectories))
            {
                using var source = new BinaryReader(File.OpenRead(file));
                var converted = new BandAnimation(source);
                for (var bone = 0; bone < converted.Parents.Length; bone++)
                    Check(float.IsFinite(converted.Sample(converted.At(18.3), bone).Position.LengthSquared()), "converted pose is finite");
            }
    }

    private static byte[] Fixture()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("GH3ANM01"));
        writer.Write(1);
        writer.Write(2);
        writer.Write(-1);
        Pose(0);
        for (var clip = 0; clip < 2; clip++)
        {
            writer.Write(2f);
            writer.Write(2);
            Pose(clip * 10);
            Pose(clip * 10 + 2);
        }
        return stream.ToArray();

        void Pose(float x)
        {
            foreach (var value in new[] { x, 0, 0, 0, 0, 0, 1 }) writer.Write(value);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Reject(byte[] data, string message)
    {
        try
        {
            using var reader = new BinaryReader(new MemoryStream(data));
            _ = new BandAnimation(reader);
        }
        catch (Exception error) when (error is IOException or InvalidDataException) { return; }
        throw new Exception(message);
    }
}
