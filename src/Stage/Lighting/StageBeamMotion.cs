using System.Numerics;

namespace Gh3Skin;

internal sealed class StageBeamMotion
{
    private const float SameRotationDot = 0.999999f;
    private const double MinimumDuration = 0.3;
    private const double AngularSpeed = Math.PI;
    private readonly double _time;
    private readonly double _duration;
    private readonly Vector3 _fromPosition;
    private readonly Quaternion _fromRotation;
    private readonly Vector3 _velocity;
    private readonly Quaternion _rotationVelocity;
    public Vector3 Position { get; }
    public Quaternion Rotation { get; }

    public StageBeamMotion(StageBeamValue value)
    {
        Position = _fromPosition = value.Position;
        Rotation = _fromRotation = value.Rotation;
    }

    private StageBeamMotion(double time, double blend, StageBeamMotion previous, StageBeamValue target)
    {
        _time = time;
        (_fromPosition, _fromRotation, _velocity, _rotationVelocity) = previous.Sample(time);
        Position = target.Position;
        Rotation = Quaternion.Dot(_fromRotation, target.Rotation) < 0 ? -target.Rotation : target.Rotation;
        var angle = 2 * Math.Acos(Math.Clamp(Quaternion.Dot(_fromRotation, Rotation), 0, 1));
        _duration = Math.Max(Math.Max(MinimumDuration, blend), 2 * angle / AngularSpeed);
    }

    public StageBeamMotion Retarget(double time, double blend, StageBeamValue target) =>
        Vector3.DistanceSquared(Position, target.Position) < StageTolerance.ZeroLengthSquared && Math.Abs(Quaternion.Dot(Rotation, target.Rotation)) > SameRotationDot
            ? this : new(time, blend, this, target);

    public (Vector3 Position, Quaternion Rotation, Vector3 Velocity, Quaternion RotationVelocity) Sample(double time)
    {
        if (_duration == 0 || time >= _time + _duration) return (Position, Rotation, Vector3.Zero, default);
        var t = (float)Math.Clamp((time - _time) / _duration, 0, 1);
        var duration = (float)_duration;
        var a = 2 * t * t * t - 3 * t * t + 1;
        var b = t * t * t - 2 * t * t + t;
        var c = 1 - a;
        var da = (6 * t * t - 6 * t) / duration;
        var db = 3 * t * t - 4 * t + 1;
        var position = _fromPosition * a + _velocity * (b * duration) + Position * c;
        var velocity = (_fromPosition - Position) * da + _velocity * db;
        var raw = _fromRotation * a + _rotationVelocity * (b * duration) + Rotation * c;
        var derivative = (_fromRotation - Rotation) * da + _rotationVelocity * db;
        var length = raw.Length();
        var rotation = raw * (1 / length);
        var rotationVelocity = (derivative - rotation * Quaternion.Dot(rotation, derivative)) * (1 / length);
        return (position, rotation, velocity, rotationVelocity);
    }
}
