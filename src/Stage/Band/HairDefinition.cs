using System.Text.Json;

namespace Gh3Skin;

// The hair and chain definitions of a rig (band/hair.json, character chains): joints, colliders, and the head capsule.
internal sealed class HairBank
{
    public int Version { get; set; }
    public Dictionary<string, HairDefinition> Members { get; set; } = new();

    public static HairBank? Load(string path)
    {
        if (!File.Exists(path)) return null;
        var bank = JsonSerializer.Deserialize<HairBank>(File.ReadAllText(path), Packages.PackageJson.CaseInsensitive);
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
            Custom = j.Custom, Stiffness = j.Stiffness, Drag = j.Drag, Gravity = j.Gravity, BendLimit = j.BendLimit,
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
    // Custom chains only (the GH3 hair has none of these): a pull toward the bind direction against the parent (per
    // second squared), a damping of the swing against the parent (per second), the share of gravity, and the largest
    // angle of the chain end from its bind direction, seen from the chain start (radians, 0: no limit; on every joint
    // of the chain).
    public bool Custom { get; set; }
    public float Stiffness { get; set; }
    public float Drag { get; set; }
    public float Gravity { get; set; } = 1;
    public float BendLimit { get; set; }
}
