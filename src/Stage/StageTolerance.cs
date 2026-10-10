namespace Gh3Skin;

// Shared numeric tolerances of the vector math in the band solvers (hair, retarget, IK).
internal static class StageTolerance
{
    // A squared length below this is zero: the vector has no direction.
    public const float ZeroLengthSquared = 1e-10f;
    public const float TinyLengthSquared = 1e-12f;
    public const float SmallLengthSquared = 1e-8f;
    // A helper axis for a cross product: when a unit vector's Y (or X) component is above this, use the other axis.
    public const float NearAxis = 0.9f;
}
