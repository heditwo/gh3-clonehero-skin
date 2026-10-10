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
        var bank = JsonSerializer.Deserialize<StageCameraBank>(File.ReadAllText(path), Packages.PackageJson.CaseInsensitive);
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
    private sealed record Cut(double Time, double Duration, OriginalCameraShot Shot, int Zoom);
    // GH3.exe seeds its script random generator with this value at boot (0x4016f3).
    private const int BootSeed = 0x06680bf3;
    // GH3 starts a cut on the first frame at or after a note, so a timer that ends within a frame of a note does not cut first.
    private const double FrameTolerance = 1.0 / 60;
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
    private Gh3Random _random = new(BootSeed);
    private readonly string _forcedShot = Environment.GetEnvironmentVariable("GH3_SKIN_CAMERA_SHOT") ?? "";
    public float Aspect { get; set; } = 16f / 9;
    public int Seed { get; init; } = BootSeed;

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
        time = Math.Max(timeline.Start, time);
        if (_timeline != timeline)
        {
            _timeline = timeline;
            _cuts.Clear();
            _next = timeline.Start;
            _cueIndex = 0;
            _enabled = true;
            _selector = new() { Array = "cameras" };
            _random = new(Seed);
        }
        while (_next <= time && _cuts.Count < StageLimits.MaximumCount) AddCut(timeline);
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
        // CCam_Orbit turns the lock frame about its Y axis before the look-at (GH3.exe 0x4ba2d0).
        var spin = shot.Control is "Orbit" or "OrbitAndMorphTwoCam" && !shot.DisableOrbit2 ? -shot.OrbitAngle * MathF.PI / 180 * t : 0;
        var a = Resolve(points[first], shot.Control == "OrbitContinuous" ? OrbitPosition(shot, time) : null, spin: spin);
        var b = points.Length == 1 ? a : Resolve(points[last], spin: spin);
        var position = Vector3.Lerp(a.Position, b.Position, fraction);
        var rotation = Quaternion.Slerp(a.Rotation, b.Rotation, fraction);
        var fov = a.Fov + (b.Fov - a.Fov) * fraction;
        if (points.Length > 2 && shot.Control != "Keyframes")
        {
            var scaled = t * (points.Length - 1);
            var segment = Math.Min((int)scaled, points.Length - 2);
            var u = scaled - segment;
            var p0 = Resolve(points[Math.Max(0, segment - 1)], spin: spin);
            var p1 = Resolve(points[segment], spin: spin);
            var p2 = Resolve(points[segment + 1], spin: spin);
            var p3 = Resolve(points[Math.Min(points.Length - 1, segment + 2)], spin: spin);
            position = 0.5f * ((2 * p1.Position) + (-p0.Position + p2.Position) * u +
                (2 * p0.Position - 5 * p1.Position + 4 * p2.Position - p3.Position) * u * u +
                (-p0.Position + 3 * p1.Position - 3 * p2.Position + p3.Position) * u * u * u);
            rotation = Quaternion.Slerp(p1.Rotation, p2.Rotation, u);
            fov = p1.Fov + (p2.Fov - p1.Fov) * u;
        }
        if (shot.Control == "SpecialStepMove" && age < shot.StepTime)
            position += Vector(shot.StepDirection) * MathF.Sin((float)(age / shot.StepTime) * shot.StepCount * 2 * MathF.PI);
        if (cut.Zoom >= 0)
        {
            var zoom = shot.Type == "longshot" ? cut.Zoom < 25 ? 62 : cut.Zoom > 95 ? 78 : a.Fov :
                cut.Zoom < 5 ? 64 : cut.Zoom > 85 ? 82 : a.Fov;
            var zoomed = Resolve(points[0], fov: zoom);
            rotation = Quaternion.Slerp(a.Rotation, zoomed.Rotation, t);
            fov = a.Fov + (zoom - a.Fov) * t;
        }
        var vertical = 2 * MathF.Atan(MathF.Tan(fov * MathF.PI / 360) / Aspect) * 180 / MathF.PI;
        return new(position, position + Vector3.Transform(Vector3.UnitZ, rotation), vertical, shot.Name) { Rotation = rotation };
    }

    // CameraCuts_StartCallback and CameraCuts_CalcTime (guitar_cameras.qb) with CameraCut_GetArraySize (GH3.exe 0x41d770).
    private void AddCut(StageTimeline timeline)
    {
        var start = _next;
        var length = 0.0;
        while (_cueIndex < timeline.Cameras.Length && timeline.Cameras[_cueIndex].Time <= start + FrameTolerance)
        {
            var cue = timeline.Cameras[_cueIndex++];
            start = Math.Max(start, cue.Time);
            if (cue.Type == "camera") _selector = Named(cue.Value);
            else if ((_bank!.Notes.Count > 0 ? _bank.Notes.GetValueOrDefault(int.Parse(cue.Value)) : PortableNote(int.Parse(cue.Value))) is { } selector)
            {
                if (selector.Array.Length == 0) _enabled = selector.Enable;
                else { _selector = selector; _enabled = true; }
            }
            if (cue.Duration > 0.2) length = cue.Duration;
        }
        if (!_enabled && _cuts.Count > 0)
        {
            _next = NextCue(timeline, double.PositiveInfinity);
            return;
        }
        var selected = _mode == "auto" ? _selector : Named(_mode);
        var array = _bank!.Arrays.GetValueOrDefault(selected.Array, _bank.Arrays["cameras"]);
        var node = Node(timeline, start);
        var excluded = selected.Type.Length == 0 && selected.Name.Length == 0 && _cuts.Count > 0 ? LastType(_cuts[^1].Shot) : "";
        var matches = Enumerable.Range(0, array.Length).Where(i => (array[i].Nodes.Length == 0 || array[i].Nodes.Contains(node)) &&
            (selected.Name.Length == 0 || array[i].Name == selected.Name) && (selected.Type.Length == 0 || array[i].Type == selected.Type) &&
            (excluded.Length == 0 || array[i].Type != excluded)).ToArray();
        if (_forcedShot.Length > 0 && matches.Any(i => array[i].Name == _forcedShot)) matches = matches.Where(i => array[i].Name == _forcedShot).ToArray();
        var shot = array[matches.Length == 0 ? 0 : matches[_random.Next(matches.Length)]];
        var duration = length;
        if (duration <= 0)
        {
            duration = 3 + _random.Next(6);
            var downbeat = timeline.Cues.FirstOrDefault(c => c.Type == "gh3-note" && c.Value == "58" && c.Time > start + duration);
            if (downbeat != null) duration = downbeat.Time - start;
        }
        _cuts.Add(new(start, Math.Max(0.001, duration), shot, Zoom(shot)));
        _next = _enabled ? start + duration : double.PositiveInfinity;
        if (_mode == "auto") _next = NextCue(timeline, _next);
        if (_next <= start) _next = start + 0.001;
    }

    // CameraCuts_HandCam draws the zoom after CameraCuts_CalcTime, and only for LongShot and Mid shots.
    private int Zoom(OriginalCameraShot shot) => shot.Control == "HandCam" && shot.Type is "longshot" or "mid" ? _random.Next(101) : -1;

    // The exporter writes "normal" for shots without a GH3 Type, and GH3 then stores None as the last type.
    private static string LastType(OriginalCameraShot shot) => shot.Type == "normal" ? "" : shot.Type;

    private static string Node(StageTimeline timeline, double time)
    {
        var walks = timeline.Track("walk", "guitarist");
        var walk = StageTimeline.UpperBound(walks, time, c => c.Time) - 1;
        return walk >= 0 ? walks[walk].Value : "guitarist_start";
    }

    private double NextCue(StageTimeline timeline, double next) =>
        _cueIndex < timeline.Cameras.Length ? Math.Min(next, timeline.Cameras[_cueIndex].Time) : next;

    private Vector3 OrbitPosition(OriginalCameraShot shot, double time)
    {
        var point = shot.Points[0];
        var angle = (float)(time * shot.AngularSpeed % 360) * MathF.PI / 180;
        var height = shot.HeightEase[1] + (shot.HeightEase[0] - shot.HeightEase[1]) * (float)Math.Exp(-time * shot.HeightEase[2]);
        return Anchor(point.LockTo, point.LockBone).Position + Vector3.Transform(Vector(point.Position),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, angle)) + Vector3.UnitY * height;
    }

    private (Vector3 Position, Quaternion Rotation, float Fov) Resolve(OriginalCameraPoint point, Vector3? orbit = null, float? fov = null, float spin = 0)
    {
        var anchor = Anchor(point.LockTo, point.LockBone);
        var frame = point.LockTo == "world" ? anchor.Rotation : anchor.Rotation * Quaternion.CreateFromAxisAngle(Vector3.UnitY, spin);
        var position = orbit ?? anchor.Position + Vector3.Transform(Vector(point.Position), frame);
        var q = point.Rotation;
        var rotation = Quaternion.Normalize(frame * new Quaternion(q[0], q[1], q[2], q[3]));
        fov ??= point.FieldOfView;
        if (point.LookAt.Length > 0)
        {
            // GH3.exe 0x4bb9d0: the look-at basis keeps the up axis of the authored Quat, so the shot keeps its roll.
            var target = Anchor(point.LookAt, point.LookBone).Position;
            var forward = Vector3.Normalize(target - position);
            var right = Vector3.Cross(Vector3.Transform(Vector3.UnitY, rotation), forward);
            if (right.LengthSquared() > 0.00001f)
            {
                right = Vector3.Normalize(right);
                var up = Vector3.Cross(forward, right);
                var look = Quaternion.CreateFromRotationMatrix(new Matrix4x4(right.X, right.Y, right.Z, 0,
                    up.X, up.Y, up.Z, 0, forward.X, forward.Y, forward.Z, 0, 0, 0, 0, 1));
                // GH3.exe 0x4b8a60 turns the view by ScreenOffset times the half field of view (pitch also divided by the aspect).
                // The local pitch comes before the yaw; the other order rolls the horizon by yaw × pitch.
                var half = fov.Value * MathF.PI / 360;
                var yaw = -point.ScreenOffset[0] * half;
                var pitch = -point.ScreenOffset[1] * half / Aspect;
                rotation = look * Quaternion.CreateFromAxisAngle(Vector3.UnitX, pitch) * Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
            }
        }
        return (position, rotation, fov.Value);
    }

    private BandPose Anchor(string role, string bone) => _anchor?.Invoke(role, bone) ??
        new BandPose(_band.GetValueOrDefault(role, Vector3.Zero), Quaternion.Identity);

    // Custom stages have no GH3 note table; GH3 camera notes (also the mapped Rock Band cuts) select their portable groups.
    private static OriginalCameraSelector? PortableNote(int note) => note switch
    {
        77 => new() { Enable = true },
        78 => new() { Enable = false },
        84 or 86 => new() { Array = "cameras", Type = "closeup" },
        85 => new() { Array = "cameras", Type = "mid" },
        87 or 110 => Named("wide"),
        88 or 104 => Named("singer"),
        89 or 92 or 100 => Named("drummer"),
        97 => Named("bassist"),
        81 or 90 or 95 or 96 or 99 => Named("guitarist"),
        82 or 91 or 93 or 94 or 103 => Named("orbit"),
        >= 79 and <= 117 => Named("default"),
        _ => null
    };

    private static OriginalCameraSelector Named(string name) => name switch
    {
        "wide" => new() { Array = "cameras_wide", Type = "stage" },
        "guitarist" => new() { Array = "cameras_guitar_closeup" },
        "default" => new() { Array = "cameras" },
        _ => new() { Array = "cameras_" + name },
    };
    private static float Smooth(float value) => value * value * (3 - 2 * value);
    private static Vector3 Vector(float[] value) => new(value[0], value[1], value[2]);
    public static bool ValidShot(string shot) => shot is "wide" or "singer" or "guitarist" or "bassist" or "drummer" or "orbit" or
        "intro" or "win" or "default";
}
