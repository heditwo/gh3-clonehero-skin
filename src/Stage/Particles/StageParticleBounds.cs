using System.Numerics;

namespace Gh3Skin;

// The bounding box and sphere of a particle system's live particles.

internal sealed class StageParticleBounds
{
    public Vector3 Minimum { get; private set; }
    public Vector3 Maximum { get; private set; }
    public Vector3 Center { get; private set; }
    public float Radius { get; private set; }

    public void Begin(Vector3 position) => Minimum = Maximum = position;

    public void Include(Vector3 position)
    {
        Minimum = Vector3.Min(Minimum, position);
        Maximum = Vector3.Max(Maximum, position);
    }

    public void Update(Vector2 width, Vector2 height)
    {
        var extent = new Vector3(Math.Max(Math.Max(width.X, width.Y), Math.Max(height.X, height.Y)));
        Minimum -= extent;
        Maximum += extent;
        Center = (Minimum + Maximum) * .5f;
        Radius = (Maximum - Center).Length();
        if (float.IsNaN(Radius)) { Minimum = Maximum = Vector3.Zero; Clear(); }
    }

    public void Clear() { Center = Vector3.Zero; Radius = 0; }
}
