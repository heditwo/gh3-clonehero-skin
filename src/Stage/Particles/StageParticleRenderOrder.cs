using System.Numerics;

namespace Gh3Skin;

internal static class StageParticleRenderOrder
{
    public static float DistanceScale(float near, float far) => 4096f / (far - near);

    public static uint Key(byte group, float order, bool transparent, byte material, Vector3 position, Vector3 camera, float distanceScale)
    {
        var x = (double)camera.X - position.X;
        var y = (double)(camera.Y - position.Y);
        var z = (double)(camera.Z - position.Z);
        var distance = unchecked((uint)(long)(Math.Sqrt((x * x + y * y) + z * z) * distanceScale));
        distance = Math.Min(distance, 4095u);
        var drawOrder = unchecked((byte)(long)order);
        return transparent ? ((uint)group << 30) | 0x30000000u | ((uint)drawOrder << 20) | ((unchecked(0u - distance) & 4095u) << 8) | material :
            ((uint)group << 30) | ((uint)material << 20) | ((uint)drawOrder << 12) | distance;
    }

    public static int[] Sort(IReadOnlyList<uint> keys) => Enumerable.Range(0, keys.Count).OrderBy(index => keys[index]).ThenByDescending(index => index).ToArray();
}
