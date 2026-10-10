using System.Numerics;

namespace Gh3Skin;

// The scripted venue objects: placement, motion, visibility, and scaling (no Unity).

internal readonly record struct StageObjectPose(Vector3 Position, Quaternion Rotation, bool Visible)
{
    public Vector3 Scale { get; init; } = Vector3.One;
    public Vector3 Velocity { get; init; } = Vector3.Zero;
}

internal sealed class StageObjectPlacement : StagePlacement
{
    public bool Created { get; set; } = true;
    public string Script { get; set; } = "";
    public float[] Scale { get; set; } = new[] { 1f, 1, 1 };
}

internal sealed class StageObjectState
{
    private sealed record Motion(Vector3 From, Vector3 To, double Start, double End)
    {
        public Vector3 At(double time) => Vector3.Lerp(From, To, (float)Math.Clamp(End <= Start ? 1 : (time - Start) / (End - Start), 0, 1));
        public Vector3 VelocityAt(double time) => time >= Start && time < End ? (To - From) / (float)(End - Start) : Vector3.Zero;
    }
    private sealed record AxisMotion(Quaternion From, Vector3 Axis, double Angle, double Speed, double Start, double End)
    {
        public Quaternion At(double time)
        {
            var angle = double.IsPositiveInfinity(End) ? Speed * Math.Max(0, time - Start) :
                Angle * Math.Clamp(End <= Start ? 1 : (time - Start) / (End - Start), 0, 1);
            return Quaternion.Normalize(From * Quaternion.CreateFromAxisAngle(Axis, (float)(angle % 360 * Math.PI / 180)));
        }
    }
    private readonly Vector3 _origin;
    private readonly Quaternion _rotation;
    private readonly Vector3 _scale;
    private Motion _position;
    private Motion _angles = new(Vector3.Zero, Vector3.Zero, 0, 0);
    private Motion _scaling = new(Vector3.One, Vector3.One, 0, 0);
    private AxisMotion? _axis;
    public bool Exists { get; private set; } = true;
    public bool Visible { get; private set; } = true;
    public bool PositionChanged { get; private set; }
    public bool RotationChanged { get; private set; }
    public double RotationTime { get; private set; } = double.NegativeInfinity;

    public StageObjectState(StageObjectPlacement placement)
    {
        if (!placement.Valid || placement.Scale.Length != 3 || placement.Scale.Any(value => !float.IsFinite(value) || value <= 0))
            throw new InvalidDataException("Invalid script object placement");
        _origin = new(placement.Position[0], placement.Position[1], placement.Position[2]);
        _rotation = new(placement.Rotation[0], placement.Rotation[1], placement.Rotation[2], placement.Rotation[3]);
        _scale = new(placement.Scale[0], placement.Scale[1], placement.Scale[2]);
        _scaling = new(_scale, _scale, 0, 0);
        var angles = Angles(_rotation);
        _angles = new(angles, angles, 0, 0);
        _position = new(_origin, _origin, 0, 0);
        Exists = placement.Created;
    }

