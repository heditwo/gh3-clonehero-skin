using System.Numerics;

namespace Gh3Skin;

// GH3's particle sort key: group, transparency, draw order, depth, and material.

internal static class StageParticleRenderOrder
{
    public static float DistanceScale(float near, float far) => StageParticleConstants.DepthSteps / (far - near);

    public static uint Key(byte group, float order, bool transparent, byte material, Vector3 position, Vector3 camera, float distanceScale)
    {
        var x = (double)camera.X - position.X;
        var y = (double)(camera.Y - position.Y);
        var z = (double)(camera.Z - position.Z);
        var distance = unchecked((uint)(long)(Math.Sqrt((x * x + y * y) + z * z) * distanceScale));
        distance = Math.Min(distance, StageParticleConstants.MaximumDepth);
        var drawOrder = unchecked((byte)(long)order);
        // GH3: the 32-bit sort key: group (2 bits), the transparent bits, draw order, depth (back to front), material;
        // opaque keys sort front to back.
        return transparent ? ((uint)group << 30) | StageParticleConstants.TransparentKeyBits | ((uint)drawOrder << 20) | ((unchecked(0u - distance) & StageParticleConstants.MaximumDepth) << 8) | material :
            ((uint)group << 30) | ((uint)material << 20) | ((uint)drawOrder << 12) | distance;
    }

    public static int[] Sort(IReadOnlyList<uint> keys) => Enumerable.Range(0, keys.Count).OrderBy(index => keys[index]).ThenByDescending(index => index).ToArray();
}
