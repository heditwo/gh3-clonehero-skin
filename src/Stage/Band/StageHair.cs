using System.Numerics;
using System.Text.Json;

namespace Gh3Skin;

internal sealed class HairBank
{
    public int Version { get; set; }
    public Dictionary<string, HairDefinition> Members { get; set; } = new();

    public static HairBank? Load(string path)
    {
        if (!File.Exists(path)) return null;
        var bank = JsonSerializer.Deserialize<HairBank>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (bank?.Version != 1 || bank.Members.Count is < 1 or > 4) throw new InvalidDataException("Invalid hair bank");
        return bank;
    }
}

internal sealed class HairDefinition
{
    public int Head { get; set; }
    public HairJoint[] Joints { get; set; } = Array.Empty<HairJoint>();
    public HairCapsule Capsule { get; set; } = new();

    // The same chains on another rig: `bone` gives the rig index of a bone of this definition's rig, or -1.
    public HairDefinition? Remap(Func<int, int> bone)
    {
        var head = bone(Head);
        var joints = Joints.Select(j => new HairJoint
        {
            Bone = bone(j.Bone), Parent = bone(j.Parent), Position = j.Position, Rotation = j.Rotation, Length = j.Length, Mass = j.Mass,
            Friction = j.Friction, AngularDamping = j.AngularDamping, Limits = j.Limits,
        }).ToArray();
        return head < 0 || joints.Any(j => j.Bone < 0 || j.Parent < 0) ? null : new HairDefinition { Head = head, Joints = joints, Capsule = Capsule };
    }
}

internal sealed class HairCapsule
{
    public float[] A { get; set; } = Array.Empty<float>();
    public float[] B { get; set; } = Array.Empty<float>();
    public float Radius { get; set; }
}

internal sealed class HairJoint
{
    public int Bone { get; set; }
    public int Parent { get; set; }
    public float[] Position { get; set; } = Array.Empty<float>();
    public float[] Rotation { get; set; } = Array.Empty<float>();
    public float Length { get; set; }
    public float Mass { get; set; }
    public float Friction { get; set; }
    public float AngularDamping { get; set; }
    public Dictionary<string, float[]> Limits { get; set; } = new();
}

internal sealed class StageHair
{
    public delegate BandPose PoseSampler(double time, BandPose[] pose);
    private sealed record Checkpoint(Vector3[] Tails, Vector3[] OldTails, BandPose[] Pose, BandPose[] Previous);
    private const int Rate = 120;
    private const float Step = 1f / Rate;
    private readonly HairDefinition _definition;
    private readonly int[] _parents;
    private readonly PoseSampler _sample;
    private readonly BandPose[] _animated;
    private readonly BandPose[] _world;
    private readonly bool[] _visited;
    private readonly BandPose[] _local;
    private readonly BandPose[] _pose;
    private readonly BandPose[] _previous;
    private readonly Vector3[] _tails;
    private readonly Vector3[] _oldTails;
    private readonly float[] _retention;
    private readonly float[] _limits;
    private readonly SortedDictionary<int, Checkpoint> _checkpoints = new();
    private int _tick = -1;
    private BandPose _root;
    private float _blend;
    private readonly Vector3 _capsuleA;
    private readonly Vector3 _capsuleB;

    public StageHair(HairDefinition definition, int[] parents, PoseSampler sample)
    {
        _definition = definition;
        _parents = parents;
        _sample = sample;
        if (definition.Head < 0 || definition.Head >= parents.Length || definition.Joints.Length is < 1 or > 32)
            throw new InvalidDataException("Invalid hair skeleton");
        _capsuleA = Vector(definition.Capsule.A);
        _capsuleB = Vector(definition.Capsule.B);
        if (!float.IsFinite(definition.Capsule.Radius) || definition.Capsule.Radius is <= 0 or > 1)
            throw new InvalidDataException("Invalid hair collider");
        var seen = new HashSet<int> { definition.Head };
        var count = definition.Joints.Length;
        _local = new BandPose[count]; _pose = new BandPose[count]; _previous = new BandPose[count];
        _tails = new Vector3[count]; _oldTails = new Vector3[count]; _retention = new float[count]; _limits = new float[count];
        for (var i = 0; i < count; i++)
        {
            var joint = definition.Joints[i];
            if (joint.Bone < 0 || joint.Bone >= parents.Length || !seen.Contains(joint.Parent) || !seen.Add(joint.Bone) ||
                !float.IsFinite(joint.Length) || joint.Length is < 0.005f or > 1 || !float.IsFinite(joint.Mass) || joint.Mass is <= 0 or > 100 ||
                !float.IsFinite(joint.Friction) || joint.Friction is < 0 or > 100 || !float.IsFinite(joint.AngularDamping) || joint.AngularDamping is < 0 or > 100 ||
                joint.Rotation.Length != 4 || !joint.Rotation.All(float.IsFinite) ||
                !joint.Limits.TryGetValue("coneLimit", out var cone) || cone.Length != 2 || !cone.All(float.IsFinite) || cone[1] is <= 0 or > 1.6f)
                throw new InvalidDataException("Invalid hair joint");
            var q = new Quaternion(joint.Rotation[0], joint.Rotation[1], joint.Rotation[2], joint.Rotation[3]);
            if (Math.Abs(q.LengthSquared() - 1) > 0.01) throw new InvalidDataException("Invalid hair bind rotation");
            _local[i] = new(Vector(joint.Position), Quaternion.Normalize(q));
            _limits[i] = cone[1];
            _retention[i] = MathF.Exp(-Step * (joint.AngularDamping + joint.Friction / (joint.Mass * joint.Length * joint.Length)));
        }
        _animated = new BandPose[parents.Length]; _world = new BandPose[parents.Length]; _visited = new bool[parents.Length];
    }

