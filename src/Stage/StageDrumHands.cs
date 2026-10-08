using System.Numerics;

namespace Gh3Skin;

internal static class StageDrumHands
{
    private const float MinimumHingeCosine = -0.97f;
    private const float MaximumHingeCosine = 0.97f;
    private static readonly Vector3 HingeAxis = -Vector3.UnitZ;

    public static void Apply(int[] parents, BandPose[] pose)
    {
        if (pose.Length != parents.Length || pose.Length <= 84 || parents[12] != 11 || parents[13] != 12 || parents[16] != 15 || parents[17] != 16)
            throw new InvalidDataException("Invalid drummer arm skeleton");
        Solve(parents, pose, 11, 12, 13, 72, 83);
        Solve(parents, pose, 15, 16, 17, 73, 84);
    }

    private static void Solve(int[] parents, BandPose[] pose, int upper, int lower, int hand, int target, int stick)
    {
        var shoulder = World(parents, pose, upper);
        var elbow = World(parents, pose, lower);
        var wrist = World(parents, pose, hand);
        var goal = World(parents, pose, target);
        var first = Vector3.Distance(shoulder.Position, elbow.Position);
        var second = Vector3.Distance(elbow.Position, wrist.Position);
        if (first < 0.00001f || second < 0.00001f) return;
        var squaredLengths = first * first + second * second;
        var doubledProduct = 2 * first * second;
        var distance = Math.Clamp(Vector3.Distance(shoulder.Position, goal.Position), MathF.Sqrt(squaredLengths + doubledProduct * MinimumHingeCosine),
            MathF.Sqrt(squaredLengths + doubledProduct * MaximumHingeCosine));
        var hinge = Vector3.Normalize(Vector3.Transform(HingeAxis, elbow.Rotation));
        var toShoulder = shoulder.Position - elbow.Position;
        var toWrist = wrist.Position - elbow.Position;
        var shoulderAlong = Vector3.Dot(toShoulder, hinge);
        var wristAlong = Vector3.Dot(toWrist, hinge);
        var shoulderPlane = toShoulder - hinge * shoulderAlong;
        var wristPlane = toWrist - hinge * wristAlong;
        var planar = shoulderPlane.Length() * wristPlane.Length();
        if (planar > 0.000001f)
        {
            var current = MathF.Atan2(Vector3.Dot(Vector3.Cross(shoulderPlane, wristPlane), hinge), Vector3.Dot(shoulderPlane, wristPlane));
            var cosine = (squaredLengths - distance * distance - 2 * shoulderAlong * wristAlong) / (2 * planar);
            var desired = MathF.Acos(Math.Clamp(cosine, -1, 1)) * (current < 0 ? -1 : 1);
            SetRotation(parents, pose, lower, Quaternion.CreateFromAxisAngle(hinge, desired - current) * elbow.Rotation);
        }
        wrist = World(parents, pose, hand);
        if ((goal.Position - shoulder.Position).LengthSquared() > 0.0000000001f)
            SetRotation(parents, pose, upper, Turn(wrist.Position - shoulder.Position, goal.Position - shoulder.Position) * shoulder.Rotation);
        SetRotation(parents, pose, hand, goal.Rotation);
        var correction = World(parents, pose, hand).Position - goal.Position;
        var stickParent = World(parents, pose, parents[stick]);
        pose[stick] = pose[stick] with { Position = pose[stick].Position + Vector3.Transform(correction, Quaternion.Inverse(stickParent.Rotation)) };
    }

    private static BandPose World(int[] parents, BandPose[] pose, int bone)
    {
        if (bone < 0) return new(Vector3.Zero, Quaternion.Identity);
        var parent = World(parents, pose, parents[bone]);
        return new(parent.Position + Vector3.Transform(pose[bone].Position, parent.Rotation), parent.Rotation * pose[bone].Rotation);
    }

    private static void SetRotation(int[] parents, BandPose[] pose, int bone, Quaternion rotation) =>
        pose[bone] = pose[bone] with { Rotation = Quaternion.Normalize(Quaternion.Inverse(World(parents, pose, parents[bone]).Rotation) * rotation) };

    private static Quaternion Turn(Vector3 from, Vector3 to)
    {
        from = Vector3.Normalize(from);
        to = Vector3.Normalize(to);
        var dot = Math.Clamp(Vector3.Dot(from, to), -1, 1);
        return dot < -0.99999f ? Quaternion.CreateFromAxisAngle(Vector3.Normalize(Perpendicular(from)), MathF.PI) :
            Quaternion.Normalize(new Quaternion(Vector3.Cross(from, to), 1 + dot));
    }

    private static Vector3 Perpendicular(Vector3 direction) => Vector3.Cross(direction, Math.Abs(direction.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
}
