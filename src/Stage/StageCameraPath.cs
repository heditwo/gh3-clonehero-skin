using System.Numerics;
using System.Text.Json;

namespace Gh3Skin;

internal readonly record struct StageCameraPose(Vector3 Position, Vector3 Target, float FieldOfView, string Shot)
{
    public Quaternion? Rotation { get; init; } = null;
}

internal sealed class OriginalCameraPoint
{
    public float[] Position { get; set; } = Array.Empty<float>();
    public float[] Rotation { get; set; } = Array.Empty<float>();
    public float FieldOfView { get; set; } = 72;
    public string LockTo { get; set; } = "world";
    public string LockBone { get; set; } = "00000000";
    public string LookAt { get; set; } = "";
    public string LookBone { get; set; } = "00000000";
    public float[] ScreenOffset { get; set; } = new float[2];
}

internal sealed class OriginalCameraShot
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Control { get; set; } = "";
    public OriginalCameraPoint[] Points { get; set; } = Array.Empty<OriginalCameraPoint>();
    public string[] Nodes { get; set; } = Array.Empty<string>();
    public float OrbitAngle { get; set; }
    public float AngularSpeed { get; set; }
    public float[] HeightEase { get; set; } = new float[3];
    public float[] Times { get; set; } = Array.Empty<float>();
    public string Interpolation { get; set; } = "smooth";
    public float StepTime { get; set; } = 6;
    public float StepCount { get; set; } = 6;
    public float[] StepDirection { get; set; } = new float[3];
    public bool HandCam { get; set; }
    public bool DisableOrbit1 { get; set; }
    public bool DisableOrbit2 { get; set; }
}

internal sealed class OriginalCameraSelector
{
    public string Array { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool Enable { get; set; } = true;
}

internal sealed class StageCameraBank
{
    public int Version { get; set; }
    public Dictionary<string, OriginalCameraShot[]> Arrays { get; set; } = new();
    public Dictionary<int, OriginalCameraSelector> Notes { get; set; } = new();

    public static StageCameraBank? Load(string path)
    {
        if (!File.Exists(path)) return null;
        var bank = JsonSerializer.Deserialize<StageCameraBank>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (bank?.Version != 1 || !bank.Arrays.ContainsKey("cameras") || bank.Arrays.Count > 128)
            throw new InvalidDataException("Invalid original camera library");
        foreach (var shot in bank.Arrays.Values.SelectMany(s => s))
        {
            if (shot.Points.Length < 1 || shot.Points.Length > (shot.Control == "Keyframes" ? 64 : 4) || !float.IsFinite(shot.OrbitAngle) ||
                shot.StepTime <= 0 || !float.IsFinite(shot.StepTime) || !float.IsFinite(shot.StepCount) ||
                shot.StepDirection.Length != 3 || !shot.StepDirection.All(float.IsFinite) || !float.IsFinite(shot.AngularSpeed) ||
                shot.HeightEase.Length != 3 || !shot.HeightEase.All(float.IsFinite) || shot.HeightEase[2] < 0 ||
                shot.Control == "OrbitContinuous" && (shot.Points.Length != 1 || shot.Points[0].LookAt.Length == 0))
                throw new InvalidDataException("Invalid original camera shot");
            if (shot.Control == "Keyframes" && (shot.Times.Length != shot.Points.Length || shot.Times.Length < 2 || shot.Times[0] != 0 ||
                !shot.Times.All(float.IsFinite) || shot.Times.Zip(shot.Times.Skip(1), (a,b) => b > a).Any(valid => !valid) ||
                shot.Interpolation is not ("linear" or "smooth")))
                throw new InvalidDataException("Invalid camera keyframe times");
            foreach (var point in shot.Points)
                if (point.Position.Length != 3 || point.Rotation.Length != 4 || point.ScreenOffset.Length != 2 ||
                    !point.Position.Concat(point.Rotation).Concat(point.ScreenOffset).All(float.IsFinite) ||
                    !float.IsFinite(point.FieldOfView) || point.FieldOfView is <= 0 or >= 170)
                    throw new InvalidDataException("Invalid original camera point");
        }
        return bank;
    }
}

internal sealed class StageCameraPath
{
    private sealed record Cut(double Time, double Duration, OriginalCameraShot Shot, uint Seed);
    private readonly StageCameraPose _fixed;
    private readonly IReadOnlyDictionary<string, Vector3> _band;
    private readonly string _mode;
    private readonly StageCameraBank? _bank;
    private readonly Func<string, string, BandPose?>? _anchor;
    private readonly List<Cut> _cuts = new();
    private StageTimeline? _timeline;
    private OriginalCameraSelector _selector = new() { Array = "cameras" };
    private bool _enabled = true;
    private int _cueIndex;
    private double _next;
    private uint _random = 0x47334833;
    private readonly string _forcedShot = Environment.GetEnvironmentVariable("GH3_SKIN_CAMERA_SHOT") ?? "";
    public float Aspect { get; set; } = 16f / 9;
    private static readonly bool LogCuts = Environment.GetEnvironmentVariable("GH3_SKIN_CAMERA_LOG") == "1";

