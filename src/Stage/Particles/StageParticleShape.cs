using System.Numerics;

namespace Gh3Skin;

internal readonly record struct StageParticleBirth(Vector3 Position, Vector3 Velocity, float Rotation, float ColorMix, float Life, float PathFraction = 0);

internal sealed class StageParticleShape
{
    public Vector3 Elevation { get; private set; }
    public Vector3 Sweep { get; private set; }
    public Vector3 Target { get; private set; }
    public Matrix4x4 TargetBasis { get; private set; } = Matrix4x4.Identity;
    public Vector3 Angle { get; private set; }
    public Matrix4x4 AngleBasis { get; private set; } = Matrix4x4.Identity;
    public bool Radiate { get; init; }
    public bool Circular { get; set; }
    public bool RandomAngle { get; set; }
    public float? TargetPosition { get; init; }

    public void SourceSpread(float degrees)
    {
        var half = degrees * .0027777778450399637f;
        SetElevation(-half, half);
        SetSweep(0, 2);
    }

    public void SetElevation(float min, float max) => Elevation = new Vector3(min, max, max - min) * MathF.PI;
    public void SetSweep(float min, float max) => Sweep = new Vector3(min, max, max - min) * MathF.PI;

    public void SetTarget(Vector3 target)
    {
        Target = target;
        TargetBasis = CreateTargetBasis(target);
    }

    private static Matrix4x4 CreateTargetBasis(Vector3 target)
    {
        if (target == Vector3.Zero) return Matrix4x4.Identity;
        var forward = Vector3.Normalize(target);
        var right = Vector3.Cross(Math.Abs(forward.Y) > .99f ? Vector3.UnitX : Vector3.UnitY, forward);
        var up = Vector3.Cross(right, forward);
        return new(right.X, right.Y, right.Z, 0, up.X, up.Y, up.Z, 0, forward.X, forward.Y, forward.Z, 0, 0, 0, 0, 1);
    }

    public void SetAngle(Vector3 degrees)
    {
        Angle = degrees * .01745329238474369f;
        AngleBasis = Matrix4x4.CreateRotationX(Angle.X) * Matrix4x4.CreateRotationY(Angle.Y) * Matrix4x4.CreateRotationZ(Angle.Z);
    }

    public bool Execute(uint command, Gh3QbValue parameters)
    {
        bool Is(string name) => command == Gh3Checksum.Value(name);
        float Number(string name) => (float)StageParticleDefinition.Number(parameters, name, 0, -StageLimits.MaximumMagnitude, StageLimits.MaximumMagnitude);
        Vector3 Vector(string name) => parameters.Field(name) != null ? StageParticleDefinition.Vector(parameters, name, Vector3.Zero, false) : new(Number("x"), Number("y"), Number("z"));
        if (Is("SetAngleSpread"))
        {
            if (parameters.Field("spread") != null)
            {
                var half = Number("spread") * .5f;
                SetElevation(-half, half);
                SetSweep(-half, half);
            }
            else
            {
                if (parameters.Field("elevation") is { } elevation) { var pair = Pair(elevation); SetElevation(pair.X, pair.Y); }
                if (parameters.Field("sweep") is { } sweep) { var pair = Pair(sweep); SetSweep(pair.X, pair.Y); }
            }
        }
        else if (Is("SetEmitTarget"))
        {
            var target = Vector("target");
            if (target != Vector3.UnitY) SetTarget(target);
        }
        else if (Is("SetEmitAngle")) SetAngle(Vector("angle"));
        else if (Is("SetCircularEmit")) Circular = Number("circular") > 0;
        else if (Is("SetRandomAngle")) RandomAngle = Number("random") > 0;
        else return false;
        return true;
    }

    public StageParticleBirth Emit(Vector3 range, Vector2 speed, Vector2 life, Func<int, int> random, Vector3 emitterVelocity = default, float velocityWeight = 0, bool fillPath = false)
    {
        if (velocityWeight != 0) TargetBasis = CreateTargetBasis(Target + emitterVelocity * velocityWeight);
        var targetBasis = Target == Vector3.Zero ? Matrix4x4.Identity : TargetBasis;
        Vector3 position;
        Vector3 direction;
        if (Radiate && TargetPosition == null)
        {
            var elevation = random(10000) * .0001f;
            var sweep = random(10000) * .0001f;
            direction = Spherical(Elevation.X + Elevation.Z * elevation, Sweep.X + Sweep.Z * sweep);
            position = Spherical((Elevation.Y + Elevation.X) * .5f - MathF.PI + elevation * (2 * MathF.PI),
                (Sweep.Y + Sweep.X) * .5f - MathF.PI + sweep * (2 * MathF.PI));
            direction = Transform(direction, targetBasis);
            position = Transform(position, targetBasis);
            var radius = random(10000);
            var extent = Math.Max(Math.Max(Math.Abs(position.X), Math.Abs(position.Y)), Math.Abs(position.Z));
            position *= (float)((double)(1f / extent) * radius * .0001f);
            if (Circular)
            {
                var length = MathF.Sqrt(position.X * position.X + position.Y * position.Y);
                if (length > 0) { position.X /= length; position.Y /= length; }
            }
            position *= range;
        }
        else
        {
            position = new((random(10000) - 5000) * .0002f, (random(10000) - 5000) * .0002f, (random(10000) - 5000) * .0002f);
            if (Circular)
            {
                var length = MathF.Sqrt(position.X * position.X + position.Y * position.Y);
                var inverse = length > .00001f ? 1 / length : 0;
                position.X *= Math.Abs(position.X) * inverse;
                position.Y *= Math.Abs(position.Y) * inverse;
            }
            position *= range;
            if (TargetPosition is { } targetPosition)
            {
                direction = Transform(new(0, 0, targetPosition), targetBasis) - position;
                if (direction.Length() > 0) direction = Vector3.Normalize(direction);
            }
            else
            {
                direction = Spherical(random(10000) * Elevation.Z * .0001f + Elevation.X, random(10000) * Sweep.Z * .0001f + Sweep.X);
                direction = Transform(direction, targetBasis);
            }
        }
        position = Transform(position, AngleBasis);
        direction = Transform(direction, AngleBasis);
        var velocity = direction * (random(10000) * (speed.Y - speed.X) * .0001f + speed.X);
        var pathFraction = fillPath ? random(10000) * .0001f : 0;
        var rotation = random(10000) * .00062831852119416f;
        var color = random(10000) * .0001f;
        var lifetime = random(10000) * (life.Y - life.X) * .0001f + life.X;
        return new(new(position.X, position.Y, -position.Z), new(velocity.X, velocity.Y, -velocity.Z), rotation, color, lifetime, pathFraction);
    }

    private static Vector3 Spherical(float elevation, float sweep)
    {
        var sin = (float)Math.Sin(elevation);
        return new(sin * (float)Math.Cos(sweep), sin * (float)Math.Sin(sweep), (float)Math.Cos(elevation));
    }

    private static Vector3 Transform(Vector3 value, Matrix4x4 matrix) => new(
        value.Z * matrix.M31 + value.Y * matrix.M21 + value.X * matrix.M11,
        value.Z * matrix.M32 + value.Y * matrix.M22 + value.X * matrix.M12,
        value.Z * matrix.M33 + value.Y * matrix.M23 + value.X * matrix.M13);

    private static Vector2 Pair(Gh3QbValue value)
    {
        if (value.Data is not float[] values || values.Length != 2 || values.Any(value => !float.IsFinite(value) || Math.Abs(value) > StageLimits.MaximumMagnitude))
            throw new InvalidDataException("Invalid particle angle range");
        return new(values[0], values[1]);
    }
}
