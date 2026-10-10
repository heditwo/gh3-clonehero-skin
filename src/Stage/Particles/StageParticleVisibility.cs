using System.Numerics;

namespace Gh3Skin;

internal sealed class StageParticleVisibility
{
    public Vector3 Center { get; private set; }
    public float Radius { get; private set; }
    public bool Pending { get; set; } = true;
    public bool Previous { get; private set; } = true;

    public void Register(Vector3 center, float radius)
    {
        static float Quantize(float value) => unchecked((short)(int)(value * StageParticleConstants.VisibilitySteps)) / (float)StageParticleConstants.VisibilitySteps;
        const int limit = StageParticleConstants.VisibilityLimit;
        Center = new(Quantize(Math.Clamp(center.X, -limit, limit)), Quantize(Math.Clamp(center.Y, -limit, limit)), Quantize(Math.Clamp(center.Z, -limit, limit)));
        Radius = Quantize(Math.Min(radius, limit));
    }

    public bool Test(IReadOnlyList<Vector4> planes)
    {
        for (var i = 0; i < planes.Count; i++)
            if (planes[i] is var plane && (-plane.W + plane.Z * Center.Z) + plane.Y * Center.Y + plane.X * Center.X < -Radius) return false;
        return true;
    }

    public static Vector4[] Planes(StageCameraPose pose, float aspect, float near, float far)
    {
        var forward = pose.Rotation is { } rotation ? Vector3.Transform(Vector3.UnitZ, rotation) : Vector3.Normalize(pose.Target - pose.Position);
        var right = pose.Rotation is { } orientation ? Vector3.Transform(Vector3.UnitX, orientation) : Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
        var up = Vector3.Cross(forward, right);
        var vertical = MathF.Tan(pose.FieldOfView * MathF.PI / 360);
        var horizontal = vertical * aspect;
        Vector4 Plane(Vector3 normal, float distance = 0)
        {
            normal = Vector3.Normalize(normal);
            return new(normal, Vector3.Dot(normal, pose.Position) + distance);
        }
        return new[] { Plane(forward, near), Plane(-forward, -far), Plane(right + forward * horizontal), Plane(-right + forward * horizontal),
            Plane(up + forward * vertical), Plane(-up + forward * vertical) };
    }

    public bool Consume()
    {
        Previous = Pending;
        Pending = false;
        return Previous;
    }
}