    public StageCameraPath(StageCameraPose fixedPose, IReadOnlyDictionary<string, Vector3> band, string mode = "auto",
        StageCameraBank? bank = null, Func<string, string, BandPose?>? anchor = null)
    {
        _fixed = fixedPose;
        _band = band;
        _mode = mode;
        _bank = bank;
        _anchor = anchor;
    }

    public StageCameraPose Sample(StageTimeline timeline, double time)
    {
        if (!double.IsFinite(time)) throw new ArgumentOutOfRangeException(nameof(time));
        if (_mode == "fixed" || _bank == null) return _fixed;
        time = Math.Max(0, time);
        if (_timeline != timeline)
        {
            _timeline = timeline;
            _cuts.Clear();
            _next = Math.Min(0, timeline.Cues.FirstOrDefault()?.Time ?? 0);
            _cueIndex = 0;
            _enabled = true;
            _selector = new() { Array = "cameras" };
            _random = 0x47334833;
        }
        while (_next <= time && _cuts.Count < 100000) AddCut(timeline);
        var index = StageTimeline.UpperBound(_cuts, time, c => c.Time) - 1;
        if (index < 0) return _fixed;
        var cut = _cuts[index];
        var age = Math.Max(0, time - cut.Time);
        var t = (float)Math.Clamp(age / cut.Duration, 0, 1);
        var shot = cut.Shot;
        var points = shot.Points;
        var fraction = shot.Control == "Pan" ? Smooth((float)Math.Clamp(age - 2, 0, 1)) : t;
        var first = 0;
        var last = points.Length - 1;
        if (shot.Control == "Keyframes")
        {
            first = Math.Clamp(StageTimeline.UpperBound(shot.Times, age, value => value) - 1, 0, points.Length - 2);
            last = first + 1;
            fraction = (float)Math.Clamp((age - shot.Times[first]) / (shot.Times[last] - shot.Times[first]), 0, 1);
            if (shot.Interpolation == "smooth") fraction = Smooth(fraction);
        }
        var a = Resolve(points[first], shot.Control == "OrbitContinuous" ? OrbitPosition(shot, time) : null);
        var b = points.Length == 1 ? a : Resolve(points[last]);
        var position = Vector3.Lerp(a.Position, b.Position, fraction);
        var rotation = Quaternion.Slerp(a.Rotation, b.Rotation, fraction);
        var fov = a.Fov + (b.Fov - a.Fov) * fraction;
        if (points.Length > 2 && shot.Control != "Keyframes")
        {
            var scaled = t * (points.Length - 1);
            var segment = Math.Min((int)scaled, points.Length - 2);
            var u = scaled - segment;
            var p0 = Resolve(points[Math.Max(0, segment - 1)]);
            var p1 = Resolve(points[segment]);
            var p2 = Resolve(points[segment + 1]);
            var p3 = Resolve(points[Math.Min(points.Length - 1, segment + 2)]);
            position = 0.5f * ((2 * p1.Position) + (-p0.Position + p2.Position) * u +
                (2 * p0.Position - 5 * p1.Position + 4 * p2.Position - p3.Position) * u * u +
                (-p0.Position + 3 * p1.Position - 3 * p2.Position + p3.Position) * u * u * u);
            rotation = Quaternion.Slerp(p1.Rotation, p2.Rotation, u);
            fov = p1.Fov + (p2.Fov - p1.Fov) * u;
        }
        if (shot.Control is "Orbit" or "OrbitAndMorphTwoCam" && !shot.DisableOrbit2)
        {
            var center = Anchor(points[^1].LockTo, points[^1].LockBone).Position;
            var orbit = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -shot.OrbitAngle * MathF.PI / 180 * t);
            position = center + Vector3.Transform(position - center, orbit);
            rotation = orbit * rotation;
        }
        if (shot.Control == "SpecialStepMove" && age < shot.StepTime)
            position += Vector(shot.StepDirection) * MathF.Sin((float)(age / shot.StepTime) * shot.StepCount * 2 * MathF.PI);
        if (shot.Control == "HandCam" || shot.HandCam)
        {
            var random = cut.Seed % 101;
            var zoom = shot.Type == "longshot" ? random < 25 ? 62 : random > 95 ? 78 : a.Fov :
                shot.Type == "mid" ? random < 5 ? 64 : random > 85 ? 82 : a.Fov : a.Fov;
            fov = a.Fov + (zoom - a.Fov) * t;
        }
        var vertical = 2 * MathF.Atan(MathF.Tan(fov * MathF.PI / 360) / Aspect) * 180 / MathF.PI;
        return new(position, position + Vector3.Transform(Vector3.UnitZ, rotation), vertical, shot.Name) { Rotation = rotation };
    }