    public void Sample(double time)
    {
        if (!double.IsFinite(time) || time > 86400) throw new ArgumentOutOfRangeException(nameof(time));
        var frame = Math.Max(0, time) * Rate;
        var target = (int)Math.Ceiling(frame);
        if (_tick < 0) Advance(0);
        if (target < _tick)
        {
            var saved = _checkpoints.Last(p => p.Key <= target);
            _tick = saved.Key;
            Array.Copy(saved.Value.Tails, _tails, _tails.Length);
            Array.Copy(saved.Value.OldTails, _oldTails, _tails.Length);
            Array.Copy(saved.Value.Pose, _pose, _pose.Length);
            Array.Copy(saved.Value.Previous, _previous, _pose.Length);
        }
        while (_tick < target) Advance(_tick + 1);
        _blend = target == 0 ? 1 : (float)(frame - target + 1);
    }

    public void Apply(BandPose[] pose)
    {
        for (var i = 0; i < _pose.Length; i++) pose[_definition.Joints[i].Bone] = BandPose.Mix(_previous[i], _pose[i], _blend);
    }

    private void Advance(int tick)
    {
        _root = _sample((double)tick / Rate, _animated);
        Array.Clear(_visited, 0, _visited.Length);
        for (var i = 0; i < _world.Length; i++) World(i);
        var head = _world[_definition.Head];
        var ca = head.Position + Vector3.Transform(_capsuleA, head.Rotation);
        var cb = head.Position + Vector3.Transform(_capsuleB, head.Rotation);
        Array.Copy(_pose, _previous, _pose.Length);
        for (var i = 0; i < _pose.Length; i++)
        {
            var joint = _definition.Joints[i];
            var parent = _world[joint.Parent];
            var origin = parent.Position + Vector3.Transform(_local[i].Position, parent.Rotation);
            var rotation = Quaternion.Normalize(parent.Rotation * _local[i].Rotation);
            var direction = Vector3.Transform(Vector3.UnitX, rotation);
            var goal = origin + direction * joint.Length;
            var tail = goal;
            if (tick > 0)
            {
                var velocity = (_tails[i] - _oldTails[i]) * _retention[i];
                var acceleration = new Vector3(0, -9.81f, 0) + (goal - _tails[i]) * (9.81f / joint.Length);
                tail = _tails[i] + velocity + acceleration * (Step * Step);
                for (var pass = 0; pass < 3; pass++)
                {
                    var axis = cb - ca;
                    var closest = ca + axis * Math.Clamp(Vector3.Dot(tail - ca, axis) / Math.Max(axis.LengthSquared(), 1e-8f), 0, 1);
                    var outward = tail - closest;
                    if (outward.LengthSquared() < _definition.Capsule.Radius * _definition.Capsule.Radius && outward.LengthSquared() > 1e-10f)
                        tail = closest + Vector3.Normalize(outward) * _definition.Capsule.Radius;
                    var proposed = tail - origin;
                    proposed = proposed.LengthSquared() < 1e-10f ? direction : Vector3.Normalize(proposed);
                    var swing = FromTo(direction, proposed);
                    var angle = 2 * MathF.Acos(Math.Clamp(Math.Abs(swing.W), 0, 1));
                    if (angle > _limits[i]) swing = Quaternion.Slerp(Quaternion.Identity, swing, _limits[i] / angle);
                    tail = origin + Vector3.Transform(direction, swing) * joint.Length;
                }
                rotation = Quaternion.Normalize(FromTo(direction, Vector3.Normalize(tail - origin)) * rotation);
            }
            _oldTails[i] = tick == 0 ? tail : _tails[i];
            _tails[i] = tail;
            _world[joint.Bone] = new(origin, rotation);
        }
        // The rig parent of a joint can be a later joint of the chains (the profile hair of a character on another
        // role's rig), so the local poses use the final world poses of this step.
        for (var i = 0; i < _pose.Length; i++)
        {
            var bone = _definition.Joints[i].Bone;
            var actualParent = _parents[bone] < 0 ? _root : _world[_parents[bone]];
            var inverse = Quaternion.Inverse(actualParent.Rotation);
            _pose[i] = new(Vector3.Transform(_world[bone].Position - actualParent.Position, inverse), Quaternion.Normalize(inverse * _world[bone].Rotation));
        }
        _tick = tick;
        if (tick == 0) Array.Copy(_pose, _previous, _pose.Length);
        if (tick % Rate == 0 && !_checkpoints.ContainsKey(tick))
            _checkpoints.Add(tick, new(_tails.ToArray(), _oldTails.ToArray(), _pose.ToArray(), _previous.ToArray()));
    }

    private BandPose World(int bone)
    {
        if (_visited[bone]) return _world[bone];
        var parent = _parents[bone] < 0 ? _root : World(_parents[bone]);
        var local = _animated[bone];
        _visited[bone] = true;
        return _world[bone] = new(parent.Position + Vector3.Transform(local.Position, parent.Rotation), Quaternion.Normalize(parent.Rotation * local.Rotation));
    }

    private static Quaternion FromTo(Vector3 a, Vector3 b)
    {
        var dot = Math.Clamp(Vector3.Dot(a, b), -1, 1);
        if (dot < -0.99999f)
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(a, Math.Abs(a.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX)), MathF.PI);
        var cross = Vector3.Cross(a, b);
        return Quaternion.Normalize(new Quaternion(cross, 1 + dot));
    }

    private static Vector3 Vector(float[] v)
    {
        if (v.Length != 3 || !v.All(float.IsFinite)) throw new InvalidDataException("Invalid hair vector");
        return new(v[0], v[1], v[2]);
    }
}
