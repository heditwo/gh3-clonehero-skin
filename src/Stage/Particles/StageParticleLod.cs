using System.Numerics;

namespace Gh3Skin;

// GH3's particle level of detail (off in GH3 PC; kept for parity checks).

internal sealed class StageParticleLod
{
    // GH3: the default LOD distances (UseDefLODDistances).
    public Vector2 Distances { get; private set; } = new(5, 20);
    public int Minimum { get; private set; }
    public int Counter { get; private set; }
    public float Scale { get; private set; } = 1;
    public float Pending { get; private set; }
    public bool Culled { get; private set; }

    public StageParticleLod(Gh3QbValue? source = null, int counter = 0)
    {
        Counter = counter;
        if (source == null) return;
        Minimum = (int)StageParticleDefinition.Number(source, "LODMinParticles", 0, int.MinValue, int.MaxValue);
        foreach (var field in source.Fields)
        {
            if (field.Name == 0 && field.Value.Type == Gh3QbType.Checksum && field.Value.Integer == Gh3Checksum.Value("UseDefLODDistances")) Distances = new(5, 20); // GH3: see above
            else if (field.Name == Gh3Checksum.Value("LODDistances") || field.Name == Gh3Checksum.Value("LOD_Distances"))
            {
                if (field.Value.Data is not float[] values || values.Length != 2 || values.Any(value => !float.IsFinite(value)))
                    throw new InvalidDataException("Invalid particle LOD distances");
                Distances = new(values[0], values[1]);
            }
        }
    }

    public void Set(Gh3QbValue parameters)
    {
        Minimum = (int)StageParticleDefinition.Number(parameters, "min_particles", 0, int.MinValue, int.MaxValue);
        Distances = new((float)StageParticleDefinition.Number(parameters, "lod_dist1", 0, -StageLimits.MaximumMagnitude, StageLimits.MaximumMagnitude),
            (float)StageParticleDefinition.Number(parameters, "lod_dist2", 0, -StageLimits.MaximumMagnitude, StageLimits.MaximumMagnitude));
    }

    public float? Advance(float duration, float? distance, bool visible, bool enabled, float time, float maximumLife, float lifetime, bool stopped, bool burst, bool exempt = false)
    {
        Pending += duration;
        Culled = enabled && !visible && !exempt && lifetime == 0 && !stopped && !burst && time > maximumLife;
        if (Culled) { Pending = 0; return null; }
        var skipped = visible ? 0 : 1;
        if (distance > Distances.Y) { Scale = .5f; skipped += 2; }
        else if (distance > Distances.X)
        {
            Scale = (distance.Value - Distances.X) / (Distances.Y - Distances.X) * .5f + .5f;
            skipped++;
        }
        var update = ++Counter > skipped;
        if (update) Counter = 0;
        if (!enabled) { Scale = 1; update = true; }
        if (!update) return null;
        var elapsed = Pending;
        Pending = 0;
        return elapsed;
    }
}
