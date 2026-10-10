using UnityEngine;
using Gh3Skin.Packages;

namespace Gh3Skin;

// The bones that a character package adds to its rig (character.json bones, PACKAGE-FORMAT.md section 9.5) and its
// custom chains and colliders, on the performer that plays it. The added bones come after the rig bones (rig index
// N + k). Their bind poses come from the GLB inverse bind matrices, relative to the character joint whose bind pose skins
// the bone that drives the parent (CharacterRig.Map), so in another role an added bone follows the same body bone as
// the mesh around it.
internal sealed class CharacterExtras
{
    public Transform[] Bones { get; private init; } = System.Array.Empty<Transform>();
    public int[] Parents { get; private init; } = System.Array.Empty<int>();
    public BandPose[] Bind { get; private init; } = System.Array.Empty<BandPose>();
    public HairJoint[] Joints { get; private init; } = System.Array.Empty<HairJoint>();
    public HairCollider[] Colliders { get; private init; } = System.Array.Empty<HairCollider>();

    public string Summary => Bones.Length == 0 ? "" : $"; {Bones.Length} added bones, {Joints.Length} chain joints, {Colliders.Length} colliders";

    public static (int[] Bones, int[] Bind)? MapJoints(CharacterModel character, int rigCount, Func<string[], (int[] Bones, int[] Bind)?> map) =>
        CharacterChainRig.MapJoints(character, rigCount, map);

    public static CharacterExtras Create(CharacterModel character, int[] map, int[] bind, Transform[] bones, int layer)
    {
        if (character.AddedBones.Length == 0) return new CharacterExtras();
        var (parents, rest, hair, colliders) = CharacterChainRig.Plan(character, map, bind);
        var transforms = new Transform[parents.Length];
        try
        {
            for (var k = 0; k < parents.Length; k++)
            {
                var transform = new GameObject($"Added Bone {character.AddedBones[k].Name}") { layer = layer }.transform;
                transforms[k] = transform;
                transform.SetParent(parents[k] < bones.Length ? bones[parents[k]] : transforms[parents[k] - bones.Length], false);
                transform.localPosition = new Vector3(rest[k].Position.X, rest[k].Position.Y, rest[k].Position.Z);
                transform.localRotation = new Quaternion(rest[k].Rotation.X, rest[k].Rotation.Y, rest[k].Rotation.Z, rest[k].Rotation.W);
            }
            return new CharacterExtras { Bones = transforms, Parents = parents, Bind = rest, Joints = hair, Colliders = colliders };
        }
        catch
        {
            foreach (var transform in transforms) if (transform != null) UnityEngine.Object.Destroy(transform.gameObject);
            throw;
        }
    }

    // The added bones are on the body when their parent is.
    public void AddToBody(HashSet<int> body, int rigCount)
    {
        for (var k = 0; k < Parents.Length; k++)
            if (body.Contains(Parents[k])) body.Add(rigCount + k);
    }

    public void Destroy()
    {
        foreach (var bone in Bones) if (bone != null) UnityEngine.Object.Destroy(bone.gameObject);
    }
}
