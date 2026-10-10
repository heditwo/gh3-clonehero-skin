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
    // The body colliders of the custom chains of a character package (character.json `colliders`); the GH3 hair collides
    // with the head capsule only.
    public HairCollider[] Colliders { get; set; } = Array.Empty<HairCollider>();

    // The same chains on another rig: `bone` gives the rig index of a bone of this definition's rig, or -1.
    public HairDefinition? Remap(Func<int, int> bone)
    {
        var head = bone(Head);
        var joints = Joints.Select(j => new HairJoint
        {
            Bone = bone(j.Bone), Parent = bone(j.Parent), Position = j.Position, Rotation = j.Rotation, Length = j.Length, Mass = j.Mass,
            Friction = j.Friction, AngularDamping = j.AngularDamping, LinearDamping = j.LinearDamping, Limits = j.Limits, Type = j.Type, Axis = j.Axis, Range = j.Range,
            TwistAxis = j.TwistAxis, ConeAxis = j.ConeAxis, PlaneAxis = j.PlaneAxis, MaxAngularVelocity = j.MaxAngularVelocity,
            Direction = j.Direction, Radius = j.Radius, ColliderMask = j.ColliderMask, HeadCapsule = j.HeadCapsule,
        }).ToArray();
        var colliders = Colliders.Select(c => new HairCollider { Bone = bone(c.Bone), A = c.A, B = c.B, Radius = c.Radius }).ToArray();
        return head < 0 || joints.Any(j => j.Bone < 0 || j.Parent < 0) || colliders.Any(c => c.Bone < 0) ? null :
            new HairDefinition { Head = head, Joints = joints, Capsule = Capsule, Colliders = colliders };
    }

    // The profile hair (or none) with the custom chains of a character package after it. Without profile hair the
    // definition has no head capsule (Head -1): the custom joints collide with their own colliders only.
    public static HairDefinition? Merge(HairDefinition? profile, HairJoint[] joints, HairCollider[] colliders)
    {
        if (joints.Length == 0) return profile;
        return new HairDefinition
        {
            Head = profile?.Head ?? -1, Capsule = profile?.Capsule ?? new HairCapsule(),
            Joints = (profile?.Joints ?? Array.Empty<HairJoint>()).Concat(joints).ToArray(), Colliders = colliders,
        };
    }
}

// A capsule (or a sphere: A equal to B) on a bone, in the bone's bind frame (runtime coordinates).
internal sealed class HairCollider
{
    public int Bone { get; set; }
    public float[] A { get; set; } = Array.Empty<float>();
    public float[] B { get; set; } = Array.Empty<float>();
    public float Radius { get; set; }
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
    public float LinearDamping { get; set; }
    public Dictionary<string, float[]> Limits { get; set; } = new();
    // Character library joints (PACKAGE-FORMAT.md section 10.1): "ragdoll" (the default), "hinge" (Havok limited hinge:
    // rotation about Axis within Range radians), or "prismatic" (Havok prismatic: translation along Axis within Range
    // metres, no rotation). Axis is in the joint's bind frame; Range is relative to the bind pose.
    public string Type { get; set; } = "ragdoll";
    public float[] Axis { get; set; } = Array.Empty<float>();
    public float[] Range { get; set; } = Array.Empty<float>();
    // Optional: the Havok ragdoll limit frames in the joint's bind frame. The cone limits the angle from
    // TwistAxis (on the joint) to ConeAxis (on the parent); the planes limit the angle from TwistAxis to the plane normal
    // to PlaneAxis. Without them the cone is centred on the bind direction and the planes do not apply.
    public float[] TwistAxis { get; set; } = Array.Empty<float>();
    public float[] ConeAxis { get; set; } = Array.Empty<float>();
    public float[] PlaneAxis { get; set; } = Array.Empty<float>();
    // Optional: the Havok speed limit of the body in radians per second; 0 is no limit.
    public float MaxAngularVelocity { get; set; }
    // Custom chains (character.json): the bone axis that the joint points along (GH3 joints point along +X), the
    // collision radius of the joint, the colliders that it touches (bit i: HairDefinition.Colliders[i]), and whether it
    // touches the head capsule (the GH3 hair does).
    public float[] Direction { get; set; } = Array.Empty<float>();
    public float Radius { get; set; }
    public long ColliderMask { get; set; }
    public bool HeadCapsule { get; set; } = true;
}

