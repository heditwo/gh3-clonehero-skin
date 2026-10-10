using System.Numerics;

namespace Gh3Skin;

// The lights that light a particle system: GH3's light choice and its spherical harmonics.

internal sealed record StageParticleLight(int Type, Vector4 Color, Vector4 Position, Vector4 Direction, float Intensity, float Weight,
    float SpecularPower, float FarSquared, float InverseRange, uint Flags, Vector4[] Harmonics);

internal sealed record StageParticleLightSet(uint Flags, int[] Indices, float[] Weights, Vector4 Ambient, Vector4[] Harmonics,
    Vector4 Direction, Vector4 Color);

internal static class StageParticleLights
{
    // GH3: light group band also lights group 0x89e1cc18 (unnamed); the default group lights all.
    public static bool Accepts(uint group, uint lightGroup, uint enabled, uint flags) =>
        (enabled & 2) != 0 && (flags & 2) != 0 && (lightGroup == group || group == (lightGroup == Gh3Keys.Band ? 0x89e1cc18u : 0u) || lightGroup == Gh3Keys.DefaultGroup);

    public static StageParticleLightSet Select(IReadOnlyList<StageParticleLight> lights, Vector4 position, bool disableSH = false)
    {
        var count = lights.Count;
        if (count > 32) throw new ArgumentOutOfRangeException(nameof(lights));
        Span<int> order = stackalloc int[count];
        for (var i = 0; i < count; i++) order[i] = i;
        Span<float> attenuation = stackalloc float[count];
        Span<float> energy = stackalloc float[count];
        Span<float> rank = stackalloc float[count];
        for (var i = 0; i < count; i++)
        {
            var light = lights[i];
            var delta = light.Position - position;
            // GH3: native light ranking: distance falloff, Rec. 601 luminance, shadow lights first (x 50000).
            var distance = (delta.X * delta.X + delta.Z * delta.Z) + delta.Y * delta.Y + .0001f;
            attenuation[i] = Math.Clamp((light.FarSquared - distance) * light.InverseRange, 0, 1);
            energy[i] = light.Intensity * light.Weight;
            // GH3: see above.
            var luminance = (light.Color.Z * .114f + light.Color.Y * .587f) + light.Color.X * .299f;
            // GH3: see above.
            rank[i] = luminance * attenuation[i] * ((light.Flags & 8) != 0 ? 50000 : 1) * energy[i];
        }
        for (var i = 0; i < count - 1; i++)
        {
            var changed = false;
            for (var j = i + 1; j < count; j++)
                if (rank[order[j]] > rank[order[i]])
                {
                    (order[i], order[j]) = (order[j], order[i]);
                    changed = true;
                }
            if (!changed) break;
        }
        var ambient = Vector4.Zero;
        var harmonics = new Vector4[4];
        var indices = new int[4];
        var weights = new float[4];
        var selected = 0;
        Span<(Vector4 Color, Vector3 Direction)> projected = stackalloc (Vector4, Vector3)[count];
        var projectedCount = 0;
        foreach (var i in order)
        {
            var light = lights[i];
            var color = light.Color * energy[i];
            if (light.Type is 1 or 2)
            {
                if (selected > 0)
                    Project(ref projectedCount, projected, (light.Type == 1 ? color : color * attenuation[i],
                        Normalize(light.Type == 1 ? light.Direction : position - light.Position)));
                if (selected < 4)
                {
                    indices[selected] = i;
                    weights[selected++] = light.Weight;
                }
            }
            else if (light.Type == 3)
            {
                var delta = position - light.Position;
                var facing = (light.Direction.Z * delta.Z + light.Direction.Y * delta.Y) + light.Direction.X * delta.X;
                Project(ref projectedCount, projected, (light.Color * (energy[i] * Math.Clamp(facing, 0, 1)), Normalize(delta)));
            }
            else if (light.Type == 4 && !disableSH)
                for (var h = 0; h < 4; h++) harmonics[h] += light.Harmonics[h] * energy[i];
            else if (light.Type == 5) ambient += color;
        }
        if (!disableSH)
            foreach (var sample in projected[..projectedCount])
            {
                // GH3: the native spherical-harmonic projection weights.
                harmonics[0] += sample.Color * .2352944314479828f;
                // GH3: see above.
                harmonics[1] += sample.Color * (-sample.Direction.X * .47058919072151184f);
                // GH3: see above.
                harmonics[2] += sample.Color * (-sample.Direction.Y * .47058919072151184f);
                // GH3: see above.
                harmonics[3] += sample.Color * (-sample.Direction.Z * .47058919072151184f);
            }
        for (var i = selected; i < indices.Length; i++) indices[i] = indices[0];
        var direction = Vector4.Zero;
        var directColor = Vector4.Zero;
        if (selected > 0)
        {
            var light = lights[indices[0]];
            var delta = position - light.Position;
            direction = light.Type == 1 ? light.Direction : new(Normalize(delta), delta.W);
            directColor = light.Color * (light.Intensity * weights[0]);
            directColor.W = light.SpecularPower;
        }
        return new((uint)selected | (disableSH ? 0u : 128u), indices, weights, ambient, harmonics, direction, directColor);
    }

    private static void Project(ref int count, Span<(Vector4 Color, Vector3 Direction)> projected, (Vector4 Color, Vector3 Direction) value) =>
        projected[count++] = value;

    private static Vector3 Normalize(Vector4 value)
    {
        var direction = new Vector3(value.X, value.Y, value.Z);
        var length = MathF.Sqrt((value.X * value.X + value.Y * value.Y) + value.Z * value.Z);
        return length > .00001f ? direction * (1 / length) : direction; // GH3: zero-length guard
    }
}
