using System.Numerics;

namespace Gh3Skin;

// GH3 DegenerateBlend node (CDegenerateBlendAnimNode: GH3.exe 0x6599b0 add, 0x659730 update, 0x659860 evaluate).
// Each branch keeps a weight that starts at 1 when the next branch is added. Every update lowers all weights except the
// newest by dt / BlendDuration of the newest branch, and a branch is dropped with its weight at 0. The output starts at
// the oldest branch and lerps to each newer branch with factor 1 - weight of the branch before it (linear blend curve).
// CDegeneratePartialAnimNode (0x659d40, 0x659f50) uses a weighted mean instead; the band anim trees do not use it.
internal sealed class StageBlendTrack
{
    internal readonly record struct Branch(double Time, string Clip, double Blend);
    internal const int MaxDepth = 16;
    private readonly Branch[] _branches;
    private readonly BandPose[] _sample;
    private readonly bool[] _sampled;
    private readonly float[] _factors = new float[MaxDepth + 1];

    public StageBlendTrack(IEnumerable<Branch> branches, int bones)
    {
        _branches = branches.OrderBy(b => b.Time).ToArray();
        _sample = new BandPose[bones];
        _sampled = new bool[bones];
    }

    public IEnumerable<string> Clips => _branches.Select(b => b.Clip).Distinct();

    // Writes the lerp factor of branch k to factors[k - oldest] and returns the oldest branch that still has weight.
    public static int Factors(IReadOnlyList<Branch> branches, int newest, double time, Span<float> factors)
    {
        Span<double> progress = stackalloc double[MaxDepth + 1];
        var oldest = newest;
        var decay = 0.0;
        for (var k = newest - 1; k >= 0 && newest - k <= MaxDepth; k--)
        {
            var next = branches[k + 1];
            if (next.Blend <= 0) break;
            decay += ((k + 1 == newest ? time : branches[k + 2].Time) - next.Time) / next.Blend;
            if (decay >= 1) break;
            progress[newest - 1 - k] = decay;
            oldest = k;
        }
        factors[0] = 1;
        for (var k = oldest; k < newest; k++) factors[k + 1 - oldest] = (float)progress[newest - 1 - k];
        return oldest;
    }

    public void Evaluate(double time, Func<string, PerformanceClip> clip, BandPose[] bind, BandPose[] output, bool[] touched)
    {
        var newest = StageTimeline.UpperBound(_branches, time, b => b.Time) - 1;
        if (newest < 0) return;
        var oldest = Factors(_branches, newest, time, _factors);
        var first = _branches[oldest];
        clip(first.Clip).SampleDelta(output, touched, bind, time - first.Time);
        for (var k = oldest + 1; k <= newest; k++)
        {
            var weight = _factors[k - oldest];
            var branch = _branches[k];
            Array.Fill(_sample, new BandPose(Vector3.Zero, Quaternion.Identity));
            Array.Clear(_sampled, 0, _sampled.Length);
            clip(branch.Clip).SampleDelta(_sample, _sampled, bind, time - branch.Time);
            for (var bone = 0; bone < _sample.Length; bone++)
            {
                if (!_sampled[bone] && !touched[bone]) continue;
                output[bone] = BandPose.Mix(output[bone], _sample[bone], weight);
                touched[bone] = true;
            }
        }
    }
}

// One chain of a GH3 IK parameter array (guitar_anim_tree.qb): bone0-2 and boneTarget as bone checksums, HingeAxis, and
// the hinge cosine limits.
internal sealed class IkChainDefinition
{
    public string[] Bones { get; set; } = Array.Empty<string>();
    public string Target { get; set; } = "";
    public float[] Hinge { get; set; } = Array.Empty<float>();
    public float CosMax { get; set; }
    public float CosMin { get; set; }
}

// GH3 guitarist_static_tree and drummer_static_tree after the animation layers: Havok two-joint IK (Hero_Ik_params,
// drummer_IK_params; they differ only in the hand target bones), then hero_ConstraintBones.
internal sealed class StageGuitarRig
{
    private sealed record Chain(int First, int Second, int End, int Target, Vector3 Hinge, int Side, float CosMax, float CosMin);
    private sealed record Constraint(int Type, int Bone, int Target, float Amount);
    private const float CosMaxArm = -0.97f, CosMinArm = 0.97f, CosMaxLeg = -0.98999995f, CosMinLeg = 0.97f;
    private readonly int[] _parents;
    private readonly BandPose[] _bind;
    private readonly Chain[] _chains;
    private readonly Constraint[] _constraints;
    private readonly Vector3[] _position;
    private readonly Quaternion[] _rotation;