internal sealed class StageHair
{
    public delegate BandPose PoseSampler(double time, BandPose[] pose);
    private sealed record Checkpoint(Vector3[] Tails, Vector3[] OldTails, Vector3[] Origins, Quaternion[] Frames, BandPose[] Pose, BandPose[] Previous);
    private const int Rate = 120;
    private const float Step = 1f / Rate;
    private static readonly Vector3 Gravity = new(0, -9.81f, 0);
    // GH3 creates its Havok world with the 4-iteration solver (SOLVER_TYPE_4ITERS_MEDIUM: tau 0.6, damping 1), and its
    // ragdoll limits have angularLimitsTauFactor 0.8: each step corrects about 0.48 of a limit violation.
    private const int Iterations = 4;
    private const float LimitTau = 0.6f * 0.8f;
    private const int Passes = 3;
    private static readonly float LimitShare = 1 - MathF.Pow(1 - LimitTau, 1f / (Passes * Iterations));
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
    private readonly Vector3[] _origins;
    private readonly Quaternion[] _frames;
    private readonly float[] _retention;
    private readonly float[] _brake;
    private readonly float[] _maxTurn;
    private readonly float[] _inverseInertia;
    private readonly int[] _chain;
    private readonly int[] _types;
    private readonly Vector3[] _axes;
    private readonly float[] _low;
    private readonly float[] _high;
    private readonly Vector3[] _twist;
    private readonly Vector3[] _cone;
    private readonly Vector3[] _plane;
    private readonly float[] _coneCos;
    private readonly float[] _coneSin;
    private readonly bool[] _planes;
    private readonly float[] _planeLow;
    private readonly float[] _planeHigh;
    // The turn from the +X frame of the solver to the bone frame of a joint that points along another axis.
    private readonly Quaternion[] _align;
    private readonly bool[] _aligned;
    private readonly float[] _radius;
    private readonly bool[] _headCapsule;
    private readonly HairCollider[] _colliders;
    private readonly Vector3[] _colliderA;
    private readonly Vector3[] _colliderB;
    private readonly Vector3[] _colliderLocalA;
    private readonly Vector3[] _colliderLocalB;
    private readonly bool _hasCapsule;
    private readonly int[] _jointOf;
    // The custom joints that the collider pass moves: joints with colliders and the joints that hang from them.
    private readonly bool[] _projected;
    private readonly int _projectCount;
    public const int MaxJoints = 96;
    public const int MaxColliders = 32;
    private const int Ragdoll = 0, Hinge = 1, Prismatic = 2;
    private readonly SortedDictionary<int, Checkpoint> _checkpoints = new();
    private int _tick = -1;
    private BandPose _root;
    private float _blend;
    private readonly Vector3 _capsuleA;
    private readonly Vector3 _capsuleB;
    // The fastest joint end since the start (metres per second) and its bone. GH3 hair follows the head at a few metres
    // per second; a chain whose rest pose does not fit its ragdoll whips much faster.
    public const float WarningSpeed = 30;
    public float PeakSpeed { get; private set; }
    public int PeakBone { get; private set; } = -1;

