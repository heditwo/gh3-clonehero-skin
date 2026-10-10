using System.Numerics;

namespace Gh3Skin;

// The quads of a particle (billboard, aligned, cross, ribbon) as GH3 builds them.

internal readonly record struct StageParticleBasis(Vector3 Right, Vector3 Up, bool Visible = true);
internal readonly record struct StageParticleAxes(Vector3 Right, Vector3 Up, Vector3 Depth, bool Visible = true);
internal readonly record struct StageParticleQuad(Vector3 A, Vector3 B, Vector3 C, Vector3 D, Vector4 Uv, float StartAlpha = 1, float EndAlpha = 1)
{
    public StageParticleAxes Lighting { get; init; } = default;
}

internal static class StageParticleGeometry
{
    public static StageParticleAxes FlatLighting(Vector3 velocity, Vector3 right, Vector3 up, bool align, float pathTime, float angle,
        bool rotate3d = false, int randomIndex = 0, float colorMix = 0, Vector3 alignTo = default)
    {
        if (!align) return CrossBasis(new(2, 2, 2), velocity, right, up, false, angle, false, rotate3d, randomIndex, colorMix, alignTo);
        var forward = Vector3.Cross(right, up);
        if (velocity.LengthSquared() == 0) return new(Vector3.Zero, Vector3.Zero, forward, false);
        up = Vector3.Normalize(velocity);
        right = Vector3.Cross(up, forward);
        if (pathTime > 0 && right.LengthSquared() > 0) right = Vector3.Normalize(right);
        return new(right, up, forward, pathTime <= 0 || right != Vector3.Zero);
    }

    public static StageParticleAxes CrossLighting(StageParticleAxes axes, bool split, int side)
    {
        var tangent = side == 0 ? axes.Depth : -axes.Depth;
        var up = axes.Up + axes.Right * ((side == 0 ? -1 : 1) * (split ? 1 : .5f));
        if (up.LengthSquared() > 0) up = Vector3.Normalize(up);
        return new(tangent, up, Vector3.Cross(tangent, up), axes.Visible);
    }

    public static StageParticleBasis Basis(Vector2 size, Vector3 velocity, Vector3 right, Vector3 up, bool align, float pathTime, float angle)
    {
        if (align)
        {
            if (velocity.LengthSquared() == 0) return new(Vector3.Zero, Vector3.Zero, false);
            var forward = Vector3.Cross(right, up);
            if (pathTime > 0)
            {
                up = velocity * (pathTime * .5f);
                right = Vector3.Cross(up, forward);
                if (right.LengthSquared() == 0) return new(Vector3.Zero, Vector3.Zero, false);
                if (right.LengthSquared() > 0) right = Vector3.Normalize(right);
                return new(right * (size.X * .5f), up);
            }
            up = velocity;
            if (up.LengthSquared() > 0) up = Vector3.Normalize(up);
            right = Vector3.Cross(up, forward);
            return new(right * (size.X * .5f), up * (size.Y * .5f));
        }
        // GH3: the rotation is quantized to 256 steps of a turn (1/2pi and 2pi/256 as float32).
        var index = (int)(angle * .15915493667125702f * 256) & 255;
        // GH3: see above.
        var rotation = index * .02454369328916073f;
        var sine = (float)Math.Sin(rotation);
        var cosine = (float)Math.Cos(rotation);
        return new((right * cosine + up * sine) * (size.X * .5f), (up * cosine - right * sine) * (size.Y * .5f));
    }

    public static Vector4 TextureBounds(int columns, int rows, int frame)
    {
        frame &= columns * rows - 1;
        var u = (frame % columns) / (float)columns;
        var v = (frame / columns) / (float)rows;
        return new(u, v, u + 1f / columns, v + 1f / rows);
    }

    public static Vector4 History(Vector3 displacement)
    {
        var length = displacement.Length();
        return length > 0 ? new(displacement * (1 / length), length) : Vector4.Zero;
    }

    public static IEnumerable<StageParticleQuad> Ribbon(Vector3 position, float width, Vector4[] history, int head, Vector3 right, Vector3 up, int columns, int rows, int frame, Vector3 alignTo = default)
    {
        if (history.Length == 0) yield break;
        var forward = alignTo == Vector3.Zero ? Vector3.Cross(right, up) : Alignment(alignTo).Depth;
        var bounds = TextureBounds(columns, rows, frame);
        var step = 1f / history.Length / columns;
        var v = bounds.Y;
        var side = Vector3.Zero;
        Vector3 Side(Vector4 value) => Vector3.Cross(new Vector3(value.X, value.Y, value.Z), forward) * (width * .5f);
        if (history[head].W != 0) side = Side(history[head]);
        var a = position + side;
        var b = position - side;
        for (var index = 0; index < history.Length; index++)
        {
            var value = history[(head + index) % history.Length];
            if (value.W != 0)
            {
                position -= new Vector3(value.X, value.Y, value.Z) * value.W;
                side = Side(value);
            }
            var nextV = v + step;
            if ((index + value.W) - index != 0)
            {
                var c = position - side;
                var d = position + side;
                var direction = new Vector3(value.X, value.Y, value.Z);
                yield return new(a, b, c, d, new(bounds.X, v, bounds.Z, nextV)) { Lighting = new(Vector3.Cross(direction, forward), direction, forward) };
                a = d;
                b = c;
            }
            v = nextV;
        }
    }

