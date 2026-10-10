using System.Numerics;
using System.Text.Json;

namespace Gh3Skin;

// The collision of the custom chain joints with the body capsules (segment against capsule, closest points).
internal sealed partial class StageHair
{
    // The body colliders of a custom joint: the joint segment leaves each capsule that it enters; it turns about its
    // origin, so a joint that starts inside a collider (a skirt joint on the pelvis) is pushed out at its end only.
    private Vector3 Collide(int i, Vector3 origin, Vector3 tail, float length)
    {
        var mask = _definition.Joints[i].ColliderMask;
        for (var c = 0; c < _colliders.Length; c++)
        {
            if ((mask & (1L << c)) == 0) continue;
            var radius = _colliders[c].Radius + _radius[i];
            if (_custom[i])
            {
                // The middle and the end of the joint leave the capsule. Fixed points give a push that grows smoothly with
                // the depth; the closest point of the segment jumps along it and makes a joint that lies on a capsule shake.
                for (var k = 1; k <= 2; k++) tail = Push(origin, tail, length, k * 0.5f, c, radius);
                continue;
            }
            var (s, t) = Closest(origin, tail, _colliderA[c], _colliderB[c]);
            if (s < MinimumContactFraction) (s, t) = (1, Closest(tail, tail, _colliderA[c], _colliderB[c]).T);
            var point = origin + (tail - origin) * s;
            var away = point - (_colliderA[c] + (_colliderB[c] - _colliderA[c]) * t);
            var distance = away.LengthSquared();
            if (distance >= radius * radius) continue;
            distance = MathF.Sqrt(distance);
            if (distance < MinimumDistance) continue;
            tail += away / distance * ((radius - distance) / s);
            var turned = tail - origin;
            if (turned.LengthSquared() > ZeroLengthSquared) tail = origin + Vector3.Normalize(turned) * length;
        }
        return tail;
    }

    // The joint turned about its origin so that its point at `s` leaves collider `c` (the sum of the radii `radius`).
    private Vector3 Push(Vector3 origin, Vector3 tail, float length, float s, int c, float radius)
    {
        var point = origin + (tail - origin) * s;
        var t = Closest(point, point, _colliderA[c], _colliderB[c]).T;
        var away = point - (_colliderA[c] + (_colliderB[c] - _colliderA[c]) * t);
        var distance = away.LengthSquared();
        if (distance >= radius * radius) return tail;
        distance = MathF.Sqrt(distance);
        if (distance < MinimumDistance) return tail;
        tail += away / distance * ((radius - distance) / s);
        var turned = tail - origin;
        return turned.LengthSquared() > ZeroLengthSquared ? origin + Vector3.Normalize(turned) * length : tail;
    }

    // The parameters (s on p0-p1, t on q0-q1) of the closest points of two segments.
    private static (float S, float T) Closest(Vector3 p0, Vector3 p1, Vector3 q0, Vector3 q1)
    {
        var d1 = p1 - p0;
        var d2 = q1 - q0;
        var r = p0 - q0;
        var a = Vector3.Dot(d1, d1);
        var e = Vector3.Dot(d2, d2);
        var f = Vector3.Dot(d2, r);
        if (e < ZeroLengthSquared) return (a < ZeroLengthSquared ? 0 : Math.Clamp(-Vector3.Dot(d1, r) / a, 0, 1), 0);
        var c = Vector3.Dot(d1, r);
        var b = Vector3.Dot(d1, d2);
        var denominator = a * e - b * b;
        var s = denominator > ZeroLengthSquared ? Math.Clamp((b * f - c * e) / denominator, 0, 1) : 0;
        var t = (b * s + f) / e;
        if (t < 0) { t = 0; s = a < ZeroLengthSquared ? 0 : Math.Clamp(-c / a, 0, 1); }
        else if (t > 1) { t = 1; s = a < ZeroLengthSquared ? 0 : Math.Clamp((b - c) / a, 0, 1); }
        return (s, t);
    }
}
