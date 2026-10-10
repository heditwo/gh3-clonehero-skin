using Gh3Skin.Packages;

namespace Gh3Skin;

// The rig rows of the bones that a character package adds (character.json bones, PACKAGE-FORMAT.md section 9.5), its chain
// joints, and its colliders on the rig that plays it, without Unity objects (CharacterExtras makes the bones; the hair
// bench uses it directly).
internal static class CharacterChainRig
{
    // CharacterRig.Map for the joints of the rig, with the added joints after the rig bones: for each character joint,
    // the rig bone (rigCount + k for added bone k) and the joint whose bind pose skins it.
    public static (int[] Bones, int[] Bind)? MapJoints(CharacterModel character, int rigCount, Func<string[], (int[] Bones, int[] Bind)?> map)
    {
        var added = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var k = 0; k < character.AddedBones.Length; k++) added[character.AddedBones[k].Name] = k;
        var rigJoints = character.Joints.Where(j => !added.ContainsKey(j)).ToArray();
        if (map(rigJoints) is not { } result) return null;
        var bones = new int[character.Joints.Length];
        var bind = new int[character.Joints.Length];
        for (int i = 0, at = 0; i < character.Joints.Length; i++)
        {
            if (added.TryGetValue(character.Joints[i], out var k)) { bones[i] = rigCount + k; bind[i] = i; continue; }
            bones[i] = result.Bones[at];
            bind[i] = System.Array.IndexOf(character.Joints, rigJoints[result.Bind[at]]);
            at++;
        }
        return (bones, bind);
    }

    // The rig rows of the added bones (parent rig index, bind pose relative to it), the chain joints, and the colliders,
    // without Unity objects (the hair bench uses it).
    public static (int[] Parents, BandPose[] Bind, HairJoint[] Joints, HairCollider[] Colliders) Plan(CharacterModel character, int[] map, int[] bind)
    {
        var joints = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < character.Joints.Length; i++) joints[character.Joints[i]] = i;
        int Joint(string name) => joints.TryGetValue(name, out var index) ? index : throw new InvalidDataException($"the GLB has no joint {name}");
        Matrix4d Ibm(int joint) => new(character.Bindposes[joint].Select(v => (double)v).ToArray());
        // The bind pose of joint `self` relative to the rig bone that drives `parent`.
        Matrix4d Relative(int parent, int self) => Ibm(bind[parent]) * Ibm(self).Inverse();
        var parents = new int[character.AddedBones.Length];
        var rest = new BandPose[parents.Length];
        for (var k = 0; k < parents.Length; k++)
        {
            var bone = character.AddedBones[k];
            var parent = Joint(bone.Parent);
            parents[k] = map[parent];
            rest[k] = Pose(Relative(parent, Joint(bone.Name)));
        }
        var hair = character.ChainJoints.Select(chain =>
        {
            var self = Joint(chain.Bone);
            var parent = Joint(chain.Parent);
            var local = Pose(Relative(parent, self));
            var joint = chain.Joint;
            return new HairJoint
            {
                Bone = map[self], Parent = map[parent], Position = new[] { local.Position.X, local.Position.Y, local.Position.Z },
                Rotation = new[] { local.Rotation.X, local.Rotation.Y, local.Rotation.Z, local.Rotation.W }, Length = joint.Length, Mass = joint.Mass,
                Friction = joint.Friction, AngularDamping = joint.AngularDamping, Limits = joint.Limits, Type = joint.Type, Axis = joint.Axis, Range = joint.Range,
                TwistAxis = joint.TwistAxis, ConeAxis = joint.ConeAxis, PlaneAxis = joint.PlaneAxis, MaxAngularVelocity = joint.MaxAngularVelocity,
                Direction = joint.Direction, Radius = joint.Radius, ColliderMask = chain.ColliderMask, HeadCapsule = false,
                Custom = true, Stiffness = joint.Stiffness, Drag = joint.Drag, Gravity = joint.Gravity, BendLimit = joint.BendLimit,
            };
        }).ToArray();
        var colliders = character.Colliders.Select(collider =>
        {
            var self = Joint(collider.Bone);
            // A collider follows the bone that drives its bone; its ends move into the frame of that bone.
            var frame = Ibm(bind[self]) * Ibm(self).Inverse();
            return new HairCollider { Bone = map[self], A = Point(frame, collider.A), B = Point(frame, collider.B), Radius = collider.Radius };
        }).ToArray();
        return (parents, rest, hair, colliders);
    }

    private static BandPose Pose(Matrix4d m)
    {
        var rotation = Matrix4d.Quaternion(m.Upper3());
        var q = System.Numerics.Quaternion.Normalize(new((float)rotation[0], (float)rotation[1], (float)rotation[2], (float)rotation[3]));
        return new(new((float)m[0, 3], (float)m[1, 3], (float)m[2, 3]), q);
    }

    private static float[] Point(Matrix4d m, float[] p) => Enumerable.Range(0, 3).Select(r => (float)(m[r, 0] * p[0] + m[r, 1] * p[1] + m[r, 2] * p[2] + m[r, 3])).ToArray();
}
