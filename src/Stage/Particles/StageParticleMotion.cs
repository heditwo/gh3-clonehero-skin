using System.Numerics;

namespace Gh3Skin;

internal readonly record struct StageParticleMotion(Vector3 Position, Vector3 Velocity)
{
    public StageParticleMotion Step(float duration, Vector3 force, bool bounce, float height, float coefficient, Vector3 displacement = default)
        => Step(duration, force, bounce, height, coefficient, out _, displacement);

    public StageParticleMotion Step(float duration, Vector3 force, bool bounce, float height, float coefficient, out bool collision, Vector3 displacement = default)
    {
        collision = false;
        if (duration <= 0) return this;
        var velocity = Velocity + force * duration;
        var position = Position + (velocity * duration + displacement);
        if (bounce && position.Y < height)
        {
            collision = true;
            position.Y = height;
            if (velocity.Y < 0) velocity *= new Vector3(coefficient, -coefficient, coefficient);
        }
        return new(position, velocity);
    }

    public static bool Callback(bool collision, float rate, bool configured, Func<int, int> random)
        => collision && rate != 0 && configured && rate >= random(10000) * .0001f;
}
