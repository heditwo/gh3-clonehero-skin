using System.Numerics;

namespace Gh3Skin;

internal static class StageDrumHands
{
    private const float MinimumHingeCosine = -0.97f;
    private const float MaximumHingeCosine = 0.97f;

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
        var toward = goal.Position - shoulder.Position;
        var distance = toward.Length();
        var direction = distance > 0.00001f ? toward / distance : Vector3.Normalize(elbow.Position - shoulder.Position);
        var squaredLengths = first * first + second * second;
        var doubledProduct = 2 * first * second;
        distance = Math.Clamp(distance, MathF.Sqrt(squaredLengths + doubledProduct * MinimumHingeCosine),
            MathF.Sqrt(squaredLengths + doubledProduct * MaximumHingeCosine));
        var along = (first * first - second * second + distance * distance) / (2 * distance);
        var height = MathF.Sqrt(Math.Max(0, first * first - along * along));
        var hinge = Vector3.Transform(-Vector3.UnitZ, elbow.Rotation);
        var pole = Vector3.Cross(hinge, direction);
        var bend = elbow.Position - shoulder.Position;
        bend -= direction * Vector3.Dot(bend, direction);
        if (pole.LengthSquared() < 0.000001f) pole = bend;
        if (pole.LengthSquared() < 0.000001f) pole = Perpendicular(direction);
        if (Vector3.Dot(pole, bend) < 0) pole = -pole;
        var elbowGoal = shoulder.Position + direction * along + Vector3.Normalize(pole) * height;
        var wristGoal = shoulder.Position + direction * distance;
        SetRotation(parents, pose, upper, Turn(elbow.Position - shoulder.Position, elbowGoal - shoulder.Position) * shoulder.Rotation);
        elbow = World(parents, pose, lower);
        wrist = World(parents, pose, hand);
        SetRotation(parents, pose, lower, Turn(wrist.Position - elbow.Position, wristGoal - elbow.Position) * elbow.Rotation);
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