    // `ik`: the IK chains of a character library profile (Hero_Ik_params or Satan_Ik_params); null: Hero_Ik_params.
    public StageGuitarRig(int[] parents, BandPose[] bind, string[] bones, string handTarget = "Bone_IK_Hand_Guitar", IkChainDefinition[]? ik = null)
    {
        _parents = parents;
        _bind = bind;
        _position = new Vector3[bind.Length];
        _rotation = new Quaternion[bind.Length];
        int Bone(string name) => Array.IndexOf(bones, Gh3Checksum.Of(name));
        // Hinge axes are the original values; mirroring Z to Unity space keeps a Z axis unchanged for an axis-angle pair.
        int Key(string checksum) => Array.IndexOf(bones, checksum);
        _chains = (ik != null ? ik.Select(c => new Chain(Key(c.Bones[0]), Key(c.Bones[1]), Key(c.Bones[2]), Key(c.Target), new Vector3(c.Hinge[0], c.Hinge[1], c.Hinge[2]),
                c.Target == Gh3Checksum.Of(handTarget + "_R") ? 1 : c.Target == Gh3Checksum.Of(handTarget + "_L") ? -1 : 0, c.CosMax, c.CosMin)).ToArray() : new[]
        {
            new Chain(Bone("Bone_Bicep_R"), Bone("Bone_Forearm_R"), Bone("Bone_Palm_R"), Bone(handTarget + "_R"), -Vector3.UnitZ, 1, CosMaxArm, CosMinArm),
            new Chain(Bone("Bone_Bicep_L"), Bone("Bone_Forearm_L"), Bone("Bone_Palm_L"), Bone(handTarget + "_L"), -Vector3.UnitZ, -1, CosMaxArm, CosMinArm),
            new Chain(Bone("Bone_Thigh_L"), Bone("Bone_Knee_L"), Bone("Bone_Ankle_L"), Bone("Bone_IK_Foot_Slave_L"), Vector3.UnitZ, 0, CosMaxLeg, CosMinLeg),
            new Chain(Bone("Bone_Thigh_R"), Bone("Bone_Knee_R"), Bone("Bone_Ankle_R"), Bone("Bone_IK_Foot_Slave_R"), Vector3.UnitZ, 0, CosMaxLeg, CosMinLeg),
        }).Where(c => c.First >= 0 && c.Second >= 0 && c.End >= 0 && c.Target >= 0 &&
            parents[c.Second] == c.First && parents[c.End] == c.Second).ToArray();
        const int split = 1, splitR = 2, twist = 3, twistChild = 4;
        _constraints = new (int, string, string, float)[]
        {
            (twistChild, "Bone_Twist_Wrist_L", "Bone_Palm_L", 0.5f), (twistChild, "Bone_Twist_Wrist_R", "Bone_Palm_R", 0.5f),
            (twist, "Bone_Twist_Bicep_Mid_R", "Bone_Bicep_R", 0.5f), (twist, "Bone_Twist_Bicep_Mid_L", "Bone_Bicep_L", 0.5f),
            (twist, "Bone_Twist_Bicep_Top_R", "Bone_Bicep_R", 1), (twist, "Bone_Twist_Bicep_Top_L", "Bone_Bicep_L", 1),
            (twist, "Bone_Twist_Thigh_R", "Bone_Thigh_R", 0.5f), (twist, "Bone_Twist_Thigh_L", "Bone_Thigh_L", 0.5f),
            (split, "Bone_Split_Knee_R", "Bone_Knee_R", 0.5f), (split, "Bone_Split_Knee_L", "Bone_Knee_L", 0.5f),
            (splitR, "Bone_Split_Ass_R", "Bone_Twist_Thigh_R", 0.5f), (splitR, "Bone_Split_Ass_L", "Bone_Twist_Thigh_L", 0.5f),
            (split, "Bone_Split_Elbow_R", "Bone_Forearm_R", 0.5f), (split, "Bone_Split_Elbow_L", "Bone_Forearm_L", 0.5f),
        }.Select(c => new Constraint(c.Item1, Bone(c.Item2), Bone(c.Item3), c.Item4)).Where(c => c.Bone >= 0 && c.Target >= 0).ToArray();
    }

    public void Apply(BandPose[] pose, float right, float left)
    {
        for (var i = 0; i < pose.Length; i++) Model(pose, i);
        foreach (var chain in _chains)
        {
            var gain = chain.Side switch { 1 => right, -1 => left, _ => 1 };
            if (gain > 0) Solve(pose, chain, gain);
        }
        foreach (var constraint in _constraints) Constrain(pose, constraint);
    }

    private void Model(BandPose[] pose, int bone)
    {
        var parent = _parents[bone];
        if (parent < 0)
        {
            _position[bone] = pose[bone].Position;
            _rotation[bone] = pose[bone].Rotation;
            return;
        }
        _position[bone] = _position[parent] + Vector3.Transform(pose[bone].Position, _rotation[parent]);
        _rotation[bone] = Quaternion.Normalize(_rotation[parent] * pose[bone].Rotation);
    }

    private void SetModelRotation(BandPose[] pose, int bone, Quaternion rotation)
    {
        var parent = _parents[bone];
        var local = parent < 0 ? rotation : Quaternion.Inverse(_rotation[parent]) * rotation;
        pose[bone] = pose[bone] with { Rotation = Quaternion.Normalize(local) };
        Model(pose, bone);
    }