    private void AddCut(StageTimeline timeline)
    {
        var start = _next;
        var duration = 0.0;
        while (_cueIndex < timeline.Cameras.Length && timeline.Cameras[_cueIndex].Time <= start)
        {
            var cue = timeline.Cameras[_cueIndex++];
            if (cue.Type == "camera") _selector = Named(cue.Value);
            else if (_bank!.Notes.TryGetValue(int.Parse(cue.Value), out var selector))
            {
                if (selector.Array.Length == 0) _enabled = selector.Enable;
                else { _selector = selector; _enabled = true; }
            }
            if (cue.Duration > 0.2) duration = cue.Duration;
        }
        var selected = _mode == "auto" ? _selector : Named(_mode);
        var array = _bank!.Arrays.GetValueOrDefault(selected.Array, _bank.Arrays["cameras"]);
        var node = "guitarist_start";
        var walks = timeline.Track("walk", "guitarist");
        var walk = StageTimeline.UpperBound(walks, start, c => c.Time) - 1;
        if (walk >= 0) node = walks[walk].Value;
        var candidates = array.Where(s => s.Nodes.Length == 0 || s.Nodes.Contains(node)).ToArray();
        if (candidates.Length == 0) candidates = _bank.Arrays.GetValueOrDefault("cameras_wide", array).Where(s => s.Nodes.Length == 0).ToArray();
        if (candidates.Length == 0) throw new InvalidDataException("Stage has no unrestricted camera");
        var filtered = candidates.Where(s => (selected.Name.Length == 0 || s.Name == selected.Name) &&
            (selected.Type.Length == 0 || s.Type == selected.Type)).ToArray();
        if (filtered.Length > 0) candidates = filtered;
        if (selected.Type.Length == 0 && selected.Name.Length == 0 && _cuts.Count > 0)
        {
            filtered = candidates.Where(s => s.Type != _cuts[^1].Shot.Type).ToArray();
            if (filtered.Length > 0) candidates = filtered;
        }
        if (_forcedShot.Length > 0 && candidates.Any(s => s.Name == _forcedShot)) candidates = candidates.Where(s => s.Name == _forcedShot).ToArray();
        var seed = Random();
        var shot = candidates[seed % candidates.Length];
        if (duration <= 0)
        {
            duration = 3 + Random() / (double)uint.MaxValue * 5;
            var downbeat = timeline.Cues.FirstOrDefault(c => c.Type == "gh3-note" && c.Value == "58" && c.Time > start + duration);
            if (downbeat != null) duration = downbeat.Time - start;
        }
        _cuts.Add(new(start, Math.Max(0.001, duration), shot, seed));
        if (LogCuts) Console.WriteLine($"GH3 camera cut {start:0.00}s {duration:0.00}s {selected.Array}/{selected.Type}/{selected.Name} -> {shot.Name} ({shot.Type}, {shot.Control})");
        _next = _enabled ? start + duration : double.PositiveInfinity;
        if (_mode == "auto" && _cueIndex < timeline.Cameras.Length) _next = Math.Min(_next, timeline.Cameras[_cueIndex].Time);
        if (_next <= start) _next = start + 0.001;
    }