    public static IEnumerable<StageParticleQuad> FlatRibbon(Vector3 position, float width, Vector4[] history, int head, Vector3 right, Vector3 up, Vector3 alignTo = default)
    {
        if (history.Length == 0) yield break;
        var forward = alignTo == Vector3.Zero ? Vector3.Cross(right, up) : Alignment(alignTo).Depth;
        var step = 1f / history.Length;
        var alpha = 1f;
        var side = Vector3.Cross(new Vector3(history[head].X, history[head].Y, history[head].Z), forward) * (width * .5f);
        var a = position + side;
        var b = position - side;
        for (var index = 0; index < history.Length; index++)
        {
            var value = history[(head + index) % history.Length];
            var nextAlpha = Math.Max(0, alpha - step);
            if (value.W != 0)
            {
                var direction = new Vector3(value.X, value.Y, value.Z);
                position -= direction * value.W;
                side = Vector3.Cross(direction, forward) * (width * .5f);
                var c = position - side;
                var d = position + side;
                yield return new(a, b, c, d, new(0, alpha, 1, nextAlpha), alpha, nextAlpha);
                a = d;
                b = c;
            }
            alpha = nextAlpha;
        }
    }

    public static float TrailAlpha(float alpha, float weight) => (byte)Math.Clamp((int)(alpha * 255 * weight), 0, 255) / 255f;

    public static IEnumerable<StageParticleQuad> Cross(Vector3 position, Vector3 right, Vector3 up, Vector3 depth, bool split, Vector4 uv)
    {
        var top = position + up - right;
        var bottom = position - up + (split ? right : Vector3.Zero);
        yield return new(top + depth, top - depth, bottom - depth, bottom + depth, new(uv.Z, uv.Y, uv.X, uv.W));
        top = position + up + right;
        bottom = position - up - (split ? right : Vector3.Zero);
        yield return new(top - depth, top + depth, bottom + depth, bottom - depth, uv);
    }

    public static StageParticleAxes CrossBasis(Vector3 size, Vector3 velocity, Vector3 right, Vector3 up, bool align, float angle, bool split,
        bool rotate3d = false, int randomIndex = 0, float colorMix = 0, Vector3 alignTo = default)
    {
        var forward = Vector3.Cross(right, up);
        if (!align)
        {
            if (alignTo != Vector3.Zero)
            {
                var axes = Alignment(alignTo);
                right = axes.Right;
                up = axes.Up;
                forward = axes.Depth;
            }
            if (rotate3d)
            {
                // GH3: 2pi/128 per random index.
                var x = randomIndex * .04908738657832146f;
                var y = colorMix * MathF.PI;
                var axis = new Vector3((float)Math.Cos(x) * (float)Math.Sin(y), -(float)Math.Sin(x), (float)Math.Cos(x) * (float)Math.Cos(y));
                var rotation = Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), angle);
                return new(Vector3.Transform(Vector3.UnitX, rotation) * (size.X * .5f), Vector3.Transform(Vector3.UnitY, rotation) * (size.Y * .5f),
                    Vector3.Transform(Vector3.UnitZ, rotation) * (size.Z * .5f));
            }
            var basis = Basis(new(size.X, size.Y), velocity, right, up, false, 0, angle);
            return new(basis.Right, basis.Up, forward * (size.Z * .5f));
        }
        var length = velocity.Y * velocity.Y + velocity.Z * velocity.Z + velocity.X * velocity.X;
        if (length == 0) return new(Vector3.Zero, Vector3.Zero, Vector3.Zero, false);
        var direction = velocity * (1 / (split ? MathF.Sqrt(length) : length * length));
        var rotated = Basis(new(2, 2), Vector3.Zero, Vector3.UnitX, Vector3.UnitY, false, 0, angle);
        if (direction.Y == 0 && direction.Z == 0) { right = -Vector3.UnitZ; up = rotated.Up; forward = rotated.Right; }
        else if (direction.X == 0 && direction.Z == 0) { right = rotated.Right; up = -Vector3.UnitZ; forward = rotated.Up; }
        else if (direction.X == 0 && direction.Y == 0) { right = rotated.Right; up = rotated.Up; forward = Vector3.UnitZ; }
        else
        {
            var axis = Vector3.Normalize(new Vector3(-direction.Y, direction.X, 0));
            var rotation = Quaternion.CreateFromAxisAngle(axis, (float)Math.Acos(Math.Clamp(direction.Z, -1, 1)));
            right = Vector3.Transform(rotated.Right, rotation);
            up = Vector3.Transform(rotated.Up, rotation);
            forward = Vector3.Transform(Vector3.UnitZ, rotation);
        }
        return new(right * (size.X * .5f), up * (size.Y * .5f), forward * (size.Z * .5f));
    }

    public static StageParticleAxes Alignment(Vector3 direction)
    {
        if (direction == Vector3.Zero) return new(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
        direction = Vector3.Normalize(direction);
        if (direction.Y == 0 && direction.Z == 0) return new(direction.X > 0 ? -Vector3.UnitZ : Vector3.UnitZ, Vector3.UnitY, direction);
        if (direction.X == 0 && direction.Z == 0) return new(Vector3.UnitX, direction.Y > 0 ? -Vector3.UnitZ : Vector3.UnitZ, direction);
        if (direction.X == 0 && direction.Y == 0) return new(direction.Z > 0 ? Vector3.UnitX : -Vector3.UnitX, Vector3.UnitY, direction);
        var axis = Vector3.Normalize(new Vector3(-direction.Y, direction.X, 0));
        var rotation = Quaternion.CreateFromAxisAngle(axis, (float)Math.Acos(Math.Clamp(direction.Z, -1, 1)));
        return new(Vector3.Transform(Vector3.UnitX, rotation), Vector3.Transform(Vector3.UnitY, rotation), Vector3.Transform(Vector3.UnitZ, rotation));
    }
}