    // hkaTwoJointsIkSolver as called by GH3's IK node: hinge the second joint to the target distance, turn the first
    // joint toward the target with a damped shortest rotation, then copy the target bone's rotation to the end bone.
    private void Solve(BandPose[] pose, Chain chain, float gain)
    {
        var first = _position[chain.First];
        var second = _position[chain.Second];
        var end = _position[chain.End];
        var target = _position[chain.Target];
        var upper = Vector3.Distance(first, second);
        var lower = Vector3.Distance(second, end);
        if (upper < 1e-5f || lower < 1e-5f) return;
        var reach = Vector3.DistanceSquared(first, target);
        var (cosMax, cosMin) = (chain.CosMax, chain.CosMin);
        var cosine = Math.Max(Math.Min((reach - upper * upper - lower * lower) / (-2 * upper * lower), cosMin), cosMax);
        var current = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(first - second), Vector3.Normalize(end - second)), -1, 1));
        var axis = Vector3.Transform(chain.Hinge, _rotation[chain.Second]);
        var hinge = Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), (MathF.Acos(cosine) - current) * gain);
        SetModelRotation(pose, chain.Second, hinge * _rotation[chain.Second]);
        Model(pose, chain.End);
        var from = Vector3.Normalize(_position[chain.End] - first);
        var to = Vector3.Normalize(target - first);
        var damped = gain * Vector3.Dot(from, to) + 1 - gain;
        if (damped < 1)
        {
            Quaternion turn;
            if (damped < -1)
            {
                var other = MathF.Abs(from.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
                turn = Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(from, other)), MathF.PI);
            }
            else
            {
                var half = MathF.Sqrt((damped + 1) * 0.5f);
                var cross = Vector3.Cross(from, to) * (gain * 0.5f / half);
                turn = Quaternion.Normalize(new Quaternion(cross, half));
            }
            SetModelRotation(pose, chain.First, turn * _rotation[chain.First]);
            Model(pose, chain.Second);
            Model(pose, chain.End);
        }
        var hand = gain >= 1 ? _rotation[chain.Target] : Quaternion.Slerp(_rotation[chain.End], _rotation[chain.Target], gain);
        SetModelRotation(pose, chain.End, hand);
    }

    // hero_ConstraintBones, evaluated on original GH3 quaternions with the same products as GH3.
    private void Constrain(BandPose[] pose, Constraint constraint)
    {
        var target = Gh3(pose[constraint.Target].Rotation);
        Quaternion driven;
        switch (constraint.Type)
        {
            case 1:
                driven = target;
                break;
            case 2:
                var stop = _parents[constraint.Bone];
                for (var bone = _parents[constraint.Target]; bone >= 0 && bone != stop; bone = _parents[bone])
                    target = Multiply(Gh3(pose[bone].Rotation), target);
                driven = Multiply(new Quaternion(0, 1, 0, 0), target);
                break;
            case 3:
                driven = Roll(Multiply(target, Quaternion.Conjugate(Gh3(_bind[constraint.Target].Rotation))), 0);
                break;
            default:
                var axis = pose[constraint.Target].Position * new Vector3(1, 1, -1);
                driven = Roll(target, axis.X >= axis.Y && axis.X >= axis.Z ? 0 : axis.Y >= axis.X && axis.Y >= axis.Z ? 1 : 2);
                break;
        }
        var result = Multiply(Gh3(_bind[constraint.Bone].Rotation), Quaternion.Slerp(Quaternion.Identity, driven, constraint.Amount));
        pose[constraint.Bone] = pose[constraint.Bone] with { Rotation = Quaternion.Normalize(Gh3(result)) };
    }

    // Unity (x, y, -z, w) is the mirrored conjugate of the GH3 quaternion; the map is its own inverse.
    private static Quaternion Gh3(Quaternion q) => new(q.X, q.Y, -q.Z, q.W);
    private static Quaternion Multiply(Quaternion a, Quaternion b) => a * b;

    // GH3 keeps one Euler angle of a ZYX decomposition and rebuilds a single-axis rotation from it.
    private static Quaternion Roll(Quaternion q, int axis)
    {
        float angle = axis switch
        {
            0 => MathF.Atan2(2 * (q.Y * q.Z + q.W * q.X), q.W * q.W - q.X * q.X - q.Y * q.Y + q.Z * q.Z),
            1 => MathF.Asin(Math.Clamp(-2 * (q.X * q.Z - q.Y * q.W), -1, 1)),
            _ => MathF.Atan2(2 * (q.X * q.Y + q.Z * q.W), q.W * q.W + q.X * q.X - q.Y * q.Y - q.Z * q.Z),
        };
        var s = MathF.Sin(angle * 0.5f);
        var c = MathF.Cos(angle * 0.5f);
        return axis switch { 0 => new(s, 0, 0, c), 1 => new(0, s, 0, c), _ => new(0, 0, s, c) };
    }
}