    private Vector3 OrbitPosition(OriginalCameraShot shot, double time)
    {
        var point = shot.Points[0];
        var angle = (float)(time * shot.AngularSpeed % 360) * MathF.PI / 180;
        var height = shot.HeightEase[1] + (shot.HeightEase[0] - shot.HeightEase[1]) * (float)Math.Exp(-time * shot.HeightEase[2]);
        return Anchor(point.LockTo, point.LockBone).Position + Vector3.Transform(Vector(point.Position),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, angle)) + Vector3.UnitY * height;
    }

    private (Vector3 Position, Quaternion Rotation, float Fov) Resolve(OriginalCameraPoint point, Vector3? orbit = null)
    {
        var anchor = Anchor(point.LockTo, point.LockBone);
        var position = orbit ?? anchor.Position + Vector3.Transform(Vector(point.Position), anchor.Rotation);
        var q = point.Rotation;
        var rotation = Quaternion.Normalize(anchor.Rotation * new Quaternion(q[0], q[1], q[2], q[3]));
        if (point.LookAt.Length > 0)
        {
            var target = Anchor(point.LookAt, point.LookBone).Position;
            var forward = Vector3.Normalize(target - position);
            var right = Vector3.Cross(Vector3.UnitY, forward);
            if (right.LengthSquared() > 0.00001f)
            {
                right = Vector3.Normalize(right);
                var up = Vector3.Cross(forward, right);
                var look = Quaternion.CreateFromRotationMatrix(new Matrix4x4(right.X, right.Y, right.Z, 0,
                    up.X, up.Y, up.Z, 0, forward.X, forward.Y, forward.Z, 0, 0, 0, 0, 1));
                var tangent = MathF.Tan(point.FieldOfView * MathF.PI / 360);
                var yaw = -MathF.Atan(point.ScreenOffset[0] * tangent);
                var pitch = MathF.Atan(point.ScreenOffset[1] * tangent / Aspect);
                rotation = look * Quaternion.CreateFromYawPitchRoll(yaw, pitch, 0);
            }
        }
        return (position, rotation, point.FieldOfView);
    }

    private BandPose Anchor(string role, string bone) => _anchor?.Invoke(role, bone) ??
        new BandPose(_band.GetValueOrDefault(role, Vector3.Zero), Quaternion.Identity);

    private uint Random()
    {
        _random ^= _random << 13;
        _random ^= _random >> 17;
        _random ^= _random << 5;
        return _random;
    }

    private static OriginalCameraSelector Named(string name) => name switch
    {
        "wide" => new() { Array = "cameras_wide", Type = "stage" },
        "guitarist" => new() { Array = "cameras_guitar_closeup" },
        _ => new() { Array = "cameras_" + name },
    };
    private static float Smooth(float value) => value * value * (3 - 2 * value);
    private static Vector3 Vector(float[] value) => new(value[0], value[1], value[2]);
    public static bool ValidShot(string shot) => shot is "wide" or "singer" or "guitarist" or "bassist" or "drummer" or "orbit";
}