    public StageHair(HairDefinition definition, int[] parents, PoseSampler sample)
    {
        _definition = definition;
        _parents = parents;
        _sample = sample;
        _hasCapsule = definition.Head >= 0;
        if (definition.Head >= parents.Length || definition.Joints.Length is < 1 or > MaxJoints || definition.Colliders.Length > MaxColliders)
            throw new InvalidDataException("Invalid hair skeleton");
        if (_hasCapsule)
        {
            _capsuleA = Vector(definition.Capsule.A);
            _capsuleB = Vector(definition.Capsule.B);
            if (!float.IsFinite(definition.Capsule.Radius) || definition.Capsule.Radius is <= 0 or > 1)
                throw new InvalidDataException("Invalid hair collider");
        }
        var count = definition.Joints.Length;
        // A chain starts at a bone that is not a hair joint (the head; on the character library profiles also the chest,
        // pelvis, arms, or legs); a joint after it can hang from an earlier joint.
        var seen = Enumerable.Range(0, parents.Length).Except(definition.Joints.Select(j => j.Bone)).ToHashSet();
        if (_hasCapsule && !seen.Contains(definition.Head)) throw new InvalidDataException("Invalid hair skeleton");
        _colliders = definition.Colliders;
        _colliderA = new Vector3[_colliders.Length]; _colliderB = new Vector3[_colliders.Length];
        _colliderLocalA = new Vector3[_colliders.Length]; _colliderLocalB = new Vector3[_colliders.Length];
        for (var c = 0; c < _colliders.Length; c++)
        {
            var collider = _colliders[c];
            if (!seen.Contains(collider.Bone) || !float.IsFinite(collider.Radius) || collider.Radius is <= 0 or > 5) throw new InvalidDataException("Invalid hair collider");
            _colliderLocalA[c] = Vector(collider.A);
            _colliderLocalB[c] = collider.B.Length == 0 ? _colliderLocalA[c] : Vector(collider.B);
        }
        _local = new BandPose[count]; _pose = new BandPose[count]; _previous = new BandPose[count];
        _tails = new Vector3[count]; _oldTails = new Vector3[count]; _origins = new Vector3[count]; _frames = new Quaternion[count];
        _retention = new float[count]; _brake = new float[count]; _maxTurn = new float[count]; _inverseInertia = new float[count]; _chain = new int[count];
        _types = new int[count]; _axes = new Vector3[count]; _low = new float[count]; _high = new float[count];
        _twist = new Vector3[count]; _cone = new Vector3[count]; _plane = new Vector3[count]; _coneCos = new float[count]; _coneSin = new float[count];
        _planes = new bool[count]; _planeLow = new float[count]; _planeHigh = new float[count];
        _align = new Quaternion[count]; _aligned = new bool[count]; _radius = new float[count]; _headCapsule = new bool[count];
        for (var i = 0; i < count; i++)
        {
            var joint = definition.Joints[i];
            _align[i] = Quaternion.Identity;
            if (joint.Direction.Length > 0)
            {
                var direction = Unit(joint.Direction);
                _aligned[i] = Vector3.Dot(direction, Vector3.UnitX) < 0.999999f;
                if (_aligned[i]) _align[i] = FromTo(Vector3.UnitX, direction);
            }
            if (!float.IsFinite(joint.Radius) || joint.Radius is < 0 or > 0.5f || (joint.ColliderMask >> _colliders.Length) != 0)
                throw new InvalidDataException("Invalid hair joint");
            _radius[i] = joint.Radius;
            _headCapsule[i] = joint.HeadCapsule && _hasCapsule;
            if (joint.Bone < 0 || joint.Bone >= parents.Length || !seen.Contains(joint.Parent) || !seen.Add(joint.Bone) ||
                !float.IsFinite(joint.Length) || joint.Length is < 0.005f or > 1 || !float.IsFinite(joint.Mass) || joint.Mass is <= 0 or > 100 ||
                !float.IsFinite(joint.Friction) || joint.Friction is < 0 or > 100 || !float.IsFinite(joint.AngularDamping) || joint.AngularDamping is < 0 or > 100 ||
                !float.IsFinite(joint.LinearDamping) || joint.LinearDamping is < 0 or > 100 || !float.IsFinite(joint.MaxAngularVelocity) || joint.MaxAngularVelocity < 0 ||
                joint.Rotation.Length != 4 || !joint.Rotation.All(float.IsFinite))
                throw new InvalidDataException("Invalid hair joint");
            var q = new Quaternion(joint.Rotation[0], joint.Rotation[1], joint.Rotation[2], joint.Rotation[3]);
            if (Math.Abs(q.LengthSquared() - 1) > 0.01) throw new InvalidDataException("Invalid hair bind rotation");
            _local[i] = new(Vector(joint.Position), Quaternion.Normalize(q));
            // The joint frame of the solver: +X along the joint.
            if (_aligned[i]) _local[i] = new(_local[i].Position, Quaternion.Normalize(_local[i].Rotation * _align[i]));
            _types[i] = joint.Type switch { "ragdoll" or "" => Ragdoll, "hinge" => Hinge, "prismatic" => Prismatic, _ => throw new InvalidDataException("Invalid hair joint type") };
            // Havok angular damping, the constraint friction torque over the inertia of a point mass at the joint end (a
            // distance per step), and the speed limit of the body.
            _retention[i] = MathF.Exp(-Step * joint.AngularDamping);
            _brake[i] = joint.Friction / (joint.Mass * joint.Length) * Step * Step;
            _maxTurn[i] = joint.MaxAngularVelocity * Step;
            _inverseInertia[i] = 1 / (joint.Mass * joint.Length * joint.Length);
            if (_types[i] == Ragdoll)
            {
                if (!joint.Limits.TryGetValue("coneLimit", out var cone) || cone.Length != 2 || !cone.All(float.IsFinite) || cone[1] is <= 0 or > 1.6f)
                    throw new InvalidDataException("Invalid hair joint");
                _coneCos[i] = MathF.Cos(cone[1]);
                _coneSin[i] = MathF.Sin(cone[1]);
                _twist[i] = _cone[i] = Vector3.UnitX;
                if (joint.TwistAxis.Length == 0 && joint.ConeAxis.Length == 0 && joint.PlaneAxis.Length == 0) continue;
                if (!joint.Limits.TryGetValue("planesLimit", out var planes) || planes.Length != 2 || !planes.All(float.IsFinite) || planes[0] > planes[1] ||
                    planes[0] < -MathF.PI / 2 || planes[1] > MathF.PI / 2)
                    throw new InvalidDataException("Invalid hair joint");
                _twist[i] = Solver(i, Unit(joint.TwistAxis));
                _cone[i] = Solver(i, Unit(joint.ConeAxis));
                _plane[i] = Solver(i, Unit(joint.PlaneAxis));
                _planes[i] = true;
                _planeLow[i] = MathF.Sin(planes[0]);
                _planeHigh[i] = MathF.Sin(planes[1]);
                continue;
            }
            var axis = Vector(joint.Axis);
            var limit = _types[i] == Hinge ? MathF.PI : 1;
            if (Math.Abs(axis.LengthSquared() - 1) > 0.01f || joint.Range.Length != 2 || !joint.Range.All(float.IsFinite) ||
                joint.Range[0] > joint.Range[1] || joint.Range[0] < -limit || joint.Range[1] > limit)
                throw new InvalidDataException("Invalid hair joint axis or range");
            _axes[i] = Vector3.Normalize(axis);
            if (_types[i] == Hinge) _axes[i] = Solver(i, _axes[i]);
            else if (_aligned[i]) throw new InvalidDataException("Invalid hair joint direction");
            _low[i] = joint.Range[0];
            _high[i] = joint.Range[1];
            if (_types[i] == Prismatic) _retention[i] = MathF.Exp(-Step * (joint.LinearDamping + joint.Friction / joint.Mass));
        }
        // The positions and frames of the joints that hang from an aligned joint are relative to its solver frame.
        for (var i = 0; i < count; i++)
        {
            var j = Array.FindIndex(definition.Joints, 0, i, p => p.Bone == definition.Joints[i].Parent);
            if (j < 0 || !_aligned[j]) continue;
            var inverse = Quaternion.Inverse(_align[j]);
            _local[i] = new(Vector3.Transform(_local[i].Position, inverse), Quaternion.Normalize(inverse * _local[i].Rotation));
        }
        // A joint that starts at the end of its parent joint shares the chain with it: the two bodies pull on each other.
        for (var i = 0; i < count; i++)
        {
            var bone = definition.Joints[i].Parent;
            var j = Array.FindIndex(definition.Joints, 0, i, p => p.Bone == bone);
            _chain[i] = j >= 0 && _types[j] != Prismatic && _types[i] != Prismatic &&
                Vector3.Distance(_local[i].Position, new Vector3(definition.Joints[j].Length, 0, 0)) < 0.002f ? j : -1;
        }
        _animated = new BandPose[parents.Length]; _world = new BandPose[parents.Length]; _visited = new bool[parents.Length];
        _jointOf = Enumerable.Repeat(-1, parents.Length).ToArray();
        for (var i = 0; i < count; i++) _jointOf[definition.Joints[i].Bone] = i;
        _projected = new bool[count];
        for (var i = 0; i < count; i++)
        {
            var parentJoint = _jointOf[definition.Joints[i].Parent];
            _projected[i] = _types[i] != Prismatic && (definition.Joints[i].ColliderMask != 0 || parentJoint >= 0 && parentJoint < i && _projected[parentJoint]);
            if (_projected[i]) _projectCount++;
        }
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
            Array.Copy(saved.Value.Origins, _origins, _tails.Length);
            Array.Copy(saved.Value.Frames, _frames, _tails.Length);
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

    // GH3 keyframes the body bodies of the ragdoll to the animation and simulates only the accessory bodies. They hang
    // from their constraints under gravity: no motor or spring pulls them to the bind pose (the ragdoll motors are off).
    private void Advance(int tick)
    {
        _root = _sample((double)tick / Rate, _animated);
        Array.Clear(_visited, 0, _visited.Length);
        for (var i = 0; i < _world.Length; i++) World(i);
        var head = _hasCapsule ? _world[_definition.Head] : _root;
        var ca = head.Position + Vector3.Transform(_capsuleA, head.Rotation);
        var cb = head.Position + Vector3.Transform(_capsuleB, head.Rotation);
        for (var c = 0; c < _colliders.Length; c++)
        {
            var bone = _world[_colliders[c].Bone];
            _colliderA[c] = bone.Position + Vector3.Transform(_colliderLocalA[c], bone.Rotation);
            _colliderB[c] = bone.Position + Vector3.Transform(_colliderLocalB[c], bone.Rotation);
        }
        Array.Copy(_pose, _previous, _pose.Length);
        for (var i = 0; i < _pose.Length; i++)
        {
            var joint = _definition.Joints[i];
            var parent = _world[joint.Parent];
            var origin = parent.Position + Vector3.Transform(_local[i].Position, parent.Rotation);
            var rotation = Quaternion.Normalize(parent.Rotation * _local[i].Rotation);
            if (_types[i] == Prismatic)
            {
                // The joint slides along its axis between the limits; gravity and the motion of its parent move it.
                var axis = Vector3.Transform(_axes[i], rotation);
                var point = origin;
                if (tick > 0)
                {
                    var moved = _tails[i] + (_tails[i] - _oldTails[i]) * _retention[i] + Gravity * (Step * Step);
                    point = origin + axis * Math.Clamp(Vector3.Dot(moved - origin, axis), _low[i], _high[i]);
                }
                _oldTails[i] = tick == 0 ? point : _tails[i];
                _tails[i] = point;
                _world[joint.Bone] = new(point, rotation);
                continue;
            }
            var direction = Vector3.Transform(Vector3.UnitX, rotation);
            var tail = origin + direction * joint.Length;
            if (tick > 0)
            {
                tail = _tails[i] + (_tails[i] - _oldTails[i]) * _retention[i] + Gravity * (Step * Step);
                // Constraint friction slows the turn against the parent (where the parent alone would carry the end).
                var carried = origin + Vector3.Transform(_tails[i] - _origins[i], rotation * Quaternion.Inverse(_frames[i]));
                var relative = tail - carried;
                tail = relative.LengthSquared() <= _brake[i] * _brake[i] ? carried : tail - Vector3.Normalize(relative) * _brake[i];
                if (_maxTurn[i] > 0)
                {
                    var before = Vector3.Normalize(_tails[i] - _origins[i]);
                    var now = tail - origin;
                    now = now.LengthSquared() < 1e-10f ? before : Vector3.Normalize(now);
                    var turn = MathF.Acos(Math.Clamp(Vector3.Dot(before, now), -1, 1));
                    if (turn > _maxTurn[i]) tail = origin + Vector3.Transform(before, Quaternion.Slerp(Quaternion.Identity, FromTo(before, now), _maxTurn[i] / turn)) * joint.Length;
                }
            }
            _oldTails[i] = tick == 0 ? tail : _tails[i];
            _tails[i] = tail;
            _origins[i] = origin;
            _frames[i] = rotation;
            var toward = tail - origin;
            _world[joint.Bone] = new(origin, toward.LengthSquared() < 1e-10f ? rotation : Quaternion.Normalize(FromTo(direction, Vector3.Normalize(toward)) * rotation));
        }
        // The constraints, as the Havok solver iterates them: the length and the limits of each joint act on the joint
        // and, in a chain, on its parent joint by their masses and inertias, so a heavy chain end slows the joints above
        // it instead of whipping on a parent that only the head moves. The last pass gives the limits to the joint only.
        for (var pass = 0; pass < (tick == 0 ? 1 : Iterations); pass++)
        {
            for (var i = 0; i < _pose.Length; i++)
            {
                if (_types[i] == Prismatic) continue;
                var joint = _definition.Joints[i];
                var j = _chain[i];
                var raw = _tails[i];
                if (j >= 0 && tick > 0)
                {
                    var upper = _definition.Joints[j];
                    var anchor = _world[upper.Bone];
                    var offset = raw - (anchor.Position + Vector3.Transform(_local[i].Position, anchor.Rotation));
                    var length = offset.Length();
                    if (length > 1e-6f)
                    {
                        var stretch = offset / length * (length - joint.Length);
                        var share = upper.Mass / (joint.Mass + upper.Mass);
                        raw -= stretch * share;
                        Turn(j, anchor.Position, FromTo(Vector3.Normalize(_tails[j] - anchor.Position), Vector3.Normalize(_tails[j] + stretch * (1 - share) - anchor.Position)));
                    }
                }
                var parent = _world[joint.Parent];
                var origin = parent.Position + Vector3.Transform(_local[i].Position, parent.Rotation);
                var rotation = Quaternion.Normalize(parent.Rotation * _local[i].Rotation);
                var tail = Constrain(i, origin, rotation, raw, ca, cb);
                if (j >= 0 && tick > 0 && pass < Iterations - 1)
                {
                    var from = raw - origin;
                    from = from.LengthSquared() < 1e-10f ? Vector3.Transform(Vector3.UnitX, rotation) : Vector3.Normalize(from);
                    var correction = FromTo(from, Vector3.Normalize(tail - origin));
                    var share = _inverseInertia[j] / (_inverseInertia[i] + _inverseInertia[j]);
                    Turn(j, _world[_definition.Joints[j].Bone].Position, Quaternion.Slerp(Quaternion.Identity, Quaternion.Inverse(correction), share));
                    parent = _world[joint.Parent];
                    origin = parent.Position + Vector3.Transform(_local[i].Position, parent.Rotation);
                    rotation = Quaternion.Normalize(parent.Rotation * _local[i].Rotation);
                    tail = origin + Vector3.Transform(from, Quaternion.Slerp(Quaternion.Identity, correction, 1 - share)) * joint.Length;
                }
                _tails[i] = tail;
                _origins[i] = origin;
                _frames[i] = rotation;
                _world[joint.Bone] = new(origin, Quaternion.Normalize(FromTo(Vector3.Transform(Vector3.UnitX, rotation), Vector3.Normalize(tail - origin)) * rotation));
            }
            if (_projectCount > 0) Project();
        }
        for (var i = 0; i < _pose.Length; i++) Measure(i, tick);
        // The rig parent of a joint can be a later joint of the chains (the profile hair of a character on another
        // role's rig), so the local poses use the final world poses of this step.
        for (var i = 0; i < _pose.Length; i++)
        {
            var bone = _definition.Joints[i].Bone;
            var actualParent = _parents[bone] < 0 ? _root : Bone(_parents[bone]);
            var world = Bone(bone);
            var inverse = Quaternion.Inverse(actualParent.Rotation);
            _pose[i] = new(Vector3.Transform(world.Position - actualParent.Position, inverse), Quaternion.Normalize(inverse * world.Rotation));
        }
        _tick = tick;
        if (tick == 0) Array.Copy(_pose, _previous, _pose.Length);
        if (tick % Rate == 0 && !_checkpoints.ContainsKey(tick))
            _checkpoints.Add(tick, new(_tails.ToArray(), _oldTails.ToArray(), _origins.ToArray(), _frames.ToArray(), _pose.ToArray(), _previous.ToArray()));
    }

    // The world pose of a rig bone in its own bone frame (the solver keeps the frames of the joints with +X along them).
    private BandPose Bone(int bone)
    {
        var joint = _jointOf[bone];
        return joint >= 0 && _aligned[joint] ? new(_world[bone].Position, Quaternion.Normalize(_world[bone].Rotation * Quaternion.Inverse(_align[joint]))) : _world[bone];
    }

    // Turns joint `i` about its origin by `turn` (world).
    private void Turn(int i, Vector3 origin, Quaternion turn)
    {
        var bone = _definition.Joints[i].Bone;
        _tails[i] = origin + Vector3.Transform(_tails[i] - origin, turn);
        _world[bone] = new(origin, Quaternion.Normalize(turn * _world[bone].Rotation));
    }

    // The head capsule and the joint limits, a few times; the joint keeps its length. The limits act softly, as in Havok.
    private Vector3 Constrain(int i, Vector3 origin, Quaternion rotation, Vector3 tail, Vector3 ca, Vector3 cb)
    {
        var joint = _definition.Joints[i];
        var direction = Vector3.Transform(Vector3.UnitX, rotation);
        var hingeAxis = Vector3.Transform(_axes[i], rotation);
        var twist = Vector3.Transform(_twist[i], rotation);
        var cone = Vector3.Transform(_cone[i], rotation);
        var plane = Vector3.Transform(_plane[i], rotation);
        for (var pass = 0; pass < Passes; pass++)
        {
            if (_headCapsule[i])
            {
                var axis = cb - ca;
                var closest = ca + axis * Math.Clamp(Vector3.Dot(tail - ca, axis) / Math.Max(axis.LengthSquared(), 1e-8f), 0, 1);
                var outward = tail - closest;
                if (outward.LengthSquared() < _definition.Capsule.Radius * _definition.Capsule.Radius && outward.LengthSquared() > 1e-10f)
                    tail = closest + Vector3.Normalize(outward) * _definition.Capsule.Radius;
            }
            var proposed = tail - origin;
            proposed = proposed.LengthSquared() < 1e-10f ? direction : Vector3.Normalize(proposed);
            if (_types[i] == Hinge)
            {
                // Only the turn about the hinge axis, within the limits from the bind pose.
                var angle = MathF.Atan2(Vector3.Dot(Vector3.Cross(direction, proposed), hingeAxis), Vector3.Dot(direction, proposed));
                proposed = Vector3.Transform(direction, Quaternion.CreateFromAxisAngle(hingeAxis, Math.Clamp(angle, _low[i], _high[i])));
            }
            else
            {
                var moved = Vector3.Transform(twist, FromTo(direction, proposed));
                proposed = Vector3.Transform(proposed, Quaternion.Slerp(Quaternion.Identity, FromTo(moved, Limit(i, moved, cone, plane)), LimitShare));
            }
            tail = origin + proposed * joint.Length;
        }
        return tail;
    }

    // The body colliders, after each solver iteration: in chain order, a custom joint follows the end of its parent
    // joint, keeps its direction, and leaves the colliders that it touches. A push is not limited by the cone; the next
    // iteration brings the joint back toward its limits and turns the joints above it, so the chain lifts off a leg.
    private void Project()
    {
        for (var i = 0; i < _pose.Length; i++)
        {
            if (!_projected[i]) continue;
            var joint = _definition.Joints[i];
            var parent = _world[joint.Parent];
            var origin = parent.Position + Vector3.Transform(_local[i].Position, parent.Rotation);
            var rotation = Quaternion.Normalize(parent.Rotation * _local[i].Rotation);
            var along = _tails[i] - _origins[i];
            var tail = origin + (along.LengthSquared() < 1e-10f ? Vector3.Transform(Vector3.UnitX, rotation) : Vector3.Normalize(along)) * joint.Length;
            if (joint.ColliderMask != 0) tail = Collide(i, origin, tail, joint.Length);
            _tails[i] = tail;
            _origins[i] = origin;
            _frames[i] = rotation;
            _world[joint.Bone] = new(origin, Quaternion.Normalize(FromTo(Vector3.Transform(Vector3.UnitX, rotation), Vector3.Normalize(tail - origin)) * rotation));
        }
    }

    // The body colliders of a custom joint: the joint segment leaves each capsule that it enters; it turns about its
    // origin, so a joint that starts inside a collider (a skirt joint on the pelvis) is pushed out at its end only.
    private Vector3 Collide(int i, Vector3 origin, Vector3 tail, float length)
    {
        var mask = _definition.Joints[i].ColliderMask;
        for (var c = 0; c < _colliders.Length; c++)
        {
            if ((mask & (1L << c)) == 0) continue;
            var radius = _colliders[c].Radius + _radius[i];
            var (s, t) = Closest(origin, tail, _colliderA[c], _colliderB[c]);
            if (s < 0.1f) (s, t) = (1, Closest(tail, tail, _colliderA[c], _colliderB[c]).T);
            var point = origin + (tail - origin) * s;
            var away = point - (_colliderA[c] + (_colliderB[c] - _colliderA[c]) * t);
            var distance = away.LengthSquared();
            if (distance >= radius * radius) continue;
            distance = MathF.Sqrt(distance);
            if (distance < 1e-5f) continue;
            tail += away / distance * ((radius - distance) / s);
            var turned = tail - origin;
            if (turned.LengthSquared() > 1e-10f) tail = origin + Vector3.Normalize(turned) * length;
        }
        return tail;
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
        if (e < 1e-10f) return (a < 1e-10f ? 0 : Math.Clamp(-Vector3.Dot(d1, r) / a, 0, 1), 0);
        var c = Vector3.Dot(d1, r);
        var b = Vector3.Dot(d1, d2);
        var denominator = a * e - b * b;
        var s = denominator > 1e-10f ? Math.Clamp((b * f - c * e) / denominator, 0, 1) : 0;
        var t = (b * s + f) / e;
        if (t < 0) { t = 0; s = a < 1e-10f ? 0 : Math.Clamp(-c / a, 0, 1); }
        else if (t > 1) { t = 1; s = a < 1e-10f ? 0 : Math.Clamp((b - c) / a, 0, 1); }
        return (s, t);
    }

    // The Havok ragdoll limits on the twist axis of the joint: the cone around the parent's cone axis, and the angle to
    // the plane normal to the parent's plane axis.
    private Vector3 Limit(int i, Vector3 twist, Vector3 cone, Vector3 plane)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            var cos = Vector3.Dot(twist, cone);
            if (cos < _coneCos[i])
            {
                var side = twist - cone * cos;
                if (side.LengthSquared() > 1e-12f) twist = cone * _coneCos[i] + Vector3.Normalize(side) * _coneSin[i];
            }
            if (!_planes[i]) break;
            var sin = Math.Clamp(Vector3.Dot(twist, plane), -1, 1);
            var clamped = Math.Clamp(sin, _planeLow[i], _planeHigh[i]);
            var flat = twist - plane * sin;
            if (clamped == sin || flat.LengthSquared() < 1e-12f) break;
            twist = Vector3.Normalize(flat) * MathF.Sqrt(1 - clamped * clamped) + plane * clamped;
        }
        return twist;
    }

    private void Measure(int joint, int tick)
    {
        if (tick == 0) return;
        var speed = Vector3.Distance(_tails[joint], _oldTails[joint]) * Rate;
        if (speed <= PeakSpeed) return;
        PeakSpeed = speed;
        PeakBone = _definition.Joints[joint].Bone;
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

    // An axis of the joint's bone frame in the solver frame of the joint.
    private Vector3 Solver(int i, Vector3 axis) => _aligned[i] ? Vector3.Transform(axis, Quaternion.Inverse(_align[i])) : axis;

    private static Vector3 Unit(float[] v)
    {
        var vector = Vector(v);
        if (Math.Abs(vector.LengthSquared() - 1) > 0.01f) throw new InvalidDataException("Invalid hair joint axis");
        return Vector3.Normalize(vector);
    }

    private static Vector3 Vector(float[] v)
    {
        if (v.Length != 3 || !v.All(float.IsFinite)) throw new InvalidDataException("Invalid hair vector");
        return new(v[0], v[1], v[2]);
    }
}