    public StageObjectPose Sample(double time)
    {
        var angles = _angles.At(time) * (MathF.PI / 180);
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angles.Z) * Quaternion.CreateFromAxisAngle(Vector3.UnitY, -angles.Y) *
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, -angles.X);
        return new(_position.At(time), _axis?.At(time) ?? (RotationChanged ? Quaternion.Normalize(rotation) : _rotation), Exists && Visible) {
            Scale = _scaling.At(time), Velocity = _position.VelocityAt(time) };
    }

    public void Pulse(Vector3 scale, float amount, double duration, double time) => _scaling = new(scale * amount, scale, time, time + duration);

    private static Vector3 Angles(Quaternion rotation)
    {
        var source = new Quaternion(-rotation.X, -rotation.Y, rotation.Z, rotation.W);
        var x = Vector3.Transform(Vector3.UnitX, source);
        var y = Vector3.Transform(Vector3.UnitY, source);
        var z = Vector3.Transform(Vector3.UnitZ, source);
        var pitch = MathF.Atan2(-x.Z, MathF.Sqrt(x.X * x.X + x.Y * x.Y));
        // GH3: the native gimbal-lock threshold of the Euler decomposition.
        return new Vector3(Math.Abs(MathF.Cos(pitch)) > .00001f ? MathF.Atan2(y.Z, z.Z) : MathF.Atan2(-z.Y, y.Y),
            pitch, Math.Abs(MathF.Cos(pitch)) > .00001f ? MathF.Atan2(x.Y, x.X) : 0) * (180 / MathF.PI);
    }

    public Gh3ScriptCallResult? Execute(uint command, Gh3QbValue parameters, double time)
    {
        bool Is(string name) => command == Gh3Checksum.Value(name);
        Gh3QbValue? Field(string name) => parameters.Field(name);
        Vector3 Vector(Gh3QbValue? value)
        {
            if (value?.Data is not float[] { Length: 3 } xyz || xyz.Any(x => !float.IsFinite(x))) throw new InvalidDataException("Object motion needs a finite vector");
            return new(xyz[0], xyz[1], xyz[2]);
        }
        Gh3QbValue? Unnamed() => parameters.Fields.FirstOrDefault(item => item.Name == 0 && item.Value.Type == Gh3QbType.Vector)?.Value;
        double Duration(Vector3 from, Vector3 to)
        {
            var seconds = StageSoundBank.Number(Field("time"), 0);
            if (Field("speed") is { } speed)
            {
                var rate = Gh3ScriptRuntime.Numeric(speed);
                if (rate <= 0) throw new InvalidDataException("Object motion speed must be positive");
                seconds = Vector3.Distance(from, to) / rate;
            }
            if (!double.IsFinite(seconds) || seconds is < 0 or > StageLimits.MaximumSeconds) throw new InvalidDataException("Invalid object motion duration");
            return seconds;
        }
        if (Is("Hide") || Is("Unhide")) { Visible = Is("Unhide"); return new(true); }
        if (Is("Create"))
        {
            Exists = true; Visible = true; PositionChanged = false; RotationChanged = false; RotationTime = double.NegativeInfinity;
            _position = new(_origin, _origin, time, time);
            var angles = Angles(_rotation); _angles = new(angles, angles, time, time);
            _scaling = new(_scale, _scale, time, time);
            _axis = null;
            return new(true);
        }
        if (Is("Kill")) { Exists = false; return new(true); }
        if (Is("Obj_GetPosition"))
        {
            var p = _position.At(time);
            var position = new Gh3QbValue(Gh3QbType.Vector, -1, new[] { p.X, p.Y, -p.Z });
            return new(true, new[] { new Gh3QbField(Gh3Checksum.Value("position"), position), new Gh3QbField(Gh3Checksum.Value("pos"), position) });
        }
        if (Is("Obj_SetPosition") || Is("Obj_MoveToPos") || Is("Obj_MoveToRelPos"))
        {
            var p = Vector(Field("position") ?? Field("pos") ?? Unnamed());
            p.Z = -p.Z;
            var start = _position.At(time);
            if (Is("Obj_MoveToRelPos")) p = start + Vector3.Transform(p, Sample(time).Rotation);
            _position = new(start, p, time, time + (Is("Obj_SetPosition") ? 0 : Duration(start, p)));
            PositionChanged = true;
            return new(true);
        }
        if (Is("Obj_Rotate"))
        {
            var start = _axis == null ? _angles.At(time) : Angles(_axis.At(time));
            var target = Field("absolute") is { } absolute ? Vector(absolute) : start + Vector(Field("relative") ?? Unnamed());
            _angles = new(start, target, time, time + Duration(start, target));
            RotationChanged = true;
            RotationTime = time;
            _axis = null;
            return new(true);
        }
        if (Is("Obj_RotX") || Is("Obj_RotY") || Is("Obj_RotZ"))
        {
            var axis = Is("Obj_RotX") ? -Vector3.UnitX : Is("Obj_RotY") ? -Vector3.UnitY : Vector3.UnitZ;
            var angle = StageSoundBank.Number(Field("angle"), 0);
            var speed = StageSoundBank.Number(Field("speed"), 0);
            var duration = StageSoundBank.Number(Field("time"), 0);
            if (!double.IsFinite(angle) || !double.IsFinite(speed) || !double.IsFinite(duration) || duration is < 0 or > StageLimits.MaximumSeconds)
                throw new InvalidDataException("Invalid axis rotation");
            if (Field("angle") != null && Field("speed") != null)
            {
                if (speed == 0 && angle != 0) throw new InvalidDataException("A finite rotation needs a non-zero speed");
                duration = speed == 0 ? 0 : Math.Abs(angle / speed);
            }
            var end = Field("angle") != null ? time + duration : speed == 0 ? time : double.PositiveInfinity;
            _axis = new(Sample(time).Rotation, axis, angle, speed, time, end);
            RotationChanged = true;
            RotationTime = time;
            return new(true);
        }
        if (Is("Obj_StopMoving")) { var p = _position.At(time); _position = new(p, p, time, time); return new(true); }
        if (Is("Obj_StopRotating"))
        {
            _axis = new(Sample(time).Rotation, Vector3.UnitY, 0, 0, time, time);
            return new(true);
        }
        if (Is("Obj_EnableScaling")) return new(true);
        if (Is("Obj_GetScaling"))
        {
            var s = _scaling.At(time);
            return new(true, new[] { new Gh3QbField(Gh3Checksum.Value("scaling"), new(Gh3QbType.Vector, -1, new[] { s.X, s.Y, s.Z })) });
        }
        if (Is("Obj_ApplyScaling") || Is("Obj_MorphScaling"))
        {
            var target = Vector(Field(Is("Obj_ApplyScaling") ? "scale" : "target_scale"));
            var duration = StageSoundBank.Number(Field("blend_duration"), 0);
            if (!double.IsFinite(duration) || duration is < 0 or > StageLimits.MaximumSeconds) throw new InvalidDataException("Invalid object scaling duration");
            _scaling = new(_scaling.At(time), target, time, time + duration);
            return new(true);
        }
        if (Is("Obj_WaitMove")) return new(true, Wait: Math.Max(0, _position.End - time));
        if (Is("Obj_WaitRotate"))
        {
            var end = _axis?.End ?? _angles.End;
            if (double.IsPositiveInfinity(end)) throw new InvalidDataException("Cannot wait for an endless object rotation");
            return new(true, Wait: Math.Max(0, end - time));
        }
        return null;
    }
}
